using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Payments.Routing;
using Application.Payments.Switch;
using Channels.Harness;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Onchain.Resolvers.Remote;

/// <summary>
/// NL-316 and NL-322 at the HTLC switch, on <see cref="ThreeNodeHarness"/> (real onions and SQLite persistence): an
/// incoming HTLC whose channel is closing on chain (Failed or OnchainResolving) is accepted as our final hop with the
/// same <c>FinalHopProcessor</c> checks, and instead of a fulfill (which the channel can no longer carry) the preimage is
/// persisted on its record, where the BOLT 5 resolvers take it from, with the invoice settled in the same save. Nothing
/// is sent to the peer, nothing is forwarded from such a channel, and nothing on it is failed off chain.
/// </summary>
public class OnchainFinalHopSwitchTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(60_000_000);
    private static readonly LightningMoney s_firstPart = LightningMoney.MilliSatoshis(25_000_000);
    private static readonly LightningMoney s_secondPart = LightningMoney.MilliSatoshis(35_000_000);

    private readonly SteppedTimeProvider _clock = new();
    private readonly Mock<IBlockchainMonitor> _carolMonitor = new();

    public OnchainFinalHopSwitchTests()
    {
        _carolMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(ThreeNodeHarness.BlockHeight);
    }

    [Theory]
    [InlineData(ChannelState.OnchainResolving)]
    [InlineData(ChannelState.Failed)]
    public async Task Given_HtlcForOurInvoiceNotFulfilledBeforeTheClose_When_TheSwitchDecides_Then_AcceptedWithThePreimageOnItsRecordAndTheInvoiceSettled(
        ChannelState closedState)
    {
        // Arrange: Carol's switch has not acted on the locked-in HTLC when her channel with Bob goes on chain
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        harness.Carol.SwitchSuspended = true;
        await PayPartAsync(harness, invoice, s_amount, s_amount);
        await harness.PumpAsync();
        harness.Carol.SwitchSuspended = false;
        var htlc = CarolIncoming(harness).Single();
        harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).UpdateState(closedState);
        var sentBefore = harness.Sent.Count;

        // Act: the resolver hands it to the switch (twice: every round while the invoice is Open)
        await harness.Carol.Switch.HandleAsync(new IncomingHtlcLockedIn(ThreeNodeHarness.BobCarolChannelId, htlc),
                                               CancellationToken.None);
        await harness.Carol.Switch.HandleAsync(new IncomingHtlcLockedIn(ThreeNodeHarness.BobCarolChannelId, htlc),
                                               CancellationToken.None);
        await harness.PumpAsync();

        // Assert: the preimage on the record (memory and database), the invoice settled for the HTLC, nothing sent
        var committed = CarolIncoming(harness).Single();
        Assert.Equal(invoice.Preimage, committed.KnownPreimage);
        Assert.Null(committed.Removal);
        Assert.Equal(invoice.Preimage, (await StoredIncomingAsync(harness, committed.Id))!.KnownPreimage);
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(s_amount, stored.AmountReceived);
        Assert.Equal(sentBefore, harness.Sent.Count);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
    }

    [Fact]
    public async Task Given_TwoPartsOfASetOnAChannelGoneOnChain_When_TheSwitchDecides_Then_BothCommittedWithOneSettle()
    {
        // Arrange (NL-322): both parts are locked in, not acted on, when the channel goes on chain
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        harness.Carol.SwitchSuspended = true;
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();
        harness.Carol.SwitchSuspended = false;
        harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).UpdateState(ChannelState.OnchainResolving);
        var sentBefore = harness.Sent.Count;

        // Act
        foreach (var htlc in CarolIncoming(harness))
            await harness.Carol.Switch.HandleAsync(new IncomingHtlcLockedIn(ThreeNodeHarness.BobCarolChannelId, htlc),
                                                   CancellationToken.None);
        await harness.PumpAsync();

        // Assert: every part carries the preimage, the invoice settled for the sum, nothing sent
        Assert.All(CarolIncoming(harness), h => Assert.Equal(invoice.Preimage, h.KnownPreimage));
        foreach (var htlc in CarolIncoming(harness))
            Assert.Equal(invoice.Preimage, (await StoredIncomingAsync(harness, htlc.Id))!.KnownPreimage);
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(s_amount, stored.AmountReceived);
        Assert.Equal(sentBefore, harness.Sent.Count);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        Assert.Equal(0, _clock.PendingTimers);
    }

    [Fact]
    public async Task Given_APartHeldWhenItsChannelGoesOnChain_When_TheSetTimesOut_Then_NotFailedOffChainNorCommitted()
    {
        // Arrange: one part of two, held while the channel was open; then the channel goes on chain
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);
        harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).UpdateState(ChannelState.OnchainResolving);
        var sentBefore = harness.Sent.Count;

        // Act: mpp_timeout, then the resolver asks again
        _clock.Advance(TimeSpan.FromSeconds(60));
        await CarolSwitch(harness).WhenIdleAsync();
        await harness.PumpAsync();
        var htlc = CarolIncoming(harness).Single();
        await harness.Carol.Switch.HandleAsync(new IncomingHtlcLockedIn(ThreeNodeHarness.BobCarolChannelId, htlc),
                                               CancellationToken.None);

        // Assert: nothing sent (it times out on chain), nothing committed, the invoice still Open, held again
        Assert.Equal(sentBefore, harness.Sent.Count);
        Assert.Null(CarolIncoming(harness).Single().KnownPreimage);
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);
    }

    [Fact]
    public async Task Given_AMarkedPartOfAnOpenInvoiceOnAChannelGoneOnChain_When_TheSetTimesOut_Then_TheMarkIsTakenBack()
    {
        // Arrange (NL-323): a held part carries the preimage (we stopped between the marks and the settle, or the set
        // became incomplete after the marks), the invoice is still Open, and the part's channel goes on chain
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();
        var part = CarolIncoming(harness).Single();
        await MarkAsync(harness, part.Id, invoice.Preimage);
        harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).UpdateState(ChannelState.OnchainResolving);
        var sentBefore = harness.Sent.Count;

        // Act: mpp_timeout
        _clock.Advance(TimeSpan.FromSeconds(60));
        await CarolSwitch(harness).WhenIdleAsync();

        // Assert: left to time out on chain without its preimage (memory and database), so no resolver claims it
        Assert.Null(CarolIncoming(harness).Single().KnownPreimage);
        Assert.Null((await StoredIncomingAsync(harness, part.Id))!.KnownPreimage);
        Assert.Equal(sentBefore, harness.Sent.Count);
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
    }

    [Fact]
    public async Task Given_AMarkedPartOfAnOpenInvoice_When_TheSetTimesOut_Then_FailedWithoutItsMark()
    {
        // Arrange (NL-323): the same leftover mark on an open channel
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();
        var part = CarolIncoming(harness).Single();
        await MarkAsync(harness, part.Id, invoice.Preimage);

        // Act
        _clock.Advance(TimeSpan.FromSeconds(60));
        await CarolSwitch(harness).WhenIdleAsync();

        // Assert: failed back, and its record (which the engine keeps through the removal) has no preimage
        var failed = harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!
                            .GetHtlc(HtlcDirection.Incoming, part.Id)!;
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal?.Kind);
        Assert.Null(failed.KnownPreimage);
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
    }

    [Fact]
    public async Task Given_TheSettleOfACompleteSetFails_When_TheSwitchReceivesTheLastPart_Then_TheMarksAreTakenBackAndTheTimerRetries()
    {
        // Arrange: both parts locked in; the settle's save fails once, after the other part was marked
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        harness.Carol.SwitchSuspended = true;
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();
        harness.Carol.SwitchSuspended = false;
        var parts = CarolIncoming(harness).OrderBy(h => h.Id).ToList();
        await harness.Carol.Switch.HandleAsync(new IncomingHtlcLockedIn(ThreeNodeHarness.BobCarolChannelId, parts[0]),
                                               CancellationToken.None);
        var failures = 0;
        harness.Carol.AfterInvoiceRead = _ =>
        {
            if (failures == 0 && CarolIncoming(harness).Any(h => h.KnownPreimage is not null))
            {
                failures++;
                throw new InvalidOperationException("simulated settle failure");
            }

            return Task.CompletedTask;
        };

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Carol.Switch.HandleAsync(
                                                                new IncomingHtlcLockedIn(
                                                                    ThreeNodeHarness.BobCarolChannelId, parts[1]),
                                                                CancellationToken.None));

        // Assert: nothing settled, no part keeps a mark (memory and database), the set waits with its timer
        Assert.Equal(1, failures);
        Assert.All(CarolIncoming(harness), h => Assert.Null(h.KnownPreimage));
        foreach (var htlc in parts)
            Assert.Null((await StoredIncomingAsync(harness, htlc.Id))!.KnownPreimage);
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);
        Assert.Equal(1, _clock.PendingTimers);

        // The timer finds the set complete and fulfills it
        _clock.Advance(TimeSpan.FromSeconds(60));
        await CarolSwitch(harness).WhenIdleAsync();
        await harness.PumpAsync();
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(s_amount, stored.AmountReceived);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        Assert.Empty(CarolIncoming(harness));
    }

    [Fact]
    public async Task Given_AnHtlcWithAnotherSecretOnAChannelGoneOnChain_When_TheSwitchDecides_Then_NotAcceptedNorFailed()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        harness.Carol.SwitchSuspended = true;
        await PayPartAsync(harness, invoice, s_amount, s_amount, new Secret(Enumerable.Repeat((byte)7, 32).ToArray()));
        await harness.PumpAsync();
        harness.Carol.SwitchSuspended = false;
        harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).UpdateState(ChannelState.OnchainResolving);
        var sentBefore = harness.Sent.Count;

        // Act
        await harness.Carol.Switch.HandleAsync(
            new IncomingHtlcLockedIn(ThreeNodeHarness.BobCarolChannelId, CarolIncoming(harness).Single()),
            CancellationToken.None);

        // Assert: no preimage for it (so no claim), no failure (the channel cannot carry one), the invoice Open
        Assert.Null(CarolIncoming(harness).Single().KnownPreimage);
        Assert.Equal(sentBefore, harness.Sent.Count);
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
    }

    [Fact]
    public async Task Given_AForwardOnAChannelGoneOnChain_When_TheSwitchDecides_Then_NeitherForwardedNorFailedNorCommitted()
    {
        // Arrange (B5-LCL-RO-02): Bob is the intermediate hop; his channel with Alice goes on chain before his switch
        // acts on the HTLC, whose hash is Carol's invoice
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        harness.Bob.SwitchSuspended = true;
        await PayPartAsync(harness, invoice, s_amount, s_amount);
        await harness.PumpAsync();
        harness.Bob.SwitchSuspended = false;
        var incoming = Assert.Single(harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!.Htlcs.Values,
                                     h => h.Direction == HtlcDirection.Incoming);
        harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).UpdateState(ChannelState.OnchainResolving);
        var sentBefore = harness.Sent.Count;

        // Act
        await harness.Bob.Switch.HandleAsync(new IncomingHtlcLockedIn(ThreeNodeHarness.AliceBobChannelId, incoming),
                                             CancellationToken.None);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(sentBefore, harness.Sent.Count);
        Assert.Empty(harness.Bob.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs);
        Assert.Null(await harness.Bob.InScopeAsync(u => u.ForwardCircuitDbRepository.GetByIncomingAsync(
                                                       ThreeNodeHarness.AliceBobChannelId, incoming.Id)));
        Assert.Null(harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!
                           .GetHtlc(HtlcDirection.Incoming, incoming.Id)!.KnownPreimage);
    }

    private Task<ThreeNodeHarness> CreateHarnessAsync() =>
        ThreeNodeHarness.CreateAsync(h => h.Carol.ConfigureServices = services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
            services.Replace(ServiceDescriptor.Singleton(_carolMonitor.Object));
        });

    private static Task<InvoiceModel> CreateInvoiceAsync(ThreeNodeHarness harness) =>
        harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "on chain", null, TestContext.Current.CancellationToken);

    private static HtlcSwitch CarolSwitch(ThreeNodeHarness harness) =>
        harness.Carol.Services.GetRequiredService<HtlcSwitch>();

    private static List<HtlcRecord> CarolIncoming(ThreeNodeHarness harness) =>
        harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values
               .Where(h => h.Direction == HtlcDirection.Incoming)
               .ToList();

    private static Task<HtlcRecord?> StoredIncomingAsync(ThreeNodeHarness harness, ulong htlcId)
    {
        var channel = harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId);
        return harness.Carol.InScopeAsync(async u => (await u.ChannelStateDbRepository.LoadAsync(
                                                          channel.ChannelId, channel.Commitments!.Params))!
                                                    .Commitments.GetHtlc(HtlcDirection.Incoming, htlcId));
    }

    /// <summary>
    /// Persists <paramref name="preimage"/> on an incoming HTLC's record as the switch's mark does (a leftover mark:
    /// a stop between the marks and the settle).
    /// </summary>
    private static async Task MarkAsync(ThreeNodeHarness harness, ulong htlcId, Secret preimage)
    {
        var channel = harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId);
        var next = RemoteFinalHopClaimTests.WithKnownPreimage(channel.Commitments!, htlcId, preimage);
        var record = next.GetHtlc(HtlcDirection.Incoming, htlcId)!;
        await harness.Carol.InScopeAsync(async u =>
        {
            await u.ChannelStateDbRepository.ApplyAsync(next, new ChannelTransition([record], [], [], false, false,
                                                                                    false, false));
            await u.SaveChangesAsync();
            return 0;
        });
        channel.UpdateCommitments(next);
    }

    private static Task<InvoiceModel> GetInvoiceAsync(ThreeNodeHarness harness, InvoiceModel invoice) =>
        harness.Carol.InScopeAsync(async u => (await u.InvoiceDbRepository.GetByPaymentHashAsync(invoice.PaymentHash))!);

    /// <summary>
    /// Alice → Bob → Carol, one HTLC carrying <paramref name="part"/> to Carol, whose payload promises
    /// <paramref name="total"/> (<c>payment_data.total_msat</c>).
    /// </summary>
    private static async Task PayPartAsync(ThreeNodeHarness harness, InvoiceModel invoice, LightningMoney part,
                                           LightningMoney total, Secret? paymentSecret = null)
    {
        var route = harness.RouteToCarol(part, invoice.PaymentHash, paymentSecret ?? invoice.PaymentSecret);
        var serializer = harness.Alice.Services.GetRequiredService<IHopPayloadSerializer>();
        var hops = new List<OnionHop>();
        foreach (var hop in route.Hops)
        {
            var payload = hop.IsFinal
                              ? new HopPayload([
                                    new AmtToForwardTlv(hop.AmountToForward),
                                    new OutgoingCltvValueTlv(hop.OutgoingCltvValue),
                                    new PaymentDataTlv(route.PaymentSecret, total)
                                ])
                              : PaymentOnionFactory.CreatePayload(hop, route);
            using var stream = new MemoryStream();
            await serializer.SerializeAsync(payload, stream);
            hops.Add(new OnionHop(hop.NodeId, stream.ToArray()));
        }

        var sessionKey = PaymentOnionFactory.CreateSessionKey();
        var constructed = harness.Alice.Services.GetRequiredService<ISphinxService>()
                                 .ConstructWithSharedSecrets(hops, new PrivKey(sessionKey), route.PaymentHash);
        await harness.Alice.Operations.OfferHtlcAsync(ThreeNodeHarness.AliceBobChannelId, route.FirstHopAmount,
                                                      route.PaymentHash, route.FirstHopCltvExpiry, constructed.Packet,
                                                      null, HtlcOrigin.Local(route.PaymentHash));
    }
}