using System.ComponentModel.DataAnnotations;

namespace SearchService.Application.Options;

/// <summary>
/// Tuning knobs for the asynchronous search execution engine, bound from the
/// <c>Search</c> configuration section.
/// </summary>
/// <remarks>
/// The defaults are exactly the values the functional specification calls for:
/// six batches of five hotels, five seconds apart. They are configurable so that
/// automated tests can compress a 30 second workflow into a fraction of a second
/// without changing production behaviour.
/// </remarks>
public sealed class SearchExecutionOptions
{
    /// <summary>Name of the configuration section these options bind to.</summary>
    public const string SectionName = "Search";

    /// <summary>Number of batches appended before a search is marked complete.</summary>
    [Range(1, 1000)]
    public int BatchCount { get; set; } = 6;

    /// <summary>Number of hotels generated per batch.</summary>
    [Range(1, 1000)]
    public int HotelsPerBatch { get; set; } = 5;

    /// <summary>Delay before each batch is appended.</summary>
    [Required]
    public TimeSpan BatchInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum number of searches the engine executes concurrently.</summary>
    [Range(1, 10_000)]
    public int MaxConcurrentSearches { get; set; } = 64;

    /// <summary>Lowest price, inclusive, used when generating fake hotel offers.</summary>
    [Range(0, 1_000_000)]
    public int MinHotelPrice { get; set; } = 80;

    /// <summary>Highest price, exclusive, used when generating fake hotel offers.</summary>
    [Range(1, 1_000_000)]
    public int MaxHotelPrice { get; set; } = 400;
}
