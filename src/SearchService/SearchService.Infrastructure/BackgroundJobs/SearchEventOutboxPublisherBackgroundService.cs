using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SearchService.Application.Abstractions;
using SearchService.Application.Options;
using Shared.EventContracts;

namespace SearchService.Infrastructure.BackgroundJobs;

/// <summary>
/// Drains the search event outbox, retrying deliveries the inline publish could not complete.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes completion notifications at-least-once. The completion handler records the
/// event before contacting the broker and removes it again on a successful inline publish, so in a
/// healthy system this service finds nothing to do. It earns its keep only when the broker was
/// unreachable at the moment a search completed: those events stay owed, and each sweep tries them
/// again until the broker accepts them.
/// </para>
/// <para>
/// Failures are expected here rather than exceptional, so a failed sweep logs at Debug and leaves
/// the entries in place. Logging every attempt at Warning would turn a five minute outage into
/// thousands of near-identical records. The Warning is emitted once per sweep that had work and
/// could not finish it, which is enough to see an unhealthy broker without drowning the log.
/// </para>
/// </remarks>
public sealed partial class SearchEventOutboxPublisherBackgroundService : BackgroundService
{
    private readonly ISearchEventOutbox _outbox;
    private readonly ISearchEventsPublisher _publisher;
    private readonly ILogger<SearchEventOutboxPublisherBackgroundService> _logger;
    private readonly SearchEventOutboxOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchEventOutboxPublisherBackgroundService"/> class.
    /// </summary>
    /// <param name="outbox">Store of events still owed to the broker.</param>
    /// <param name="publisher">Outbound messaging boundary used to retry deliveries.</param>
    /// <param name="options">Sweep interval and batch size.</param>
    /// <param name="logger">Sink for structured log records.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SearchEventOutboxPublisherBackgroundService(
        ISearchEventOutbox outbox,
        ISearchEventsPublisher publisher,
        IOptions<SearchEventOutboxOptions> options,
        ILogger<SearchEventOutboxPublisherBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
    }

    /// <summary>
    /// Sweeps the outbox on the configured interval until the host stops.
    /// </summary>
    /// <param name="stoppingToken">Token signalled when the host begins shutting down.</param>
    /// <returns>A task that completes when the service stops.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogPublisherStarted(_options.PollInterval, _options.BatchSize);

        using PeriodicTimer timer = new(_options.PollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await DrainOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }

        LogPublisherStopped();
    }

    /// <summary>
    /// Attempts delivery of one batch of owed events.
    /// </summary>
    /// <param name="cancellationToken">Token signalled when the host begins shutting down.</param>
    /// <returns>A task that completes once the batch has been attempted.</returns>
    /// <remarks>
    /// Never throws. A sweep that fails must not take the service down, because the next sweep is
    /// exactly the retry the outbox exists to provide.
    /// </remarks>
    private async Task DrainOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<SearchCompletedEvent> pending =
                await _outbox.GetPendingAsync(_options.BatchSize, cancellationToken).ConfigureAwait(false);

            if (pending.Count == 0)
            {
                return;
            }

            int delivered = 0;

            foreach (SearchCompletedEvent owed in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await _publisher.PublishSearchCompletedAsync(owed, cancellationToken).ConfigureAwait(false);
                    await _outbox.RemoveAsync(owed.SearchId, cancellationToken).ConfigureAwait(false);

                    delivered++;

                    LogEventRedelivered(owed.SearchId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The broker is still unhealthy. Stop the sweep rather than grinding through
                    // the rest of the batch: they share one connection, so the others will fail
                    // the same way and each costs a full publish deadline.
                    LogSweepIncomplete(ex, delivered, pending.Count);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            LogSweepFailed(ex);
        }
    }

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "Search event outbox publisher started. PollInterval={PollInterval} BatchSize={BatchSize}")]
    private partial void LogPublisherStarted(TimeSpan pollInterval, int batchSize);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Information,
        Message = "Search event outbox publisher stopped.")]
    private partial void LogPublisherStopped();

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Information,
        Message = "Event published. SearchId={SearchId}")]
    private partial void LogEventRedelivered(Guid searchId);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Warning,
        Message = "Outbox sweep stopped early; the broker is still unhealthy. Delivered={Delivered} Pending={Pending}")]
    private partial void LogSweepIncomplete(Exception exception, int delivered, int pending);

    [LoggerMessage(
        EventId = 2104,
        Level = LogLevel.Error,
        Message = "Outbox sweep failed unexpectedly.")]
    private partial void LogSweepFailed(Exception exception);
}
