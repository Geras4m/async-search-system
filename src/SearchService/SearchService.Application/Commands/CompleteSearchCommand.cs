using MediatR;

namespace SearchService.Application.Commands;

/// <summary>
/// Marks a search as completed, persists it, and publishes the completion event.
/// </summary>
/// <param name="SearchId">Identifier of the search to complete.</param>
/// <remarks>
/// Idempotent: completing an already completed search neither fails nor republishes the event.
/// </remarks>
public sealed record CompleteSearchCommand(Guid SearchId) : IRequest;
