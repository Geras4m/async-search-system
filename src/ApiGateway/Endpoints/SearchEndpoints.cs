using System.Text.Json;
using ApiGateway.Contracts;
using ApiGateway.GrpcClients;
using FluentValidation;

namespace ApiGateway.Endpoints;

/// <summary>
/// The public HTTP/JSON surface of the system: starting a hotel search and polling it for results.
/// </summary>
/// <remarks>
/// The handlers deliberately contain no transport logic. They validate, delegate to
/// <see cref="ISearchServiceClient"/> and shape the outcome; every unexpected failure is left to
/// the global exception handler so error responses stay uniform.
/// </remarks>
public static partial class SearchEndpoints
{
    /// <summary>OpenAPI tag that groups both endpoints.</summary>
    private const string SearchesTag = "Searches";

    /// <summary>Route of the search collection.</summary>
    private const string SearchesRoute = "/searches";

    /// <summary>Route of a single search, polled by identifier.</summary>
    private const string SearchByIdRoute = "/searches/{searchId}";

    /// <summary>Logger category shared by the endpoint handlers.</summary>
    private static readonly string LogCategory = typeof(SearchEndpoints).FullName!;

    /// <summary>
    /// Registers the search endpoints on the application's routing table.
    /// </summary>
    /// <param name="app">The route builder to register on.</param>
    /// <returns>The same <paramref name="app"/>, so registrations can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(SearchesRoute, StartSearchAsync)
            .WithName("StartSearch")
            .WithSummary("Starts a new hotel search.")
            .WithDescription(
                "Validates the destination, forwards the request to the Search Service over gRPC "
                + "and returns the identifier to poll results with. Execution continues asynchronously.")
            .WithTags(SearchesTag)
            .Produces<StartSearchResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        app.MapGet(SearchByIdRoute, GetSearchResultsAsync)
            .WithName("GetSearchResults")
            .WithSummary("Returns the current state of a search.")
            .WithDescription(
                "Returns every hotel accumulated so far together with the completion flag. "
                + "While the search is still running the flag is false and the list may be empty.")
            .WithTags(SearchesTag)
            .Produces<SearchResultsResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        return app;
    }

    /// <summary>
    /// Handles <c>POST /searches</c>.
    /// </summary>
    /// <param name="request">The parsed request body.</param>
    /// <param name="validator">Validator for <paramref name="request"/>.</param>
    /// <param name="searchServiceClient">Client used to reach the Search Service.</param>
    /// <param name="loggerFactory">Factory the handler's logger is taken from.</param>
    /// <param name="cancellationToken">Token aborted when the caller disconnects.</param>
    /// <returns>
    /// <c>200 OK</c> carrying a <see cref="StartSearchResponse"/>, or <c>400 Bad Request</c> with
    /// the validation failures.
    /// </returns>
    private static async Task<IResult> StartSearchAsync(
        StartSearchRequest request,
        IValidator<StartSearchRequest> validator,
        ISearchServiceClient searchServiceClient,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        var validationResult = await validator.ValidateAsync(request, cancellationToken);
        if (!validationResult.IsValid)
        {
            // Keyed by the JSON member name the caller actually sent, not by the CLR property
            // name, so the problem document lines up with the request body.
            var errors = validationResult.Errors
                .GroupBy(failure => JsonNamingPolicy.CamelCase.ConvertName(failure.PropertyName), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(failure => failure.ErrorMessage).ToArray(),
                    StringComparer.Ordinal);

            LogValidationRejected(logger, validationResult.Errors.Count);

            return Results.ValidationProblem(errors);
        }

        var searchId = await searchServiceClient.StartSearchAsync(request.Destination, cancellationToken);

        // The specification pins the response body to { "searchId": "..." }. Results.Created would
        // add a Location header but also change the status code the acceptance script asserts on,
        // so the exact contract wins and the body is returned with 200 OK.
        return Results.Ok(new StartSearchResponse(searchId));
    }

    /// <summary>
    /// Handles <c>GET /searches/{searchId}</c>.
    /// </summary>
    /// <param name="searchId">Raw route value, parsed here so a malformed value yields a 400 rather than a 404.</param>
    /// <param name="searchServiceClient">Client used to reach the Search Service.</param>
    /// <param name="loggerFactory">Factory the handler's logger is taken from.</param>
    /// <param name="cancellationToken">Token aborted when the caller disconnects.</param>
    /// <returns>
    /// <c>200 OK</c> carrying a <see cref="SearchResultsResponse"/>, <c>400 Bad Request</c> when
    /// the identifier is not a GUID, or <c>404 Not Found</c> when no such search exists.
    /// </returns>
    private static async Task<IResult> GetSearchResultsAsync(
        string searchId,
        ISearchServiceClient searchServiceClient,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        if (!Guid.TryParse(searchId, out var parsedSearchId))
        {
            LogMalformedSearchId(logger);

            return Results.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["searchId"] = ["The search identifier must be a valid GUID."],
                },
                detail: "The search identifier in the route is not a valid GUID.",
                title: "Invalid search identifier.");
        }

        var results = await searchServiceClient.GetSearchResultsAsync(parsedSearchId, cancellationToken);
        if (results is null)
        {
            LogSearchNotFound(logger, parsedSearchId);

            return Results.Problem(
                title: "Search not found.",
                detail: "No search exists with the specified identifier.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(results);
    }

    /// <summary>Logs a request rejected by request validation.</summary>
    /// <param name="logger">Logger to write to.</param>
    /// <param name="failureCount">Number of validation failures.</param>
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Warning,
        Message = "Start search request rejected by validation. FailureCount={FailureCount}")]
    private static partial void LogValidationRejected(ILogger logger, int failureCount);

    /// <summary>Logs a poll whose route value is not a GUID.</summary>
    /// <param name="logger">Logger to write to.</param>
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Search polled with a malformed identifier.")]
    private static partial void LogMalformedSearchId(ILogger logger);

    /// <summary>Logs a poll for a search the Search Service does not know.</summary>
    /// <param name="logger">Logger to write to.</param>
    /// <param name="searchId">Identifier that was polled.</param>
    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Search polled but not found. SearchId={SearchId}")]
    private static partial void LogSearchNotFound(ILogger logger, Guid searchId);
}
