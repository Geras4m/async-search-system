using System.Collections.Concurrent;
using System.Globalization;
using SearchService.Domain.Entities;
using SearchService.Domain.Exceptions;
using SearchService.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// The store the whole workflow rests on. Beyond the obvious round trips, two properties are
/// load bearing and would fail silently if they broke: every aggregate crossing the boundary is
/// a snapshot, and a reader never observes a half-appended batch.
/// </summary>
public sealed class InMemorySearchRepositoryTests
{
    private static readonly DateTime CreatedAtUtc = new(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);
    private static readonly DateTime CompletedAtUtc = new(2026, 3, 14, 9, 27, 23, DateTimeKind.Utc);

    private readonly InMemorySearchRepository _repository = new();

    [Fact]
    public async Task GetAsync_WithAnUnknownId_ReturnsNull()
    {
        // Act
        Search? found = await _repository.GetAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        found.ShouldBeNull();
    }

    [Fact]
    public async Task CreateAsync_ThenGetAsync_ReturnsTheStoredSearch()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();

        // Act
        await _repository.CreateAsync(Search.Create(searchId, "Paris", CreatedAtUtc), CancellationToken.None);
        Search? found = await _repository.GetAsync(searchId, CancellationToken.None);

        // Assert
        found.ShouldNotBeNull();
        found.Id.ShouldBe(searchId);
        found.CreatedAtUtc.ShouldBe(CreatedAtUtc);
        found.IsCompleted.ShouldBeFalse();
        found.CompletedAtUtc.ShouldBeNull();
        found.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ThenGetAsync_ReturnsTheUpdatedState()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        await _repository.CreateAsync(Search.Create(searchId, "Paris", CreatedAtUtc), CancellationToken.None);

        Search? loaded = await _repository.GetAsync(searchId, CancellationToken.None);
        loaded.ShouldNotBeNull();
        loaded.AppendResults([new HotelResult("hotel-a", "Hotel 1", 123.45m)]);
        loaded.MarkCompleted(CompletedAtUtc);

        // Act
        await _repository.UpdateAsync(loaded, CancellationToken.None);
        Search? reloaded = await _repository.GetAsync(searchId, CancellationToken.None);

        // Assert
        reloaded.ShouldNotBeNull();
        reloaded.IsCompleted.ShouldBeTrue();
        reloaded.CompletedAtUtc.ShouldBe(CompletedAtUtc);
        reloaded.Results.Count.ShouldBe(1);
        reloaded.Results[0].ShouldBe(new HotelResult("hotel-a", "Hotel 1", 123.45m));
    }

    [Fact]
    public async Task CreateAsync_WithADuplicateId_ThrowsInvalidOperationException()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        await _repository.CreateAsync(Search.Create(searchId, "Paris", CreatedAtUtc), CancellationToken.None);

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => _repository.CreateAsync(Search.Create(searchId, "Paris", CreatedAtUtc), CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithADuplicateId_LeavesTheExistingSearchUntouched()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        var original = Search.Create(searchId, "Paris", CreatedAtUtc);
        original.AppendResults([new HotelResult("hotel-a", "Hotel 1", 123.45m)]);
        await _repository.CreateAsync(original, CancellationToken.None);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(
            () => _repository.CreateAsync(Search.Create(searchId, "Paris", CreatedAtUtc), CancellationToken.None));

        // Assert
        Search? stored = await _repository.GetAsync(searchId, CancellationToken.None);
        stored.ShouldNotBeNull();
        stored.Results.Count.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateAsync_WithAnUnknownId_ThrowsSearchNotFoundException()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();

        // Act
        SearchNotFoundException exception = await Should.ThrowAsync<SearchNotFoundException>(
            () => _repository.UpdateAsync(Search.Create(searchId, "Paris", CreatedAtUtc), CancellationToken.None));

        // Assert
        exception.SearchId.ShouldBe(searchId);
    }

    [Fact]
    public async Task CreateAsync_WithANullSearch_ThrowsArgumentNullException()
    {
        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(
            () => _repository.CreateAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAsync_WithANullSearch_ThrowsArgumentNullException()
    {
        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(
            () => _repository.UpdateAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_WithAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(
            () => _repository.GetAsync(Guid.NewGuid(), cancellation.Token));
    }

    [Fact]
    public async Task GetAsync_WhenTheReturnedAggregateIsMutated_DoesNotChangeWhatTheStoreReturnsNext()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        await _repository.CreateAsync(Search.Create(searchId, "Paris", CreatedAtUtc), CancellationToken.None);

        Search? firstRead = await _repository.GetAsync(searchId, CancellationToken.None);
        firstRead.ShouldNotBeNull();

        // Act
        firstRead.AppendResults([new HotelResult("hotel-a", "Hotel 1", 123.45m)]);
        firstRead.MarkCompleted(CompletedAtUtc);

        // Assert
        Search? secondRead = await _repository.GetAsync(searchId, CancellationToken.None);
        secondRead.ShouldNotBeNull();
        secondRead.Results.ShouldBeEmpty();
        secondRead.IsCompleted.ShouldBeFalse();
        secondRead.CompletedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_CalledTwice_ReturnsTwoIndependentInstances()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        await _repository.CreateAsync(Search.Create(searchId, "Paris", CreatedAtUtc), CancellationToken.None);

        // Act
        Search? first = await _repository.GetAsync(searchId, CancellationToken.None);
        Search? second = await _repository.GetAsync(searchId, CancellationToken.None);

        // Assert
        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        second.ShouldNotBeSameAs(first);

        first.AppendResults([new HotelResult("hotel-a", "Hotel 1", 123.45m)]);
        second.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenTheSuppliedAggregateIsMutatedAfterwards_DoesNotChangeTheStoredState()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        var search = Search.Create(searchId, "Paris", CreatedAtUtc);
        await _repository.CreateAsync(search, CancellationToken.None);

        // Act
        search.AppendResults([new HotelResult("hotel-a", "Hotel 1", 123.45m)]);
        search.MarkCompleted(CompletedAtUtc);

        // Assert
        Search? stored = await _repository.GetAsync(searchId, CancellationToken.None);
        stored.ShouldNotBeNull();
        stored.Results.ShouldBeEmpty();
        stored.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateAsync_WhenTheSuppliedAggregateIsMutatedAfterwards_DoesNotChangeTheStoredState()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        await _repository.CreateAsync(Search.Create(searchId, "Paris", CreatedAtUtc), CancellationToken.None);

        Search? loaded = await _repository.GetAsync(searchId, CancellationToken.None);
        loaded.ShouldNotBeNull();
        loaded.AppendResults([new HotelResult("hotel-a", "Hotel 1", 123.45m)]);
        await _repository.UpdateAsync(loaded, CancellationToken.None);

        // Act
        loaded.AppendResults([new HotelResult("hotel-b", "Hotel 2", 200m)]);
        loaded.MarkCompleted(CompletedAtUtc);

        // Assert
        Search? stored = await _repository.GetAsync(searchId, CancellationToken.None);
        stored.ShouldNotBeNull();
        stored.Results.Count.ShouldBe(1);
        stored.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_WhileAnotherWriterAppendsBatches_NeverObservesAPartiallyAppliedBatch()
    {
        // Arrange
        // This is the race the snapshotting exists for: the background engine appends a batch to
        // one search while gRPC callers poll the very same search. A reader must always see the
        // state before a batch or the state after it, so an observed result count that is not a
        // whole number of batches would mean a torn read.
        const int hotelsPerBatch = 5;
        const int batchCount = 400;

        Guid searchId = Guid.NewGuid();
        await _repository.CreateAsync(Search.Create(searchId, "Paris", CreatedAtUtc), CancellationToken.None);

        var observedCounts = new ConcurrentQueue<int>();

        Task writer = Task.Run(async () =>
        {
            for (int batchNumber = 1; batchNumber <= batchCount; batchNumber++)
            {
                Search? search = await _repository.GetAsync(searchId, CancellationToken.None);
                search!.AppendResults(CreateBatch(batchNumber, hotelsPerBatch));
                await _repository.UpdateAsync(search, CancellationToken.None);
            }
        });

        Task reader = Task.Run(async () =>
        {
            do
            {
                Search? search = await _repository.GetAsync(searchId, CancellationToken.None);

                // Enumerating rather than reading Count is deliberate: a live List<T> being
                // appended to under an active enumerator is exactly what throws.
                int observed = 0;
                foreach (HotelResult _ in search!.Results)
                {
                    observed++;
                }

                observedCounts.Enqueue(observed);
            }
            while (!writer.IsCompleted);
        });

        // Act
        await Should.NotThrowAsync(() => Task.WhenAll(writer, reader));

        // Assert
        observedCounts.ShouldNotBeEmpty();
        observedCounts.ShouldAllBe(observed => observed % hotelsPerBatch == 0);

        Search? finalState = await _repository.GetAsync(searchId, CancellationToken.None);
        finalState.ShouldNotBeNull();
        finalState.Results.Count.ShouldBe(batchCount * hotelsPerBatch);
    }

    [Fact]
    public async Task CreateAsync_WhenManySearchesAreCreatedConcurrently_StoresEveryOneOfThem()
    {
        // Arrange
        Guid[] searchIds = [.. Enumerable.Range(0, 200).Select(_ => Guid.NewGuid())];

        // Act
        await Task.WhenAll(searchIds.Select(searchId =>
            _repository.CreateAsync(Search.Create(searchId, "Paris", CreatedAtUtc), CancellationToken.None)));

        // Assert
        foreach (Guid searchId in searchIds)
        {
            Search? stored = await _repository.GetAsync(searchId, CancellationToken.None);
            stored.ShouldNotBeNull();
            stored.Id.ShouldBe(searchId);
        }
    }

    private static IReadOnlyList<HotelResult> CreateBatch(int batchNumber, int hotelsPerBatch)
    {
        int firstHotelNumber = ((batchNumber - 1) * hotelsPerBatch) + 1;

        return
        [
            .. Enumerable.Range(firstHotelNumber, hotelsPerBatch).Select(static hotelNumber =>
                new HotelResult(
                    HotelId: string.Create(CultureInfo.InvariantCulture, $"hotel-{hotelNumber}"),
                    Name: string.Create(CultureInfo.InvariantCulture, $"Hotel {hotelNumber}"),
                    Price: 100m + hotelNumber)),
        ];
    }

    [Fact]
    public async Task GetAsync_ReturnsTheDestinationTheSearchWasCreatedWith()
    {
        Guid searchId = Guid.NewGuid();
        await _repository.CreateAsync(Search.Create(searchId, "Reykjavik", CreatedAtUtc), CancellationToken.None);

        var loaded = await _repository.GetAsync(searchId, CancellationToken.None);

        loaded.ShouldNotBeNull();
        loaded.Destination.ShouldBe("Reykjavik");
    }
}
