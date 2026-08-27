using System.Globalization;
using Microsoft.Extensions.Options;
using SearchService.Application.Abstractions;
using SearchService.Application.Options;
using SearchService.Domain.Entities;

namespace SearchService.Infrastructure.Generation;

/// <summary>
/// <see cref="IHotelResultGenerator"/> that produces deterministic, contiguously numbered
/// fake hotels, standing in for a real supplier or inventory integration.
/// </summary>
/// <remarks>
/// <para>
/// Batch <c>N</c> yields hotels
/// <c>((N - 1) * HotelsPerBatch + 1)</c> through <c>(N * HotelsPerBatch)</c>, so with the
/// default of five hotels per batch, batch 1 produces "Hotel 1" to "Hotel 5" and batch 6
/// produces "Hotel 26" to "Hotel 30". Numbering is derived from the batch index rather
/// than from a running counter, which keeps the generator stateless and therefore safe to
/// share as a singleton across concurrently executing searches.
/// </para>
/// <para>
/// Only the price is random. Names are stable, which makes the batching behaviour easy to
/// assert in tests and easy to eyeball while polling the API.
/// </para>
/// </remarks>
/// <param name="options">Execution options supplying the batch size and the price range.</param>
/// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
public sealed class SequentialHotelResultGenerator(IOptions<SearchExecutionOptions> options) : IHotelResultGenerator
{
    private readonly SearchExecutionOptions _options =
        (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <summary>
    /// Generates the hotels belonging to one batch.
    /// </summary>
    /// <param name="batchNumber">One-based batch index.</param>
    /// <returns>
    /// The hotels for that batch, exactly <c>HotelsPerBatch</c> of them, in ascending
    /// hotel-number order.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="batchNumber"/> is less than one, or the configured
    /// <c>MinHotelPrice</c> is greater than the configured <c>MaxHotelPrice</c>.
    /// </exception>
    /// <remarks>
    /// Prices are drawn from <see cref="Random.Shared"/>, which is thread-safe, so no
    /// synchronisation is needed even when several searches generate batches at once.
    /// Names are formatted with <see cref="CultureInfo.InvariantCulture"/> so the wire
    /// representation never depends on the host's locale.
    /// </remarks>
    public IReadOnlyList<HotelResult> GenerateBatch(int batchNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchNumber, 1);

        int hotelsPerBatch = _options.HotelsPerBatch;
        int firstHotelNumber = ((batchNumber - 1) * hotelsPerBatch) + 1;

        HotelResult[] batch = new HotelResult[hotelsPerBatch];

        for (int offset = 0; offset < hotelsPerBatch; offset++)
        {
            int hotelNumber = firstHotelNumber + offset;

            batch[offset] = new HotelResult(
                HotelId: Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                Name: string.Create(CultureInfo.InvariantCulture, $"Hotel {hotelNumber}"),
                Price: Random.Shared.Next(_options.MinHotelPrice, _options.MaxHotelPrice));
        }

        return batch;
    }
}
