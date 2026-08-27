using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IntegrationTests.Fixtures;
using RabbitMQ.Client;
using Shared.Common;
using Shared.EventContracts;
using Shouldly;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Proves the guarantee the transactional outbox exists to provide: a search that completes while
/// the broker is down still gets its completion event delivered once the broker comes back.
/// </summary>
/// <remarks>
/// <para>
/// This is the one hole a mock cannot show. Before the outbox, the completion was persisted and
/// the publish then failed, and nothing anywhere remembered that the announcement was still owed:
/// the search was complete, the Notification Service never heard, and no retry existed. Only a
/// real broker that really stops answering can demonstrate that the event is now merely late
/// instead of lost.
/// </para>
/// <para>
/// The second test is the other half of the same claim. At-least-once delivery is worthless if it
/// is really at-least-twice, so the healthy path is pinned to exactly one delivery: the inline
/// publish removes the outbox entry, and the sweep that follows must find nothing to re-send.
/// </para>
/// </remarks>
/// <param name="broker">The suite's broker.</param>
[Collection(AsyncSearchSystemSuite.Name)]
public sealed class SearchEventOutboxRecoveryTests(RabbitMqFixture broker)
{
    /// <summary>Ceiling for a single test, so a hung system fails instead of hanging the run.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Budget for a search to finish. A compressed search produces its six batches in about 1.2
    /// seconds; the rest is slack for a machine under load.
    /// </summary>
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Budget for the delayed delivery once the broker is back. Deliberately generous: it has to
    /// cover the broker finishing its start-up, the publisher's own connection retry ladder and
    /// however many sweeps that takes, and the point of the assertion is that the event arrives at
    /// all, not that it arrives quickly.
    /// </summary>
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Budget for the ordinary, broker-is-healthy delivery.</summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the queue is watched after the first delivery before concluding there is no second
    /// one. Six sweeps at the configured poll interval, so a publisher that re-sent what it had
    /// already delivered would have had ample opportunity to do so.
    /// </summary>
    private static readonly TimeSpan DuplicateWatchWindow = TimeSpan.FromSeconds(3);

    /// <summary>Gap between drains of the retained queue.</summary>
    private static readonly TimeSpan QueuePollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Sweep interval the outbox publisher runs at during these tests, in place of the five second
    /// default, so recovery is observed in about a second rather than in five.
    /// </summary>
    /// <remarks>
    /// Delivered as an environment variable because it is read through the standard configuration
    /// pipeline: <c>Outbox</c> is the section <c>SearchEventOutboxOptions</c> binds, and the double
    /// underscore is the section separator. The shared host factory exposes no seam for extra
    /// settings, and it is used by every other test class, so widening it for this one would be the
    /// larger change.
    /// </remarks>
    private const string OutboxPollIntervalVariable = "Outbox__PollInterval";

    /// <summary>Value written to <see cref="OutboxPollIntervalVariable"/>.</summary>
    private const string OutboxPollInterval = "00:00:00.500";

    [DockerFact]
    public async Task CompletionEvent_SurvivesABrokerOutage_AndIsDeliveredOnceTheBrokerReturns()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);

        var endpoint = broker.RequireEndpoint();

        // Bound before anything else happens, and deliberately durable rather than the exclusive
        // auto-deleted queue the other tests use. Two reasons, and both are the ordering problem
        // this test would otherwise have. A queue bound only after the broker returns would race
        // the retry sweep and could miss the very message it exists to observe, so the binding has
        // to pre-date the outage. And an exclusive queue is owned by the connection that declared
        // it, which the outage kills, taking the queue and its binding with it; a durable,
        // non-exclusive queue outlives both the connection and the broker application, so the
        // re-published event is retained until this test gets around to reading it.
        await using var retained = await RetainedCompletionQueue.DeclareAsync(endpoint, cancellation.Token);

        using var outboxSweep = EnvironmentVariableScope.Set(OutboxPollIntervalVariable, OutboxPollInterval);

        await using var system = new AsyncSearchSystemFactory(endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        var brokerIsDown = false;

        try
        {
            await broker.StopBrokerAsync(cancellation.Token);
            brokerIsDown = true;

            // Guards the whole test against being vacuous. If taking the broker down quietly did
            // nothing, everything below would pass while proving nothing at all.
            (await CanConnectAsync(endpoint, cancellation.Token)).ShouldBeFalse(
                $"the broker at {endpoint.Describe()} must really be down for this test to mean anything");

            var searchId = await api.StartSearchAsync("Paris", cancellation.Token);

            // Completion must not depend on the broker. The search state is persisted before the
            // event is published, and the publish failure is a deferral rather than a failure, so
            // a client polling through the gateway sees a finished search with every result even
            // though nothing can be announced yet.
            var state = await Wait.UntilAsync(
                token => api.GetSearchStateAsync(searchId, token),
                observed => observed.IsCompleted,
                $"search {searchId} to complete while the broker is down",
                cancellation.Token,
                CompletionTimeout);

            state.SearchId.ShouldBe(searchId);
            state.IsCompleted.ShouldBeTrue();
            state.Results.Count.ShouldBe(
                AsyncSearchSystemFactory.ExpectedResultCount,
                "a broker outage must not cost the client any results");

            await broker.StartBrokerAsync(cancellation.Token);
            brokerIsDown = false;

            // The event was owed, not lost. Nothing re-issues the command and nothing re-completes
            // the search, so the only thing that can put this message on the exchange now is the
            // outbox sweep retrying the delivery the inline publish could not make.
            var delivered = await retained.WaitForAsync(searchId, RecoveryTimeout, cancellation.Token);

            delivered.SearchId.ShouldBe(searchId);
            delivered.CompletedAtUtc.ShouldBeLessThanOrEqualTo(DateTime.UtcNow.AddMinutes(1));
        }
        finally
        {
            // The container is shared by the whole suite, so the broker goes back up even when the
            // assertions above failed. Without this, one failure here would fail every test after
            // it for an entirely unrelated reason.
            if (brokerIsDown)
            {
                await broker.StartBrokerAsync(CancellationToken.None);
            }
        }
    }

    [DockerFact]
    public async Task CompletionEvent_IsDeliveredExactlyOnce_WhenTheBrokerStaysHealthy()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);

        var endpoint = broker.RequireEndpoint();

        await using var retained = await RetainedCompletionQueue.DeclareAsync(endpoint, cancellation.Token);

        // The same brisk sweep as the outage test, on purpose: the risk being ruled out here is
        // the outbox re-sending an event the inline publish already delivered, and a sweep that
        // rarely runs would hide exactly that.
        using var outboxSweep = EnvironmentVariableScope.Set(OutboxPollIntervalVariable, OutboxPollInterval);

        await using var system = new AsyncSearchSystemFactory(endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        var searchId = await api.StartSearchAsync("Paris", cancellation.Token);

        await retained.WaitForAsync(searchId, EventTimeout, cancellation.Token);

        // At-least-once is only a guarantee worth having if the common case is exactly once. The
        // inline publish removes the outbox entry, so every sweep in this window must find the
        // outbox empty and send nothing.
        var delivered = await retained.KeepDrainingAsync(searchId, DuplicateWatchWindow, cancellation.Token);

        delivered.ShouldBe(
            1,
            $"a healthy broker must receive exactly one completion event for search {searchId}");
    }

    /// <summary>
    /// Reports whether the broker accepts an AMQP connection right now.
    /// </summary>
    /// <param name="endpoint">Broker to try.</param>
    /// <param name="cancellationToken">Token that abandons the attempt.</param>
    /// <returns><see langword="true"/> when a connection was opened, otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Used only to confirm that an outage is real. A refusal is the expected answer rather than an
    /// error, so it is reported as a value; a cancelled attempt is the test's own deadline and is
    /// left to propagate.
    /// </remarks>
    private static async Task<bool> CanConnectAsync(BrokerEndpoint endpoint, CancellationToken cancellationToken)
    {
        var connectionFactory = new ConnectionFactory
        {
            HostName = endpoint.Host,
            Port = endpoint.Port,
            UserName = endpoint.UserName,
            Password = endpoint.Password,
            VirtualHost = "/",
            ClientProvidedName = "outbox-recovery-tests-liveness",
            RequestedConnectionTimeout = TimeSpan.FromSeconds(2),
        };

        try
        {
            await using var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sets an environment variable for the duration of a test and puts back whatever was there
    /// before.
    /// </summary>
    /// <remarks>
    /// Every integration test class shares one xUnit collection and therefore runs sequentially in
    /// one process, so a variable scoped this way cannot leak into a test running beside it. The
    /// restore still matters for the tests that run after.
    /// </remarks>
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;

        private EnvironmentVariableScope(string name, string? previousValue)
        {
            _name = name;
            _previousValue = previousValue;
        }

        /// <summary>
        /// Sets the variable, capturing the value to restore.
        /// </summary>
        /// <param name="name">Name of the variable.</param>
        /// <param name="value">Value to set for the duration of the scope.</param>
        /// <returns>A scope that restores the previous value when disposed.</returns>
        public static EnvironmentVariableScope Set(string name, string value)
        {
            var previousValue = Environment.GetEnvironmentVariable(name);

            Environment.SetEnvironmentVariable(name, value);

            return new EnvironmentVariableScope(name, previousValue);
        }

        /// <summary>Restores the previous value.</summary>
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previousValue);
    }

    /// <summary>
    /// A durable queue of this test class's own, bound to the completion exchange, that holds on to
    /// events across a broker outage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not <c>BrokerProbe</c>. That probe binds an exclusive, auto-deleted queue and
    /// keeps a live consumer on it, which is the right shape for watching a healthy broker and the
    /// wrong shape here: the outage closes the connection that owns the queue, so the queue, the
    /// binding and any consumer would all be gone by the time the retry arrives.
    /// </para>
    /// <para>
    /// The queue is durable, non-exclusive and never auto-deleted, so it belongs to the broker
    /// rather than to a connection and survives the broker application being stopped and started.
    /// The name carries a fresh identifier per test, so a queue that outlives a failed run cannot
    /// feed stale messages to the next one, and the queue is deleted on the way out.
    /// </para>
    /// </remarks>
    private sealed class RetainedCompletionQueue : IAsyncDisposable
    {
        private readonly BrokerEndpoint _endpoint;
        private readonly string _queueName;
        private readonly List<SearchCompletedEvent> _received = [];

        private IConnection? _connection;
        private IChannel? _channel;

        private RetainedCompletionQueue(BrokerEndpoint endpoint, string queueName)
        {
            _endpoint = endpoint;
            _queueName = queueName;
        }

        /// <summary>
        /// Declares the exchange, the queue and the binding, and connects.
        /// </summary>
        /// <param name="endpoint">Broker to bind on.</param>
        /// <param name="cancellationToken">Token that abandons the operation.</param>
        /// <returns>A queue that is already bound and already retaining.</returns>
        public static async Task<RetainedCompletionQueue> DeclareAsync(
            BrokerEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            var queue = new RetainedCompletionQueue(endpoint, $"outbox-recovery-tests.{Guid.NewGuid():N}");

            try
            {
                await queue.EnsureBoundAsync(cancellationToken);

                return queue;
            }
            catch
            {
                await queue.DisposeAsync();

                throw;
            }
        }

        /// <summary>
        /// Waits for a completion event carrying the given identifier to reach the queue.
        /// </summary>
        /// <param name="searchId">Identifier the awaited event must carry.</param>
        /// <param name="timeout">Budget for the wait.</param>
        /// <param name="cancellationToken">Token that abandons the wait.</param>
        /// <returns>The first matching event.</returns>
        /// <exception cref="TimeoutException">No matching event arrived within the budget.</exception>
        public async Task<SearchCompletedEvent> WaitForAsync(
            Guid searchId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            await Wait.UntilAsync(
                token => DrainAsync(searchId, token),
                drained => drained.Matching > 0,
                $"the completion event for search {searchId} to reach the retained queue '{_queueName}'",
                cancellationToken,
                timeout,
                QueuePollInterval);

            return _received.First(observed => observed.SearchId == searchId);
        }

        /// <summary>
        /// Keeps draining the queue for a fixed window and reports how many events for the given
        /// search were seen in total.
        /// </summary>
        /// <param name="searchId">Identifier to count.</param>
        /// <param name="window">How long to keep watching.</param>
        /// <param name="cancellationToken">Token that abandons the wait.</param>
        /// <returns>The number of matching events seen since the queue was declared.</returns>
        /// <remarks>
        /// Waiting out a window is unavoidable when the expectation is that nothing more happens:
        /// there is no state to poll for the absence of a message, only time in which it did not
        /// arrive. The queue is still drained throughout rather than slept through, so a duplicate
        /// is captured the moment it appears.
        /// </remarks>
        public async Task<int> KeepDrainingAsync(Guid searchId, TimeSpan window, CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();

            var drained = await DrainAsync(searchId, cancellationToken);

            while (elapsed.Elapsed < window)
            {
                await Task.Delay(QueuePollInterval, cancellationToken);

                drained = await DrainAsync(searchId, cancellationToken);
            }

            return drained.Matching;
        }

        /// <summary>
        /// Deletes the queue and closes the connection.
        /// </summary>
        /// <returns>A task that completes once everything has been released.</returns>
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_channel is { IsOpen: true })
                {
                    await _channel.QueueDeleteAsync(_queueName);
                }
            }
            catch (Exception)
            {
                // Best effort. The name carries a fresh identifier per test, so a queue left
                // behind on a broker that is already unhealthy cannot affect anything else, and
                // failing the test on a cleanup error would hide whatever really went wrong.
            }

            await CloseAsync();
        }

        /// <summary>
        /// Takes every message currently on the queue and counts the ones for a given search.
        /// </summary>
        /// <param name="searchId">Identifier to count.</param>
        /// <param name="cancellationToken">Token that abandons the operation.</param>
        /// <returns>What this drain, and every drain before it, has seen.</returns>
        /// <remarks>
        /// A broker that is still refusing connections is reported rather than thrown, because a
        /// drain that runs a moment too early is an ordinary part of waiting for recovery. The
        /// reason is carried into the observed value so that a wait which does time out says why.
        /// </remarks>
        private async Task<DrainResult> DrainAsync(Guid searchId, CancellationToken cancellationToken)
        {
            try
            {
                var channel = await EnsureBoundAsync(cancellationToken);

                while (true)
                {
                    var delivery = await channel.BasicGetAsync(_queueName, autoAck: true, cancellationToken);

                    if (delivery is null)
                    {
                        break;
                    }

                    var observed = JsonSerializer.Deserialize<SearchCompletedEvent>(
                        delivery.Body.Span,
                        EventSerialization.Options);

                    if (observed is not null)
                    {
                        _received.Add(observed);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await CloseAsync();

                return new DrainResult(
                    CountFor(searchId),
                    _received.Count,
                    $"the queue could not be read: {exception.GetType().Name}: {exception.Message}");
            }

            return new DrainResult(CountFor(searchId), _received.Count, "the queue was read successfully");
        }

        /// <summary>
        /// Returns an open channel with the exchange, the queue and the binding declared on it,
        /// reconnecting if the previous connection did not survive.
        /// </summary>
        /// <param name="cancellationToken">Token that abandons the operation.</param>
        /// <returns>An open channel.</returns>
        /// <remarks>
        /// Every declaration is idempotent and matches what the Search Service and the Notification
        /// Service declare, so re-running them after a reconnect changes nothing on the broker. The
        /// queue survives the outage on its own; re-declaring is how this side finds its way back
        /// to it without having to know whether it did.
        /// </remarks>
        private async Task<IChannel> EnsureBoundAsync(CancellationToken cancellationToken)
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            await CloseAsync();

            var connectionFactory = new ConnectionFactory
            {
                HostName = _endpoint.Host,
                Port = _endpoint.Port,
                UserName = _endpoint.UserName,
                Password = _endpoint.Password,
                VirtualHost = "/",
                ClientProvidedName = "outbox-recovery-tests",
            };

            _connection = await connectionFactory.CreateConnectionAsync(cancellationToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

            await _channel.ExchangeDeclareAsync(
                exchange: MessagingConstants.SearchCompletedExchange,
                type: MessagingConstants.SearchCompletedExchangeType,
                durable: true,
                autoDelete: false,
                cancellationToken: cancellationToken);

            await _channel.QueueDeclareAsync(
                queue: _queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                cancellationToken: cancellationToken);

            await _channel.QueueBindAsync(
                queue: _queueName,
                exchange: MessagingConstants.SearchCompletedExchange,
                routingKey: string.Empty,
                cancellationToken: cancellationToken);

            return _channel;
        }

        /// <summary>
        /// Counts the events seen so far for one search.
        /// </summary>
        /// <param name="searchId">Identifier to count.</param>
        /// <returns>How many matching events have been taken off the queue.</returns>
        private int CountFor(Guid searchId) => _received.Count(observed => observed.SearchId == searchId);

        /// <summary>
        /// Closes the channel and the connection, ignoring failures.
        /// </summary>
        /// <returns>A task that completes once both have been released.</returns>
        private async Task CloseAsync()
        {
            var channel = _channel;
            var connection = _connection;

            _channel = null;
            _connection = null;

            try
            {
                if (channel is not null)
                {
                    await channel.DisposeAsync();
                }

                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
            }
            catch (Exception)
            {
                // A connection the broker has already dropped usually faults on close, and this
                // runs precisely when that is expected. Both references are gone either way.
            }
        }

        /// <summary>
        /// The outcome of one drain, phrased so that a timed-out wait explains itself.
        /// </summary>
        /// <param name="Matching">Events seen so far for the search being waited on.</param>
        /// <param name="Total">Events seen so far for any search.</param>
        /// <param name="Note">What happened on this drain.</param>
        private sealed record DrainResult(int Matching, int Total, string Note)
        {
            /// <summary>Renders the result for a failure message.</summary>
            /// <returns>A one-line description of what the queue has produced so far.</returns>
            public override string ToString() => string.Create(
                CultureInfo.InvariantCulture,
                $"{Matching} matching and {Total} total event(s) so far, {Note}");
        }
    }
}
