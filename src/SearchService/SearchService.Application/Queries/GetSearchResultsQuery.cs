using MediatR;
using SearchService.Application.Models;

namespace SearchService.Application.Queries;

/// <summary>
/// Reads the current state of a search.
/// </summary>
/// <param name="SearchId">Identifier of the search to read.</param>
public sealed record GetSearchResultsQuery(Guid SearchId) : IRequest<SearchResultsDto?>;
