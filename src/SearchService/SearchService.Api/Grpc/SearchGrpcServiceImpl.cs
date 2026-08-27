using System.Diagnostics.CodeAnalysis;
using FluentValidation;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MediatR;
using Microsoft.Extensions.Logging;
using SearchService.Application.Commands;
using SearchService.Application.Queries;
using Shared.GrpcContracts;

namespace SearchService.Api.Grpc;

/// <summary>
/// gRPC endpoint of the Search Service and the only transport the API Gateway is allowed to use.
/// </summary>
/// <remarks>
/// The type is a thin transport adapter: it translates protobuf messages into application layer
/// commands and queries, dispatches them through <see cref="IMediator"/>, and translates the
/// outcome back into protobuf messages or gRPC status codes. No business rule lives here.
/// </remarks>
/// <param name="mediator">Dispatches commands and queries to the application layer.</param>
/// <param name="logger">Sink for transport level diagnostics.</param>
// CA1711 justification: the frozen cross-service contract fixes this type name. The "Impl"
// suffix is what separates the server implementation from the generated SearchGrpcService
// contract type it derives from, and the API Gateway plus the integration tests are written
// against this exact name, so the suggested "Core" suffix is not available here.
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Name is fixed by the cross-service contract; see the comment above.")]
public sealed partial class SearchGrpcServiceImpl(IMediator mediator, ILogger<SearchGrpcServiceImpl> logger)
    : SearchGrpcService.SearchGrpcServiceBase
{
    /// <summary>
    /// Registers a new search and returns its identifier immediately, without waiting for results.
    /// </summary>
    /// <param name="request">Carries the destination the client is searching hotels for.</param>
    /// <param name="context">Call context supplying the deadline and the cancellation token.</param>
    /// <returns>A response carrying the string form of the new search identifier.</returns>
    /// <exception cref="RpcException">
    /// Thrown with <see cref="StatusCode.InvalidArgument"/> when the destination fails validation.
    /// </exception>
    public override async Task<StartSearchResponse> StartSearch(
        StartSearchRequest request,
        ServerCallContext context)
    {
        try
        {
            var result = await mediator.Send(
                new StartSearchCommand(request.Destination),
                context.CancellationToken);

            return new StartSearchResponse { SearchId = result.SearchId.ToString() };
        }
        catch (ValidationException exception)
        {
            StartSearchRejected(logger, request.Destination, exception);

            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
        }
    }

    /// <summary>
    /// Returns everything accumulated for a search so far together with its completion flag.
    /// </summary>
    /// <param name="request">Carries the string form of the search identifier to read.</param>
    /// <param name="context">Call context supplying the deadline and the cancellation token.</param>
    /// <returns>A response describing the current state of the search.</returns>
    /// <exception cref="RpcException">
    /// Thrown with <see cref="StatusCode.InvalidArgument"/> when the identifier is not a GUID, and
    /// with <see cref="StatusCode.NotFound"/> when no search carries that identifier.
    /// </exception>
    public override async Task<GetSearchResultsResponse> GetSearchResults(
        GetSearchResultsRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.SearchId, out var searchId))
        {
            MalformedSearchId(logger, request.SearchId);

            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"'{request.SearchId}' is not a valid search identifier."));
        }

        var results = await mediator.Send(new GetSearchResultsQuery(searchId), context.CancellationToken);

        if (results is null)
        {
            SearchNotFound(logger, searchId);

            throw new RpcException(new Status(
                StatusCode.NotFound,
                $"Search '{searchId}' was not found."));
        }

        var response = new GetSearchResultsResponse
        {
            SearchId = results.SearchId.ToString(),
            IsCompleted = results.IsCompleted,
            CreatedAtUtc = Timestamp.FromDateTime(
                DateTime.SpecifyKind(results.CreatedAtUtc, DateTimeKind.Utc)),
        };

        foreach (var hotel in results.Results)
        {
            response.Results.Add(new HotelResult
            {
                HotelId = hotel.HotelId,
                Name = hotel.Name,
                Price = hotel.Price.ToDecimalValue(),
            });
        }

        return response;
    }

    /// <summary>Records a start request that the application layer refused.</summary>
    /// <param name="logger">Logger the entry is written to.</param>
    /// <param name="destination">Destination the caller supplied.</param>
    /// <param name="exception">Validation failure describing what was wrong.</param>
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Warning,
        Message = "Start search rejected by validation. Destination={Destination}")]
    private static partial void StartSearchRejected(ILogger logger, string destination, Exception exception);

    /// <summary>Records a lookup whose identifier was not a GUID.</summary>
    /// <param name="logger">Logger the entry is written to.</param>
    /// <param name="searchId">Raw identifier the caller supplied.</param>
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Malformed search identifier rejected. SearchId={SearchId}")]
    private static partial void MalformedSearchId(ILogger logger, string searchId);

    /// <summary>Records a lookup for a search that does not exist.</summary>
    /// <param name="logger">Logger the entry is written to.</param>
    /// <param name="searchId">Identifier that produced no search.</param>
    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Search not found. SearchId={SearchId}")]
    private static partial void SearchNotFound(ILogger logger, Guid searchId);
}
