using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SearchService.Application.Abstractions;
using SearchService.Application.Options;
using SearchService.Infrastructure.BackgroundJobs;
using Shared.EventContracts;
using Shouldly;
using Xunit;

namespace UnitTests.BackgroundJobs;

/// <summary>
/// The retry half of the outbox. It only matters when the broker was unreachable at the moment a
/// search completed, so the properties worth pinning down are the recovery ones: an owed event is
/// delivered and then dropped, a failed delivery stays owed, and a failing broker never takes the
/// service down with it, because the next sweep is the retry.
/// </summary>
public sealed class SearchEventOutboxPublisherBackgroundServiceTests
{
    private static readonly DateTime CompletedAtUtc = new(2026, 3, 14, 9, 27, 23, DateTimeKind.Utc);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WaitPollDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly ISearchEventOutbox _outbox = Substitute.For<ISearchEventOutbox>();
    private readonly ISearchEventsPublisher _publisher = Substitute.For<ISearchEventsPublisher>();
    private readonly List<SearchCompletedEvent> _owed = [];
    private readonly object _gate = new();

    private int _sweepCount;
    private int _publishAttempts;
    private int _removals;
    private bool _publishFails;

    public SearchEventOutboxPublisherBackgroundServiceTests()
    {
        // A stateful stand-in for the real outbox: sweeps see what is genuinely still owed, so a
        // delivery that is not removed really does come back on the next sweep.
        _outbox
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Interlocked.Increment(ref _sweepCount);

                lock (_gate)
                {
                    IReadOnlyList<SearchCompletedEvent> batch = [.. _owed.Take(call.Arg<int>())];
                    return batch;
                }
            });

        _outbox
            .RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Interlocked.Increment(ref _removals);

                lock (_gate)
                {
                    return _owed.RemoveAll(owed => owed.SearchId == call.Arg<Guid>()) > 0;
                }
            });

        _publisher
            .PublishSearchCompletedAsync(Arg.Any<SearchCompletedEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref _publishAttempts);

                return Volatile.Read(ref _publishFails)
                    ? Task.FromException(new InvalidOperationException("Broker unavailable."))
                    : Task.CompletedTask;
            });
    }

    [Fact]
    public async Task ExecuteAsync_WithAnOwedEvent_PublishesItAndRemovesItFromTheOutbox()
    {
        // Arrange
        var owed = new SearchCompletedEvent(Guid.NewGuid(), CompletedAtUtc);
        GivenOwed(owed);

        using SearchEventOutboxPublisherBackgroundService service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);

        try
        {
            bool drained = await WaitForAsync(() => PendingCount == 0);

            // Assert
            drained.ShouldBeTrue("an owed event must be delivered within a few sweeps");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        await _publisher.Received(1).PublishSearchCompletedAsync(owed, Arg.Any<CancellationToken>());
        await _outbox.Received(1).RemoveAsync(owed.SearchId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithSeveralOwedEvents_DrainsThemAll()
    {
        // Arrange
        SearchCompletedEvent[] backlog =
        [
            .. Enumerable.Range(0, 5).Select(_ => new SearchCompletedEvent(Guid.NewGuid(), CompletedAtUtc)),
        ];

        GivenOwed(backlog);

        using SearchEventOutboxPublisherBackgroundService service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);

        try
        {
            bool drained = await WaitForAsync(() => PendingCount == 0);

            // Assert
            drained.ShouldBeTrue("the whole backlog must clear once the broker is healthy");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Volatile.Read(ref _publishAttempts).ShouldBe(backlog.Length);
        Volatile.Read(ref _removals).ShouldBe(backlog.Length);
    }

    [Fact]
    public async Task ExecuteAsync_WhenThePublisherThrows_KeepsTheEventOwedAndRetriesUntilTheBrokerRecovers()
    {
        // Arrange
        // The recovery path the outbox exists for. While the broker is down the entry must stay
        // owed and the service must stay alive; once the broker comes back a later sweep delivers
        // it without anyone replaying the original command.
        var owed = new SearchCompletedEvent(Guid.NewGuid(), CompletedAtUtc);
        GivenOwed(owed);
        Volatile.Write(ref _publishFails, true);

        using SearchEventOutboxPublisherBackgroundService service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);

        try
        {
            bool retried = await WaitForAsync(() => Volatile.Read(ref _publishAttempts) >= 2);

            // Assert: a failed delivery is not a lost one.
            retried.ShouldBeTrue("a failed delivery must be attempted again on the next sweep");
            PendingCount.ShouldBe(1, "an event the broker refused is still owed");
            Volatile.Read(ref _removals).ShouldBe(0, "nothing may be removed before the broker accepts it");

            Task executing = service.ExecuteTask.ShouldNotBeNull();
            executing.IsCompleted.ShouldBeFalse("a failing broker must not stop the service");

            // Act: the broker comes back.
            Volatile.Write(ref _publishFails, false);

            // Assert
            bool recovered = await WaitForAsync(() => PendingCount == 0);
            recovered.ShouldBeTrue("a later sweep must deliver the event once the broker is healthy");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        await _outbox.Received(1).RemoveAsync(owed.SearchId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithAnEmptyOutbox_PublishesNothing()
    {
        // Arrange
        using SearchEventOutboxPublisherBackgroundService service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);

        try
        {
            bool swept = await WaitForAsync(() => Volatile.Read(ref _sweepCount) >= 3);

            // Assert
            swept.ShouldBeTrue("the publisher must keep sweeping even when there is nothing to do");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        await _publisher.DidNotReceive().PublishSearchCompletedAsync(
            Arg.Any<SearchCompletedEvent>(),
            Arg.Any<CancellationToken>());
        await _outbox.DidNotReceive().RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithAConfiguredBatchSize_AsksTheOutboxForThatManyEvents()
    {
        // Arrange
        // The batch size is what bounds the work done while the broker is recovering, so it has to
        // reach the outbox rather than sit unused in the options object.
        const int BatchSize = 7;

        using SearchEventOutboxPublisherBackgroundService service = CreateService(BatchSize);

        // Act
        await service.StartAsync(CancellationToken.None);

        try
        {
            bool swept = await WaitForAsync(() => Volatile.Read(ref _sweepCount) >= 1);

            // Assert
            swept.ShouldBeTrue("the outbox must be swept at least once");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        await _outbox.Received().GetPendingAsync(BatchSize, Arg.Any<CancellationToken>());
        await _outbox.DidNotReceive().GetPendingAsync(
            Arg.Is<int>(maxCount => maxCount != BatchSize),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_WhileTheServiceIsSweeping_EndsItPromptlyWithoutThrowing()
    {
        // Arrange
        using SearchEventOutboxPublisherBackgroundService service = CreateService();

        await service.StartAsync(CancellationToken.None);
        (await WaitForAsync(() => Volatile.Read(ref _sweepCount) >= 1)).ShouldBeTrue();

        // Act
        Task stopping = service.StopAsync(CancellationToken.None);
        Task finished = await Task.WhenAny(stopping, Task.Delay(WaitTimeout, CancellationToken.None));

        // Assert
        finished.ShouldBeSameAs(stopping, "shutdown must not wait on the poll interval indefinitely");
        await Should.NotThrowAsync(() => stopping);

        Task executed = service.ExecuteTask.ShouldNotBeNull();
        executed.IsCompleted.ShouldBeTrue();
        executed.IsFaulted.ShouldBeFalse("cancellation is an expected shutdown, not a failure");
    }

    [Fact]
    public async Task StopAsync_BeforeAnythingWasOwed_LeavesThePublisherUntouched()
    {
        // Arrange
        using SearchEventOutboxPublisherBackgroundService service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // Assert
        await _publisher.DidNotReceive().PublishSearchCompletedAsync(
            Arg.Any<SearchCompletedEvent>(),
            Arg.Any<CancellationToken>());
    }

    private int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _owed.Count;
            }
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        long deadline = Environment.TickCount64 + (long)WaitTimeout.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(WaitPollDelay, CancellationToken.None);
        }

        return condition();
    }

    private SearchEventOutboxPublisherBackgroundService CreateService(int batchSize = 50) =>
        new(
            _outbox,
            _publisher,
            Options.Create(new SearchEventOutboxOptions
            {
                PollInterval = PollInterval,
                BatchSize = batchSize,
            }),
            NullLogger<SearchEventOutboxPublisherBackgroundService>.Instance);

    private void GivenOwed(params SearchCompletedEvent[] events)
    {
        lock (_gate)
        {
            _owed.AddRange(events);
        }
    }
}
