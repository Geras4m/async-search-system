using System.Text.Json;
using IntegrationTests.Fixtures;
using Shouldly;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Proves the publishing half of the messaging contract: when a search completes, the Search
/// Service really does put a <c>SearchCompletedEvent</c> on the broker, in the shape the
/// Notification Service expects.
/// </summary>
/// <remarks>
/// The Notification Service is deliberately absent here. A test that asserted the event arrived
/// by watching the consumer would fail for two different reasons at once; watching the exchange
/// directly pins the publisher on its own, so a later failure of the consumer test can only mean
/// the consumer.
/// </remarks>
/// <param name="broker">The suite's broker.</param>
[Collection(AsyncSearchSystemSuite.Name)]
public sealed class SearchCompletedEventPublishingTests(RabbitMqFixture broker)
{
    /// <summary>Ceiling for a single test, so a hung system fails instead of hanging the run.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Budget for the event to travel. A compressed search takes about 1.2 seconds to produce its
    /// six batches; the rest is slack for a cold broker connection.
    /// </summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);

    [DockerFact]
    public async Task CompletedSearch_PublishesItsCompletionEventToTheExchange()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.RequireEndpoint());

        var api = new GatewayApi(system.CreateGatewayClient());

        await using var probe = await BrokerProbe.ConnectAsync(broker.RequireEndpoint(), cancellation.Token);

        // Bound before the search is started: a queue bound afterwards could miss the event and
        // the test would be timing-dependent rather than deterministic.
        await probe.WatchCompletionEventsAsync(cancellation.Token);

        var startedAtUtc = DateTime.UtcNow;

        var searchId = await api.StartSearchAsync("Paris", cancellation.Token);

        var observed = await probe.WaitForEventAsync(searchId, EventTimeout, cancellation.Token);

        observed.Event.SearchId.ShouldBe(searchId);
        observed.Event.CompletedAtUtc.ShouldBeGreaterThanOrEqualTo(startedAtUtc.AddSeconds(-1));
        observed.Event.CompletedAtUtc.ShouldBeLessThanOrEqualTo(DateTime.UtcNow.AddMinutes(1));

        // The payload is the contract. Both services read it with the same shared serializer
        // settings, so the members on the wire are camelCase.
        using var document = JsonDocument.Parse(observed.Json);
        var root = document.RootElement;

        root.GetProperty("searchId").GetGuid().ShouldBe(searchId);
        root.TryGetProperty("completedAtUtc", out var completedAtUtc)
            .ShouldBeTrue($"the event must carry the completion instant, but was: {observed.Json}");
        completedAtUtc.ValueKind.ShouldBe(JsonValueKind.String, observed.Json);
    }

    [DockerFact]
    public async Task CompletedSearch_PublishesExactlyOneEventForThatSearch()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.RequireEndpoint());

        var api = new GatewayApi(system.CreateGatewayClient());

        await using var probe = await BrokerProbe.ConnectAsync(broker.RequireEndpoint(), cancellation.Token);
        await probe.WatchCompletionEventsAsync(cancellation.Token);

        var searchId = await api.StartSearchAsync("Paris", cancellation.Token);

        await probe.WaitForEventAsync(searchId, EventTimeout, cancellation.Token);

        // A search is completed once, so a second event for the same identifier would mean the
        // completion path is not idempotent. Waiting for one that must never arrive is what makes
        // a timeout the success condition here.
        await Should.ThrowAsync<TimeoutException>(
            () => probe.WaitForEventAsync(searchId, TimeSpan.FromSeconds(2), cancellation.Token));
    }
}
