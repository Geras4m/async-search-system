namespace SearchService.Application.Abstractions;

/// <summary>
/// Hand-off point between the gRPC request thread and the background execution engine.
/// </summary>
/// <remarks>
/// <c>StartSearchCommand</c> must return an identifier immediately, so the actual work is
/// scheduled rather than awaited. This abstraction is the seam that keeps the request path
/// fast while the engine drains the backlog on its own schedule.
/// </remarks>
public interface ISearchExecutionScheduler
{
    /// <summary>
    /// Schedules a search for asynchronous execution.
    /// </summary>
    /// <param name="searchId">Identifier of the search to execute.</param>
    /// <param name="cancellationToken">Token used to cancel the scheduling operation.</param>
    /// <returns>A task that completes once the search has been accepted for execution.</returns>
    ValueTask ScheduleAsync(Guid searchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams scheduled search identifiers until cancellation.
    /// </summary>
    /// <param name="cancellationToken">Token that stops the stream, normally host shutdown.</param>
    /// <returns>An asynchronous stream of scheduled search identifiers.</returns>
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken);
}
