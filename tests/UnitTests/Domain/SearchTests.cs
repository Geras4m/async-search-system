using SearchService.Domain.Entities;
using Shouldly;
using Xunit;

namespace UnitTests.Domain;

/// <summary>
/// The aggregate enforces the two invariants the rest of the system relies on: results only
/// accumulate while a search is running, and completion happens exactly once. Snapshotting is
/// what makes the repository's thread safety possible, so it is pinned here too.
/// </summary>
public sealed class SearchTests
{
    private static readonly DateTime CreatedAtUtc = new(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);
    private static readonly DateTime CompletedAtUtc = new(2026, 3, 14, 9, 27, 23, DateTimeKind.Utc);

    [Fact]
    public void Create_WithAnIdentifierAndTimestamp_StartsIncompleteWithNoResults()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();

        // Act
        var search = Search.Create(searchId, "Paris", CreatedAtUtc);

        // Assert
        search.Id.ShouldBe(searchId);
        search.Destination.ShouldBe("Paris");
        search.CreatedAtUtc.ShouldBe(CreatedAtUtc);
        search.IsCompleted.ShouldBeFalse();
        search.CompletedAtUtc.ShouldBeNull();
        search.Results.ShouldBeEmpty();
    }

    [Fact]
    public void AppendResults_WithSuccessiveBatches_AccumulatesThemInOrder()
    {
        // Arrange
        var search = Search.Create(Guid.NewGuid(), "Paris", CreatedAtUtc);

        // Act
        search.AppendResults([new HotelResult("hotel-a", "Hotel 1", 100m)]);
        search.AppendResults(
        [
            new HotelResult("hotel-b", "Hotel 2", 110m),
            new HotelResult("hotel-c", "Hotel 3", 120m),
        ]);

        // Assert
        search.Results.Count.ShouldBe(3);
        search.Results.Select(hotel => hotel.Name)
            .ShouldBe(new[] { "Hotel 1", "Hotel 2", "Hotel 3" });
    }

    [Fact]
    public void AppendResults_WithAnEmptyBatch_LeavesTheSearchUnchanged()
    {
        // Arrange
        var search = Search.Create(Guid.NewGuid(), "Paris", CreatedAtUtc);

        // Act
        search.AppendResults([]);

        // Assert
        search.Results.ShouldBeEmpty();
    }

    [Fact]
    public void AppendResults_AfterCompletion_ThrowsInvalidOperationException()
    {
        // Arrange
        var search = Search.Create(Guid.NewGuid(), "Paris", CreatedAtUtc);
        search.MarkCompleted(CompletedAtUtc);

        // Act / Assert
        Should.Throw<InvalidOperationException>(
            () => search.AppendResults([new HotelResult("hotel-a", "Hotel 1", 100m)]));

        search.Results.ShouldBeEmpty();
    }

    [Fact]
    public void AppendResults_WithANullBatch_ThrowsArgumentNullException()
    {
        // Arrange
        var search = Search.Create(Guid.NewGuid(), "Paris", CreatedAtUtc);

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => search.AppendResults(null!));
    }

    [Fact]
    public void MarkCompleted_OnARunningSearch_ReturnsTrueAndStampsTheCompletionTime()
    {
        // Arrange
        var search = Search.Create(Guid.NewGuid(), "Paris", CreatedAtUtc);

        // Act
        bool completed = search.MarkCompleted(CompletedAtUtc);

        // Assert
        completed.ShouldBeTrue();
        search.IsCompleted.ShouldBeTrue();
        search.CompletedAtUtc.ShouldBe(CompletedAtUtc);
    }

    [Fact]
    public void MarkCompleted_OnAnAlreadyCompletedSearch_ReturnsFalseAndKeepsTheOriginalTimestamp()
    {
        // Arrange
        var search = Search.Create(Guid.NewGuid(), "Paris", CreatedAtUtc);
        search.MarkCompleted(CompletedAtUtc);

        // Act
        bool completedAgain = search.MarkCompleted(CompletedAtUtc.AddHours(1));

        // Assert
        completedAgain.ShouldBeFalse();
        search.IsCompleted.ShouldBeTrue();
        search.CompletedAtUtc.ShouldBe(CompletedAtUtc);
    }

    [Fact]
    public void CreateSnapshot_AfterTheOriginalChanges_LeavesTheSnapshotUntouched()
    {
        // Arrange
        var search = Search.Create(Guid.NewGuid(), "Paris", CreatedAtUtc);
        search.AppendResults([new HotelResult("hotel-a", "Hotel 1", 100m)]);

        // Act
        Search snapshot = search.CreateSnapshot();
        search.AppendResults([new HotelResult("hotel-b", "Hotel 2", 110m)]);
        search.MarkCompleted(CompletedAtUtc);

        // Assert
        snapshot.ShouldNotBeSameAs(search);
        snapshot.Results.Count.ShouldBe(1);
        snapshot.IsCompleted.ShouldBeFalse();
        snapshot.CompletedAtUtc.ShouldBeNull();
    }

    [Fact]
    public void CreateSnapshot_AfterTheSnapshotChanges_LeavesTheOriginalUntouched()
    {
        // Arrange
        var search = Search.Create(Guid.NewGuid(), "Paris", CreatedAtUtc);
        search.AppendResults([new HotelResult("hotel-a", "Hotel 1", 100m)]);

        // Act
        Search snapshot = search.CreateSnapshot();
        snapshot.AppendResults([new HotelResult("hotel-b", "Hotel 2", 110m)]);
        snapshot.MarkCompleted(CompletedAtUtc);

        // Assert
        search.Results.Count.ShouldBe(1);
        search.IsCompleted.ShouldBeFalse();
        search.CompletedAtUtc.ShouldBeNull();
    }

    [Fact]
    public void CreateSnapshot_OnACompletedSearch_CopiesEveryField()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        var search = Search.Create(searchId, "Paris", CreatedAtUtc);
        search.AppendResults(
        [
            new HotelResult("hotel-a", "Hotel 1", 123.45m),
            new HotelResult("hotel-b", "Hotel 2", 80m),
        ]);
        search.MarkCompleted(CompletedAtUtc);

        // Act
        Search snapshot = search.CreateSnapshot();

        // Assert
        snapshot.Id.ShouldBe(searchId);
        snapshot.CreatedAtUtc.ShouldBe(CreatedAtUtc);
        snapshot.IsCompleted.ShouldBeTrue();
        snapshot.CompletedAtUtc.ShouldBe(CompletedAtUtc);
        snapshot.Results.Count.ShouldBe(2);
        snapshot.Results[0].ShouldBe(new HotelResult("hotel-a", "Hotel 1", 123.45m));
        snapshot.Results[1].ShouldBe(new HotelResult("hotel-b", "Hotel 2", 80m));
    }

    [Fact]
    public void Constructor_WithNullResults_ThrowsArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(
            () => new Search(Guid.NewGuid(), "Paris", CreatedAtUtc, isCompleted: false, completedAtUtc: null, results: null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutADestination_Throws(string? destination)
    {
        Should.Throw<ArgumentException>(
            () => Search.Create(Guid.NewGuid(), destination!, CreatedAtUtc));
    }

    [Fact]
    public void CreateSnapshot_CarriesTheDestination()
    {
        // A snapshot that dropped the destination would silently blank the field on every
        // read, because the repository hands out snapshots rather than stored instances.
        var search = Search.Create(Guid.NewGuid(), "Reykjavik", CreatedAtUtc);

        var snapshot = search.CreateSnapshot();

        snapshot.Destination.ShouldBe("Reykjavik");
    }
}
