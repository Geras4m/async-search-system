using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SearchService.Application.Abstractions;
using SearchService.Application.Commands;
using SearchService.Application.Handlers;
using SearchService.Domain.Entities;
using SearchService.Domain.Repositories;
using Shouldly;
using Xunit;

namespace UnitTests.Handlers;

/// <summary>
/// The start of a search is the only synchronous step of the workflow, so it has to do three
/// things and do them in the right order: create the aggregate, persist it, then hand the
/// identifier to the background engine.
/// </summary>
public sealed class StartSearchCommandHandlerTests
{
    private static readonly DateTime CreatedAtUtc = new(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);

    private readonly ISearchRepository _repository = Substitute.For<ISearchRepository>();
    private readonly ISearchExecutionScheduler _scheduler = Substitute.For<ISearchExecutionScheduler>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly StartSearchCommandHandler _handler;

    public StartSearchCommandHandlerTests()
    {
        _clock.UtcNow.Returns(CreatedAtUtc);

        _handler = new StartSearchCommandHandler(
            _repository,
            _scheduler,
            _clock,
            NullLogger<StartSearchCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WithAValidCommand_ReturnsANonEmptySearchId()
    {
        // Arrange
        var command = new StartSearchCommand("Paris");

        // Act
        StartSearchResult result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.SearchId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Handle_CalledTwice_ReturnsADistinctSearchIdEachTime()
    {
        // Arrange
        var command = new StartSearchCommand("Paris");

        // Act
        StartSearchResult first = await _handler.Handle(command, CancellationToken.None);
        StartSearchResult second = await _handler.Handle(command, CancellationToken.None);

        // Assert
        second.SearchId.ShouldNotBe(first.SearchId);
    }

    [Fact]
    public async Task Handle_WithAValidCommand_PersistsANewIncompleteSearchCarryingTheReturnedId()
    {
        // Arrange
        Search? persisted = null;
        _repository
            .When(repository => repository.CreateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>()))
            .Do(call => persisted = call.Arg<Search>());

        // Act
        StartSearchResult result = await _handler.Handle(new StartSearchCommand("Paris"), CancellationToken.None);

        // Assert
        await _repository.Received(1).CreateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>());

        persisted.ShouldNotBeNull();
        persisted.Id.ShouldBe(result.SearchId);
        persisted.CreatedAtUtc.ShouldBe(CreatedAtUtc);
        persisted.IsCompleted.ShouldBeFalse();
        persisted.CompletedAtUtc.ShouldBeNull();
        persisted.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WithAValidCommand_SchedulesExactlyTheReturnedSearchId()
    {
        // Act
        StartSearchResult result = await _handler.Handle(new StartSearchCommand("Paris"), CancellationToken.None);

        // Assert
        await _scheduler.Received(1).ScheduleAsync(result.SearchId, Arg.Any<CancellationToken>());
        await _scheduler.Received(1).ScheduleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithAValidCommand_PersistsTheSearchBeforeSchedulingIt()
    {
        // Arrange
        // The ordering is the whole point of this test: the background engine dequeues an
        // identifier the instant it is written, so scheduling before the repository knows about
        // the search would let the engine look up a search that does not exist yet.

        // Act
        await _handler.Handle(new StartSearchCommand("Paris"), CancellationToken.None);

        // Assert
        // CA2012 is suppressed for this block only. Inside Received.InOrder the calls are
        // specifications being matched against calls already recorded, not real invocations, so
        // the ValueTask the scheduler spec yields is a placeholder with nothing to await.
#pragma warning disable CA2012
        Received.InOrder(() =>
        {
            _repository.CreateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>());
            _scheduler.ScheduleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        });
#pragma warning restore CA2012
    }

    [Fact]
    public async Task Handle_WithAValidCommand_ForwardsTheCancellationTokenToBothCollaborators()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();

        // Act
        await _handler.Handle(new StartSearchCommand("Paris"), cancellation.Token);

        // Assert
        await _repository.Received(1).CreateAsync(Arg.Any<Search>(), cancellation.Token);
        await _scheduler.Received(1).ScheduleAsync(Arg.Any<Guid>(), cancellation.Token);
    }

    [Fact]
    public async Task Handle_WhenPersistenceFails_DoesNotScheduleTheSearch()
    {
        // Arrange
        _repository
            .When(repository => repository.CreateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("Store unavailable."));

        // Act
        await Should.ThrowAsync<InvalidOperationException>(
            () => _handler.Handle(new StartSearchCommand("Paris"), CancellationToken.None));

        // Assert
        await _scheduler.DidNotReceive().ScheduleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithANullCommand_ThrowsArgumentNullException()
    {
        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(
            () => _handler.Handle(null!, CancellationToken.None));
    }
}
