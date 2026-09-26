namespace NLightning.Application.Tests.Payments.Send;

using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Harness;

/// <summary>
/// ABCD W7-A proof (NL-326, NL-072, NL-022; BOLT 4 attributable failures and hold times), in process with real crypto,
/// real onions and the production channel layer: David (erring or final node) creates <c>attribution_data</c> with his
/// hold time, Carol wraps it with hers, both travel in the <c>update_fail_htlc</c>/<c>update_fulfill_htlc</c> TLV 1
/// through the production handlers, engine and <c>IChannelOperations</c>, and Bob's <c>PaymentService</c> verifies them
/// hop by hop, blames the erring hop and records every hop's hold time on the payment's route.
/// </summary>
/// <remarks>
/// The switch is the harness stand-in (<see cref="HarnessForwardingSwitch"/> with
/// <see cref="HarnessForwardingSwitch.UseAttribution"/>), which calls the seam the production switch uses:
/// <c>IChannelOperations.GetHoldTimeAsync</c> and the attributed <c>FailHtlcAsync</c>/<c>FulfillHtlcAsync</c>
/// overloads. Each node stamps its HTLCs a fixed time in the past (<see cref="HarnessStateStore.AddedAtOffset"/>), so the
/// hold times are at least that long.
/// </remarks>
public class AttributionHarnessTests : IDisposable
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_carolHeld = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan s_davidHeld = TimeSpan.FromSeconds(1);

    // Generous for a slow CI machine: hold times count the real time the harness takes too
    private static readonly TimeSpan s_slack = TimeSpan.FromSeconds(20);

    private readonly PaymentHarness _harness = new();

    public AttributionHarnessTests()
    {
        // David knows Carol's channel_update for Carol-David (W1-E), so his invoices hint through her
        var carolUpdate = PaymentHarnessNode.PeerUpdate(_harness.Carol, PaymentHarness.ScidCarolDavid);
        _harness.David.ChannelUpdates.Setup(s => s.TryGetRemoteChannelUpdate(_harness.CarolDavid, out carolUpdate))
                .Returns(true);
        _harness.Carol.Store.AddedAtOffset = s_carolHeld;
        _harness.David.Store.AddedAtOffset = s_davidHeld;
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Given_EveryHopAttributes_When_ThePayeeFails_Then_BobVerifiesTheErringHopAndRecordsHoldTimes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnableAttribution(_harness.Carol, _harness.David);
        var invoice = await _harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "canceled", null, ct);
        Assert.True(await _harness.David.InvoiceService.CancelInvoiceAsync(invoice.PaymentHash, ct));

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert: the legacy failure is still read (0x400F from hop 1), and the attribution verified every hop
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, payment.FailureCode);
        Assert.Equal(1, payment.FailureSourceIndex);
        Assert.Contains("hold times", payment.FailureReason);
        Assert.DoesNotContain("did not verify", payment.FailureReason);

        // Assert: each hop's hold time is the one it reported (David created it, Carol wrapped it with hers)
        var carolReported = Assert.Single(_harness.Carol.Switch.ReportedHoldTimes).HoldTime;
        var davidReported = Assert.Single(_harness.David.Switch.ReportedHoldTimes).HoldTime;
        AssertHoldTime(payment.Route[0], carolReported, s_carolHeld);
        AssertHoldTime(payment.Route[1], davidReported, s_davidHeld);
        Assert.True(carolReported >= davidReported, $"Carol held {carolReported}, David {davidReported}");

        // Assert: the stored payment (what listpayments shows) has them too
        var stored = await _harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct);
        Assert.Equal(payment.Route.Select(h => h.HoldTime), stored!.Route.Select(h => h.HoldTime));
    }

    [Fact]
    public async Task Given_EveryHopAttributes_When_ThePayeeFulfills_Then_BobRecordsEachHopsHoldTime()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnableAttribution(_harness.Carol, _harness.David);
        var invoice = await _harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "held", null, ct);

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        var stored = await _harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct);
        AssertHoldTime(stored!.Route[0], Assert.Single(_harness.Carol.Switch.ReportedHoldTimes).HoldTime, s_carolHeld);
        AssertHoldTime(stored.Route[1], Assert.Single(_harness.David.Switch.ReportedHoldTimes).HoldTime, s_davidHeld);
    }

    [Fact]
    public async Task Given_CarolGarblesTheReturnPacket_When_BobDecrypts_Then_TheAttributionBlamesCarol()
    {
        // Arrange: Carol wraps correctly, then flips a byte of the reason she sends (after her HMACs covered it)
        var ct = TestContext.Current.CancellationToken;
        EnableAttribution(_harness.Carol, _harness.David);
        _harness.Carol.Switch.TamperFailure = packet =>
        {
            var reason = packet.Reason.ToArray();
            reason[40] ^= 0x01;
            return new AttributedErrorPacket(reason, packet.AttributionData);
        };
        var invoice = await _harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "garbled", null, ct);
        Assert.True(await _harness.David.InvoiceService.CancelInvoiceAsync(invoice.PaymentHash, ct));

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert: no hop authenticates the return packet, and Carol's own HMAC (over the packet she forwarded) fails
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Null(payment.FailureCode);
        Assert.Equal(0, payment.FailureSourceIndex);
        Assert.Contains("attribution_data blames hop 0", payment.FailureReason);
        Assert.All(payment.Route, h => Assert.Null(h.HoldTime));
    }

    [Fact]
    public async Task Given_ThePayeeSendsNoAttribution_When_CarolWrapsAZeroBlock_Then_OnlyCarolsHoldTimeIsVerified()
    {
        // Arrange: David does not support option_attribution_data; Carol instantiates an all-zero block (BOLT 4)
        var ct = TestContext.Current.CancellationToken;
        EnableAttribution(_harness.Carol);
        var invoice = await _harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "partial", null, ct);
        Assert.True(await _harness.David.InvoiceService.CancelInvoiceAsync(invoice.PaymentHash, ct));

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert: the erring hop still comes from the return packet; attribution stops at David, who added none
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, payment.FailureCode);
        Assert.Equal(1, payment.FailureSourceIndex);
        Assert.Contains("attribution_data of hop 1", payment.FailureReason);
        AssertHoldTime(payment.Route[0], Assert.Single(_harness.Carol.Switch.ReportedHoldTimes).HoldTime, s_carolHeld);
        Assert.Null(payment.Route[1].HoldTime);
    }

    [Fact]
    public async Task Given_NoHopAttributes_When_ThePayeeFails_Then_TheLegacyResultIsUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "legacy", null, ct);
        Assert.True(await _harness.David.InvoiceService.CancelInvoiceAsync(invoice.PaymentHash, ct));

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, payment.FailureCode);
        Assert.Equal(1, payment.FailureSourceIndex);
        Assert.DoesNotContain("hold times", payment.FailureReason);
        Assert.All(payment.Route, h => Assert.Null(h.HoldTime));
    }

    [Fact]
    public async Task Given_CarolFailsTheForward_When_ItCarriesAttribution_Then_BobBlamesCarolWithHerHoldTime()
    {
        // Arrange: Carol is the erring node (unknown_next_peer) and creates the attribution_data herself
        var ct = TestContext.Current.CancellationToken;
        EnableAttribution(_harness.Carol, _harness.David);
        _harness.Carol.Switch.FailEveryForward = true;
        var invoice = await _harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "no forward", null, ct);

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.UnknownNextPeer, payment.FailureCode);
        Assert.Equal(0, payment.FailureSourceIndex);
        AssertHoldTime(payment.Route[0], Assert.Single(_harness.Carol.Switch.ReportedHoldTimes).HoldTime, s_carolHeld);
        Assert.Null(payment.Route[1].HoldTime);
    }

    private static void EnableAttribution(params PaymentHarnessNode[] nodes)
    {
        foreach (var node in nodes)
            node.Switch.UseAttribution = true;
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