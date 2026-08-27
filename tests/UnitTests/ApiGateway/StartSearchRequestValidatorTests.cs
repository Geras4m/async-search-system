using ApiGateway.Contracts;
using ApiGateway.Validators;
using FluentValidation.Results;
using Shouldly;
using Xunit;

namespace UnitTests.ApiGateway;

/// <summary>
/// The edge-side copy of the destination rule. It exists so obviously bad input never costs a
/// gRPC round trip, which only holds if it accepts and rejects exactly the same lengths as the
/// Search Service does: strictly greater than two, strictly less than one hundred.
/// </summary>
public sealed class StartSearchRequestValidatorTests
{
    private readonly StartSearchRequestValidator _validator = new();

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(50)]
    [InlineData(98)]
    [InlineData(99)]
    public void Validate_WithADestinationLengthInsideTheAllowedRange_Succeeds(int length)
    {
        // Arrange
        var request = new StartSearchRequest(new string('a', length));

        // Act
        ValidationResult result = _validator.Validate(request);

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
        var request = new StartSearchRequest(new string('a', length));

        // Act
        ValidationResult result = _validator.Validate(request);

        // Assert
        result.IsValid.ShouldBeFalse(
            $"a destination of {length} characters is outside the allowed range");
        result.Errors.ShouldContain(
            error => error.PropertyName == nameof(StartSearchRequest.Destination));
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
        var request = new StartSearchRequest(destination!);

        // Act
        ValidationResult result = _validator.Validate(request);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.ErrorMessage == "Destination is required.");
    }

    [Fact]
    public void Validate_WithARealisticDestination_Succeeds()
    {
        // Arrange
        var request = new StartSearchRequest("Paris");

        // Act
        ValidationResult result = _validator.Validate(request);

        // Assert
        result.IsValid.ShouldBeTrue();
    }
}
