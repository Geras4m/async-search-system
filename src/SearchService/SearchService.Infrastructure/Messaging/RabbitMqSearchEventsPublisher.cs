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

    /// <summary>
    /// Ceiling on a single publish, covering the wait for the publish gate, opening and
    /// declaring a channel, and the broker's confirmation.
    /// </summary>
    /// <remarks>
    /// A healthy publish is a single round trip. This bound exists for the unhealthy case:
    /// it stops one publish against an unreachable broker from pinning a process wide gate
    /// and a search execution slot for the duration of the connection retry ladder.
    /// </remarks>
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Grace period for closing a channel that is being discarded after a failure.
    /// </summary>
    /// <remarks>
    /// Deliberately short. This runs while the publish gate is held, so a channel close that
    /// hangs against an unresponsive broker would defeat <see cref="PublishTimeout"/>.
    /// </remarks>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

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

        // Everything below runs under a bounded deadline. This is not belt and braces: the
        // publish gate is process wide, and opening a channel can walk the connection
        // provider's whole retry ladder when the broker is unreachable. Without a ceiling a
        // single unlucky publish would hold both this gate and the execution engine's
        // concurrency slot for the length of that ladder, and enough of them would stall the
        // engine. A few seconds is far longer than a healthy publish needs.
        using CancellationTokenSource publishTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        publishTimeout.CancelAfter(PublishTimeout);

        CancellationToken publishToken = publishTimeout.Token;

        // Acquiring the gate is inside the try, and tracked, for two reasons. Waiting for the
        // gate is one of the places the deadline can fire — under contention it is the most
        // likely one — so it has to be covered by the same translation and logging as the
        // publish itself, or the common failure surfaces as a bare "operation was canceled"
        // with no mention of the broker. The flag is what keeps the finally honest: when the
        // wait is the thing that failed, the semaphore was never taken and releasing it would
        // corrupt the count and let two publishers onto one channel.
        bool gateAcquired = false;

        try
        {
            await _publishGate.WaitAsync(publishToken).ConfigureAwait(false);
            gateAcquired = true;

            IChannel channel = await GetOrOpenChannelAsync(publishToken).ConfigureAwait(false);

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
                cancellationToken: publishToken).ConfigureAwait(false);

            LogEventPublished(completedEvent.SearchId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own deadline fired rather than the caller's token. Reported as a
            // TimeoutException on purpose: a bare "the operation was canceled" tells whoever
            // reads the log nothing, whereas this names the deadline and the broker as the
            // suspects, which is exactly what a broker outage looks like from in here.
            TimeoutException timeout = new(
                $"Publishing the search completed event for search '{completedEvent.SearchId}' "
                    + $"did not complete within {PublishTimeout}. The broker is unreachable or not responding.");

            LogPublishFailed(timeout, completedEvent.SearchId, MessagingConstants.SearchCompletedExchange);

            // Only touch the channel while holding the gate. A publisher that timed out while
            // still queued never owned it, and discarding it from here would race the
            // publisher that does.
            if (gateAcquired)
            {
                await DiscardChannelAsync().ConfigureAwait(false);
            }

            throw timeout;
        }
        catch (Exception ex)
        {
            LogPublishFailed(ex, completedEvent.SearchId, MessagingConstants.SearchCompletedExchange);

            // The channel may be unusable after a failed publish, so drop it and let the next
            // attempt open and re-declare a fresh one. Same ownership rule as above.
            if (gateAcquired)
            {
                await DiscardChannelAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (gateAcquired)
            {
                _publishGate.Release();
            }
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

        // Publisher confirms are opt-in and default to off. Without them BasicPublishAsync
        // completes as soon as the frame reaches the socket, so a broker that never accepts
        // the message would still be reported as a successful publish. With confirms and
        // confirmation tracking enabled the publish awaits the broker's ack and surfaces a
        // nack as a PublishException, which the caller already logs and rethrows.
        CreateChannelOptions channelOptions = new(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);

        IChannel channel = await connection
            .CreateChannelAsync(channelOptions, cancellationToken)
            .ConfigureAwait(false);

        // From here the channel exists on the broker but is not yet reachable through
        // _channel, so nothing else can clean it up. The declare below is cancellable and, now
        // that publishing runs under a deadline, that cancellation is an ordinary occurrence
        // rather than a shutdown-only one. Letting the exception escape would abandon an open
        // channel on the shared connection with no reference left to close it, and enough of
        // those would exhaust the connection's channel budget and break publishing for good.
        try
        {
            await channel.ExchangeDeclareAsync(
                exchange: MessagingConstants.SearchCompletedExchange,
                type: MessagingConstants.SearchCompletedExchangeType,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeChannelAsync(channel).ConfigureAwait(false);

            throw;
        }

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

        await DisposeChannelAsync(channel).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes one channel, never blocking the caller for longer than <see cref="CloseTimeout"/>.
    /// </summary>
    /// <param name="channel">The channel to close.</param>
    /// <returns>A task that completes once the channel has been closed or given up on.</returns>
    /// <remarks>
    /// Closing a channel is an AMQP round trip, so against a broker that has stopped answering
    /// it can hang for as long as the connection takes to fault. That matters here because
    /// this runs on the failure path of a publish, while the publish gate is still held: an
    /// unbounded close would make the publish deadline a fiction and reintroduce exactly the
    /// stall the deadline exists to prevent. The close is therefore abandoned after a short
    /// grace period. Dropping the reference is enough — the connection owns the channel and
    /// tears it down when it is itself disposed or faults.
    /// </remarks>
    private async ValueTask DisposeChannelAsync(IChannel channel)
    {
        try
        {
            await channel.DisposeAsync().AsTask().WaitAsync(CloseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogChannelCloseTimedOut(CloseTimeout);
        }
        catch (Exception ex)
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

    /// <summary>Records a channel close abandoned because it exceeded its grace period.</summary>
    /// <param name="closeTimeout">The grace period that elapsed.</param>
    [LoggerMessage(
        EventId = 4004,
        Level = LogLevel.Warning,
        Message = "Closing the RabbitMQ publishing channel exceeded {CloseTimeout} and was abandoned.")]
    private partial void LogChannelCloseTimedOut(TimeSpan closeTimeout);
}
