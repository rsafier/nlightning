namespace NLightning.Application.Tests.Payments.Send;

using Application.Payments.Send;
using Domain.Channels.ValueObjects;
using Domain.Models;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Payloads;
using Harness;

/// <summary>
/// NL-270 proof, in process with real crypto, real onions and the production engine: Bob retries a payment after a
/// hop's retryable failure (<c>fee_insufficient</c> with Carol's signed channel_update, <c>temporary_channel_failure</c>,
/// <c>expiry_too_soon</c>) within the per-call fee limit, stops on a permanent one, and splits a payment no single
/// channel can carry into <c>basic_mpp</c> parts (same hash, <c>payment_secret</c> and <c>total_msat</c>), re-sending a
/// part that failed while another is held by the payee.
/// </summary>
public class PaymentRetryHarnessTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    /// <summary>Carol's fee as David's invoice hints it (her harness policy: 2000 msat + 500 ppm).</summary>
    private static ulong HintFee(ulong amountMsat) => 2_000 + amountMsat * 500 / 1_000_000;

    private static PaymentHarness Harness(PaymentHarnessTopology? topology = null)
    {
        var harness = new PaymentHarness(topology ?? new PaymentHarnessTopology());

        // David knows Carol's channel_update for each Carol-David channel (W1-E), so his invoices hint through her
        var carolUpdate = PaymentHarnessNode.PeerUpdate(harness.Carol, PaymentHarness.ScidCarolDavid);
        harness.David.ChannelUpdates.Setup(s => s.TryGetRemoteChannelUpdate(harness.CarolDavid, out carolUpdate))
               .Returns(true);
        if (harness.Topology.SecondCarolDavid)
        {
            var secondUpdate = PaymentHarnessNode.PeerUpdate(harness.Carol, PaymentHarness.ScidCarolDavid2);
            harness.David.ChannelUpdates
                   .Setup(s => s.TryGetRemoteChannelUpdate(harness.CarolDavid2, out secondUpdate)).Returns(true);
        }

        return harness;
    }

    private static Task<PayInvoiceResult> PayAsync(PaymentHarness harness, string bolt11,
                                                   PayInvoiceOptions? options = null) =>
        harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(bolt11, null,
                                                                    options ?? new PayInvoiceOptions
                                                                    {
                                                                        Timeout = s_timeout
                                                                    }, TestContext.Current.CancellationToken));

    /// <summary>Carol's field for an UPDATE failure about Carol → David: her newer policy, signed.</summary>
    private static byte[] CarolUpdateField(PaymentHarness harness, uint feeBase, uint feePpm, uint timestamp = 2) =>
        FailureChannelUpdateFactory.Encode(harness.Carol
                                                  .SignedUpdate(harness.David, PaymentHarness.ScidCarolDavid,
                                                                timestamp, feeBase, feePpm, 40).GetBytes());

    [Fact]
    public async Task Given_CarolAsksForAHigherFee_When_BobPays_Then_TheRetryPaysHerSignedNewFeeAndSucceeds()
    {
        // Arrange: Carol's first forward is refused with fee_insufficient and her signed update (3000 msat + 1000 ppm)
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "fee", null, ct);
        var field = CarolUpdateField(harness, 3_000, 1_000);
        var refused = 0;
        harness.Carol.Switch.ForwardInterceptor = (htlc, _) =>
            Interlocked.Increment(ref refused) == 1
                ? FailureMessage.FeeInsufficient(LightningMoney.MilliSatoshis(htlc.AmountMsat), field)
                : null;

        // Act
        var result = await PayAsync(harness, invoice.Bolt11);

        // Assert: two HTLCs; the second carries Carol's new fee on top of the amount
        var newFee = 3_000 + s_amount.MilliSatoshi * 1_000 / 1_000_000;
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(invoice.Preimage, result.Payment.Preimage);
        Assert.Equal((2, 1), (result.Attempts, result.Parts));
        Assert.Equal(newFee, result.Payment.Fee.MilliSatoshi);
        var forwards = harness.Carol.Switch.Forwards.ToArray();
        Assert.Equal(2, forwards.Length);
        Assert.Equal(s_amount.MilliSatoshi + HintFee(s_amount.MilliSatoshi), forwards[0].IncomingAmountMsat);
        Assert.Equal(s_amount.MilliSatoshi + newFee, forwards[1].IncomingAmountMsat);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_CarolsNewFeeIsAboveTheCallsFeeLimit_When_BobPays_Then_ItFailsWithoutARetry()
    {
        // Arrange: the hint fee (27,000 msat) fits the per-call limit of 30,000 msat, Carol's new one (53,000) does not
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "fee limit", null, ct);
        var field = CarolUpdateField(harness, 3_000, 1_000);
        harness.Carol.Switch.ForwardInterceptor = (htlc, _) =>
            FailureMessage.FeeInsufficient(LightningMoney.MilliSatoshis(htlc.AmountMsat), field);

        // Act
        var result = await PayAsync(harness, invoice.Bolt11, new PayInvoiceOptions
        {
            Timeout = s_timeout,
            MaxFee = LightningMoney.MilliSatoshis(30_000)
        });

        // Assert
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(FailureCode.FeeInsufficient, result.Payment.FailureCode);
        Assert.Equal(0, result.Payment.FailureSourceIndex);
        Assert.Contains("exceeds the limit of 30000 msat", result.Payment.FailureReason);
        Assert.Equal(1, result.Attempts);
        Assert.Single(harness.Carol.Switch.Forwards);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_AHintFeeAboveTheDefaultLimit_When_TheCallRaisesItsFeeLimit_Then_ThePaymentSucceeds()
    {
        // Arrange: Carol (as David knows her) charges 10,000 sat, far above max(0.5 %, 5000 msat) of 50,000 sat
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        ChannelUpdatePayload? expensive = new(ChannelUpdatePayload.EmptySignature,
                                              Domain.Protocol.Constants.ChainConstants.Regtest,
                                              PaymentHarness.ScidCarolDavid, 2, ChannelUpdatePayload.MessageFlagMustBeOne,
                                              0, 40, 1_000, 10_000_000, 1, PaymentHarness.FundingSatoshis * 1_000);
        harness.David.ChannelUpdates.Setup(s => s.TryGetRemoteChannelUpdate(harness.CarolDavid, out expensive))
               .Returns(true);
        var refusedInvoice = await harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "default", null, ct);
        var paidInvoice = await harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "raised", null, ct);

        // Act
        var refused = await PayAsync(harness, refusedInvoice.Bolt11);
        var paid = await PayAsync(harness, paidInvoice.Bolt11, new PayInvoiceOptions
        {
            Timeout = s_timeout,
            MaxFee = LightningMoney.MilliSatoshis(10_100_000)
        });

        // Assert
        Assert.Equal(PaymentStatus.Failed, refused.Payment.Status);
        Assert.Contains("exceeds the limit", refused.Payment.FailureReason);
        Assert.Equal(0, refused.Attempts);
        Assert.Equal(PaymentStatus.Succeeded, paid.Payment.Status);
        Assert.Equal(10_000_000 + s_amount.MilliSatoshi / 1_000_000, paid.Payment.Fee.MilliSatoshi);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_TemporaryChannelFailureOnOneHint_When_BobPays_Then_TheRetryTakesTheOtherHint()
    {
        // Arrange: two Carol-David channels, so David's invoice hints both; Carol fails every forward over the first
        // one Bob tries
        using var harness = Harness(new PaymentHarnessTopology(SecondCarolDavid: true));
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "tcf", null, ct);
        ShortChannelId? failing = null;
        harness.Carol.Switch.ForwardInterceptor = (_, forward) =>
        {
            failing ??= forward.OutgoingShortChannelId;
            return forward.OutgoingShortChannelId == failing ? FailureMessage.TemporaryChannelFailure() : null;
        };

        // Act
        var result = await PayAsync(harness, invoice.Bolt11);

        // Assert
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(2, result.Attempts);
        var forwards = harness.Carol.Switch.Forwards.ToArray();
        Assert.Equal(2, forwards.Length);
        Assert.NotEqual(forwards[0].Forward.OutgoingShortChannelId, forwards[1].Forward.OutgoingShortChannelId);
        Assert.Equal(forwards[1].Forward.OutgoingShortChannelId, result.Payment.Route[1].ShortChannelId);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_ExpiryTooSoonFromCarol_When_BobPays_Then_TheRetryAddsCltvAndSucceeds()
    {
        // Arrange
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "soon", null, ct);
        var refused = 0;
        harness.Carol.Switch.ForwardInterceptor = (_, _) =>
            Interlocked.Increment(ref refused) == 1 ? FailureMessage.ExpiryTooSoon() : null;

        // Act
        var result = await PayAsync(harness, invoice.Bolt11);

        // Assert: the second onion asks David for ExpiryTooSoonExtraBlocks more blocks
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(2, result.Attempts);
        var forwards = harness.Carol.Switch.Forwards.ToArray();
        Assert.Equal(forwards[0].Forward.OutgoingCltvValue + new PaymentSendOptions().ExpiryTooSoonExtraBlocks,
                     forwards[1].Forward.OutgoingCltvValue);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_ThePayeeFailsPermanently_When_BobPays_Then_NoRetry()
    {
        // Arrange
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "canceled", null, ct);
        Assert.True(await harness.David.InvoiceService.CancelInvoiceAsync(invoice.PaymentHash, ct));

        // Act
        var result = await PayAsync(harness, invoice.Bolt11);

        // Assert
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Payment.FailureCode);
        Assert.Equal(1, result.Payment.FailureSourceIndex);
        Assert.Contains("Not retried: permanent failure from the payee", result.Payment.FailureReason);
        Assert.Equal(1, result.Attempts);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_ANodeFailureAtTheOnlyHintNode_When_BobPays_Then_ItStopsWhenNoRouteIsLeft()
    {
        // Arrange
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "node", null, ct);
        harness.Carol.Switch.ForwardInterceptor = (_, _) => FailureMessage.TemporaryNodeFailure();

        // Act
        var result = await PayAsync(harness, invoice.Bolt11);

        // Assert
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(FailureCode.TemporaryNodeFailure, result.Payment.FailureCode);
        Assert.Contains("failed earlier", result.Payment.FailureReason);
        Assert.Equal(1, result.Attempts);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_NoChannelCarriesTheAmountAndBasicMpp_When_BobPaysCarol_Then_SplitOverBothChannels()
    {
        // Arrange: two Bob-Carol channels with 500,000 sat of Bob's each; Carol's invoice for 700,000 sat
        using var harness = Harness(new PaymentHarnessTopology(SecondBobCarol: true, BobCarolFundingSatoshis: 1_000_000,
                                                               BobCarolPushSatoshis: 500_000));
        var amount = LightningMoney.Satoshis(700_000);
        var invoice = await harness.Carol.CreateMppInvoiceAsync(amount, []);
        var before1 = harness.Bob.Channel(harness.BobCarol).LocalBalance.MilliSatoshi;
        var before2 = harness.Bob.Channel(harness.BobCarol2).LocalBalance.MilliSatoshi;

        // Act
        var result = await PayAsync(harness, invoice.Bolt11);

        // Assert: two HTLCs, each telling Carol total_msat = the amount, together the amount
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(invoice.Preimage, result.Payment.Preimage);
        Assert.Equal((2, 2), (result.Attempts, result.Parts));
        Assert.True(result.Payment.Fee.IsZero);
        var received = harness.Carol.Switch.Received.ToArray();
        Assert.Equal(2, received.Length);
        Assert.All(received, r => Assert.Equal(amount.MilliSatoshi, r.TotalMsat));
        Assert.Equal(amount.MilliSatoshi, received.Aggregate(0UL, (sum, r) => sum + r.AmountMsat));
        var spent1 = before1 - harness.Bob.Channel(harness.BobCarol).LocalBalance.MilliSatoshi;
        var spent2 = before2 - harness.Bob.Channel(harness.BobCarol2).LocalBalance.MilliSatoshi;
        Assert.True(spent1 > 0 && spent2 > 0);
        Assert.Equal(amount.MilliSatoshi, spent1 + spent2);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_OnePartAllowed_When_NoChannelCarriesTheAmount_Then_NotSplitAndNothingOffered()
    {
        // Arrange
        using var harness = Harness(new PaymentHarnessTopology(SecondBobCarol: true, BobCarolFundingSatoshis: 1_000_000,
                                                               BobCarolPushSatoshis: 500_000));
        var invoice = await harness.Carol.CreateMppInvoiceAsync(LightningMoney.Satoshis(700_000), []);

        // Act
        var result = await PayAsync(harness, invoice.Bolt11, new PayInvoiceOptions
        {
            Timeout = s_timeout,
            MaxParts = 1
        });

        // Assert
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Contains("splitting is turned off", result.Payment.FailureReason);
        Assert.Equal(0, result.Attempts);
        Assert.Empty(harness.Carol.Switch.Received);
    }

    [Fact]
    public async Task Given_OnePartFailsWhileTheOtherIsHeld_When_BobPaysDavid_Then_ThePartIsSentAgainAndAllSettle()
    {
        // Arrange: two Bob-Carol channels (500,000 sat each for Bob), David's basic_mpp invoice for 700,000 sat hinted
        // through Carol; Carol refuses the first part she sees with fee_insufficient and her signed new policy
        using var harness = Harness(new PaymentHarnessTopology(SecondBobCarol: true, BobCarolFundingSatoshis: 1_000_000,
                                                               BobCarolPushSatoshis: 500_000));
        var amount = LightningMoney.Satoshis(700_000);
        var invoice = await harness.David.CreateMppInvoiceAsync(
                          amount, [new RoutingInfo(harness.Carol.NodeId, PaymentHarness.ScidCarolDavid, 2_000, 500, 40)]);
        var field = CarolUpdateField(harness, 3_000, 1_000);
        var refused = 0;
        harness.Carol.Switch.ForwardInterceptor = (htlc, _) =>
            Interlocked.Increment(ref refused) == 1
                ? FailureMessage.FeeInsufficient(LightningMoney.MilliSatoshis(htlc.AmountMsat), field)
                : null;

        // Act
        var result = await PayAsync(harness, invoice.Bolt11);

        // Assert: two parts, then the refused one's amount sent again at Carol's new fee (it no longer fits its channel
        // alone, so it may be split again); every HTLC but the refused one reached David, together the amount
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(invoice.Preimage, result.Payment.Preimage);
        Assert.True(result.Attempts >= 3, $"{result.Attempts} HTLC(s)");
        Assert.True(result.Parts >= 2, $"{result.Parts} part(s) at once");
        Assert.Equal(result.Attempts, harness.Carol.Switch.Forwards.Count);
        var received = harness.David.Switch.Received.ToArray();
        Assert.Equal(result.Attempts - 1, received.Length);
        Assert.All(received, r => Assert.Equal(amount.MilliSatoshi, r.TotalMsat));
        Assert.Equal(amount.MilliSatoshi, received.Aggregate(0UL, (sum, r) => sum + r.AmountMsat));
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_AChannelSnapshot_When_EstimatingWhatItCanSend_Then_TheEngineAcceptsThatAndRefusesOneMore()
    {
        // Arrange
        using var harness = Harness();
        var commitments = harness.Bob.Channel(harness.BobCarol).Commitments!;
        var onion = new byte[Domain.Protocol.Onion.Constants.OnionConstants.PacketLength];
        var hash = new Domain.Crypto.ValueObjects.Hash(new byte[32]);

        // Act
        var max = LocalLiquidityEstimator.MaxSendableMsat(commitments, [], PaymentHarness.BlockHeight + 144);

        // Assert
        Assert.InRange(max, 1UL, PaymentHarness.FundingSatoshis * 1_000);
        commitments.SendAdd(max, hash, PaymentHarness.BlockHeight + 144, onion);
        Assert.Throws<Domain.Exceptions.CommitmentRefusedException>(
            () => commitments.SendAdd(max + 1, hash, PaymentHarness.BlockHeight + 144, onion));
    }

    private static void AssertNoPendingHtlcs(PaymentHarness harness)
    {
        foreach (var (node, channelId) in harness.ChannelEnds)
            Assert.Empty(node.Channel(channelId).Commitments!.Htlcs);
    }
}