using System.Security.Cryptography;

namespace NLightning.Application.Tests.Payments.Send;

using Application.Payments.Send;
using Application.Payments.Switch;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Harness;
using NLightning.Tests.Utils;

/// <summary>
/// NL-1276 proof of payroute phase C: routes attached to a payroute payment still in flight, in process with real
/// crypto and onions. Bob pays David's basic_mpp invoice in two shards; Carol fails the first, David holds the second,
/// and a replacement attached inside the window completes the set — or is refused before anything is offered (another
/// secret or total, too little, too late, nothing in flight, a settled payment), or fails with its own attributed
/// failure. The LND form (SendToRouteV2 with one shard per call) joins the set call by call and answers per shard.
/// </summary>
public partial class PayRouteTests
{
    [Fact]
    public async Task Given_OneShardFailedAndOneHeld_When_AReplacementIsAttached_Then_TheSetSettlesAndEachCallReportsItsRoutes()
    {
        // Arrange: the first shard (25,000,000 msat) fails at Carol, David holds the second (35,000,000 msat)
        using var harness = new PaymentHarness();
        var bobBefore = harness.Bob.Channel(harness.BobCarol).LocalBalance.MilliSatoshi;
        var davidBefore = harness.David.Channel(harness.CarolDavid).LocalBalance.MilliSatoshi;
        var (invoice, payTask, forwards) = await StartHeldSetAsync(harness);

        // Act: attach the replacement for the failed shard
        var attached = await PayRouteAsync(harness, AttachByInvoice(invoice.Bolt11!, ViaCarol(harness, s_firstShard)));
        var first = await harness.RunAsync(payTask);

        // Assert: the attached call reports only its own route, settled with the set
        Assert.Equal(PaymentStatus.Succeeded, attached.Payment.Status);
        Assert.Equal(invoice.Preimage, attached.Payment.Preimage);
        var replacement = Assert.Single(attached.Outcomes);
        Assert.Equal(0, replacement.Index);
        Assert.Equal(PaymentPartState.Succeeded, replacement.Status);
        Assert.NotNull(replacement.HtlcId);

        // Assert: the first call reports its two routes: the failed one attributed to Carol, the held one settled
        Assert.Equal(PaymentStatus.Succeeded, first.Payment.Status);
        Assert.Equal(2, first.Outcomes.Count);
        Assert.Equal(PaymentPartState.Failed, first.Outcomes[0].Status);
        Assert.Equal(FailureCode.TemporaryChannelFailure, first.Outcomes[0].FailureCode);
        Assert.Equal(0, first.Outcomes[0].FailureSourceIndex);
        Assert.Equal(PaymentPartState.Succeeded, first.Outcomes[1].Status);
        Assert.NotEqual(replacement.HtlcId, first.Outcomes[1].HtlcId);

        // Assert: three forwards at Carol, the payee got the whole total in two parts, the money moved once
        Assert.Equal(3, forwards());
        var received = harness.David.Switch.Received.ToArray();
        Assert.Equal(2, received.Length);
        Assert.All(received, r => Assert.Equal(s_total.MilliSatoshi, r.TotalMsat));
        var fees = Fee(harness.Carol, s_firstShard.MilliSatoshi) + Fee(harness.Carol, s_secondShard.MilliSatoshi);
        Assert.Equal(fees, first.Payment.Fee.MilliSatoshi);
        Assert.Equal(bobBefore - s_total.MilliSatoshi - fees,
                     harness.Bob.Channel(harness.BobCarol).LocalBalance.MilliSatoshi);
        Assert.Equal(davidBefore + s_total.MilliSatoshi,
                     harness.David.Channel(harness.CarolDavid).LocalBalance.MilliSatoshi);
        Assert.Equal(InvoiceStatus.Accepted,
                     (await harness.David.Invoices.GetByPaymentHashAsync(invoice.PaymentHash))!.Status);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_AHeldSet_When_AttachedRoutesDoNotFitIt_Then_EachIsRefusedBeforeAnythingIsOffered()
    {
        // Arrange
        using var harness = new PaymentHarness();
        var ct = TestContext.Current.CancellationToken;
        var (invoice, payTask, forwards) = await StartHeldSetAsync(harness);
        var replacement = ViaCarol(harness, s_firstShard);
        var otherInvoice = await harness.David.CreateMppInvoiceAsync(s_total, []);

        // Act / Assert: another payment secret
        var secret = await Assert.ThrowsAsync<ArgumentException>(() => PayRouteAsync(harness, new PayRouteRequest
        {
            PaymentHash = invoice.PaymentHash,
            PaymentSecret = new Secret(RandomNumberGenerator.GetBytes(32)),
            TotalAmount = s_total,
            Routes = [replacement],
            Attach = PayRouteAttachMode.Required
        }));
        Assert.Contains("payment secret differs", secret.Message);

        // Act / Assert: another total
        var total = await Assert.ThrowsAsync<ArgumentException>(() => PayRouteAsync(harness, new PayRouteRequest
        {
            PaymentHash = invoice.PaymentHash,
            PaymentSecret = invoice.PaymentSecret,
            TotalAmount = s_total + LightningMoney.MilliSatoshis(1_000),
            Routes = [replacement],
            Attach = PayRouteAttachMode.Required
        }));
        Assert.Contains("differs from the 60000000 msat", total.Message);

        // Act / Assert: with the part in flight the set would still not reach the total
        var tooLittle = await Assert.ThrowsAsync<ArgumentException>(
            () => PayRouteAsync(harness, AttachByInvoice(invoice.Bolt11!,
                                                         ViaCarol(harness, LightningMoney.MilliSatoshis(10_000_000)))));
        Assert.Contains("less than the total", tooLittle.Message);

        // Act / Assert: no payroute payment of that hash in flight
        var nothing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PayRouteAsync(harness, AttachByInvoice(otherInvoice.Bolt11!, replacement)));
        Assert.Contains("No payroute payment", nothing.Message);

        // Act / Assert: past the attach window (the payee's mpp_timeout) since the held part was offered
        harness.Bob.Clock.Advance(new PaymentSendOptions().PayRouteAttachWindow + TimeSpan.FromSeconds(1));
        var late = await Assert.ThrowsAsync<ArgumentException>(
            () => PayRouteAsync(harness, AttachByInvoice(invoice.Bolt11!, replacement)));
        Assert.Contains("Too late", late.Message);

        // Assert: nothing more was forwarded; the payee's timeout then fails the held part and the payment
        Assert.Equal(2, forwards());
        harness.David.Clock.Advance(HtlcSwitchOptions.DefaultMppTimeout + TimeSpan.FromSeconds(1));
        await harness.David.Switch.WhenIdleAsync();
        await harness.RunAsync(payTask);
        await WaitFor.TrueAsync(async () =>
        {
            await harness.PumpAsync();
            return (await harness.Bob.PaymentService.GetPaymentAsync(invoice.PaymentHash, ct))?.Status
                == PaymentStatus.Failed;
        }, TimeSpan.FromSeconds(10), "the payment to fail at the payee's mpp_timeout", ct);
        Assert.Equal(InvoiceStatus.Open,
                     (await harness.David.Invoices.GetByPaymentHashAsync(invoice.PaymentHash))!.Status);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_ASettledPayment_When_RoutesAreAttached_Then_ItIsRefused()
    {
        // Arrange: a payroute payment that succeeded
        using var harness = new PaymentHarness();
        var invoice = await InvoiceOf(harness);
        var paid = await PayRouteAsync(harness, ByInvoice(invoice.Bolt11!, ViaCarol(harness, s_amount)));
        Assert.Equal(PaymentStatus.Succeeded, paid.Payment.Status);
        var forwardsBefore = harness.Carol.Switch.Forwards.Count;

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PayRouteAsync(harness, AttachByInvoice(invoice.Bolt11!, ViaCarol(harness, s_amount))));

        // Assert
        Assert.Contains("already succeeded", exception.Message);
        Assert.Equal(forwardsBefore, harness.Carol.Switch.Forwards.Count);
        Assert.Equal(PaymentStatus.Succeeded, Assert.Single(harness.Bob.Payments.Payments).Status);
    }

    [Fact]
    public async Task Given_AnAttachedReplacementThatAlsoFails_When_ThePayeeTimesOut_Then_EveryRouteCarriesItsOwnFailure()
    {
        // Arrange: Carol fails the first shard and the replacement (her first and third forwards)
        using var harness = new PaymentHarness();
        var ct = TestContext.Current.CancellationToken;
        var (invoice, payTask, forwards) = await StartHeldSetAsync(harness, failForwards: [1, 3]);

        // Act: attach, let the replacement fail at Carol, then fire the payee's mpp_timeout for the held part
        var attachTask = harness.Bob.PaymentService.PayRouteAsync(
            AttachByInvoice(invoice.Bolt11!, ViaCarol(harness, s_firstShard)),
            new PayInvoiceOptions { Timeout = s_timeout }, ct);
        await WaitFor.TrueAsync(async () =>
        {
            await harness.PumpAsync();
            return forwards() == 3 && harness.Bob.Channel(harness.BobCarol).Commitments!.Htlcs.Count == 1;
        }, TimeSpan.FromSeconds(10), "the replacement to fail at Carol", ct);
        harness.David.Clock.Advance(HtlcSwitchOptions.DefaultMppTimeout + TimeSpan.FromSeconds(1));
        await harness.David.Switch.WhenIdleAsync();
        var attached = await harness.RunAsync(attachTask);
        var first = await harness.RunAsync(payTask);

        // Assert: the attached route failed at Carol (hop 0), the held one with the payee's mpp_timeout (hop 1)
        Assert.Equal(PaymentStatus.Failed, attached.Payment.Status);
        var replacement = Assert.Single(attached.Outcomes);
        Assert.Equal(PaymentPartState.Failed, replacement.Status);
        Assert.Equal(FailureCode.TemporaryChannelFailure, replacement.FailureCode);
        Assert.Equal(0, replacement.FailureSourceIndex);
        Assert.Equal(2, first.Outcomes.Count);
        Assert.Equal(FailureCode.TemporaryChannelFailure, first.Outcomes[0].FailureCode);
        Assert.Equal(0, first.Outcomes[0].FailureSourceIndex);
        Assert.Equal(FailureCode.MppTimeout, first.Outcomes[1].FailureCode);
        Assert.Equal(1, first.Outcomes[1].FailureSourceIndex);
        Assert.Equal(PaymentStatus.Failed, first.Payment.Status);
        Assert.Equal(InvoiceStatus.Open,
                     (await harness.David.Invoices.GetByPaymentHashAsync(invoice.PaymentHash))!.Status);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_LndShardsSentOneCallEach_When_OneFailsAndIsReplaced_Then_EachCallAnswersItsOwnShardAndTheSetSettles()
    {
        // Arrange: SendToRouteV2's form (NL-1276): one shard per call, the raw identity with its mpp record (secret
        // and total), joining the payroute payment of the hash; Carol fails her second forward
        using var harness = new PaymentHarness();
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.David.CreateMppInvoiceAsync(s_total, []);
        var forwards = 0;
        harness.Carol.Switch.ForwardInterceptor = (_, _) => Interlocked.Increment(ref forwards) == 2
            ? FailureMessage.TemporaryChannelFailure()
            : null;
        var options = new PayInvoiceOptions { Timeout = Timeout.InfiniteTimeSpan };

        // Act: the first shard is held by the payee and its call waits
        var firstTask = harness.Bob.PaymentService.PayRouteAsync(LndShard(invoice, ViaCarol(harness, s_firstShard)),
                                                                 options, ct);
        await WaitFor.TrueAsync(async () =>
        {
            await harness.PumpAsync();
            return harness.David.Switch.HeldPaymentHashes.Contains(invoice.PaymentHash);
        }, TimeSpan.FromSeconds(10), "the payee to hold the first shard", ct);

        // Act: the second shard fails at Carol and its call answers at once, the first still held
        var failed = await harness.RunAsync(
            harness.Bob.PaymentService.PayRouteAsync(LndShard(invoice, ViaCarol(harness, s_secondShard)), options,
                                                     ct));
        Assert.False(firstTask.IsCompleted);

        // Act: a shard that would exceed the total with the one in flight is refused (LND: value exceeds amount)
        var over = await Assert.ThrowsAsync<ArgumentException>(
            () => PayRouteAsync(harness, LndShard(invoice, ViaCarol(harness, LightningMoney.MilliSatoshis(40_000_000)))));

        // Act: the replacement completes the set
        var replaced = await harness.RunAsync(
            harness.Bob.PaymentService.PayRouteAsync(LndShard(invoice, ViaCarol(harness, s_secondShard)), options,
                                                     ct));
        var first = await harness.RunAsync(firstTask);

        // Assert: each call answered its own shard, the failed one attributed to Carol
        Assert.Equal(PaymentPartState.Failed, Assert.Single(failed.Outcomes).Status);
        Assert.Equal(FailureCode.TemporaryChannelFailure, failed.Outcomes[0].FailureCode);
        Assert.Equal(0, failed.Outcomes[0].FailureSourceIndex);
        Assert.Equal(PaymentStatus.InFlight, failed.Payment.Status);
        Assert.Contains("over the payment's total", over.Message);
        Assert.Equal(PaymentPartState.Succeeded, Assert.Single(replaced.Outcomes).Status);
        Assert.Equal(PaymentPartState.Succeeded, Assert.Single(first.Outcomes).Status);
        Assert.Equal(PaymentStatus.Succeeded, first.Payment.Status);
        Assert.Equal(invoice.Preimage, first.Payment.Preimage);
        Assert.Equal(3, forwards);
        Assert.Equal(s_total.MilliSatoshi,
                     harness.David.Switch.Received.ToArray().Aggregate(0UL, (sum, r) => sum + r.AmountMsat));
        AssertNoPendingHtlcs(harness);
    }

    /// <summary>
    /// Starts Bob's two-shard payroute payment of a fresh basic_mpp invoice of David's (25,000,000 + 35,000,000 msat
    /// over Bob → Carol → David) with Carol failing the forwards numbered in <paramref name="failForwards"/> (default:
    /// the first), and returns once the payee holds the second shard and the first has failed back to Bob.
    /// </summary>
    /// <returns>The invoice, the running first call and Carol's forward count.</returns>
    private static async Task<(InvoiceModel Invoice, Task<PayRouteResult> PayTask, Func<int> Forwards)>
        StartHeldSetAsync(PaymentHarness harness, int[]? failForwards = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var failing = failForwards ?? [1];
        var invoice = await harness.David.CreateMppInvoiceAsync(s_total, []);
        var forwards = 0;
        harness.Carol.Switch.ForwardInterceptor = (_, _) => failing.Contains(Interlocked.Increment(ref forwards))
            ? FailureMessage.TemporaryChannelFailure()
            : null;
        var payTask = harness.Bob.PaymentService.PayRouteAsync(
            ByInvoice(invoice.Bolt11!, ViaCarol(harness, s_firstShard), ViaCarol(harness, s_secondShard)),
            new PayInvoiceOptions { Timeout = s_timeout }, ct);
        await WaitFor.TrueAsync(async () =>
        {
            await harness.PumpAsync();
            return harness.David.Switch.HeldPaymentHashes.Contains(invoice.PaymentHash)
                && harness.Bob.Channel(harness.BobCarol).Commitments!.Htlcs.Count == 1;
        }, TimeSpan.FromSeconds(10), "the payee to hold the second shard and the first to fail back", ct);
        return (invoice, payTask, () => Volatile.Read(ref forwards));
    }

    private static PayRouteRequest AttachByInvoice(string bolt11, params PayRouteRoute[] routes) =>
        new() { Bolt11 = bolt11, Routes = routes, Attach = PayRouteAttachMode.Required };

    /// <summary>One shard as LND's <c>SendToRouteV2</c> sends it (raw form with the mpp record).</summary>
    private static PayRouteRequest LndShard(InvoiceModel invoice, PayRouteRoute route) => new()
    {
        PaymentHash = invoice.PaymentHash,
        PaymentSecret = invoice.PaymentSecret,
        TotalAmount = s_total,
        Routes = [route],
        Attach = PayRouteAttachMode.IfInFlight,
        IndependentShards = true
    };
}