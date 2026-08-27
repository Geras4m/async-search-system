using SearchService.Infrastructure.BackgroundJobs;
using Shouldly;
using Xunit;

namespace UnitTests.BackgroundJobs;

/// <summary>
/// The hand-off between the gRPC request path and the background execution engine. Two
/// properties matter: identifiers come back in the order they were scheduled, and the stream ends
/// on cancellation rather than pinning a host shutdown open forever.
/// </summary>
public sealed class ChannelSearchExecutionSchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ChannelSearchExecutionScheduler _scheduler = new();

    [Fact]
    public async Task ScheduleAsync_OnAnUnboundedQueue_CompletesWithoutSuspending()
    {
        // Act
        ValueTask scheduling = _scheduler.ScheduleAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        scheduling.IsCompletedSuccessfully.ShouldBeTrue(
            "scheduling must never make the gRPC request path wait");

        await scheduling;
    }

    [Fact]
    public async Task ReadAllAsync_AfterSeveralSearchesAreScheduled_YieldsTheirIdsInFifoOrder()
    {
        // Arrange
        Guid[] scheduled = [.. Enumerable.Range(0, 10).Select(_ => Guid.NewGuid())];

        foreach (Guid searchId in scheduled)
        {
            await _scheduler.ScheduleAsync(searchId, CancellationToken.None);
        }

        using var cancellation = new CancellationTokenSource(Timeout);
        List<Guid> observed = [];

        // Act
        await foreach (Guid searchId in _scheduler.ReadAllAsync(cancellation.Token))
        {
            observed.Add(searchId);

            if (observed.Count == scheduled.Length)
            {
                break;
            }
        }

        // Assert
        observed.ShouldBe(scheduled);
    }

    [Fact]
    public async Task ReadAllAsync_WhenAnIdIsScheduledWhileTheReaderWaits_DeliversIt()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(Timeout);
        Guid searchId = Guid.NewGuid();

        Task<Guid> reader = Task.Run(
            async () =>
            {
                await foreach (Guid observed in _scheduler.ReadAllAsync(cancellation.Token))
                {
                    return observed;
                }

                return Guid.Empty;
            },
            CancellationToken.None);

        // Act
        await _scheduler.ScheduleAsync(searchId, CancellationToken.None);

        // Assert
        (await reader).ShouldBe(searchId);
    }

    [Fact]
    public async Task ReadAllAsync_WhenTheTokenIsCancelled_StopsEnumeratingInsteadOfHanging()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();

        Task enumeration = Task.Run(
            async () =>
            {
                await foreach (Guid _ in _scheduler.ReadAllAsync(cancellation.Token))
                {
                    // Nothing is ever scheduled, so the enumerator parks on the empty queue and
                    // only cancellation can bring it back.
                }
            },
            CancellationToken.None);

        // Act
        await cancellation.CancelAsync();

        // Assert
        Task finished = await Task.WhenAny(enumeration, Task.Delay(Timeout, CancellationToken.None));
        finished.ShouldBeSameAs(
            enumeration,
            "a cancelled read loop must unblock so the host can shut down");

        await Should.ThrowAsync<OperationCanceledException>(() => enumeration);
    }
}
