namespace NLightning.Application.Tests.Payments.Trampoline;

using Application.Payments.Routing;
using Domain.Channels.Commitments;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Harness;

/// <summary>
/// NL-875 TR5 phase 1, the TR2 target end to end on <see cref="TrampolineHarness"/> (A → T → X → C, real onions,
/// signatures and SQLite persistence): A builds by hand a trampoline onion whose only hop is C (the final trampoline
/// node) and carries it in an outer onion A → T → X → C, in which T and X are plain forwarding hops. C counts every
/// part against the inner total (D-TR4), settles its BOLT 11 invoice, and creates every failure with the trampoline and
/// the outer secrets, so A reads it at the trampoline layer.
/// </summary>
public class TrampolineTargetE2ETests
{
    private static readonly LightningMoney s_total = LightningMoney.MilliSatoshis(60_000_000);
    private static readonly LightningMoney s_firstPart = LightningMoney.MilliSatoshis(25_000_000);
    private static readonly LightningMoney s_secondPart = LightningMoney.MilliSatoshis(35_000_000);

    [Fact]
    public async Task Given_ASinglePartTrampolinePaymentThroughTAndX_When_ItReachesC_Then_CSettlesAndAGetsThePreimage()
    {
        // Arrange
        await using var harness = await TrampolineHarness.CreateAsync();
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_total);
        var plan = await TrampolineHarness.PlanFinalTrampolineAsync(harness.A, harness.C, invoice);

        // Act
        await harness.SendTrampolinePartAsync(plan, TrampolineHarness.AliceTrampolineChannelId,
                                              [harness.T, harness.X, harness.C], s_total);
        await harness.PumpAsync();

        // Assert: C's invoice advertised bit 57 and is settled for the total; A has the preimage
        Assert.True(TrampolineHarness.Decode(invoice).Features!.IsFeatureSet(Feature.OptionTrampolineRouting, false));
        var fulfilled = Assert.Single(harness.A.PaymentHandler.Fulfilled);
        Assert.Equal(invoice.Preimage, fulfilled.PaymentPreimage);
        Assert.Empty(harness.A.PaymentHandler.Failed);
        var stored = await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash);
        Assert.NotNull(stored);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(s_total, stored.AmountReceived);
        // T forwarded the outer onion as a plain hop: no relay of its own
        Assert.Null(await TrampolineHarness.GetRelayAsync(harness.T, invoice.PaymentHash));
        harness.AssertQuiescent();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_TwoPartsOverTwoFirstHops_When_TheSecondArrives_Then_CSettlesOnceForTheInnerTotal(
        bool oneOuterSet)
    {
        // Arrange: two A–T channels; either one outer MPP set (one trampoline onion forwarding the total, one outer
        // secret, outer total = the total) or two outer payments of their own amounts, each with its own trampoline
        // onion forwarding its amount towards the inner total (as two trampoline legs would, D-TR4)
        await using var harness = await TrampolineHarness.CreateAsync(new TrampolineHarnessOptions
        {
            SecondAliceTrampolineChannel = true
        });
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_total);
        var plan = await TrampolineHarness.PlanFinalTrampolineAsync(harness.A, harness.C, invoice,
                                                                    oneOuterSet ? null : s_firstPart);
        var secondPlan = oneOuterSet
                             ? plan
                             : await TrampolineHarness.PlanFinalTrampolineAsync(harness.A, harness.C, invoice,
                                                                                s_secondPart);

        // Act: the first part is held by C
        await harness.SendTrampolinePartAsync(plan, TrampolineHarness.AliceTrampolineChannelId,
                                              [harness.T, harness.X, harness.C], s_firstPart,
                                              oneOuterSet ? s_total : s_firstPart);
        await harness.PumpAsync();

        // Assert
        Assert.Empty(harness.A.PaymentHandler.Fulfilled);
        Assert.Contains(invoice.PaymentHash, TrampolineHarness.SwitchOf(harness.C).HeldPaymentHashes);

        // Act: the second part, over the other first hop, completes the inner total
        await harness.SendTrampolinePartAsync(secondPlan, TrampolineHarness.AliceTrampoline2ChannelId,
                                              [harness.T, harness.X, harness.C], s_secondPart,
                                              oneOuterSet ? s_total : s_secondPart);
        await harness.PumpAsync();

        // Assert: both parts fulfilled with the preimage, the invoice settled once for the total
        Assert.Equal(2, harness.A.PaymentHandler.Fulfilled.Count);
        Assert.All(harness.A.PaymentHandler.Fulfilled, f => Assert.Equal(invoice.Preimage, f.PaymentPreimage));
        Assert.Contains(harness.A.PaymentHandler.Fulfilled,
                        f => f.ChannelId == TrampolineHarness.AliceTrampolineChannelId);
        Assert.Contains(harness.A.PaymentHandler.Fulfilled,
                        f => f.ChannelId == TrampolineHarness.AliceTrampoline2ChannelId);
        var stored = await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash);
        Assert.NotNull(stored);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(s_total, stored.AmountReceived);
        Assert.Empty(TrampolineHarness.SwitchOf(harness.C).HeldPaymentHashes);
        harness.AssertQuiescent();
    }

    [Fact]
    public async Task Given_AWrongInnerPaymentSecret_When_CRefuses_Then_ADecryptsItAtTheTrampolineLayer()
    {
        // Arrange
        await using var harness = await TrampolineHarness.CreateAsync();
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_total);
        var plan = await TrampolineHarness.PlanFinalTrampolineAsync(
                       harness.A, harness.C, invoice,
                       paymentSecret: new Secret(Enumerable.Repeat((byte)0x99, 32).ToArray()));

        // Act
        var onion = await harness.SendTrampolinePartAsync(plan, TrampolineHarness.AliceTrampolineChannelId,
                                                          [harness.T, harness.X, harness.C], s_total);
        await harness.PumpAsync();

        // Assert: C (trampoline hop 0) answers incorrect_or_unknown_payment_details inside the trampoline layer
        var decrypted = DecryptSingleFailure(harness, onion, plan);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, decrypted.Code);
        Assert.Equal(InvoiceStatus.Open,
                     (await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash))!.Status);
        harness.AssertQuiescent();
    }

    [Fact]
    public async Task Given_PartsBelowTheInnerTotal_When_TheMppTimeoutPasses_Then_ADecryptsMppTimeoutAtTheTrampolineLayer()
    {
        // Arrange: one part of 25,000 sat towards the 60,000 sat inner total
        await using var harness = await TrampolineHarness.CreateAsync();
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_total);
        var plan = await TrampolineHarness.PlanFinalTrampolineAsync(harness.A, harness.C, invoice);
        var onion = await harness.SendTrampolinePartAsync(plan, TrampolineHarness.AliceTrampolineChannelId,
                                                          [harness.T, harness.X, harness.C], s_firstPart);
        await harness.PumpAsync();
        Assert.Contains(invoice.PaymentHash, TrampolineHarness.SwitchOf(harness.C).HeldPaymentHashes);
        Assert.Empty(harness.A.PaymentHandler.Failed);

        // Act
        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));

        // Assert
        var decrypted = DecryptSingleFailure(harness, onion, plan);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.MppTimeout, decrypted.Code);
        Assert.Empty(TrampolineHarness.SwitchOf(harness.C).HeldPaymentHashes);
        Assert.Equal(InvoiceStatus.Open,
                     (await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash))!.Status);
        harness.AssertQuiescent();
    }

    [Fact]
    public async Task Given_TrampolineOffOnC_When_ATrampolineOnionReachesIt_Then_InvalidOnionPayloadAtTheOuterLayer()
    {
        // Arrange: only T advertises trampoline routing
        await using var harness = await TrampolineHarness.CreateAsync(new TrampolineHarnessOptions
        {
            Trampoline = TrampolineHarnessNodes.T
        });
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_total);
        Assert.False(TrampolineHarness.Decode(invoice).Features!.HasFeature(Feature.OptionTrampolineRouting));
        var plan = await TrampolineHarness.PlanFinalTrampolineAsync(harness.A, harness.C, invoice);

        // Act
        var onion = await harness.SendTrampolinePartAsync(plan, TrampolineHarness.AliceTrampolineChannelId,
                                                          [harness.T, harness.X, harness.C], s_total);
        await harness.PumpAsync();

        // Assert: TLV 20 is an unknown even type to C, the outer final hop (index 2), as before TR2
        var decrypted = DecryptSingleFailure(harness, onion, plan);
        Assert.Equal(TrampolineFailureLayer.Outer, decrypted.Layer);
        Assert.Equal(2, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.InvalidOnionPayload, decrypted.Code);
        Assert.Equal(InvoiceStatus.Open,
                     (await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash))!.Status);
        harness.AssertQuiescent();
    }

    [Fact]
    public async Task Given_CRestartsWithOnePartHeld_When_TheSecondPartArrives_Then_TheRestoredSetCompletesAndCSettles()
    {
        // Arrange: the first part held by C, then C restarts (only the outer secret is stored; the replay re-peels the
        // stored onion and rebuilds the set against the inner total)
        await using var harness = await TrampolineHarness.CreateAsync(new TrampolineHarnessOptions
        {
            SecondAliceTrampolineChannel = true
        });
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_total);
        var plan = await TrampolineHarness.PlanFinalTrampolineAsync(harness.A, harness.C, invoice);
        await harness.SendTrampolinePartAsync(plan, TrampolineHarness.AliceTrampolineChannelId,
                                              [harness.T, harness.X, harness.C], s_firstPart);
        await harness.PumpAsync();
        await harness.RestartAsync(harness.C);
        await harness.ReconnectAsync(harness.C);
        await harness.PumpAsync();
        Assert.Contains(invoice.PaymentHash, TrampolineHarness.SwitchOf(harness.C).HeldPaymentHashes);
        Assert.Empty(harness.A.PaymentHandler.Fulfilled);
        Assert.Empty(harness.A.PaymentHandler.Failed);

        // Act
        await harness.SendTrampolinePartAsync(plan, TrampolineHarness.AliceTrampoline2ChannelId,
                                              [harness.T, harness.X, harness.C], s_secondPart);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(2, harness.A.PaymentHandler.Fulfilled.Count);
        Assert.All(harness.A.PaymentHandler.Fulfilled, f => Assert.Equal(invoice.Preimage, f.PaymentPreimage));
        Assert.Empty(harness.A.PaymentHandler.Failed);
        var stored = await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash);
        Assert.NotNull(stored);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(s_total, stored.AmountReceived);
        harness.AssertQuiescent();
    }

    private static TrampolineDecryptedFailure DecryptSingleFailure(TrampolineHarness harness, PaymentOnion onion,
                                                                   TrampolinePaymentPlan plan)
    {
        var failed = Assert.Single(harness.A.PaymentHandler.Failed);
        Assert.Empty(harness.A.PaymentHandler.Fulfilled);
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var decrypted = TrampolineHarness.DecryptTrampolineFailure(harness.A, onion, plan.Onion, failed);
        Assert.NotNull(decrypted);
        return decrypted;
    }
}