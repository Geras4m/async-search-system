using IntegrationTests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotificationService.Consumers;
using NotificationService.Messaging;
using Shared.EventContracts;
using Shouldly;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Proves the consuming half of the messaging contract, which is the specification's last step:
/// the Notification Service picks the completion event off the broker and logs the identifier of
/// the search that finished.
/// </summary>
/// <remarks>
/// The real consumer and the real connection provider are hosted in a real generic host, wired to
/// the suite's broker, with a recording logger provider in place of the console. Asserting on the
/// captured record rather than on scraped console text is what lets the test check both the
/// message the specification quotes and the structured <c>SearchId</c> behind it.
/// </remarks>
[Collection(AsyncSearchSystemSuite.Name)]
public sealed class NotificationServiceConsumerTests
{
    /// <summary>Text the specification requires the Notification Service to log.</summary>
    private const string ExpectedMessage = "Search completed event received.";

    /// <summary>Text the consumer logs once it is bound to the queue and ready.</summary>
    private const string ConsumerReadyMessage = "Consuming search completion events.";

    /// <summary>Ceiling for a single test, so a hung consumer fails instead of hanging the run.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    private readonly RabbitMqFixture _broker;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationServiceConsumerTests"/> class.
    /// </summary>
    /// <param name="broker">The suite's broker.</param>
    /// <exception cref="ArgumentNullException"><paramref name="broker"/> is <see langword="null"/>.</exception>
    public NotificationServiceConsumerTests(RabbitMqFixture broker)
    {
        ArgumentNullException.ThrowIfNull(broker);

        _broker = broker;
    }

    [DockerFact]
    public async Task Consumer_LogsTheSearchIdOfTheCompletionEventItReceives()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);

        var recorder = new RecordingLoggerProvider();
        var host = BuildNotificationServiceHost(recorder);

        try
        {
            await host.StartAsync(cancellation.Token);

            // Publishing before the queue is bound would race the consumer's own topology
            // declaration, so the test waits for the consumer to announce it is ready.
            await Wait.UntilAsync(
                () => recorder.Snapshot().Any(record => record.Message.Contains(ConsumerReadyMessage, StringComparison.Ordinal)),
                "the notification service to start consuming",
                cancellation.Token);

            var completedEvent = new SearchCompletedEvent(Guid.NewGuid(), DateTime.UtcNow);

            await using (var probe = await BrokerProbe.ConnectAsync(_broker.RequireEndpoint(), cancellation.Token))
            {
                await probe.PublishAsync(completedEvent, cancellation.Token);
            }

            IEnumerable<LogRecord> Matches() => recorder
                .Snapshot()
                .Where(record =>
                    record.Message.Contains(ExpectedMessage, StringComparison.Ordinal)
                    && string.Equals(
                        record.Property("SearchId"),
                        completedEvent.SearchId.ToString(),
                        StringComparison.OrdinalIgnoreCase));

            await Wait.UntilAsync(
                () => Matches().Any(),
                $"the notification service to log '{ExpectedMessage}' for search {completedEvent.SearchId}",
                cancellation.Token);

            var logged = Matches().First();

            logged.Level.ShouldBe(LogLevel.Information);
            logged.Category.ShouldBe(typeof(SearchCompletedConsumer).FullName);
            logged.Message.ShouldContain(ExpectedMessage);
            logged.Message.ShouldContain(completedEvent.SearchId.ToString());
            logged.Property("SearchId").ShouldBe(completedEvent.SearchId.ToString());
            logged.Exception.ShouldBeNull();
        }
        finally
        {
            await StopAsync(host);
        }
    }

    [DockerFact]
    public async Task Consumer_DiscardsAnUnreadableEventAndKeepsConsuming()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);

        var recorder = new RecordingLoggerProvider();
        var host = BuildNotificationServiceHost(recorder);

        try
        {
            await host.StartAsync(cancellation.Token);

            await Wait.UntilAsync(
                () => recorder.Snapshot().Any(record => record.Message.Contains(ConsumerReadyMessage, StringComparison.Ordinal)),
                "the notification service to start consuming",
                cancellation.Token);

            var survivor = new SearchCompletedEvent(Guid.NewGuid(), DateTime.UtcNow);

            await using (var probe = await BrokerProbe.ConnectAsync(_broker.RequireEndpoint(), cancellation.Token))
            {
                // A body that can never deserialise. The consumer has to reject it without
                // requeueing, or it would redeliver forever and the event behind it would never
                // be handled.
                await probe.PublishRawAsync("this is not json"u8.ToArray(), cancellation.Token);
                await probe.PublishAsync(survivor, cancellation.Token);
            }

            await Wait.UntilAsync(
                () => recorder.Snapshot().Any(record =>
                    record.Message.Contains(ExpectedMessage, StringComparison.Ordinal)
                    && string.Equals(
                        record.Property("SearchId"),
                        survivor.SearchId.ToString(),
                        StringComparison.OrdinalIgnoreCase)),
                $"the notification service to keep consuming after a poison message and log {survivor.SearchId}",
                cancellation.Token);

            recorder.Snapshot()
                .ShouldContain(record => record.Level == LogLevel.Error, "the unreadable message should have been reported");
        }
        finally
        {
            await StopAsync(host);
        }
    }

    /// <summary>
    /// Builds a host running the real Notification Service consumer against the suite's broker.
    /// </summary>
    /// <param name="recorder">Logger provider that captures what the consumer logs.</param>
    /// <returns>The unstarted host.</returns>
    private IHost BuildNotificationServiceHost(RecordingLoggerProvider recorder)
    {
        var builder = Host.CreateApplicationBuilder();

        var endpoint = _broker.RequireEndpoint();

        // Added last, so these win over the settings file the worker ships with.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["RabbitMq:Host"] = endpoint.Host,
            ["RabbitMq:Port"] = endpoint.PortValue,
            ["RabbitMq:UserName"] = endpoint.UserName,
            ["RabbitMq:Password"] = endpoint.Password,
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:MaxConnectRetries"] = "5",
            ["RabbitMq:RetryDelay"] = "00:00:00.200",
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(recorder);

        // Exactly the registrations the Notification Service's own entry point makes.
        builder.Services
            .AddOptions<RabbitMqOptions>()
            .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();
        builder.Services.AddHostedService<SearchCompletedConsumer>();

        return builder.Build();
    }

    /// <summary>
    /// Stops the host and releases it asynchronously, which its broker connection requires.
    /// </summary>
    /// <param name="host">Host to stop.</param>
    /// <returns>A task that completes once the host has been released.</returns>
    private static async Task StopAsync(IHost host)
    {
        await host.StopAsync(CancellationToken.None);

        if (host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            host.Dispose();
        }
    }
}
