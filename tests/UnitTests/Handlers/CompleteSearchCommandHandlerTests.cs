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
/// Completion is the step the rest of the system reacts to. It has to flip the flag, persist that
/// fact before announcing it, and announce it exactly once no matter how often the command is
/// replayed. Since the outbox arrived it also has to record the obligation to publish before it
/// attempts the publish, and hold on to that record until the broker has actually taken the event.
/// </summary>
public sealed class CompleteSearchCommandHandlerTests
{
    private static readonly DateTime CreatedAtUtc = new(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);
    private static readonly DateTime CompletedAtUtc = new(2026, 3, 14, 9, 27, 23, DateTimeKind.Utc);

    private readonly ISearchRepository _repository = Substitute.For<ISearchRepository>();
    private readonly ISearchEventsPublisher _publisher = Substitute.For<ISearchEventsPublisher>();
    private readonly ISearchEventOutbox _outbox = Substitute.For<ISearchEventOutbox>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly CompleteSearchCommandHandler _handler;

    private Search? _persisted;

    public CompleteSearchCommandHandlerTests()
    {
        _clock.UtcNow.Returns(CompletedAtUtc);

        _handler = new CompleteSearchCommandHandler(
            _repository,
            _publisher,
            _outbox,
            _clock,
            NullLogger<CompleteSearchCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WithARunningSearch_PersistsItAsCompletedWithTheClockTimestamp()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));

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
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));

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
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));

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
    public async Task Handle_WithARunningSearch_EnqueuesTheEventBeforeAttemptingToPublishIt()
    {
        // Arrange
        // The obligation has to be recorded first. Enqueuing only after a failed publish would
        // leave a window in which a crash loses the event, which is the exact hole the outbox
        // exists to close.
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));

        // Act
        await _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None);

        // Assert
        // CA2012 is suppressed for the block below rather than worked around: inside
        // Received.InOrder a call is not an operation but a description of one already recorded,
        // so the ValueTask it hands back is a matching artefact with nothing to await.
#pragma warning disable CA2012
        Received.InOrder(() =>
        {
            _outbox.EnqueueAsync(Arg.Any<SearchCompletedEvent>(), Arg.Any<CancellationToken>());
            _publisher.PublishSearchCompletedAsync(
                Arg.Any<SearchCompletedEvent>(),
                Arg.Any<CancellationToken>());
        });
#pragma warning restore CA2012
    }

    [Fact]
    public async Task Handle_WithARunningSearch_EnqueuesTheSameEventItPublishes()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));

        SearchCompletedEvent? published = null;
        _publisher
            .When(publisher => publisher.PublishSearchCompletedAsync(
                Arg.Any<SearchCompletedEvent>(),
                Arg.Any<CancellationToken>()))
            .Do(call => published = call.Arg<SearchCompletedEvent>());

        // Act
        await _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None);

        // Assert
        published.ShouldNotBeNull();
        published.SearchId.ShouldBe(searchId);
        published.CompletedAtUtc.ShouldBe(CompletedAtUtc);

        await _outbox.Received(1).EnqueueAsync(published, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTheInlinePublishSucceeds_RemovesTheOutboxEntry()
    {
        // Arrange
        // Nothing is owed once the broker has the event, so the background publisher must find an
        // empty outbox and never redeliver it.
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));

        // Act
        await _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None);

        // Assert
        await _outbox.Received(1).RemoveAsync(searchId, Arg.Any<CancellationToken>());

        Received.InOrder(() =>
        {
            _publisher.PublishSearchCompletedAsync(
                Arg.Any<SearchCompletedEvent>(),
                Arg.Any<CancellationToken>());
            _outbox.RemoveAsync(searchId, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Handle_WhenThePublisherThrows_DoesNotPropagateAndLeavesTheEventOwedInTheOutbox()
    {
        // Arrange
        // This is the regression guard for the lost event hole. A broker outage at the moment a
        // search completes must not fail the command and must not discard the announcement: the
        // search stays complete, the entry stays owed, and the background publisher retries it.
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));

        _publisher
            .PublishSearchCompletedAsync(Arg.Any<SearchCompletedEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Broker unavailable."));

        // Act
        await Should.NotThrowAsync(
            () => _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None));

        // Assert
        _persisted.ShouldNotBeNull();
        _persisted.IsCompleted.ShouldBeTrue();
        _persisted.CompletedAtUtc.ShouldBe(CompletedAtUtc);

        await _outbox.Received(1).EnqueueAsync(
            Arg.Is<SearchCompletedEvent>(completed => completed.SearchId == searchId),
            Arg.Any<CancellationToken>());

        await _outbox.DidNotReceive().RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ForTheSameSearchTwice_PublishesAndEnqueuesNothingTheSecondTime()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));

        // Act
        await _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None);
        await _handler.Handle(new CompleteSearchCommand(searchId), CancellationToken.None);

        // Assert
        await _repository.Received(1).UpdateAsync(Arg.Any<Search>(), Arg.Any<CancellationToken>());
        await _publisher.Received(1).PublishSearchCompletedAsync(
            Arg.Any<SearchCompletedEvent>(),
            Arg.Any<CancellationToken>());
        await _outbox.Received(1).EnqueueAsync(
            Arg.Any<SearchCompletedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithAnUnknownSearchId_ThrowsSearchNotFoundExceptionAndTouchesNothing()
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
        await _outbox.DidNotReceive().EnqueueAsync(
            Arg.Any<SearchCompletedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithARunningSearch_ForwardsTheCancellationTokenToEveryCollaborator()
    {
        // Arrange
        Guid searchId = Guid.NewGuid();
        GivenStoredSearch(Search.Create(searchId, "Paris", CreatedAtUtc));
        using var cancellation = new CancellationTokenSource();

        // Act
        await _handler.Handle(new CompleteSearchCommand(searchId), cancellation.Token);

        // Assert
        await _repository.Received(1).UpdateAsync(Arg.Any<Search>(), cancellation.Token);
        await _outbox.Received(1).EnqueueAsync(Arg.Any<SearchCompletedEvent>(), cancellation.Token);
        await _publisher.Received(1).PublishSearchCompletedAsync(
            Arg.Any<SearchCompletedEvent>(),
            cancellation.Token);
        await _outbox.Received(1).RemoveAsync(searchId, cancellation.Token);
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
