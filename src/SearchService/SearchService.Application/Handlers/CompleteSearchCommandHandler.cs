using MediatR;
using Microsoft.Extensions.Logging;
using SearchService.Application.Abstractions;
using SearchService.Application.Commands;
using SearchService.Domain.Exceptions;
using SearchService.Domain.Repositories;
using Shared.EventContracts;

namespace SearchService.Application.Handlers;

/// <summary>
/// Handles <see cref="CompleteSearchCommand"/>: marks the search completed, persists that state
/// and publishes the <see cref="SearchCompletedEvent"/> to the message broker.
/// </summary>
/// <param name="repository">Persistence boundary for the search aggregate.</param>
/// <param name="eventsPublisher">Outbound messaging boundary for search domain events.</param>
/// <param name="outbox">Records events that still owe a delivery to the broker.</param>
/// <param name="clock">Supplies the completion timestamp.</param>
/// <param name="logger">Sink for structured log records.</param>
/// <remarks>
/// <para>
/// Completing an already completed search is a no-op: the state is not rewritten and the event is
/// not published again, so a retried or duplicated command cannot fan out duplicate notifications.
/// </para>
/// <para>
/// Delivery of the event is at-least-once, not best-effort. The intent to publish is recorded in
/// the outbox before the broker is contacted, so a publish that fails is retried in the background
/// rather than lost. The inline publish is an optimisation for the common case: it removes the
/// outbox entry on success, so a healthy system never waits for a sweep.
/// </para>
/// </remarks>
public sealed partial class CompleteSearchCommandHandler(
    ISearchRepository repository,
    ISearchEventsPublisher eventsPublisher,
    ISearchEventOutbox outbox,
    IClock clock,
    ILogger<CompleteSearchCommandHandler> logger)
    : IRequestHandler<CompleteSearchCommand>
{
    /// <summary>
    /// Completes a search and announces it to the rest of the system.
    /// </summary>
    /// <param name="request">The command to handle.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes once the search is persisted and the event published.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="SearchNotFoundException">No search with the requested identifier exists.</exception>
    public async Task Handle(CompleteSearchCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var search = await repository.GetAsync(request.SearchId, cancellationToken)
            ?? throw new SearchNotFoundException(request.SearchId);

        var completedAtUtc = clock.UtcNow;

        if (!search.MarkCompleted(completedAtUtc))
        {
            // Already complete: the state is final and the event has been published once already.
            LogAlreadyCompleted(logger, request.SearchId);
            return;
        }

        // Persist the completed state before publishing. Completion is the fact clients poll for;
        // the event merely announces it. Writing first means a broker outage can never leave a
        // search stuck in the running state, and the caller can safely retry the command.
        await repository.UpdateAsync(search, cancellationToken);

        LogSearchCompleted(logger, request.SearchId);

        var completedEvent = new SearchCompletedEvent(search.Id, completedAtUtc);

        // Record the obligation before attempting it. This ordering is the whole point of the
        // outbox: if the process dies or the broker is unreachable between here and the publish
        // below, the event is still owed and the background publisher will deliver it. Enqueuing
        // after a successful publish instead would leave exactly the gap this closes.
        await outbox.EnqueueAsync(completedEvent, cancellationToken);

        try
        {
            await eventsPublisher.PublishSearchCompletedAsync(completedEvent, cancellationToken);
        }
        catch (Exception exception)
        {
            // Deliberately not rethrown. The search is complete and the event is safely owed, so
            // this is a deferral rather than a failure, and letting it propagate would mark the
            // whole search execution as failed for something that will resolve itself. Logged at
            // Warning because a persistently unhealthy broker still needs to be visible.
            LogEventPublishDeferred(logger, request.SearchId, exception);
            return;
        }

        // Delivered inline, so nothing is owed any more.
        await outbox.RemoveAsync(request.SearchId, cancellationToken);
    }

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Search completed. SearchId={SearchId}")]
    private static partial void LogSearchCompleted(ILogger logger, Guid searchId);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Debug,
        Message = "Search was already completed, nothing to publish. SearchId={SearchId}")]
    private static partial void LogAlreadyCompleted(ILogger logger, Guid searchId);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Warning,
        Message = "Publishing the search completed event failed; it stays in the outbox for retry. SearchId={SearchId}")]
    private static partial void LogEventPublishDeferred(ILogger logger, Guid searchId, Exception exception);
}
