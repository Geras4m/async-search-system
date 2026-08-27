using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotificationService.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared.Common;
using Shared.EventContracts;

namespace NotificationService.Consumers;

/// <summary>
/// Long-running consumer of <see cref="SearchCompletedEvent"/> messages delivered to the
/// <see cref="MessagingConstants.NotificationSearchCompletedQueue"/> queue.
/// </summary>
/// <param name="connectionProvider">Supplies the shared broker connection.</param>
/// <param name="logger">Logger used to report consumed events and delivery failures.</param>
public sealed partial class SearchCompletedConsumer(
    IRabbitMqConnectionProvider connectionProvider,
    ILogger<SearchCompletedConsumer> logger) : BackgroundService
{
    /// <summary>
    /// Number of unacknowledged deliveries the broker may keep in flight for this consumer.
    /// Modest on purpose: handling an event is cheap, and a small window also keeps the
    /// redelivery burst after an unclean shutdown small.
    /// </summary>
    private const ushort PrefetchCount = 10;

    private IChannel? _channel;

    /// <summary>
    /// Declares the topology, starts consuming, and then stays alive until the host stops.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host begins shutting down.</param>
    /// <returns>A task that completes when the worker stops.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = await connectionProvider.GetConnectionAsync(stoppingToken).ConfigureAwait(false);
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
        _channel = channel;

        // The consumer declares the topology just as the publisher does. Both declarations are
        // idempotent and identical, which is what makes container start-up order irrelevant:
        // whichever service wins the race creates the exchange, the queue and the binding.
        await channel.ExchangeDeclareAsync(
            exchange: MessagingConstants.SearchCompletedExchange,
            type: MessagingConstants.SearchCompletedExchangeType,
            durable: true,
            autoDelete: false,
            cancellationToken: stoppingToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            queue: MessagingConstants.NotificationSearchCompletedQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: stoppingToken).ConfigureAwait(false);

        // A fanout exchange ignores the routing key, so an empty one is the conventional bind.
        await channel.QueueBindAsync(
            queue: MessagingConstants.NotificationSearchCompletedQueue,
            exchange: MessagingConstants.SearchCompletedExchange,
            routingKey: string.Empty,
            cancellationToken: stoppingToken).ConfigureAwait(false);

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: PrefetchCount,
            global: false,
            cancellationToken: stoppingToken).ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, eventArgs) => HandleDeliveryAsync(channel, eventArgs, stoppingToken);

        await channel.BasicConsumeAsync(
            queue: MessagingConstants.NotificationSearchCompletedQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken).ConfigureAwait(false);

        LogConsumerStarted(
            logger,
            MessagingConstants.NotificationSearchCompletedQueue,
            MessagingConstants.SearchCompletedExchange,
            PrefetchCount);

        // Deliveries arrive on the consumer callback, not on this task. A BackgroundService is
        // considered finished the moment ExecuteAsync returns, which would tear the channel
        // down again, so park here for the lifetime of the service and let the broker push.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the host is shutting down.
        }
    }

    /// <summary>
    /// Closes the consumer channel once the host has stopped the worker.
    /// </summary>
    /// <param name="cancellationToken">Token observed while the host stops.</param>
    /// <returns>A task that completes when the channel has been released.</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        var channel = Interlocked.Exchange(ref _channel, null);
        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        // The connection outlives this consumer: it belongs to the singleton provider, which
        // the host disposes asynchronously once every hosted service has stopped.
        LogConsumerStopped(logger);
    }

    /// <summary>
    /// Handles a single delivery: deserialise it, log it, and settle it with the broker.
    /// </summary>
    /// <param name="channel">Channel the delivery arrived on.</param>
    /// <param name="eventArgs">The delivery, including its body and delivery tag.</param>
    /// <param name="cancellationToken">Token signalled when the host shuts down.</param>
    /// <returns>A task that completes once the delivery has been acknowledged or rejected.</returns>
    private async Task HandleDeliveryAsync(
        IChannel channel,
        BasicDeliverEventArgs eventArgs,
        CancellationToken cancellationToken)
    {
        try
        {
            // Shutdown has begun, so do not start handling a message we may not finish.
            // Leaving it unsettled is the safe choice: the broker redelivers it on next start.
            cancellationToken.ThrowIfCancellationRequested();

            var completedEvent = JsonSerializer.Deserialize<SearchCompletedEvent>(
                eventArgs.Body.Span,
                EventSerialization.Options);

            if (completedEvent is null)
            {
                // A literal JSON null body is a POISON MESSAGE: no amount of redelivery turns
                // it into an event, so it is nacked WITHOUT requeue. Requeueing would put the
                // same body straight back at the head of the queue and loop forever.
                LogNullPayloadDiscarded(logger, eventArgs.DeliveryTag);
                await NackAsync(channel, eventArgs, requeue: false).ConfigureAwait(false);
                return;
            }

            LogSearchCompletedEventReceived(logger, completedEvent.SearchId, completedEvent.CompletedAtUtc);

            await AckAsync(channel, eventArgs).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown raced this delivery. It was never settled, so the broker will redeliver
            // it to the next consumer that starts. That is the at-least-once contract working
            // as intended; the only thing that would be wrong is letting this escape into the
            // consumer dispatcher, where it would be swallowed without a trace.
            LogDeliveryAbandonedDuringShutdown(logger, eventArgs.DeliveryTag);
        }
        catch (JsonException ex)
        {
            // POISON MESSAGE: malformed JSON cannot become valid on a retry. Nack WITHOUT
            // requeue so the broker discards it (or routes it to a dead-letter exchange, where
            // one is configured) instead of redelivering the same broken body to us forever.
            LogDeserializationFailed(logger, ex, eventArgs.DeliveryTag);
            await NackAsync(channel, eventArgs, requeue: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The payload parsed, so the message itself is not at fault. Requeue it and let the
            // broker hand it back for another attempt.
            LogHandlingFailed(logger, ex, eventArgs.DeliveryTag);
            await NackAsync(channel, eventArgs, requeue: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Acknowledges a delivery, swallowing broker errors so a failure to settle one message
    /// never escapes into the consumer dispatcher.
    /// </summary>
    /// <param name="channel">Channel the delivery arrived on.</param>
    /// <param name="eventArgs">The delivery being acknowledged.</param>
    /// <returns>A task that completes once the acknowledgement has been attempted.</returns>
    /// <remarks>
    /// Settling deliberately ignores the host's stopping token. The message has already been
    /// handled and logged; abandoning the acknowledgement because shutdown began would leave
    /// the broker holding an unacknowledged delivery and cause a duplicate on the next start.
    /// Writing an ack frame is trivial, so it is allowed to complete.
    /// </remarks>
    private async Task AckAsync(IChannel channel, BasicDeliverEventArgs eventArgs)
    {
        try
        {
            await channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogAckFailed(logger, ex, eventArgs.DeliveryTag);
        }
    }

    /// <summary>
    /// Negatively acknowledges a delivery, swallowing broker errors so that a failure to settle
    /// one message never escapes into the consumer dispatcher.
    /// </summary>
    /// <param name="channel">Channel the delivery arrived on.</param>
    /// <param name="eventArgs">The delivery being rejected.</param>
    /// <param name="requeue">Whether the broker should redeliver the message.</param>
    /// <returns>A task that completes once the rejection has been attempted.</returns>
    /// <remarks>
    /// Like <see cref="AckAsync"/>, settling ignores the host's stopping token so that a
    /// decision already taken about a message is actually communicated to the broker.
    /// </remarks>
    private async Task NackAsync(
        IChannel channel,
        BasicDeliverEventArgs eventArgs,
        bool requeue)
    {
        try
        {
            await channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRejectFailed(logger, ex, eventArgs.DeliveryTag, requeue);
        }
    }

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "Consuming search completion events. Queue={Queue} Exchange={Exchange} PrefetchCount={PrefetchCount}")]
    private static partial void LogConsumerStarted(ILogger logger, string queue, string exchange, ushort prefetchCount);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Search completed event received. SearchId={SearchId} CompletedAtUtc={CompletedAtUtc}")]
    private static partial void LogSearchCompletedEventReceived(ILogger logger, Guid searchId, DateTime completedAtUtc);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Search completed event payload was null and was discarded. DeliveryTag={DeliveryTag}")]
    private static partial void LogNullPayloadDiscarded(ILogger logger, ulong deliveryTag);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Error,
        Message = "Search completed event could not be deserialized and was discarded. DeliveryTag={DeliveryTag}")]
    private static partial void LogDeserializationFailed(ILogger logger, Exception exception, ulong deliveryTag);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Error,
        Message = "Search completed event handling failed and was requeued. DeliveryTag={DeliveryTag}")]
    private static partial void LogHandlingFailed(ILogger logger, Exception exception, ulong deliveryTag);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Warning,
        Message = "Failed to reject delivery. DeliveryTag={DeliveryTag} Requeue={Requeue}")]
    private static partial void LogRejectFailed(ILogger logger, Exception exception, ulong deliveryTag, bool requeue);

    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Information,
        Message = "Stopped consuming search completion events.")]
    private static partial void LogConsumerStopped(ILogger logger);

    /// <summary>Records a delivery abandoned unsettled because the host began shutting down.</summary>
    /// <param name="logger">Logger the entry is written to.</param>
    /// <param name="deliveryTag">Broker delivery tag of the abandoned message.</param>
    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Information,
        Message = "Search completed event abandoned unsettled during shutdown; the broker will redeliver it. DeliveryTag={DeliveryTag}")]
    private static partial void LogDeliveryAbandonedDuringShutdown(ILogger logger, ulong deliveryTag);

    /// <summary>Records a failure to acknowledge a delivery that was handled successfully.</summary>
    /// <param name="logger">Logger the entry is written to.</param>
    /// <param name="exception">The failure raised by the broker.</param>
    /// <param name="deliveryTag">Broker delivery tag of the message.</param>
    [LoggerMessage(
        EventId = 2011,
        Level = LogLevel.Warning,
        Message = "Failed to acknowledge a handled search completed event; it will be redelivered. DeliveryTag={DeliveryTag}")]
    private static partial void LogAckFailed(ILogger logger, Exception exception, ulong deliveryTag);
}
