using NLightning.Tests.Utils.Accounting;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.LiquidityAds.Constants;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Protocol.Messages;
using static LiquidityAdsKit;

/// <summary>
/// Liquidity ads in the dual-funded open (NL-850, BOLT PR #1153 as Eclair 0.14.3 speaks it; plan
/// <c>docs/agents/LIQUIDITY_ADS_PLAN.md</c> LA3/LA4/LA6) on <see cref="DualFundHarness"/>, both roles NLightning: Alice
/// buys inbound liquidity in her <c>open_channel2</c>, Bob sells it in <c>accept_channel2</c>; the fee moves from
/// Alice's first-commitment balance to Bob's; one purchase row per signed attempt on each side; the confirmation starts
/// the lease and books the fee with <c>ChannelFunded</c>; RBF re-purchases at the new feerate and whichever attempt
/// confirms brings its own fee; restarts keep the fee in the balances.
/// </summary>
public class DualFundLiquidityAdsTests
{
    [Fact]
    public async Task Given_BobSellsLiquidity_When_AliceBuysItInHerOpen_Then_TheFeeMovesAndIsBookedAtTheConfirmation()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var fees = Fees(2_500);

        // Act
        var result = await OpenAsync(harness);

        // Assert: the request and Bob's answer on the wire
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        Assert.Equal(1_000UL, fees.MiningFeeSat);
        Assert.Equal(5_500UL, fees.ServiceFeeSat);
        var channelId = result.ChannelId;
        var txId = result.FundingTxId!.Value;
        var open = (OpenChannel2Message)harness.Transcript.Single(t => t.Message is OpenChannel2Message).Message;
        Assert.Equal(RequestedSat, open.RequestFundingTlv!.Request.RequestedSat);
        Assert.Equal(Rate().ToFundingRate(), open.RequestFundingTlv.Request.Rate);
        var accept = (AcceptChannel2Message)harness.Transcript.Single(t => t.Message is AcceptChannel2Message).Message;
        Assert.Equal(LightningMoney.Satoshis(RequestedSat), accept.Payload.FundingAmount);
        var willFund = accept.ProvideFundingTlv!.WillFund;
        Assert.Equal(Rate().ToFundingRate(), willFund.Rate);
        Assert.Equal((byte[])Application.Channels.DualFunding.DualFundedOpenService
                                        .GetFundingScript(harness.Alice.Channel(channelId)),
                     willFund.FundingScript);

        // The fee moved from Alice's balance to Bob's in both first commitments, the funding output is the shares' sum
        await AssertBalancesAsync(harness, channelId, 600_000, RequestedSat, fees.TotalMsat);

        // One purchase per side, recorded with the attempt, and Bob's sale slot given back once signed
        Assert.Equal(LiquidityPurchaseRole.Buyer, result.Purchase!.Role);
        var bought = Assert.Single(await PurchasesAsync(harness.Alice, channelId));
        var sold = Assert.Single(await PurchasesAsync(harness.Bob, channelId));
        AssertPurchase(bought, LiquidityPurchaseRole.Buyer, LiquidityPurchaseKind.ChannelOpen, txId, harness.Bob,
                       fees);
        AssertPurchase(sold, LiquidityPurchaseRole.Seller, LiquidityPurchaseKind.ChannelOpen, txId, harness.Alice,
                       fees);
        Assert.Equal(willFund.Signature, bought.Signature);
        Assert.Equal(willFund.FundingScript, bought.FundingScript);
        Assert.Equal(0, LiquidityAds(harness.Bob).SalesInProgress);
        Assert.Empty(await EventsAsync(harness.Alice));

        // Act: the funding confirms
        await harness.ConfirmFundingAsync(channelId, txId);

        // Assert: open, the lease running from the funding's block on both sides, the fee booked with ChannelFunded
        Assert.Equal(ChannelState.Open, harness.Alice.Channel(channelId).State);
        Assert.Equal(ChannelState.Open, harness.Bob.Channel(channelId).State);
        foreach (var node in harness.Nodes)
        {
            var purchase = Assert.Single(await PurchasesAsync(node, channelId));
            Assert.Equal(LiquidityPurchaseStatus.Active, purchase.Status);
            Assert.Equal(DualFundHarness.FundingHeight, purchase.LeaseStartHeight);
            Assert.Equal(DualFundHarness.FundingHeight + LiquidityAdsConstants.LeaseBlocks, purchase.LeaseEndHeight);
        }

        await AssertBookedAsync(harness, channelId, txId, 600_000, RequestedSat, fees, AccountingDetailKeys
                                                                                             .LiquidityKindOpen);

        // Act: a payment each way
        await PayBothWaysAsync(harness, channelId);

        // Assert
        Assert.Equal(LightningMoney.MilliSatoshis(600_000_000 - fees.TotalMsat) - LightningMoney.Satoshis(30_000),
                     harness.Alice.Channel(channelId).LocalBalance);
        Assert.Equal(LightningMoney.MilliSatoshis(RequestedSat * 1_000 + fees.TotalMsat)
                   + LightningMoney.Satoshis(30_000), harness.Bob.Channel(channelId).LocalBalance);
    }

    [Fact]
    public async Task Given_AnOpenWithAPurchase_When_AliceBumpsIt_Then_ThePurchaseIsMadeAgainAtTheNewFeerate()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var first = await OpenAsync(harness);
        Assert.True(first.FailureReason is null, $"{first.FailureReason}\n{harness.Describe()}");
        var channelId = first.ChannelId;

        // Act: no liquidity given, so the bump repeats the open's purchase
        var bump = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(channelId, 5_000, null, null,
                                                                           TestContext.Current.CancellationToken));

        // Assert: tx_init_rbf asks again, tx_ack_rbf answers, at the new feerate's mining fee
        Assert.True(bump.FailureReason is null, $"{bump.FailureReason}\n{harness.Describe()}");
        var bumpTxId = bump.FundingTxId!.Value;
        var initRbf = (TxInitRbfMessage)harness.Transcript.Single(t => t.Message is TxInitRbfMessage).Message;
        Assert.Equal(RequestedSat, initRbf.RequestFundingTlv!.Request.RequestedSat);
        Assert.Equal(Rate().ToFundingRate(), initRbf.RequestFundingTlv.Request.Rate);
        var ackRbf = (TxAckRbfMessage)harness.Transcript.Single(t => t.Message is TxAckRbfMessage).Message;
        Assert.NotNull(ackRbf.ProvideFundingTlv);
        var fees = Fees(5_000);
        Assert.Equal(2_000UL, fees.MiningFeeSat);
        await AssertBalancesAsync(harness, channelId, 600_000, RequestedSat, fees.TotalMsat);
        Assert.Equal(LiquidityPurchaseKind.OpenRbf, bump.Purchase!.Kind);
        foreach (var node in harness.Nodes)
        {
            var purchases = await PurchasesAsync(node, channelId);
            Assert.Equal([LiquidityPurchaseKind.ChannelOpen, LiquidityPurchaseKind.OpenRbf],
                         purchases.Select(p => p.Kind));
            Assert.Equal(fees, purchases.Single(p => p.FundingTxId == bumpTxId).Fees);
            Assert.All(purchases, p => Assert.Equal(LiquidityPurchaseStatus.Pending, p.Status));
        }

        // Act: the replacement confirms
        await harness.ConfirmFundingAsync(channelId, bumpTxId);

        // Assert: its purchase active, the first one replaced, only the confirmed attempt's fee booked
        foreach (var node in harness.Nodes)
        {
            var purchases = await PurchasesAsync(node, channelId);
            Assert.Equal(LiquidityPurchaseStatus.Replaced,
                         purchases.Single(p => p.FundingTxId == first.FundingTxId).Status);
            Assert.Equal(LiquidityPurchaseStatus.Active, purchases.Single(p => p.FundingTxId == bumpTxId).Status);
        }

        await AssertBookedAsync(harness, channelId, bumpTxId, 600_000, RequestedSat, fees,
                                AccountingDetailKeys.LiquidityKindRbf);
        await PayBothWaysAsync(harness, channelId);
    }

    [Fact]
    public async Task Given_ABumpedOpenWithPurchases_When_TheFirstAttemptConfirms_Then_ItsOwnFeeApplies()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        var firstTxId = first.FundingTxId!.Value;
        var bump = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(channelId, 5_000,
                                                                           TestContext.Current.CancellationToken));
        Assert.True(bump.FailureReason is null, $"{bump.FailureReason}\n{harness.Describe()}");

        // Act: the first attempt is the one mined
        await harness.ConfirmFundingAsync(channelId, firstTxId);

        // Assert: both on the first attempt with its fee (2,500 sat/kw), its purchase active, the bump's replaced
        var fees = Fees(2_500);
        Assert.Equal(firstTxId, harness.Alice.Channel(channelId).FundingOutput!.TransactionId);
        Assert.Equal(ChannelState.Open, harness.Alice.Channel(channelId).State);
        Assert.Equal(ChannelState.Open, harness.Bob.Channel(channelId).State);
        await AssertBalancesAsync(harness, channelId, 600_000, RequestedSat, fees.TotalMsat);
        foreach (var node in harness.Nodes)
        {
            var purchases = await PurchasesAsync(node, channelId);
            Assert.Equal(LiquidityPurchaseStatus.Active, purchases.Single(p => p.FundingTxId == firstTxId).Status);
            Assert.Equal(LiquidityPurchaseStatus.Replaced,
                         purchases.Single(p => p.FundingTxId == bump.FundingTxId).Status);
        }

        await AssertBookedAsync(harness, channelId, firstTxId, 600_000, RequestedSat, fees,
                                AccountingDetailKeys.LiquidityKindOpen);
        await PayBothWaysAsync(harness, channelId);
    }

    [Fact]
    public async Task Given_AnOpenWithAPurchase_When_AliceBumpsItBuyingAnotherAmount_Then_BobsShareFollowsIt()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;

        // Act: 300,000 sat this time
        var bump = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(
                                              channelId, 5_000, null, new LiquidityRequest(300_000),
                                              TestContext.Current.CancellationToken));

        // Assert: Bob contributes exactly the new amount and the fee is the new amount's
        Assert.True(bump.FailureReason is null, $"{bump.FailureReason}\n{harness.Describe()}");
        var fees = Fees(5_000, 300_000);
        await AssertBalancesAsync(harness, channelId, 600_000, 300_000, fees.TotalMsat);
        var purchase = Assert.Single(await PurchasesAsync(harness.Bob, channelId),
                                     p => p.FundingTxId == bump.FundingTxId);
        Assert.Equal(300_000UL, purchase.RequestedSat);
        Assert.Equal(300_000UL, purchase.ContributedSat);

        await harness.ConfirmFundingAsync(channelId, bump.FundingTxId!.Value);
        await AssertBookedAsync(harness, channelId, bump.FundingTxId.Value, 600_000, 300_000, fees,
                                AccountingDetailKeys.LiquidityKindRbf);
        await PayBothWaysAsync(harness, channelId);
    }

    [Fact]
    public async Task Given_ThePeerLosesTheSecondCommitmentSigned_When_ANodeRestarts_Then_TheFeeIsStillInTheBalances()
    {
        // Arrange: stop right before the second commitment_signed is delivered
        await using var harness = await CreateAsync();
        _ = StartOpen(harness);
        var commitments = 0;
        await harness.PumpAsync((_, message) => message is CommitmentSignedMessage && ++commitments == 2);
        var (sender, signed) = harness.Transcript.First(t => t.Message is CommitmentSignedMessage);
        var channelId = signed.Payload.ChannelId;
        var receiver = sender == "Alice" ? harness.Alice : harness.Bob;

        // Act: the node whose peer's commitment_signed was lost restarts, then both reconnect (next_funding)
        await harness.RestartAsync(receiver);
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: the open completed with the fee in both nodes' balances
        var fees = Fees(2_500);
        foreach (var node in harness.Nodes)
            Assert.Single(node.Published);
        var txId = harness.Alice.Published[0].TransactionId;
        await AssertBalancesAsync(harness, channelId, 600_000, RequestedSat, fees.TotalMsat);

        // ...and it confirms with the purchase active and booked, then carries payments
        await harness.ConfirmFundingAsync(channelId, txId);
        await AssertBookedAsync(harness, channelId, txId, 600_000, RequestedSat, fees,
                                AccountingDetailKeys.LiquidityKindOpen);
        await PayBothWaysAsync(harness, channelId);
    }

    [Fact]
    public async Task Given_BothNodesRestartBetweenAttempts_When_AliceBumps_Then_ThePurchaseIsRepeatedFromTheStoredRows()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;

        // Act: both restart (the negotiations are rebuilt from the stored rows), then Alice bumps
        foreach (var node in harness.Nodes)
        {
            await harness.RestartAsync(node);
            await harness.ReconnectAsync();
            await harness.PumpAsync();
        }

        await AssertBalancesAsync(harness, channelId, 600_000, RequestedSat, Fees(2_500).TotalMsat);
        var bump = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(channelId, 5_000,
                                                                           TestContext.Current.CancellationToken));

        // Assert: the stored purchase was requested again, at the new feerate
        Assert.True(bump.FailureReason is null, $"{bump.FailureReason}\n{harness.Describe()}");
        var initRbf = (TxInitRbfMessage)harness.Transcript.Single(t => t.Message is TxInitRbfMessage).Message;
        Assert.Equal(RequestedSat, initRbf.RequestFundingTlv!.Request.RequestedSat);
        var fees = Fees(5_000);
        await AssertBalancesAsync(harness, channelId, 600_000, RequestedSat, fees.TotalMsat);

        // Act: Bob restarts once more, then the replacement confirms
        await harness.RestartAsync(harness.Bob);
        await harness.ReconnectAsync();
        await harness.PumpAsync();
        await harness.ConfirmFundingAsync(channelId, bump.FundingTxId!.Value);

        // Assert
        await AssertBalancesAsync(harness, channelId, 600_000, RequestedSat, fees.TotalMsat);
        await AssertBookedAsync(harness, channelId, bump.FundingTxId.Value, 600_000, RequestedSat, fees,
                                AccountingDetailKeys.LiquidityKindRbf);
        await PayBothWaysAsync(harness, channelId);
    }

    /// <summary>
    /// NL-871: Alice's own fee limit of the open's purchase (7,000 sat; the fee is 6,500 sat at 2,500 sat/kw) is stored
    /// with it and still applies after both nodes restart, when <c>bumpopen</c> repeats the purchase without a new
    /// request: at 5,000 sat/kw the fee is 7,500 sat, so the bump is refused before anything is sent.
    /// </summary>
    [Fact]
    public async Task Given_AnOpenBoughtWithAFeeLimit_When_AliceBumpsWithoutANewRequest_Then_TheLimitStillApplies()
    {
        // Arrange
        await using var harness = await CreateAsync();
        Assert.Equal(6_500UL, Fees(2_500).TotalSat);
        Assert.Equal(7_500UL, Fees(5_000).TotalSat);
        var first = await OpenAsync(harness, new LiquidityRequest(RequestedSat, MaxFeeSat: 7_000));
        Assert.True(first.FailureReason is null, $"{first.FailureReason}\n{harness.Describe()}");
        var channelId = first.ChannelId;
        Assert.Equal(7_000UL, Assert.Single(await PurchasesAsync(harness.Alice, channelId)).MaxFeeSat);
        Assert.Null(Assert.Single(await PurchasesAsync(harness.Bob, channelId)).MaxFeeSat);
        foreach (var node in harness.Nodes)
        {
            await harness.RestartAsync(node);
            await harness.ReconnectAsync();
            await harness.PumpAsync();
        }

        var sent = harness.Transcript.Count;

        // Act (pumped, so a bump that is not refused completes instead of waiting for the peer)
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                          () => harness.RunAsync(harness.Alice.DualFund.BumpAsync(
                                                     channelId, 5_000, TestContext.Current.CancellationToken)));

        // Assert: the stored limit, no tx_init_rbf, the open's attempt and fee unchanged
        Assert.Contains("above the limit of 7000 sat", refused.Message);
        await harness.PumpAsync();
        Assert.DoesNotContain(harness.Transcript.Skip(sent), t => t.Message is TxInitRbfMessage);
        await AssertBalancesAsync(harness, channelId, 600_000, RequestedSat, Fees(2_500).TotalMsat);

        // Act / Assert: a bump within the limit (a new request naming a higher one) goes through
        var bump = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(
                                              channelId, 5_000, null, new LiquidityRequest(RequestedSat, MaxFeeSat: 8_000),
                                              TestContext.Current.CancellationToken));
        Assert.True(bump.FailureReason is null, $"{bump.FailureReason}\n{harness.Describe()}");
        Assert.Equal(8_000UL, bump.Purchase!.MaxFeeSat);
    }

    [Fact]
    public async Task Given_ARestartDuringAnRbfAttemptWithAPurchase_When_ThePeerForgotIt_Then_BackOnTheFirstFee()
    {
        // Arrange: stop as soon as one node constructed the RBF attempt (stored it with its purchase)
        await using var harness = await CreateAsync();
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        var firstTxId = first.FundingTxId!.Value;
        _ = harness.Alice.DualFund.BumpAsync(channelId, 5_000, TestContext.Current.CancellationToken);
        await harness.PumpAsync((_, _) => harness.Nodes.Any(n => n.Channel(channelId).FundingOutput!.TransactionId
                                                                   != firstTxId));
        var constructed = harness.Nodes.Single(n => n.Channel(channelId).FundingOutput!.TransactionId != firstTxId);

        // Act: that node restarts; the other forgot its unconstructed attempt, so next_funding gets tx_abort
        await harness.RestartAsync(constructed);
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: both back on the first attempt with its own fee (rebuilt from the stored rows on the restarted node)
        Assert.Contains(harness.Transcript, t => t.Message is TxAbortMessage);
        var fees = Fees(2_500);
        await AssertBalancesAsync(harness, channelId, 600_000, RequestedSat, fees.TotalMsat);

        // ...and the first attempt opens the channel: its purchase active, the aborted attempt's (if stored) replaced
        await harness.ConfirmFundingAsync(channelId, firstTxId);
        foreach (var node in harness.Nodes)
        {
            var purchases = await PurchasesAsync(node, channelId);
            Assert.Equal(LiquidityPurchaseStatus.Active, purchases.Single(p => p.FundingTxId == firstTxId).Status);
            Assert.All(purchases.Where(p => p.FundingTxId != firstTxId),
                       p => Assert.Equal(LiquidityPurchaseStatus.Replaced, p.Status));
        }

        await AssertBookedAsync(harness, channelId, firstTxId, 600_000, RequestedSat, fees,
                                AccountingDetailKeys.LiquidityKindOpen);
        await PayBothWaysAsync(harness, channelId);
    }

    private static void AssertPurchase(LiquidityPurchaseModel purchase, LiquidityPurchaseRole role,
                                       LiquidityPurchaseKind kind, TxId fundingTxId, DualFundNode peer,
                                       LiquidityFees fees)
    {
        Assert.Equal(role, purchase.Role);
        Assert.Equal(kind, purchase.Kind);
        Assert.Equal(fundingTxId, purchase.FundingTxId);
        Assert.Equal(LiquidityPurchaseStatus.Pending, purchase.Status);
        Assert.Equal(RequestedSat, purchase.RequestedSat);
        Assert.Equal(RequestedSat, purchase.ContributedSat);
        Assert.Equal(Rate().ToFundingRate(), purchase.Rate);
        Assert.Equal(LiquidityPaymentType.FromChannelBalance, purchase.PaymentType);
        Assert.Equal(fees, purchase.Fees);
        Assert.Equal(peer.NodeId, purchase.PeerNodeId);
        Assert.Equal(LiquidityAdsConstants.LeaseBlocks, purchase.LeaseBlocks);
        Assert.True(purchase.Id > 0);
    }

    /// <summary>
    /// Each node's feed at the confirmation: ChannelFunded of its own share (its liquidityFeeMsat detail the signed fee)
    /// and the fee as LiquidityFeePaid (Alice) or LiquidityFeeEarned (Bob) of the confirmed attempt, nothing for the
    /// other attempts; the books' channels account then holds the channel balance and the fee is an expense or income.
    /// </summary>
    private static async Task AssertBookedAsync(DualFundHarness harness, ChannelId channelId, TxId txId,
                                                ulong aliceShareSat, ulong bobShareSat, LiquidityFees fees,
                                                string kind)
    {
        var feeMsat = checked((long)fees.TotalMsat);
        foreach (var (node, shareSat, weBought) in new[] { (harness.Alice, aliceShareSat, true),
                                                           (harness.Bob, bobShareSat, false) })
        {
            var events = await EventsAsync(node);
            var funded = Assert.Single(events, e => e.Kind == AccountingEventKind.ChannelFunded);
            Assert.Equal(AccountingEventKeys.ChannelFunded(channelId, txId), funded.EventKey);
            Assert.Equal(checked((long)shareSat * 1_000), funded.AmountMsat);
            Assert.Equal((weBought ? feeMsat : -feeMsat).ToString(System.Globalization.CultureInfo.InvariantCulture),
                         funded.Details[AccountingDetailKeys.LiquidityFeeMsat]);

            var fee = Assert.Single(events, e => e.Kind is AccountingEventKind.LiquidityFeePaid
                                                       or AccountingEventKind.LiquidityFeeEarned);
            Assert.Equal(weBought ? AccountingEventKind.LiquidityFeePaid : AccountingEventKind.LiquidityFeeEarned,
                         fee.Kind);
            Assert.Equal(AccountingEventKeys.LiquidityFee(channelId, txId), fee.EventKey);
            Assert.Equal(weBought ? -feeMsat : feeMsat, fee.AmountMsat);
            Assert.Equal(weBought ? feeMsat : 0, fee.FeeMsat);
            Assert.Equal(DualFundHarness.FundingHeight, fee.BlockHeight);
            Assert.Equal(txId, fee.TxId);
            Assert.Equal(kind, fee.Details[AccountingDetailKeys.Kind]);

            var books = BooksSimulator.Of(events.OrderBy(e => e.Id));
            Assert.Equal(checked((long)node.Channel(channelId).LocalBalance.MilliSatoshi),
                         books[AccountRole.Channels]);
            if (weBought)
                Assert.Equal(feeMsat, books[AccountRole.LiquidityFees]);
            else
                Assert.Equal(-feeMsat, books[AccountRole.LiquidityIncome]);
        }
    }

    private static async Task PayBothWaysAsync(DualFundHarness harness, ChannelId channelId)
    {
        var aliceBefore = harness.Alice.Channel(channelId).LocalBalance;
        await harness.Alice.PayAsync(harness.Bob, channelId, LightningMoney.Satoshis(50_000));
        await harness.PumpAsync();
        await harness.Bob.PayAsync(harness.Alice, channelId, LightningMoney.Satoshis(20_000));
        await harness.PumpAsync();

        Assert.True(harness.Alice.PaymentHandler.Fulfilled.Count == 1, harness.Describe());
        Assert.True(harness.Bob.PaymentHandler.Fulfilled.Count == 1, harness.Describe());
        Assert.Equal(aliceBefore - LightningMoney.Satoshis(30_000), harness.Alice.Channel(channelId).LocalBalance);
    }
}