namespace SearchService.Application.Models;

/// <summary>
/// Transport-neutral projection of a hotel result returned by a query.
/// </summary>
/// <param name="HotelId">Stable identifier of the hotel offer.</param>
/// <param name="Name">Display name of the hotel.</param>
/// <param name="Price">Nightly price.</param>
public sealed record HotelResultDto(string HotelId, string Name, decimal Price);
