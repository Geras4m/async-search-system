using SearchService.Domain.Entities;

namespace SearchService.Application.Abstractions;

/// <summary>
/// Produces the hotel offers that make up one batch of a search.
/// </summary>
/// <remarks>
/// Stands in for whatever a real system would call: a supplier aggregator, a cache,
/// an inventory service. Keeping it behind an interface means the handlers and their
/// tests never depend on the fake data generator.
/// </remarks>
public interface IHotelResultGenerator
{
    /// <summary>
    /// Generates the hotels belonging to one batch.
    /// </summary>
    /// <param name="batchNumber">One-based batch index.</param>
    /// <returns>The hotels for that batch.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchNumber"/> is less than one.</exception>
    IReadOnlyList<HotelResult> GenerateBatch(int batchNumber);
}
