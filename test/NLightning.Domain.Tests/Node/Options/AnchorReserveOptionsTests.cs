using Microsoft.Extensions.Configuration;

namespace NLightning.Domain.Tests.Node.Options;

using Domain.Node.Options;

public class AnchorReserveOptionsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 10_000)]
    [InlineData(3, 30_000)]
    [InlineData(10, 100_000)]
    [InlineData(11, 100_000)]
    [InlineData(1_000, 100_000)]
    public void Given_DefaultOptions_When_ReserveComputed_Then_TenThousandPerChannelCappedAtOneHundredThousand(
        int channels, long expectedSat)
    {
        // Arrange (LND's anchor reserve)
        var options = new AnchorReserveOptions();

        // Act
        var reserve = options.GetRequiredReserve(channels);

        // Assert
        Assert.Equal(expectedSat, reserve.Satoshi);
    }

    [Fact]
    public void Given_ZeroPerChannel_When_ReserveComputed_Then_ItIsOff()
    {
        // Arrange
        var options = new AnchorReserveOptions { ReservePerChannel = 0 };

        // Act / Assert
        Assert.True(options.GetRequiredReserve(5).IsZero);
    }

    [Fact]
    public void Given_AHugePerChannelReserve_When_ManyChannels_Then_TheCapAppliesWithoutOverflow()
    {
        // Arrange
        var options = new AnchorReserveOptions { ReservePerChannel = ulong.MaxValue / 2, MaxReserve = 1_000_000 };

        // Act / Assert
        Assert.Equal(1_000_000, options.GetRequiredReserve(int.MaxValue).Satoshi);
    }

    [Fact]
    public void Given_ANegativeCount_When_ReserveComputed_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnchorReserveOptions().GetRequiredReserve(-1));
    }

    [Fact]
    public void Given_MaxBelowPerChannel_When_Validated_Then_ReportsTheError()
    {
        // Arrange
        var options = new NodeOptions { Anchors = new AnchorReserveOptions { ReservePerChannel = 20, MaxReserve = 10 } };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains("MaxReserve"));
    }

    [Fact]
    public void Given_ANodeAnchorsSection_When_Bound_Then_TheReserveOptionsAreRead()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Node:Anchors:ReservePerChannel"] = "25000",
                               ["Node:Anchors:MaxReserve"] = "50000",
                               ["Node:Anchors:PendingOpenTimeout"] = "00:05:00"
                           })
                           .Build();

        // Act
        var options = configuration.GetSection("Node").Get<NodeOptions>()!;

        // Assert
        Assert.Equal(25_000UL, options.Anchors.ReservePerChannel);
        Assert.Equal(50_000UL, options.Anchors.MaxReserve);
        Assert.Equal(50_000, options.Anchors.GetRequiredReserve(3).Satoshi);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Anchors.PendingOpenTimeout);
        Assert.Empty(options.GetValidationErrors());
    }

    [Fact]
    public void Given_ANonPositivePendingOpenTimeout_When_Validated_Then_ReportsTheError()
    {
        // Arrange
        var options = new NodeOptions { Anchors = new AnchorReserveOptions { PendingOpenTimeout = TimeSpan.Zero } };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains("PendingOpenTimeout"));
    }
}