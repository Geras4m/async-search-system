using System.ComponentModel.DataAnnotations;

namespace SearchService.Application.Options;

/// <summary>
/// Settings for the background publisher that drains the search event outbox, bound from the
/// <c>Outbox</c> configuration section.
/// </summary>
public sealed class SearchEventOutboxOptions
{
    /// <summary>Name of the configuration section these options bind to.</summary>
    public const string SectionName = "Outbox";

    /// <summary>How long the publisher waits between sweeps of the outbox.</summary>
    /// <remarks>
    /// <para>
    /// Only undelivered events wait for a sweep. The happy path publishes inline during the
    /// completion command and removes its entry immediately, so this interval governs recovery
    /// latency after a broker outage, not normal notification latency.
    /// </para>
    /// <para>
    /// An entry is visible to a sweep for as long as its inline publish is still running, so an
    /// interval shorter than the publish deadline lets a slow but successful publish be sent a
    /// second time. That is harmless under the at-least-once contract the outbox provides, and
    /// only reachable against a degraded broker; raising this value trades recovery latency for a
    /// narrower window. Eliminating it entirely needs a lease rather than a plain read.
    /// </para>
    /// </remarks>
    [Required]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum number of events the publisher takes in a single sweep.</summary>
    /// <remarks>
    /// Bounds the work done while the broker is recovering, so a large backlog is drained over
    /// several sweeps instead of one long burst.
    /// </remarks>
    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 50;
}
