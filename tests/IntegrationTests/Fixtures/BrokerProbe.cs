using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared.Common;
using Shared.EventContracts;

namespace IntegrationTests.Fixtures;

/// <summary>
/// A direct AMQP client for the tests: it can watch what the Search Service publishes and it can
/// publish an event itself, which is what lets the two halves of the messaging contract be
/// proven separately.
/// </summary>
/// <remarks>
/// The probe binds its own throwaway queue to the completion exchange. Because that exchange is
/// a fanout, watching it neither competes with the Notification Service for messages nor
/// requires the Notification Service to be running.
/// </remarks>
internal sealed class BrokerProbe : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly Channel<byte[]> _deliveries = Channel.CreateUnbounded<byte[]>();

    private BrokerProbe(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    /// <summary>
    /// Opens a connection and a channel to the broker.
    /// </summary>
    /// <param name="endpoint">Broker to connect to.</param>
    /// <param name="cancellationToken">Token that abandons the attempt.</param>
    /// <returns>A connected probe.</returns>
    public static async Task<BrokerProbe> ConnectAsync(BrokerEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var connectionFactory = new ConnectionFactory
        {
            HostName = endpoint.Host,
            Port = endpoint.Port,
            UserName = endpoint.UserName,
            Password = endpoint.Password,
            VirtualHost = "/",
            ClientProvidedName = "integration-tests",
        };

        var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);

        try
        {
            var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            return new BrokerProbe(connection, channel);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Declares the completion exchange and binds a throwaway queue to it, then starts consuming.
    /// </summary>
    /// <param name="cancellationToken">Token that abandons the operation.</param>
    /// <returns>A task that completes once the queue is bound and being consumed.</returns>
    /// <remarks>
    /// The queue is exclusive and auto-deleted, so it exists only for this connection and leaves
    /// nothing behind. The exchange declaration matches the publisher's exactly, which is what
    /// makes it idempotent: whichever side declares first, both agree on the topology.
    /// </remarks>
    public async Task WatchCompletionEventsAsync(CancellationToken cancellationToken)
    {
        await _channel.ExchangeDeclareAsync(
            exchange: MessagingConstants.SearchCompletedExchange,
            type: MessagingConstants.SearchCompletedExchangeType,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        var queue = await _channel.QueueDeclareAsync(
            queue: string.Empty,
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: cancellationToken);

        await _channel.QueueBindAsync(
            queue: queue.QueueName,
            exchange: MessagingConstants.SearchCompletedExchange,
            routingKey: string.Empty,
            cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);

        consumer.ReceivedAsync += (_, delivery) =>
        {
            // The body is only valid for the duration of the callback, so it is copied out.
            _deliveries.Writer.TryWrite(delivery.Body.ToArray());

            return Task.CompletedTask;
        };

        await _channel.BasicConsumeAsync(
            queue: queue.QueueName,
            autoAck: true,
            consumer: consumer,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Publishes a completion event exactly as the Search Service does.
    /// </summary>
    /// <param name="completedEvent">Event to publish.</param>
    /// <param name="cancellationToken">Token that abandons the operation.</param>
    /// <returns>A task that completes once the broker has accepted the message.</returns>
    public async Task PublishAsync(SearchCompletedEvent completedEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completedEvent);

        await _channel.ExchangeDeclareAsync(
            exchange: MessagingConstants.SearchCompletedExchange,
            type: MessagingConstants.SearchCompletedExchangeType,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        var body = JsonSerializer.SerializeToUtf8Bytes(completedEvent, EventSerialization.Options);

        await PublishBodyAsync(
            body,
            completedEvent.SearchId.ToString("D", CultureInfo.InvariantCulture),
            cancellationToken);
    }

    /// <summary>
    /// Publishes an arbitrary body to the completion exchange, so a consumer can be shown what it
    /// does with a payload it cannot read.
    /// </summary>
    /// <param name="body">Body to publish verbatim.</param>
    /// <param name="cancellationToken">Token that abandons the operation.</param>
    /// <returns>A task that completes once the broker has accepted the message.</returns>
    public async Task PublishRawAsync(byte[] body, CancellationToken cancellationToken)
    {
        await _channel.ExchangeDeclareAsync(
            exchange: MessagingConstants.SearchCompletedExchange,
            type: MessagingConstants.SearchCompletedExchangeType,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        await PublishBodyAsync(body, messageId: null, cancellationToken);
    }

    /// <summary>
    /// Publishes one message to the completion exchange.
    /// </summary>
    /// <param name="body">Body to publish.</param>
    /// <param name="messageId">Identifier stamped on the message, if any.</param>
    /// <param name="cancellationToken">Token that abandons the operation.</param>
    /// <returns>A task that completes once the broker has accepted the message.</returns>
    private async Task PublishBodyAsync(byte[] body, string? messageId, CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = MessagingConstants.EventContentType,
        };

        if (messageId is not null)
        {
            properties.MessageId = messageId;
        }

        await _channel.BasicPublishAsync(
            exchange: MessagingConstants.SearchCompletedExchange,
            routingKey: string.Empty,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Waits for a completion event carrying the given identifier.
    /// </summary>
    /// <param name="searchId">Identifier the awaited event must carry.</param>
    /// <param name="timeout">Budget for the wait.</param>
    /// <param name="cancellationToken">Token that abandons the wait.</param>
    /// <returns>The matching event, together with the raw JSON it arrived as.</returns>
    /// <exception cref="TimeoutException">No matching event arrived within the budget.</exception>
    public async Task<ObservedEvent> WaitForEventAsync(
        Guid searchId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        var seen = 0;

        try
        {
            while (true)
            {
                var body = await _deliveries.Reader.ReadAsync(budget.Token);
                seen++;

                var json = Encoding.UTF8.GetString(body);
                var completedEvent = JsonSerializer.Deserialize<SearchCompletedEvent>(
                    body,
                    EventSerialization.Options);

                // Other searches may complete while this one is being watched; the fanout
                // delivers every one of them here, so anything else is skipped rather than
                // failed on.
                if (completedEvent is not null && completedEvent.SearchId == searchId)
                {
                    return new ObservedEvent(completedEvent, json);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No search completed event for {searchId} arrived on the '{MessagingConstants.SearchCompletedExchange}' "
                + $"exchange within {timeout.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s. "
                + $"Messages observed on the bound queue: {seen.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    /// <summary>
    /// Closes the channel and the connection.
    /// </summary>
    /// <returns>A task that completes once both have been released.</returns>
    public async ValueTask DisposeAsync()
    {
        _deliveries.Writer.TryComplete();

        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// An event as it was observed on the broker.
    /// </summary>
    /// <param name="Event">The deserialized event.</param>
    /// <param name="Json">The exact payload that crossed the broker.</param>
    public sealed record ObservedEvent(SearchCompletedEvent Event, string Json);
}
