namespace Shared.EventContracts;

/// <summary>
/// Published by the Search Service once a search has produced its final batch.
/// </summary>
/// <param name="SearchId">Identifier of the search that finished.</param>
/// <param name="CompletedAtUtc">UTC instant at which the search was marked complete.</param>
public sealed record SearchCompletedEvent(Guid SearchId, DateTime CompletedAtUtc);
