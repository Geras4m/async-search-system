using System.Collections.Concurrent;
using SearchService.Domain.Entities;
using SearchService.Domain.Exceptions;
using SearchService.Domain.Repositories;

namespace SearchService.Infrastructure.Persistence;

/// <summary>
/// Process-local <see cref="ISearchRepository"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type is the store, not a cache in front of one, so it must be registered as a
/// singleton. A scoped or transient registration would give every request its own empty
/// dictionary and searches would appear to vanish between the POST and the first GET.
/// </para>
/// <para>
/// <b>Why every entry is a snapshot.</b> Two callers touch the same search at the same
/// time: the background execution engine appends a batch every few seconds, while gRPC
/// callers poll for results. <see cref="Search"/> is deliberately not thread-safe — it
/// wraps a plain <see cref="List{T}"/>. If the dictionary handed out the stored instance,
/// a reader could enumerate that list while the engine was adding to it, which is a torn
/// read: either an <see cref="InvalidOperationException"/> from the enumerator, or a
/// result set containing half of an appended batch.
/// </para>
/// <para>
/// Storing <see cref="Search.CreateSnapshot"/> on write and returning
/// <see cref="Search.CreateSnapshot"/> on read removes that race without a single lock.
/// Each stored value is reachable only through the dictionary and is never mutated after
/// publication; each returned value belongs solely to its caller. Writers therefore
/// replace a reference — an atomic operation — instead of mutating shared state, and a
/// reader either sees the state before a batch or the state after it, never something in
/// between. <see cref="HotelResult"/> is an immutable record, so copying the list is
/// sufficient to make the copy independent.
/// </para>
/// <para>
/// Swapping this for a database implementation touches no other layer: the same
/// snapshot-on-read guarantee falls naturally out of loading rows into fresh objects.
/// </para>
/// </remarks>
public sealed class InMemorySearchRepository : ISearchRepository
{
    private readonly ConcurrentDictionary<Guid, Search> _searches = new();

    /// <summary>
    /// Loads a search by identifier.
    /// </summary>
    /// <param name="id">Identifier of the search.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task producing an independent snapshot of the search, or <see langword="null"/>
    /// when no search with that identifier exists.
    /// </returns>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    /// <remarks>
    /// The returned aggregate is a copy. Mutating it has no effect on the store; persisting
    /// those mutations is what <see cref="UpdateAsync"/> is for.
    /// </remarks>
    public Task<Search?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Search? snapshot = _searches.TryGetValue(id, out Search? stored)
            ? stored.CreateSnapshot()
            : null;

        return Task.FromResult(snapshot);
    }

    /// <summary>
    /// Stores a newly created search.
    /// </summary>
    /// <param name="search">The search to store.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes once the search has been stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="search"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A search with the same identifier already exists.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    /// <remarks>
    /// Insertion goes through <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> so a
    /// duplicate identifier is rejected atomically rather than silently overwriting a
    /// search that is already collecting results.
    /// </remarks>
    public Task CreateAsync(Search search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_searches.TryAdd(search.Id, search.CreateSnapshot()))
        {
            throw new InvalidOperationException(
                $"Search '{search.Id}' already exists and cannot be created twice.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Overwrites the stored state of an existing search.
    /// </summary>
    /// <param name="search">The search whose state should be persisted.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes once the search has been updated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="search"/> is <see langword="null"/>.</exception>
    /// <exception cref="SearchNotFoundException">No search with that identifier exists.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    /// <remarks>
    /// <para>
    /// The update is a read/compare/replace loop over
    /// <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/>. That is deliberate:
    /// <c>TryUpdate</c> replaces a value only while the key is still present, so an update
    /// racing with a removal fails instead of re-inserting — a plain indexer assignment or
    /// an <c>AddOrUpdate</c> would resurrect a deleted search.
    /// </para>
    /// <para>
    /// A failed <c>TryUpdate</c> means another writer replaced the value first, so the loop
    /// re-reads and retries; the entry disappearing entirely surfaces as
    /// <see cref="SearchNotFoundException"/>.
    /// </para>
    /// </remarks>
    public Task UpdateAsync(Search search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        cancellationToken.ThrowIfCancellationRequested();

        Search snapshot = search.CreateSnapshot();

        while (true)
        {
            if (!_searches.TryGetValue(search.Id, out Search? current))
            {
                throw new SearchNotFoundException(search.Id);
            }

            if (_searches.TryUpdate(search.Id, snapshot, current))
            {
                return Task.CompletedTask;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
