namespace NLightning.Infrastructure.Bitcoin.Tests.Services;

using Bitcoin.Services;

public class FeeRateConverterTests
{
    [Theory]
    [InlineData(10, 2_500)] // NL-288: 10 sat/vB is 2,500 sat/kw, not 10,000
    [InlineData(25, 6_250)]
    [InlineData(1.5, 375)]
    [InlineData(2, 500)]
    [InlineData(1, 253)] // 250 is below the BOLT 3 floor
    [InlineData(0, 253)]
    public void Given_SatPerVByte_When_Converted_Then_ItIsTimes250AtLeastTheFloor(double satPerVByte,
                                                                                 long expected)
    {
        // Act
        var satPerKw = FeeRateConverter.SatPerVByteToSatPerKw((decimal)satPerVByte);

        // Assert
        Assert.Equal(expected, satPerKw);
    }

    [Theory]
    [InlineData(10_000, "sat/kvB", 2_500)]
    [InlineData(2_500, "sat/kw", 2_500)]
    [InlineData(10, "SAT/VB", 2_500)]
    [InlineData(0.0001, "BTC/kvB", 2_500)] // 0.0001 BTC/kvB = 10,000 sat/kvB = 10 sat/vB
    [InlineData(1_001, "sat/kvB", 253)]
    public void Given_RateInUnit_When_Converted_Then_ItIsSatPerKw(double rate, string unit, long expected)
    {
        // Act
        var satPerKw = FeeRateConverter.ToSatPerKw((decimal)rate, unit);

        // Assert
        Assert.Equal(expected, satPerKw);
    }

    [Theory]
    [InlineData("sat/B")]
    [InlineData("")]
    [InlineData(null)]
    public void Given_UnknownUnit_When_Converted_Then_ItThrows(string? unit)
    {
        // Act / Assert
        Assert.False(FeeRateConverter.IsKnownUnit(unit));
        Assert.Throws<ArgumentException>(() => FeeRateConverter.ToSatPerKw(10, unit!));
    }

    [Fact]
    public void Given_NegativeRate_When_Converted_Then_ItThrows()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => FeeRateConverter.ToSatPerKw(-1, FeeRateConverter.SatPerVByte));
    }
}