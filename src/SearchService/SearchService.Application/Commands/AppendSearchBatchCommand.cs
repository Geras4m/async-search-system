using MediatR;

namespace SearchService.Application.Commands;

/// <summary>
/// Appends a single generated batch of hotels to an existing search and persists it.
/// </summary>
/// <param name="SearchId">Identifier of the search to append to.</param>
/// <param name="BatchNumber">One-based batch index, used to pick the hotel range and for logging.</param>
public sealed record AppendSearchBatchCommand(Guid SearchId, int BatchNumber) : IRequest;
