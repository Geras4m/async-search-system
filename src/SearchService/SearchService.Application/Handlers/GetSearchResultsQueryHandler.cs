using MediatR;
using SearchService.Application.Models;
using SearchService.Application.Queries;
using SearchService.Domain.Repositories;

namespace SearchService.Application.Handlers;

/// <summary>
/// Handles <see cref="GetSearchResultsQuery"/>: reads the current state of a search and projects
/// it onto the transport-neutral <see cref="SearchResultsDto"/>.
/// </summary>
/// <param name="repository">Persistence boundary for the search aggregate.</param>
/// <remarks>
/// A missing search is not an exception here. The query returns <see langword="null"/> and the
/// gRPC layer translates that into a <c>NOT_FOUND</c> status, which the API Gateway surfaces as
/// HTTP 404. An existing search with no results yet is a perfectly normal answer: an empty result
/// list with <c>IsCompleted</c> still <see langword="false"/>.
/// </remarks>
public sealed class GetSearchResultsQueryHandler(ISearchRepository repository)
    : IRequestHandler<GetSearchResultsQuery, SearchResultsDto?>
{
    /// <summary>
    /// Reads the accumulated results and completion flag of a search.
    /// </summary>
    /// <param name="request">The query to handle.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// The current state of the search, or <see langword="null"/> when no search with that
    /// identifier exists.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public async Task<SearchResultsDto?> Handle(GetSearchResultsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var search = await repository.GetAsync(request.SearchId, cancellationToken);

        if (search is null)
        {
            return null;
        }

        IReadOnlyList<HotelResultDto> results =
            [.. search.Results.Select(static hotel => new HotelResultDto(hotel.HotelId, hotel.Name, hotel.Price))];

        return new SearchResultsDto(search.Id, search.IsCompleted, search.CreatedAtUtc, results);
    }
}
