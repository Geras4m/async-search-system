using ApiGateway.Contracts;
using FluentValidation;

namespace ApiGateway.Validators;

/// <summary>
/// Validates the body of <c>POST /searches</c> before any gRPC call is made, so obviously bad
/// input is rejected at the edge instead of costing a round trip to the Search Service.
/// </summary>
public sealed class StartSearchRequestValidator : AbstractValidator<StartSearchRequest>
{
    /// <summary>
    /// Shortest accepted destination. The specification requires a length strictly greater
    /// than two characters.
    /// </summary>
    private const int MinimumDestinationLength = 3;

    /// <summary>
    /// Longest accepted destination. The specification requires a length strictly less than one
    /// hundred characters.
    /// </summary>
    private const int MaximumDestinationLength = 99;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartSearchRequestValidator"/> class and
    /// declares the rules for <see cref="StartSearchRequest.Destination"/>.
    /// </summary>
    public StartSearchRequestValidator()
    {
        RuleFor(request => request.Destination)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithMessage("Destination is required.")
            .MinimumLength(MinimumDestinationLength)
            .WithMessage($"Destination must be longer than {MinimumDestinationLength - 1} characters.")
            .MaximumLength(MaximumDestinationLength)
            .WithMessage($"Destination must be shorter than {MaximumDestinationLength + 1} characters.");
    }
}
