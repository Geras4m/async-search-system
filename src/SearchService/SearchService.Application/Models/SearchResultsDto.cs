namespace SearchService.Application.Models;

/// <summary>
/// Current state of a search: everything accumulated so far plus the completion flag.
/// </summary>
/// <param name="SearchId">Identifier of the search.</param>
/// <param name="IsCompleted">Whether result generation has finished.</param>
/// <param name="CreatedAtUtc">UTC instant the search was created.</param>
/// <param name="Results">Results accumulated so far. Empty while the first batch is pending.</param>
public sealed record SearchResultsDto(
    Guid SearchId,
    bool IsCompleted,
    DateTime CreatedAtUtc,
    IReadOnlyList<HotelResultDto> Results);
