namespace NLightning.Application.Tests.Payments.Send;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Channels.Commitments.Events;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Harness;

/// <summary>
/// NL-321 proof: the parts of a split payment that are not the one the payment row records are stored with their
/// routes (<c>PaymentParts</c>), so after a restart (a second <see cref="PaymentService"/> over the same stores, with
/// no sending session) a part's error onion is still decrypted and the startup reconciliation settles the parts whose
/// HTLCs died while the node was down.
/// </summary>
public class PaymentPartsRestartTests
{
    private static readonly LightningMoney s_amount = LightningMoney.Satoshis(700_000);

    /// <summary>Two Bob-Carol channels with 500,000 sat of Bob's each: Carol's invoice splits over both.</summary>
    private static PaymentHarness Harness()
    {
        return new PaymentHarness(new PaymentHarnessTopology(SecondBobCarol: true,
                                                             BobCarolFundingSatoshis: 1_000_000,
                                                             BobCarolPushSatoshis: 500_000));
    }

    /// <summary>
    /// Pays Carol's invoice split in two parts, both of which Carol fails (her payee failure), while Bob's outcomes
    /// are consumed by the test instead of the (original) payment service.
    /// </summary>
    private static async Task<(PaymentHarness Harness, InvoiceModel Invoice,
        List<OutgoingHtlcFailed> Failures, PayInvoiceResult Result)> PaySplitAndFailAsync(PaymentHarness harness)
    {
        var invoice = await harness.Carol.CreateMppInvoiceAsync(s_amount, []);
        var failures = new List<OutgoingHtlcFailed>();
        harness.Carol.Switch.FinalHopInterceptor = (htlc, _) =>
            FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(htlc.AmountMsat),
                                                            PaymentHarness.BlockHeight);
        harness.Bob.Switch.OwnOutcomeInterceptor = e =>
        {
            if (e is OutgoingHtlcFailed failed)
                failures.Add(failed);
            return true;
        };

        // Nothing is delivered to the payment service: both parts stay "in flight" there while the real HTLCs die
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null,
                                                new PayInvoiceOptions { Timeout = TimeSpan.FromSeconds(2) },
                                                TestContext.Current.CancellationToken));
        return (harness, invoice, failures, result);
    }

    [Fact]
    public async Task Given_APartsFailureArrivesAfterARestart_When_ItsRouteIsStored_Then_ItsErrorIsDecrypted()
    {
        // Arrange: two parts, both failed at Carol with the real (encrypted) errors, but the events never reached the
        // payment service; the restart loses the session
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var (_, invoice, failures, result) = await PaySplitAndFailAsync(harness);

        Assert.Equal(PaymentStatus.InFlight, result.Payment.Status);
        var parts = await harness.Bob.Parts.GetForPaymentAsync(invoice.PaymentHash);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(PaymentPartState.InFlight, p.State));

        // Act: after the restart, the failure of the part the row does not record is matched through the part rows
        var stored = await harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct);
        var other = Assert.Single(failures, f => f.ChannelId != stored!.OutgoingChannelId
                                              || f.HtlcId != stored!.OutgoingHtlcId);
        var handled = await harness.Bob.RestartedPaymentOutcomeHandler().HandleOutgoingHtlcFailedAsync(other, ct);

        // Assert: the part's stored route decrypts its error (before NL-321 the payment failed without a code)
        Assert.True(handled);
        var failed = await harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct);
        Assert.Equal(PaymentStatus.Failed, failed!.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, failed.FailureCode);
        Assert.Equal(0, failed.FailureSourceIndex); // a one-hop part: the payee
        Assert.Contains("payee", failed.FailureReason);
        Assert.Null(failed.Preimage);
        Assert.Equal((stored!.OutgoingChannelId, stored.OutgoingHtlcId),
                     (failed.OutgoingChannelId, failed.OutgoingHtlcId));

        var failedParts = await harness.Bob.Parts.GetForPaymentAsync(invoice.PaymentHash);
        var failedPart = Assert.Single(failedParts, p => p.ChannelId == other.ChannelId && p.HtlcId == other.HtlcId);
        Assert.Equal(PaymentPartState.Failed, failedPart.State);

        // NL-602: the session-less failure recorded the payment's final failure once; a replay of it adds nothing
        var recorded = Assert.Single(harness.Bob.Accounting.Saved);
        Assert.Equal(AccountingEventKind.PaymentFailed, recorded.Kind);
        Assert.Equal(AccountingEventKeys.PaymentFailed(invoice.PaymentHash, failed.CreatedAt.UtcTicks), recorded.EventKey);
        Assert.False(await harness.Bob.RestartedPaymentOutcomeHandler().HandleOutgoingHtlcFailedAsync(other, ct));
        Assert.Single(harness.Bob.Accounting.Saved);
    }

    [Fact]
    public async Task Given_EveryPartsHtlcDiedWhileDown_When_ReconcilingAtStartup_Then_ThePaymentFailsWithoutACode()
    {
        // Arrange: two parts, both failed at Carol while Bob's outcomes were consumed by the test (a crash before
        // they arrived); the restart loses the session, the HTLCs are gone from the channels
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var (_, invoice, _, result) = await PaySplitAndFailAsync(harness);
        Assert.Equal(PaymentStatus.InFlight, result.Payment.Status);

        // Act
        var reconciled = await harness.Bob.RestartedPaymentOutcomeHandler().ReconcileInFlightPaymentsAsync(ct);

        // Assert: the parts' HTLCs are gone, so their outcome is unknown (no error onion arrives for a dead HTLC);
        // with no live part left the payment is failed without a code
        Assert.Equal(1, reconciled);
        var failed = await harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct);
        Assert.Equal(PaymentStatus.Failed, failed!.Status);
        Assert.Null(failed.FailureCode);
        Assert.Contains("after the restart", failed.FailureReason);
        var parts = await harness.Bob.Parts.GetForPaymentAsync(invoice.PaymentHash);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(PaymentPartState.Failed, p.State));

        // NL-602: the reconciliation's failure is recorded once; reconciling again adds nothing
        var recorded = Assert.Single(harness.Bob.Accounting.Saved);
        Assert.Equal(AccountingEventKind.PaymentFailed, recorded.Kind);
        Assert.Contains("after the restart", recorded.Details["reason"]);
        Assert.Equal(0, await harness.Bob.RestartedPaymentOutcomeHandler().ReconcileInFlightPaymentsAsync(ct));
        Assert.Single(harness.Bob.Accounting.Saved);
    }

    [Fact]
    public async Task Given_AnUnknownOutcomeAtStartup_When_AFulfillIsReplayedLater_Then_OnlyTheSuccessIsPublished()
    {
        // Arrange (NL-1001): the reconciliation fails the payment for an unknown outcome; that is not final, so no
        // PaymentFailedEvent is published (a Cashu mint would give the melt's ecash back), and a fulfill replayed later
        // still proves it paid
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var (_, invoice, _, result) = await PaySplitAndFailAsync(harness);
        Assert.Equal(PaymentStatus.InFlight, result.Payment.Status);
        using var events = harness.Bob.PaymentEvents.Subscribe();
        var restarted = harness.Bob.RestartedPaymentOutcomeHandler();

        // Act
        Assert.Equal(1, await restarted.ReconcileInFlightPaymentsAsync(ct));
        var unknown = await harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct);
        var part = (await harness.Bob.Parts.GetForPaymentAsync(invoice.PaymentHash))[0];
        await restarted.HandleOutgoingHtlcFulfilledAsync(
            new OutgoingHtlcFulfilled(part.ChannelId, part.HtlcId, invoice.PaymentHash, invoice.Preimage), ct);

        // Assert: the failure was an unknown outcome and published nothing; the replayed fulfill is the first event
        Assert.True(unknown!.IsOutcomeUnknown);
        var succeeded = Assert.IsType<PaymentSucceededEvent>(await Events.PaymentEventHubTests.ReadOneAsync(events));
        Assert.Equal(invoice.PaymentHash, succeeded.PaymentHash);
        Assert.Equal(PaymentStatus.Succeeded,
                     (await harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct))!.Status);
    }

    [Fact]
    public async Task Given_APartStillLiveAfterARestart_When_Reconciling_Then_ThePaymentStaysInFlight()
    {
        // Arrange: two parts; only the first one fails at Carol (its event consumed), the second is held by her
        using var harness = Harness();
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.Carol.CreateMppInvoiceAsync(s_amount, []);
        var seen = 0;
        harness.Carol.Switch.FinalHopInterceptor = (htlc, _) => Interlocked.Increment(ref seen) == 1
            ? FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(htlc.AmountMsat),
                                                              PaymentHarness.BlockHeight)
            : null;
        harness.Bob.Switch.OwnOutcomeInterceptor = e => e is OutgoingHtlcFailed;

        // Act: the call times out with the second part still held
        var result = await harness.RunAsync(harness.Bob.PaymentService.PayInvoiceAsync(
                                                invoice.Bolt11!, null,
                                                new PayInvoiceOptions { Timeout = TimeSpan.FromSeconds(2) },
                                                TestContext.Current.CancellationToken));
        Assert.Equal(PaymentStatus.InFlight, result.Payment.Status);
        var reconciled = await harness.Bob.RestartedPaymentOutcomeHandler().ReconcileInFlightPaymentsAsync(ct);

        // Assert: the failed part's row is settled, the live one keeps the payment in flight
        Assert.Equal(0, reconciled);
        var stored = await harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct);
        Assert.Equal(PaymentStatus.InFlight, stored!.Status);
        var parts = await harness.Bob.Parts.GetForPaymentAsync(invoice.PaymentHash);
        Assert.Equal(2, parts.Count);
        Assert.Single(parts, p => p.State == PaymentPartState.Failed);
        Assert.Single(parts, p => p.State == PaymentPartState.InFlight);
        Assert.Empty(harness.Bob.Accounting.Saved); // NL-602: still in flight, nothing final to record
    }
}