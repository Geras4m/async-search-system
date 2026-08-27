namespace ApiGateway.Contracts;

/// <summary>
/// Body of a successful <c>POST /searches</c> response.
/// </summary>
/// <param name="SearchId">
/// Identifier of the search that was just registered. Clients poll
/// <c>GET /searches/{searchId}</c> with this value until the search completes.
/// </param>
public sealed record StartSearchResponse(Guid SearchId);
