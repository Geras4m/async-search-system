using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using SearchService.Application.Abstractions;
using Shared.Common;
using Shared.EventContracts;

namespace SearchService.Infrastructure.Messaging;

/// <summary>
/// Publishes <see cref="SearchCompletedEvent"/> messages to the RabbitMQ fanout exchange
/// the Notification Service binds its queue to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The exchange is declared on publish, not at start-up.</b> Exchange declaration is
/// idempotent, so declaring it here makes the boot order of the services irrelevant: the
/// Search Service can publish before the Notification Service has ever run, and the
/// Notification Service can declare and bind the same topology from its own side.
/// </para>
/// <para>
/// <b>One channel, reused, guarded by a semaphore.</b> An AMQP channel is a cheap multiplexed
/// session but is explicitly not thread-safe, so concurrent publishes have to be serialised
/// somehow. The two options are a channel per publish or a shared channel behind a gate, and
/// this implementation takes the shared channel: opening a channel costs a broker round trip
/// and re-declaring the exchange costs another, which would double the network cost of every
/// event, while completion events are low volume and perfectly happy to be serialised. A
/// <see cref="SemaphoreSlim"/> rather than a <see langword="lock"/> because the publish path
/// is asynchronous. If a publish fails, the channel is discarded and the next publish
/// transparently opens and re-declares a fresh one, which is what makes a broker restart
/// self-healing.
/// </para>
/// <para>
/// Messages are marked persistent and the exchange is declared durable, so an event the
/// broker has already accepted survives a broker restart.
/// </para>
/// </remarks>
/// <param name="connectionProvider">Supplies the shared broker connection.</param>
/// <param name="logger">Logger for publish outcomes.</param>
/// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
public sealed partial class RabbitMqSearchEventsPublisher(
    IRabbitMqConnectionProvider connectionProvider,
    ILogger<RabbitMqSearchEventsPublisher> logger) : ISearchEventsPublisher, IAsyncDisposable
{
    private readonly IRabbitMqConnectionProvider _connectionProvider =
        connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));

    private readonly ILogger<RabbitMqSearchEventsPublisher> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly SemaphoreSlim _publishGate = new(1, 1);

    private IChannel? _channel;
    private bool _disposed;

    /// <summary>
    /// Publishes a <see cref="SearchCompletedEvent"/> to the message broker.
    /// </summary>
    /// <param name="completedEvent">The event to publish.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes once the broker has accepted the message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="completedEvent"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The publisher has already been disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    /// <remarks>
    /// The payload is serialised with the shared <see cref="EventSerialization.Options"/>,
    /// the same instance the Notification Service deserialises with, so the two sides of the
    /// contract cannot drift apart. Failures are logged and rethrown rather than swallowed:
    /// the caller decides whether a lost notification should fail the operation.
    /// </remarks>
    public async Task PublishSearchCompletedAsync(
        SearchCompletedEvent completedEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedEvent);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            IChannel channel = await GetOrOpenChannelAsync(cancellationToken).ConfigureAwait(false);

            byte[] body = JsonSerializer.SerializeToUtf8Bytes(completedEvent, EventSerialization.Options);

            BasicProperties properties = new()
            {
                Persistent = true,
                ContentType = MessagingConstants.EventContentType,
                MessageId = completedEvent.SearchId.ToString("D", CultureInfo.InvariantCulture),
                Timestamp = ToAmqpTimestamp(completedEvent.CompletedAtUtc),
            };

            await channel.BasicPublishAsync(
                exchange: MessagingConstants.SearchCompletedExchange,
                routingKey: string.Empty,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            LogEventPublished(completedEvent.SearchId);
        }
        catch (Exception ex)
        {
            LogPublishFailed(ex, completedEvent.SearchId, MessagingConstants.SearchCompletedExchange);

            // The channel may be unusable after a failed publish, so drop it and let the next
            // attempt open and re-declare a fresh one.
            await DiscardChannelAsync().ConfigureAwait(false);

            throw;
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <summary>
    /// Closes the publishing channel and releases the resources owned by the publisher.
    /// </summary>
    /// <returns>A task that completes once the channel has been closed.</returns>
    /// <remarks>
    /// The shared connection is not closed here: it belongs to
    /// <see cref="IRabbitMqConnectionProvider"/>, which the container disposes separately.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await DiscardChannelAsync().ConfigureAwait(false);

        _publishGate.Dispose();
    }

    /// <summary>
    /// Converts a UTC instant to the seconds-since-epoch representation AMQP uses.
    /// </summary>
    /// <param name="utcInstant">The instant to convert.</param>
    /// <returns>The equivalent AMQP timestamp.</returns>
    private static AmqpTimestamp ToAmqpTimestamp(DateTime utcInstant)
    {
        DateTime utc = DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc);

        return new AmqpTimestamp(new DateTimeOffset(utc).ToUnixTimeSeconds());
    }

    /// <summary>
    /// Returns the shared publishing channel, opening it and declaring the exchange on first
    /// use or after a failure.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>An open channel with the completion exchange declared on it.</returns>
    /// <remarks>Callers must hold the publish gate.</remarks>
    private async Task<IChannel> GetOrOpenChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await DiscardChannelAsync().ConfigureAwait(false);

        IConnection connection = await _connectionProvider.GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        IChannel channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            exchange: MessagingConstants.SearchCompletedExchange,
            type: MessagingConstants.SearchCompletedExchangeType,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        LogExchangeDeclared(
            MessagingConstants.SearchCompletedExchange,
            MessagingConstants.SearchCompletedExchangeType);

        _channel = channel;

        return channel;
    }

    /// <summary>
    /// Closes and forgets the current channel, if there is one.
    /// </summary>
    /// <returns>A task that completes once the channel has been released.</returns>
    private async ValueTask DiscardChannelAsync()
    {
        IChannel? channel = _channel;

        if (channel is null)
        {
            return;
        }

        _channel = null;

        try
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Closing a channel whose connection has already gone typically faults. The
            // channel is being discarded either way, so this must not mask the real error.
            LogChannelCloseFailed(ex);
        }
    }

    [LoggerMessage(
        EventId = 4000,
        Level = LogLevel.Information,
        Message = "Event published. SearchId={SearchId}")]
    private partial void LogEventPublished(Guid searchId);

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Error,
        Message = "Failed to publish the search completed event. SearchId={SearchId} Exchange={Exchange}")]
    private partial void LogPublishFailed(Exception exception, Guid searchId, string exchange);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Information,
        Message = "Search completed exchange declared. Exchange={Exchange} ExchangeType={ExchangeType}")]
    private partial void LogExchangeDeclared(string exchange, string exchangeType);

    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Debug,
        Message = "Ignored a failure while closing a RabbitMQ channel.")]
    private partial void LogChannelCloseFailed(Exception exception);
}
