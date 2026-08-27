using Microsoft.Extensions.Options;
using SearchService.Application.Options;
using SearchService.Domain.Entities;
using SearchService.Infrastructure.Generation;
using Shouldly;
using Xunit;

namespace UnitTests.Generation;

/// <summary>
/// The generator stands in for a supplier integration. The specification pins the numbering
/// exactly — batch 1 gives hotels 1 to 5 and batch 6 gives 26 to 30 — and prices must stay inside
/// the configured range, because an out-of-range price only shows up as a strange number in a
/// client's result list.
/// </summary>
public sealed class SequentialHotelResultGeneratorTests
{
    private readonly SearchExecutionOptions _options = new();
    private readonly SequentialHotelResultGenerator _generator;

    public SequentialHotelResultGeneratorTests()
    {
        _generator = new SequentialHotelResultGenerator(Options.Create(_options));
    }

    [Fact]
    public void GenerateBatch_ForTheFirstBatch_YieldsHotelsOneThroughFive()
    {
        // Act
        IReadOnlyList<HotelResult> batch = _generator.GenerateBatch(1);

        // Assert
        batch.Select(hotel => hotel.Name)
            .ShouldBe(new[] { "Hotel 1", "Hotel 2", "Hotel 3", "Hotel 4", "Hotel 5" });
    }

    [Fact]
    public void GenerateBatch_ForTheSixthBatch_YieldsHotelsTwentySixThroughThirty()
    {
        // Act
        IReadOnlyList<HotelResult> batch = _generator.GenerateBatch(6);

        // Assert
        batch.Select(hotel => hotel.Name)
            .ShouldBe(new[] { "Hotel 26", "Hotel 27", "Hotel 28", "Hotel 29", "Hotel 30" });
    }

    [Theory]
    [InlineData(2, "Hotel 6", "Hotel 10")]
    [InlineData(3, "Hotel 11", "Hotel 15")]
    [InlineData(4, "Hotel 16", "Hotel 20")]
    [InlineData(5, "Hotel 21", "Hotel 25")]
    public void GenerateBatch_ForAMiddleBatch_YieldsTheContiguousRangeForThatBatch(
        int batchNumber,
        string expectedFirstName,
        string expectedLastName)
    {
        // Act
        IReadOnlyList<HotelResult> batch = _generator.GenerateBatch(batchNumber);

        // Assert
        batch[0].Name.ShouldBe(expectedFirstName);
        batch[^1].Name.ShouldBe(expectedLastName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void GenerateBatch_ForAnyBatch_ReturnsExactlyHotelsPerBatchResults(int batchNumber)
    {
        // Act
        IReadOnlyList<HotelResult> batch = _generator.GenerateBatch(batchNumber);

        // Assert
        batch.Count.ShouldBe(_options.HotelsPerBatch);
    }

    [Fact]
    public void GenerateBatch_AcrossTheWholeRun_ProducesPricesInsideTheConfiguredRange()
    {
        // Arrange
        // Prices are random, so a single batch proves little. Sampling every batch of many runs
        // makes an off-by-one on either bound overwhelmingly likely to be caught.
        List<decimal> prices = [];

        // Act
        for (int run = 0; run < 100; run++)
        {
            for (int batchNumber = 1; batchNumber <= _options.BatchCount; batchNumber++)
            {
                prices.AddRange(_generator.GenerateBatch(batchNumber).Select(hotel => hotel.Price));
            }
        }

        // Assert
        prices.Count.ShouldBe(100 * _options.BatchCount * _options.HotelsPerBatch);
        prices.ShouldAllBe(price => price >= _options.MinHotelPrice);
        prices.ShouldAllBe(price => price < _options.MaxHotelPrice);
    }

    [Fact]
    public void GenerateBatch_AcrossTheWholeRun_ProducesUniqueHotelIds()
    {
        // Act
        List<string> hotelIds = [];
        for (int batchNumber = 1; batchNumber <= _options.BatchCount; batchNumber++)
        {
            hotelIds.AddRange(_generator.GenerateBatch(batchNumber).Select(hotel => hotel.HotelId));
        }

        // Assert
        hotelIds.Count.ShouldBe(_options.BatchCount * _options.HotelsPerBatch);
        hotelIds.Distinct(StringComparer.Ordinal).Count().ShouldBe(hotelIds.Count);
        hotelIds.Count(static hotelId => Guid.TryParse(hotelId, out _)).ShouldBe(hotelIds.Count);
    }

    [Fact]
    public void GenerateBatch_WithACustomBatchSize_ShiftsTheHotelNumberingAccordingly()
    {
        // Arrange
        var generator = new SequentialHotelResultGenerator(
            Options.Create(new SearchExecutionOptions { HotelsPerBatch = 3 }));

        // Act
        IReadOnlyList<HotelResult> batch = generator.GenerateBatch(2);

        // Assert
        batch.Select(hotel => hotel.Name).ShouldBe(new[] { "Hotel 4", "Hotel 5", "Hotel 6" });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void GenerateBatch_WithABatchNumberBelowOne_ThrowsArgumentOutOfRangeException(int batchNumber)
    {
        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => _generator.GenerateBatch(batchNumber));
    }

    [Fact]
    public void Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new SequentialHotelResultGenerator(null!));
    }
}
