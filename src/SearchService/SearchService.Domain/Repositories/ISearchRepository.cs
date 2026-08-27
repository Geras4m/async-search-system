using SearchService.Domain.Entities;

namespace SearchService.Domain.Repositories;

/// <summary>
/// Persistence boundary for the <see cref="Search"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// Declared in the Domain layer and implemented in Infrastructure, so the application
/// code never depends on where searches are actually stored. Swapping the in-memory
/// store for a database touches only the Infrastructure layer.
/// </para>
/// <para>
/// Implementations must be safe for concurrent use: the background execution engine
/// appends results to a search while clients read the same search through gRPC.
/// Implementations are expected to hand out snapshots rather than live aggregate
/// instances, so a reader can never observe a partially applied batch.
/// </para>
/// </remarks>
public interface ISearchRepository
{
    /// <summary>
    /// Loads a search by identifier.
    /// </summary>
    /// <param name="id">Identifier of the search.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A snapshot of the search, or <see langword="null"/> when no search with that identifier exists.
    /// </returns>
    Task<Search?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a newly created search.
    /// </summary>
    /// <param name="search">The search to store.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes once the search has been stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="search"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A search with the same identifier already exists.</exception>
    Task CreateAsync(Search search, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrites the stored state of an existing search.
    /// </summary>
    /// <param name="search">The search whose state should be persisted.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes once the search has been updated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="search"/> is <see langword="null"/>.</exception>
    /// <exception cref="Exceptions.SearchNotFoundException">No search with that identifier exists.</exception>
    Task UpdateAsync(Search search, CancellationToken cancellationToken = default);
}
