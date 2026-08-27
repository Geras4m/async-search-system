using FluentValidation.Results;
using SearchService.Application.Commands;
using SearchService.Application.Validators;
using Shouldly;
using Xunit;

namespace UnitTests.Validators;

/// <summary>
/// The service-side guarantee on the destination, held no matter which client calls. The
/// specification states the length must be strictly greater than two and strictly less than one
/// hundred, so the four boundary lengths are pinned individually: an off-by-one here would slip
/// through every happy-path test in the suite.
/// </summary>
public sealed class StartSearchCommandValidatorTests
{
    private readonly StartSearchCommandValidator _validator = new();

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(50)]
    [InlineData(98)]
    [InlineData(99)]
    public void Validate_WithADestinationLengthInsideTheAllowedRange_Succeeds(int length)
    {
        // Arrange
        var command = new StartSearchCommand(new string('a', length));

        // Act
        ValidationResult result = _validator.Validate(command);

        // Assert
        result.IsValid.ShouldBeTrue(
            $"a destination of {length} characters is inside the allowed range");
        result.Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(500)]
    public void Validate_WithADestinationLengthOutsideTheAllowedRange_Fails(int length)
    {
        // Arrange
        var command = new StartSearchCommand(new string('a', length));

        // Act
        ValidationResult result = _validator.Validate(command);

        // Assert
        result.IsValid.ShouldBeFalse(
            $"a destination of {length} characters is outside the allowed range");
        result.Errors.ShouldContain(
            error => error.PropertyName == nameof(StartSearchCommand.Destination));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void Validate_WithAMissingDestination_Fails(string? destination)
    {
        // Arrange
        var command = new StartSearchCommand(destination!);

        // Act
        ValidationResult result = _validator.Validate(command);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.ErrorMessage == "Destination is required.");
    }

    [Fact]
    public void Validate_WithAShortestAndLongestAcceptedDestination_AgreesWithThePublishedBounds()
    {
        // Arrange
        // The constants are part of the validator's public surface; if they ever drift away from
        // the specification's "> 2 and < 100" this test is the one that notices.

        // Act / Assert
        StartSearchCommandValidator.MinimumDestinationLength.ShouldBe(3);
        StartSearchCommandValidator.MaximumDestinationLength.ShouldBe(99);
    }

    [Fact]
    public void Validate_WithARealisticDestination_Succeeds()
    {
        // Arrange
        var command = new StartSearchCommand("Paris");

        // Act
        ValidationResult result = _validator.Validate(command);

        // Assert
        result.IsValid.ShouldBeTrue();
    }
}
