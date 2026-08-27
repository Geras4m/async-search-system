using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shared.GrpcContracts;
using GatewayProgram = ApiGateway.Program;
using SearchServiceProgram = SearchService.Api.Program;

namespace IntegrationTests.Fixtures;

/// <summary>
/// Runs the API Gateway and the Search Service side by side, in one process, wired to each other
/// exactly as they are in production: the gateway reaches the Search Service over gRPC and
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two hosts, one process.</b> Each service is hosted by its own
/// <see cref="WebApplicationFactory{TEntryPoint}"/>. The Search Service host is created first so
/// its in-memory transport exists; the gateway's gRPC client is then re-registered against that
/// transport. <see cref="TestServer"/> speaks HTTP/2 in memory, which is what gRPC needs, so the
/// call path under test is the real generated client, the real interceptor chain and the real
/// service implementation, with only the socket removed.
/// </para>
/// <para>
/// <b>Compressed time.</b> The specification's search takes six batches five seconds apart, half
/// a minute per test. <c>Search:BatchInterval</c> exists precisely so that a test can shorten it:
/// the batch count and batch size stay at the specified six and five, so the workflow under test
/// is the real one, and only the wait between batches is scaled down.
/// </para>
/// </remarks>
public sealed class AsyncSearchSystemFactory : IAsyncDisposable
{
    /// <summary>Batches a search produces, as the specification mandates.</summary>
    public const int BatchCount = 6;

    /// <summary>Hotels in one batch, as the specification mandates.</summary>
    public const int HotelsPerBatch = 5;

    /// <summary>Lowest price a generated hotel may carry.</summary>
    public const int MinHotelPrice = 80;

    /// <summary>Highest price a generated hotel may carry.</summary>
    public const int MaxHotelPrice = 400;

    /// <summary>Hotels a completed search holds: every batch, appended.</summary>
    public const int ExpectedResultCount = BatchCount * HotelsPerBatch;

    /// <summary>
    /// Wait between batches while the tests run. Twenty-five times shorter than production, so a
    /// full six-batch search finishes in about 1.2 seconds instead of 30.
    /// </summary>
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Address the gateway's gRPC client is pointed at. The value is irrelevant beyond being a
    /// well-formed absolute URI: every request is handled by the in-memory transport.
    /// </summary>
    private const string InMemorySearchServiceAddress = "http://localhost";

    private readonly WebApplicationFactory<SearchServiceProgram> _searchService;
    private readonly WebApplicationFactory<GatewayProgram> _gateway;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncSearchSystemFactory"/> class and starts
    /// both hosts.
    /// </summary>
    /// <param name="broker">Endpoint of the broker both hosts publish to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="broker"/> is <see langword="null"/>.</exception>
    public AsyncSearchSystemFactory(BrokerEndpoint broker)
    {
        ArgumentNullException.ThrowIfNull(broker);

        _searchService = new SearchServiceHost(broker);

        // Touching Server builds and starts the Search Service host; the handler it hands out is
        // the in-memory transport the gateway's gRPC client is bound to below.
        var searchServiceTransport = _searchService.Server.CreateHandler();

        _gateway = new ApiGatewayHost(searchServiceTransport);

        // Same again for the gateway: build it now so that a wiring mistake fails the fixture
        // rather than the first assertion of the first test that happens to use it.
        _ = _gateway.Server;
    }

    /// <summary>
    /// Creates an HTTP client bound to the API Gateway, the only entry point a client of this
    /// system is allowed to use.
    /// </summary>
    /// <returns>
    /// A client whose base address is the gateway. Ownership stays with the factory, which
    /// disposes every client it created.
    /// </returns>
    public HttpClient CreateGatewayClient() => _gateway.CreateClient();

    /// <summary>
    /// Stops both hosts.
    /// </summary>
    /// <returns>A task that completes once both hosts have been released.</returns>
    /// <remarks>
    /// The gateway goes first: its client factory owns the handler that talks to the Search
    /// Service host, so releasing it before the host it points at keeps the shutdown ordered.
    /// Disposal is asynchronous because both hosts hold singletons that own broker connections
    /// and implement only <see cref="IAsyncDisposable"/>.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await _gateway.DisposeAsync();
        await _searchService.DisposeAsync();
    }

    /// <summary>
    /// Builds the configuration overrides shared by both hosts: the broker endpoint, plus a
    /// deliberately impatient reconnect policy so a run without a broker fails fast instead of
    /// holding the suite open for the production retry budget.
    /// </summary>
    /// <param name="broker">Endpoint of the broker.</param>
    /// <returns>Configuration entries to layer over the host's own settings.</returns>
    private static Dictionary<string, string?> BrokerSettings(BrokerEndpoint broker) => new(StringComparer.Ordinal)
    {
        ["RabbitMq:Host"] = broker.Host,
        ["RabbitMq:Port"] = broker.PortValue,
        ["RabbitMq:UserName"] = broker.UserName,
        ["RabbitMq:Password"] = broker.Password,
        ["RabbitMq:VirtualHost"] = "/",
        ["RabbitMq:MaxConnectRetries"] = "3",
        ["RabbitMq:RetryDelay"] = "00:00:00.200",
    };

    /// <summary>
    /// Hosts the Search Service in memory with the search workflow compressed in time.
    /// </summary>
    /// <param name="broker">Endpoint the completion event is published to.</param>
    private sealed class SearchServiceHost(BrokerEndpoint broker) : WebApplicationFactory<SearchServiceProgram>
    {
        /// <summary>
        /// Layers the test configuration over the Search Service's own settings.
        /// </summary>
        /// <param name="builder">The host builder being configured.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ConfigureAppConfiguration(configuration =>
            {
                var settings = BrokerSettings(broker);

                // The specified batch count and batch size are kept exactly; only the interval
                // between batches is compressed, so what runs is the specified workflow.
                settings["Search:BatchCount"] = BatchCount.ToString(CultureInfo.InvariantCulture);
                settings["Search:HotelsPerBatch"] = HotelsPerBatch.ToString(CultureInfo.InvariantCulture);
                settings["Search:BatchInterval"] = BatchInterval.ToString("c", CultureInfo.InvariantCulture);
                settings["Search:MinHotelPrice"] = MinHotelPrice.ToString(CultureInfo.InvariantCulture);
                settings["Search:MaxHotelPrice"] = MaxHotelPrice.ToString(CultureInfo.InvariantCulture);

                configuration.AddInMemoryCollection(settings);
            });
        }
    }

    /// <summary>
    /// Hosts the API Gateway in memory with its gRPC client bound to the Search Service host's
    /// transport instead of to a socket.
    /// </summary>
    /// <param name="searchServiceTransport">In-memory transport of the Search Service host.</param>
    private sealed class ApiGatewayHost(HttpMessageHandler searchServiceTransport)
        : WebApplicationFactory<GatewayProgram>
    {
        /// <summary>
        /// Redirects the gateway's only downstream dependency onto the in-memory transport.
        /// </summary>
        /// <param name="builder">The host builder being configured.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Grpc:SearchService"] = InMemorySearchServiceAddress,
                }));

            builder.ConfigureTestServices(services =>
            {
                services.AddResponseBodyCompatibility();

                // The gateway registered this client against a real address at start-up. Removing
                // that registration first is what guarantees the test's registration is the one
                // resolved, rather than relying on which of two identical registrations wins.
                services.RemoveAll<SearchGrpcService.SearchGrpcServiceClient>();

                services
                    .AddGrpcClient<SearchGrpcService.SearchGrpcServiceClient>(options =>
                        options.Address = new Uri(InMemorySearchServiceAddress))
                    .ConfigurePrimaryHttpMessageHandler(() => searchServiceTransport)

                    // The transport belongs to the Search Service host, not to this client
                    // factory. An expiring handler entry would dispose it mid-suite, so the entry
                    // is pinned for the lifetime of the host.
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            });
        }
    }

}
