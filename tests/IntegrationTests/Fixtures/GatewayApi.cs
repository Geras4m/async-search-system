using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace IntegrationTests.Fixtures;

/// <summary>
/// The client's view of the system: HTTP and JSON against the API Gateway, and nothing else.
/// </summary>
/// <remarks>
/// Bodies are written and read as text rather than through a shared DTO so the tests assert the
/// documented wire contract instead of asserting that the gateway agrees with itself.
/// </remarks>
/// <param name="client">Client bound to the gateway.</param>
internal sealed class GatewayApi(HttpClient client)
{
    /// <summary>Media type both endpoints speak.</summary>
    private const string JsonMediaType = "application/json";

    /// <summary>
    /// Reader for the gateway's responses. Cached: a fresh options instance per call would be
    /// re-warmed by the serializer every time.
    /// </summary>
    private static readonly JsonSerializerOptions ResponseOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Route of the search collection.</summary>
    private static readonly Uri SearchesRoute = new("/searches", UriKind.Relative);

    /// <summary>
    /// Starts a search and returns the identifier the gateway assigned it.
    /// </summary>
    /// <param name="destination">Destination to search hotels for.</param>
    /// <param name="cancellationToken">Token that aborts the call.</param>
    /// <returns>The identifier of the new search.</returns>
    public async Task<Guid> StartSearchAsync(string destination, CancellationToken cancellationToken)
    {
        using var response = await PostSearchAsync(
            $$"""{"destination":"{{destination}}"}""",
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"POST /searches answered with: {body}");

        using var document = JsonDocument.Parse(body);

        document.RootElement.TryGetProperty("searchId", out var searchIdProperty)
            .ShouldBeTrue($"POST /searches must answer with a searchId member, but answered: {body}");

        searchIdProperty.TryGetGuid(out var searchId)
            .ShouldBeTrue($"searchId must be a parseable GUID, but was: {searchIdProperty}");

        return searchId;
    }

    /// <summary>
    /// Posts a raw body to <c>POST /searches</c>, so malformed requests can be exercised exactly
    /// as a client would send them.
    /// </summary>
    /// <param name="body">Body to send verbatim.</param>
    /// <param name="cancellationToken">Token that aborts the call.</param>
    /// <returns>The gateway's response. The caller owns it.</returns>
    public async Task<HttpResponseMessage> PostSearchAsync(string body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body, Encoding.UTF8, JsonMediaType);

        return await client.PostAsync(SearchesRoute, content, cancellationToken);
    }

    /// <summary>
    /// Polls a search by its raw route value, so malformed identifiers can be exercised.
    /// </summary>
    /// <param name="searchId">Route value to send verbatim.</param>
    /// <param name="cancellationToken">Token that aborts the call.</param>
    /// <returns>The gateway's response. The caller owns it.</returns>
    public async Task<HttpResponseMessage> GetSearchAsync(string searchId, CancellationToken cancellationToken) =>
        await client.GetAsync(new Uri($"/searches/{searchId}", UriKind.Relative), cancellationToken);

    /// <summary>
    /// Polls a search and returns the response body verbatim, failing the test on any status
    /// other than <c>200 OK</c>.
    /// </summary>
    /// <param name="searchId">Identifier of the search to poll.</param>
    /// <param name="cancellationToken">Token that aborts the call.</param>
    /// <returns>The raw JSON document the gateway answered with.</returns>
    public async Task<string> GetSearchJsonAsync(Guid searchId, CancellationToken cancellationToken)
    {
        using var response = await GetSearchAsync(searchId.ToString(), cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET /searches/{searchId} answered with: {body}");

        return body;
    }

    /// <summary>
    /// Polls a search and deserializes the state it reports.
    /// </summary>
    /// <param name="searchId">Identifier of the search to poll.</param>
    /// <param name="cancellationToken">Token that aborts the call.</param>
    /// <returns>The state of the search at the moment it was polled.</returns>
    public async Task<SearchStateDocument> GetSearchStateAsync(Guid searchId, CancellationToken cancellationToken)
    {
        var body = await GetSearchJsonAsync(searchId, cancellationToken);

        var state = JsonSerializer.Deserialize<SearchStateDocument>(body, ResponseOptions);

        state.ShouldNotBeNull($"GET /searches/{searchId} answered with a body that is not a search state: {body}");

        return state;
    }
}
