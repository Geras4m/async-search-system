using SearchService.Infrastructure.Persistence;
using Shared.EventContracts;
using Shouldly;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// The record of what the broker still owes the rest of the system. Beyond the round trip, three
/// properties are load bearing: oldest events are handed back first so a backlog drains in order,
/// re-recording an event that is already owed neither duplicates it nor pushes it behind newer
/// ones, and concurrent completions never lose an entry.
/// </summary>
public sealed class InMemorySearchEventOutboxTests
{
    private static readonly DateTime CompletedAtUtc = new(2026, 3, 14, 9, 27, 23, DateTimeKind.Utc);

    private readonly InMemorySearchEventOutbox _outbox = new();

    [Fact]
    public void PendingCount_OnAFreshOutbox_IsZero()
    {
        // Assert
        _outbox.PendingCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetPendingAsync_OnAFreshOutbox_ReturnsNothing()
    {
        // Act
        IReadOnlyList<SearchCompletedEvent> pending = await _outbox.GetPendingAsync(10, CancellationToken.None);

        // Assert
        pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnqueueAsync_ThenGetPendingAsync_ThenRemoveAsync_CompletesTheRoundTrip()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        var owed = new SearchCompletedEvent(searchId, CompletedAtUtc);

        // Act
        await _outbox.EnqueueAsync(owed, CancellationToken.None);

        IReadOnlyList<SearchCompletedEvent> pending = await _outbox.GetPendingAsync(10, CancellationToken.None);
        bool removed = await _outbox.RemoveAsync(searchId, CancellationToken.None);

        IReadOnlyList<SearchCompletedEvent> afterRemoval =
            await _outbox.GetPendingAsync(10, CancellationToken.None);

        // Assert
        pending.ShouldHaveSingleItem().ShouldBe(owed);
        removed.ShouldBeTrue();
        afterRemoval.ShouldBeEmpty();
        _outbox.PendingCount.ShouldBe(0);
    }

    [Fact]
    public async Task EnqueueAsync_WithANullEvent_ThrowsArgumentNullException()
    {
        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await _outbox.EnqueueAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task GetPendingAsync_WithSeveralOwedEvents_ReturnsThemOldestFirst()
    {
        // Arrange
        // Deliberately descending timestamps: ordering follows the enqueue sequence, not the clock,
        // so two searches completing inside the same tick still drain in a defined order.
        Guid[] searchIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        for (int index = 0; index < searchIds.Length; index++)
        {
            await _outbox.EnqueueAsync(
                new SearchCompletedEvent(searchIds[index], CompletedAtUtc.AddSeconds(-index)),
                CancellationToken.None);
        }

        // Act
        IReadOnlyList<SearchCompletedEvent> pending = await _outbox.GetPendingAsync(10, CancellationToken.None);

        // Assert
        pending.Select(owed => owed.SearchId).ShouldBe(searchIds);
    }

    [Fact]
    public async Task GetPendingAsync_WithAMaxCountBelowTheBacklog_ReturnsOnlyThatManyOldestEvents()
    {
        // Arrange
        Guid[] searchIds = [.. Enumerable.Range(0, 5).Select(_ => Guid.NewGuid())];

        foreach (Guid searchId in searchIds)
        {
            await _outbox.EnqueueAsync(new SearchCompletedEvent(searchId, CompletedAtUtc), CancellationToken.None);
        }

        // Act
        IReadOnlyList<SearchCompletedEvent> batch = await _outbox.GetPendingAsync(2, CancellationToken.None);

        // Assert
        batch.Select(owed => owed.SearchId).ShouldBe(searchIds.Take(2));
        _outbox.PendingCount.ShouldBe(
            5,
            "reading a batch is not a removal; entries stay owed until the broker accepts them");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task GetPendingAsync_WithAMaxCountBelowOne_ThrowsArgumentOutOfRangeException(int maxCount)
    {
        // Arrange
        await _outbox.EnqueueAsync(
            new SearchCompletedEvent(Guid.NewGuid(), CompletedAtUtc),
            CancellationToken.None);

        // Act / Assert
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await _outbox.GetPendingAsync(maxCount, CancellationToken.None));
    }

    [Fact]
    public async Task EnqueueAsync_WithASearchIdThatIsAlreadyOwed_DoesNotRecordItTwice()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        var owed = new SearchCompletedEvent(searchId, CompletedAtUtc);

        // Act
        // A retried CompleteSearchCommand must not queue a second delivery for the same search.
        await _outbox.EnqueueAsync(owed, CancellationToken.None);
        await _outbox.EnqueueAsync(owed, CancellationToken.None);

        // Assert
        _outbox.PendingCount.ShouldBe(1);

        IReadOnlyList<SearchCompletedEvent> pending = await _outbox.GetPendingAsync(10, CancellationToken.None);
        pending.ShouldHaveSingleItem().ShouldBe(owed);
    }

    [Fact]
    public async Task EnqueueAsync_WithASearchIdThatIsAlreadyOwed_LeavesItWhereItWasInTheQueue()
    {
        // Arrange
        // The starvation guard: an entry the broker keeps rejecting would otherwise be re-enqueued
        // to the back of the queue on every retry and permanently jump ahead of, or behind, the
        // rest of the backlog. Its original position and its original payload both stand.
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid third = Guid.NewGuid();

        await _outbox.EnqueueAsync(new SearchCompletedEvent(first, CompletedAtUtc), CancellationToken.None);
        await _outbox.EnqueueAsync(new SearchCompletedEvent(second, CompletedAtUtc), CancellationToken.None);
        await _outbox.EnqueueAsync(new SearchCompletedEvent(third, CompletedAtUtc), CancellationToken.None);

        // Act
        await _outbox.EnqueueAsync(
            new SearchCompletedEvent(first, CompletedAtUtc.AddHours(1)),
            CancellationToken.None);

        // Assert
        IReadOnlyList<SearchCompletedEvent> pending = await _outbox.GetPendingAsync(10, CancellationToken.None);

        pending.Select(owed => owed.SearchId).ShouldBe([first, second, third]);
        pending[0].CompletedAtUtc.ShouldBe(
            CompletedAtUtc,
            "the first recording of an event is the one that stands");
        _outbox.PendingCount.ShouldBe(3);
    }

    [Fact]
    public async Task RemoveAsync_WithAnUnknownSearchId_ReturnsFalse()
    {
        // Arrange
        await _outbox.EnqueueAsync(
            new SearchCompletedEvent(Guid.NewGuid(), CompletedAtUtc),
            CancellationToken.None);

        // Act
        bool removed = await _outbox.RemoveAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        removed.ShouldBeFalse();
        _outbox.PendingCount.ShouldBe(1);
    }

    [Fact]
    public async Task RemoveAsync_CalledTwiceForTheSameSearchId_ReportsTheSecondCallAsANoOp()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        await _outbox.EnqueueAsync(new SearchCompletedEvent(searchId, CompletedAtUtc), CancellationToken.None);

        // Act
        bool first = await _outbox.RemoveAsync(searchId, CancellationToken.None);
        bool second = await _outbox.RemoveAsync(searchId, CancellationToken.None);

        // Assert
        first.ShouldBeTrue();
        second.ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveAsync_WithOneOfSeveralOwedEvents_LeavesTheOthersInOrder()
    {
        // Arrange
        Guid[] searchIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        foreach (Guid searchId in searchIds)
        {
            await _outbox.EnqueueAsync(new SearchCompletedEvent(searchId, CompletedAtUtc), CancellationToken.None);
        }

        // Act
        await _outbox.RemoveAsync(searchIds[1], CancellationToken.None);

        // Assert
        IReadOnlyList<SearchCompletedEvent> pending = await _outbox.GetPendingAsync(10, CancellationToken.None);

        pending.Select(owed => owed.SearchId).ShouldBe([searchIds[0], searchIds[2]]);
        _outbox.PendingCount.ShouldBe(2);
    }

    [Fact]
    public async Task PendingCount_AcrossEnqueuesAndRemovals_TracksTheOutstandingDeliveries()
    {
        // Arrange
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        // Act / Assert
        await _outbox.EnqueueAsync(new SearchCompletedEvent(first, CompletedAtUtc), CancellationToken.None);
        _outbox.PendingCount.ShouldBe(1);

        await _outbox.EnqueueAsync(new SearchCompletedEvent(second, CompletedAtUtc), CancellationToken.None);
        _outbox.PendingCount.ShouldBe(2);

        await _outbox.EnqueueAsync(new SearchCompletedEvent(first, CompletedAtUtc), CancellationToken.None);
        _outbox.PendingCount.ShouldBe(2, "a duplicate is not a second obligation");

        await _outbox.RemoveAsync(first, CancellationToken.None);
        _outbox.PendingCount.ShouldBe(1);

        await _outbox.RemoveAsync(second, CancellationToken.None);
        _outbox.PendingCount.ShouldBe(0);
    }

    [Fact]
    public async Task EnqueueAsync_FromManyThreadsAtOnce_RecordsEveryDistinctEvent()
    {
        // Arrange
        // Searches complete on the background execution engine, so several completions can record
        // their obligation at the same moment. Losing one there is exactly the silent hole the
        // outbox exists to prevent.
        Guid[] searchIds = [.. Enumerable.Range(0, 512).Select(_ => Guid.NewGuid())];

        // Act
        await Parallel.ForEachAsync(
            searchIds,
            CancellationToken.None,
            (searchId, token) => _outbox.EnqueueAsync(new SearchCompletedEvent(searchId, CompletedAtUtc), token));

        // Assert
        _outbox.PendingCount.ShouldBe(searchIds.Length);

        IReadOnlyList<SearchCompletedEvent> pending =
            await _outbox.GetPendingAsync(searchIds.Length, CancellationToken.None);

        pending.Select(owed => owed.SearchId).OrderBy(id => id).ShouldBe(searchIds.OrderBy(id => id));
    }

    [Fact]
    public async Task EnqueueAsync_WithAnAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _outbox.EnqueueAsync(
                new SearchCompletedEvent(Guid.NewGuid(), CompletedAtUtc),
                cancellation.Token));

        _outbox.PendingCount.ShouldBe(0);
    }
}
