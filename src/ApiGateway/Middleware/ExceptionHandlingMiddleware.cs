using System.Diagnostics;
using System.Text.Json;
using FluentValidation;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;

namespace ApiGateway.Middleware;

/// <summary>
/// Terminal error boundary for the gateway. Every failure that escapes an endpoint is translated
/// into an RFC 7807 <c>application/problem+json</c> response and logged once, at a level that
/// matches whether the caller or the system is at fault.
/// </summary>
/// <remarks>
/// <para>
/// This is written as explicit middleware rather than as an <c>IExceptionHandler</c> behind
/// <c>UseExceptionHandler</c> on purpose. The exception handler pipeline re-executes the request
/// on a cleared <see cref="HttpContext"/>, which happens outside the enrichment scope that
/// <c>UseSerilogRequestLogging</c> opens; the failure would then be reported twice and the mapped
/// status code would not be the one attributed to the request. Sitting directly inside that scope
/// keeps exactly one structured log entry per failed request and makes the translated status code
/// the one Serilog reports.
/// </para>
/// <para>
/// It is an <see cref="IMiddleware"/> implementation so its dependencies are resolved from the
/// request scope by the framework's middleware factory instead of being captured once at startup.
/// </para>
/// </remarks>
/// <param name="problemDetailsService">Writes the problem document using the configured formatters.</param>
/// <param name="environment">Used to decide whether raw exception text may reach the caller.</param>
/// <param name="logger">Logger for handled failures.</param>
public sealed partial class ExceptionHandlingMiddleware(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<ExceptionHandlingMiddleware> logger) : IMiddleware
{
    /// <summary>Media type mandated by RFC 7807.</summary>
    private const string ProblemJsonContentType = "application/problem+json";

    /// <summary>Detail returned for unexpected failures outside the Development environment.</summary>
    private const string OpaqueServerErrorDetail =
        "An unexpected error occurred while processing the request.";

    /// <summary>Detail returned when the request itself could not be read.</summary>
    private const string MalformedRequestDetail =
        "The request could not be read. Ensure the body is valid JSON matching the documented contract.";

    /// <summary>
    /// Invokes the rest of the pipeline and converts anything that escapes it into a problem
    /// document.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="next"/> is <see langword="null"/>.
    /// </exception>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller went away. There is nobody left to answer, so this is not a failure.
            LogRequestAborted(logger, context.Request.Method, context.Request.Path.Value);
        }
        catch (RpcException rpcException)
            when (rpcException.StatusCode == StatusCode.Cancelled
                && context.RequestAborted.IsCancellationRequested)
        {
            // Same situation, different exception type. When a polling client disconnects, the
            // gRPC call it triggered is cancelled and the library reports that as an RpcException
            // with StatusCode.Cancelled rather than an OperationCanceledException, so the branch
            // above does not catch it. Without this it would fall through to the generic mapping
            // and be reported as a 504 at Error level, blaming the Search Service for a timeout
            // that never happened and putting noise in the logs every time somebody closes a tab.
            LogRequestAborted(logger, context.Request.Method, context.Request.Path.Value);
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted)
            {
                LogResponseAlreadyStarted(
                    logger,
                    context.Request.Method,
                    context.Request.Path.Value,
                    exception);

                throw;
            }

            await WriteProblemDetailsAsync(context, exception);
        }
    }

    /// <summary>
    /// Maps a gRPC status onto the HTTP status the gateway reports for it.
    /// </summary>
    /// <param name="exception">The failed call.</param>
    /// <returns>A problem document describing the downstream failure.</returns>
    private static ProblemDetails FromRpcException(RpcException exception) => exception.StatusCode switch
    {
        StatusCode.Unavailable => new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Search service unavailable.",
            Detail = "The search service is currently unavailable.",
        },
        StatusCode.DeadlineExceeded or StatusCode.Cancelled => new ProblemDetails
        {
            Status = StatusCodes.Status504GatewayTimeout,
            Title = "Search service timed out.",
            Detail = "The search service did not respond in time.",
        },
        StatusCode.InvalidArgument => new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Invalid request.",
            Detail = "The search service rejected the request as invalid.",
        },
        StatusCode.NotFound => new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Search not found.",
            Detail = "No search exists with the specified identifier.",
        },
        _ => new ProblemDetails
        {
            Status = StatusCodes.Status502BadGateway,
            Title = "Search service error.",
            Detail = "The search service returned an unexpected error.",
        },
    };

    /// <summary>
    /// Turns validation failures raised outside the endpoint handlers into a 400 carrying the
    /// offending fields.
    /// </summary>
    /// <param name="exception">The validation failure.</param>
    /// <returns>A problem document with an <c>errors</c> member.</returns>
    private static ProblemDetails FromValidationException(ValidationException exception)
    {
        // Keyed by the JSON member name the caller sent, matching the endpoint-level responses.
        var errors = exception.Errors
            .GroupBy(failure => JsonNamingPolicy.CamelCase.ConvertName(failure.PropertyName), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.ErrorMessage).ToArray(),
                StringComparer.Ordinal);

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
            Detail = "The request did not pass validation.",
        };

        problemDetails.Extensions["errors"] = errors;

        return problemDetails;
    }

    /// <summary>
    /// Maps an exception to a problem document, logs it and writes it to the response.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="exception">The failure to translate.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    private async Task WriteProblemDetailsAsync(HttpContext context, Exception exception)
    {
        var problemDetails = CreateProblemDetails(context, exception);
        var statusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;

        LogFailure(context, exception, statusCode, problemDetails.Title);

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = ProblemJsonContentType;

        var written = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problemDetails,
            Exception = exception,
        });

        if (!written)
        {
            await context.Response.WriteAsJsonAsync(
                problemDetails,
                options: null,
                contentType: ProblemJsonContentType,
                cancellationToken: context.RequestAborted);
        }
    }

    /// <summary>
    /// Translates an exception into the problem document the caller receives.
    /// </summary>
    /// <param name="context">The current request, used for the <c>instance</c> member.</param>
    /// <param name="exception">The failure to translate.</param>
    /// <returns>A populated <see cref="ProblemDetails"/>.</returns>
    private ProblemDetails CreateProblemDetails(HttpContext context, Exception exception)
    {
        var problemDetails = exception switch
        {
            RpcException rpcException => FromRpcException(rpcException),
            ValidationException validationException => FromValidationException(validationException),

            // Raised by model binding when the body is absent, truncated or not valid JSON. It
            // already carries the status the framework would have used, which is a 4xx: the caller
            // is at fault, so it must not be reported as a server failure.
            BadHttpRequestException badRequestException => new ProblemDetails
            {
                Status = badRequestException.StatusCode,
                Title = "Malformed request.",
                Detail = environment.IsDevelopment()
                    ? badRequestException.Message
                    : MalformedRequestDetail,
            },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Internal server error.",
                Detail = environment.IsDevelopment()
                    ? string.Concat(exception.GetType().Name, ": ", exception.Message)
                    : OpaqueServerErrorDetail,
            },
        };

        problemDetails.Instance = context.Request.Path.Value;
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;

        return problemDetails;
    }

    /// <summary>
    /// Writes one structured log entry for the handled failure: caller mistakes at
    /// <see cref="LogLevel.Warning"/>, system failures at <see cref="LogLevel.Error"/>.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="exception">The failure being reported.</param>
    /// <param name="statusCode">The status code the caller receives.</param>
    /// <param name="title">Short description of the failure.</param>
    private void LogFailure(HttpContext context, Exception exception, int statusCode, string? title)
    {
        var level = statusCode >= StatusCodes.Status500InternalServerError
            ? LogLevel.Error
            : LogLevel.Warning;

        LogRequestFailed(
            logger,
            level,
            context.Request.Method,
            context.Request.Path.Value,
            statusCode,
            title,
            exception);
    }

    /// <summary>Logs a request the caller abandoned before it could be answered.</summary>
    /// <param name="logger">Logger to write to.</param>
    /// <param name="method">HTTP method of the abandoned request.</param>
    /// <param name="path">Path of the abandoned request.</param>
    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Request aborted by the client. Method={Method} Path={Path}")]
    private static partial void LogRequestAborted(ILogger logger, string method, string? path);

    /// <summary>
    /// Logs a failure that arrived too late to be translated because the response was already
    /// on the wire.
    /// </summary>
    /// <param name="logger">Logger to write to.</param>
    /// <param name="method">HTTP method of the failed request.</param>
    /// <param name="path">Path of the failed request.</param>
    /// <param name="exception">The failure being reported.</param>
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Error,
        Message = "Request failed after the response had started; the connection is aborted. Method={Method} Path={Path}")]
    private static partial void LogResponseAlreadyStarted(
        ILogger logger,
        string method,
        string? path,
        Exception exception);

    /// <summary>Logs a handled failure at the level matching who is at fault.</summary>
    /// <param name="logger">Logger to write to.</param>
    /// <param name="level">Warning for caller mistakes, error for system failures.</param>
    /// <param name="method">HTTP method of the failed request.</param>
    /// <param name="path">Path of the failed request.</param>
    /// <param name="statusCode">Status code the caller receives.</param>
    /// <param name="reason">Short description of the failure.</param>
    /// <param name="exception">The failure being reported.</param>
    [LoggerMessage(
        EventId = 3002,
        Message = "Request failed. Method={Method} Path={Path} StatusCode={StatusCode} Reason={Reason}")]
    private static partial void LogRequestFailed(
        ILogger logger,
        LogLevel level,
        string method,
        string? path,
        int statusCode,
        string? reason,
        Exception exception);
}
