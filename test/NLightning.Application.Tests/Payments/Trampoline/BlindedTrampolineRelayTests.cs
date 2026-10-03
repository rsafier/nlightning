using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Tests.Payments.Trampoline;

using Application.Payments.Routing;
using Application.Payments.Switch;
using Application.Payments.Trampoline;
using Channels.Harness;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Trampoline;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Switch;

/// <summary>
/// NL-895: Carol relays a blinded trampoline hop (a BOLT 12 recipient that supports trampoline made her a hop of its
/// blinded path) with the production relay engine on <see cref="ThreeNodeHarness"/> (real onions, route blinding and
/// SQLite). Alice is both the payer (over Bob) and the recipient whose blinded path names Carol's next hop by
/// <c>short_channel_id</c> (D-NL895-1) and fixes Carol's price in <c>payment_relay</c> (D-NL895-2), cheaper in fee and
/// delta than Carol's <c>Node:Trampoline</c> policy, so a NODE|26 would show. The leg is a fake that records what the
/// engine asks; every failure is read by Alice.
/// </summary>
public class BlindedTrampolineRelayTests
{
    private const uint IncomingCltvDelta = 700;

    // Carol's price as the recipient set it: 100 msat + 10 ppm, delta 50 (her Node:Trampoline asks 1000 + 1000 ppm,
    // delta 576; her plain forwarding delta is 40)
    private static readonly BlindedPaymentRelay s_carolRelay = new(50, 10, 100);

    // The payment's outer total and what payment_relay leaves of it: ceil((1,000,200 - 100) * 10^6 / 1,000,010)
    private static readonly LightningMoney s_total = LightningMoney.MilliSatoshis(1_000_200);
    private static readonly LightningMoney s_amountOut = LightningMoney.MilliSatoshis(1_000_090);

    private readonly SteppedTimeProvider _clock = new();
    private readonly Mock<IBlockchainMonitor> _carolMonitor = new();
    private readonly FakeLegSender _legSender = new();

    public BlindedTrampolineRelayTests()
    {
        _carolMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(ThreeNodeHarness.BlockHeight);
    }

    private static uint IncomingCltv => ThreeNodeHarness.BlockHeight + IncomingCltvDelta;
    private static uint FinalCltv => IncomingCltv - 200;

    #region Next node (D-NL895-1)

    [Fact]
    public async Task Given_ARecipientDataNamingOurChannelByItsScid_When_TheSetCompletes_Then_TheLegGoesToItsPeer()
    {
        // Arrange: Carol is the introduction node; her data names the Carol-Alice channel by its real scid
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);

        // Act
        await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: the leg pays Alice what payment_relay leaves, at the outer expiry minus its delta, with the whole
        // difference as its fee and Carol's plain forwarding delta kept
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(harness.Alice.NodeId, leg.NextNodeId);
        Assert.Equal(s_amountOut, leg.Amount);
        Assert.Equal(IncomingCltv - s_carolRelay.CltvExpiryDelta, leg.FinalCltvExpiry);
        Assert.Equal(s_total - s_amountOut, leg.MaxFee);
        Assert.Equal(IncomingCltv - 40, leg.MaxFirstHopCltvExpiry);
        Assert.True(leg.AllowMpp);
        Assert.Null(leg.RecipientBlindedPaths);

        // Assert: Alice (the recipient) peels the next trampoline packet with the next path key
        Assert.NotNull(leg.NextPathKey);
        Assert.NotNull(leg.NextTrampolinePacket);
        var peeled = harness.Alice.Services.GetRequiredService<ITrampolineOnionService>()
                            .Peel(leg.NextTrampolinePacket, payment.Hash, leg.NextPathKey);
        Assert.True(peeled.IsFinal);

        // Assert: the resolved node is stored for a restart
        var (relay, parts) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Sending, relay.Status);
        Assert.Equal(harness.Alice.NodeId, relay.NextNodeId);
        Assert.Equal((byte[])leg.NextPathKey.Value, relay.NextPathKey);
        Assert.Single(parts);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_ARecipientDataNamingOurChannelByAnAlias_When_TheSetCompletes_Then_TheLegGoesToItsPeer()
    {
        // Arrange: Bob-Carol requires option_scid_alias; the data names it by Carol's alias
        await using var harness = await CreateHarnessAsync(bobCarolScidAlias: FeatureSupport.Compulsory);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.BobCarolCarolAlias),
                                            introduction: true);

        // Act
        await PayPartAsync(harness, payment, s_total, bobCarolScid: ThreeNodeHarness.BobCarolCarolAlias);
        await harness.PumpAsync();

        // Assert
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(harness.Bob.NodeId, leg.NextNodeId);
        Assert.Equal(s_amountOut, leg.Amount);
    }

    [Fact]
    public async Task Given_AnUnknownScidAtTheIntroductionNode_When_ThePartArrives_Then_OurOwnInvalidOnionBlinding()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness, CarolData(new ShortChannelId(999, 9, 9)), introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: Carol's own error at the trampoline layer, nothing stored, no leg
        var decrypted = Decrypt(harness, onion, payment.Trampoline);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.InvalidOnionBlinding, decrypted.Code);
        Assert.Empty(_legSender.Started);
        Assert.Null(await harness.Carol.InScopeAsync(u => u.TrampolineRelayDbRepository.GetAsync(payment.Hash)));
    }

    [Fact]
    public async Task Given_AnUnknownScidPastTheIntroductionNode_When_ThePartArrives_Then_MalformedWithThePacketHash()
    {
        // Arrange: Bob introduces the path, Carol gets her path key in the outer payload
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness, CarolData(new ShortChannelId(999, 9, 9)), introduction: false);

        // Act
        await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: update_fail_malformed_htlc + invalid_onion_blinding with the trampoline packet's sha256 (PR 836)
        var malformed = Assert.Single(harness.Sent, s => s is { From: "Carol", To: "Bob" }
                                                     && s.Message is UpdateFailMalformedHtlcMessage);
        var payload = ((UpdateFailMalformedHtlcMessage)malformed.Message).Payload;
        Assert.Equal((ushort)FailureCode.InvalidOnionBlinding, payload.FailureCode);
        Assert.Equal(SHA256.HashData(payment.Trampoline.Packet.ToBytes()), payload.Sha256OfOnion.ToArray());
        Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Empty(_legSender.Started);
    }

    #endregion

    #region Policy (D-NL895-2)

    [Fact]
    public async Task Given_TwoPartsOfTheTotal_When_TheSetCompletes_Then_PaymentRelayAppliesToTheWholeSet()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);

        // Act: 400,000 then 600,200 msat, each promising the outer total
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000));
        await harness.PumpAsync();
        Assert.Empty(_legSender.Started);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(600_200));
        await harness.PumpAsync();

        // Assert: one leg for the whole set, at payment_relay over the total (not over each part)
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(s_amountOut, leg.Amount);
        Assert.Equal(IncomingCltv - s_carolRelay.CltvExpiryDelta, leg.FinalCltvExpiry);
        Assert.Equal(s_total - s_amountOut, leg.MaxFee);
        Assert.Equal(2, (await GetRelayAsync(harness, payment.Hash)).Parts.Count);
    }

    [Fact]
    public async Task Given_CarolRestartsWhileCollecting_When_TheLastPartArrives_Then_TheLegGoesToTheResolvedNode()
    {
        // Arrange: the first part of a scid-named blinded hop, then Carol restarts from her database
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000));
        await harness.PumpAsync();
        Assert.Equal(harness.Alice.NodeId, (await GetRelayAsync(harness, payment.Hash)).Relay.NextNodeId);

        // Act
        await harness.RestartAsync(harness.Carol);
        await harness.Carol.Services.GetRequiredService<TrampolineRelayService>()
                   .StartAsync(TestContext.Current.CancellationToken);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(600_200));
        await harness.PumpAsync();

        // Assert: one leg, to the node resolved before the restart, at payment_relay's price
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(harness.Alice.NodeId, leg.NextNodeId);
        Assert.Equal(s_amountOut, leg.Amount);
        Assert.NotNull(leg.NextPathKey);
        Assert.Equal(2, (await GetRelayAsync(harness, payment.Hash)).Parts.Count);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_APaymentRelayDeltaBelowOurForwardingDelta_When_TheSetCompletes_Then_InvalidOnionBlinding()
    {
        // Arrange: delta 20 < Carol's 40
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var data = CarolData(ThreeNodeHarness.CarolAliceScid, new BlindedPaymentRelay(20, 10, 100));
        var payment = await NewPaymentAsync(harness, data, introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: our own invalid_onion_blinding, never NODE|26; the relay failed before any leg
        var decrypted = Decrypt(harness, onion, payment.Trampoline);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(FailureCode.InvalidOnionBlinding, decrypted.Code);
        Assert.Empty(_legSender.Started);
        var (relay, _) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Failed, relay.Status);
        Assert.Equal((ushort)FailureCode.InvalidOnionBlinding, relay.FailureCode);
    }

    [Fact]
    public async Task Given_AnOuterExpiryAboveTheHtlcs_When_ThePartArrives_Then_InvalidOnionBlinding()
    {
        // Arrange: the outer payload promises an expiry 10 blocks above the HTLC's
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total, outerCltv: IncomingCltv + 10);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, Decrypt(harness, onion, payment.Trampoline).Code);
        Assert.Empty(_legSender.Started);
        Assert.Null(await harness.Carol.InScopeAsync(u => u.TrampolineRelayDbRepository.GetAsync(payment.Hash)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_PaymentConstraintsViolated_When_ThePartArrives_Then_TheBlindedAnswerForOurRole(
        bool introduction)
    {
        // Arrange: max_cltv_expiry below the outer expiry
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var data = CarolData(ThreeNodeHarness.CarolAliceScid,
                             constraints: new BlindedPaymentConstraints(IncomingCltv - 1, 1));
        var payment = await NewPaymentAsync(harness, data, introduction);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: at the introduction node our own invalid_onion_blinding, past it update_fail_malformed_htlc
        Assert.Empty(_legSender.Started);
        if (introduction)
        {
            Assert.Equal(FailureCode.InvalidOnionBlinding, Decrypt(harness, onion, payment.Trampoline).Code);
            Assert.DoesNotContain(harness.Sent, s => s.Message is UpdateFailMalformedHtlcMessage);
        }
        else
        {
            Assert.Contains(harness.Sent, s => s is { From: "Carol", To: "Bob" }
                                            && s.Message is UpdateFailMalformedHtlcMessage);
        }
    }

    #endregion

    #region Helpers

    private Task<ThreeNodeHarness> CreateHarnessAsync(bool carolAlice = false,
                                                      FeatureSupport bobCarolScidAlias = FeatureSupport.No) =>
        ThreeNodeHarness.CreateAsync(h =>
        {
            h.Carol.Options.Features.OptionTrampolineRouting = FeatureSupport.Optional;
            h.Carol.Options.Features.AllowExperimentalFeatures = true;
            h.Carol.ConfigureServices = services =>
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
                services.Replace(ServiceDescriptor.Singleton(_carolMonitor.Object));
                services.Configure<HtlcSwitchOptions>(o => o.BlindedErrorMaxDelay = TimeSpan.Zero);
                services.AddTrampolineRelayServices();
                services.AddSingleton<ITrampolineLegSender>(_legSender);
            };
        }, bobCarolScidAlias, carolAlice);

    private static BlindedRecipientData CarolData(ShortChannelId shortChannelId, BlindedPaymentRelay? relay = null,
                                                  BlindedPaymentConstraints? constraints = null)
    {
        return new BlindedRecipientData
        {
            ShortChannelId = shortChannelId,
            PaymentRelay = relay ?? s_carolRelay,
            PaymentConstraints = constraints ?? new BlindedPaymentConstraints(IncomingCltv + 10_000, 1)
        };
    }

    /// <summary>
    /// Alice's blinded path (Carol → Alice, or Bob → Carol → Alice when Carol is not the introduction node) with Carol's
    /// <paramref name="carolData"/>, its hops as trampoline hops (BOLTs PR 836 TR-R-07), and, past the introduction
    /// node, Carol's path key for the outer payload (what Bob, the previous trampoline hop, would send her).
    /// </summary>
    private static async Task<BlindedPayment> NewPaymentAsync(ThreeNodeHarness harness, BlindedRecipientData carolData,
                                                              bool introduction)
    {
        var preimage = new Secret(RandomNumberGenerator.GetBytes(32));
        var hash = new Hash(SHA256.HashData((byte[])preimage));
        var blinding = harness.Alice.Services.GetRequiredService<IRouteBlindingService>();
        var nodeIds = new List<CompactPubKey>();
        var data = new List<BlindedRecipientData>();
        if (!introduction)
        {
            nodeIds.Add(harness.Bob.NodeId);
            data.Add(new BlindedRecipientData
            {
                NextNodeId = harness.Carol.NodeId,
                PaymentRelay = s_carolRelay,
                PaymentConstraints = new BlindedPaymentConstraints(IncomingCltv + 20_000, 1)
            });
        }

        nodeIds.Add(harness.Carol.NodeId);
        data.Add(carolData);
        nodeIds.Add(harness.Alice.NodeId);
        data.Add(new BlindedRecipientData { PathId = RandomNumberGenerator.GetBytes(32) });
        var path = blinding.CreateBlindedPath(nodeIds, data.Select(blinding.EncodeRecipientData).ToList(),
                                              RandomNumberGenerator.GetBytes(32));

        var carolIndex = introduction ? 0 : 1;
        var carolHop = path.Hops[carolIndex];
        CompactPubKey? outerPathKey = null;
        var hops = new List<(CompactPubKey, HopPayload)>();
        if (introduction)
        {
            hops.Add((harness.Carol.NodeId,
                      new HopPayload(new EncryptedRecipientDataTlv(carolHop.EncryptedRecipientData.Span),
                                     new CurrentPathKeyTlv(path.FirstPathKey))));
        }
        else
        {
            outerPathKey = harness.Bob.Services.GetRequiredService<IRouteBlindingService>()
                                  .UnblindAsLocalNode(path.FirstPathKey, path.Hops[0].EncryptedRecipientData)
                                  .NextPathKey;
            hops.Add((carolHop.BlindedNodeId,
                      new HopPayload(new EncryptedRecipientDataTlv(carolHop.EncryptedRecipientData.Span))));
        }

        hops.Add((path.Hops[^1].BlindedNodeId,
                  new HopPayload(new AmtToForwardTlv(s_amountOut), new OutgoingCltvValueTlv(FinalCltv),
                                 new EncryptedRecipientDataTlv(path.Hops[^1].EncryptedRecipientData.Span),
                                 new TotalAmountMsatTlv(s_amountOut))));

        var serializer = harness.Alice.Services.GetRequiredService<IHopPayloadSerializer>();
        var onionHops = new List<OnionHop>();
        foreach (var (nodeId, payload) in hops)
        {
            using var stream = new MemoryStream();
            await serializer.SerializeAsync(payload, stream);
            onionHops.Add(new OnionHop(nodeId, stream.ToArray()));
        }

        var trampoline = harness.Alice.Services.GetRequiredService<ITrampolineOnionService>()
                                .Build(onionHops, RandomNumberGenerator.GetBytes(32), hash,
                                       TrampolineOnionSizePolicy.Auto(650));
        return new BlindedPayment(hash, trampoline, RandomNumberGenerator.GetBytes(32), outerPathKey);
    }

    /// <summary>
    /// Alice → Bob → Carol, one HTLC of <paramref name="part"/> expiring at <see cref="IncomingCltv"/>, whose outer
    /// final payload promises <see cref="s_total"/> and carries the trampoline onion (and, past the introduction
    /// node, Carol's path key).
    /// </summary>
    private static async Task<PaymentOnion> PayPartAsync(ThreeNodeHarness harness, BlindedPayment payment,
                                                         LightningMoney part, uint? outerCltv = null,
                                                         ShortChannelId? bobCarolScid = null)
    {
        var route = harness.RouteToCarol(part, payment.Hash, new Secret(payment.OuterSecret), IncomingCltvDelta,
                                         bobCarolScid: bobCarolScid);
        var serializer = harness.Alice.Services.GetRequiredService<IHopPayloadSerializer>();
        var hops = new List<OnionHop>();
        foreach (var hop in route.Hops)
        {
            HopPayload payload;
            if (hop.IsFinal)
            {
                var tlvs = new List<Domain.Protocol.Tlv.BaseTlv>
                {
                    new AmtToForwardTlv(hop.AmountToForward),
                    new OutgoingCltvValueTlv(outerCltv ?? hop.OutgoingCltvValue),
                    new PaymentDataTlv(payment.OuterSecret, s_total),
                    new TrampolineOnionPacketTlv(payment.Trampoline.Packet)
                };
                if (payment.OuterPathKey is { } pathKey)
                    tlvs.Add(new CurrentPathKeyTlv(pathKey));
                payload = new HopPayload(tlvs.ToArray());
            }
            else
            {
                payload = PaymentOnionFactory.CreatePayload(hop, route);
            }

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

    private static async Task<(TrampolineRelayModel Relay, IReadOnlyList<TrampolineRelayPartModel> Parts)>
        GetRelayAsync(ThreeNodeHarness harness, Hash paymentHash)
    {
        var stored = await harness.Carol.InScopeAsync(u => u.TrampolineRelayDbRepository.GetAsync(paymentHash));
        Assert.NotNull(stored);
        return stored.Value;
    }

    private static TrampolineDecryptedFailure Decrypt(ThreeNodeHarness harness, PaymentOnion onion,
                                                      TrampolineOnion trampoline)
    {
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var decrypted = harness.Alice.Services.GetRequiredService<ITrampolineFailureOnionService>()
                               .DecryptTrampolineErrorPacket(onion.SharedSecrets, trampoline.SharedSecrets,
                                                             failed.Removal.Reason.Span);
        Assert.NotNull(decrypted);
        return decrypted;
    }

    private sealed record BlindedPayment(Hash Hash, TrampolineOnion Trampoline, byte[] OuterSecret,
                                         CompactPubKey? OuterPathKey);

    /// <summary>Records the legs the engine starts.</summary>
    private sealed class FakeLegSender : ITrampolineLegSender
    {
        private readonly List<TrampolineLegRequest> _started = [];

        public IReadOnlyList<TrampolineLegRequest> Started
        {
            get
            {
                lock (_started)
                    return _started.ToList();
            }
        }

        public Task StartAsync(TrampolineLegRequest request, CancellationToken cancellationToken)
        {
            lock (_started)
                _started.Add(request);
            return Task.CompletedTask;
        }

        public Task HandleOutgoingFulfilledAsync(OutgoingHtlcFulfilled fulfilled, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task HandleOutgoingFailedAsync(OutgoingHtlcFailed failed, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    #endregion
}