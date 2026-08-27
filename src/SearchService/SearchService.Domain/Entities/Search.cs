namespace SearchService.Domain.Entities;

/// <summary>
/// Aggregate root tracking one asynchronous hotel search: its accumulated results
/// and whether result generation has finished.
/// </summary>
/// <remarks>
/// <para>
/// State changes go through <see cref="AppendResults"/> and <see cref="MarkCompleted"/>
/// rather than public setters, so the aggregate cannot be driven into an invalid state
/// (for example gaining results after completion).
/// </para>
/// <para>
/// The type is not thread-safe on its own. Safe concurrent use is the repository's job:
/// see the snapshot behaviour documented on <c>ISearchRepository</c>.
/// </para>
/// </remarks>
public sealed class Search
{
    private readonly List<HotelResult> _results;

    /// <summary>
    /// Rehydrates a search from stored state.
    /// </summary>
    /// <param name="id">Identifier of the search.</param>
    /// <param name="createdAtUtc">UTC instant the search was created.</param>
    /// <param name="isCompleted">Whether result generation has finished.</param>
    /// <param name="completedAtUtc">UTC instant the search completed, or <see langword="null"/> if still running.</param>
    /// <param name="results">Results accumulated so far.</param>
    public Search(
        Guid id,
        DateTime createdAtUtc,
        bool isCompleted,
        DateTime? completedAtUtc,
        IEnumerable<HotelResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        Id = id;
        CreatedAtUtc = createdAtUtc;
        IsCompleted = isCompleted;
        CompletedAtUtc = completedAtUtc;
        _results = [.. results];
    }

    /// <summary>Unique identifier clients poll with.</summary>
    public Guid Id { get; }

    /// <summary>UTC instant at which the search was created.</summary>
    public DateTime CreatedAtUtc { get; }

    /// <summary><see langword="true"/> once the final batch has been appended.</summary>
    public bool IsCompleted { get; private set; }

    /// <summary>UTC instant at which the search completed, or <see langword="null"/> while it is still running.</summary>
    public DateTime? CompletedAtUtc { get; private set; }

    /// <summary>Every hotel result accumulated so far, in the order the batches were appended.</summary>
    public IReadOnlyList<HotelResult> Results => _results;

    /// <summary>
    /// Starts a brand new search with no results.
    /// </summary>
    /// <param name="id">Identifier to assign.</param>
    /// <param name="createdAtUtc">UTC creation instant.</param>
    /// <returns>A search in its initial, incomplete state.</returns>
    public static Search Create(Guid id, DateTime createdAtUtc) =>
        new(id, createdAtUtc, isCompleted: false, completedAtUtc: null, results: []);

    /// <summary>
    /// Appends one batch of hotel results.
    /// </summary>
    /// <param name="batch">Results to append.</param>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The search has already been completed.</exception>
    public void AppendResults(IEnumerable<HotelResult> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (IsCompleted)
        {
            throw new InvalidOperationException(
                $"Search '{Id}' is already completed and cannot accept further results.");
        }

        _results.AddRange(batch);
    }

    /// <summary>
    /// Marks the search as completed. Calling this on an already completed search is a no-op,
    /// which keeps the operation idempotent under message or job retries.
    /// </summary>
    /// <param name="completedAtUtc">UTC instant of completion.</param>
    /// <returns>
    /// <see langword="true"/> if this call completed the search, <see langword="false"/> if it was already complete.
    /// </returns>
    public bool MarkCompleted(DateTime completedAtUtc)
    {
        if (IsCompleted)
        {
            return false;
        }

        IsCompleted = true;
        CompletedAtUtc = completedAtUtc;

        return true;
    }

    /// <summary>
    /// Produces an independent copy of this aggregate.
    /// </summary>
    /// <returns>A copy that shares no mutable state with the original.</returns>
    /// <remarks>
    /// The repository snapshots on read and on write so a reader can never observe a
    /// half-appended batch while the background engine is writing.
    /// <see cref="HotelResult"/> is immutable, so copying the list is enough.
    /// </remarks>
    public Search CreateSnapshot() => new(Id, CreatedAtUtc, IsCompleted, CompletedAtUtc, _results);
}
