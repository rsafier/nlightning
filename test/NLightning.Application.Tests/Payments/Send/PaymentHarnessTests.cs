using System.Security.Cryptography;

namespace NLightning.Application.Tests.Payments.Send;

using Application.Payments.Send.Interfaces;
using Bolt11.Models;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Harness;

/// <summary>
/// ABCD W2-C proof, in process with real crypto and real onions: Bob pays Carol directly and pays David through Carol
/// along the route hint of David's own invoice (<c>InvoiceService</c>, NL-245); failures are decrypted at Bob with the
/// per-hop shared secrets and the <c>FailureInterpreter</c> result is stored on the payment.
/// </summary>
public class PaymentHarnessTests : IDisposable
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    private readonly PaymentHarness _harness = new();

    public PaymentHarnessTests()
    {
        // David knows Carol's channel_update for Carol-David (W1-E), so his invoices hint through her
        var carolUpdate = PaymentHarnessNode.PeerUpdate(_harness.Carol, PaymentHarness.ScidCarolDavid);
        _harness.David.ChannelUpdates.Setup(s => s.TryGetRemoteChannelUpdate(_harness.CarolDavid, out carolUpdate))
                .Returns(true);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Given_CarolInvoice_When_BobPaysDirectly_Then_SucceedsWithPreimageAndBalancesMove()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.Carol.InvoiceService.CreateInvoiceAsync(s_amount, "direct", null, ct);
        var bobBefore = _harness.Bob.Channel(_harness.BobCarol).LocalBalance;
        var carolBefore = _harness.Carol.Channel(_harness.BobCarol).LocalBalance;

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(invoice.Preimage, payment.Preimage);
        Assert.Equal(LightningMoney.Zero, payment.Fee);
        Assert.Equal(_harness.BobCarol, payment.OutgoingChannelId);
        var hop = Assert.Single(payment.Route);
        Assert.Equal(_harness.Carol.NodeId, hop.NodeId);
        Assert.Equal(PaymentHarness.ScidBobCarol, hop.ShortChannelId);
        Assert.Equal(s_amount, hop.Amount);
        Assert.Equal(PaymentHarness.BlockHeight + 40 + 3, hop.CltvExpiry);
        Assert.Equal(bobBefore - s_amount, _harness.Bob.Channel(_harness.BobCarol).LocalBalance);
        Assert.Equal(carolBefore + s_amount, _harness.Carol.Channel(_harness.BobCarol).LocalBalance);
        Assert.Equal(InvoiceStatus.Accepted,
                     (await _harness.Carol.Invoices.GetByPaymentHashAsync(invoice.PaymentHash))!.Status);
        Assert.Contains(_harness.Bob.Switch.PaymentOutcomes, o => o is { Event: OutgoingHtlcFulfilled, Handled: true });
        AssertNoPendingHtlcs();
    }

    [Fact]
    public async Task Given_DavidInvoiceWithHintThroughCarol_When_BobPays_Then_CarolForwardsAndBobPaysHerFee()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "via carol", null, ct);
        var feeCarol = 2_000 + s_amount.MilliSatoshi * 500 / 1_000_000;
        var bobBc = _harness.Bob.Channel(_harness.BobCarol).LocalBalance;
        var carolBc = _harness.Carol.Channel(_harness.BobCarol).LocalBalance;
        var carolCd = _harness.Carol.Channel(_harness.CarolDavid).LocalBalance;
        var davidCd = _harness.David.Channel(_harness.CarolDavid).LocalBalance;

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert: David's invoice carries the hint through Carol with Carol's own policy (NL-245)
        var hint = Assert.Single(Assert.Single(Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest).RouteHints));
        Assert.Equal(_harness.Carol.NodeId, hint.CompactPubKey);
        Assert.Equal(PaymentHarness.ScidCarolDavid, hint.ShortChannelId);
        Assert.Equal((2_000u, 500u, (ushort)40),
                     (hint.FeeBaseMsat, hint.FeeProportionalMillionths, hint.CltvExpiryDelta));

        // Assert: the payment and its stored route
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(invoice.Preimage, payment.Preimage);
        Assert.Equal(invoice.PaymentHash, (Domain.Crypto.ValueObjects.Hash)SHA256.HashData(payment.Preimage!.Value));
        Assert.Equal(feeCarol, payment.Fee.MilliSatoshi);
        Assert.Equal(2, payment.Route.Count);
        Assert.Equal((_harness.Carol.NodeId, PaymentHarness.ScidBobCarol, s_amount.MilliSatoshi + feeCarol,
                      PaymentHarness.BlockHeight + 40 + 3 + 40),
                     (payment.Route[0].NodeId, payment.Route[0].ShortChannelId, payment.Route[0].Amount.MilliSatoshi,
                      payment.Route[0].CltvExpiry));
        Assert.Equal((_harness.David.NodeId, PaymentHarness.ScidCarolDavid, s_amount, PaymentHarness.BlockHeight + 43),
                     (payment.Route[1].NodeId, payment.Route[1].ShortChannelId, payment.Route[1].Amount,
                      payment.Route[1].CltvExpiry));
        Assert.Equal(2, payment.HopSharedSecrets.Distinct().Count());

        // Assert: balances (msat) on both channels
        var amountBc = s_amount.MilliSatoshi + feeCarol;
        Assert.Equal(bobBc.MilliSatoshi - amountBc, _harness.Bob.Channel(_harness.BobCarol).LocalBalance.MilliSatoshi);
        Assert.Equal(carolBc.MilliSatoshi + amountBc,
                     _harness.Carol.Channel(_harness.BobCarol).LocalBalance.MilliSatoshi);
        Assert.Equal(carolCd - s_amount, _harness.Carol.Channel(_harness.CarolDavid).LocalBalance);
        Assert.Equal(davidCd + s_amount, _harness.David.Channel(_harness.CarolDavid).LocalBalance);
        AssertNoPendingHtlcs();
    }

    [Fact]
    public async Task Given_CanceledDavidInvoice_When_BobPays_Then_Hop1FailureIsDecryptedAndStored()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "canceled", null, ct);
        Assert.True(await _harness.David.InvoiceService.CancelInvoiceAsync(invoice.PaymentHash, ct));
        var bobBc = _harness.Bob.Channel(_harness.BobCarol).LocalBalance;

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert: David's 0x400F travelled back through Carol (wrapped) and Bob attributed it to hop 1
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, payment.FailureCode);
        Assert.Equal(1, payment.FailureSourceIndex);
        Assert.Contains("payee", payment.FailureReason);
        Assert.Null(payment.Preimage);
        Assert.Equal(payment.Status, (await _harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct))!
                                        .Status);
        Assert.Equal(bobBc, _harness.Bob.Channel(_harness.BobCarol).LocalBalance);
        Assert.Contains(_harness.Bob.Switch.PaymentOutcomes, o => o is { Event: OutgoingHtlcFailed, Handled: true });
        AssertNoPendingHtlcs();
    }

    [Fact]
    public async Task Given_CarolCannotForward_When_BobPaysDavid_Then_UnknownNextPeerFromHop0IsStored()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "no forward", null, ct);
        _harness.Carol.Switch.FailEveryForward = true;

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.UnknownNextPeer, payment.FailureCode);
        Assert.Equal(0, payment.FailureSourceIndex);
        Assert.Contains(_harness.Carol.NodeId.ToString(), payment.FailureReason);
        AssertNoPendingHtlcs();
    }

    [Fact]
    public async Task Given_PaidInvoice_When_PaidAgain_Then_RefusedAndListPaymentsShowsOneSucceeded()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.Carol.InvoiceService.CreateInvoiceAsync(s_amount, "twice", null, ct);
        await _harness.RunAsync(_harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));
        var payments = await _harness.Bob.PaymentService.ListPaymentsAsync(0, 10, ct);

        // Assert
        Assert.Equal(typeof(InvalidOperationException), exception.GetType());
        var listed = Assert.Single(payments);
        Assert.Equal(PaymentStatus.Succeeded, listed.Status);
        Assert.Equal(invoice.PaymentHash, listed.PaymentHash);
    }

    [Fact]
    public async Task Given_FailedPayment_When_RetriedAfterTheInvoiceWorksAgain_Then_TheRetryReplacesItAndSucceeds()
    {
        // Arrange: the first attempt fails at Carol
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "retry", null, ct);
        _harness.Carol.Switch.FailEveryForward = true;
        var first = await _harness.RunAsync(
                        _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));
        _harness.Carol.Switch.FailEveryForward = false;

        // Act
        var second = await _harness.RunAsync(
                         _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert
        Assert.Equal(PaymentStatus.Failed, first.Status);
        Assert.Equal(PaymentStatus.Succeeded, second.Status);
        Assert.Null(second.FailureCode);
        Assert.Single(_harness.Bob.Payments.Payments);
        AssertNoPendingHtlcs();
    }

    [Fact]
    public async Task Given_AmountAboveOurBalance_When_BobPays_Then_NothingIsOfferedAndThePaymentFailsWithoutCode()
    {
        // Arrange: Bob has 1.5M sat on Bob-Carol, and Carol's invoice offers basic_mpp (advertised by default since
        // ABCD W6-B), but even a split over every path cannot carry it; the engine's own sender rules
        // (LocalLiquidityEstimator) say no route can carry it, so nothing is offered
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.Carol.InvoiceService.CreateInvoiceAsync(LightningMoney.Satoshis(1_900_000),
                                                                              "too much", null, ct);

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Null(payment.FailureCode);
        Assert.Null(payment.OutgoingHtlcId);
        Assert.Contains("can send at most", payment.FailureReason);
        Assert.Contains("even split over every path", payment.FailureReason);
        Assert.Empty(_harness.Carol.Switch.Events);
        AssertNoPendingHtlcs();
    }

    [Fact]
    public async Task Given_ShortTimeout_When_TheOutcomeIsLate_Then_InFlightIsReturnedAndTheOutcomeIsStoredLater()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.Carol.InvoiceService.CreateInvoiceAsync(s_amount, "late", null, ct);

        // Act: nothing is delivered while Bob waits
        var payment = await _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null,
                                                                          TimeSpan.FromMilliseconds(50), ct);
        await _harness.PumpAsync();

        // Assert
        Assert.Equal(PaymentStatus.InFlight, payment.Status);
        Assert.Equal(0UL, payment.OutgoingHtlcId);
        var stored = await _harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct);
        Assert.Equal(PaymentStatus.Succeeded, stored!.Status);
        Assert.Equal(invoice.Preimage, stored.Preimage);
        AssertNoPendingHtlcs();
    }

    [Fact]
    public async Task Given_HintFeeAboveTheLimit_When_BobPays_Then_NoRouteIsStoredAndNothingIsOffered()
    {
        // Arrange: Carol's policy (as David knows it) charges 10,000 sat per payment
        var ct = TestContext.Current.CancellationToken;
        ChannelUpdatePayload? expensive = new(ChannelUpdatePayload.EmptySignature,
                                              Domain.Protocol.Constants.ChainConstants.Regtest,
                                              PaymentHarness.ScidCarolDavid, 2, ChannelUpdatePayload.MessageFlagMustBeOne,
                                              0, 40, 1_000, 10_000_000, 1, PaymentHarness.FundingSatoshis * 1_000);
        _harness.David.ChannelUpdates.Setup(s => s.TryGetRemoteChannelUpdate(_harness.CarolDavid, out expensive))
                .Returns(true);
        var invoice = await _harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "expensive", null, ct);

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Contains("exceeds the limit", payment.FailureReason);
        Assert.Empty(_harness.Bob.Switch.Events);
        Assert.Empty(_harness.Carol.Switch.Events);
    }

    [Fact]
    public async Task Given_TheHtlcIdSaveFailsAndNoOriginIsStored_When_CarolFulfills_Then_ThePaymentSucceeds()
    {
        // Arrange: no origin is stored (an HTLC offered before NL-250); the save of the HTLC id fails
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.Carol.InvoiceService.CreateInvoiceAsync(s_amount, "id lost", null, ct);
        _harness.Bob.Store.DropOrigins = true;
        _harness.Bob.Payments.FailNextUpdates = 1;

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert: the fulfill matched through the HTLC's record in channel memory
        Assert.Empty(_harness.Bob.Store.Origins);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(invoice.Preimage, payment.Preimage);
        Assert.Equal((_harness.BobCarol, 0UL), (payment.OutgoingChannelId!.Value, payment.OutgoingHtlcId!.Value));
        AssertNoPendingHtlcs();
    }

    [Fact]
    public async Task Given_TheHtlcIdSaveFailsAndNoOriginIsStored_When_DavidFails_Then_TheFailureIsStored()
    {
        // Arrange: the failed HTLC has left channel memory when its failure arrives
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.David.InvoiceService.CreateInvoiceAsync(s_amount, "id lost", null, ct);
        Assert.True(await _harness.David.InvoiceService.CancelInvoiceAsync(invoice.PaymentHash, ct));
        _harness.Bob.Store.DropOrigins = true;
        _harness.Bob.Payments.FailNextUpdates = 1;

        // Act
        var payment = await _harness.RunAsync(
                          _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert
        Assert.Empty(_harness.Bob.Store.Origins);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, payment.FailureCode);
        Assert.Equal(1, payment.FailureSourceIndex);
        AssertNoPendingHtlcs();
    }

    [Fact]
    public async Task Given_TheOfferFailsToPersist_When_BobPays_Then_ThePaymentFailsAndTheHashCanBePaidAgain()
    {
        // Arrange: the add's save throws (neither a refusal nor an unknown channel)
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.Carol.InvoiceService.CreateInvoiceAsync(s_amount, "save fails", null, ct);
        _harness.Bob.Store.FailNextApply = true;

        // Act
        var first = await _harness.RunAsync(
                        _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));
        var second = await _harness.RunAsync(
                         _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, s_timeout, ct));

        // Assert
        Assert.Equal(PaymentStatus.Failed, first.Status);
        Assert.Null(first.FailureCode);
        Assert.Contains("Injected channel state save failure", first.FailureReason);
        Assert.Equal(PaymentStatus.Succeeded, second.Status);
        AssertNoPendingHtlcs();
    }

    [Fact]
    public async Task Given_ALiveHtlcWhoseIdWasNotRecorded_When_ReconcilingAtStartup_Then_ItIsAttached()
    {
        // Arrange: the id save fails and nothing is delivered while Bob waits
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.Carol.InvoiceService.CreateInvoiceAsync(s_amount, "reconcile", null, ct);
        _harness.Bob.Payments.FailNextUpdates = 1;
        await _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, TimeSpan.FromMilliseconds(50), ct);
        Assert.Null((await _harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct))!.OutgoingHtlcId);

        // Act
        var reconciled = await OutcomeHandler(_harness.Bob).ReconcileInFlightPaymentsAsync(ct);
        var attached = await _harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct);
        await _harness.PumpAsync();

        // Assert
        Assert.Equal(1, reconciled);
        Assert.Equal(PaymentStatus.InFlight, attached!.Status);
        Assert.Equal((_harness.BobCarol, 0UL), (attached.OutgoingChannelId!.Value, attached.OutgoingHtlcId!.Value));
        var stored = await _harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct);
        Assert.Equal(PaymentStatus.Succeeded, stored!.Status);
        AssertNoPendingHtlcs();
    }

    [Fact]
    public async Task Given_ALiveUnrecordedRetry_When_AnEarlierAttemptsFailureIsReplayed_Then_TheRetryIsNotFailed()
    {
        // Arrange: the attempt's HTLC 0 is live and its id was not recorded
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _harness.Carol.InvoiceService.CreateInvoiceAsync(s_amount, "replay", null, ct);
        _harness.Bob.Payments.FailNextUpdates = 1;
        await _harness.Bob.PaymentService.PayInvoiceAsync(invoice.Bolt11, null, TimeSpan.FromMilliseconds(50), ct);

        // Act: an archived failure of another HTLC with the same hash (an earlier attempt) is replayed
        var handled = await OutcomeHandler(_harness.Bob).HandleOutgoingHtlcFailedAsync(
                          new OutgoingHtlcFailed(_harness.BobCarol, 77, invoice.PaymentHash,
                                                 HtlcRemoval.Fail(new byte[292])), ct);
        var afterReplay = await _harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct);
        await _harness.PumpAsync();

        // Assert
        Assert.False(handled);
        Assert.Equal(PaymentStatus.InFlight, afterReplay!.Status);
        var stored = await _harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct);
        Assert.Equal(PaymentStatus.Succeeded, stored!.Status);
        Assert.Equal(0UL, stored.OutgoingHtlcId);
        AssertNoPendingHtlcs();
    }

    private static IPaymentOutcomeHandler OutcomeHandler(PaymentHarnessNode node) =>
        (IPaymentOutcomeHandler)node.PaymentService;

    private void AssertNoPendingHtlcs()
    {
        foreach (var (node, channelId) in new[]
                 {
                     (_harness.Bob, _harness.BobCarol), (_harness.Carol, _harness.BobCarol),
                     (_harness.Carol, _harness.CarolDavid), (_harness.David, _harness.CarolDavid)
                 })
            Assert.Empty(node.Channel(channelId).Commitments!.Htlcs);
    }
}