using NSubstitute;
using SearchService.Application.Handlers;
using SearchService.Application.Models;
using SearchService.Application.Queries;
using SearchService.Domain.Entities;
using SearchService.Domain.Repositories;
using Shouldly;
using Xunit;

namespace UnitTests.Handlers;

/// <summary>
/// The read side of the workflow. A missing search must be distinguishable from a search that
/// simply has no results yet, and every field — prices included — has to survive the projection
/// onto the transport-neutral DTO.
/// </summary>
public sealed class GetSearchResultsQueryHandlerTests
{
    private static readonly DateTime CreatedAtUtc = new(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);

    private readonly ISearchRepository _repository = Substitute.For<ISearchRepository>();
    private readonly GetSearchResultsQueryHandler _handler;

    public GetSearchResultsQueryHandlerTests()
    {
        _handler = new GetSearchResultsQueryHandler(_repository);
    }

    [Fact]
    public async Task Handle_WithAnUnknownSearchId_ReturnsNull()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        _repository.GetAsync(searchId, Arg.Any<CancellationToken>()).Returns((Search?)null);

        // Act
        SearchResultsDto? result = await _handler.Handle(
            new GetSearchResultsQuery(searchId),
            CancellationToken.None);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_WithABrandNewSearch_ReturnsAnEmptyResultListAndIsCompletedFalse()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        _repository
            .GetAsync(searchId, Arg.Any<CancellationToken>())
            .Returns(Search.Create(searchId, "Paris", CreatedAtUtc));

        // Act
        SearchResultsDto? result = await _handler.Handle(
            new GetSearchResultsQuery(searchId),
            CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.SearchId.ShouldBe(searchId);
        result.IsCompleted.ShouldBeFalse();
        result.CreatedAtUtc.ShouldBe(CreatedAtUtc);
        result.Results.ShouldNotBeNull();
        result.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WithAnExistingSearch_MapsEveryFieldIncludingPrices()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        var search = Search.Create(searchId, "Paris", CreatedAtUtc);
        search.AppendResults(
        [
            new HotelResult("hotel-a", "Hotel 1", 123.45m),
            new HotelResult("hotel-b", "Hotel 2", 80m),
            new HotelResult("hotel-c", "Hotel 3", 399.99m),
        ]);

        _repository.GetAsync(searchId, Arg.Any<CancellationToken>()).Returns(search);

        // Act
        SearchResultsDto? result = await _handler.Handle(
            new GetSearchResultsQuery(searchId),
            CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.SearchId.ShouldBe(searchId);
        result.CreatedAtUtc.ShouldBe(CreatedAtUtc);
        result.IsCompleted.ShouldBeFalse();

        result.Results.Count.ShouldBe(3);
        result.Results.ShouldBe(new[]
        {
            new HotelResultDto("hotel-a", "Hotel 1", 123.45m),
            new HotelResultDto("hotel-b", "Hotel 2", 80m),
            new HotelResultDto("hotel-c", "Hotel 3", 399.99m),
        });
    }

    [Fact]
    public async Task Handle_WithACompletedSearch_ReturnsIsCompletedTrue()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        var search = Search.Create(searchId, "Paris", CreatedAtUtc);
        search.AppendResults([new HotelResult("hotel-a", "Hotel 1", 100m)]);
        search.MarkCompleted(CreatedAtUtc.AddSeconds(30));

        _repository.GetAsync(searchId, Arg.Any<CancellationToken>()).Returns(search);

        // Act
        SearchResultsDto? result = await _handler.Handle(
            new GetSearchResultsQuery(searchId),
            CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.IsCompleted.ShouldBeTrue();
        result.Results.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_WithAnExistingSearch_PreservesTheOrderResultsWereAppendedIn()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        var search = Search.Create(searchId, "Paris", CreatedAtUtc);
        search.AppendResults([new HotelResult("hotel-a", "Hotel 1", 100m)]);
        search.AppendResults([new HotelResult("hotel-b", "Hotel 2", 110m)]);
        search.AppendResults([new HotelResult("hotel-c", "Hotel 3", 120m)]);

        _repository.GetAsync(searchId, Arg.Any<CancellationToken>()).Returns(search);

        // Act
        SearchResultsDto? result = await _handler.Handle(
            new GetSearchResultsQuery(searchId),
            CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.Results.Select(hotel => hotel.Name)
            .ShouldBe(new[] { "Hotel 1", "Hotel 2", "Hotel 3" });
    }

    [Fact]
    public async Task Handle_WithAValidQuery_ForwardsTheCancellationTokenToTheRepository()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        _repository
            .GetAsync(searchId, Arg.Any<CancellationToken>())
            .Returns(Search.Create(searchId, "Paris", CreatedAtUtc));

        using var cancellation = new CancellationTokenSource();

        // Act
        await _handler.Handle(new GetSearchResultsQuery(searchId), cancellation.Token);

        // Assert
        await _repository.Received(1).GetAsync(searchId, cancellation.Token);
    }

    [Fact]
    public async Task Handle_WithANullQuery_ThrowsArgumentNullException()
    {
        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(
            () => _handler.Handle(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_ProjectsTheDestinationOntoTheResult()
    {
        Guid searchId = Guid.NewGuid();
        _repository
            .GetAsync(searchId, Arg.Any<CancellationToken>())
            .Returns(Search.Create(searchId, "Reykjavik", CreatedAtUtc));

        var result = await _handler.Handle(new GetSearchResultsQuery(searchId), CancellationToken.None);

        result.ShouldNotBeNull();
        result.Destination.ShouldBe("Reykjavik");
    }
}
