using ApiGateway.Contracts;
using Grpc.Core;
using Shared.GrpcContracts;
using GrpcGetSearchResultsRequest = Shared.GrpcContracts.GetSearchResultsRequest;
using GrpcStartSearchRequest = Shared.GrpcContracts.StartSearchRequest;

namespace ApiGateway.GrpcClients;

/// <summary>
/// Thin adapter over the generated <see cref="SearchGrpcService.SearchGrpcServiceClient"/>.
/// It translates between the gateway's HTTP contracts and the protobuf messages, applies a
/// deadline to every call, and turns "search does not exist" into a <see langword="null"/> result
/// instead of an exception.
/// </summary>
/// <param name="client">The generated gRPC stub, supplied by the gRPC client factory.</param>
/// <param name="logger">Logger for the outbound calls.</param>
public sealed partial class SearchServiceClient(
    SearchGrpcService.SearchGrpcServiceClient client,
    ILogger<SearchServiceClient> logger) : ISearchServiceClient
{
    /// <summary>
    /// Wall-clock budget for a single call to the Search Service. Both operations are designed to
    /// return immediately, so anything slower means the service is in trouble. Without a deadline
    /// a hung server would hold the gateway's request open indefinitely; with one it surfaces as
    /// <see cref="StatusCode.DeadlineExceeded"/> and is reported as a gateway timeout.
    /// </summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task<Guid> StartSearchAsync(string destination, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        LogForwardingStartSearch(logger, destination);

        var request = new GrpcStartSearchRequest { Destination = destination };

        using var call = client.StartSearchAsync(request, CreateCallOptions(cancellationToken));
        var response = await call.ResponseAsync;

        if (!Guid.TryParse(response.SearchId, out var searchId))
        {
            throw new InvalidOperationException(
                "The search service returned a search identifier that is not a valid GUID.");
        }

        LogStartSearchAccepted(logger, searchId, destination);

        return searchId;
    }

    /// <inheritdoc />
    public async Task<SearchResultsResponse?> GetSearchResultsAsync(
        Guid searchId,
        CancellationToken cancellationToken)
    {
        var request = new GrpcGetSearchResultsRequest { SearchId = searchId.ToString() };

        try
        {
            using var call = client.GetSearchResultsAsync(request, CreateCallOptions(cancellationToken));
            var response = await call.ResponseAsync;

            var results = new List<HotelResultResponse>(response.Results.Count);
            foreach (var hotel in response.Results)
            {
                results.Add(new HotelResultResponse(hotel.HotelId, hotel.Name, hotel.Price.ToDecimal()));
            }

            LogSearchResultsFetched(logger, searchId, response.IsCompleted, results.Count);

            return new SearchResultsResponse(searchId, response.Destination, response.IsCompleted, results);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
        {
            // Not a failure condition: polling for an unknown identifier is a normal client mistake
            // and is reported to the caller as HTTP 404 rather than as a broken dependency.
            LogSearchNotFound(logger, searchId);
            return null;
        }
    }

    /// <summary>
    /// Builds the per-call options: the caller's cancellation token plus an absolute deadline.
    /// </summary>
    /// <param name="cancellationToken">Token that aborts the call when the HTTP request goes away.</param>
    /// <returns>Options applied to a single unary call.</returns>
    private static CallOptions CreateCallOptions(CancellationToken cancellationToken) =>
        new(deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: cancellationToken);

    /// <summary>Logs that a start request is about to leave the gateway.</summary>
    /// <param name="logger">Logger to write to.</param>
    /// <param name="destination">Destination being searched.</param>
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Debug,
        Message = "Forwarding start search request. Destination={Destination}")]
    private static partial void LogForwardingStartSearch(ILogger logger, string destination);

    /// <summary>Logs that the Search Service accepted a new search.</summary>
    /// <param name="logger">Logger to write to.</param>
    /// <param name="searchId">Identifier assigned by the Search Service.</param>
    /// <param name="destination">Destination being searched.</param>
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Search start accepted. SearchId={SearchId} Destination={Destination}")]
    private static partial void LogStartSearchAccepted(ILogger logger, Guid searchId, string destination);

    /// <summary>Logs a successful poll.</summary>
    /// <param name="logger">Logger to write to.</param>
    /// <param name="searchId">Identifier of the polled search.</param>
    /// <param name="isCompleted">Whether the search is final.</param>
    /// <param name="resultCount">Number of hotels accumulated so far.</param>
    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Debug,
        Message = "Search results fetched. SearchId={SearchId} IsCompleted={IsCompleted} ResultCount={ResultCount}")]
    private static partial void LogSearchResultsFetched(
        ILogger logger,
        Guid searchId,
        bool isCompleted,
        int resultCount);

    /// <summary>Logs that the Search Service does not know the requested identifier.</summary>
    /// <param name="logger">Logger to write to.</param>
    /// <param name="searchId">Identifier that was polled.</param>
    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Debug,
        Message = "Search not found. SearchId={SearchId}")]
    private static partial void LogSearchNotFound(ILogger logger, Guid searchId);
}
