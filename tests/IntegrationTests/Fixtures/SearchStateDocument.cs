namespace IntegrationTests.Fixtures;

/// <summary>
/// The body of <c>GET /searches/{searchId}</c> as the specification documents it, declared here
/// rather than reused from the gateway so the tests pin the wire contract independently.
/// </summary>
/// <param name="SearchId">Identifier of the polled search.</param>
/// <param name="IsCompleted">Whether the search has produced its final batch.</param>
/// <param name="Results">Hotels accumulated so far.</param>
internal sealed record SearchStateDocument(
    Guid SearchId,
    bool IsCompleted,
    IReadOnlyList<HotelDocument> Results);

/// <summary>
/// One hotel offer inside a <see cref="SearchStateDocument"/>.
/// </summary>
/// <param name="HotelId">Identifier of the offer.</param>
/// <param name="Name">Display name of the hotel.</param>
/// <param name="Price">Price of the offer.</param>
internal sealed record HotelDocument(string HotelId, string Name, decimal Price);
