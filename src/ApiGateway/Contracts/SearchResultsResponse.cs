namespace ApiGateway.Contracts;

/// <summary>
/// Body of a successful <c>GET /searches/{searchId}</c> response: the state of a search at the
/// moment it was polled.
/// </summary>
/// <param name="SearchId">Identifier of the polled search.</param>
/// <param name="Destination">Destination the search was started for.</param>
/// <param name="IsCompleted">
/// <see langword="true"/> once every batch has been produced and the search is final;
/// <see langword="false"/> while results are still arriving.
/// </param>
/// <param name="Results">
/// Every hotel accumulated so far. Empty while the search is still warming up, never
/// <see langword="null"/>.
/// </param>
/// <remarks>
/// Serialized with the ASP.NET Core default camelCase naming policy, which produces exactly the
/// shape the specification mandates:
/// <c>{ "searchId": "...", "isCompleted": false, "results": [] }</c>.
/// </remarks>
public sealed record SearchResultsResponse(
    Guid SearchId,
    string Destination,
    bool IsCompleted,
    IReadOnlyList<HotelResultResponse> Results);

/// <summary>
/// A single hotel offer inside a <see cref="SearchResultsResponse"/>.
/// </summary>
/// <param name="HotelId">Stable identifier of the hotel offer.</param>
/// <param name="Name">Display name of the hotel.</param>
/// <param name="Price">
/// Price of the offer. Carried end to end as a <see cref="decimal"/> so no monetary precision is
/// lost in transport.
/// </param>
public sealed record HotelResultResponse(string HotelId, string Name, decimal Price);
