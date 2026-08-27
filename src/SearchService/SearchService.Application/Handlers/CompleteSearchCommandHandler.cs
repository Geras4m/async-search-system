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
/// <param name="clock">Supplies the completion timestamp.</param>
/// <param name="logger">Sink for structured log records.</param>
/// <remarks>
/// Completing an already completed search is a no-op: the state is not rewritten and the event is
/// not published again, so a retried or duplicated command cannot fan out duplicate notifications.
/// </remarks>
public sealed partial class CompleteSearchCommandHandler(
    ISearchRepository repository,
    ISearchEventsPublisher eventsPublisher,
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

        try
        {
            await eventsPublisher.PublishSearchCompletedAsync(
                new SearchCompletedEvent(search.Id, completedAtUtc),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // The search stays completed; only the announcement failed. Surfaced to the caller so
            // the failure is visible rather than silently swallowed, and logged with the identifier
            // so the affected search can be found in the logs.
            LogEventPublishFailed(logger, request.SearchId, exception);
            throw;
        }
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
        Level = LogLevel.Error,
        Message = "Publishing the search completed event failed. SearchId={SearchId}")]
    private static partial void LogEventPublishFailed(ILogger logger, Guid searchId, Exception exception);
}
