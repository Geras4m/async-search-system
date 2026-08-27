namespace SearchService.Domain.Entities;

/// <summary>
/// A single hotel offer produced by a search.
/// </summary>
/// <param name="HotelId">Stable identifier of the hotel offer.</param>
/// <param name="Name">Display name of the hotel.</param>
/// <param name="Price">Nightly price. Money, so <see cref="decimal"/> rather than a float.</param>
/// <remarks>
/// Immutable by design. A background worker appends results while clients read them, and an
/// immutable value can be handed to a reader with no risk of it changing underneath.
/// </remarks>
public sealed record HotelResult(string HotelId, string Name, decimal Price);
