namespace NLightning.Domain.Tests.Channels.DualFunding;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.DualFunding;
using Domain.Enums;
using Domain.Money;
using Domain.Node;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// The BOLT 2 "Channel Establishment v2" rules outside the interactive-tx engine (splicing plan wave DF, DF1/DF2).
/// </summary>
public class DualFundingRulesTests
{
    // A P2WSH funding script (OP_0 <32 bytes>) and a P2WPKH change script (OP_0 <20 bytes>)
    private static readonly BitcoinScript s_fundingScript = new([0x00, 0x20, .. Enumerable.Repeat((byte)0x11, 32)]);
    private static readonly BitcoinScript s_changeScript = new([0x00, 0x14, .. Enumerable.Repeat((byte)0x22, 20)]);

    #region channel reserve: "1% of the total channel balance rounded down ... or the dust_limit_satoshis"

    [Theory]
    [InlineData(1_000_000, 546, 10_000)]
    [InlineData(1_234_567, 546, 12_345)]
    [InlineData(40_000, 546, 546)]
    [InlineData(54_699, 546, 546)]
    [InlineData(54_700, 546, 547)]
    public void Given_ATotalFunding_When_GetChannelReserve_Then_OnePercentRoundedDownOrTheDustLimit(
        long totalSat, long dustSat, long expectedSat)
    {
        // Act
        var reserve = DualFundingRules.GetChannelReserve(LightningMoney.Satoshis(totalSat),
                                                         LightningMoney.Satoshis(dustSat));

        // Assert
        Assert.Equal(LightningMoney.Satoshis(expectedSat), reserve);
    }

    #endregion

    #region opener weight: "the channel funding output must be added by the opener, who pays its fees"

    [Fact]
    public void Given_AP2WshFundingScript_When_GetOpenerExtraWeight_Then_CommonFieldsPlusTheFundingOutput()
    {
        // Act
        var weight = DualFundingRules.GetOpenerExtraWeight(s_fundingScript);

        // Assert: 42 wu of common fields and (8 + 1 + 34) x 4 = 172 wu for the P2WSH output (BOLT 3 Appendix F)
        Assert.Equal(42 + 172, weight);
    }

    #endregion

    #region commitment_signed: "if the message has one or more HTLCs: MUST fail the negotiation"

    [Fact]
    public void Given_NoHtlcSignature_When_CheckFirstCommitmentSigned_Then_Accepted()
    {
        Assert.Null(DualFundingRules.CheckFirstCommitmentSigned(0));
    }

    [Fact]
    public void Given_AnHtlcSignature_When_CheckFirstCommitmentSigned_Then_TheNegotiationFails()
    {
        Assert.StartsWith("[DF-CS-01]", DualFundingRules.CheckFirstCommitmentSigned(1));
    }

    #endregion

    #region open_channel2 receiver

    [Fact]
    public void Given_NoChannelType_When_CheckOpenChannel2_Then_Refused()
    {
        Assert.StartsWith("[DF-OPEN-01]", DualFundingRules.CheckOpenChannel2(null, 2_500, 2_500, 100, 253));
    }

    [Fact]
    public void Given_ZeroFeeCommitmentsWithAFeerate_When_CheckOpenChannel2_Then_Refused()
    {
        // Arrange
        var channelType = new FeatureSet();
        channelType.SetFeature(Feature.ZeroFeeCommitments, true);

        // Act & Assert
        Assert.StartsWith("[DF-OPEN-02]", DualFundingRules.CheckOpenChannel2(channelType, 2_500, 1, 100, 253));
        Assert.Null(DualFundingRules.CheckOpenChannel2(channelType, 2_500, 0, 100, 253));
    }

    [Fact]
    public void Given_ATimestampLocktime_When_CheckOpenChannel2_Then_Refused()
    {
        Assert.StartsWith("[DF-OPEN-03]",
                          DualFundingRules.CheckOpenChannel2(new FeatureSet(), 2_500, 2_500,
                                                             DualFundingRules.LocktimeThreshold, 253));
    }

    [Fact]
    public void Given_AFundingFeerateBelowTheFloor_When_CheckOpenChannel2_Then_Refused()
    {
        Assert.StartsWith("[DF-OPEN-04]", DualFundingRules.CheckOpenChannel2(new FeatureSet(), 252, 2_500, 0, 253));
        Assert.Null(DualFundingRules.CheckOpenChannel2(new FeatureSet(), 253, 2_500, 0, 253));
    }

    #endregion

    #region RBF: "MUST NOT have sent or received a channel_ready message"

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Given_ChannelReady_When_CanRbf_Then_OnlyBeforeIt(bool channelReady, bool expected)
    {
        Assert.Equal(expected, DualFundingRules.CanRbf(channelReady));
    }

    [Fact]
    public void Given_APreviousContribution_When_RebuiltAtAHigherFeerate_Then_TheSameInputsPayMoreFromTheChange()
    {
        // Arrange: one 1,000,000 sat input for a 600,000 sat share, change 399,000 sat
        var previous = Contribution(1_000_000, 399_000);
        var spec = Spec(1_000_000, 600_000);

        // Act
        var rebuilt = DualFundingRules.RebuildContributionForFeerate(previous, LightningMoney.Satoshis(600_000), true,
                                                                     spec, 5_000, LightningMoney.Satoshis(546))!;

        // Assert: same inputs (IT-RBF-01), change = input - share - our fee at 5,000 sat/kw
        Assert.Equal(previous.Inputs, rebuilt.Inputs);
        Assert.Equal(previous.ReservationId, rebuilt.ReservationId);
        var fee = CollaborativeFeeCalculator.GetLocalContributionFee(rebuilt, true, spec, 5_000);
        var change = Assert.Single(rebuilt.Outputs);
        Assert.True(change.IsChange);
        Assert.Equal(LightningMoney.Satoshis(1_000_000 - 600_000) - fee, change.Amount);
    }

    [Fact]
    public void Given_ChangeBelowDust_When_Rebuilt_Then_TheChangeIsDroppedAndTheRestGoesToFees()
    {
        // Arrange: the input leaves about 1,000 sat after the share, less than the fee plus a dust output at a high rate
        var previous = Contribution(601_000, 900);
        var spec = Spec(1_000_000, 600_000);

        // Act
        var rebuilt = DualFundingRules.RebuildContributionForFeerate(previous, LightningMoney.Satoshis(600_000), false,
                                                                     spec, 2_000, LightningMoney.Satoshis(546));

        // Assert
        Assert.NotNull(rebuilt);
        Assert.Empty(rebuilt.Outputs);
    }

    [Fact]
    public void Given_InputsThatCannotPayTheNewFee_When_Rebuilt_Then_Null()
    {
        // Arrange
        var previous = Contribution(600_100, 0, withChange: false);
        var spec = Spec(1_000_000, 600_000);

        // Act & Assert
        Assert.Null(DualFundingRules.RebuildContributionForFeerate(previous, LightningMoney.Satoshis(600_000), true,
                                                                   spec, 10_000, LightningMoney.Satoshis(546)));
    }

    #endregion

    private static InteractiveTxContribution Contribution(long inputSat, long changeSat, bool withChange = true)
    {
        var input = new ContributedInput(new TxId(Enumerable.Repeat((byte)0x33, 32).ToArray()), 0, [0x01], 0xFFFFFFFD,
                                         LightningMoney.Satoshis(inputSat), s_changeScript, 273);
        List<ContributedOutput> outputs = withChange
                                              ? [new ContributedOutput(LightningMoney.Satoshis(changeSat), s_changeScript,
                                                                       true)]
                                              : [];
        return new InteractiveTxContribution([input], outputs, Guid.NewGuid());
    }

    private static SharedFundingSpec Spec(long totalSat, long localSat) =>
        new(null, s_fundingScript, LightningMoney.Satoshis(totalSat), LightningMoney.Zero, LightningMoney.Zero,
            LightningMoney.Satoshis(localSat), LightningMoney.Satoshis(totalSat - localSat));
}