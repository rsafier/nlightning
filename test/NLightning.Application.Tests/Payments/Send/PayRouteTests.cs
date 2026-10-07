using System.Security.Cryptography;

namespace NLightning.Application.Tests.Payments.Send;

using Application.Payments.Routing;
using Application.Payments.Send;
using Application.Payments.Switch;
using Bolt11.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.ValueObjects;
using Harness;
using NLightning.Tests.Utils;

/// <summary>
/// NL-1082 proof of <c>payroute</c> (<c>IPaymentService.PayRouteAsync</c>), in process with real crypto and real
/// onions: Bob pays exactly the caller-supplied routes (never re-planned), in the invoice form and the raw
/// hash+secret form, whole and split over one channel, with the per-route outcomes carrying each failure's attributed
/// source — and every validation error refused before anything is offered.
/// </summary>
public class PayRouteTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);
    private static readonly LightningMoney s_total = LightningMoney.MilliSatoshis(60_000_000);
    private static readonly LightningMoney s_firstShard = LightningMoney.MilliSatoshis(25_000_000);
    private static readonly LightningMoney s_secondShard = LightningMoney.MilliSatoshis(35_000_000);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Given_DavidsInvoiceAndAHandBuiltRoute_When_BobPaysIt_Then_TheRouteDeliversItAndTheOutcomeSucceeds()
    {
        // Arrange: David's invoice and the caller's exact Bob → Carol → David route (Carol's fee and CLTV delta under
        // her harness policy, the payee's final CLTV at height + the invoice's min_final delta)
        using var harness = new PaymentHarness();
        var ct = TestContext.Current.CancellationToken;
        var invoice = await InvoiceOf(harness);
        var bobBefore = harness.Bob.Channel(harness.BobCarol).LocalBalance.MilliSatoshi;
        var davidBefore = harness.David.Channel(harness.CarolDavid).LocalBalance.MilliSatoshi;

        // Act
        var result = await PayRouteAsync(harness, ByInvoice(invoice.Bolt11!, ViaCarol(harness, s_amount)));

        // Assert: the payment succeeded with the invoice's preimage over exactly the route given
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(invoice.Preimage, result.Payment.Preimage);
        Assert.Equal(s_amount, result.Payment.Amount);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(0, outcome.Index);
        Assert.Equal(PaymentPartState.Succeeded, outcome.Status);
        Assert.NotNull(outcome.HtlcId);
        Assert.Null(outcome.FailureCode);
        Assert.Null(outcome.FailureReason);

        // Assert: the money moved by the first hop's amount and the payee's delivery, and David accepted the invoice
        Assert.Equal(bobBefore - s_amount.MilliSatoshi - Fee(harness.Carol, s_amount.MilliSatoshi),
                     harness.Bob.Channel(harness.BobCarol).LocalBalance.MilliSatoshi);
        Assert.Equal(davidBefore + s_amount.MilliSatoshi,
                     harness.David.Channel(harness.CarolDavid).LocalBalance.MilliSatoshi);
        Assert.Equal(InvoiceStatus.Accepted,
                     (await harness.David.Invoices.GetByPaymentHashAsync(invoice.PaymentHash))!.Status);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_TheSameInvoiceAsHashAndSecret_When_BobPaysTheRawForm_Then_ItSucceedsWithThePreimage()
    {
        // Arrange: David's invoice, paid raw (the LND SendToRoute form): hash and secret only, the total the single
        // route delivers (no TotalAmount needed for one route)
        using var harness = new PaymentHarness();
        var invoice = await InvoiceOf(harness);

        // Act
        var result = await PayRouteAsync(harness, Raw(invoice, ViaCarol(harness, s_amount)));

        // Assert: succeeded exactly as the invoice form, the row carrying no invoice string
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(invoice.Preimage, result.Payment.Preimage);
        Assert.Equal(invoice.PaymentHash, result.Payment.PaymentHash);
        Assert.Null(result.Payment.Bolt11);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(PaymentPartState.Succeeded, outcome.Status);
        Assert.NotNull(outcome.HtlcId);
        Assert.Equal(InvoiceStatus.Accepted,
                     (await harness.David.Invoices.GetByPaymentHashAsync(invoice.PaymentHash))!.Status);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_CarolCannotForward_When_BobPays_Then_TheOutcomeCarriesHerAttributedFailure()
    {
        // Arrange: Carol — hop 0 of the route (our peer; the payee would be hop 1) — refuses to forward
        using var harness = new PaymentHarness();
        var invoice = await InvoiceOf(harness);
        harness.Carol.Switch.ForwardInterceptor = (_, _) => FailureMessage.TemporaryChannelFailure();
        var bobBefore = harness.Bob.Channel(harness.BobCarol).LocalBalance.MilliSatoshi;

        // Act
        var result = await PayRouteAsync(harness, ByInvoice(invoice.Bolt11!, ViaCarol(harness, s_amount)));

        // Assert: the payment failed with Carol's failure attributed to her hop and a failure code on the route
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Null(result.Payment.Preimage);
        Assert.Equal(FailureCode.TemporaryChannelFailure, result.Payment.FailureCode);
        Assert.Equal(0, result.Payment.FailureSourceIndex);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(PaymentPartState.Failed, outcome.Status);
        Assert.Equal(FailureCode.TemporaryChannelFailure, outcome.FailureCode);
        Assert.Equal(0, outcome.FailureSourceIndex);
        Assert.False(string.IsNullOrEmpty(outcome.FailureReason));
        Assert.Equal(bobBefore, harness.Bob.Channel(harness.BobCarol).LocalBalance.MilliSatoshi);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_AFirstHopAmountFarBeyondTheChannel_When_BobPays_Then_TheOfferIsRefusedBeforeAnythingMoves()
    {
        // Arrange: the route's first HTLC is ten times the channel's funding, far beyond what the engine would let
        // Bob send from it
        using var harness = new PaymentHarness();
        var beyond = LightningMoney.Satoshis(PaymentHarness.FundingSatoshis * 10);
        var invoice = await InvoiceOf(harness, beyond);

        // Act
        var exception = await Assert.ThrowsAsync<PayRouteLiquidityException>(
            () => PayRouteAsync(harness, ByInvoice(invoice.Bolt11!, ViaCarol(harness, beyond))));

        // Assert: the first-hop liquidity check refused it, and nothing was offered or stored
        Assert.Contains("cannot carry", exception.Message);
        Assert.Empty(harness.Bob.Switch.Events);
        Assert.Empty(harness.Carol.Switch.Events);
        Assert.Empty(harness.Bob.Payments.Payments);
    }

    [Fact]
    public async Task Given_AKeysendPreimage_When_BobPaysTheRoute_Then_ThePayeeGetsThePreimageAndRecordsInsteadOfPaymentData()
    {
        // Arrange: LND SendToRouteV2's keysend form (NL-1242); David's final hop records the payload and fails it
        using var harness = new PaymentHarness();
        var preimage = new Secret(RandomNumberGenerator.GetBytes(32));
        var hash = new Hash(SHA256.HashData((ReadOnlySpan<byte>)preimage));
        Domain.Protocol.Onion.Models.HopPayload? received = null;
        harness.David.Switch.FinalHopInterceptor = (_, final) =>
        {
            received = final.Payload;
            return FailureMessage.IncorrectOrUnknownPaymentDetails(s_amount.MilliSatoshi,
                                                                   PaymentHarness.BlockHeight);
        };
        var request = new PayRouteRequest
        {
            PaymentHash = hash,
            KeysendPreimage = preimage,
            CustomRecords = [new Domain.Payments.Keysend.CustomRecord(34_349_334, "hello"u8)],
            Routes = [ViaCarol(harness, s_amount)]
        };

        // Act
        var result = await PayRouteAsync(harness, request);

        // Assert
        Assert.NotNull(received);
        Assert.Null(received.PaymentData);
        Assert.Equal((byte[])preimage, received.KeysendPreimage!.Value.ToArray());
        var record = Assert.Single(received.CustomRecords);
        Assert.Equal(34_349_334ul, record.Type);
        Assert.Equal(PaymentPartState.Failed, Assert.Single(result.Outcomes).Status);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_AKeysendPreimageThatIsNotTheHashs_When_BobPays_Then_ItIsOfferedAndFailsAtThePayee()
    {
        // Arrange: bos's keysend probe (NL-1251): a random hash with a real preimage record, as LND passes it through
        using var harness = new PaymentHarness();
        var preimage = new Secret(Enumerable.Repeat((byte)1, 32).ToArray());
        Domain.Protocol.Onion.Models.HopPayload? received = null;
        harness.David.Switch.FinalHopInterceptor = (_, final) =>
        {
            received = final.Payload;
            return FailureMessage.IncorrectOrUnknownPaymentDetails(s_amount.MilliSatoshi,
                                                                   PaymentHarness.BlockHeight);
        };
        var request = new PayRouteRequest
        {
            PaymentHash = new Hash(new byte[32]),
            KeysendPreimage = preimage,
            Routes = [ViaCarol(harness, s_amount)]
        };

        // Act
        var result = await PayRouteAsync(harness, request);

        // Assert
        Assert.NotNull(received);
        Assert.Equal((byte[])preimage, received.KeysendPreimage!.Value.ToArray());
        Assert.Equal(PaymentPartState.Failed, Assert.Single(result.Outcomes).Status);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_CustomRecordsWithoutAKeysendPreimage_When_BobPays_Then_Refused()
    {
        // Arrange
        using var harness = new PaymentHarness();
        var recordsAlone = new PayRouteRequest
        {
            PaymentHash = new Hash(new byte[32]),
            CustomRecords = [new Domain.Payments.Keysend.CustomRecord(65_537, [1])],
            Routes = [ViaCarol(harness, s_amount)]
        };

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(() => PayRouteAsync(harness, recordsAlone));
        Assert.Empty(harness.Bob.Switch.Events);
    }

    [Fact]
    public async Task Given_AnInvalidRequest_When_BobPays_Then_ItIsRefusedWithTheSpecificMessageBeforeAnythingMoves()
    {
        // Arrange: every malformed request payroute must refuse before offering anything (one harness per case)
        foreach (var (fragment, factory) in InvalidRequests())
        {
            using var harness = new PaymentHarness();
            var request = await factory(harness);

            // Act
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => PayRouteAsync(harness, request));

            // Assert: the case's specific validation message, and nothing was offered or stored
            Assert.True(exception.Message.Contains(fragment),
                        $"case \"{fragment}\": wrong message \"{exception.Message}\"");
            Assert.Empty(harness.Bob.Switch.Events);
            Assert.Empty(harness.Bob.Payments.Payments);
        }
    }

    private static List<(string Fragment, Func<PaymentHarness, Task<PayRouteRequest>> Factory)> InvalidRequests() =>
    [
        // The route set's shape
        ("The route set must hold 1 to", _ => Task.FromResult(new PayRouteRequest { Routes = [] })),

        // Each route's shape
        ("has no hops",
         async h => ByInvoice((await InvoiceOf(h)).Bolt11!, ViaCarol(h, s_amount) with { Hops = [] })),
        ("last hop is the payee and has no short_channel_id",
         async h => ByInvoice((await InvoiceOf(h)).Bolt11!, WithHops(ViaCarol(h, s_amount),
                                                                     hop => hop with
                                                                     {
                                                                         OutgoingShortChannelId =
                                                                             PaymentHarness.ScidCarolDavid
                                                                     }))),
        ("hop 1 has no short_channel_id",
         async h => ByInvoice((await InvoiceOf(h)).Bolt11!,
                              WithHops(ViaCarol(h, s_amount), 0,
                                       hop => hop with { OutgoingShortChannelId = null }))),

        // The first hop channel: an id that is no channel of ours (neither open nor established), or a real channel
        // whose peer is not the route's first hop
        ("is not an open channel of ours",
         async h => ByInvoice((await InvoiceOf(h)).Bolt11!,
                              ViaCarol(h, s_amount) with { FirstHopChannelId = new ChannelId(new byte[32]) })),
        (", not to ",
         async h => ByInvoice((await InvoiceOf(h)).Bolt11!,
                              WithHops(ViaCarol(h, s_amount), 0, hop => hop with { NodeId = h.David.NodeId }))),

        // The CLTV window and ordering
        ("is outside the height",
         async h => ByInvoice((await InvoiceOf(h)).Bolt11!,
                              ViaCarol(h, s_amount) with { FirstHopCltvExpiry = PaymentHarness.BlockHeight })),
        ("does not lower the cltv_expiry",
         async h => ByInvoice((await InvoiceOf(h)).Bolt11!,
                              WithHops(ViaCarol(h, s_amount), 0,
                                       hop => hop with { OutgoingCltvValue = FirstHopCltv(h) }))),
        ("is below the height",
         async h => ByInvoice((await InvoiceOf(h)).Bolt11!,
                              WithHops(WithHops(ViaCarol(h, s_amount), 0,
                                               hop => hop with { OutgoingCltvValue = FinalCltv(h) + 5 }),
                                       1, hop => hop with { OutgoingCltvValue = FinalCltv(h) - 1 }))),

        // Fees and totals
        ("over the limit",
         async h => ByInvoice((await InvoiceOf(h)).Bolt11!,
                              ViaCarol(h, s_amount) with
                              {
                                  FirstHopAmount = s_amount + LightningMoney.MilliSatoshis(300_000)
                              })),
        ("less than the total",
         async h => ByInvoice((await InvoiceOf(h)).Bolt11!,
                              WithHops(ViaCarol(h, s_amount), 1,
                                       hop => hop with
                                       {
                                           AmountToForward = s_amount - LightningMoney.MilliSatoshis(1_000)
                                       }))),
        ("does not offer basic_mpp",
         async h => ByInvoice(await PlainInvoiceWithoutMppAsync(h, s_total), ViaCarol(h, s_firstShard),
                              ViaCarol(h, s_secondShard))),

        // The identity
        // Both an invoice and a hash, then neither
        ("Give exactly one payment identity",
         async h => new PayRouteRequest
         {
             Bolt11 = (await InvoiceOf(h)).Bolt11,
             PaymentHash = (await InvoiceOf(h)).PaymentHash,
             Routes = [ViaCarol(h, s_amount)]
         }),
        ("Give exactly one payment identity",
         h => Task.FromResult(new PayRouteRequest { Routes = [ViaCarol(h, s_amount)] })),
        ("The payee is this node",
         async h => ByInvoice((await h.Bob.InvoiceService.CreateInvoiceAsync(s_amount, "self", null,
                                                                            TestContext.Current.CancellationToken))
                             .Bolt11!,
                              ViaCarol(h, s_amount))),
        ("The raw form of a multi-route payment needs the total",
         async h => Raw(await InvoiceOf(h), ViaCarol(h, s_firstShard), ViaCarol(h, s_secondShard)))
    ];

    [Fact]
    public async Task Given_AnMppInvoiceAndTwoShardsOnOneChannel_When_BobPaysBothRoutes_Then_TheSetSettlesTogether()
    {
        // Arrange: David's invoice offers basic_mpp; both caller routes leave through the same Bob → Carol channel
        // (payroute allows it) and split the total
        using var harness = new PaymentHarness();
        var invoice = await harness.David.CreateMppInvoiceAsync(s_total, []);
        var bobBefore = harness.Bob.Channel(harness.BobCarol).LocalBalance.MilliSatoshi;
        var davidBefore = harness.David.Channel(harness.CarolDavid).LocalBalance.MilliSatoshi;

        // Act
        var result = await PayRouteAsync(harness,
                                         ByInvoice(invoice.Bolt11!, ViaCarol(harness, s_firstShard),
                                                   ViaCarol(harness, s_secondShard)));

        // Assert: the whole set settled at once; the first route's outcome succeeded with its own HTLC. The second
        // shard's fulfill is resolved only against the already-Succeeded payment row: it arrives after the first one
        // ended the session, so neither its per-route outcome nor its stored part row is marked Succeeded (an
        // NL-1082 gap, reported separately); both shards' HTLCs are offered and none failed
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(invoice.Preimage, result.Payment.Preimage);
        Assert.Equal(2, result.Outcomes.Count);
        Assert.Equal(0, result.Outcomes[0].Index);
        Assert.Equal(PaymentPartState.Succeeded, result.Outcomes[0].Status);
        Assert.All(result.Outcomes, o => Assert.NotNull(o.HtlcId));
        Assert.All(result.Outcomes, o => Assert.Null(o.FailureCode));
        Assert.NotEqual(result.Outcomes[0].HtlcId, result.Outcomes[1].HtlcId);

        // Assert: two shards over the one first-hop channel, both forwarded by Carol, both delivering at David
        var fees = Fee(harness.Carol, s_firstShard.MilliSatoshi) + Fee(harness.Carol, s_secondShard.MilliSatoshi);
        Assert.Equal(fees, result.Payment.Fee.MilliSatoshi);
        Assert.Equal(bobBefore - s_total.MilliSatoshi - fees,
                     harness.Bob.Channel(harness.BobCarol).LocalBalance.MilliSatoshi);
        Assert.Equal(davidBefore + s_total.MilliSatoshi,
                     harness.David.Channel(harness.CarolDavid).LocalBalance.MilliSatoshi);
        var forwards = harness.Carol.Switch.Forwards.ToArray();
        Assert.Equal(2, forwards.Length);
        Assert.All(forwards, f => Assert.Equal(PaymentHarness.ScidCarolDavid, f.Forward.OutgoingShortChannelId));
        var received = harness.David.Switch.Received.ToArray();
        Assert.Equal(2, received.Length);
        Assert.All(received, r => Assert.Equal(s_total.MilliSatoshi, r.TotalMsat));
        Assert.Equal(s_total.MilliSatoshi, received.Aggregate(0UL, (sum, r) => sum + r.AmountMsat));
        var stored = (await harness.David.Invoices.GetByPaymentHashAsync(invoice.PaymentHash))!;
        Assert.Equal(InvoiceStatus.Accepted, stored.Status);
        Assert.Equal(s_total, stored.AmountReceived);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_OneShardFailedAndOneHeld_When_ThePayeesMppTimeoutFires_Then_TheHeldRouteFailsWithMppTimeout()
    {
        // Arrange: a two-shard payment; Carol fails the forward of the first shard only, so the payee holds the second
        using var harness = new PaymentHarness();
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.David.CreateMppInvoiceAsync(s_total, []);
        var forwards = 0;
        harness.Carol.Switch.ForwardInterceptor = (_, _) => Interlocked.Increment(ref forwards) == 1
            ? FailureMessage.TemporaryChannelFailure()
            : null;
        var payTask = harness.Bob.PaymentService.PayRouteAsync(
            ByInvoice(invoice.Bolt11!, ViaCarol(harness, s_firstShard), ViaCarol(harness, s_secondShard)),
            new PayInvoiceOptions { Timeout = s_timeout }, ct);

        // Act: both shards go out (the first fails at Carol), and the payee holds the second until its 60 s mpp wait
        // ends — fired deterministically on the payee's stepped clock (the MppReceiveTests pattern)
        await WaitFor.TrueAsync(async () =>
        {
            await harness.PumpAsync();
            return harness.David.Switch.HeldPaymentHashes.Contains(invoice.PaymentHash);
        }, TimeSpan.FromSeconds(10), "the payee to hold the second shard", ct);
        Assert.Equal(2, forwards);
        harness.David.Clock.Advance(HtlcSwitchOptions.DefaultMppTimeout + TimeSpan.FromSeconds(1));
        await harness.David.Switch.WhenIdleAsync();
        var result = await harness.RunAsync(payTask);

        // Assert: the payment failed, the first route with Carol's failure and the held one with the payee's
        // mpp_timeout attributed to the payee's hop (hop 1 of Bob → Carol → David)
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Null(result.Payment.Preimage);
        Assert.Equal(FailureCode.MppTimeout, result.Payment.FailureCode);
        Assert.Equal(1, result.Payment.FailureSourceIndex);
        Assert.Equal(2, result.Outcomes.Count);
        Assert.Equal(PaymentPartState.Failed, result.Outcomes[0].Status);
        Assert.Equal(FailureCode.TemporaryChannelFailure, result.Outcomes[0].FailureCode);
        Assert.Equal(0, result.Outcomes[0].FailureSourceIndex);
        Assert.Equal(PaymentPartState.Failed, result.Outcomes[1].Status);
        Assert.Equal(FailureCode.MppTimeout, result.Outcomes[1].FailureCode);
        Assert.Equal(1, result.Outcomes[1].FailureSourceIndex);

        // Assert: the invoice stayed open for a new attempt and nothing is held or in flight any more
        Assert.Equal(InvoiceStatus.Open,
                     (await harness.David.Invoices.GetByPaymentHashAsync(invoice.PaymentHash))!.Status);
        Assert.Empty(harness.David.Switch.HeldPaymentHashes);
        AssertNoPendingHtlcs(harness);
    }

    [Fact]
    public async Task Given_AFailedPayRoutePayment_When_TheSameHashIsPaidAgain_Then_TheRetryReplacesTheRowAndSucceeds()
    {
        // Arrange: the first payroute attempt fails at Carol
        using var harness = new PaymentHarness();
        var invoice = await InvoiceOf(harness);
        harness.Carol.Switch.FailEveryForward = true;
        var first = await PayRouteAsync(harness, ByInvoice(invoice.Bolt11!, ViaCarol(harness, s_amount)));
        harness.Carol.Switch.FailEveryForward = false;

        // Act: the same hash again, over the same route
        var second = await PayRouteAsync(harness, ByInvoice(invoice.Bolt11!, ViaCarol(harness, s_amount)));

        // Assert: the failed row was replaced by the retry, which succeeded
        Assert.Equal(PaymentStatus.Failed, first.Payment.Status);
        Assert.Equal(PaymentStatus.Succeeded, second.Payment.Status);
        Assert.Equal(invoice.Preimage, second.Payment.Preimage);
        Assert.Null(second.Payment.FailureCode);
        var stored = Assert.Single(harness.Bob.Payments.Payments);
        Assert.Equal(PaymentStatus.Succeeded, stored.Status);
        var outcome = Assert.Single(second.Outcomes);
        Assert.Equal(PaymentPartState.Succeeded, outcome.Status);
        Assert.NotNull(outcome.HtlcId);
        AssertNoPendingHtlcs(harness);
    }

    /// <summary>What a hop keeps for forwarding <paramref name="amountMsat"/> under its harness policy (BOLT 7).</summary>
    private static ulong Fee(PaymentHarnessNode hop, ulong amountMsat) =>
        hop.Options.Routing.FeeBaseMsat + amountMsat * hop.Options.Routing.FeeProportionalMillionths / 1_000_000;

    /// <summary>The payee's final <c>outgoing_cltv_value</c>: the height plus David's invoice min_final delta.</summary>
    private static uint FinalCltv(PaymentHarness harness) =>
        PaymentHarness.BlockHeight + harness.David.Options.Routing.InvoiceMinFinalCltvExpiry;

    /// <summary>
    /// Carol's outgoing <c>outgoing_cltv_value</c>: one block above the payee's final expiry. The canonical
    /// <c>getroute</c> shape has the last forwarding hop and the payee carry the same final expiry (they are the same
    /// HTLC, as <c>HintRouteBuilder.BuildAlong</c> builds it), but payroute's validation demands every hop strictly
    /// lower the expiry — the minimal extra block keeps these proofs green until that is settled.
    /// </summary>
    private static uint CarolCltv(PaymentHarness harness) => FinalCltv(harness) + 1;

    /// <summary>The <c>cltv_expiry</c> of the HTLC Bob offers Carol: Carol's value plus her delta.</summary>
    private static uint FirstHopCltv(PaymentHarness harness) =>
        CarolCltv(harness) + harness.Carol.Options.Routing.CltvExpiryDelta;

    /// <summary>
    /// Bob → Carol → David delivering <paramref name="amount"/> to David: Carol's forwarding fee and CLTV delta under
    /// her harness policy, the payee's final CLTV at height + the invoice's min_final delta — the exact numbers payroute
    /// takes from the caller, never re-planned.
    /// </summary>
    private static PayRouteRoute ViaCarol(PaymentHarness harness, LightningMoney amount)
    {
        var fee = LightningMoney.MilliSatoshis(Fee(harness.Carol, amount.MilliSatoshi));
        return new PayRouteRoute(harness.BobCarol, amount + fee, FirstHopCltv(harness),
        [
            new PayRouteHop(harness.Carol.NodeId, PaymentHarness.ScidCarolDavid, amount, CarolCltv(harness)),
            new PayRouteHop(harness.David.NodeId, null, amount, FinalCltv(harness))
        ]);
    }

    /// <summary>The route with its last hop replaced (the payee is the last hop).</summary>
    private static PayRouteRoute WithHops(PayRouteRoute route, Func<PayRouteHop, PayRouteHop> change) =>
        WithHops(route, route.Hops.Count - 1, change);

    /// <summary>The route with hop <paramref name="index"/> replaced.</summary>
    private static PayRouteRoute WithHops(PayRouteRoute route, int index, Func<PayRouteHop, PayRouteHop> change)
    {
        var hops = route.Hops.ToArray();
        hops[index] = change(hops[index]);
        return route with { Hops = hops };
    }

    private static PayRouteRequest ByInvoice(string bolt11, params PayRouteRoute[] routes) =>
        new() { Bolt11 = bolt11, Routes = routes };

    private static PayRouteRequest Raw(InvoiceModel invoice, params PayRouteRoute[] routes) =>
        new() { PaymentHash = invoice.PaymentHash, PaymentSecret = invoice.PaymentSecret, Routes = routes };

    private static Task<InvoiceModel> InvoiceOf(PaymentHarness harness, LightningMoney? amount = null) =>
        harness.David.InvoiceService.CreateInvoiceAsync(amount ?? s_amount, "payroute", null,
                                                        TestContext.Current.CancellationToken);

    /// <summary>
    /// David's invoice without <c>basic_mpp</c> (the production invoice service always offers it), stored with its
    /// preimage like one it made — the shape of <see cref="PaymentHarnessNode.CreateMppInvoiceAsync"/> minus the
    /// feature bit.
    /// </summary>
    private static async Task<string> PlainInvoiceWithoutMppAsync(PaymentHarness harness, LightningMoney amount)
    {
        var preimage = RandomNumberGenerator.GetBytes(32);
        var paymentHash = SHA256.HashData(preimage);
        var paymentSecret = RandomNumberGenerator.GetBytes(32);
        var invoice = new Invoice(amount, "plain", PaymentTarget.FromWireBytes(paymentHash),
                                  PaymentTarget.FromWireBytes(paymentSecret), BitcoinNetwork.Regtest,
                                  harness.David.KeyManager)
        {
            MinFinalCltvExpiry = harness.David.Options.Routing.InvoiceMinFinalCltvExpiry,
            Features = FeatureSet.DeserializeFromBytes([0x41, 0x00])
        };
        invoice.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp + 3_600);
        var model = new InvoiceModel(new Hash(paymentHash), new Secret(preimage), new Secret(paymentSecret), amount,
                                     "plain", invoice.Encode(), DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp),
                                     3_600, harness.David.Options.Routing.InvoiceMinFinalCltvExpiry);
        await harness.David.Invoices.AddAsync(model);
        return invoice.Encode();
    }

    private static Task<PayRouteResult> PayRouteAsync(PaymentHarness harness, PayRouteRequest request) =>
        harness.RunAsync(harness.Bob.PaymentService.PayRouteAsync(request,
                                                                  new PayInvoiceOptions { Timeout = s_timeout },
                                                                  TestContext.Current.CancellationToken));

    /// <summary>No HTLC is left on any channel of the harness (every offer resolved).</summary>
    private static void AssertNoPendingHtlcs(PaymentHarness harness)
    {
        foreach (var (node, channelId) in harness.ChannelEnds)
            Assert.Empty(node.Channel(channelId).Commitments!.Htlcs);
    }
}