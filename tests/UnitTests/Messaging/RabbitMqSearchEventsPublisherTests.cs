using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RabbitMQ.Client;
using SearchService.Infrastructure.Messaging;
using Shared.Common;
using Shared.EventContracts;
using Shouldly;
using Xunit;

namespace UnitTests.Messaging;

/// <summary>
/// The publisher is the seam between a completed search and everything downstream, and the only
/// place in the Search Service that talks AMQP. What matters here is not that a method was called
/// but that the message put on the wire is the one the Notification Service can read, that the
/// broker is actually asked to confirm it, and that every failure path leaves the publisher
/// usable: no leaked channels, no leaked semaphore, and a diagnosis that names the broker instead
/// of a bare cancellation.
/// </summary>
public sealed class RabbitMqSearchEventsPublisherTests
{
    /// <summary>
    /// The wire contract is asserted with literals rather than with the shared constants on
    /// purpose. Comparing a constant against itself would still pass if someone renamed the
    /// exchange, and neither the Notification Service nor the deployed brokers would follow.
    /// </summary>
    private const string ExpectedExchange = "search.completed";

    private const string ExpectedExchangeType = "fanout";

    private const string ExpectedContentType = "application/json";

    private static readonly DateTime CompletedAtUtc = new(2026, 3, 14, 9, 27, 23, DateTimeKind.Utc);

    /// <summary>
    /// Ceiling applied by the tests themselves, so that a regression which parks a publish on a
    /// held gate fails the run instead of hanging it.
    /// </summary>
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PublishSearchCompletedAsync_OnAHealthyBroker_PutsTheSerialisedEventOnTheFanoutExchange()
    {
        // Arrange
        IChannel channel = GivenAnOpenChannel();
        List<PublishedMessage> published = CapturePublishes(channel);
        (IRabbitMqConnectionProvider provider, _, _) = GivenABrokerServing(channel);

        SearchCompletedEvent completed = new(Guid.NewGuid(), CompletedAtUtc);

        await using RabbitMqSearchEventsPublisher publisher = CreatePublisher(provider);

        // Act
        await publisher.PublishSearchCompletedAsync(completed, CancellationToken.None);

        // Assert
        PublishedMessage message = published.ShouldHaveSingleItem();

        message.Exchange.ShouldBe(ExpectedExchange);

        // Fanout routes by binding, not by key. A non-empty key here would be silently ignored
        // today and would quietly start to matter the day the exchange type changes.
        message.RoutingKey.ShouldBeEmpty();
        message.Mandatory.ShouldBeFalse();

        message.Properties.Persistent.ShouldBeTrue(
            "an event the broker has accepted must survive a broker restart");
        message.Properties.ContentType.ShouldBe(ExpectedContentType);
        message.Properties.MessageId.ShouldBe(Format(completed.SearchId));
        message.Properties.Timestamp.UnixTime.ShouldBe(new DateTimeOffset(CompletedAtUtc).ToUnixTimeSeconds());

        // The bytes are the contract. Reading them back with the same options the consumer uses is
        // the only assertion that would catch a naming-policy or casing drift.
        Encoding.UTF8.GetString(message.Body).ShouldContain("\"searchId\"");

        SearchCompletedEvent? roundTripped =
            JsonSerializer.Deserialize<SearchCompletedEvent>(message.Body, EventSerialization.Options);

        roundTripped.ShouldBe(completed);
    }

    [Fact]
    public async Task PublishSearchCompletedAsync_WhenOpeningTheChannel_TurnsOnPublisherConfirmationsAndTracking()
    {
        // Arrange
        IChannel channel = GivenAnOpenChannel();
        CapturePublishes(channel);
        (IRabbitMqConnectionProvider provider, _, List<CreateChannelOptions?> channelOptions) =
            GivenABrokerServing(channel);

        await using RabbitMqSearchEventsPublisher publisher = CreatePublisher(provider);

        // Act
        await publisher.PublishSearchCompletedAsync(NewEvent(), CancellationToken.None);

        // Assert
        CreateChannelOptions? options = channelOptions.ShouldHaveSingleItem();

        options.ShouldNotBeNull(
            "leaving the options null takes the broker defaults, which have confirms off");

        // Confirms are opt-in and default to off, and with them off a publish completes as soon as
        // the frame reaches the socket. A broker that never accepts the message would then be
        // reported as a successful publish, and the outbox would drop the entry for an event that
        // nobody ever received.
        options!.PublisherConfirmationsEnabled.ShouldBeTrue();
        options.PublisherConfirmationTrackingEnabled.ShouldBeTrue();
    }

    [Fact]
    [SuppressMessage(
        "Usage",
        "CA2012:Use ValueTasks correctly",
        Justification = "Inside Received.InOrder the call is a specification, not an invocation: "
            + "the substitute records the expected call and returns a default ValueTask that is "
            + "never connected to any work.")]
    public async Task PublishSearchCompletedAsync_BeforeTheFirstPublish_DeclaresTheDurableFanoutExchange()
    {
        // Arrange
        IChannel channel = GivenAnOpenChannel();
        CapturePublishes(channel);
        (IRabbitMqConnectionProvider provider, _, _) = GivenABrokerServing(channel);

        await using RabbitMqSearchEventsPublisher publisher = CreatePublisher(provider);

        // Act
        await publisher.PublishSearchCompletedAsync(NewEvent(), CancellationToken.None);

        // Assert
        // Every boolean is pinned with Arg.Is rather than a bare literal: NSubstitute needs a
        // specification for every argument of a type once one of them uses a matcher, and a
        // durable exchange is exactly the argument a regression would flip.
        await channel.Received(1).ExchangeDeclareAsync(
            ExpectedExchange,
            ExpectedExchangeType,
            Arg.Is(true),
            Arg.Is(false),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Is(false),
            Arg.Is(false),
            Arg.Any<CancellationToken>());

        // Order is the point: declaring after the publish would make the boot order of the two
        // services matter again, which is the whole reason the declare lives on this path.
        Received.InOrder(() =>
        {
            channel.ExchangeDeclareAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<bool>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());

            channel.BasicPublishAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task PublishSearchCompletedAsync_CalledTwiceInARow_OpensAndDeclaresOnlyOneChannel()
    {
        // Arrange
        IChannel channel = GivenAnOpenChannel();
        List<PublishedMessage> published = CapturePublishes(channel);
        (IRabbitMqConnectionProvider provider, IConnection connection, _) = GivenABrokerServing(channel);

        await using RabbitMqSearchEventsPublisher publisher = CreatePublisher(provider);

        // Act
        await publisher.PublishSearchCompletedAsync(NewEvent(), CancellationToken.None);
        await publisher.PublishSearchCompletedAsync(NewEvent(), CancellationToken.None);

        // Assert
        published.Count.ShouldBe(2);

        // Opening a channel and re-declaring the exchange are a broker round trip each, so a
        // channel per publish would triple the network cost of every completion event.
        await connection.Received(1).CreateChannelAsync(
            Arg.Any<CreateChannelOptions>(),
            Arg.Any<CancellationToken>());

        await channel.Received(1).ExchangeDeclareAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Pins the publish deadline and, more importantly, the diagnosis it produces.
    /// </summary>
    /// <returns>A task that completes when the assertion has run.</returns>
    /// <remarks>
    /// This test really does wait for the publisher deadline, which is ten seconds, because that
    /// deadline is a private constant with no injection seam and inventing one purely for a test
    /// would be a worse trade than ten seconds of wall clock. Nothing else in the suite is slow,
    /// and the property earns it: without the deadline a single publish against an unreachable
    /// broker holds the process-wide publish gate for the length of the connection retry ladder.
    /// </remarks>
    [Fact]
    public async Task PublishSearchCompletedAsync_WhenTheBrokerNeverAnswers_ReportsATimeoutNamingTheSearch()
    {
        // Arrange
        IRabbitMqConnectionProvider provider = Substitute.For<IRabbitMqConnectionProvider>();

        provider
            .GetConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                // Stands in for the connection provider walking its retry ladder against a broker
                // that is simply not there: it never answers and never fails, it only waits.
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());

                return Substitute.For<IConnection>();
            });

        SearchCompletedEvent completed = NewEvent();

        await using RabbitMqSearchEventsPublisher publisher = CreatePublisher(provider);

        // Act
        Exception? thrown = await CapturePublishFailureAsync(
            () => publisher.PublishSearchCompletedAsync(completed, CancellationToken.None));

        // Assert
        // A bare "the operation was canceled" would send whoever reads the log looking for a
        // cancelled request. The translation is what points them at the broker instead.
        TimeoutException timeout = thrown.ShouldBeOfType<TimeoutException>();

        timeout.Message.ShouldContain(Format(completed.SearchId));
    }

    [Fact]
    public async Task PublishSearchCompletedAsync_WithACallerTokenAlreadyCancelled_SurfacesCancellationNotATimeout()
    {
        // Arrange
        IChannel channel = GivenAnOpenChannel();
        CapturePublishes(channel);
        (IRabbitMqConnectionProvider provider, _, _) = GivenABrokerServing(channel);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await using RabbitMqSearchEventsPublisher publisher = CreatePublisher(provider);

        // Act
        Exception? thrown = await CapturePublishFailureAsync(
            () => publisher.PublishSearchCompletedAsync(NewEvent(), cancelled.Token));

        // Assert
        thrown.ShouldNotBeNull();

        // The caller pulled the plug, so the caller gets cancellation. Dressing this up as a
        // TimeoutException would blame the broker for a shutdown or an abandoned request.
        thrown.ShouldBeAssignableTo<OperationCanceledException>();
        thrown.ShouldNotBeAssignableTo<TimeoutException>();
    }

    [Fact]
    public async Task PublishSearchCompletedAsync_WhenDeclaringTheExchangeFails_ClosesTheChannelItJustOpened()
    {
        // Arrange
        IChannel channel = GivenAnOpenChannel();
        CapturePublishes(channel);

        channel
            .ExchangeDeclareAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<bool>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("the broker refused the exchange declaration"));

        (IRabbitMqConnectionProvider provider, _, _) = GivenABrokerServing(channel);

        await using RabbitMqSearchEventsPublisher publisher = CreatePublisher(provider);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(
            () => publisher.PublishSearchCompletedAsync(NewEvent(), CancellationToken.None));

        // Assert
        // At that moment the channel exists on the broker but is not yet reachable through the
        // publisher field, so if the declare failure escaped unhandled nothing would ever close
        // it. Enough of those exhaust the channel budget of the shared connection and publishing
        // stops for good.
        await channel.Received(1).DisposeAsync();

        await channel.DidNotReceive().BasicPublishAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<BasicProperties>(),
            Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [SuppressMessage(
        "Usage",
        "CA2012:Use ValueTasks correctly",
        Justification = "The ValueTask here is the placeholder a substitute returns while a call "
            + "is being configured; it is handed straight to Throws and never awaited as work.")]
    public async Task PublishSearchCompletedAsync_AfterAFailedPublish_StillPublishesOnAFreshChannel()
    {
        // Arrange
        IChannel failing = GivenAnOpenChannel();

        failing
            .BasicPublishAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("the broker dropped the channel mid publish"));

        IChannel recovered = GivenAnOpenChannel();
        List<PublishedMessage> published = CapturePublishes(recovered);

        (IRabbitMqConnectionProvider provider, IConnection connection, _) =
            GivenABrokerServing(failing, recovered);

        SearchCompletedEvent second = NewEvent();

        await using RabbitMqSearchEventsPublisher publisher = CreatePublisher(provider);

        await Should.ThrowAsync<InvalidOperationException>(
            () => publisher.PublishSearchCompletedAsync(NewEvent(), CancellationToken.None));

        // Act
        // The ceiling here turns a semaphore that was never released into a failure rather than a
        // hung run: a corrupted gate would park this call until the publish deadline fired.
        await publisher
            .PublishSearchCompletedAsync(second, CancellationToken.None)
            .WaitAsync(TestDeadline);

        // Assert
        published.ShouldHaveSingleItem().Properties.MessageId.ShouldBe(Format(second.SearchId));

        await failing.Received(1).DisposeAsync();

        // A broker restart is meant to be self-healing: the poisoned channel is dropped and the
        // next publish transparently opens and re-declares a new one.
        await connection.Received(2).CreateChannelAsync(
            Arg.Any<CreateChannelOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishSearchCompletedAsync_AfterThePublisherIsDisposed_ThrowsObjectDisposedException()
    {
        // Arrange
        IChannel channel = GivenAnOpenChannel();
        CapturePublishes(channel);
        (IRabbitMqConnectionProvider provider, _, _) = GivenABrokerServing(channel);

        RabbitMqSearchEventsPublisher publisher = CreatePublisher(provider);

        await publisher.PublishSearchCompletedAsync(NewEvent(), CancellationToken.None);
        await publisher.DisposeAsync();

        // Act & Assert
        // The gate is gone by now, so publishing after disposal has to be rejected up front rather
        // than left to fail as an ObjectDisposedException raised from inside a semaphore.
        await Should.ThrowAsync<ObjectDisposedException>(
            () => publisher.PublishSearchCompletedAsync(NewEvent(), CancellationToken.None));

        // Disposal closes the publishing channel but leaves the shared connection alone: it
        // belongs to the provider, which the container disposes separately.
        await channel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task PublishSearchCompletedAsync_WithoutAnEvent_ThrowsArgumentNullException()
    {
        // Arrange
        IChannel channel = GivenAnOpenChannel();
        (IRabbitMqConnectionProvider provider, IConnection connection, _) = GivenABrokerServing(channel);

        await using RabbitMqSearchEventsPublisher publisher = CreatePublisher(provider);

        // Act
        ArgumentNullException thrown = await Should.ThrowAsync<ArgumentNullException>(
            () => publisher.PublishSearchCompletedAsync(null!, CancellationToken.None));

        // Assert
        thrown.ParamName.ShouldBe("completedEvent");

        // The guard runs before anything is opened, so a bad call costs no broker round trip.
        await connection.DidNotReceive().CreateChannelAsync(
            Arg.Any<CreateChannelOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Constructor_WithoutAConnectionProvider_ThrowsArgumentNullException() =>
        Should.Throw<ArgumentNullException>(
            () => new RabbitMqSearchEventsPublisher(null!, NullLogger<RabbitMqSearchEventsPublisher>.Instance));

    [Fact]
    public void Constructor_WithoutALogger_ThrowsArgumentNullException() =>
        Should.Throw<ArgumentNullException>(
            () => new RabbitMqSearchEventsPublisher(Substitute.For<IRabbitMqConnectionProvider>(), null!));

    private static RabbitMqSearchEventsPublisher CreatePublisher(IRabbitMqConnectionProvider connectionProvider) =>
        new(connectionProvider, NullLogger<RabbitMqSearchEventsPublisher>.Instance);

    private static SearchCompletedEvent NewEvent() => new(Guid.NewGuid(), CompletedAtUtc);

    private static string Format(Guid searchId) => searchId.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>
    /// A channel the broker still considers usable, so that the reuse check in the publisher is
    /// satisfied.
    /// </summary>
    /// <returns>The substituted channel.</returns>
    private static IChannel GivenAnOpenChannel()
    {
        IChannel channel = Substitute.For<IChannel>();

        channel.IsOpen.Returns(true);

        return channel;
    }

    /// <summary>
    /// Wires a connection provider that hands out the supplied channels in order, repeating the
    /// last one, and records the options every channel was opened with.
    /// </summary>
    /// <param name="channels">The channels to serve, in the order they should be handed out.</param>
    /// <returns>The provider, the connection behind it, and the recorded channel options.</returns>
    private static (IRabbitMqConnectionProvider Provider, IConnection Connection, List<CreateChannelOptions?> ChannelOptions)
        GivenABrokerServing(params IChannel[] channels)
    {
        List<CreateChannelOptions?> observedOptions = [];
        int handedOut = 0;

        IConnection connection = Substitute.For<IConnection>();

        connection.IsOpen.Returns(true);

        connection
            .CreateChannelAsync(Arg.Any<CreateChannelOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observedOptions.Add(call.ArgAt<CreateChannelOptions?>(0));

                IChannel next = channels[Math.Min(handedOut, channels.Length - 1)];
                handedOut++;

                return Task.FromResult(next);
            });

        IRabbitMqConnectionProvider provider = Substitute.For<IRabbitMqConnectionProvider>();

        provider.GetConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connection));

        return (provider, connection, observedOptions);
    }

    /// <summary>
    /// Records every message published on a channel, copying the body out of the borrowed buffer.
    /// </summary>
    /// <param name="channel">The channel to observe.</param>
    /// <returns>The list the observed messages are appended to.</returns>
    [SuppressMessage(
        "Usage",
        "CA2012:Use ValueTasks correctly",
        Justification = "The ValueTask here is the placeholder a substitute returns while a call "
            + "is being configured; it is handed straight to Returns and never awaited as work.")]
    private static List<PublishedMessage> CapturePublishes(IChannel channel)
    {
        List<PublishedMessage> published = [];

        channel
            .BasicPublishAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                published.Add(new PublishedMessage(
                    call.ArgAt<string>(0),
                    call.ArgAt<string>(1),
                    call.ArgAt<bool>(2),
                    call.ArgAt<BasicProperties>(3),
                    call.ArgAt<ReadOnlyMemory<byte>>(4).ToArray()));

                return ValueTask.CompletedTask;
            });

        return published;
    }

    /// <summary>
    /// Runs a publish and returns the failure it produced, keeping a translated deadline and a
    /// genuine cancellation apart instead of collapsing both into one assertion.
    /// </summary>
    /// <param name="publish">The publish to run.</param>
    /// <returns>The exception the publish threw, or <see langword="null"/> if it succeeded.</returns>
    private static async Task<Exception?> CapturePublishFailureAsync(Func<Task> publish)
    {
        try
        {
            await publish();
        }
        catch (TimeoutException timeout)
        {
            return timeout;
        }
        catch (OperationCanceledException cancelled)
        {
            return cancelled;
        }

        return null;
    }

    /// <summary>
    /// One message as it was handed to the broker.
    /// </summary>
    /// <param name="Exchange">Exchange the message was published to.</param>
    /// <param name="RoutingKey">Routing key the message was published with.</param>
    /// <param name="Mandatory">Whether the message was published as mandatory.</param>
    /// <param name="Properties">AMQP properties stamped on the message.</param>
    /// <param name="Body">A copy of the published body.</param>
    private sealed record PublishedMessage(
        string Exchange,
        string RoutingKey,
        bool Mandatory,
        BasicProperties Properties,
        byte[] Body);
}
