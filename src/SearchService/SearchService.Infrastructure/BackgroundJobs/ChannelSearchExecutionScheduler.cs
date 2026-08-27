using System.Threading.Channels;
using SearchService.Application.Abstractions;

namespace SearchService.Infrastructure.BackgroundJobs;

/// <summary>
/// In-process <see cref="ISearchExecutionScheduler"/> built on an unbounded
/// <see cref="Channel{T}"/> of search identifiers.
/// </summary>
/// <remarks>
/// <para>
/// The channel is the hand-off between the gRPC request path, which must return a
/// <c>SearchId</c> immediately, and <c>SearchExecutionBackgroundService</c>, which does the
/// slow work. Writing an identifier costs a single enqueue, so <c>StartSearch</c> never
/// waits on execution.
/// </para>
/// <para>
/// The channel is unbounded and configured with <c>SingleReader = true</c> and
/// <c>SingleWriter = false</c>: exactly one background service drains it, while any number
/// of concurrent gRPC calls write to it. Declaring those constraints lets the channel pick
/// its cheapest internal implementation. Unbounded means scheduling never blocks or drops a
/// search; back pressure is applied instead by the engine's own concurrency limit, so the
/// queue only grows when searches arrive faster than they can be run.
/// </para>
/// <para>
/// Registered as a singleton so that the writer side and the reader side share one queue.
/// Being in-process, the queue is not durable: identifiers still queued when the process
/// stops are lost. A durable store or a broker-backed queue would replace this class
/// without any change to the Application layer, which only knows the interface.
/// </para>
/// </remarks>
public sealed class ChannelSearchExecutionScheduler : ISearchExecutionScheduler
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    /// <summary>
    /// Schedules a search for asynchronous execution.
    /// </summary>
    /// <param name="searchId">Identifier of the search to execute.</param>
    /// <param name="cancellationToken">Token used to cancel the scheduling operation.</param>
    /// <returns>A task that completes once the search has been accepted for execution.</returns>
    /// <remarks>
    /// Completes synchronously in practice: an unbounded channel always has room, so the
    /// returned <see cref="ValueTask"/> is already completed and allocates nothing.
    /// </remarks>
    public ValueTask ScheduleAsync(Guid searchId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(searchId, cancellationToken);

    /// <summary>
    /// Streams scheduled search identifiers until cancellation.
    /// </summary>
    /// <param name="cancellationToken">Token that stops the stream, normally host shutdown.</param>
    /// <returns>An asynchronous stream of scheduled search identifiers.</returns>
    /// <remarks>
    /// The stream is intended for a single consumer. Enumerating it from two places would
    /// violate the <c>SingleReader</c> contract the channel was created with.
    /// </remarks>
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
