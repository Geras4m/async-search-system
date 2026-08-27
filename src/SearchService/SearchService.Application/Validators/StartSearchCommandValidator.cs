using FluentValidation;
using SearchService.Application.Commands;

namespace SearchService.Application.Validators;

/// <summary>
/// Validates <see cref="StartSearchCommand"/> before it reaches its handler.
/// </summary>
/// <remarks>
/// The specification states the destination is required, longer than two characters and shorter
/// than one hundred. Those bounds are expressed here as an inclusive 3 to 99 character range,
/// which is the same set of accepted values. The API Gateway validates the incoming HTTP request
/// as well; this validator is the service-side guarantee that holds no matter which client calls.
/// </remarks>
public sealed class StartSearchCommandValidator : AbstractValidator<StartSearchCommand>
{
    /// <summary>Shortest accepted destination, in characters: strictly longer than two.</summary>
    public const int MinimumDestinationLength = 3;

    /// <summary>Longest accepted destination, in characters: strictly shorter than one hundred.</summary>
    public const int MaximumDestinationLength = 99;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartSearchCommandValidator"/> class.
    /// </summary>
    public StartSearchCommandValidator()
    {
        RuleFor(command => command.Destination)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
                .WithMessage("Destination is required.")
            .MinimumLength(MinimumDestinationLength)
                .WithMessage($"Destination must be at least {MinimumDestinationLength} characters long.")
            .MaximumLength(MaximumDestinationLength)
                .WithMessage($"Destination must be at most {MaximumDestinationLength} characters long.");
    }
}
