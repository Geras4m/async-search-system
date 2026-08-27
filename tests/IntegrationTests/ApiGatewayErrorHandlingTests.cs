using System.Net;
using System.Text.Json;
using IntegrationTests.Fixtures;
using Shouldly;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// The edge cases the specification calls out for the API Gateway: a malformed identifier, an
/// identifier nobody knows, and a request body that does not pass validation.
/// </summary>
/// <remarks>
/// <para>
/// The distinction these tests defend is between the three kinds of "no": <c>400</c> when the
/// caller sent something the gateway cannot even interpret, <c>404</c> when the request was
/// perfectly well formed but names something that does not exist, and never <c>500</c>, because a
/// caller's mistake is not a server failure.
/// </para>
/// <para>
/// None of these need a broker: no search here ever reaches completion, so they run whether or
/// not Docker is present.
/// </para>
/// </remarks>
/// <param name="broker">The suite's broker, which both hosts are configured against.</param>
[Collection(AsyncSearchSystemSuite.Name)]
public sealed class ApiGatewayErrorHandlingTests(RabbitMqFixture broker)
{
    /// <summary>Ceiling for a single test, so a hung system fails instead of hanging the run.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task GetSearch_WithAnIdentifierThatIsNotAGuid_IsRejectedAsABadRequest()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.Endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        using var response = await api.GetSearchAsync("not-a-guid", cancellation.Token);

        var body = await response.Content.ReadAsStringAsync(cancellation.Token);

        // Specifically not a 404: the identifier is unusable, so the request never gets far
        // enough for the search to be missing. And specifically not a 500: the caller is at
        // fault, not the system.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);

        using var document = JsonDocument.Parse(body);

        document.RootElement.TryGetProperty("errors", out var errors)
            .ShouldBeTrue($"the problem document should name the offending field: {body}");
        errors.TryGetProperty("searchId", out _)
            .ShouldBeTrue($"the offending field is the route value: {body}");
    }

    [Fact]
    public async Task GetSearch_WithAnUnknownIdentifier_IsReportedAsNotFound()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.Endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        using var response = await api.GetSearchAsync(Guid.NewGuid().ToString(), cancellation.Token);

        var body = await response.Content.ReadAsStringAsync(cancellation.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, body);
    }

    [Fact]
    public async Task StartSearch_WithAnEmptyDestination_IsRejectedAsABadRequest()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.Endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        using var response = await api.PostSearchAsync("""{"destination":""}""", cancellation.Token);

        var body = await response.Content.ReadAsStringAsync(cancellation.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        body.ShouldContain("destination", Case.Insensitive);
    }

    [Fact]
    public async Task StartSearch_WithATooShortDestination_IsRejectedAsABadRequest()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.Endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        // The specification requires a destination longer than two characters.
        using var response = await api.PostSearchAsync("""{"destination":"Pa"}""", cancellation.Token);

        var body = await response.Content.ReadAsStringAsync(cancellation.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
    }

    [Fact]
    public async Task StartSearch_WithTheShortestAcceptedDestination_Succeeds()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.Endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        // Three characters: the first length the validator must accept. Asserting the boundary
        // from both sides is what proves the rule is "longer than two" rather than "at least two"
        // or "at least four".
        using var response = await api.PostSearchAsync("""{"destination":"Rio"}""", cancellation.Token);

        var body = await response.Content.ReadAsStringAsync(cancellation.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);

        document.RootElement.GetProperty("searchId").TryGetGuid(out var searchId).ShouldBeTrue(body);
        searchId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task StartSearch_WithATooLongDestination_IsRejectedAsABadRequest()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.Endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        // The specification requires a destination shorter than one hundred characters.
        var destination = new string('a', 100);

        using var response = await api.PostSearchAsync(
            $$"""{"destination":"{{destination}}"}""",
            cancellation.Token);

        var body = await response.Content.ReadAsStringAsync(cancellation.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
    }

    [Fact]
    public async Task StartSearch_WithAMalformedBody_IsRejectedAsABadRequest()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.Endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        // Truncated JSON. Model binding cannot produce a request from it, and the error boundary
        // has to report that as the caller's fault rather than letting it escape as a 500.
        using var response = await api.PostSearchAsync("""{"destination": "Par""", cancellation.Token);

        var body = await response.Content.ReadAsStringAsync(cancellation.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
    }

    [Fact]
    public async Task StartSearch_WithNoBodyAtAll_IsRejectedAsABadRequest()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await using var system = new AsyncSearchSystemFactory(broker.Endpoint);

        var api = new GatewayApi(system.CreateGatewayClient());

        using var response = await api.PostSearchAsync(string.Empty, cancellation.Token);

        var body = await response.Content.ReadAsStringAsync(cancellation.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
    }
}
