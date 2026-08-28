using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SearchService.Application.Abstractions;
using SearchService.Application.Commands;
using SearchService.Application.Handlers;
using SearchService.Domain.Entities;
using SearchService.Domain.Exceptions;
using SearchService.Domain.Repositories;
using Shouldly;
using Xunit;

namespace UnitTests.Handlers;

/// <summary>
/// Appending a batch is the step that runs six times per search. It must add the generated batch
/// to whatever the search already holds and persist the result, never replace what came before.
/// </summary>
public sealed class AppendSearchBatchCommandHandlerTests
{
    private const int HotelsPerBatch = 5;

    private static readonly DateTime CreatedAtUtc = new(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);

    private readonly ISearchRepository _repository = Substitute.For<ISearchRepository>();
    private readonly IHotelResultGenerator _generator = Substitute.For<IHotelResultGenerator>();
    private readonly AppendSearchBatchCommandHandler _handler;

    private Search? _persisted;

    public AppendSearchBatchCommandHandlerTests()
    {
        _generator
            .GenerateBatch(Arg.Any<int>())
            .Returns(call => CreateBatch(call.Arg<int>()));

        _handler = new AppendSearchBatchCommandHandler(
            _repository,
            _generator,
            NullLogger<AppendSearchBatchCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WithAnExistingSearch_AppendsTheGeneratedBatchAndPersistsIt()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));

        // Act
        await _handler.Handle(new AppendSearchBatchCommand(searchId, 1), CancellationToken.None);

        // Assert
        _generator.Received(1).GenerateBatch(1);
        await _repository.Received(1).UpdateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>());

        _persisted.ShouldNotBeNull();
        _persisted.Id.ShouldBe(searchId);
        _persisted.Results.Select(hotel => hotel.Name)
            .ShouldBe(new[] { "Hotel 1", "Hotel 2", "Hotel 3", "Hotel 4", "Hotel 5" });
    }

    [Fact]
    public async Task Handle_ForSuccessiveBatches_AccumulatesResultsInsteadOfReplacingThem()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));

        // Act
        for (int batchNumber = 1; batchNumber <= 3; batchNumber++)
        {
            await _handler.Handle(new AppendSearchBatchCommand(searchId, batchNumber), CancellationToken.None);
        }

        // Assert
        _persisted.ShouldNotBeNull();
        _persisted.Results.Count.ShouldBe(3 * HotelsPerBatch);
        _persisted.Results[0].Name.ShouldBe("Hotel 1");
        _persisted.Results[^1].Name.ShouldBe("Hotel 15");
        _persisted.Results.Select(hotel => hotel.HotelId).Distinct(StringComparer.Ordinal)
            .Count().ShouldBe(3 * HotelsPerBatch);
    }

    [Fact]
    public async Task Handle_ForTheSixthBatch_AsksTheGeneratorForThatExactBatchNumber()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));

        // Act
        await _handler.Handle(new AppendSearchBatchCommand(searchId, 6), CancellationToken.None);

        // Assert
        _generator.Received(1).GenerateBatch(6);
        _generator.DidNotReceive().GenerateBatch(Arg.Is<int>(batchNumber => batchNumber != 6));

        _persisted.ShouldNotBeNull();
        _persisted.Results.Select(hotel => hotel.Name)
            .ShouldBe(new[] { "Hotel 26", "Hotel 27", "Hotel 28", "Hotel 29", "Hotel 30" });
    }

    [Fact]
    public async Task Handle_WithAnUnknownSearchId_ThrowsSearchNotFoundException()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        _repository.GetAsync(searchId, Arg.Any<CancellationToken>()).Returns((Search?)null);

        // Act
        SearchNotFoundException exception = await Should.ThrowAsync<SearchNotFoundException>(
            () => _handler.Handle(new AppendSearchBatchCommand(searchId, 1), CancellationToken.None));

        // Assert
        exception.SearchId.ShouldBe(searchId);
        _generator.DidNotReceive().GenerateBatch(Arg.Any<int>());
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithAnAlreadyCompletedSearch_ThrowsInvalidOperationExceptionAndPersistsNothing()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        var completed = Search.Create(searchId, "Paris", CreatedAtUtc);
        completed.MarkCompleted(CreatedAtUtc.AddSeconds(30));
        GivenStoredSearch(completed);

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => _handler.Handle(new AppendSearchBatchCommand(searchId, 1), CancellationToken.None));

        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithAValidCommand_ForwardsTheCancellationTokenToTheRepository()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));
        using var cancellation = new CancellationTokenSource();

        // Act
        await _handler.Handle(new AppendSearchBatchCommand(searchId, 1), cancellation.Token);

        // Assert
        await _repository.Received(1).GetAsync(searchId, cancellation.Token);
        await _repository.Received(1).UpdateAsync(Arg.Any<Search>(), cancellation.Token);
    }

    [Fact]
    public async Task Handle_WithANullCommand_ThrowsArgumentNullException()
    {
        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(
            () => _handler.Handle(null!, CancellationToken.None));
    }

    /// <summary>
    /// Makes the repository substitute behave like the real one: reads hand out an independent
    /// snapshot and writes replace the stored state, so accumulation across batches is exercised
    /// rather than assumed.
    /// </summary>
    private void GivenStoredSearch(Search search)
    {
        Search stored = search.CreateSnapshot();

        _repository
            .GetAsync(search.Id, Arg.Any<CancellationToken>())
            .Returns(_ => stored.CreateSnapshot());

        _repository
            .When(repository => repository.UpdateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                stored = call.Arg<Search>().CreateSnapshot();
                _persisted = stored;
            });
    }

    private static IReadOnlyList<HotelResult> CreateBatch(int batchNumber)
    {
        int firstHotelNumber = ((batchNumber - 1) * HotelsPerBatch) + 1;

        return
        [
            .. Enumerable.Range(firstHotelNumber, HotelsPerBatch).Select(static hotelNumber =>
                new HotelResult(
                    HotelId: Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                    Name: string.Create(CultureInfo.InvariantCulture, $"Hotel {hotelNumber}"),
                    Price: 100m + hotelNumber)),
        ];
    }
}
