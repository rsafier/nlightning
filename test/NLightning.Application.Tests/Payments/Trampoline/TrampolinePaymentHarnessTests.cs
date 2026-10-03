using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Trampoline;

using Application.Payments.Routing;
using Application.Payments.Send;
using Application.Payments.Trampoline;
using Bolt11.Models;
using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Gossip.Graph;
using Domain.Money;
using Domain.Node;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.ValueObjects;
using Send.Harness;

/// <summary>
/// NL-875 TR4-T5: the payer and the outgoing-leg sender over real nodes (<see cref="PaymentHarness"/>: Bob, Carol and
/// David with real channels, Sphinx, trampoline onions and failure onions). Carol is the trampoline node and David the
/// recipient, both through <see cref="HarnessTrampolineNode"/> stand-ins; Bob's and Carol's sends are the production
/// <see cref="PaymentService"/>.
/// </summary>
public class TrampolinePaymentHarnessTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(100_000);

    private static async Task<InvoiceModel> CreateInvoiceAsync(PaymentHarnessNode node, LightningMoney amount,
                                                               bool trampoline = true, bool requireTrampoline = false)
    {
        var preimage = RandomNumberGenerator.GetBytes(32);
        var paymentHash = SHA256.HashData(preimage);
        var paymentSecret = RandomNumberGenerator.GetBytes(32);
        // var_onion_optin (8) and payment_secret (14) compulsory, basic_mpp (17), trampoline_routing (56/57)
        var features = FeatureSet.DeserializeFromBytes([0x41, 0x00]);
        features.SetFeature(Feature.BasicMpp, false);
        if (trampoline || requireTrampoline)
            features.SetFeature(Feature.OptionTrampolineRouting, requireTrampoline);
        var invoice = new Invoice(amount, "trampoline", PaymentTarget.FromWireBytes(paymentHash),
                                  PaymentTarget.FromWireBytes(paymentSecret), BitcoinNetwork.Regtest, node.KeyManager)
        {
            MinFinalCltvExpiry = node.Options.Routing.InvoiceMinFinalCltvExpiry,
            Features = features
        };
        invoice.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp + 3_600);

        var model = new InvoiceModel(new Hash(paymentHash), new Secret(preimage), new Secret(paymentSecret), amount,
                                     "trampoline", invoice.Encode(),
                                     DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp), 3_600,
                                     node.Options.Routing.InvoiceMinFinalCltvExpiry);
        await node.Invoices.AddAsync(model);
        return model;
    }

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        while (!condition())
            await Task.Delay(10);
        return true;
    }

    private static PayInvoiceOptions Through(PaymentHarnessNode trampoline, LightningMoney? maxFee = null) =>
        new() { TrampolineNode = trampoline.NodeId, Timeout = s_timeout, MaxFee = maxFee };

    [Fact]
    public async Task Given_ABolt11InvoiceWithTheBit_When_PaidThroughCarol_Then_DavidIsPaidAndTheOnionsFollowTheSpec()
    {
        // Arrange
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol);
        var david = new HarnessTrampolineNode(harness.David);
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);

        // Act
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null, Through(harness.Carol),
                                                TestContext.Current.CancellationToken));

        // Assert: Bob paid David's amount plus Carol's fee at Bob's default budget (1000 msat + 1000 ppm)
        var payment = result.Payment;
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(harness.David.NodeId, payment.PayeeNodeId);
        Assert.Equal(1_100UL, payment.Fee.MilliSatoshi);
        Assert.Equal(InvoiceStatus.Accepted,
                     (await harness.David.Invoices.GetByPaymentHashAsync(invoice.PaymentHash))!.Status);

        // Carol's outer payload: a random outer secret (never the invoice's), the total she receives and TLV 20
        var atCarol = Assert.Single(carol.Received);
        Assert.NotEqual(invoice.PaymentSecret, atCarol.Outer.PaymentData!.PaymentSecret);
        Assert.Equal(101_100UL, atCarol.Outer.PaymentData.TotalMsat.MilliSatoshi);
        Assert.Equal(650, atCarol.Outer.TrampolineOnionPacket!.Value.HopPayloadsLength);
        // Her trampoline payload: amt_to_forward, outgoing_cltv_value and outgoing_node_id only
        Assert.Equal(harness.David.NodeId, atCarol.Inner.OutgoingNodeId);
        Assert.Equal(100_000UL, atCarol.Inner.AmtToForward!.MilliSatoshi);
        Assert.Null(atCarol.Inner.PaymentData);
        Assert.False(atCarol.IsFinal);
        Assert.True(atCarol.CltvExpiry >= atCarol.Inner.OutgoingCltvValue + 576);

        // Carol's leg: an absolute final expiry, David's outer payload carries it and Carol's random outer secret
        var leg = Assert.Single(carol.Legs);
        var atDavid = Assert.Single(david.Received);
        Assert.True(atDavid.IsFinal);
        Assert.Equal(leg.FinalCltvExpiry, atDavid.CltvExpiry);
        Assert.Equal(atCarol.Inner.OutgoingCltvValue, leg.FinalCltvExpiry);
        Assert.Equal(invoice.PaymentSecret, atDavid.Inner.PaymentData!.PaymentSecret);
        Assert.Equal(100_000UL, atDavid.Inner.PaymentData.TotalMsat.MilliSatoshi);
        Assert.NotEqual(invoice.PaymentSecret, atDavid.Outer.PaymentData!.PaymentSecret);

        // The leg reported once, stored as a relay and carried origin 3; no payment event of Carol's
        await ((PaymentService)harness.Carol.PaymentService).WhenRoundsIdleAsync();
        var outcome = Assert.Single(carol.Outcomes);
        Assert.Equal(invoice.Preimage, outcome.Preimage);
        var legRow = await harness.Carol.Payments.GetByPaymentHashAsync(invoice.PaymentHash);
        Assert.True(legRow!.IsTrampolineRelay);
        Assert.Equal(PaymentStatus.Succeeded, legRow.Status);
        Assert.Contains(harness.Carol.Store.Origins.Values, o => o == HtlcOrigin.Trampoline(invoice.PaymentHash));
        Assert.DoesNotContain(harness.Carol.Accounting.Saved, e => e.Kind.ToString().StartsWith("Payment"));

        // Bob stored the attempt's trampoline hops (Carol, David) with their shared secrets
        var hops = harness.Bob.TrampolineHops.All;
        Assert.Equal([harness.Carol.NodeId, harness.David.NodeId], hops.Select(h => h.NodeId));
        Assert.All(hops, h => Assert.Equal(0, h.Attempt));
    }

    [Fact]
    public async Task Given_ChannelsTooSmallForTheAmount_When_PaidThroughCarol_Then_BothLegsSplitWithOneTrampolineOnion()
    {
        // Arrange: no single channel carries 2,000,000 sat
        using var harness = new PaymentHarness(new PaymentHarnessTopology(SecondBobCarol: true, SecondCarolDavid: true));
        var carol = new HarnessTrampolineNode(harness.Carol);
        var david = new HarnessTrampolineNode(harness.David);
        var amount = LightningMoney.Satoshis(2_000_000);
        var invoice = await CreateInvoiceAsync(harness.David, amount);

        // Act
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null, Through(harness.Carol),
                                                TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.True(result.Parts >= 2);
        var atCarol = carol.Received.ToList();
        Assert.True(atCarol.Count >= 2);
        var packets = atCarol.Select(p => Convert.ToHexString(p.Outer.TrampolineOnionPacket!.Value.ToBytes()))
                             .Distinct();
        Assert.Single(packets);
        Assert.Single(atCarol.Select(p => p.Outer.PaymentData!.PaymentSecret).Distinct());
        Assert.True(david.Received.Count >= 2);
        Assert.Equal(amount.MilliSatoshi, david.Received.Aggregate(0UL, (sum, p) => sum + p.AmountMsat));
    }

    [Fact]
    public async Task Given_CarolAsksForAHigherFee_When_Paid_Then_BobCachesHerPolicyAndRetriesAtIt()
    {
        // Arrange: Carol charges 5000 msat + 2000 ppm, above Bob's default offer
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol) { Policy = new TrampolinePolicy(5_000, 2_000, 144) };
        _ = new HarnessTrampolineNode(harness.David);
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);

        // Act
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null,
                                                Through(harness.Carol, LightningMoney.MilliSatoshis(10_000)),
                                                TestContext.Current.CancellationToken));

        // Assert: the second attempt paid her policy, and Bob remembers it
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(5_200UL, result.Payment.Fee.MilliSatoshi);
        var bob = (PaymentService)harness.Bob.PaymentService;
        Assert.Equal(new TrampolinePolicy(5_000, 2_000, 144), bob.GetCachedTrampolinePolicy(harness.Carol.NodeId));
        Assert.Equal([0, 1], harness.Bob.TrampolineHops.All.Select(h => h.Attempt).Distinct());
        Assert.Equal(2, carol.Received.Count);
        Assert.Single(carol.Legs);
    }

    [Fact]
    public async Task Given_CarolsFeeAboveTheLimit_When_Paid_Then_TheTrampolineFailureIsRecordedAndNotRetried()
    {
        // Arrange
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol) { Policy = new TrampolinePolicy(5_000, 2_000, 144) };
        _ = new HarnessTrampolineNode(harness.David);
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);

        // Act
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null,
                                                Through(harness.Carol, LightningMoney.MilliSatoshis(3_000)),
                                                TestContext.Current.CancellationToken));

        // Assert: NODE|26 read from the trampoline layer of the trampoline node (outer index 0), not retried
        var payment = result.Payment;
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.TrampolineFeeOrExpiryInsufficient, payment.FailureCode);
        Assert.Equal(0, payment.FailureSourceIndex);
        Assert.Contains("above the fee limit", payment.FailureReason);
        Assert.Single(carol.Received);
    }

    [Fact]
    public async Task Given_ATemporaryTrampolineFailure_When_Paid_Then_OneRetryWithADoubledBudget()
    {
        // Arrange: Carol refuses her first relay with temporary_trampoline_failure
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol)
        {
            RefuseRelay = attempt => attempt == 0 ? FailureMessage.TemporaryTrampolineFailure() : null
        };
        _ = new HarnessTrampolineNode(harness.David);
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);

        // Act
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null, Through(harness.Carol),
                                                TestContext.Current.CancellationToken));

        // Assert: twice the default fee (2 x (1000 + 100) msat)
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(2_200UL, result.Payment.Fee.MilliSatoshi);
        Assert.Equal(2, carol.Received.Count);
    }

    [Fact]
    public async Task Given_TemporaryTrampolineFailuresEveryTime_When_Paid_Then_OnlyOneRetry()
    {
        // Arrange
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol)
        {
            RefuseRelay = _ => FailureMessage.TemporaryTrampolineFailure()
        };
        _ = new HarnessTrampolineNode(harness.David);
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);

        // Act
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null, Through(harness.Carol),
                                                TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, result.Payment.FailureCode);
        Assert.Equal(2, carol.Received.Count);
    }

    [Fact]
    public async Task Given_DavidRefusesThePayment_When_TheErrorComesBack_Then_CarolRewrapsItAndBobReadsItAtTheRightHop()
    {
        // Arrange: David answers incorrect_or_unknown_payment_details in his trampoline layer
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol);
        _ = new HarnessTrampolineNode(harness.David)
        {
            RecipientFailure = FailureMessage.IncorrectOrUnknownPaymentDetails(s_amount, PaymentHarness.BlockHeight)
        };
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);

        // Act
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null, Through(harness.Carol),
                                                TestContext.Current.CancellationToken));

        // Assert: Carol's leg ended with the unwrapped packet (not retried), and Bob, decrypting both layers, attributes
        // the failure to trampoline hop 1 (the payee), outer index 0 + 1
        await ((PaymentService)harness.Carol.PaymentService).WhenRoundsIdleAsync();
        var outcome = Assert.Single(carol.Outcomes);
        Assert.Equal(TrampolineLegFailureKind.DownstreamTrampolineError, outcome.Failure!.Kind);
        Assert.NotNull(outcome.Failure.DownstreamPacketToRewrap);
        Assert.False(outcome.Failure.ErringNodeIsNextTrampoline);
        Assert.Single(carol.Legs);

        var payment = result.Payment;
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, payment.FailureCode);
        Assert.Equal(1, payment.FailureSourceIndex);
        Assert.Contains("the payee", payment.FailureReason);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Given_TheNextTrampolineFailsOnItsOuterLayer_When_TheLegEnds_Then_RouteFailureAndBobRetriesOnce()
    {
        // Arrange: David fails every HTLC on his outer layer only (an error Carol, not Bob, can read)
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol);
        _ = new HarnessTrampolineNode(harness.David)
        {
            OuterFailure = FailureMessage.TemporaryNodeFailure()
        };
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);

        // Act
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null, Through(harness.Carol),
                                                TestContext.Current.CancellationToken));

        // Assert: each leg ended at once with RouteFailure naming the next trampoline; Carol answered
        // temporary_trampoline_failure and Bob retried once with a doubled budget
        await ((PaymentService)harness.Carol.PaymentService).WhenRoundsIdleAsync();
        Assert.Equal(2, carol.Outcomes.Count);
        Assert.All(carol.Outcomes, o =>
        {
            Assert.Equal(TrampolineLegFailureKind.RouteFailure, o.Failure!.Kind);
            Assert.True(o.Failure.ErringNodeIsNextTrampoline);
            Assert.Equal(FailureCode.TemporaryNodeFailure, o.Failure.Failure!.Code);
            Assert.Null(o.Failure.DownstreamPacketToRewrap);
        });
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, result.Payment.FailureCode);
    }

    [Fact]
    public async Task Given_TheAlwaysModeAndAGraphNodeWithTheBit_When_Paid_Then_ThroughThatNode()
    {
        // Arrange: Node:Payments:Trampoline = Always, and Carol announces trampoline_routing in Bob's graph
        using var harness = new PaymentHarness(new PaymentHarnessTopology(BobUsesGraph: true));
        var carol = new HarnessTrampolineNode(harness.Carol);
        _ = new HarnessTrampolineNode(harness.David);
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var features = new FeatureSet();
        features.SetFeature(Feature.OptionTrampolineRouting, false);
        harness.Bob.GraphView = new GraphSnapshot(harness.BuildGraph(timestamp).Channels,
                                                  [
                                                      new GraphNode(harness.Carol.NodeId, timestamp,
                                                                    features.GetWireBytes(), new byte[32],
                                                                    new byte[3])
                                                  ]);
        harness.Bob.Services.GetRequiredService<IOptions<PaymentSendOptions>>().Value.Trampoline =
            TrampolinePaymentMode.Always;
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);

        // Act: no trampoline node named by the call
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null, new PayInvoiceOptions { Timeout = s_timeout },
                                                TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Single(carol.Received);
        Assert.Equal(harness.Carol.NodeId, harness.Bob.TrampolineHops.All[0].NodeId);
    }

    [Fact]
    public async Task Given_TheNeverMode_When_AnInvoiceWithTheBitIsPaid_Then_APlainPayment()
    {
        // Arrange: the default mode, Carol announcing trampoline_routing changes nothing
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol);
        _ = new HarnessTrampolineNode(harness.David);
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);

        // Act: David's invoice has no route hint, so without a trampoline Bob has no route
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null, new PayInvoiceOptions { Timeout = s_timeout },
                                                TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Empty(carol.Received);
        Assert.Empty(harness.Bob.TrampolineHops.All);
    }

    [Fact]
    public async Task Given_AnInvoiceWithoutTheBit_When_PaidThroughATrampoline_Then_RefusedAndNothingSent()
    {
        // Arrange
        using var harness = new PaymentHarness();
        var invoice = await CreateInvoiceAsync(harness.David, s_amount, trampoline: false);

        // Act / Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
                            () => harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11!, null,
                                                                             Through(harness.Carol),
                                                                             TestContext.Current.CancellationToken));
        Assert.Contains("bit 57", exception.Message);
        Assert.Null(await harness.Bob.Payments.GetByPaymentHashAsync(invoice.PaymentHash));
    }

    [Fact]
    public async Task Given_AnInvoiceThatRequiresTrampoline_When_PaidWithoutOne_Then_Refused()
    {
        // Arrange
        using var harness = new PaymentHarness();
        var invoice = await CreateInvoiceAsync(harness.David, s_amount, requireTrampoline: true);

        // Act / Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
                            () => harness.Bob.PaymentService.PayInvoiceAsync(
                                invoice.Bolt11!, null, new PayInvoiceOptions { Timeout = s_timeout },
                                TestContext.Current.CancellationToken));
        Assert.Contains("bit 56", exception.Message);
    }

    [Fact]
    public async Task Given_ThePayeeIsTheTrampolineNode_When_Paid_Then_APlainPayment()
    {
        // Arrange
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol);
        var invoice = await harness.Carol.InvoiceService.CreateInvoiceAsync(s_amount, "plain", null,
                                                                            TestContext.Current.CancellationToken);

        // Act
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null, Through(harness.Carol),
                                                TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Empty(carol.Received);
        Assert.Empty(harness.Bob.TrampolineHops.All);
    }

    [Fact]
    public async Task Given_ALegStartedTwiceWhileInFlight_When_ItEnds_Then_OneHtlcAndOneReport()
    {
        // Arrange: a leg Carol sends for a relay (the request built as the relay engine would)
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol);
        var david = new HarnessTrampolineNode(harness.David);
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);
        var request = await CreateLegToDavidAsync(harness, invoice, finalCltv: PaymentHarness.BlockHeight + 100);
        var sender = (ITrampolineLegSender)harness.Carol.PaymentService;

        // Act
        await sender.StartAsync(request, TestContext.Current.CancellationToken);
        await sender.StartAsync(request, TestContext.Current.CancellationToken);
        await harness.RunAsync(UntilAsync(() => !carol.Outcomes.IsEmpty));
        await ((PaymentService)harness.Carol.PaymentService).WhenRoundsIdleAsync();

        // Assert: the absolute expiry reached David as is; one HTLC, one report with the preimage and what was sent
        var atDavid = Assert.Single(david.Received);
        Assert.Equal(PaymentHarness.BlockHeight + 100, atDavid.CltvExpiry);
        Assert.Equal(s_amount.MilliSatoshi, atDavid.AmountMsat);
        var outcome = Assert.Single(carol.Outcomes);
        Assert.Equal(invoice.Preimage, outcome.Preimage);
        var row = await harness.Carol.Payments.GetByPaymentHashAsync(invoice.PaymentHash);
        Assert.True(row!.IsTrampolineRelay);
        Assert.Single(harness.Carol.Store.Origins.Values, o => o == HtlcOrigin.Trampoline(invoice.PaymentHash));
    }

    [Fact]
    public async Task Given_ALegToANodeWithoutAChannel_When_Started_Then_NoRouteAndNothingOffered()
    {
        // Arrange: Bob has a channel to Carol only, so Carol has no route to an unknown node
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol);
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);
        var request = await CreateLegToDavidAsync(harness, invoice, finalCltv: PaymentHarness.BlockHeight + 100);
        request = request with { NextNodeId = new TestNodeKeyManager(0x77).NodeId };

        // Act
        await ((ITrampolineLegSender)harness.Carol.PaymentService).StartAsync(request,
                                                                             TestContext.Current.CancellationToken);
        await ((PaymentService)harness.Carol.PaymentService).WhenRoundsIdleAsync();

        // Assert
        var outcome = Assert.Single(carol.Outcomes);
        Assert.Equal(TrampolineLegFailureKind.NoRoute, outcome.Failure!.Kind);
        Assert.Empty(harness.Carol.Store.Origins);
    }

    [Fact]
    public async Task Given_ALegFailureDeliveredAfterARestart_When_Handled_Then_TheLegIsReportedWithThePacket()
    {
        // Arrange: Carol's leg failure goes to a restarted payment service (no session), David refuses the payment
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol);
        _ = new HarnessTrampolineNode(harness.David)
        {
            RecipientFailure = FailureMessage.IncorrectOrUnknownPaymentDetails(s_amount, PaymentHarness.BlockHeight)
        };
        var restarted = (PaymentService)harness.Carol.RestartedPaymentOutcomeHandler();
        restarted.LegObserver = carol;
        harness.Carol.Switch.OwnOutcomeInterceptor = channelEvent =>
        {
            if (channelEvent is not OutgoingHtlcFailed failed)
                return false;

            restarted.HandleOutgoingFailedAsync(failed, CancellationToken.None).GetAwaiter().GetResult();
            return true;
        };
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);

        // Act
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null, Through(harness.Carol),
                                                TestContext.Current.CancellationToken));

        // Assert: the restarted service failed the stored leg and reported the packet Bob then read
        await restarted.WhenRoundsIdleAsync();
        var outcome = Assert.Single(carol.Outcomes);
        Assert.Equal(TrampolineLegFailureKind.DownstreamTrampolineError, outcome.Failure!.Kind);
        Assert.Equal(PaymentStatus.Failed,
                     (await harness.Carol.Payments.GetByPaymentHashAsync(invoice.PaymentHash))!.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Payment.FailureCode);
    }

    [Fact]
    public async Task Given_ABobFailureDeliveredAfterARestart_When_Handled_Then_TheStoredTrampolineHopsDecryptIt()
    {
        // Arrange: Bob's failure goes to a restarted payment service, which has only the stored rows
        using var harness = new PaymentHarness();
        _ = new HarnessTrampolineNode(harness.Carol);
        _ = new HarnessTrampolineNode(harness.David)
        {
            RecipientFailure = FailureMessage.IncorrectOrUnknownPaymentDetails(s_amount, PaymentHarness.BlockHeight)
        };
        var restarted = harness.Bob.RestartedPaymentOutcomeHandler();
        var handled = false;
        harness.Bob.Switch.OwnOutcomeInterceptor = channelEvent =>
        {
            if (channelEvent is not OutgoingHtlcFailed failed)
                return false;

            restarted.HandleOutgoingHtlcFailedAsync(failed, CancellationToken.None).GetAwaiter().GetResult();
            handled = true;
            return true;
        };
        var invoice = await CreateInvoiceAsync(harness.David, s_amount);

        // Act: the original call's session never sees the outcome; wait for the restarted service instead
        _ = harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11!, null, Through(harness.Carol),
                                                       TestContext.Current.CancellationToken);
        await harness.RunAsync(UntilAsync(() => handled));

        // Assert
        var payment = await harness.Bob.Payments.GetByPaymentHashAsync(invoice.PaymentHash);
        Assert.Equal(PaymentStatus.Failed, payment!.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, payment.FailureCode);
        Assert.Equal(1, payment.FailureSourceIndex);
        Assert.Contains("trampoline hop 1", payment.FailureReason);
    }

    [Fact]
    public async Task Given_ABolt12RecipientWithoutTheBit_When_PaidThroughCarol_Then_CarolGetsItsPathsAndFeatures()
    {
        // Arrange: a blinded path introduced by David; the recipient's features carry basic_mpp only
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol);
        _ = new HarnessTrampolineNode(harness.David);
        var features = new FeatureSet();
        features.SetFeature(Feature.BasicMpp, false);
        var path = BlindedPathIntroducedByDavid(harness, 2);
        var request = new PayBlindedRequest(new Hash(RandomNumberGenerator.GetBytes(32)), s_amount, [path])
        {
            RecipientFeatures = features
        };

        // Act: David cannot read the made-up encrypted data, so the payment ends failed
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayBlindedAsync(
                                                request, Through(harness.Carol),
                                                TestContext.Current.CancellationToken));

        // Assert: Carol's trampoline payload carried amt, cltv, the paths (TLV 22) and the features (TLV 21), no
        // outgoing_node_id, and her leg paid the paths at the absolute expiry plus the path's delta
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        var atCarol = carol.Received.First();
        Assert.Null(atCarol.Inner.OutgoingNodeId);
        Assert.Equal(path.Path.FirstNodeId,
                     Assert.Single(atCarol.Inner.RecipientBlindedPaths!).ToBlindedPaymentPath(path.Path.FirstNodeId)
                                                                       .Path.FirstNodeId);
        Assert.True(atCarol.Inner.RecipientFeatures!.GetFeatureSet().IsFeatureSet(Feature.BasicMpp, false));
        Assert.Equal(100_000UL, atCarol.Inner.AmtToForward!.MilliSatoshi);
        var leg = carol.Legs.First();
        Assert.Null(leg.NextNodeId);
        Assert.NotNull(leg.RecipientBlindedPaths);
        var offered = harness.David.Switch.Events.OfType<IncomingHtlcLockedIn>().First();
        Assert.Equal(leg.FinalCltvExpiry + path.PayInfo.CltvExpiryDelta, offered.Htlc.CltvExpiry);
        Assert.All(carol.Outcomes, o => Assert.Equal(TrampolineLegFailureKind.RouteFailure, o.Failure!.Kind));
    }

    [Fact]
    public async Task Given_ABolt12RecipientWithTheBit_When_PaidThroughCarol_Then_TheBlindedHopsAreTrampolineHops()
    {
        // Arrange
        using var harness = new PaymentHarness();
        var carol = new HarnessTrampolineNode(harness.Carol);
        var david = new HarnessTrampolineNode(harness.David);
        var features = new FeatureSet();
        features.SetFeature(Feature.OptionTrampolineRouting, false);
        var path = BlindedPathIntroducedByDavid(harness, 2);
        var request = new PayBlindedRequest(new Hash(RandomNumberGenerator.GetBytes(32)), s_amount, [path])
        {
            RecipientFeatures = features
        };

        // Act: David does not relay blinded trampoline hops in this harness, so the payment fails
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayBlindedAsync(
                                                request, Through(harness.Carol),
                                                TestContext.Current.CancellationToken));

        // Assert: Carol forwards to the introduction node by node id with the next trampoline layer; David, the
        // introduction node, peels a blinded trampoline payload: encrypted_recipient_data and current_path_key only
        var atCarol = carol.Received.First();
        Assert.Equal(harness.David.NodeId, atCarol.Inner.OutgoingNodeId);
        Assert.Equal(s_amount.MilliSatoshi + path.PayInfo.ComputeFeeMsat(s_amount.MilliSatoshi),
                     atCarol.Inner.AmtToForward!.MilliSatoshi);
        Assert.Equal(harness.David.NodeId, carol.Legs.First().NextNodeId);
        var atDavid = david.Received.First();
        Assert.False(atDavid.IsFinal);
        Assert.Equal(path.Path.Hops[0].EncryptedRecipientData.ToArray(),
                     atDavid.Inner.EncryptedRecipientData!.Value.ToArray());
        Assert.Equal(path.Path.FirstPathKey, atDavid.Inner.CurrentPathKey);
        Assert.Null(atDavid.Inner.AmtToForward);
        // David's trampoline-layer error reached Bob through Carol's re-wrap: hop 1 of the trampoline route
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, result.Payment.FailureCode);
        Assert.Equal(1, result.Payment.FailureSourceIndex);
    }

    /// <summary>A made-up blinded path introduced by David (real node id, random blinded ids and data).</summary>
    private static BlindedPaymentPath BlindedPathIntroducedByDavid(PaymentHarness harness, int hops)
    {
        var pathHops = Enumerable.Range(0, hops)
                                 .Select(i => new BlindedPathHop(new TestNodeKeyManager((byte)(0x60 + i)).NodeId,
                                                                 RandomNumberGenerator.GetBytes(40)))
                                 .ToList();
        return new BlindedPaymentPath(new BlindedPath(harness.David.NodeId, new TestNodeKeyManager(0x5F).NodeId,
                                                      pathHops),
                                      new BlindedPayInfo(500, 1_000, 36, 1, 500_000_000));
    }

    /// <summary>A leg to David as a relay engine would ask for it: David's trampoline onion (final, with the invoice's
    /// secret), the leg's absolute expiry and a first-hop cap 200 blocks above it.</summary>
    private static async Task<TrampolineLegRequest> CreateLegToDavidAsync(PaymentHarness harness, InvoiceModel invoice,
                                                                          uint finalCltv)
    {
        var factory = new TrampolineOnionFactory(
            (Domain.Protocol.Onion.Interfaces.ITrampolineOnionService)harness.Carol.Services.GetService(
                typeof(Domain.Protocol.Onion.Interfaces.ITrampolineOnionService))!,
            (Domain.Serialization.Interfaces.IHopPayloadSerializer)harness.Carol.Services.GetService(
                typeof(Domain.Serialization.Interfaces.IHopPayloadSerializer))!);
        var onion = await factory.CreateAsync(
                        [
                            new TrampolineHop(harness.David.NodeId,
                                              TrampolineOnionFactory.CreateFinalPayload(
                                                  s_amount, finalCltv, invoice.PaymentSecret, s_amount), s_amount,
                                              finalCltv)
                        ], invoice.PaymentHash, 1_000);
        return new TrampolineLegRequest(invoice.PaymentHash, s_amount, finalCltv, finalCltv + 200,
                                        LightningMoney.MilliSatoshis(1_000), harness.David.NodeId, onion.ToTlvValue(),
                                        null, null, null, false, harness.Carol.Clock.GetUtcNow().AddMinutes(5));
    }
}