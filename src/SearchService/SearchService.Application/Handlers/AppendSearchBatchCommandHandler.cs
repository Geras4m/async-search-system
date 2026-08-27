using MediatR;
using Microsoft.Extensions.Logging;
using SearchService.Application.Abstractions;
using SearchService.Application.Commands;
using SearchService.Domain.Exceptions;
using SearchService.Domain.Repositories;

namespace SearchService.Application.Handlers;

/// <summary>
/// Handles <see cref="AppendSearchBatchCommand"/>: generates one batch of hotels, appends it to
/// the search and persists the updated aggregate.
/// </summary>
/// <param name="repository">Persistence boundary for the search aggregate.</param>
/// <param name="hotelResultGenerator">Produces the hotels belonging to the requested batch.</param>
/// <param name="logger">Sink for structured log records.</param>
public sealed partial class AppendSearchBatchCommandHandler(
    ISearchRepository repository,
    IHotelResultGenerator hotelResultGenerator,
    ILogger<AppendSearchBatchCommandHandler> logger)
    : IRequestHandler<AppendSearchBatchCommand>
{
    /// <summary>
    /// Appends a single batch of generated hotels to an existing search.
    /// </summary>
    /// <param name="request">The command to handle.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes once the batch has been persisted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="SearchNotFoundException">No search with the requested identifier exists.</exception>
    /// <exception cref="InvalidOperationException">The search has already been completed.</exception>
    public async Task Handle(AppendSearchBatchCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var search = await repository.GetAsync(request.SearchId, cancellationToken)
            ?? throw new SearchNotFoundException(request.SearchId);

        var batch = hotelResultGenerator.GenerateBatch(request.BatchNumber);

        search.AppendResults(batch);

        await repository.UpdateAsync(search, cancellationToken);

        LogBatchAdded(logger, request.SearchId, request.BatchNumber, search.Results.Count);
    }

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Batch added. SearchId={SearchId} Batch={BatchNumber} ResultCount={ResultCount}")]
    private static partial void LogBatchAdded(ILogger logger, Guid searchId, int batchNumber, int resultCount);
}
