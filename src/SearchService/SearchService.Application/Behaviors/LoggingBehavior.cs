using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace SearchService.Application.Behaviors;

/// <summary>
/// MediatR pipeline behaviour that traces every request through the pipeline, recording how long
/// the rest of the pipeline took to answer it.
/// </summary>
/// <typeparam name="TRequest">Type of the request flowing through the pipeline.</typeparam>
/// <typeparam name="TResponse">Type the request responds with.</typeparam>
/// <param name="logger">Sink for structured log records.</param>
/// <remarks>
/// Both records are written at <see cref="LogLevel.Debug"/>: useful when diagnosing a slow or
/// stuck search, quiet in normal operation where the workflow already logs its own milestones.
/// </remarks>
public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>
    /// Cached once per closed generic type, so the name is never resolved through reflection
    /// on the request path.
    /// </summary>
    private static readonly string RequestName = typeof(TRequest).Name;

    /// <summary>
    /// Logs the start and the elapsed time of the request, and invokes the rest of the pipeline.
    /// </summary>
    /// <param name="request">The request being handled.</param>
    /// <param name="next">Continuation representing the next step, ultimately the handler.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The response produced by the rest of the pipeline.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is <see langword="null"/>.</exception>
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        // Checked once up front so neither the log records nor the timing cost anything when
        // debug logging is switched off, which is the normal production configuration.
        var traceEnabled = logger.IsEnabled(LogLevel.Debug);

        if (traceEnabled)
        {
            LoggingBehaviorLog.Handling(logger, RequestName);
        }

        // Timestamp arithmetic rather than a Stopwatch instance: no allocation per request.
        var startedAt = traceEnabled ? Stopwatch.GetTimestamp() : 0L;

        try
        {
            return await next(cancellationToken);
        }
        finally
        {
            if (traceEnabled)
            {
                var elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

                LoggingBehaviorLog.Finished(logger, RequestName, elapsedMilliseconds);
            }
        }
    }
}

/// <summary>
/// Source-generated log records for <see cref="LoggingBehavior{TRequest, TResponse}"/>.
/// </summary>
/// <remarks>
/// Declared outside the generic behaviour so a single set of log methods serves every closed
/// generic instantiation of it.
/// </remarks>
internal static partial class LoggingBehaviorLog
{
    /// <summary>Records that a request entered the pipeline.</summary>
    /// <param name="logger">Sink for structured log records.</param>
    /// <param name="requestName">Simple type name of the request.</param>
    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Debug,
        Message = "Handling {RequestName}.")]
    internal static partial void Handling(ILogger logger, string requestName);

    /// <summary>Records that a request left the pipeline, and how long it took.</summary>
    /// <param name="logger">Sink for structured log records.</param>
    /// <param name="requestName">Simple type name of the request.</param>
    /// <param name="elapsedMilliseconds">Time the rest of the pipeline took, in milliseconds.</param>
    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Debug,
        Message = "Finished {RequestName} in {ElapsedMilliseconds} ms.")]
    internal static partial void Finished(ILogger logger, string requestName, double elapsedMilliseconds);
}
