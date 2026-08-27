using System.Collections.Concurrent;
using SearchService.Application.Abstractions;
using Shared.EventContracts;

namespace SearchService.Infrastructure.Persistence;

/// <summary>
/// Process-local <see cref="ISearchEventOutbox"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by search identifier.
/// </summary>
/// <remarks>
/// <para>
/// Must be registered as a singleton: it is the store, and a scoped registration would give the
/// completion handler and the background publisher two different outboxes, so nothing would ever
/// be retried.
/// </para>
/// <para>
/// Keying by <see cref="SearchCompletedEvent.SearchId"/> makes <c>EnqueueAsync</c> idempotent for
/// free, which matters because a retried <c>CompleteSearchCommand</c> must not queue a second
/// delivery for the same search.
/// </para>
/// <para>
/// Ordering is by enqueue sequence rather than by timestamp. Two searches completing inside the
/// same clock tick would otherwise have no defined order, and a monotonic counter also keeps the
/// ordering stable if the system clock moves.
/// </para>
/// <para>
/// This mirrors the in-memory search repository, and carries the same caveat: a process restart
/// loses undelivered events. A database-backed implementation writing in the same transaction as
/// the aggregate is what makes the pattern fully atomic, and would replace this type alone.
/// </para>
/// </remarks>
public sealed class InMemorySearchEventOutbox : ISearchEventOutbox
{
    private readonly ConcurrentDictionary<Guid, Entry> _pending = new();
    private long _sequence;

    /// <inheritdoc />
    public int PendingCount => _pending.Count;

    /// <inheritdoc />
    public ValueTask EnqueueAsync(SearchCompletedEvent completedEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedEvent);
        cancellationToken.ThrowIfCancellationRequested();

        // GetOrAdd rather than an assignment: re-enqueuing an event that is already owed must not
        // move it to the back of the queue, or a permanently failing event could starve the rest.
        _pending.GetOrAdd(
            completedEvent.SearchId,
            static (_, state) => new Entry(Interlocked.Increment(ref state.Owner._sequence), state.Event),
            (Owner: this, Event: completedEvent));

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchCompletedEvent>> GetPendingAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<SearchCompletedEvent> batch =
        [
            .. _pending.Values
                .OrderBy(entry => entry.Sequence)
                .Take(maxCount)
                .Select(entry => entry.Event),
        ];

        return Task.FromResult(batch);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(Guid searchId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_pending.TryRemove(searchId, out _));
    }

    /// <summary>One owed delivery, carrying the order in which it was recorded.</summary>
    /// <param name="Sequence">Monotonic enqueue order.</param>
    /// <param name="Event">The event awaiting delivery.</param>
    private sealed record Entry(long Sequence, SearchCompletedEvent Event);
}
