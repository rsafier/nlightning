using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Trampoline;

using Application.Payments.Send;
using Application.Payments.Trampoline;
using Domain.Accounting.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Send.Harness;

/// <summary>
/// NL-898: the payer verifies the outer layer's <c>attribution_data</c> of a failure through a trampoline node over the
/// outer route, as for any payment (NL-326). Bob pays Erin through the trampoline David over the graph route Bob →
/// Carol → David: Carol forwards with attribution (<see cref="HarnessForwardingSwitch.UseAttribution"/>) and David
/// creates or re-wraps the trampoline failure with outer-layer attribution as the production relay does
/// (<see cref="HarnessTrampolineNode.UseAttribution"/>, <c>TrampolineErrorPackets</c>). Also NL-982: the failed payment
/// records no fee.
/// </summary>
public class TrampolineAttributionHarnessTests : IDisposable
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(100_000);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_carolHeld = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan s_davidHeld = TimeSpan.FromSeconds(1);

    // Generous for a slow CI machine: hold times count the real time the harness takes too
    private static readonly TimeSpan s_slack = TimeSpan.FromSeconds(20);

    private readonly PaymentHarness _harness;
    private readonly HarnessTrampolineNode _david;
    private readonly HarnessTrampolineNode _erin;

    public TrampolineAttributionHarnessTests()
    {
        _harness = new PaymentHarness(new PaymentHarnessTopology(Erin: true, BobUsesGraph: true));
        _harness.Bob.GraphView = _harness.BuildGraph((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _harness.Carol.Store.AddedAtOffset = s_carolHeld;
        _harness.David.Store.AddedAtOffset = s_davidHeld;
        _david = new HarnessTrampolineNode(_harness.David);
        _erin = new HarnessTrampolineNode(_harness.Erin!);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Given_EveryOuterHopAttributes_When_TheTrampolineRefusesTheRelay_Then_BobVerifiesAndRecordsHoldTimes()
    {
        // Arrange: David answers trampoline_fee_or_expiry_insufficient (NODE|26) with the policy Bob already paid, so
        // Bob stops after one attempt
        EnableAttribution();
        _david.RefuseRelay = _ => RefuseWithBobsPolicy();
        var invoice = await TrampolinePaymentHarnessTests.CreateInvoiceAsync(_harness.Erin!, s_amount);

        // Act
        var payment = await PayAsync(invoice);

        // Assert: the trampoline layer's error is read as before
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.TrampolineFeeOrExpiryInsufficient, payment.FailureCode);
        Assert.Equal(1, payment.FailureSourceIndex);

        // Assert: the outer layer's attribution verified both outer hops, and each hop's hold time is its own; the
        // reason names the trampoline node David for the last one (NL-924: the stored route names the payee Erin there)
        Assert.Contains("hold times", payment.FailureReason);
        Assert.Contains($"(the last from the trampoline node {_harness.David.NodeId})", payment.FailureReason);
        Assert.DoesNotContain("did not verify", payment.FailureReason);
        Assert.Equal(2, payment.Route.Count);
        AssertHoldTime(payment.Route[0], Assert.Single(_harness.Carol.Switch.ReportedHoldTimes).HoldTime, s_carolHeld);
        AssertHoldTime(payment.Route[1], Assert.Single(_david.ReportedHoldTimes), s_davidHeld);
        var stored = await _harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash,
                                                                       TestContext.Current.CancellationToken);
        Assert.Equal(payment.Route.Select(h => h.HoldTime), stored!.Route.Select(h => h.HoldTime));
    }

    [Fact]
    public async Task Given_TheRecipientFails_When_TheTrampolineRewrapsItWithAttribution_Then_BobVerifiesTheOuterRoute()
    {
        // Arrange: Erin refuses in her trampoline layer; David's leg ends with her packet, which he re-wraps
        EnableAttribution();
        _erin.RecipientFailure = FailureMessage.IncorrectOrUnknownPaymentDetails(s_amount, PaymentHarness.BlockHeight);
        var invoice = await TrampolinePaymentHarnessTests.CreateInvoiceAsync(_harness.Erin!, s_amount);

        // Act
        var payment = await PayAsync(invoice);

        // Assert: trampoline hop 1 (the payee) is the erring node, outer index 1 + 1
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, payment.FailureCode);
        Assert.Equal(2, payment.FailureSourceIndex);
        Assert.Contains("the payee", payment.FailureReason);

        // Assert: Carol's and David's outer-layer attribution verified
        Assert.Contains("hold times", payment.FailureReason);
        Assert.DoesNotContain("did not verify", payment.FailureReason);
        AssertHoldTime(payment.Route[0], Assert.Single(_harness.Carol.Switch.ReportedHoldTimes).HoldTime, s_carolHeld);
        AssertHoldTime(payment.Route[1], Assert.Single(_david.ReportedHoldTimes), s_davidHeld);
    }

    [Fact]
    public async Task Given_TheTrampolineGarblesItsAttribution_When_BobVerifies_Then_TheTrampolineHopIsNamed()
    {
        // Arrange: David flips his hold time after his HMACs covered it
        EnableAttribution();
        _david.RefuseRelay = _ => RefuseWithBobsPolicy();
        _david.TamperAttribution = packet =>
        {
            var data = packet.AttributionData.ToArray();
            data[0] ^= 0x01;
            return new AttributedErrorPacket(packet.Reason.ToArray(), data);
        };
        var invoice = await TrampolinePaymentHarnessTests.CreateInvoiceAsync(_harness.Erin!, s_amount);

        // Act
        var payment = await PayAsync(invoice);

        // Assert: the failure is still read; Carol verifies, David (outer hop 1) does not, so only Carol's hold time
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.TrampolineFeeOrExpiryInsufficient, payment.FailureCode);
        Assert.Contains($"attribution_data of outer hop 1 (the trampoline node {_harness.David.NodeId})",
                        payment.FailureReason);
        Assert.DoesNotContain(_harness.Erin!.NodeId.ToString(), payment.FailureReason);
        AssertHoldTime(payment.Route[0], Assert.Single(_harness.Carol.Switch.ReportedHoldTimes).HoldTime, s_carolHeld);
        Assert.Null(payment.Route[1].HoldTime);
    }

    [Fact]
    public async Task Given_ARelayLegFailsWithAttribution_When_DavidsLegEnds_Then_TheLegRowKeepsItAndTheOutcomeIsUnchanged()
    {
        // Arrange (NL-924): the NL-898 branch also runs for a relay's outgoing leg (DecideLegFailure): Erin refuses in
        // her trampoline layer with outer-layer attribution, so David's leg (David → Erin, his production
        // PaymentService) fails with a packet he must re-wrap for Bob
        EnableAttribution();
        _erin.RecipientFailure = FailureMessage.IncorrectOrUnknownPaymentDetails(s_amount, PaymentHarness.BlockHeight);
        var invoice = await TrampolinePaymentHarnessTests.CreateInvoiceAsync(_harness.Erin!, s_amount);

        // Act
        var payment = await PayAsync(invoice);
        await _david.PaymentService.WhenRoundsIdleAsync();

        // Assert: the leg ended as without attribution: a downstream error with the packet to re-wrap, read by Bob
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, payment.FailureCode);
        var outcome = Assert.Single(_david.Outcomes);
        Assert.Equal(TrampolineLegFailureKind.DownstreamTrampolineError, outcome.Failure!.Kind);
        Assert.NotNull(outcome.Failure.DownstreamPacketToRewrap);

        // Assert: the leg's row keeps the attribution Erin's outer layer carried: her hold time and the reason's
        var leg = await _harness.David.Payments.GetByPaymentHashAsync(invoice.PaymentHash);
        Assert.True(leg!.IsTrampolineRelay);
        Assert.Equal(PaymentStatus.Failed, leg.Status);
        Assert.Contains("hold times", leg.FailureReason);
        Assert.Equal(_harness.Erin!.NodeId, Assert.Single(leg.Route).NodeId);
        Assert.Equal(AttributionHoldTime.ToDuration(Assert.Single(_erin.ReportedHoldTimes)), leg.Route[0].HoldTime);
    }

    [Fact]
    public async Task Given_NoHopAttributes_When_TheTrampolineRefusesTheRelay_Then_TheResultIsUnchanged()
    {
        // Arrange
        _david.RefuseRelay = _ => RefuseWithBobsPolicy();
        var invoice = await TrampolinePaymentHarnessTests.CreateInvoiceAsync(_harness.Erin!, s_amount);

        // Act
        var payment = await PayAsync(invoice);

        // Assert
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.TrampolineFeeOrExpiryInsufficient, payment.FailureCode);
        Assert.Equal(1, payment.FailureSourceIndex);
        Assert.DoesNotContain("hold times", payment.FailureReason);
        Assert.DoesNotContain("attribution_data", payment.FailureReason);
        Assert.All(payment.Route, h => Assert.Null(h.HoldTime));
    }

    [Fact]
    public async Task Given_AFailedPaymentThroughATrampoline_When_Stored_Then_ItRecordsNoFeeAndBooksNone()
    {
        // Arrange (NL-982): the attempt offered Carol's routing fee and David's trampoline fee
        _david.RefuseRelay = _ => RefuseWithBobsPolicy();
        var invoice = await TrampolinePaymentHarnessTests.CreateInvoiceAsync(_harness.Erin!, s_amount);

        // Act
        var payment = await PayAsync(invoice);

        // Assert: nothing was paid, so the payment shows no fee, in memory and stored, and the books record none
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.True(payment.Fee.IsZero);
        var stored = await _harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash,
                                                                       TestContext.Current.CancellationToken);
        Assert.True(stored!.Fee.IsZero);
        var failed = Assert.Single(_harness.Bob.Accounting.Saved, e => e.Kind == AccountingEventKind.PaymentFailed);
        Assert.Equal(0, failed.FeeMsat);
        Assert.DoesNotContain(_harness.Bob.Accounting.Saved, e => e.Kind == AccountingEventKind.PaymentSucceeded);
    }

    private void EnableAttribution()
    {
        _harness.Carol.Switch.UseAttribution = true;
        _harness.David.Switch.UseAttribution = true;
        _david.UseAttribution = true;
        _erin.UseAttribution = true;
    }

    /// <summary>trampoline_fee_or_expiry_insufficient with the policy Bob offers by default: Bob does not retry.
    /// </summary>
    private FailureMessage RefuseWithBobsPolicy()
    {
        var options = _harness.Bob.Services.GetRequiredService<IOptions<PaymentSendOptions>>().Value;
        return FailureMessage.TrampolineFeeOrExpiryInsufficient(options.TrampolineFeeBaseMsat,
                                                                options.TrampolineFeeProportionalMillionths,
                                                                options.TrampolineCltvExpiryDelta);
    }

    private async Task<PaymentModel> PayAsync(InvoiceModel invoice)
    {
        var options = new PayInvoiceOptions { TrampolineNode = _harness.David.NodeId, Timeout = s_timeout };
        var result = await _harness.RunAsync(_harness.Bob.PaymentService.PayInvoiceAsync(
                                                 invoice.Bolt11!, null, options,
                                                 TestContext.Current.CancellationToken));
        return result.Payment;
    }

    /// <summary>The hop's recorded hold time is what it reported, and at least what it was set up to hold.</summary>
    private static void AssertHoldTime(PaymentHop hop, uint reported, TimeSpan atLeast)
    {
        Assert.Equal(AttributionHoldTime.ToDuration(reported), hop.HoldTime);
        Assert.InRange(hop.HoldTime!.Value, atLeast, atLeast + s_slack);
        Assert.Equal(0, hop.HoldTime.Value.Ticks
                      % (TimeSpan.TicksPerMillisecond * OnionConstants.AttributionHoldTimeUnitMilliseconds));
    }
}