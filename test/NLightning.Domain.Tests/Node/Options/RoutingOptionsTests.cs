namespace NLightning.Domain.Tests.Node.Options;

using Domain.Money;
using Domain.Node.Options;

public class RoutingOptionsTests
{
    [Fact]
    public void Given_DefaultOptions_When_GetValidationErrors_Then_NoErrors()
    {
        // Arrange
        var options = new RoutingOptions();

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Given_DefaultOptions_When_Read_Then_MatchRoadmapDefaults()
    {
        // Arrange
        var options = new RoutingOptions();

        // Assert
        Assert.Equal(1_000UL, options.FeeBaseMsat);
        Assert.Equal(1U, options.FeeProportionalMillionths);
        Assert.Equal((ushort)40, options.CltvExpiryDelta);
        Assert.Equal(2_016U, options.MaxCltvExpiryDistance);
        Assert.Equal((ushort)40, options.InvoiceMinFinalCltvExpiry);
        Assert.Equal((ushort)18, options.ExpiryTooSoonBlocks);
        Assert.Equal(3_600U, options.InvoiceExpirySeconds);
        Assert.Equal(1_000UL, options.HtlcMinimumMsat);
        Assert.Null(options.HtlcMaximumMsat);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)18)]
    [InlineData((ushort)33)]
    public void Given_CltvExpiryDeltaBelow34_When_GetValidationErrors_Then_Error(ushort delta)
    {
        // Arrange
        var options = new RoutingOptions { CltvExpiryDelta = delta, ExpiryTooSoonBlocks = 1 };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains(nameof(RoutingOptions.CltvExpiryDelta)) && e.Contains("34"));
    }

    [Theory]
    [InlineData((ushort)34)]
    [InlineData((ushort)40)]
    [InlineData((ushort)80)]
    [InlineData((ushort)144)]
    public void Given_CltvExpiryDeltaAtLeast34_When_GetValidationErrors_Then_NoErrors(ushort delta)
    {
        // Arrange
        var options = new RoutingOptions { CltvExpiryDelta = delta };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Given_MaxCltvExpiryDistanceBelowDelta_When_GetValidationErrors_Then_Error()
    {
        // Arrange
        var options = new RoutingOptions { CltvExpiryDelta = 40, MaxCltvExpiryDistance = 39 };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains(nameof(RoutingOptions.MaxCltvExpiryDistance)));
    }

    [Theory]
    [InlineData((ushort)0, false)]
    [InlineData((ushort)17, false)]
    [InlineData((ushort)18, true)]
    [InlineData((ushort)144, true)]
    public void Given_InvoiceMinFinalCltvExpiry_When_GetValidationErrors_Then_AtLeast18IsValid(ushort value,
        bool valid)
    {
        // Arrange
        var options = new RoutingOptions { InvoiceMinFinalCltvExpiry = value };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Equal(valid, errors.Count == 0);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)40)]
    [InlineData((ushort)41)]
    public void Given_ExpiryTooSoonBlocksZeroOrNotBelowDelta_When_GetValidationErrors_Then_Error(ushort blocks)
    {
        // Arrange
        var options = new RoutingOptions { CltvExpiryDelta = 40, ExpiryTooSoonBlocks = blocks };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains(nameof(RoutingOptions.ExpiryTooSoonBlocks)));
    }

    [Fact]
    public void Given_ZeroInvoiceExpiry_When_GetValidationErrors_Then_Error()
    {
        // Arrange
        var options = new RoutingOptions { InvoiceExpirySeconds = 0 };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains(nameof(RoutingOptions.InvoiceExpirySeconds)));
    }

    [Theory]
    [InlineData(999UL, false)]
    [InlineData(1_000UL, true)]
    [InlineData(5_000_000UL, true)]
    public void Given_HtlcMaximum_When_GetValidationErrors_Then_MustNotBeBelowMinimum(ulong maximum, bool valid)
    {
        // Arrange
        var options = new RoutingOptions { HtlcMinimumMsat = 1_000, HtlcMaximumMsat = maximum };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void Given_SeveralErrors_When_GetValidationErrors_Then_AllAreReported()
    {
        // Arrange
        var options = new RoutingOptions
        {
            CltvExpiryDelta = 20,
            InvoiceMinFinalCltvExpiry = 10,
            InvoiceExpirySeconds = 0
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.True(errors.Count >= 3);
    }

    [Fact]
    public void Given_CarolPolicy_When_CalculateFee_Then_UsesBolt7Formula()
    {
        // Arrange
        var options = new RoutingOptions { FeeBaseMsat = 2_000, FeeProportionalMillionths = 500 };

        // Act
        var fee = options.CalculateFee(LightningMoney.MilliSatoshis(50_000_123UL));

        // Assert
        Assert.Equal(27_000UL, fee.MilliSatoshi);
    }
}