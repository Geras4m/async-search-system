using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace SearchService.Application.Behaviors;

/// <summary>
/// MediatR pipeline behaviour that runs every registered <see cref="IValidator{T}"/> for a request
/// and aborts the pipeline when any of them reports a failure.
/// </summary>
/// <typeparam name="TRequest">Type of the request flowing through the pipeline.</typeparam>
/// <typeparam name="TResponse">Type the request responds with.</typeparam>
/// <param name="validators">All validators registered for <typeparamref name="TRequest"/>.</param>
/// <remarks>
/// Keeping validation in the pipeline rather than in the handlers means every request is validated
/// the same way, and a handler can assume it only ever receives a valid request. Requests with no
/// registered validator pass straight through, so adding a validator later needs no wiring change.
/// </remarks>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IReadOnlyList<IValidator<TRequest>> _validators = [.. validators];

    /// <summary>
    /// Validates the request and, when it is valid, invokes the rest of the pipeline.
    /// </summary>
    /// <param name="request">The request being handled.</param>
    /// <param name="next">Continuation representing the next step, ultimately the handler.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The response produced by the rest of the pipeline.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationException">At least one validator reported a failure.</exception>
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (_validators.Count == 0)
        {
            return await next(cancellationToken);
        }

        var context = new ValidationContext<TRequest>(request);
        List<ValidationFailure>? failures = null;

        // Sequential on purpose: a ValidationContext is not designed to be shared across
        // concurrent validators, and the handful of rules involved cost nothing to run in order.
        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken);

            if (result.IsValid)
            {
                continue;
            }

            failures ??= [];
            failures.AddRange(result.Errors);
        }

        if (failures is { Count: > 0 })
        {
            throw new ValidationException(failures);
        }

        return await next(cancellationToken);
    }
}
