using MediatR;
using Microsoft.Extensions.Logging;
using SearchService.Application.Abstractions;
using SearchService.Application.Commands;
using SearchService.Domain.Entities;
using SearchService.Domain.Repositories;

namespace SearchService.Application.Handlers;

/// <summary>
/// Handles <see cref="StartSearchCommand"/>: creates the <see cref="Search"/> aggregate,
/// persists its initial state and hands the identifier to the background execution engine.
/// </summary>
/// <param name="repository">Persistence boundary for the <see cref="Search"/> aggregate.</param>
/// <param name="scheduler">Hand-off point to the background execution engine.</param>
/// <param name="clock">Supplies the creation timestamp.</param>
/// <param name="logger">Sink for structured log records.</param>
/// <remarks>
/// The command returns as soon as the identifier exists. No result generation happens on the
/// request path, which is what keeps the gRPC call fast regardless of how long a search runs.
/// </remarks>
public sealed partial class StartSearchCommandHandler(
    ISearchRepository repository,
    ISearchExecutionScheduler scheduler,
    IClock clock,
    ILogger<StartSearchCommandHandler> logger)
    : IRequestHandler<StartSearchCommand, StartSearchResult>
{
    /// <summary>
    /// Creates a search, stores it and schedules it for asynchronous execution.
    /// </summary>
    /// <param name="request">The command to handle.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The identifier the client polls with.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public async Task<StartSearchResult> Handle(StartSearchCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var search = Search.Create(Guid.NewGuid(), request.Destination, clock.UtcNow);

        // Ordering is deliberate: persist first, schedule second. The background engine picks an
        // identifier up the instant it is queued, so scheduling before the repository knows about
        // the search would let the engine dequeue an id that cannot be loaded yet.
        await repository.CreateAsync(search, cancellationToken);

        // Logged before scheduling so the creation record can never appear after the first
        // "Batch added" line written by the engine for the very same search.
        LogSearchCreated(logger, search.Id, search.Destination);

        await scheduler.ScheduleAsync(search.Id, cancellationToken);

        return new StartSearchResult(search.Id);
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Search created. SearchId={SearchId} Destination={Destination}")]
    private static partial void LogSearchCreated(ILogger logger, Guid searchId, string destination);
}
