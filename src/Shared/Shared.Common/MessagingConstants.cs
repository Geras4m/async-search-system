namespace Shared.Common;

/// <summary>
/// Broker topology shared by the publisher (Search Service) and the consumer
/// (Notification Service). Both sides declare it, so start-up order does not matter.
/// </summary>
public static class MessagingConstants
{
    /// <summary>
    /// Fanout exchange the Search Service publishes search completion events to.
    /// </summary>
    public const string SearchCompletedExchange = "search.completed";

    /// <summary>
    /// Durable queue the Notification Service consumes search completion events from.
    /// </summary>
    public const string NotificationSearchCompletedQueue = "notification.search.completed";

    /// <summary>
    /// Exchange type used for <see cref="SearchCompletedExchange"/>.
    /// </summary>
    /// <remarks>
    /// Fanout is deliberate: a completion event is a broadcast fact, and additional
    /// subscribers must be able to bind their own queues without the publisher changing.
    /// </remarks>
    public const string SearchCompletedExchangeType = "fanout";

    /// <summary>
    /// Content type stamped on published event messages.
    /// </summary>
    public const string EventContentType = "application/json";
}
