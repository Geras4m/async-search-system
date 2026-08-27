using Shared.EventContracts;

namespace SearchService.Application.Abstractions;

/// <summary>
/// Durable record of search events that still owe a delivery to the message broker.
/// </summary>
/// <remarks>
/// <para>
/// Without this, publishing is at-most-once. The completion of a search is persisted before the
/// event is published, so a broker outage at exactly that moment left the search correctly marked
/// complete and the announcement gone for good: nothing retried it, and the Notification Service
/// never learned. That is a silent hole in an event-driven system, because the two facts that must
/// agree are written to two different places with no link between them.
/// </para>
/// <para>
/// The outbox is that link. The intent to publish is recorded alongside the state change, and a
/// background publisher drains it until the broker accepts each event, which turns delivery into
/// at-least-once. Consumers must therefore be idempotent, which the Notification Service is: it
/// logs by <c>SearchId</c> and holds no state.
/// </para>
/// <para>
/// The implementation here is in-memory, matching the in-memory search repository. In a real
/// deployment it would be a table written in the same transaction as the aggregate, which is what
/// makes the pattern genuinely atomic. Swapping it changes no code in this layer.
/// </para>
/// </remarks>
public interface ISearchEventOutbox
{
    /// <summary>
    /// Records that an event still has to reach the broker.
    /// </summary>
    /// <param name="completedEvent">The event awaiting delivery.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes once the event has been recorded.</returns>
    /// <remarks>
    /// Enqueuing the same search twice is a no-op. The event is keyed by its
    /// <see cref="SearchCompletedEvent.SearchId"/>, so a retried command cannot queue a duplicate.
    /// </remarks>
    ValueTask EnqueueAsync(SearchCompletedEvent completedEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns events still awaiting delivery, oldest first.
    /// </summary>
    /// <param name="maxCount">Maximum number of events to return.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The pending events, oldest first, at most <paramref name="maxCount"/> of them.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCount"/> is less than one.</exception>
    Task<IReadOnlyList<SearchCompletedEvent>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an event once the broker has accepted it.
    /// </summary>
    /// <param name="searchId">Identifier of the search whose event was delivered.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task producing <see langword="true"/> when an entry was removed, and
    /// <see langword="false"/> when there was nothing left to remove.
    /// </returns>
    Task<bool> RemoveAsync(Guid searchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Number of events still awaiting delivery.
    /// </summary>
    /// <remarks>Exposed for diagnostics and tests; a growing value means the broker is unhealthy.</remarks>
    int PendingCount { get; }
}
