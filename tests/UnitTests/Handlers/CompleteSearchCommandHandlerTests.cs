using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SearchService.Application.Abstractions;
using SearchService.Application.Commands;
using SearchService.Application.Handlers;
using SearchService.Domain.Entities;
using SearchService.Domain.Exceptions;
using SearchService.Domain.Repositories;
using Shared.EventContracts;
using Shouldly;
using Xunit;

namespace UnitTests.Handlers;

/// <summary>
/// Completion is the step the rest of the system reacts to. It has to flip the flag, persist
/// that fact before announcing it, and announce it exactly once no matter how often the command
/// is replayed.
/// </summary>
public sealed class CompleteSearchCommandHandlerTests
{
    private static readonly DateTime CreatedAtUtc = new(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);
    private static readonly DateTime CompletedAtUtc = new(2026, 3, 14, 9, 27, 23, DateTimeKind.Utc);

    private readonly ISearchRepository _repository = Substitute.For<ISearchRepository>();
    private readonly ISearchEventsPublisher _publisher = Substitute.For<ISearchEventsPublisher>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly CompleteSearchCommandHandler _handler;

    private Search? _persisted;

    public CompleteSearchCommandHandlerTests()
    {
        _clock.UtcNow.Returns(CompletedAtUtc);

        _handler = new CompleteSearchCommandHandler(
            _repository,
            _publisher,
            _clock,
            NullLogger<CompleteSearchCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WithARunningSearch_PersistsItAsCompletedWithTheClockTimestamp()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, CreatedAtUtc));

        // Act
        await _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None);

        // Assert
        await _repository.Received(1).UpdateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>());

        _persisted.ShouldNotBeNull();
        _persisted.Id.ShouldBe(searchId);
        _persisted.IsCompleted.ShouldBeTrue();
        _persisted.CompletedAtUtc.ShouldBe(CompletedAtUtc);
    }

    [Fact]
    public async Task Handle_WithARunningSearch_PublishesExactlyOneEventCarryingTheSearchId()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, CreatedAtUtc));

        SearchCompletedEvent? published = null;
        _publisher
            .When(publisher => publisher.PublishSearchCompletedAsync(
                Arg.Any<SearchCompletedEvent>(),
                Arg.Any<CancellationToken>()))
            .Do(call => published = call.Arg<SearchCompletedEvent>());

        // Act
        await _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None);

        // Assert
        await _publisher.Received(1).PublishSearchCompletedAsync(
            Arg.Any<SearchCompletedEvent>(),
            Arg.Any<CancellationToken>());

        published.ShouldNotBeNull();
        published.SearchId.ShouldBe(searchId);
        published.CompletedAtUtc.ShouldBe(CompletedAtUtc);
    }

    [Fact]
    public async Task Handle_WithARunningSearch_PersistsTheCompletionBeforePublishingTheEvent()
    {
        // Arrange
        // Completion is what clients poll for; the event only announces it. Writing first means a
        // broker outage can never leave a search stuck reporting itself as still running.
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, CreatedAtUtc));

        // Act
        await _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None);

        // Assert
        Received.InOrder(() =>
        {
            _repository.UpdateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>());
            _publisher.PublishSearchCompletedAsync(
                Arg.Any<SearchCompletedEvent>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Handle_ForTheSameSearchTwice_PublishesNothingTheSecondTime()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, CreatedAtUtc));

        // Act
        await _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None);
        await _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None);

        // Assert
        await _publisher.Received(1).PublishSearchCompletedAsync(
            Arg.Any<SearchCompletedEvent>(),
            Arg.Any<CancellationToken>());
        await _repository.Received(1).UpdateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenThePublisherThrows_RethrowsButLeavesTheSearchPersistedAsCompleted()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, CreatedAtUtc));

        var brokerFailure = new InvalidOperationException("Broker unavailable.");
        _publisher
            .PublishSearchCompletedAsync(Arg.Any<SearchCompletedEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(brokerFailure);

        // Act
        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None));

        // Assert
        thrown.ShouldBeSameAs(brokerFailure);

        _persisted.ShouldNotBeNull();
        _persisted.IsCompleted.ShouldBeTrue();
        _persisted.CompletedAtUtc.ShouldBe(CompletedAtUtc);
    }

    [Fact]
    public async Task Handle_WithAnUnknownSearchId_ThrowsSearchNotFoundExceptionAndPublishesNothing()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        _repository.GetAsync(searchId, Arg.Any<CancellationToken>()).Returns((Search?)null);

        // Act
        SearchNotFoundException exception = await Should.ThrowAsync<SearchNotFoundException>(
            () => _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None));

        // Assert
        exception.SearchId.ShouldBe(searchId);
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>());
        await _publisher.DidNotReceive().PublishSearchCompletedAsync(
            Arg.Any<SearchCompletedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithARunningSearch_ForwardsTheCancellationTokenToBothCollaborators()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, CreatedAtUtc));
        using var cancellation = new CancellationTokenSource();

        // Act
        await _handler.Handle(new CompleteSearchCommand(searchId), cancellation.Token);

        // Assert
        await _repository.Received(1).UpdateAsync(Arg.Any<Search>(), cancellation.Token);
        await _publisher.Received(1).PublishSearchCompletedAsync(
            Arg.Any<SearchCompletedEvent>(),
            cancellation.Token);
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
    /// snapshot of the last persisted state, so a replayed command genuinely observes a search
    /// that is already complete.
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
}
