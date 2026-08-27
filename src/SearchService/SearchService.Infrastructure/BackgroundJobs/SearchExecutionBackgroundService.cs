using System.Collections.Concurrent;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SearchService.Application.Abstractions;
using SearchService.Application.Commands;
using SearchService.Application.Options;

namespace SearchService.Infrastructure.BackgroundJobs;

/// <summary>
/// The search execution engine: drains scheduled search identifiers and drives each search
/// through its batches to completion.
/// </summary>
/// <remarks>
/// <para>
/// One long-lived loop reads identifiers from <see cref="ISearchExecutionScheduler"/> and
/// starts each search as its own task without awaiting it, so a search that is midway
/// through its five-second waits never delays the next one. Concurrency is bounded by a
/// <see cref="SemaphoreSlim"/> sized from <c>MaxConcurrentSearches</c>: the slot is taken
/// before the search starts and released when it ends, so the reader loop naturally applies
/// back pressure to the queue instead of spawning unbounded work.
/// </para>
/// <para>
/// <b>Isolation.</b> Each search is executed inside its own DI scope and its own exception
/// boundary. Every failure other than shutdown cancellation is logged against its
/// <c>SearchId</c> and swallowed, because one poisoned search must never take the engine,
/// and therefore every other search in the process, down with it.
/// </para>
/// <para>
/// <b>Scope lifetime.</b> This service is a singleton, while MediatR handlers and their
/// dependencies are scoped. Capturing an <see cref="IMediator"/> in a field would be a
/// captive dependency and would keep one scope alive for the lifetime of the process, so a
/// scope is created per search from <see cref="IServiceScopeFactory"/> and the mediator is
/// resolved from inside it.
/// </para>
/// </remarks>
public sealed partial class SearchExecutionBackgroundService : BackgroundService
{
    /// <summary>
    /// How long <see cref="StopAsync"/> waits for searches that are still running once the
    /// stopping token has been signalled. In-flight searches observe the same token and
    /// unwind almost immediately; the grace period exists only so that a search caught
    /// mid-publish can finish, and is bounded so a stuck search cannot hold the host open.
    /// </summary>
    private static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(15);

    private readonly ISearchExecutionScheduler _scheduler;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SearchExecutionBackgroundService> _logger;
    private readonly SearchExecutionOptions _options;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly ConcurrentDictionary<Task, byte> _inFlightSearches = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchExecutionBackgroundService"/> class.
    /// </summary>
    /// <param name="scheduler">Queue of searches waiting to be executed.</param>
    /// <param name="scopeFactory">Factory used to create one dependency injection scope per search.</param>
    /// <param name="options">Execution options: batch count, batch interval and concurrency limit.</param>
    /// <param name="logger">Logger for engine lifecycle and per-search failures.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// An explicit constructor rather than a primary one, because the concurrency limiter
    /// has to be sized from the resolved options value.
    /// </remarks>
    public SearchExecutionBackgroundService(
        ISearchExecutionScheduler scheduler,
        IServiceScopeFactory scopeFactory,
        IOptions<SearchExecutionOptions> options,
        ILogger<SearchExecutionBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentSearches, _options.MaxConcurrentSearches);
    }

    /// <summary>
    /// Waits for searches that are still running, then completes shutdown.
    /// </summary>
    /// <param name="cancellationToken">Token signalling that shutdown may no longer be delayed.</param>
    /// <returns>A task that completes once the engine has stopped.</returns>
    /// <remarks>
    /// <see cref="BackgroundService.StopAsync"/> is awaited first so that the stopping token
    /// is signalled and the reader loop has ended. Only then is it meaningful to wait for
    /// the searches that were already running, which happens under a bounded grace period
    /// linked to the host's own shutdown token.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        Task[] pending = [.. _inFlightSearches.Keys];

        if (pending.Length == 0)
        {
            return;
        }

        LogWaitingForInFlightSearches(pending.Length);

        using CancellationTokenSource graceTokenSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        graceTokenSource.CancelAfter(ShutdownGracePeriod);

        try
        {
            // Individual searches contain their own failures, so this can only end in
            // completion or in the grace period running out.
            await Task.WhenAll(pending).WaitAsync(graceTokenSource.Token).ConfigureAwait(false);

            LogInFlightSearchesFinished();
        }
        catch (OperationCanceledException)
        {
            LogInFlightSearchesAbandoned(_inFlightSearches.Count);
        }
    }

    /// <summary>
    /// Releases the resources owned by the engine.
    /// </summary>
    public override void Dispose()
    {
        _concurrencyLimiter.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Reads scheduled searches and starts each one, up to the configured concurrency limit.
    /// </summary>
    /// <param name="stoppingToken">Token signalled when the host begins shutting down.</param>
    /// <returns>A task that completes when the engine stops accepting work.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogEngineStarted(_options.BatchCount, _options.BatchInterval, _options.MaxConcurrentSearches);

        try
        {
            await foreach (Guid searchId in _scheduler.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // Taking the slot here, on the reader loop, is what bounds concurrency:
                // while the engine is saturated the queue simply stays unread.
                await _concurrencyLimiter.WaitAsync(stoppingToken).ConfigureAwait(false);

                StartSearch(searchId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown: stop reading and let StopAsync drain what is running.
        }

        LogEngineStopped();
    }

    /// <summary>
    /// Starts one search and keeps track of it so that shutdown can wait for it.
    /// </summary>
    /// <param name="searchId">Identifier of the search to execute.</param>
    /// <param name="stoppingToken">Token signalled when the host begins shutting down.</param>
    private void StartSearch(Guid searchId, CancellationToken stoppingToken)
    {
        // Deliberately not awaited: the returned task is the unit of concurrency.
        Task execution = RunSearchAsync(searchId, stoppingToken);

        if (execution.IsCompleted)
        {
            return;
        }

        _inFlightSearches[execution] = 0;

        // Registered after the task was added, so a search that finishes in the meantime
        // still deregisters itself and the dictionary cannot leak completed tasks.
        _ = execution.ContinueWith(
            static (finished, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(finished, out _),
            _inFlightSearches,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Executes a single search: appends every batch on the configured interval and then
    /// marks the search complete.
    /// </summary>
    /// <param name="searchId">Identifier of the search to execute.</param>
    /// <param name="cancellationToken">Token signalled when the host begins shutting down.</param>
    /// <returns>A task that completes when the search has finished, failed, or been abandoned.</returns>
    /// <remarks>
    /// Never faults. Cancellation is treated as an ordinary shutdown outcome, and every other
    /// failure is logged and contained, which is what keeps the engine alive for the other
    /// searches: a broker outage, a handler bug or a missing aggregate must not tear down the
    /// shared loop.
    /// </remarks>
    private async Task RunSearchAsync(Guid searchId, CancellationToken cancellationToken)
    {
        try
        {
            LogSearchExecutionStarted(searchId);

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            for (int batchNumber = 1; batchNumber <= _options.BatchCount; batchNumber++)
            {
                await Task.Delay(_options.BatchInterval, cancellationToken).ConfigureAwait(false);

                await mediator.Send(new AppendSearchBatchCommand(searchId, batchNumber), cancellationToken)
                    .ConfigureAwait(false);
            }

            await mediator.Send(new CompleteSearchCommand(searchId), cancellationToken).ConfigureAwait(false);

            LogSearchExecutionFinished(searchId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogSearchExecutionAbandoned(searchId);
        }
        catch (Exception ex)
        {
            LogSearchExecutionFailed(ex, searchId);
        }
        finally
        {
            ReleaseSlot();
        }
    }

    /// <summary>
    /// Returns the concurrency slot that was taken before the search started.
    /// </summary>
    private void ReleaseSlot()
    {
        try
        {
            _concurrencyLimiter.Release();
        }
        catch (ObjectDisposedException)
        {
            // The host disposed the service while this search was still unwinding. There is
            // no longer a queue to admit anything into, so the slot no longer matters.
        }
    }

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "Search execution engine started. BatchCount={BatchCount} BatchInterval={BatchInterval} MaxConcurrentSearches={MaxConcurrentSearches}")]
    private partial void LogEngineStarted(int batchCount, TimeSpan batchInterval, int maxConcurrentSearches);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Search execution engine stopped accepting work.")]
    private partial void LogEngineStopped();

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Information,
        Message = "Waiting for in-flight searches to finish. InFlightSearches={InFlightSearches}")]
    private partial void LogWaitingForInFlightSearches(int inFlightSearches);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Information,
        Message = "All in-flight searches finished.")]
    private partial void LogInFlightSearchesFinished();

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Warning,
        Message = "Gave up waiting for in-flight searches. InFlightSearches={InFlightSearches}")]
    private partial void LogInFlightSearchesAbandoned(int inFlightSearches);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Debug,
        Message = "Search execution started. SearchId={SearchId}")]
    private partial void LogSearchExecutionStarted(Guid searchId);

    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Debug,
        Message = "Search execution finished. SearchId={SearchId}")]
    private partial void LogSearchExecutionFinished(Guid searchId);

    [LoggerMessage(
        EventId = 2007,
        Level = LogLevel.Information,
        Message = "Search execution abandoned because the service is stopping. SearchId={SearchId}")]
    private partial void LogSearchExecutionAbandoned(Guid searchId);

    [LoggerMessage(
        EventId = 2008,
        Level = LogLevel.Error,
        Message = "Search execution failed. SearchId={SearchId}")]
    private partial void LogSearchExecutionFailed(Exception exception, Guid searchId);
}
