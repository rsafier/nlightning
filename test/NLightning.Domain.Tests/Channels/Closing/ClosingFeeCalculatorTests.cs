namespace NLightning.Domain.Tests.Channels.Closing;

using Domain.Channels.Closing;

public class ClosingFeeCalculatorTests
{
    [Fact]
    public void Given_P2WpkhAndP2WshOutputs_When_EstimateWeight_Then_EqualsBolt3CommitmentWeight()
    {
        // Arrange: the same shape as BOLT 3's commitment without HTLCs (one 2-of-2 input, a P2WSH and a P2WPKH output,
        // 73-byte signatures), whose expected weight is 724
        // Act
        var weight = ClosingFeeCalculator.EstimateWeight(22, 34);

        // Assert
        Assert.Equal(724UL, weight);
    }

    [Theory]
    [InlineData(22, 22, 676UL)]
    [InlineData(34, 34, 772UL)]
    [InlineData(34, 22, 724UL)]
    public void Given_ScriptLengths_When_EstimateWeight_Then_FourPerNonWitnessByte(int local, int remote,
                                                                                   ulong expected)
    {
        // Act / Assert
        Assert.Equal(expected, ClosingFeeCalculator.EstimateWeight(local, remote));
    }

    [Theory]
    [InlineData(22, 34, 568UL)]
    [InlineData(34, 34, 616UL)]
    [InlineData(22, 22, 520UL)]
    public void Given_ATaprootKeyPathSpend_When_EstimateWeight_Then_UsesThe64ByteSchnorrWitness(int local, int remote,
                                                                                            ulong expected)
    {
        // Arrange: NL-968, marker and flag (2), one item (1) of 64 bytes with its length (65): 68 WU of witness, 156
        // less than the 2-of-2's 224
        // Act
        var weight = ClosingFeeCalculator.EstimateWeight(local, remote, taprootKeyPath: true);

        // Assert
        Assert.Equal(expected, weight);
        Assert.Equal(ClosingFeeCalculator.EstimateWeight(local, remote) - 156, weight);
    }

    [Fact]
    public void Given_NoTaproot_When_EstimateWeight_Then_SameAsTheTwoArgumentForm()
    {
        // Act / Assert
        Assert.Equal(ClosingFeeCalculator.EstimateWeight(22, 34), ClosingFeeCalculator.EstimateWeight(22, 34, false));
    }

    [Fact]
    public void Given_Feerate_When_FeeSat_Then_RoundedDown()
    {
        // Act / Assert
        Assert.Equal(183UL, ClosingFeeCalculator.FeeSat(253, 724)); // 183.17
        Assert.Equal(7_240UL, ClosingFeeCalculator.FeeSat(10_000, 724));
    }
}