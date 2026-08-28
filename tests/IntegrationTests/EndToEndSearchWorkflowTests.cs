using System.Globalization;
using System.Net;
using System.Text.Json;
using IntegrationTests.Fixtures;
using Shouldly;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// The workflow the specification is written around, exercised end to end through the only entry
/// point a client has: start a search through the API Gateway, poll it, watch the batches
/// accumulate, and see the search settle into its completed state.
/// </summary>
/// <remarks>
/// Nothing here reaches past the gateway. The Search Service is only ever spoken to over gRPC, by
/// the gateway itself, which is what makes these tests a check of the assembled system rather
/// than of any one component. Each test raises its own pair of hosts, so one test can never
/// observe a search another one started.
/// </remarks>
/// <param name="broker">The suite's broker, which both hosts are configured against.</param>
[Collection(AsyncSearchSystemSuite.Name)]
public sealed class EndToEndSearchWorkflowTests(RabbitMqFixture broker)
{
    /// <summary>Ceiling for a single test, so a hung system fails instead of hanging the run.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task StartSearch_AnswersWithAParseableSearchId()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.Endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        using var response = await api.PostSearchAsync("""{"destination":"Paris"}""", cancellation.Token);

        var body = await response.Content.ReadAsStringAsync(cancellation.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);

        document.RootElement.TryGetProperty("searchId", out var searchId).ShouldBeTrue(body);
        searchId.TryGetGuid(out var parsed).ShouldBeTrue(body);
        parsed.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task PollImmediatelyAfterStarting_ReportsAnIncompleteSearchWithNoResults()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.Endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        // The very first call through the gateway pays for the gRPC pipeline being built. Paying
        // it here keeps that cost outside the window this test measures, which is narrow: the
        // first batch lands one batch interval after the search starts.
        await WarmUpAsync(api, cancellation.Token);

        var searchId = await api.StartSearchAsync("Paris", cancellation.Token);

        var body = await api.GetSearchJsonAsync(searchId, cancellation.Token);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        root.GetProperty("searchId").GetGuid().ShouldBe(searchId);
        root.GetProperty("isCompleted").GetBoolean().ShouldBeFalse(body);

        var results = root.GetProperty("results");
        results.ValueKind.ShouldBe(JsonValueKind.Array, body);
        results.GetArrayLength().ShouldBe(0, $"a search that has just started has no results yet: {body}");
    }

    [DockerFact]
    public async Task StartedSearch_AccumulatesEveryBatchAndThenCompletes()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.RequireEndpoint());

        var api = new GatewayApi(system.CreateGatewayClient());

        await WarmUpAsync(api, cancellation.Token);

        // Steps 1 and 2: start the search through the gateway and take the identifier it answers
        // with.
        var searchId = await api.StartSearchAsync("Paris", cancellation.Token);

        // Steps 3 and 4: poll until the search reports itself complete, recording the result
        // count every time it changes.
        var observedCounts = new List<int>();

        var finalState = await Wait.UntilAsync(
            async token =>
            {
                var state = await api.GetSearchStateAsync(searchId, token);

                if (observedCounts.Count == 0 || observedCounts[^1] != state.Results.Count)
                {
                    observedCounts.Add(state.Results.Count);
                }

                return state;
            },
            state => state.IsCompleted,
            $"search {searchId} to report itself completed",
            cancellation.Token);

        var progression = string.Join(
            " -> ",
            observedCounts.Select(count => count.ToString(CultureInfo.InvariantCulture)));

        // Results were seen growing, they only ever grew, and they grew one whole batch at a time.
        observedCounts.Count.ShouldBeGreaterThanOrEqualTo(
            3,
            $"the results should have been observed growing over time, but the progression was: {progression}");

        for (var index = 1; index < observedCounts.Count; index++)
        {
            observedCounts[index].ShouldBeGreaterThan(
                observedCounts[index - 1],
                $"the result count must never shrink, but the progression was: {progression}");
        }

        foreach (var count in observedCounts)
        {
            (count % AsyncSearchSystemFactory.HotelsPerBatch).ShouldBe(
                0,
                $"results arrive one batch of {AsyncSearchSystemFactory.HotelsPerBatch} at a time, "
                + $"but the progression was: {progression}");
        }

        // Step 5: the search is final, and holds every batch it promised.
        finalState.IsCompleted.ShouldBeTrue();
        finalState.SearchId.ShouldBe(searchId);
        finalState.Results.Count.ShouldBe(
            AsyncSearchSystemFactory.ExpectedResultCount,
            $"{AsyncSearchSystemFactory.BatchCount} batches of {AsyncSearchSystemFactory.HotelsPerBatch} hotels "
            + $"were expected, but the progression was: {progression}");

        observedCounts[^1].ShouldBe(AsyncSearchSystemFactory.ExpectedResultCount);

        // The payload itself has to be usable: every hotel identified, named and priced.
        foreach (var hotel in finalState.Results)
        {
            hotel.HotelId.ShouldNotBeNullOrWhiteSpace();
            hotel.Name.ShouldNotBeNullOrWhiteSpace();
            hotel.Price.ShouldBeInRange(
                (decimal)AsyncSearchSystemFactory.MinHotelPrice,
                (decimal)AsyncSearchSystemFactory.MaxHotelPrice);
        }

        finalState.Results
            .Select(hotel => hotel.HotelId)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(AsyncSearchSystemFactory.ExpectedResultCount, "every hotel must be distinctly identified");
    }

    /// <summary>
    /// Sends one throwaway request through the whole gateway to Search Service path so that the
    /// first timed call is not the one that pays for building it.
    /// </summary>
    /// <param name="api">The gateway to warm up.</param>
    /// <param name="cancellationToken">Token that aborts the call.</param>
    /// <returns>A task that completes once the path is warm.</returns>
    [DockerFact]
    public async Task PolledSearch_EchoesTheDestinationItWasStartedWith()
    {
        // The destination crosses two boundaries on the way out: the aggregate to the protobuf
        // response, then protobuf back to JSON. A mapping mistake at either one is invisible to
        // the unit tests, which never leave their own layer.
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.Endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        var searchId = await api.StartSearchAsync("Reykjavik", cancellation.Token);

        var body = await api.GetSearchJsonAsync(searchId, cancellation.Token);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("destination").GetString()
            .ShouldBe("Reykjavik", $"the polled search should report what it was searching for: {body}");
    }

    private static async Task WarmUpAsync(GatewayApi api, CancellationToken cancellationToken)
    {
        using var response = await api.GetSearchAsync(Guid.NewGuid().ToString(), cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
