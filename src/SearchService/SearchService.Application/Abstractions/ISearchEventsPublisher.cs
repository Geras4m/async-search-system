using Shared.EventContracts;

namespace SearchService.Application.Abstractions;

/// <summary>
/// Outbound messaging boundary for search domain events.
/// </summary>
/// <remarks>
/// The Application layer depends on this abstraction only. The RabbitMQ implementation
/// lives in Infrastructure, which is what keeps the broker choice out of the handlers
/// and makes <c>CompleteSearchCommandHandler</c> unit-testable without a broker.
/// </remarks>
public interface ISearchEventsPublisher
{
    /// <summary>
    /// Publishes a <see cref="SearchCompletedEvent"/> to the message broker.
    /// </summary>
    /// <param name="completedEvent">The event to publish.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes once the broker has accepted the message.</returns>
    Task PublishSearchCompletedAsync(SearchCompletedEvent completedEvent, CancellationToken cancellationToken = default);
}
