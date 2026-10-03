using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Splicing;
using Application.LiquidityAds;
using Domain.Accounting.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Quiescence;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Models;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Harness;

/// <summary>
/// Liquidity ads in a splice and its RBF attempts (NL-850, plan <c>docs/agents/LIQUIDITY_ADS_PLAN.md</c> LA3/LA4/LA6)
/// on the real engine (<see cref="SpliceHarness"/> with <c>realEngine</c>): the buyer's <c>splice_init</c> carries
/// <c>request_funding</c>, the seller contributes exactly the requested amount and answers <c>provide_funding</c>
/// signed over the new funding script, the fee moves from the buyer's balance to the seller's on the new funding
/// (balance deltas), each attempt is recorded as a purchase, and the lock activates it and books the fee.
/// </summary>
public class SpliceLiquidityAdsTests
{
    private const uint CltvExpiry = 700;
    private const long SpliceIn = 50_000;
    private const ulong Requested = 200_000;
    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    #region (a) a splice that buys liquidity, each side buying

    /// <summary>
    /// The buyer splices in 50,000 sat and buys 200,000 sat of inbound liquidity: the seller contributes exactly that,
    /// the fee (400 sat mining + 3,000 sat service at 1,000 sat/kw) leaves the buyer's balance for the seller's on both
    /// nodes' commitments of the new funding, both nodes record the purchase, payments flow while pending, and the lock
    /// makes the purchase active and books <c>SpliceLocked</c> and the liquidity fee, which net to the balance change.
    /// </summary>
    [Theory]
    [InlineData("Alice")]
    [InlineData("Bob")]
    public async Task Given_ASpliceThatBuysLiquidity_When_SignedUsedAndLocked_Then_TheFeeMovesAndIsBooked(
        string buyerName)
    {
        // Arrange
        using var harness = CreateHarness(buyerName == "Alice" ? "Bob" : "Alice");
        var buyer = buyerName == "Alice" ? harness.Alice : harness.Bob;
        var seller = harness.Other(buyer);
        buyer.Fund(SpliceIn + 200_000);
        seller.Fund((long)Requested + 200_000);
        var buyerBefore = buyer.Node.State.LocalBalanceMsat;
        var sellerBefore = seller.Node.State.LocalBalanceMsat;
        var fee = FeeMsat(SpliceHarness.FeeratePerKw);

        // Act
        var result = await BuyAsync(harness, buyer, SpliceIn, new LiquidityRequest(Requested));

        // Assert: the request and the seller's signed answer on the wire
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        Assert.Empty(harness.Failures);
        var spliceTx = result.SpliceTxId!.Value;
        var init = harness.Transcript.Select(t => t.Message).OfType<SpliceInitMessage>().Single();
        Assert.Equal(new RequestFunding(Requested, SpliceLiquidityTestKit.Rate,
                                        LiquidityPaymentDetails.FromChannelBalance), init.RequestFundingTlv!.Request);
        var ack = harness.Transcript.Select(t => t.Message).OfType<SpliceAckMessage>().Single();
        Assert.Equal((long)Requested, ack.Payload.FundingContributionSatoshis);
        Assert.Equal(SpliceLiquidityTestKit.Rate, ack.ProvideFundingTlv!.WillFund.Rate);

        // The fee in both nodes' deltas, the capacity from the contributions alone
        foreach (var (node, local, remote) in new[]
                 {
                     (buyer, SpliceIn * 1_000 - fee, (long)Requested * 1_000 + fee),
                     (seller, (long)Requested * 1_000 + fee, SpliceIn * 1_000 - fee)
                 })
        {
            var pending = Assert.Single(node.Node.State.PendingFundings);
            Assert.Equal(spliceTx, pending.FundingTxId);
            Assert.Equal(TwoNodeHarness.FundingSatoshis + (ulong)SpliceIn + Requested, pending.CapacitySatoshis);
            Assert.Equal(local, pending.LocalBalanceDeltaMsat);
            Assert.Equal(remote, pending.RemoteBalanceDeltaMsat);
            Assert.Equal(local, node.FundingRows.Committed[spliceTx].LocalBalanceDeltaMsat);
        }

        // One purchase row per node, the seller's slot released, its reservation kept for the splice's inputs
        AssertPurchase(buyer, spliceTx, LiquidityPurchaseRole.Buyer, LiquidityPurchaseKind.Splice,
                       LiquidityPurchaseStatus.Pending, SpliceHarness.FeeratePerKw);
        AssertPurchase(seller, spliceTx, LiquidityPurchaseRole.Seller, LiquidityPurchaseKind.Splice,
                       LiquidityPurchaseStatus.Pending, SpliceHarness.FeeratePerKw);
        Assert.Equal(LiquidityPurchaseRole.Buyer, result.Purchase!.Role);
        Assert.Equal((ulong)fee, result.Purchase.TotalFeeMsat);
        Assert.Equal(0, LiquidityAds(seller).SalesInProgress);
        Assert.Single(seller.Contributor.ActiveReservations);

        // Act: a payment each way while pending
        var (aliceId, alicePreimage) = await OfferAsync(harness, harness.Alice, 20_000_000, 1);
        await FulfillAsync(harness, harness.Bob, aliceId, alicePreimage);
        var (bobId, bobPreimage) = await OfferAsync(harness, harness.Bob, 5_000_000, 2);
        await FulfillAsync(harness, harness.Alice, bobId, bobPreimage);
        Assert.Empty(harness.Failures);

        // Act: the lock
        await harness.ConfirmAsync(spliceTx, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);

        // Assert: the new balances carry the fee and the payments
        var buyerNet = buyer == harness.Alice ? -15_000_000L : 15_000_000L;
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Node.State.PendingFundings);
            Assert.Equal(spliceTx, node.Node.State.Params.Funding!.FundingTxId);
            Assert.Equal(ChannelState.Open, node.Node.Channel.State);
        }

        Assert.Equal((long)buyerBefore + SpliceIn * 1_000 - fee + buyerNet, (long)buyer.Node.State.LocalBalanceMsat);
        Assert.Equal((long)sellerBefore + (long)Requested * 1_000 + fee - buyerNet,
                     (long)seller.Node.State.LocalBalanceMsat);

        // The purchases are active from the splice's height
        foreach (var node in new[] { buyer, seller })
        {
            var row = Assert.Single(node.Purchases.Committed);
            Assert.Equal(LiquidityPurchaseStatus.Active, row.Status);
            Assert.Equal(TwoNodeHarness.BlockHeight + 3, row.LeaseStartHeight);
        }

        // SpliceLocked books the contribution, the liquidity event the fee: together the balance change
        AssertLockEvents(buyer, SpliceIn * 1_000, AccountingEventKind.LiquidityFeePaid, -fee);
        AssertLockEvents(seller, (long)Requested * 1_000, AccountingEventKind.LiquidityFeeEarned, fee);

        // Act / Assert: a payment over the new funding
        await OfferAsync(harness, harness.Alice, 1_000_000, 3);
        Assert.Empty(harness.Failures);
    }

    #endregion

    #region (b) splice RBF with a re-purchase

    /// <summary>
    /// Alice buys with her splice, then bumps it without naming a purchase: her <c>tx_init_rbf</c> requests the same
    /// liquidity again (BOLT PR #1153), Bob signs it again for the attempt and contributes the same amount, and the fee
    /// follows the new feerate (800 sat mining instead of 400). Each attempt has its purchase row; the lock of the bump
    /// activates its purchase, replaces the first one and books the bump's fee only.
    /// </summary>
    [Fact]
    public async Task Given_ASpliceThatBoughtLiquidity_When_TheBuyerBumpsIt_Then_ThePurchaseIsRepeatedAtTheNewFeerate()
    {
        // Arrange
        using var harness = CreateHarness("Bob");
        harness.Alice.Fund(SpliceIn + 200_000);
        harness.Bob.Fund((long)Requested + 200_000);
        var aliceBefore = harness.Alice.Node.State.LocalBalanceMsat;
        var bobBefore = harness.Bob.Node.State.LocalBalanceMsat;
        var first = (await BuyAsync(harness, harness.Alice, SpliceIn, new LiquidityRequest(Requested))).SpliceTxId!
                                                                                                        .Value;
        var mark = harness.Transcript.Count;

        // Act
        var bump = await BumpAsync(harness, harness.Alice, new SpliceBumpRequest(TwoNodeHarness.ChannelId, 2_000));

        // Assert: the request repeated, answered and signed again
        Assert.True(bump.State == SpliceNegotiationState.Signed, $"{bump.State}: {bump.FailureReason}");
        Assert.Empty(harness.Failures);
        var second = bump.SpliceTxId!.Value;
        var initRbf = harness.Transcript.Skip(mark).Select(t => t.Message).OfType<TxInitRbfMessage>().Single();
        Assert.Equal(Requested, initRbf.RequestFundingTlv!.Request.RequestedSat);
        var ackRbf = harness.Transcript.Skip(mark).Select(t => t.Message).OfType<TxAckRbfMessage>().Single();
        Assert.Equal((long)Requested, ackRbf.FundingOutputContributionTlv!.Satoshis);
        Assert.NotNull(ackRbf.ProvideFundingTlv);
        Assert.Equal(LiquidityPurchaseKind.SpliceRbf, bump.Purchase!.Kind);

        var bumpFee = FeeMsat(2_000);
        Assert.NotEqual(FeeMsat(SpliceHarness.FeeratePerKw), bumpFee);
        var attempt = harness.Alice.Node.State.PendingFundings.Single(f => f.FundingTxId == second);
        Assert.Equal(TwoNodeHarness.FundingSatoshis + (ulong)SpliceIn + Requested, attempt.CapacitySatoshis);
        Assert.Equal(SpliceIn * 1_000 - bumpFee, attempt.LocalBalanceDeltaMsat);
        Assert.Equal((long)Requested * 1_000 + bumpFee,
                     harness.Bob.Node.State.PendingFundings.Single(f => f.FundingTxId == second)
                            .LocalBalanceDeltaMsat);
        AssertPurchase(harness.Alice, second, LiquidityPurchaseRole.Buyer, LiquidityPurchaseKind.SpliceRbf,
                       LiquidityPurchaseStatus.Pending, 2_000);
        AssertPurchase(harness.Bob, second, LiquidityPurchaseRole.Seller, LiquidityPurchaseKind.SpliceRbf,
                       LiquidityPurchaseStatus.Pending, 2_000);
        Assert.Equal(0, LiquidityAds(harness.Bob).SalesInProgress);

        // Act: the bump locks
        await harness.ConfirmAsync(second, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);

        // Assert
        Assert.Empty(harness.Failures);
        Assert.Equal((long)aliceBefore + SpliceIn * 1_000 - bumpFee, (long)harness.Alice.Node.State.LocalBalanceMsat);
        Assert.Equal((long)bobBefore + (long)Requested * 1_000 + bumpFee,
                     (long)harness.Bob.Node.State.LocalBalanceMsat);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var rows = node.Purchases.Committed;
            Assert.Equal(LiquidityPurchaseStatus.Replaced, rows.Single(r => r.FundingTxId == first).Status);
            Assert.Equal(LiquidityPurchaseStatus.Active, rows.Single(r => r.FundingTxId == second).Status);
        }

        AssertLockEvents(harness.Alice, SpliceIn * 1_000, AccountingEventKind.LiquidityFeePaid, -bumpFee);
        AssertLockEvents(harness.Bob, (long)Requested * 1_000, AccountingEventKind.LiquidityFeeEarned, bumpFee);
    }

    /// <summary>
    /// The buyer bumps with a larger purchase: the seller's rebuilt contribution grows to the new amount (from the same
    /// wallet input), the capacity and the fee follow it, and the bump's purchase records it.
    /// </summary>
    [Fact]
    public async Task Given_ASpliceThatBoughtLiquidity_When_TheBumpBuysMore_Then_TheSellerContributesTheNewAmount()
    {
        // Arrange
        using var harness = CreateHarness("Bob");
        harness.Alice.Fund(SpliceIn + 200_000);
        harness.Bob.Fund((long)Requested + 200_000);
        await BuyAsync(harness, harness.Alice, SpliceIn, new LiquidityRequest(Requested));
        const ulong more = 300_000;

        // Act
        var bump = await BumpAsync(harness, harness.Alice,
                                   new SpliceBumpRequest(TwoNodeHarness.ChannelId, 2_000)
                                   {
                                       Liquidity = new LiquidityRequest(more)
                                   });

        // Assert
        Assert.True(bump.State == SpliceNegotiationState.Signed, $"{bump.State}: {bump.FailureReason}");
        Assert.Empty(harness.Failures);
        var fee = (long)LiquidityAdsRules.ComputeFees(SpliceLiquidityTestKit.Rate, 2_000, more, more, false).TotalMsat;
        var attempt = harness.Bob.Node.State.PendingFundings.Single(f => f.FundingTxId == bump.SpliceTxId);
        Assert.Equal(TwoNodeHarness.FundingSatoshis + (ulong)SpliceIn + more, attempt.CapacitySatoshis);
        Assert.Equal((long)more * 1_000 + fee, attempt.LocalBalanceDeltaMsat);
        Assert.Equal(SpliceIn * 1_000 - fee, attempt.RemoteBalanceDeltaMsat);
        var row = harness.Alice.Purchases.Committed.Single(p => p.FundingTxId == bump.SpliceTxId);
        Assert.Equal(more, row.RequestedSat);
        Assert.Equal(more, row.ContributedSat);
        Assert.Single(harness.Bob.Contributor.ActiveReservations);
    }

    /// <summary>
    /// The seller's answer to the bump is above the buyer's fee limit: the buyer refuses its <c>tx_ack_rbf</c> with
    /// <c>tx_abort</c>, the bump reports why, the first attempt stays the only one and its purchase is untouched.
    /// </summary>
    [Fact]
    public async Task Given_ABumpWhoseFeeIsAboveTheLimit_When_TheSellerAnswers_Then_TheBuyerAbortsTheAttempt()
    {
        // Arrange
        using var harness = CreateHarness("Bob");
        harness.Alice.Fund(SpliceIn + 200_000);
        harness.Bob.Fund((long)Requested + 200_000);
        var first = (await BuyAsync(harness, harness.Alice, SpliceIn, new LiquidityRequest(Requested))).SpliceTxId!
                                                                                                        .Value;

        // Act
        var bump = await BumpAsync(harness, harness.Alice,
                                   new SpliceBumpRequest(TwoNodeHarness.ChannelId, 2_000)
                                   {
                                       Liquidity = new LiquidityRequest(Requested, null, 1_000)
                                   });

        // Assert
        Assert.Equal(SpliceNegotiationState.Aborted, bump.State);
        Assert.Contains(nameof(LiquidityAdsRefusal.FeeTooHigh), bump.FailureReason);
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Equal([first], node.Node.State.PendingFundings.Select(f => f.FundingTxId));
            Assert.Equal(LiquidityPurchaseStatus.Pending, Assert.Single(node.Purchases.Committed).Status);
            Assert.False(node.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
        }

        Assert.Equal(0, LiquidityAds(harness.Bob).SalesInProgress);
    }

    /// <summary>The seller of the pending splice's liquidity may not bump it: only the buyer can request it again.
    /// </summary>
    [Fact]
    public async Task Given_ASpliceThatSoldLiquidity_When_TheSellerBumpsIt_Then_Refused()
    {
        // Arrange
        using var harness = CreateHarness("Bob");
        harness.Alice.Fund(SpliceIn + 200_000);
        harness.Bob.Fund((long)Requested + 200_000);
        await BuyAsync(harness, harness.Alice, SpliceIn, new LiquidityRequest(Requested));

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => harness.Bob.Service.BumpAsync(new SpliceBumpRequest(TwoNodeHarness.ChannelId, 2_000),
                                                               TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("LA-RBF-01", exception.Message);
        Assert.Single(harness.Alice.Node.State.PendingFundings);
    }

    /// <summary>
    /// BOLT PR #1153: an RBF of a splice that bought liquidity MUST request it again; the seller answers a
    /// <c>tx_init_rbf</c> without <c>request_funding</c> with <c>tx_abort</c> and keeps the splice as it is.
    /// </summary>
    [Fact]
    public async Task Given_ASpliceThatSoldLiquidity_When_TheBuyersRbfDropsTheRequest_Then_TxAbort()
    {
        // Arrange: Alice bought, then is the quiescence initiator again
        using var harness = CreateHarness("Bob");
        harness.Alice.Fund(SpliceIn + 200_000);
        harness.Bob.Fund((long)Requested + 200_000);
        var first = (await BuyAsync(harness, harness.Alice, SpliceIn, new LiquidityRequest(Requested))).SpliceTxId!
                                                                                                        .Value;
        await QuiesceAsync(harness, harness.Alice, QuiescencePurpose.SpliceRbf);
        var message = new TxInitRbfMessage(new TxInitRbfPayload(TwoNodeHarness.ChannelId, 2_000, 0),
                                           new FundingOutputContributionTlv(SpliceIn));

        // Act
        var replies = await HandleAsync(harness.Bob,
                                        (service, unitOfWork) =>
                                            service.HandleTxInitRbfAsync(message, SpliceHarness.CreateFeatures(),
                                                                         SpliceHarness.NodeIdOf("Alice"), unitOfWork,
                                                                         TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("must request the liquidity", AbortText(replies));
        Assert.Equal([first], harness.Bob.Node.State.PendingFundings.Select(f => f.FundingTxId));
        Assert.False(harness.Bob.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
    }

    #endregion

    #region (c) restarts

    /// <summary>
    /// The pending splice's purchase across a restart of each node: the fee stays in the deltas the restarted node loads
    /// from its funding rows, payments keep flowing, and the lock (made after both restarts) moves the fee and books it.
    /// </summary>
    [Fact]
    public async Task Given_APendingSpliceThatBoughtLiquidity_When_EachNodeRestarts_Then_TheFeeIsKept()
    {
        // Arrange
        using var harness = CreateHarness("Bob");
        harness.Alice.Fund(SpliceIn + 200_000);
        harness.Bob.Fund((long)Requested + 200_000);
        var aliceBefore = harness.Alice.Node.State.LocalBalanceMsat;
        var bobBefore = harness.Bob.Node.State.LocalBalanceMsat;
        var spliceTx = (await BuyAsync(harness, harness.Alice, SpliceIn, new LiquidityRequest(Requested)))
                      .SpliceTxId!.Value;
        var fee = FeeMsat(SpliceHarness.FeeratePerKw);

        // Act
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            await harness.RestartAsync(node);
            await harness.Harness.ReconnectAsync();
            await harness.PumpAsync();
        }

        // Assert
        Assert.Empty(harness.Failures);
        Assert.Equal(SpliceIn * 1_000 - fee, Assert.Single(harness.Alice.Node.State.PendingFundings).LocalBalanceDeltaMsat);
        Assert.Equal((long)Requested * 1_000 + fee,
                     Assert.Single(harness.Bob.Node.State.PendingFundings).LocalBalanceDeltaMsat);
        var (id, preimage) = await OfferAsync(harness, harness.Alice, 10_000_000, 1);
        await FulfillAsync(harness, harness.Bob, id, preimage);

        await harness.ConfirmAsync(spliceTx, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);
        Assert.Empty(harness.Failures);
        Assert.Equal((long)aliceBefore + SpliceIn * 1_000 - fee - 10_000_000,
                     (long)harness.Alice.Node.State.LocalBalanceMsat);
        Assert.Equal((long)bobBefore + (long)Requested * 1_000 + fee + 10_000_000,
                     (long)harness.Bob.Node.State.LocalBalanceMsat);
        AssertLockEvents(harness.Alice, SpliceIn * 1_000, AccountingEventKind.LiquidityFeePaid, -fee);
        AssertLockEvents(harness.Bob, (long)Requested * 1_000, AccountingEventKind.LiquidityFeeEarned, fee);
    }

    /// <summary>
    /// Both splice <c>commitment_signed</c> are lost and the buyer restarts: its negotiation is resumed from the stored
    /// rows on <c>channel_reestablish</c> with the contributions taken out of the deltas without the fee (the purchase
    /// row), the splice completes, and the fee stays where the purchase put it.
    /// </summary>
    [Fact]
    public async Task Given_BothCommitSigsLostAndTheBuyerRestarted_When_Reconnected_Then_TheSpliceCompletesWithTheFee()
    {
        // Arrange
        using var harness = CreateHarness("Bob");
        harness.Alice.Fund(SpliceIn + 200_000);
        harness.Bob.Fund((long)Requested + 200_000);
        var start = harness.Alice.Service.StartAsync(
            new SpliceRequest(TwoNodeHarness.ChannelId, SpliceIn, SpliceHarness.FeeratePerKw)
            {
                Liquidity = new LiquidityRequest(Requested)
            }, TestContext.Current.CancellationToken);
        await PumpHoldingAsync(harness, static (_, m) => m is CommitmentSignedMessage);
        var lost = (CommitmentSignedMessage)harness.Alice.Node.PeekNext()!;
        var spliceTx = lost.FundingTxIdTlv!.FundingTxId;
        var fee = FeeMsat(SpliceHarness.FeeratePerKw);

        // Act
        await harness.RestartAsync(harness.Alice);
        await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: completed with the contributions as negotiated and the fee in the deltas
        Assert.Empty(harness.Failures);
        var resumed = harness.Alice.Service.GetNegotiation(TwoNodeHarness.ChannelId)!;
        Assert.Equal(SpliceNegotiationState.Signed, resumed.State);
        Assert.Equal(SpliceIn, resumed.LocalContributionSatoshis);
        Assert.Equal((long)Requested, resumed.RemoteContributionSatoshis);
        foreach (var (node, local) in new[]
                 {
                     (harness.Alice, SpliceIn * 1_000 - fee), (harness.Bob, (long)Requested * 1_000 + fee)
                 })
        {
            var pending = Assert.Single(node.Node.State.PendingFundings);
            Assert.Equal(spliceTx, pending.FundingTxId);
            Assert.Equal(local, pending.LocalBalanceDeltaMsat);
            Assert.Single(node.Broadcasts);
        }

        await harness.ConfirmAsync(spliceTx, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);
        Assert.Empty(harness.Failures);
        AssertLockEvents(harness.Alice, SpliceIn * 1_000, AccountingEventKind.LiquidityFeePaid, -fee);
    }

    #endregion

    #region (d) refusals

    public static TheoryData<string, string> SellerRefusals => new()
    {
        { "not-selling", "we do not sell liquidity" },
        { "unknown-rate", nameof(LiquidityAdsRefusal.UnknownRate) },
        { "amount-out-of-range", nameof(LiquidityAdsRefusal.AmountOutOfRange) },
        { "unsupported-payment", nameof(LiquidityAdsRefusal.UnsupportedPaymentType) },
        { "wallet-empty", "cannot fund the requested amount" },
        { "griefing-cap", "too many liquidity sales" },
        { "buyer-cannot-pay", "LA-RES-01" }
    };

    /// <summary>
    /// The seller's refusals of a <c>splice_init</c> with <c>request_funding</c>, each a <c>tx_abort</c> that ends the
    /// quiescence: we do not sell, a rate we do not offer, an amount outside the rate, a payment type we do not take,
    /// a wallet that cannot fund the amount, the griefing cap, and a buyer whose balance cannot pay the fee and keep
    /// its reserve. Nothing is reserved or held afterwards.
    /// </summary>
    [Theory]
    [MemberData(nameof(SellerRefusals))]
    public async Task Given_ABadLiquidityRequest_When_TheSellerGetsSpliceInit_Then_TxAbort(string scenario,
                                                                                            string expected)
    {
        // Arrange: Bob sells (except "not-selling"), Alice is the quiescence initiator
        var expensive = new FundingRate(10_000, 1_000_000, 400, 0, 1_290_000, 0);
        using var harness = new SpliceHarness(realEngine: true,
                                              configureNode: (name, o) =>
                                              {
                                                  if (name == "Bob" && scenario != "not-selling")
                                                      SpliceLiquidityTestKit.Sell(
                                                          o, scenario == "buyer-cannot-pay"
                                                                 ? expensive
                                                                 : SpliceLiquidityTestKit.Rate);
                                              });
        if (scenario != "wallet-empty")
            harness.Bob.Fund((long)Requested + 200_000);
        using var held = scenario == "griefing-cap" ? HoldSaleSlot(harness.Bob) : null;
        var request = scenario switch
        {
            "unknown-rate" => Request(Requested, SpliceLiquidityTestKit.Rate with { FeeBasis = 1 }),
            "amount-out-of-range" => Request(5_000, SpliceLiquidityTestKit.Rate),
            "unsupported-payment" => new RequestFunding(Requested, SpliceLiquidityTestKit.Rate,
                                                        LiquidityPaymentDetails.Create(128, new byte[32])),
            "buyer-cannot-pay" => Request(Requested, expensive),
            _ => Request(Requested, SpliceLiquidityTestKit.Rate)
        };
        await QuiesceAsync(harness, harness.Alice, QuiescencePurpose.Splice);
        var message = new SpliceInitMessage(new SpliceInitPayload(TwoNodeHarness.ChannelId, 0,
                                                                  SpliceHarness.FeeratePerKw, 0, NewKey()),
                                            null, new RequestFundingTlv(request));

        // Act
        var replies = await HandleAsync(harness.Bob,
                                        (service, unitOfWork) =>
                                            service.HandleSpliceInitAsync(message, SpliceHarness.CreateFeatures(),
                                                                          SpliceHarness.NodeIdOf("Alice"), unitOfWork,
                                                                          TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains(expected, AbortText(replies));
        Assert.Null(harness.Bob.Service.GetNegotiation(TwoNodeHarness.ChannelId));
        Assert.Empty(harness.Bob.Contributor.ActiveReservations);
        Assert.Equal(held is null ? 0 : 1, LiquidityAds(harness.Bob).SalesInProgress);
        Assert.False(harness.Bob.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
        Assert.Empty(harness.Bob.Purchases.Committed);
    }

    /// <summary>
    /// The fee is above the buyer's limit (here 1,000 sat for a 3,400 sat purchase): the buyer refuses the seller's
    /// answer with <c>tx_abort</c>, its splice reports why, and the seller releases what it reserved.
    /// </summary>
    [Fact]
    public async Task Given_AFeeAboveTheBuyersLimit_When_TheSellerAnswers_Then_TheBuyerAbortsAndReportsWhy()
    {
        // Arrange
        using var harness = CreateHarness("Bob");
        harness.Alice.Fund(SpliceIn + 200_000);
        harness.Bob.Fund((long)Requested + 200_000);

        // Act
        var result = await BuyAsync(harness, harness.Alice, SpliceIn, new LiquidityRequest(Requested, null, 1_000));

        // Assert
        AssertBuyerRefused(harness, result, nameof(LiquidityAdsRefusal.FeeTooHigh));
    }

    public static TheoryData<string, string> TamperedAnswers => new()
    {
        { "missing", nameof(LiquidityAdsRefusal.Missing) },
        { "bad-signature", nameof(LiquidityAdsRefusal.BadSignature) },
        { "short-contribution", nameof(LiquidityAdsRefusal.AmountTooLow) },
        { "other-rate", nameof(LiquidityAdsRefusal.RateMismatch) }
    };

    /// <summary>
    /// The buyer's checks of the seller's <c>splice_ack</c> (Eclair <c>validateRemoteFunding</c>): no
    /// <c>provide_funding</c>, a signature over another funding script, a contribution below the request, an answer at
    /// another rate. Each is our <c>tx_abort</c>, the splice reports why, and nothing is recorded.
    /// </summary>
    [Theory]
    [MemberData(nameof(TamperedAnswers))]
    public async Task Given_ATamperedAnswer_When_TheBuyerChecksIt_Then_TxAbortAndTheReason(string scenario,
                                                                                         string expected)
    {
        // Arrange: Bob's splice_ack is held back
        using var harness = CreateHarness("Bob");
        harness.Alice.Fund(SpliceIn + 200_000);
        harness.Bob.Fund((long)Requested + 200_000);
        var start = harness.Alice.Service.StartAsync(
            new SpliceRequest(TwoNodeHarness.ChannelId, SpliceIn, SpliceHarness.FeeratePerKw)
            {
                Liquidity = new LiquidityRequest(Requested)
            }, TestContext.Current.CancellationToken);
        await PumpHoldingAsync(harness, static (from, m) => from == "Bob" && m is SpliceAckMessage, start);
        Assert.True(harness.Bob.Node.TryTakeNext(out var held));
        var ack = (SpliceAckMessage)held;
        var willFund = ack.ProvideFundingTlv!.WillFund;
        var tampered = scenario switch
        {
            "missing" => new SpliceAckMessage(ack.Payload, ack.RequireConfirmedInputsTlv),
            "bad-signature" => WithWillFund(ack, LiquidityAds(harness.Bob)
                                                    .CreateWillFund(willFund.Rate, new byte[34])),
            "short-contribution" => new SpliceAckMessage(
                new SpliceAckPayload(TwoNodeHarness.ChannelId, (long)Requested - 1, ack.Payload.FundingPubKey),
                ack.RequireConfirmedInputsTlv, ack.ProvideFundingTlv),
            _ => WithWillFund(ack, new WillFund(willFund.Rate with { FeeBasis = 1 }, willFund.FundingScript,
                                                willFund.Signature))
        };

        // Act
        await harness.Alice.Node.ChannelManager.HandleChannelMessageAsync(tampered, harness.Bob.Node.NegotiatedFeatures,
                                                                          harness.Bob.Node.NodeId);
        await harness.PumpAsync(start);
        var result = await start;

        // Assert
        AssertBuyerRefused(harness, result, expected);
    }

    /// <summary>
    /// LA-RES-01 on the buyer's side: a purchase whose fee would leave Bob below his reserve on the new funding is
    /// refused before anything is sent.
    /// </summary>
    [Fact]
    public async Task Given_ABuyerThatCannotPayTheFee_When_ItSplices_Then_RefusedBeforeSpliceInit()
    {
        // Arrange: Alice sells at an 840,000 sat flat fee; Bob has 800,000 sat and splices in 50,000 (he would keep
        // 9,600 sat, below the 22,500 sat reserve of the new 2,250,000 sat capacity)
        var expensive = new FundingRate(10_000, 1_000_000, 400, 0, 840_000, 0);
        using var harness = new SpliceHarness(realEngine: true,
                                              configureNode: (name, o) =>
                                              {
                                                  if (name == "Alice")
                                                      SpliceLiquidityTestKit.Sell(o, expensive);
                                              });
        harness.Bob.Fund(SpliceIn + 200_000);
        var mark = harness.Transcript.Count;

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => harness.Bob.Service.StartAsync(
                                new SpliceRequest(TwoNodeHarness.ChannelId, SpliceIn, SpliceHarness.FeeratePerKw)
                                {
                                    Liquidity = new LiquidityRequest(Requested)
                                }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("LA-RES-01", exception.Message);
        Assert.Equal(mark, harness.Transcript.Count);
        Assert.Empty(harness.Bob.Contributor.ActiveReservations);
    }

    /// <summary>A peer that advertises no rates sells nothing: the purchase is refused before anything is sent.</summary>
    [Fact]
    public async Task Given_APeerThatSellsNothing_When_WeBuy_Then_RefusedBeforeSpliceInit()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(SpliceIn + 200_000);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => harness.Alice.Service.StartAsync(
                                new SpliceRequest(TwoNodeHarness.ChannelId, SpliceIn, SpliceHarness.FeeratePerKw)
                                {
                                    Liquidity = new LiquidityRequest(Requested)
                                }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("does not advertise liquidity rates", exception.Message);
        Assert.Empty(harness.Transcript);
    }

    #endregion

    #region Helpers

    private static SpliceHarness CreateHarness(string seller) =>
        new((_, o) => o.MinRbfInterval = TimeSpan.Zero, realEngine: true,
            configureNode: (name, o) =>
            {
                if (name == seller)
                    SpliceLiquidityTestKit.Sell(o);
            });

    private static long FeeMsat(uint feeratePerKw) =>
        (long)LiquidityAdsRules.ComputeFees(SpliceLiquidityTestKit.Rate, feeratePerKw, Requested, Requested, false)
                               .TotalMsat;

    private static RequestFunding Request(ulong amountSat, FundingRate rate) =>
        new(amountSat, rate, LiquidityPaymentDetails.FromChannelBalance);

    private static CompactPubKey NewKey() => new Key().PubKey.ToBytes();

    private static LiquidityAdsService LiquidityAds(SpliceNode node) =>
        node.Node.Services.GetRequiredService<LiquidityAdsService>();

    private static IDisposable HoldSaleSlot(SpliceNode seller)
    {
        Assert.Null(LiquidityAds(seller).TryStartSale(SpliceHarness.NodeIdOf("Alice"),
                                                      Request(Requested, SpliceLiquidityTestKit.Rate),
                                                      out var sale));
        return sale!;
    }

    private static SpliceAckMessage WithWillFund(SpliceAckMessage ack, WillFund willFund) =>
        new(ack.Payload, ack.RequireConfirmedInputsTlv, new ProvideFundingTlv(willFund));

    private static async Task<SpliceResult> BuyAsync(SpliceHarness harness, SpliceNode buyer, long contribution,
                                                     LiquidityRequest liquidity)
    {
        var start = buyer.Service.StartAsync(
            new SpliceRequest(TwoNodeHarness.ChannelId, contribution, SpliceHarness.FeeratePerKw)
            {
                Liquidity = liquidity
            }, TestContext.Current.CancellationToken);
        await harness.PumpAsync(start);
        return await start;
    }

    private static async Task<SpliceResult> BumpAsync(SpliceHarness harness, SpliceNode node,
                                                      SpliceBumpRequest request)
    {
        var bump = node.Service.BumpAsync(request, TestContext.Current.CancellationToken);
        await harness.PumpAsync(bump);
        return await bump;
    }

    /// <summary><paramref name="initiator"/> quiesces the channel (both sides quiescent).</summary>
    private static async Task QuiesceAsync(SpliceHarness harness, SpliceNode initiator, QuiescencePurpose purpose)
    {
        var quiescence = initiator.Quiescence.RequestAsync(TwoNodeHarness.ChannelId, purpose,
                                                           TestContext.Current.CancellationToken);
        await harness.PumpAsync(quiescence);
        Assert.Equal(QuiescenceInitiator.Local, await quiescence);
    }

    /// <summary>Calls the node's splice service with a unit of work, as a handler does.</summary>
    private static async Task<IReadOnlyList<IChannelMessage>> HandleAsync(
        SpliceNode node, Func<SpliceService, IUnitOfWork, Task<IReadOnlyList<IChannelMessage>>> handle)
    {
        using var scope = node.Node.Services.CreateScope();
        return await handle(node.Service, scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    private static string AbortText(IReadOnlyList<IChannelMessage> replies) =>
        System.Text.Encoding.ASCII.GetString(Assert.IsType<TxAbortMessage>(Assert.Single(replies)).Payload.Data);

    private static void AssertBuyerRefused(SpliceHarness harness, SpliceResult result, string expected)
    {
        Assert.Equal(SpliceNegotiationState.Aborted, result.State);
        Assert.Contains(expected, result.FailureReason);
        Assert.Contains(harness.Transcript, t => t.From == "Alice" && t.Message is TxAbortMessage);
        Assert.Null(result.Purchase);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Node.State.PendingFundings);
            Assert.Empty(node.Purchases.Committed);
            Assert.Empty(node.Contributor.ActiveReservations);
            Assert.False(node.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
        }

        Assert.Equal(0, LiquidityAds(harness.Bob).SalesInProgress);
        Assert.Equal(ChannelState.Open, harness.Alice.Node.Channel.State);
    }

    private static void AssertPurchase(SpliceNode node, TxId fundingTxId, LiquidityPurchaseRole role,
                                       LiquidityPurchaseKind kind, LiquidityPurchaseStatus status, uint feeratePerKw)
    {
        var row = node.Purchases.Committed.Single(p => p.FundingTxId == fundingTxId);
        var fees = LiquidityAdsRules.ComputeFees(SpliceLiquidityTestKit.Rate, feeratePerKw, Requested, Requested,
                                                 false);
        Assert.Equal(role, row.Role);
        Assert.Equal(kind, row.Kind);
        Assert.Equal(status, row.Status);
        Assert.Equal(Requested, row.RequestedSat);
        Assert.Equal(Requested, row.ContributedSat);
        Assert.Equal(SpliceLiquidityTestKit.Rate, row.Rate);
        Assert.Equal(fees, row.Fees);
        Assert.Equal(SpliceHarness.NodeIdOf(node.Name == "Alice" ? "Bob" : "Alice"), row.PeerNodeId);
        Assert.True(LiquidityAdsRules.ComputeFees(row.Rate, feeratePerKw, row.RequestedSat, row.ContributedSat, false)
                                     .TotalSat > 0);
    }

    private static void AssertLockEvents(SpliceNode node, long contributionMsat, AccountingEventKind liquidityKind,
                                         long liquidityAmountMsat)
    {
        var locked = node.AccountingEvents.Single(e => e.Kind == AccountingEventKind.SpliceLocked);
        var liquidity = node.AccountingEvents.Single(e => e.Kind == liquidityKind);
        Assert.Equal(contributionMsat, locked.AmountMsat);
        Assert.Equal(liquidityAmountMsat, liquidity.AmountMsat);
        Assert.Equal(locked.TxId, liquidity.TxId);
        var delta = node.FundingRows.Committed[locked.TxId!.Value];
        Assert.Equal(ChannelFundingStatus.Current, delta.Status);
    }

    /// <summary>Delivers messages both ways except those <paramref name="hold"/> matches, until nothing else moves.
    /// </summary>
    private static async Task PumpHoldingAsync(SpliceHarness harness, Func<string, IChannelMessage, bool> hold,
                                               Task? until = null)
    {
        var quietRounds = 0;
        for (var round = 0; round < 2_000; round++)
        {
            await harness.WhenIdleAsync();
            var aliceSent = await TryDeliverAsync(harness.Alice, hold);
            var bobSent = await TryDeliverAsync(harness.Bob, hold);
            if (aliceSent || bobSent)
            {
                quietRounds = 0;
                continue;
            }

            if (++quietRounds >= 5 && (until is null || until.IsCompleted || HoldsOne(harness, hold)))
                return;

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("The exchange did not reach the held message");
    }

    private static bool HoldsOne(SpliceHarness harness, Func<string, IChannelMessage, bool> hold) =>
        (harness.Alice.Node.PeekNext() is { } a && hold("Alice", a))
     || (harness.Bob.Node.PeekNext() is { } b && hold("Bob", b));

    private static async Task<bool> TryDeliverAsync(SpliceNode node, Func<string, IChannelMessage, bool> hold)
    {
        if (node.Node.PeekNext() is not { } next || hold(node.Name, next))
            return false;

        return await node.Node.DeliverNextAsync();
    }

    private static async Task<(ulong Id, Secret Preimage)> OfferAsync(SpliceHarness harness, SpliceNode from,
                                                                     ulong amountMsat, int tag)
    {
        var preimage = TwoNodeHarness.Preimage(tag);
        var hash = TwoNodeHarness.Hash(preimage);
        var id = await from.Node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId,
                                                           LightningMoney.MilliSatoshis(amountMsat), hash, CltvExpiry,
                                                           s_onion, null, HtlcOrigin.Local(hash),
                                                           TestContext.Current.CancellationToken);
        await harness.PumpAsync();
        return (id, preimage);
    }

    private static async Task FulfillAsync(SpliceHarness harness, SpliceNode by, ulong id, Secret preimage)
    {
        await by.Node.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, id, preimage,
                                                  TestContext.Current.CancellationToken);
        await harness.PumpAsync();
    }

    #endregion
}