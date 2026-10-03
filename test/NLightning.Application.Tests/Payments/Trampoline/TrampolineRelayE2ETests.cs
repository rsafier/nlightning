using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Trampoline;

using Application.Gossip.Interfaces;
using Application.Payments.Invoices;
using Application.Payments.Routing;
using Application.Payments.Send;
using Application.Payments.Switch;
using Application.Payments.Trampoline;
using Bolt11.Models;
using Channels.Harness;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.Trampoline;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.ValueObjects;
using Events;
using Harness;

/// <summary>
/// NL-875 TR5 phase 2: trampoline payments end to end on <see cref="TrampolineHarness"/> (A → T → X → C, real onions,
/// signatures, switches and SQLite persistence) with the production pieces together: A's payer
/// (<c>PayInvoiceOptions.TrampolineNode</c>, <c>Node:Payments:Trampoline</c>), T's relay engine
/// (<see cref="TrampolineRelayService"/>) and T's leg sender (the production <c>PaymentService</c> routing over the
/// graph through X), and C as the final trampoline node. Where a scenario needs one exact incoming set (restarts,
/// refusals), A's onions are built by hand (<see cref="TrampolineHarness.PlanRelayAsync"/>) and A reads the failure
/// with both layers' secrets.
/// </summary>
public class TrampolineRelayE2ETests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_000);

    // T's default trampoline policy (1000 msat + 1000 ppm, delta 576) for s_amount, which is also A's default budget
    private static readonly LightningMoney s_trampolineFee = LightningMoney.MilliSatoshis(1_000 + 50_000);

    // X's forwarding fee on s_amount: T's leg pays it out of the trampoline fee
    private static readonly LightningMoney s_xFee = ThreeNodeHarness.ForwardingFeeOf(TrampolineHarness.XRouting,
                                                                                      s_amount);

    #region Scenario 1: single part

    [Fact]
    public async Task Given_APaymentThroughT_When_TRelaysItOverTheGraph_Then_CSettlesAndEveryRowAgrees()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);

        // Act
        var result = await harness.PumpUntilAsync(PayAsync(harness.A, invoice.Bolt11!, Through(harness.T)));
        await WhenRelayIdleAsync(harness);

        // Assert: A paid T's policy (A's default budget) and nothing else (A–T is direct)
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(invoice.Preimage, result.Payment.Preimage);
        Assert.Equal(s_trampolineFee, result.Payment.Fee);
        Assert.Equal(harness.C.NodeId, result.Payment.PayeeNodeId);
        var hops = await TrampolineHarness.GetTrampolineHopsAsync(harness.A, invoice.PaymentHash);
        Assert.Contains(hops, h => h is { Attempt: 0, HopIndex: 0 } && h.NodeId == harness.T.NodeId);
        Assert.Contains(hops, h => h is { Attempt: 0, HopIndex: 1 } && h.NodeId == harness.C.NodeId);

        // Assert: C settled the invoice
        var stored = await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash);
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
        Assert.Equal(s_amount, stored.AmountReceived);

        // Assert: T's relay is Fulfilled with what it kept after paying X
        var (relay, parts) = (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Fulfilled, relay.Status);
        Assert.Equal(s_trampolineFee - s_xFee, relay.FeeEarned);
        Assert.Equal(invoice.Preimage, relay.Preimage);
        Assert.Single(parts);

        // Assert: T's leg is a relay payment, X's fee only, over one HTLC
        var leg = await TrampolineHarness.GetPaymentAsync(harness.T, invoice.PaymentHash);
        Assert.NotNull(leg);
        Assert.True(leg.IsTrampolineRelay);
        Assert.Equal(PaymentStatus.Succeeded, leg.Status);
        Assert.Equal(s_xFee, leg.Fee);
        Assert.Equal(1, CountAdds(harness, "T", "X", invoice.PaymentHash));

        // Assert: one TrampolineRelaySettled on T for the fee earned, and no payment event for the leg
        var events = await UnsealedEventsAsync(harness.T);
        var settled = Assert.Single(events, e => e.Kind == AccountingEventKind.TrampolineRelaySettled);
        Assert.Equal(AccountingEventKeys.TrampolineRelaySettled(invoice.PaymentHash), settled.EventKey);
        Assert.Equal((long)(s_trampolineFee - s_xFee).MilliSatoshi, settled.AmountMsat);
        Assert.DoesNotContain(events, e => e.Kind is AccountingEventKind.PaymentSucceeded
                                                    or AccountingEventKind.PaymentFailed);
        harness.AssertQuiescent();
    }

    #endregion

    #region Scenario 2: MPP on both legs

    [Fact]
    public async Task Given_AnAmountAboveEveryChannel_When_PaidThroughT_Then_BothLegsSplitAndCSettlesOnce()
    {
        // Arrange: 1,500,000 sat, more than any one channel can carry (1,200,000 sat on the funder's side, and the
        // graph's htlc_maximum_msat of 1,000,000 sat)
        await using var harness = await CreateAsync(configure: o =>
        {
            o.SecondAliceTrampolineChannel = true;
            o.SecondLegChannels = true;
        });
        var amount = LightningMoney.Satoshis(1_500_000);
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, amount);

        // Act
        var result = await harness.PumpUntilAsync(PayAsync(harness.A, invoice.Bolt11!, Through(harness.T)));
        await WhenRelayIdleAsync(harness);

        // Assert: A split over both A–T channels, with one trampoline onion
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.True(result.Parts >= 2, $"A sent {result.Parts} part(s)");
        Assert.Equal(LightningMoney.MilliSatoshis(1_000 + amount.MilliSatoshi / 1_000), result.Payment.Fee);
        Assert.Equal(result.Parts, CountAdds(harness, "A", "T", invoice.PaymentHash));

        // Assert: one relay with every part, and T's leg split too
        var (relay, parts) = (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Fulfilled, relay.Status);
        Assert.Equal(result.Parts, parts.Count);
        Assert.True(CountAdds(harness, "T", "X", invoice.PaymentHash) >= 2);
        Assert.True(CountAdds(harness, "X", "C", invoice.PaymentHash) >= 2);

        // Assert: C settled once, for the amount
        var stored = await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash);
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
        Assert.Equal(amount, stored.AmountReceived);
        Assert.Single(await UnsealedEventsAsync(harness.C), e => e.Kind == AccountingEventKind.InvoiceSettled);
        Assert.Single(await UnsealedEventsAsync(harness.T), e => e.Kind == AccountingEventKind.TrampolineRelaySettled);
        harness.AssertQuiescent();
    }

    #endregion

    #region Scenario 3: NODE|26 and the retry at T's policy

    [Fact]
    public async Task Given_TAsksMoreThanAsBudget_When_Paid_Then_ARetriesAtTsPolicyAndTheNewRelaySettles()
    {
        // Arrange: T asks 5000 msat + 2000 ppm and a delta of 600, above A's default budget (1000 + 1000 ppm, 576)
        await using var harness = await CreateAsync(o =>
        {
            o.FeeBaseMsat = 5_000;
            o.FeeProportionalMillionths = 2_000;
            o.CltvExpiryDelta = 600;
        });
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        var policyFee = LightningMoney.MilliSatoshis(5_000 + 100_000);

        // Act
        var result = await harness.PumpUntilAsync(PayAsync(harness.A, invoice.Bolt11!, Through(harness.T)));
        await WhenRelayIdleAsync(harness);

        // Assert: two attempts, the second at T's policy, which A cached
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(policyFee, result.Payment.Fee);
        var cached = Payer(harness.A).GetCachedTrampolinePolicy(harness.T.NodeId);
        Assert.Equal(new TrampolinePolicy(5_000, 2_000, 600), cached);
        var hops = await TrampolineHarness.GetTrampolineHopsAsync(harness.A, invoice.PaymentHash);
        Assert.Equal([0, 1], hops.Select(h => h.Attempt).Distinct().Order());

        // Assert: T's refused relay was removed and the new one settled with the second HTLC alone
        var (relay, parts) = (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Fulfilled, relay.Status);
        Assert.Equal(policyFee - s_xFee, relay.FeeEarned);
        var part = Assert.Single(parts);
        Assert.Equal(1UL, part.HtlcId);
        Assert.Equal(1, CountAdds(harness, "T", "X", invoice.PaymentHash));
        Assert.Single(await UnsealedEventsAsync(harness.T), e => e.Kind == AccountingEventKind.TrampolineRelaySettled);
        harness.AssertQuiescent();
    }

    #endregion

    #region Scenario 4: the recipient's error

    [Fact]
    public async Task Given_CRefusesThePayment_When_TheErrorComesBack_Then_AReadsItAtCsTrampolineIndex()
    {
        // Arrange: C canceled its invoice, so it answers incorrect_or_unknown_payment_details
        await using var harness = await CreateAsync();
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        Assert.True(await harness.C.Invoices.CancelInvoiceAsync(invoice.PaymentHash,
                                                                TestContext.Current.CancellationToken));

        // Act
        var result = await harness.PumpUntilAsync(PayAsync(harness.A, invoice.Bolt11!, Through(harness.T)));
        await WhenRelayIdleAsync(harness);

        // Assert: T re-wrapped C's error; A decrypted it at the trampoline layer, C's index (outer hop 0 + inner 1),
        // and did not retry a PERM failure of the payee
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Payment.FailureCode);
        Assert.Equal(1, result.Payment.FailureSourceIndex);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, CountAdds(harness, "X", "C", invoice.PaymentHash));

        // Assert: T's relay failed with C's code and kept nothing
        var (relay, _) = (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Failed, relay.Status);
        Assert.Null(relay.FeeEarned);
        var leg = await TrampolineHarness.GetPaymentAsync(harness.T, invoice.PaymentHash);
        Assert.Equal(PaymentStatus.Failed, leg!.Status);
        Assert.DoesNotContain(await UnsealedEventsAsync(harness.T),
                              e => e.Kind == AccountingEventKind.TrampolineRelaySettled);
        harness.AssertQuiescent();
    }

    #endregion

    #region Payment events (Cashu plan C0, NL-991)

    [Fact]
    public async Task Given_SubscribersOnEveryNode_When_APaysThroughT_Then_ASucceedsOnceCSettlesOnceAndTPublishesNothing()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        using var aEvents = SubscribeEvents(harness.A);
        using var tEvents = SubscribeEvents(harness.T);
        using var cEvents = SubscribeEvents(harness.C);

        // Act
        await harness.PumpUntilAsync(PayAsync(harness.A, invoice.Bolt11!, Through(harness.T)));
        await WhenRelayIdleAsync(harness);

        // Assert: A (the trampoline client) published its success with the trampoline fee, after its save
        var succeeded = Assert.IsType<PaymentSucceededEvent>(await PaymentEventHubTests.ReadOneAsync(aEvents));
        Assert.Equal(invoice.PaymentHash, succeeded.PaymentHash);
        Assert.Equal(invoice.Preimage, succeeded.Preimage);
        Assert.Equal(s_amount, succeeded.Amount);
        Assert.Equal(s_trampolineFee, succeeded.Fee);
        Assert.Equal(PaymentStatus.Succeeded,
                     (await TrampolineHarness.GetPaymentAsync(harness.A, invoice.PaymentHash))!.Status);

        // Assert: C (the trampoline target) published the settle after its save
        var settled = Assert.IsType<InvoiceSettledEvent>(await PaymentEventHubTests.ReadOneAsync(cEvents));
        Assert.Equal(invoice.PaymentHash, settled.PaymentHash);
        Assert.Equal(s_amount, settled.Amount);
        Assert.Equal(InvoiceStatus.Settled,
                     (await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash))!.Status);

        // Assert: once each, and nothing for T's relay leg, a Succeeded relay payment that is not T's own
        Assert.True((await TrampolineHarness.GetPaymentAsync(harness.T, invoice.PaymentHash))!.IsTrampolineRelay);
        await AssertNoOtherEventAsync(harness.A, aEvents, invoice.PaymentHash);
        await AssertNoOtherEventAsync(harness.C, cEvents, invoice.PaymentHash);
        await AssertNoOtherEventAsync(harness.T, tEvents, invoice.PaymentHash);
    }

    [Fact]
    public async Task Given_CRefusesThePayment_When_ARelayedPaymentFails_Then_AOnlyPublishesTheFailure()
    {
        // Arrange: C canceled its invoice
        await using var harness = await CreateAsync();
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        Assert.True(await harness.C.Invoices.CancelInvoiceAsync(invoice.PaymentHash,
                                                                TestContext.Current.CancellationToken));
        using var aEvents = SubscribeEvents(harness.A);
        using var tEvents = SubscribeEvents(harness.T);

        // Act
        var result = await harness.PumpUntilAsync(PayAsync(harness.A, invoice.Bolt11!, Through(harness.T)));
        await WhenRelayIdleAsync(harness);

        // Assert: A published one failure; T's failed relay leg published nothing
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        var failed = Assert.IsType<PaymentFailedEvent>(await PaymentEventHubTests.ReadOneAsync(aEvents));
        Assert.Equal(invoice.PaymentHash, failed.PaymentHash);
        Assert.Equal(PaymentStatus.Failed,
                     (await TrampolineHarness.GetPaymentAsync(harness.T, invoice.PaymentHash))!.Status);
        await AssertNoOtherEventAsync(harness.A, aEvents, invoice.PaymentHash);
        await AssertNoOtherEventAsync(harness.T, tEvents, invoice.PaymentHash);
    }

    [Fact]
    public async Task Given_TAsksMoreThanAsBudget_When_ARetriesAtTsPolicy_Then_ASucceedsOnceWithoutAFailureEvent()
    {
        // Arrange: T refuses A's first attempt (fee_insufficient at its policy); A retries at T's policy
        await using var harness = await CreateAsync(o =>
        {
            o.FeeBaseMsat = 5_000;
            o.FeeProportionalMillionths = 2_000;
            o.CltvExpiryDelta = 600;
        });
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        using var aEvents = SubscribeEvents(harness.A);
        using var tEvents = SubscribeEvents(harness.T);

        // Act
        var result = await harness.PumpUntilAsync(PayAsync(harness.A, invoice.Bolt11!, Through(harness.T)));
        await WhenRelayIdleAsync(harness);

        // Assert: the refused first attempt published no failure; the one success carries T's policy fee
        Assert.Equal(2, result.Attempts);
        var succeeded = Assert.IsType<PaymentSucceededEvent>(await PaymentEventHubTests.ReadOneAsync(aEvents));
        Assert.Equal(invoice.PaymentHash, succeeded.PaymentHash);
        Assert.Equal(LightningMoney.MilliSatoshis(5_000 + 100_000), succeeded.Fee);
        await AssertNoOtherEventAsync(harness.A, aEvents, invoice.PaymentHash);
        await AssertNoOtherEventAsync(harness.T, tEvents, invoice.PaymentHash);
    }

    private static IPaymentEventSubscription SubscribeEvents(SwitchNode node) =>
        node.Services.GetRequiredService<IPaymentEventSource>().Subscribe();

    /// <summary>Publishes a marker on <paramref name="node"/> and asserts it is the next event read.</summary>
    private static async Task AssertNoOtherEventAsync(SwitchNode node, IPaymentEventSubscription events, Hash hash)
    {
        node.Services.GetRequiredService<IPaymentEventPublisher>()
            .Publish(new PaymentFailedEvent(hash, "marker", DateTimeOffset.UnixEpoch));
        var next = Assert.IsType<PaymentFailedEvent>(await PaymentEventHubTests.ReadOneAsync(events));
        Assert.Equal("marker", next.Reason);
        Assert.False(events.Overflowed);
    }

    #endregion

    #region Scenario 5: T restarts while collecting

    [Fact]
    public async Task Given_TRestartsWithOneOfTwoPartsHeld_When_TheSecondArrives_Then_TheSetCompletesOnce()
    {
        // Arrange: A's first part is held by T's relay
        await using var harness = await CreateAsync(configure: o =>
        {
            o.SecondAliceTrampolineChannel = true;
            o.PaymentSenders = TrampolineHarnessNodes.T;
        });
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        var plan = await TrampolineHarness.PlanRelayAsync(harness.A, harness.T, harness.C, invoice, s_trampolineFee,
                                                          600);
        var first = LightningMoney.MilliSatoshis(20_000_000);
        await harness.SendTrampolinePartAsync(plan, TrampolineHarness.AliceTrampolineChannelId, [harness.T], first);
        await harness.PumpAsync();
        Assert.Equal(TrampolineRelayStatus.Collecting,
                     (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value.Relay.Status);

        // Act: T restarts (its startup resumes the relay), then the second part arrives on the other channel
        await harness.RestartAsync(harness.T);
        await harness.ReconnectAsync(harness.T);
        Assert.Contains(invoice.PaymentHash, Engine(harness.T).CollectingPaymentHashes);
        await harness.SendTrampolinePartAsync(plan, TrampolineHarness.AliceTrampoline2ChannelId, [harness.T],
                                              plan.Total - first);
        await harness.PumpAsync();
        await WhenRelayIdleAsync(harness);

        // Assert: one relay with both parts, one leg, both of A's HTLCs fulfilled
        var (relay, parts) = (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Fulfilled, relay.Status);
        Assert.Equal(2, parts.Count);
        Assert.Empty(Engine(harness.T).CollectingPaymentHashes);
        Assert.Equal(1, CountAdds(harness, "T", "X", invoice.PaymentHash));
        Assert.Equal(2, harness.A.PaymentHandler.Fulfilled.Count);
        Assert.All(harness.A.PaymentHandler.Fulfilled, f => Assert.Equal(invoice.Preimage, f.PaymentPreimage));
        Assert.Empty(harness.A.PaymentHandler.Failed);
        Assert.Equal(InvoiceStatus.Settled,
                     (await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash))!.Status);
        harness.AssertQuiescent();
    }

    #endregion

    #region Scenario 6: T restarts while sending

    [Fact]
    public async Task Given_TRestartsWithItsLegInFlight_When_CSettles_Then_EveryPartIsFulfilledAndOneLegWasSent()
    {
        // Arrange: C holds the leg's HTLC (its switch records the lock-in without acting on it)
        await using var harness = await CreateAsync();
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        harness.C.SwitchSuspended = true;
        var payment = PayAsync(harness.A, invoice.Bolt11!, Through(harness.T));
        await harness.PumpAsync();
        await PumpUntilAsync(harness, () => CountAdds(harness, "X", "C", invoice.PaymentHash) == 1);
        Assert.False(payment.IsCompleted);
        Assert.Equal(TrampolineRelayStatus.Sending,
                     (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value.Relay.Status);

        // Act: T restarts with its leg in flight, then C settles
        await harness.RestartAsync(harness.T);
        await harness.ReconnectAsync(harness.T);
        Assert.Contains(invoice.PaymentHash, Engine(harness.T).SendingPaymentHashes);
        harness.C.SwitchSuspended = false;
        await harness.C.ReplayPendingEventsAsync();
        var result = await harness.PumpUntilAsync(payment);
        await WhenRelayIdleAsync(harness);

        // Assert: the outcome reached the restarted relay, A's part was fulfilled, and T never sent a second leg
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        var (relay, _) = (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Fulfilled, relay.Status);
        Assert.Equal(s_trampolineFee - s_xFee, relay.FeeEarned);
        Assert.Equal(1, CountAdds(harness, "T", "X", invoice.PaymentHash));
        Assert.Equal(1, CountFulfills(harness, "T", "A"));
        Assert.Equal(PaymentStatus.Succeeded,
                     (await TrampolineHarness.GetPaymentAsync(harness.T, invoice.PaymentHash))!.Status);
        Assert.Single(await UnsealedEventsAsync(harness.T), e => e.Kind == AccountingEventKind.TrampolineRelaySettled);
        harness.AssertQuiescent();
    }

    #endregion

    #region Scenario 7: refusals

    [Fact]
    public async Task Given_ACltvMarginBelowTsDelta_When_TheSetCompletes_Then_NodeTwentySixWithTsPolicy()
    {
        // Arrange: 500 blocks between the incoming expiry and the trampoline payload's, T asks 576
        await using var harness = await CreateAsync(configure: o => o.PaymentSenders = TrampolineHarnessNodes.T);
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        var plan = await TrampolineHarness.PlanRelayAsync(harness.A, harness.T, harness.C, invoice, s_trampolineFee,
                                                          500);

        // Act
        var decrypted = await SendOnePartAndReadFailureAsync(harness, plan);

        // Assert
        Assert.Equal(FailureCode.TrampolineFeeOrExpiryInsufficient, decrypted.Code);
        Assert.True(decrypted.Message!.TryGetTrampolinePolicy(out var feeBase, out var ppm, out var delta));
        Assert.Equal((1_000u, 1_000u, (ushort)576), (feeBase, ppm, delta));
        await AssertRefusedBeforeTheLegAsync(harness, invoice, FailureCode.TrampolineFeeOrExpiryInsufficient);
    }

    [Fact]
    public async Task Given_NoRelayAllowedInFlight_When_TheSetCompletes_Then_TemporaryTrampolineFailure()
    {
        // Arrange
        await using var harness = await CreateAsync(o => o.MaxRelaysInFlight = 0,
                                                    o => o.PaymentSenders = TrampolineHarnessNodes.T);
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        var plan = await TrampolineHarness.PlanRelayAsync(harness.A, harness.T, harness.C, invoice, s_trampolineFee,
                                                          600);

        // Act
        var decrypted = await SendOnePartAndReadFailureAsync(harness, plan);

        // Assert
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, decrypted.Code);
        await AssertRefusedBeforeTheLegAsync(harness, invoice, FailureCode.TemporaryTrampolineFailure);
    }

    [Fact]
    public async Task Given_OnlyOneOfTwoParts_When_TsMppTimeoutPasses_Then_AReadsMppTimeoutFromT()
    {
        // Arrange: the first of two parts reaches T
        await using var harness = await CreateAsync(configure: o => o.PaymentSenders = TrampolineHarnessNodes.T);
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        var plan = await TrampolineHarness.PlanRelayAsync(harness.A, harness.T, harness.C, invoice, s_trampolineFee,
                                                          600);
        var onion = await harness.SendTrampolinePartAsync(plan, TrampolineHarness.AliceTrampolineChannelId,
                                                          [harness.T], LightningMoney.MilliSatoshis(20_000_000));
        await harness.PumpAsync();

        // Act: one second short of the timeout, then past it
        await AdvanceAsync(harness, HtlcSwitchOptions.DefaultMppTimeout - TimeSpan.FromSeconds(1));
        Assert.Empty(harness.A.PaymentHandler.Failed);
        await AdvanceAsync(harness, TimeSpan.FromSeconds(2));

        // Assert
        var failed = Assert.Single(harness.A.PaymentHandler.Failed);
        var decrypted = TrampolineHarness.DecryptTrampolineFailure(harness.A, onion, plan.Onion, failed);
        Assert.NotNull(decrypted);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.MppTimeout, decrypted.Code);
        await AssertRefusedBeforeTheLegAsync(harness, invoice, FailureCode.MppTimeout);
    }

    #endregion

    #region Scenario 8: next node unknown or unreachable

    [Fact]
    public async Task Given_ANextNodeTDoesNotKnow_When_Paid_Then_AReadsUnknownNextTrampolineAndStops()
    {
        // Arrange: an invoice of a node that is neither T's peer nor in T's graph
        await using var harness = await CreateAsync();
        var stranger = new HarnessKeyManager(0x5D);
        var bolt11 = CreateTrampolineInvoice(stranger, s_amount, out var paymentHash);

        // Act
        var result = await harness.PumpUntilAsync(PayAsync(harness.A, bolt11, Through(harness.T)));
        await WhenRelayIdleAsync(harness);

        // Assert: a PERM failure at the trampoline layer from T (index 0): no retry
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(FailureCode.UnknownNextTrampoline, result.Payment.FailureCode);
        Assert.Equal(0, result.Payment.FailureSourceIndex);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(0, CountAdds(harness, "T", "X", paymentHash));
        var (relay, _) = (await TrampolineHarness.GetRelayAsync(harness.T, paymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Failed, relay.Status);
        Assert.Equal((ushort)FailureCode.UnknownNextTrampoline, relay.FailureCode);
        harness.AssertQuiescent();
    }

    [Fact]
    public async Task Given_ANextNodeTCannotReachWithinTheBudget_When_Paid_Then_TemporaryFailureAndOneRetry()
    {
        // Arrange: X asks 5,000 sat to forward, more than any trampoline fee A offers
        await using var harness = await CreateAsync(configure: o => o.ConfigureNode = node =>
        {
            if (node.Name == "X")
                node.Options.Routing.FeeBaseMsat = 5_000_000;
        });
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);

        // Act
        var result = await harness.PumpUntilAsync(PayAsync(harness.A, invoice.Bolt11!, Through(harness.T)));
        await WhenRelayIdleAsync(harness);

        // Assert: temporary_trampoline_failure from T, retried once with a doubled budget, then given up
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, result.Payment.FailureCode);
        Assert.Equal(0, result.Payment.FailureSourceIndex);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(0, CountAdds(harness, "T", "X", invoice.PaymentHash));
        var (relay, _) = (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Failed, relay.Status);
        Assert.Equal((ushort)FailureCode.TemporaryTrampolineFailure, relay.FailureCode);
        harness.AssertQuiescent();
    }

    #endregion

    #region Scenario 9: Node:Payments:Trampoline=Auto

    [Fact]
    public async Task Given_TheAutoModeAndNoRouteOfAsOwn_When_Paid_Then_APicksItsTrampolinePeer()
    {
        // Arrange: A sees no graph, so it has no route of its own to C; its peer T advertises trampoline_routing
        TrampolineHarness? created = null;
        await using var harness = created = await CreateAsync(configure: o =>
        {
            o.GraphViewers = TrampolineHarnessNodes.T;
            var previous = o.ConfigureServices;
            o.ConfigureServices = (node, services) =>
            {
                previous?.Invoke(node, services);
                if (node.Name != "A")
                    return;

                services.Configure<PaymentSendOptions>(p => p.Trampoline = TrampolinePaymentMode.Auto);
                services.AddSingleton(_ => PeerManagerWith(created!.T));
            };
        });
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        Assert.Empty(TrampolineHarness.Decode(invoice).RouteHints);

        // Act: no trampoline node named
        var result = await harness.PumpUntilAsync(PayAsync(harness.A, invoice.Bolt11!, new PayInvoiceOptions()));
        await WhenRelayIdleAsync(harness);

        // Assert: paid through T's relay
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(s_trampolineFee, result.Payment.Fee);
        var hops = await TrampolineHarness.GetTrampolineHopsAsync(harness.A, invoice.PaymentHash);
        Assert.Contains(hops, h => h.HopIndex == 0 && h.NodeId == harness.T.NodeId);
        Assert.Equal(TrampolineRelayStatus.Fulfilled,
                     (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value.Relay.Status);
        harness.AssertQuiescent();
    }

    #endregion

    #region Scenario 10: blinded recipients

    [Fact]
    public async Task Given_ABlindedRecipientWithoutTrampoline_When_PaidThroughT_Then_TPaysItsBlindedPaths()
    {
        // Arrange: C does not advertise trampoline_routing; its blinded path is introduced by X
        await using var harness = await CreateAsync(configure: o => o.Trampoline = TrampolineHarnessNodes.T);
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        var path = Assert.Single(await BuildBlindedPathsAsync(harness, invoice));
        Assert.Equal(harness.X.NodeId, path.Path.FirstNodeId);
        var request = new PayBlindedRequest(invoice.PaymentHash, s_amount, [path]) { PayeeNodeId = harness.C.NodeId };

        // Act
        var result = await harness.PumpUntilAsync(Payer(harness.A).PayBlindedAsync(
                                                      request, Through(harness.T) with
                                                      {
                                                          Timeout = TimeSpan.FromMinutes(5)
                                                      }, TestContext.Current.CancellationToken));
        await WhenRelayIdleAsync(harness);

        // Assert: A paid T's fee plus the path's (its budget covers the blinded part); T got the recipient's paths
        // (TLV 22) instead of a next node, paid them through X and kept its own fee
        var pathFee = LightningMoney.MilliSatoshis(path.PayInfo.ComputeFeeMsat(s_amount.MilliSatoshi));
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(s_trampolineFee + pathFee, result.Payment.Fee);
        var (relay, _) = (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Fulfilled, relay.Status);
        Assert.Null(relay.NextNodeId);
        Assert.NotNull(relay.RecipientBlindedPaths);
        Assert.Null(relay.NextTrampolinePacket);
        Assert.Equal(s_trampolineFee, relay.FeeEarned);
        Assert.Equal(pathFee, (await TrampolineHarness.GetPaymentAsync(harness.T, invoice.PaymentHash))!.Fee);
        var forwarded = Assert.IsType<UpdateAddHtlcMessage>(Assert.Single(harness.C.Received,
                                                                          m => m is UpdateAddHtlcMessage));
        Assert.NotNull(forwarded.BlindedPathTlv);
        Assert.Equal(InvoiceStatus.Settled,
                     (await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash))!.Status);
        harness.AssertQuiescent();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Given_ABlindedRecipientWithTrampoline_When_PaidThroughT_Then_XRelaysAtThePathsPaymentRelay(
        int dummyHops)
    {
        // Arrange: C advertises trampoline_routing and its blinded path (BlindedPathBuilder: X names the X-C channel by
        // short_channel_id, X's price is the path's payment_relay, C's own dummy hops after it) is introduced by X,
        // which runs the relay engine (NL-895, scenario 10(b))
        await using var harness = await CreateBlindedTrampolineHarnessAsync();
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        var path = Assert.Single(await BuildBlindedPathsAsync(harness, invoice, dummyHops));
        Assert.Equal(harness.X.NodeId, path.Path.FirstNodeId);
        Assert.Equal(dummyHops + 2, path.Path.Hops.Count);

        // Act
        var result = await harness.PumpUntilAsync(Payer(harness.A).PayBlindedAsync(
                                                      BlindedTrampolineRequest(harness, invoice, path),
                                                      Through(harness.T) with { Timeout = TimeSpan.FromMinutes(5) },
                                                      TestContext.Current.CancellationToken));
        await WhenRelaysIdleAsync(harness);

        // Assert: A paid T's fee on what X must receive (the path's amount) plus the path's fee; its inner route was
        // T, then the path's hops as trampoline hops
        var pathFee = LightningMoney.MilliSatoshis(path.PayInfo.ComputeFeeMsat(s_amount.MilliSatoshi));
        var introAmount = s_amount + pathFee;
        var trampolineFee = LightningMoney.MilliSatoshis(1_000 + (introAmount.MilliSatoshi * 1_000 + 999_999)
                                                       / 1_000_000);
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(trampolineFee + pathFee, result.Payment.Fee);
        var hops = await TrampolineHarness.GetTrampolineHopsAsync(harness.A, invoice.PaymentHash);
        Assert.Contains(hops, h => h is { Attempt: 0, HopIndex: 0 } && h.NodeId == harness.T.NodeId);
        Assert.Contains(hops, h => h is { Attempt: 0, HopIndex: 1 } && h.NodeId == harness.X.NodeId);

        // Assert: T relayed to X (its own policy) over their direct channel
        var (tRelay, _) = (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Fulfilled, tRelay.Status);
        Assert.Equal(harness.X.NodeId, tRelay.NextNodeId);
        Assert.Equal(introAmount, tRelay.AmountOut);
        Assert.Equal(trampolineFee, tRelay.FeeEarned);

        // Assert: X resolved the X-C scid to C, forwarded what payment_relay leaves of the total (not its
        // Node:Trampoline policy, which asks more) with the next path key in C's outer payload, and kept that fee
        var (xRelay, xParts) = (await TrampolineHarness.GetRelayAsync(harness.X, invoice.PaymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Fulfilled, xRelay.Status);
        Assert.Equal(harness.C.NodeId, xRelay.NextNodeId);
        Assert.NotNull(xRelay.NextPathKey);
        Assert.Equal(introAmount, xRelay.IncomingTotal);
        var xPaymentRelay = new BlindedPaymentRelay(TrampolineHarness.XRouting.CltvExpiryDelta,
                                                    TrampolineHarness.XRouting.FeeProportionalMillionths,
                                                    TrampolineHarness.XRouting.FeeBaseMsat);
        Assert.True(xPaymentRelay.TryComputeAmountToForward(introAmount.MilliSatoshi, out var xAmountOut));
        Assert.Equal(LightningMoney.MilliSatoshis(xAmountOut), xRelay.AmountOut);
        Assert.Equal(introAmount - xRelay.AmountOut, xRelay.FeeEarned);
        Assert.Single(xParts);
        var xLeg = await TrampolineHarness.GetPaymentAsync(harness.X, invoice.PaymentHash);
        Assert.NotNull(xLeg);
        Assert.True(xLeg.IsTrampolineRelay);
        Assert.Equal(LightningMoney.Zero, xLeg.Fee);
        var toC = Assert.IsType<UpdateAddHtlcMessage>(Assert.Single(harness.C.Received,
                                                                    m => m is UpdateAddHtlcMessage));
        Assert.Null(toC.BlindedPathTlv);
        Assert.Equal(xRelay.AmountOut.MilliSatoshi, toC.Payload.Amount.MilliSatoshi);

        // Assert: C settled once
        var stored = await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash);
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
        Assert.True(stored.AmountReceived! >= s_amount);
        harness.AssertQuiescent();
    }

    [Fact]
    public async Task Given_ABlindedTrampolineHopBreakingItsConstraints_When_PaidThroughT_Then_AReadsXsOwnBlindingError()
    {
        // Arrange: C's path lives one block, so A's expiry at X is above X's max_cltv_expiry (payment_constraints)
        await using var harness = await CreateBlindedTrampolineHarnessAsync();
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        var path = Assert.Single(await BuildBlindedPathsAsync(harness, invoice, 0, pathLifetimeBlocks: 1));

        // Act
        var result = await harness.PumpUntilAsync(Payer(harness.A).PayBlindedAsync(
                                                      BlindedTrampolineRequest(harness, invoice, path),
                                                      Through(harness.T) with { Timeout = TimeSpan.FromMinutes(5) },
                                                      TestContext.Current.CancellationToken));
        await WhenRelaysIdleAsync(harness);

        // Assert: X, the introduction node, answers with its own invalid_onion_blinding, which T relays at the
        // trampoline layer and A reads from X's inner index; A stops (not a T policy error), C never sees an HTLC
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(FailureCode.InvalidOnionBlinding, result.Payment.FailureCode);
        Assert.Equal(1, result.Payment.FailureSourceIndex);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, CountAdds(harness, "T", "X", invoice.PaymentHash));
        Assert.Equal(0, CountAdds(harness, "X", "C", invoice.PaymentHash));
        Assert.Null(await TrampolineHarness.GetRelayAsync(harness.X, invoice.PaymentHash));
        var (tRelay, _) = (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Failed, tRelay.Status);
        Assert.Equal(InvoiceStatus.Open,
                     (await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash))!.Status);
        harness.AssertQuiescent();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Scenario 10(b)'s harness: T, X and C advertise <c>trampoline_routing</c>; A, T and X run the payment service;
    /// T and X run the relay engine.
    /// </summary>
    private static Task<TrampolineHarness> CreateBlindedTrampolineHarnessAsync() =>
        CreateAsync(configure: o =>
        {
            o.Trampoline = TrampolineHarnessNodes.T | TrampolineHarnessNodes.X | TrampolineHarnessNodes.C;
            o.PaymentSenders = TrampolineHarnessNodes.A | TrampolineHarnessNodes.T | TrampolineHarnessNodes.X;
            var previous = o.ConfigureServices;
            o.ConfigureServices = (node, services) =>
            {
                previous?.Invoke(node, services);
                if (node.Name == "X")
                    services.AddTrampolineRelayServices();
            };
        });

    /// <summary>A's request for C's invoice over <paramref name="path"/>, C's features carrying bit 57 (as a BOLT 12
    /// invoice's would).</summary>
    private static PayBlindedRequest BlindedTrampolineRequest(TrampolineHarness harness, InvoiceModel invoice,
                                                              BlindedPaymentPath path)
    {
        var features = new FeatureSet();
        features.SetFeature(Feature.BasicMpp, false);
        features.SetFeature(Feature.OptionTrampolineRouting, false);
        return new PayBlindedRequest(invoice.PaymentHash, s_amount, [path])
        {
            PayeeNodeId = harness.C.NodeId,
            RecipientFeatures = features
        };
    }

    /// <summary>Waits for T's and X's relay timers and leg reports, then pumps what they sent.</summary>
    private static async Task WhenRelaysIdleAsync(TrampolineHarness harness)
    {
        await Engine(harness.T).WhenIdleAsync();
        await Engine(harness.X).WhenIdleAsync();
        await harness.PumpAsync();
    }

    /// <summary>
    /// C's blinded payment paths for <paramref name="invoice"/> (production <see cref="BlindedPathBuilder"/>, no dummy
    /// hop unless <paramref name="dummyHops"/>), introduced by X over their private channel; C first gets X's signed
    /// <c>channel_update</c> of it (the gossip the harness leaves out).
    /// </summary>
    private static async Task<IReadOnlyList<BlindedPaymentPath>> BuildBlindedPathsAsync(
        TrampolineHarness harness, InvoiceModel invoice, int dummyHops = 0,
        uint pathLifetimeBlocks = BlindedPathBuilder.DefaultPathLifetimeBlocks)
    {
        Assert.True(harness.X.Services.GetRequiredService<IChannelUpdateService>()
                           .TryGetLocalChannelUpdate(TrampolineHarness.XCarolChannelId, out var update));
        Assert.True(harness.C.Services.GetRequiredService<IChannelUpdateService>()
                           .HandleRemoteChannelUpdate(harness.X.NodeId, update!));
        var builder = harness.C.Services.GetRequiredService<BlindedPathBuilder>();
        return await builder.BuildAsync(new BlindedPathRequest(invoice.Preimage, invoice.Amount,
                                                               invoice.MinFinalCltvExpiry,
                                                               TrampolineHarness.BlockHeight, pathLifetimeBlocks,
                                                               IncludePrivateChannels: true, DummyHops: dummyHops),
                                        TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The harness with A and T running the payment service (both see the graph unless <paramref name="configure"/>
    /// says otherwise), and T's relay engine (<c>AddTrampolineRelayServices</c>, as <c>AddApplicationServices</c>
    /// registers it) with <paramref name="relay"/> as its <c>Node:Trampoline</c> options.
    /// </summary>
    private static Task<TrampolineHarness> CreateAsync(Action<TrampolineOptions>? relay = null,
                                                       Action<TrampolineHarnessOptions>? configure = null)
    {
        var options = new TrampolineHarnessOptions
        {
            PaymentSenders = TrampolineHarnessNodes.A | TrampolineHarnessNodes.T,
            ConfigureServices = (node, services) =>
            {
                if (node.Name != "T")
                    return;

                services.AddTrampolineRelayServices();
                if (relay is not null)
                    services.Configure(relay);
            }
        };
        configure?.Invoke(options);
        return TrampolineHarness.CreateAsync(options);
    }

    private static PayInvoiceOptions Through(SwitchNode trampoline) => new() { TrampolineNode = trampoline.NodeId };

    private static Task<PayInvoiceResult> PayAsync(SwitchNode payer, string bolt11, PayInvoiceOptions options) =>
        payer.Services.GetRequiredService<IPaymentService>()
             .PayInvoiceAsync(bolt11, null, options with { Timeout = TimeSpan.FromMinutes(5) },
                              TestContext.Current.CancellationToken);

    private static PaymentService Payer(SwitchNode node) => node.Services.GetRequiredService<PaymentService>();

    private static TrampolineRelayService Engine(SwitchNode node) =>
        node.Services.GetRequiredService<TrampolineRelayService>();

    /// <summary>Waits for T's relay timers and leg reports, then pumps what they sent.</summary>
    private static async Task WhenRelayIdleAsync(TrampolineHarness harness)
    {
        await Engine(harness.T).WhenIdleAsync();
        await harness.PumpAsync();
    }

    /// <summary>Moves the shared clock, then lets T's relay timers run and pumps what they sent.</summary>
    private static async Task AdvanceAsync(TrampolineHarness harness, TimeSpan by)
    {
        await harness.AdvanceAsync(by);
        await WhenRelayIdleAsync(harness);
    }

    /// <summary>Pumps until <paramref name="condition"/> holds (the leg starts on the thread pool).</summary>
    private static async Task PumpUntilAsync(TrampolineHarness harness, Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("The condition did not hold while the harness pumped");

            await Task.Delay(10, TestContext.Current.CancellationToken);
            await harness.PumpAsync();
        }
    }

    /// <summary>Sends the whole of <paramref name="plan"/> in one part and reads A's failure with both layers.</summary>
    private static async Task<TrampolineDecryptedFailure> SendOnePartAndReadFailureAsync(TrampolineHarness harness,
        TrampolinePaymentPlan plan)
    {
        var onion = await harness.SendTrampolinePartAsync(plan, TrampolineHarness.AliceTrampolineChannelId,
                                                          [harness.T], plan.Total);
        await harness.PumpAsync();
        await WhenRelayIdleAsync(harness);

        var failed = Assert.Single(harness.A.PaymentHandler.Failed);
        var decrypted = TrampolineHarness.DecryptTrampolineFailure(harness.A, onion, plan.Onion, failed);
        Assert.NotNull(decrypted);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        return decrypted;
    }

    /// <summary>T refused the set before any leg: C never saw an HTLC, the relay is Failed, nothing is left.</summary>
    private static async Task AssertRefusedBeforeTheLegAsync(TrampolineHarness harness, InvoiceModel invoice,
                                                             FailureCode code)
    {
        Assert.Equal(0, CountAdds(harness, "T", "X", invoice.PaymentHash));
        Assert.DoesNotContain(harness.C.Received, m => m is UpdateAddHtlcMessage);
        var (relay, _) = (await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash))!.Value;
        Assert.Equal(TrampolineRelayStatus.Failed, relay.Status);
        Assert.Equal((ushort)code, relay.FailureCode);
        Assert.Null(await TrampolineHarness.GetPaymentAsync(harness.T, invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Open,
                     (await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash))!.Status);
        harness.AssertQuiescent();
    }

    /// <summary>The <c>update_add_htlc</c>s of <paramref name="paymentHash"/> delivered from one node to another.
    /// </summary>
    private static int CountAdds(TrampolineHarness harness, string from, string to, Hash paymentHash)
    {
        lock (harness.Sent)
            return harness.Sent.Count(s => s.From == from && s.To == to
                                        && s.Message is UpdateAddHtlcMessage add
                                        && add.Payload.PaymentHash.Span.SequenceEqual((byte[])paymentHash));
    }

    /// <summary>The <c>update_fulfill_htlc</c>s delivered from one node to another.</summary>
    private static int CountFulfills(TrampolineHarness harness, string from, string to)
    {
        lock (harness.Sent)
            return harness.Sent.Count(s => s.From == from && s.To == to && s.Message is UpdateFulfillHtlcMessage);
    }

    private static Task<IReadOnlyList<AccountingEventModel>> UnsealedEventsAsync(SwitchNode node) =>
        node.InScopeAsync(u => u.AccountingEventDbRepository.GetUnsealedAsync(1_000,
                                                                              TestContext.Current.CancellationToken));

    /// <summary>A BOLT 11 invoice of <paramref name="payee"/> with <c>basic_mpp</c> and <c>trampoline_routing</c>.
    /// </summary>
    private static string CreateTrampolineInvoice(HarnessKeyManager payee, LightningMoney amount, out Hash paymentHash)
    {
        var preimage = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(preimage);
        paymentHash = new Hash(hash);
        var features = FeatureSet.DeserializeFromBytes([0x41, 0x00]);
        features.SetFeature(Feature.BasicMpp, false);
        features.SetFeature(Feature.OptionTrampolineRouting, false);
        var invoice = new Invoice(amount, "stranger", PaymentTarget.FromWireBytes(hash),
                                  PaymentTarget.FromWireBytes(RandomNumberGenerator.GetBytes(32)),
                                  BitcoinNetwork.Regtest, payee)
        {
            MinFinalCltvExpiry = 18,
            Features = features
        };
        invoice.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp + 3_600);
        return invoice.Encode();
    }

    /// <summary>
    /// A peer manager for A that knows one connected peer, <paramref name="trampoline"/>, with its <c>init</c>
    /// features (trampoline_routing among them), and answers every ping (A's commit scheduler asks before signing).
    /// </summary>
    private static IPeerManager PeerManagerWith(SwitchNode trampoline)
    {
        var peerService = new Mock<IPeerService>();
        peerService.SetupGet(p => p.Features).Returns(trampoline.Options.Features);
        peerService.SetupGet(p => p.LastMessageReceivedAt).Returns(() => DateTimeOffset.UtcNow);
        peerService.Setup(p => p.PingAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var peer = new PeerModel(trampoline.NodeId, "127.0.0.1", 9735, "IPv4");
        peer.SetPeerService(peerService.Object);

        var peerManager = new Mock<IPeerManager>();
        peerManager.Setup(m => m.GetPeer(trampoline.NodeId)).Returns(peer);
        peerManager.Setup(m => m.ListPeers()).Returns([peer]);
        return peerManager.Object;
    }

    #endregion
}