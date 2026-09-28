namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Splicing;
using Domain.Bitcoin.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// Wave SPR, SPR-T1 and NL-481: our contribution to a splice RBF attempt, rebuilt from the latest attempt's
/// (<c>SpliceService.PlanRbfContribution</c>). A splice-in keeps its wallet inputs and reservation and pays the higher
/// fee from its change; a splice-out keeps its output and pays its share from our balance (a negative contribution);
/// the interactive-tx initiator also pays the common fields and the shared input and output.
/// </summary>
public class SpliceRbfContributionTests
{
    private static readonly BitcoinScript s_change = new([0x00, 0x14, .. Enumerable.Repeat((byte)0x33, 20)]);
    private static readonly BitcoinScript s_destination = new([0x00, 0x14, .. Enumerable.Repeat((byte)0x44, 20)]);
    private static readonly Guid s_reservation = Guid.NewGuid();

    /// <summary>Common fields + the shared input + a P2WSH funding output: what the RBF initiator pays for.</summary>
    private static readonly long s_initiatorWeight =
        CollaborativeFeeCalculator.CommonFieldsWeight + SpliceFundingScripts.SharedInputWeight
      + CollaborativeFeeCalculator.OutputWeight(new BitcoinScript(new byte[34]));

    [Fact]
    public void Given_ASpliceIn_When_RebuiltAsInitiatorAtAHigherFeerate_Then_TheSameInputsPayFromTheChange()
    {
        // Arrange: 300,000 sat in, 100,000 into the channel, the rest back as change
        var previous = SpliceIn(300_000, 190_000);

        // Act
        var plan = SpliceService.PlanRbfContribution(previous, 100_000, true, 2_000, null, null);

        // Assert
        Assert.NotNull(plan);
        Assert.Equal(100_000, plan.SignedContributionSatoshis);
        Assert.Same(previous.Inputs, plan.Contribution.Inputs);
        Assert.Equal(s_reservation, plan.Contribution.ReservationId);
        var change = Assert.Single(plan.Contribution.Outputs);
        Assert.True(change.IsChange);
        var weight = s_initiatorWeight + 300 + CollaborativeFeeCalculator.OutputWeight(s_change);
        var fee = CollaborativeFeeCalculator.FeeForWeight(weight, 2_000).Satoshi;
        Assert.Equal(fee, (long)plan.FeeSatoshis);
        Assert.Equal(300_000 - 100_000 - fee, change.Amount.Satoshi);
    }

    [Fact]
    public void Given_ASpliceIn_When_RebuiltAsNonInitiator_Then_OnlyOurInputAndChangeArePaidFor()
    {
        // Arrange
        var previous = SpliceIn(300_000, 190_000);

        // Act
        var plan = SpliceService.PlanRbfContribution(previous, 100_000, false, 2_000, null, null);

        // Assert
        Assert.NotNull(plan);
        var fee = CollaborativeFeeCalculator.FeeForWeight(300 + CollaborativeFeeCalculator.OutputWeight(s_change),
                                                          2_000).Satoshi;
        Assert.Equal(fee, (long)plan.FeeSatoshis);
        Assert.Equal(100_000, plan.SignedContributionSatoshis);
    }

    [Fact]
    public void Given_AChangeBelowDust_When_Rebuilt_Then_TheChangeGoesToTheFee()
    {
        // Arrange: 100,300 sat in for a 100,000 sat splice-in
        var previous = SpliceIn(100_300, 100);

        // Act
        var plan = SpliceService.PlanRbfContribution(previous, 100_000, false, 253, null, null);

        // Assert
        Assert.NotNull(plan);
        Assert.Empty(plan.Contribution.Outputs);
        Assert.Equal(300UL, plan.FeeSatoshis);
    }

    [Fact]
    public void Given_InputsThatCannotPayTheNewFee_When_Rebuilt_Then_Null()
    {
        // Arrange
        var previous = SpliceIn(100_100, 0);

        // Act & Assert
        Assert.Null(SpliceService.PlanRbfContribution(previous, 100_000, true, 50_000, null, null));
    }

    [Theory]
    [InlineData(150_000L, true)]
    [InlineData(-1L, false)]
    public void Given_ARequestedSpliceInAmount_When_Rebuilt_Then_TheInputsAreKept(long requested, bool possible)
    {
        // Arrange
        var previous = SpliceIn(300_000, 190_000);

        // Act
        var plan = SpliceService.PlanRbfContribution(previous, 100_000, true, 2_000, requested, null);

        // Assert
        Assert.Equal(possible, plan is not null);
        if (possible)
            Assert.Equal(requested, plan!.SignedContributionSatoshis);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_ASpliceOut_When_Rebuilt_Then_TheOutputIsKeptAndTheContributionIsNegative(bool isInitiator)
    {
        // Arrange: 80,000 sat out to the destination
        var previous = new InteractiveTxContribution(
            [], [new ContributedOutput(LightningMoney.Satoshis(80_000), s_destination, false)], null);

        // Act
        var plan = SpliceService.PlanRbfContribution(previous, -80_500, isInitiator, 2_000, null, null);

        // Assert
        Assert.NotNull(plan);
        var output = Assert.Single(plan.Contribution.Outputs);
        Assert.Equal(80_000, output.Amount.Satoshi);
        Assert.Equal(s_destination, plan.SpliceOutScript);
        var weight = (isInitiator ? s_initiatorWeight : 0) + CollaborativeFeeCalculator.OutputWeight(s_destination);
        var fee = CollaborativeFeeCalculator.FeeForWeight(weight, 2_000).Satoshi;
        Assert.Equal(-(80_000 + fee), plan.SignedContributionSatoshis);
        Assert.Equal(fee, (long)plan.FeeSatoshis);
        Assert.Null(plan.Contribution.ReservationId);
    }

    [Fact]
    public void Given_ARequestedSpliceOut_When_Rebuilt_Then_TheOutputIsWhatIsLeftAfterTheFee()
    {
        // Arrange: nothing before; the operator asks for a -50,000 sat contribution to a new destination
        var plan = SpliceService.PlanRbfContribution(InteractiveTxContribution.Empty, 0, true, 2_000, -50_000,
                                                     s_destination);

        // Assert
        Assert.NotNull(plan);
        Assert.Equal(-50_000, plan.SignedContributionSatoshis);
        var output = Assert.Single(plan.Contribution.Outputs);
        Assert.Equal(50_000 - (long)plan.FeeSatoshis, output.Amount.Satoshi);
        Assert.Equal(s_destination, output.ScriptPubKey);
    }

    [Fact]
    public void Given_NoContribution_When_RebuiltAsInitiator_Then_TheSharedFeeComesFromOurBalance()
    {
        // Act
        var plan = SpliceService.PlanRbfContribution(InteractiveTxContribution.Empty, 0, true, 2_000, null, null);

        // Assert
        Assert.NotNull(plan);
        var fee = CollaborativeFeeCalculator.FeeForWeight(s_initiatorWeight, 2_000).Satoshi;
        Assert.Equal(-fee, plan.SignedContributionSatoshis);
        Assert.Same(InteractiveTxContribution.Empty, plan.Contribution);
    }

    [Fact]
    public void Given_NoContribution_When_RebuiltAsNonInitiator_Then_NothingIsContributed()
    {
        // Act
        var plan = SpliceService.PlanRbfContribution(InteractiveTxContribution.Empty, 0, false, 2_000, null, null);

        // Assert
        Assert.NotNull(plan);
        Assert.Equal(0, plan.SignedContributionSatoshis);
        Assert.Equal(0UL, plan.FeeSatoshis);
    }

    [Fact]
    public void Given_NoWalletInputs_When_ASpliceInIsRequested_Then_Null()
    {
        // Act & Assert: a new splice-in needs new wallet inputs, which an RBF does not reserve
        Assert.Null(SpliceService.PlanRbfContribution(InteractiveTxContribution.Empty, 0, true, 2_000, 10_000, null));
    }

    private static InteractiveTxContribution SpliceIn(long inputSatoshis, long changeSatoshis)
    {
        var input = new ContributedInput(new TxId(Enumerable.Repeat((byte)0x55, 32).ToArray()), 0, [0x01], 0xFFFFFFFD,
                                         LightningMoney.Satoshis(inputSatoshis), s_change, 300);
        List<ContributedOutput> outputs = changeSatoshis > 0
                                              ? [new ContributedOutput(LightningMoney.Satoshis(changeSatoshis),
                                                                       s_change, true)]
                                              : [];
        return new InteractiveTxContribution([input], outputs, s_reservation);
    }
}