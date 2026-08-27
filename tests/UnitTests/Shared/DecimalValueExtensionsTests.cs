using System.Globalization;
using Shared.GrpcContracts;
using Shouldly;
using Xunit;

namespace UnitTests.Shared;

/// <summary>
/// Every hotel price crosses the gRPC boundary through this conversion. Protobuf has no decimal
/// scalar, so money travels as a (units, nanos) pair; a defect here would not throw, it would
/// quietly quote a different price than the one the Search Service produced.
/// </summary>
public sealed class DecimalValueExtensionsTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("80")]
    [InlineData("399")]
    [InlineData("123.45")]
    [InlineData("0.000000001")]
    [InlineData("-1.75")]
    [InlineData("-0.000000001")]
    [InlineData("999999999.999999999")]
    public void ToDecimalValue_ThenToDecimal_RoundTripsTheAmountExactly(string amount)
    {
        // Arrange
        decimal original = decimal.Parse(amount, NumberStyles.Number, CultureInfo.InvariantCulture);

        // Act
        DecimalValue encoded = original.ToDecimalValue();
        decimal restored = encoded.ToDecimal();

        // Assert
        restored.ShouldBe(original);
    }

    [Theory]
    [InlineData("0", 0L, 0)]
    [InlineData("80", 80L, 0)]
    [InlineData("399", 399L, 0)]
    [InlineData("123.45", 123L, 450_000_000)]
    [InlineData("0.000000001", 0L, 1)]
    public void ToDecimalValue_WithAPositiveAmount_SplitsItIntoUnitsAndNanos(
        string amount,
        long expectedUnits,
        int expectedNanos)
    {
        // Arrange
        decimal original = decimal.Parse(amount, NumberStyles.Number, CultureInfo.InvariantCulture);

        // Act
        DecimalValue encoded = original.ToDecimalValue();

        // Assert
        encoded.Units.ShouldBe(expectedUnits);
        encoded.Nanos.ShouldBe(expectedNanos);
    }

    [Fact]
    public void ToDecimalValue_WithANegativeAmount_GivesUnitsAndNanosTheSameSign()
    {
        // Arrange
        // The wire contract requires both components to carry the sign: -1.75 is units = -1 and
        // nanos = -750,000,000. A positive nanos here would decode as -0.25.
        const decimal Original = -1.75m;

        // Act
        DecimalValue encoded = Original.ToDecimalValue();

        // Assert
        encoded.Units.ShouldBe(-1L);
        encoded.Nanos.ShouldBe(-750_000_000);
        encoded.ToDecimal().ShouldBe(Original);
    }

    [Fact]
    public void ToDecimal_WithANullValue_ReturnsZero()
    {
        // Arrange
        DecimalValue? missing = null;

        // Act
        decimal restored = missing.ToDecimal();

        // Assert
        restored.ShouldBe(0m);
    }

    [Fact]
    public void ToDecimal_WithAManuallyBuiltValue_ReassemblesUnitsAndNanos()
    {
        // Arrange
        var value = new DecimalValue { Units = 399, Nanos = 990_000_000 };

        // Act
        decimal restored = value.ToDecimal();

        // Assert
        restored.ShouldBe(399.99m);
    }

    [Fact]
    public void ToDecimalValue_AcrossTheGeneratedPriceRange_RoundTripsEveryWholeAmount()
    {
        // Arrange
        // The generator emits whole prices in [80, 400). Walking the entire range is cheap and
        // rules out any single value that fails to survive the trip.
        decimal[] prices = [.. Enumerable.Range(80, 320).Select(price => (decimal)price)];

        // Act
        decimal[] restored = [.. prices.Select(price => price.ToDecimalValue().ToDecimal())];

        // Assert
        restored.ShouldBe(prices);
    }
}
