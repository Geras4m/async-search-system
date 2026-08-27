using MediatR;

namespace SearchService.Application.Commands;

/// <summary>
/// Creates a search, persists its initial state and hands it to the background
/// execution engine. Returns as soon as the identifier exists, without waiting
/// for any results.
/// </summary>
/// <param name="Destination">Destination the client is searching hotels for.</param>
public sealed record StartSearchCommand(string Destination) : IRequest<StartSearchResult>;

/// <summary>
/// Result of <see cref="StartSearchCommand"/>.
/// </summary>
/// <param name="SearchId">Identifier the client polls with.</param>
public sealed record StartSearchResult(Guid SearchId);
