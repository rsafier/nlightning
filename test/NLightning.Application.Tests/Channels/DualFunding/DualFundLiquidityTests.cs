namespace NLightning.Application.Tests.Channels.DualFunding;

using Application.Channels.DualFunding;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Money;

/// <summary>
/// The fee transfer of a liquidity purchase in a dual-funded open's first commitment (NL-850): the fee leaves the
/// buyer's balance and reaches the seller's, the shares (the funding output) stay as they are.
/// </summary>
public class DualFundLiquidityTests
{
    private static readonly LightningMoney s_commitmentCost = LightningMoney.Satoshis(1_000);

    [Fact]
    public void Given_WeBought_When_TheFeeIsApplied_Then_OurBalanceLosesItAndTheSellersGainsIt()
    {
        // Act
        var (local, remote) = DualFundLiquidity.ApplyFee(LightningMoney.Satoshis(600_000),
                                                         LightningMoney.Satoshis(400_000), 6_500_000);

        // Assert
        Assert.Equal(LightningMoney.Satoshis(593_500), local);
        Assert.Equal(LightningMoney.Satoshis(406_500), remote);
    }

    [Fact]
    public void Given_WeSold_When_TheFeeIsApplied_Then_OurBalanceGainsIt()
    {
        // Act
        var (local, remote) = DualFundLiquidity.ApplyFee(LightningMoney.Satoshis(400_000),
                                                         LightningMoney.Satoshis(600_000), -6_500_000);

        // Assert
        Assert.Equal(LightningMoney.Satoshis(406_500), local);
        Assert.Equal(LightningMoney.Satoshis(593_500), remote);
    }

    [Theory]
    [InlineData(6_500_000)]
    [InlineData(-6_500_000)]
    [InlineData(0)]
    public void Given_Balances_When_TheFeeIsRemoved_Then_TheSharesComeBack(long feeMsat)
    {
        // Arrange
        var (local, remote) = DualFundLiquidity.ApplyFee(LightningMoney.Satoshis(600_000),
                                                         LightningMoney.Satoshis(400_000), feeMsat);

        // Act
        var shares = DualFundLiquidity.RemoveFee(local, remote, feeMsat);

        // Assert
        Assert.Equal((LightningMoney.Satoshis(600_000), LightningMoney.Satoshis(400_000)), shares);
    }

    [Fact]
    public void Given_ABuyerShareBelowTheFee_When_TheFeeIsApplied_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => DualFundLiquidity.ApplyFee(LightningMoney.Satoshis(1),
                                                     LightningMoney.Satoshis(400_000), 6_500_000));
    }

    [Fact]
    public void Given_NoFee_When_Checked_Then_NoViolation()
    {
        // Act / Assert
        Assert.Null(DualFundLiquidity.GetBalanceViolation(LightningMoney.Zero, LightningMoney.Satoshis(1), 0, true,
                                                          s_commitmentCost));
    }

    [Fact]
    public void Given_ABuyerThatCannotPayTheFee_When_Checked_Then_Refused()
    {
        // Act
        var violation = DualFundLiquidity.GetBalanceViolation(LightningMoney.Satoshis(5_000),
                                                              LightningMoney.Satoshis(400_000), 6_500_000, true,
                                                              s_commitmentCost);

        // Assert
        Assert.Contains("cannot pay the liquidity fee", violation);
    }

    [Theory]
    [InlineData(true, 7_499_999, 6_500_000)]
    [InlineData(false, 400_000_000, -6_500_000)]
    public void Given_AnOpenerLeftBelowTheCommitmentFee_When_Checked_Then_Refused(bool weAreOpener, long localMsat,
                                                                                   long feeMsat)
    {
        // Arrange: the opener (the buyer) keeps 999,999 msat after the fee, below the 1,000 sat commitment fee
        var remote = weAreOpener ? LightningMoney.Satoshis(400_000) : LightningMoney.MilliSatoshis(7_499_999L);

        // Act
        var violation = DualFundLiquidity.GetBalanceViolation(LightningMoney.MilliSatoshis(localMsat), remote,
                                                              feeMsat, weAreOpener, s_commitmentCost);

        // Assert
        Assert.Contains("cannot pay the first commitment's fee", violation);
    }

    [Fact]
    public void Given_EnoughForBoth_When_Checked_Then_NoViolation()
    {
        // Act / Assert
        Assert.Null(DualFundLiquidity.GetBalanceViolation(LightningMoney.Satoshis(600_000),
                                                          LightningMoney.Satoshis(400_000), 6_500_000, true,
                                                          s_commitmentCost));
    }

    [Theory]
    [InlineData(LiquidityPurchaseRole.Buyer, 6_500_000)]
    [InlineData(LiquidityPurchaseRole.Seller, -6_500_000)]
    public void Given_ARole_When_TheLocalFeeIsRead_Then_SignedFromOurSide(LiquidityPurchaseRole role, long expected)
    {
        // Arrange
        var rate = new FundingRate(100_000, 1_000_000, 400, 100, 500, 1_000);
        var liquidity = new DualFundLiquidity(role,
                                              new RequestFunding(400_000, rate,
                                                                 LiquidityPaymentDetails.FromChannelBalance),
                                              new WillFund(rate, [0x00, 0x20], new byte[64]),
                                              new LiquidityFees(1_000, 5_500), 400_000);

        // Act / Assert
        Assert.Equal(expected, liquidity.LocalFeeMsat);
        Assert.Equal(0, DualFundLiquidity.GetLocalFeeMsat(null));
    }
}