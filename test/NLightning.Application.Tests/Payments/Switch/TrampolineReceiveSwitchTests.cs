using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Payments.Onion;
using Application.Payments.Routing;
using Application.Payments.Switch;
using Channels.Harness;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-875 TR2-T2: Carol receives as the final trampoline node through the production <see cref="HtlcSwitch"/> on
/// <see cref="ThreeNodeHarness"/> (real onions, signatures and SQLite persistence). Alice reaches Carol over Bob with an
/// outer onion whose final payload carries a trampoline onion for Carol: the HTLC set counts against the inner total
/// (D-TR4), and every failure Carol sends is created with the trampoline and the outer secrets, so Alice reads it at
/// the trampoline layer.
/// </summary>
public class TrampolineReceiveSwitchTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(60_000_000);
    private static readonly LightningMoney s_firstPart = LightningMoney.MilliSatoshis(25_000_000);
    private static readonly LightningMoney s_secondPart = LightningMoney.MilliSatoshis(35_000_000);

    private readonly SteppedTimeProvider _clock = new();
    private readonly Mock<IBlockchainMonitor> _carolMonitor = new();

    public TrampolineReceiveSwitchTests()
    {
        _carolMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(ThreeNodeHarness.BlockHeight);
    }

    [Fact]
    public async Task Given_ASinglePartTrampolinePayment_When_ItReachesCarol_Then_FulfilledAndInvoiceSettled()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var trampoline = await BuildTrampolineAsync(harness, invoice, s_amount, s_amount);

        // Act
        await PayPartAsync(harness, invoice, trampoline, s_amount, s_amount);
        await harness.PumpAsync();

        // Assert
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(invoice.Preimage, fulfilled.PaymentPreimage);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(s_amount, stored.AmountReceived);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_TwoPartsSharingOneTrampolineOnion_When_TheSecondArrives_Then_TheInnerTotalCompletesTheSet()
    {
        // Arrange: the payer splits its leg to Carol in two HTLCs carrying the same trampoline onion
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var trampoline = await BuildTrampolineAsync(harness, invoice, s_amount, s_amount);

        // Act: the first part is held
        await PayPartAsync(harness, invoice, trampoline, s_firstPart, s_amount);
        await harness.PumpAsync();

        // Assert
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);

        // Act: the second part completes it
        await PayPartAsync(harness, invoice, trampoline, s_secondPart, s_amount);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(s_amount, stored.AmountReceived);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_TwoTrampolineLegsWithTheirOwnOuterTotals_When_Both_Arrive_Then_TheInnerTotalCompletesTheSet()
    {
        // Arrange: two legs (as through two trampoline nodes), each a single-part outer payment of its own amount
        // whose trampoline payload forwards that amount towards the invoice's total (D-TR4, Eclair's model)
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var firstLeg = await BuildTrampolineAsync(harness, invoice, s_firstPart, s_amount);
        var secondLeg = await BuildTrampolineAsync(harness, invoice, s_secondPart, s_amount);

        // Act
        await PayPartAsync(harness, invoice, firstLeg, s_firstPart, s_firstPart);
        await harness.PumpAsync();
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        await PayPartAsync(harness, invoice, secondLeg, s_secondPart, s_secondPart);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        Assert.Equal(InvoiceStatus.Settled, (await GetInvoiceAsync(harness, invoice)).Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_AWrongInnerPaymentSecret_When_CarolRefuses_Then_ThePayerReadsItAtTheTrampolineLayer()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var trampoline = await BuildTrampolineAsync(harness, invoice, s_amount, s_amount,
                                                    paymentSecret: Enumerable.Repeat((byte)0x99, 32).ToArray());

        // Act
        var onion = await PayPartAsync(harness, invoice, trampoline, s_amount, s_amount);
        await harness.PumpAsync();

        // Assert: Carol's trampoline layer (index 0), invisible to a plain outer decryption
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, decrypted.Code);
        Assert.Null(harness.Alice.Services.GetRequiredService<IFailureOnionService>()
                           .DecryptErrorPacket(onion.SharedSecrets, failed.Removal.Reason.Span));
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_AnInnerExpiryAboveTheOuterOne_When_CarolProcesses_Then_TheFailureIsDoubleWrapped()
    {
        // Arrange: the trampoline payload asks for one block more than the outer onion carries (TR-R-12)
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var trampoline = await BuildTrampolineAsync(harness, invoice,
                                                    (harness.Carol.NodeId,
                                                     new HopPayload(new AmtToForwardTlv(s_amount),
                                                                    new OutgoingCltvValueTlv(FinalCltv + 1),
                                                                    new PaymentDataTlv(invoice.PaymentSecret,
                                                                                       s_amount))));

        // Act
        var onion = await PayPartAsync(harness, invoice, trampoline, s_amount, s_amount);
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(FailureCode.FinalIncorrectCltvExpiry, decrypted.Code);
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_AnIncompleteTrampolineSet_When_60SecondsPass_Then_MppTimeoutAtTheTrampolineLayer()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var trampoline = await BuildTrampolineAsync(harness, invoice, s_amount, s_amount);
        var onion = await PayPartAsync(harness, invoice, trampoline, s_firstPart, s_amount);
        await harness.PumpAsync();

        // Act
        await AdvanceAsync(harness, TimeSpan.FromSeconds(60));

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(FailureCode.MppTimeout, decrypted.Code);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_ATimeoutRefusedWhileBobIsAway_When_TheLinkComesBack_Then_TheReplayFailsItDoubleWrapped()
    {
        // Arrange: the mpp_timeout failure is refused (link down), so the replayed lock-in sends it: the trampoline
        // secret comes from peeling the stored onion again
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var trampoline = await BuildTrampolineAsync(harness, invoice, s_amount, s_amount);
        var onion = await PayPartAsync(harness, invoice, trampoline, s_firstPart, s_amount);
        await harness.PumpAsync();
        harness.Disconnect(harness.Bob, harness.Carol);
        await AdvanceAsync(harness, TimeSpan.FromSeconds(60));
        Assert.Empty(harness.Alice.PaymentHandler.Failed);

        // Act
        await harness.ReconnectLinkAsync(harness.Bob, harness.Carol);
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(FailureCode.MppTimeout, decrypted.Code);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_APartHeldWhenCarolRestarts_When_NothingMoreArrives_Then_TheRestoredPartTimesOutDoubleWrapped()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var trampoline = await BuildTrampolineAsync(harness, invoice, s_amount, s_amount);
        var onion = await PayPartAsync(harness, invoice, trampoline, s_firstPart, s_amount);
        await harness.PumpAsync();

        // Act: the restart rebuilds the set from the persisted HTLC (only the outer secret is stored)
        await harness.RestartAsync(harness.Carol);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);
        await AdvanceAsync(harness, TimeSpan.FromSeconds(60));

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(FailureCode.MppTimeout, decrypted.Code);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_TrampolineOff_When_ATrampolineOnionReachesCarol_Then_InvalidOnionPayloadAtTheOuterLayer()
    {
        // Arrange: Carol without the feature
        await using var harness = await ThreeNodeHarness.CreateAsync(h => h.Carol.ConfigureServices = services =>
            services.Replace(ServiceDescriptor.Singleton(_carolMonitor.Object)));
        var invoice = await CreateInvoiceAsync(harness);
        var trampoline = await BuildTrampolineAsync(harness, invoice, s_amount, s_amount);

        // Act
        var onion = await PayPartAsync(harness, invoice, trampoline, s_amount, s_amount);
        await harness.PumpAsync();

        // Assert: TLV 20 refused as an unknown even type by Carol, the outer final hop (index 1)
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = harness.Alice.Services.GetRequiredService<IFailureOnionService>()
                               .DecryptErrorPacket(onion.SharedSecrets, failed.Removal.Reason.Span);
        Assert.NotNull(decrypted);
        Assert.Equal(1, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.InvalidOnionPayload, decrypted.Code);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_ARelayPartAndARelayEngine_When_LockedIn_Then_TheEngineGetsItAndNothingIsFailed()
    {
        // Arrange: Carol is asked to relay to the next trampoline node (Alice's id stands in for it)
        var ingress = new RecordingRelayIngress();
        await using var harness = await CreateHarnessAsync(ingress);
        var invoice = await CreateInvoiceAsync(harness);
        var trampoline = await BuildRelayTrampolineAsync(harness, invoice);

        // Act
        await PayPartAsync(harness, invoice, trampoline, s_amount, s_amount);
        await harness.PumpAsync();

        // Assert: one part handed over with both secrets, the HTLC left to the engine
        var (lockedIn, relay) = Assert.Single(ingress.Parts);
        Assert.Equal(ThreeNodeHarness.BobCarolChannelId, lockedIn.ChannelId);
        Assert.Equal(harness.Alice.NodeId, relay.NextNodeId);
        Assert.Equal(trampoline.SharedSecrets[0], relay.TrampolineSharedSecret);
        Assert.Equal(s_amount, relay.IncomingTotal);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Contains(harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values,
                        h => h is { Direction: HtlcDirection.Incoming, Removal: null });
    }

    [Fact]
    public async Task Given_ARelayPartWithoutARelayEngine_When_LockedIn_Then_TemporaryTrampolineFailureDoubleWrapped()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var trampoline = await BuildRelayTrampolineAsync(harness, invoice);

        // Act
        var onion = await PayPartAsync(harness, invoice, trampoline, s_amount, s_amount);
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, decrypted.Code);
        AssertNoHtlcs(harness);
    }

    #region Helpers

    private Task<ThreeNodeHarness> CreateHarnessAsync(ITrampolineRelayIngress? ingress = null) =>
        ThreeNodeHarness.CreateAsync(h =>
        {
            h.Carol.Options.Features.OptionTrampolineRouting = FeatureSupport.Optional;
            h.Carol.Options.Features.AllowExperimentalFeatures = true;
            h.Carol.ConfigureServices = services =>
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
                services.Replace(ServiceDescriptor.Singleton(_carolMonitor.Object));
                if (ingress is not null)
                    services.AddSingleton(ingress);
            };
        });

    private static Task<InvoiceModel> CreateInvoiceAsync(ThreeNodeHarness harness) =>
        harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "trampoline", null,
                                                  TestContext.Current.CancellationToken);

    private static HtlcSwitch CarolSwitch(ThreeNodeHarness harness) =>
        harness.Carol.Services.GetRequiredService<HtlcSwitch>();

    private async Task AdvanceAsync(ThreeNodeHarness harness, TimeSpan by)
    {
        _clock.Advance(by);
        await CarolSwitch(harness).WhenIdleAsync();
        await harness.PumpAsync();
    }

    private static uint FinalCltv => ThreeNodeHarness.BlockHeight + 43;

    /// <summary>
    /// Alice's trampoline onion for Carol as the final trampoline node: <paramref name="amount"/> towards
    /// <paramref name="total"/> with the invoice's secret (or <paramref name="paymentSecret"/>).
    /// </summary>
    private static async Task<TrampolineOnion> BuildTrampolineAsync(ThreeNodeHarness harness, InvoiceModel invoice,
                                                                    LightningMoney amount, LightningMoney total,
                                                                    Secret? paymentSecret = null)
    {
        var payload = new HopPayload(new AmtToForwardTlv(amount), new OutgoingCltvValueTlv(FinalCltv),
                                     new PaymentDataTlv(paymentSecret ?? invoice.PaymentSecret, total));
        return await BuildTrampolineAsync(harness, invoice, (harness.Carol.NodeId, payload));
    }

    /// <summary>A trampoline onion in which Carol relays to Alice's node id (then a final hop for Alice).</summary>
    private static Task<TrampolineOnion> BuildRelayTrampolineAsync(ThreeNodeHarness harness, InvoiceModel invoice) =>
        BuildTrampolineAsync(harness, invoice,
                             (harness.Carol.NodeId,
                              new HopPayload(new AmtToForwardTlv(s_amount - LightningMoney.MilliSatoshis(1_000)),
                                             new OutgoingCltvValueTlv(FinalCltv - 1),
                                             new OutgoingNodeIdTlv(harness.Alice.NodeId))),
                             (harness.Alice.NodeId,
                              new HopPayload(new AmtToForwardTlv(s_amount - LightningMoney.MilliSatoshis(1_000)),
                                             new OutgoingCltvValueTlv(FinalCltv - 1),
                                             new PaymentDataTlv(invoice.PaymentSecret, s_amount))));

    private static async Task<TrampolineOnion> BuildTrampolineAsync(
        ThreeNodeHarness harness, InvoiceModel invoice, params (CompactPubKey NodeId, HopPayload Payload)[] hops)
    {
        var serializer = harness.Alice.Services.GetRequiredService<IHopPayloadSerializer>();
        var onionHops = new List<OnionHop>();
        foreach (var (nodeId, payload) in hops)
        {
            using var stream = new MemoryStream();
            await serializer.SerializeAsync(payload, stream);
            onionHops.Add(new OnionHop(nodeId, stream.ToArray()));
        }

        return harness.Alice.Services.GetRequiredService<ITrampolineOnionService>()
                      .Build(onionHops, RandomNumberGenerator.GetBytes(32), invoice.PaymentHash,
                             TrampolineOnionSizePolicy.Auto(650));
    }

    /// <summary>
    /// Alice → Bob → Carol, one HTLC of <paramref name="part"/>, whose outer final payload promises
    /// <paramref name="outerTotal"/> with a random outer secret and carries <paramref name="trampoline"/>.
    /// </summary>
    private static async Task<PaymentOnion> PayPartAsync(ThreeNodeHarness harness, InvoiceModel invoice,
                                                         TrampolineOnion trampoline, LightningMoney part,
                                                         LightningMoney outerTotal)
    {
        var route = harness.RouteToCarol(part, invoice.PaymentHash, invoice.PaymentSecret);
        var serializer = harness.Alice.Services.GetRequiredService<IHopPayloadSerializer>();
        var hops = new List<OnionHop>();
        foreach (var hop in route.Hops)
        {
            var payload = hop.IsFinal
                              ? new HopPayload(new AmtToForwardTlv(hop.AmountToForward),
                                               new OutgoingCltvValueTlv(hop.OutgoingCltvValue),
                                               new PaymentDataTlv(RandomNumberGenerator.GetBytes(32), outerTotal),
                                               new TrampolineOnionPacketTlv(trampoline.Packet))
                              : PaymentOnionFactory.CreatePayload(hop, route);
            using var stream = new MemoryStream();
            await serializer.SerializeAsync(payload, stream);
            hops.Add(new OnionHop(hop.NodeId, stream.ToArray()));
        }

        var constructed = harness.Alice.Services.GetRequiredService<ISphinxService>()
                                 .ConstructWithSharedSecrets(hops, new PrivKey(PaymentOnionFactory.CreateSessionKey()),
                                                             route.PaymentHash);
        var onion = new PaymentOnion(route, constructed.Packet, constructed.SharedSecrets);
        await harness.Alice.Operations.OfferHtlcAsync(ThreeNodeHarness.AliceBobChannelId, route.FirstHopAmount,
                                                      route.PaymentHash, route.FirstHopCltvExpiry, onion.Packet,
                                                      null, HtlcOrigin.Local(route.PaymentHash));
        return onion;
    }

    private static Task<InvoiceModel> GetInvoiceAsync(ThreeNodeHarness harness, InvoiceModel invoice) =>
        harness.Carol.InScopeAsync(async u => (await u.InvoiceDbRepository.GetByPaymentHashAsync(invoice.PaymentHash))!);

    private static TrampolineDecryptedFailure Decrypt(ThreeNodeHarness harness, PaymentOnion onion,
                                                      TrampolineOnion trampoline, OutgoingHtlcFailed failed)
    {
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var decrypted = harness.Alice.Services.GetRequiredService<ITrampolineFailureOnionService>()
                               .DecryptTrampolineErrorPacket(onion.SharedSecrets, trampoline.SharedSecrets,
                                                             failed.Removal.Reason.Span);
        Assert.NotNull(decrypted);
        return decrypted;
    }

    private static void AssertNoHtlcs(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
            foreach (var channel in node.Channels)
                Assert.Empty(channel.Commitments!.Htlcs);
    }

    private sealed class RecordingRelayIngress : ITrampolineRelayIngress
    {
        public List<(IncomingHtlcLockedIn LockedIn, IncomingOnionTrampolineRelay Relay)> Parts { get; } = [];

        public Task HandleNewPartAsync(IncomingHtlcLockedIn lockedIn, IncomingOnionTrampolineRelay onion,
                                       CancellationToken cancellationToken)
        {
            lock (Parts)
                Parts.Add((lockedIn, onion));
            return Task.CompletedTask;
        }
    }

    #endregion
}