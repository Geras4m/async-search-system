using ApiGateway.Contracts;
using Grpc.Core;

namespace ApiGateway.GrpcClients;

/// <summary>
/// The gateway's view of the Search Service. Hides the generated gRPC stub behind an abstraction
/// that speaks in HTTP-facing contracts, which keeps the endpoints free of transport concerns and
/// makes them trivial to unit test.
/// </summary>
public interface ISearchServiceClient
{
    /// <summary>
    /// Registers a new search and returns as soon as the Search Service has accepted it. Result
    /// generation continues asynchronously on the server.
    /// </summary>
    /// <param name="destination">Destination to search hotels for. Already validated by the caller.</param>
    /// <param name="cancellationToken">Token used to abandon the call, typically the request abort token.</param>
    /// <returns>The identifier the client polls results with.</returns>
    /// <exception cref="RpcException">The Search Service was unreachable or answered with a failure status.</exception>
    Task<Guid> StartSearchAsync(string destination, CancellationToken cancellationToken);

    /// <summary>
    /// Reads everything accumulated for a search so far, together with its completion flag.
    /// </summary>
    /// <param name="searchId">Identifier returned by <see cref="StartSearchAsync"/>.</param>
    /// <param name="cancellationToken">Token used to abandon the call, typically the request abort token.</param>
    /// <returns>
    /// The current state of the search, or <see langword="null"/> when no search with that
    /// identifier exists. A missing search is an expected outcome, not a failure.
    /// </returns>
    /// <exception cref="RpcException">The Search Service was unreachable or answered with a failure status other than <see cref="StatusCode.NotFound"/>.</exception>
    Task<SearchResultsResponse?> GetSearchResultsAsync(Guid searchId, CancellationToken cancellationToken);
}
