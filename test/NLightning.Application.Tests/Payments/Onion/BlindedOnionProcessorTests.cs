namespace NLightning.Application.Tests.Payments.Onion;

using Application.Payments.Onion;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;

/// <summary>
/// ONION M5 in <see cref="IncomingOnionProcessor"/>, with real Sphinx onions over a blinded path Bob → Carol → Dave
/// (Bob the introduction node): each hop reads its recipient data, the relay amounts and the error rules.
/// </summary>
public class BlindedOnionProcessorTests : IDisposable
{
    private static readonly Hash s_paymentHash = Enumerable.Repeat((byte)0x77, 32).ToArray();
    private static readonly OnionReplayOwner s_replayOwner = new(ChannelId.Zero, 0, 1_000);
    private static readonly BlindedPaymentRelay s_relay = new(40, 100, 1_000);
    private const uint FinalCltv = 700;
    private const ulong AmountMsat = 1_000_000;

    private readonly PaymentsTestNode _sender = new("sender", 0x41);
    private readonly PaymentsTestNode _bob = new("bob", 0x42);
    private readonly PaymentsTestNode _carol = new("carol", 0x43);
    private readonly PaymentsTestNode _dave = new("dave", 0x44);

    public BlindedOnionProcessorTests()
    {
        foreach (var node in new[] { _bob, _carol, _dave })
        {
            node.Options.Features.AllowExperimentalFeatures = true;
            node.Options.Features.OptionRouteBlinding = FeatureSupport.Optional;
        }
    }

    public void Dispose()
    {
        _sender.Dispose();
        _bob.Dispose();
        _carol.Dispose();
        _dave.Dispose();
    }

    [Fact]
    public async Task Given_BlindedPath_When_EachHopProcesses_Then_IntroductionRelayAndFinalHopReadTheirData()
    {
        // Arrange
        var (onion, path) = await BuildAsync(carolConstraints: new BlindedPaymentConstraints(FinalCltv + 1_000, 1));
        var bobAmount = AmountMsat + 2 * (s_relay.FeeBaseMsat + 200);
        var bobCltv = FinalCltv + 2U * s_relay.CltvExpiryDelta;

        // Act: Bob (introduction), then Carol (path_key in update_add_htlc), then Dave (recipient)
        var bob = Assert.IsType<IncomingOnionForward>(
            await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, s_replayOwner, null,
                                                   LightningMoney.MilliSatoshis(bobAmount), bobCltv));
        var carol = Assert.IsType<IncomingOnionForward>(
            await _carol.OnionProcessor.ProcessAsync(bob.NextPacket.ToBytes(), s_paymentHash, s_replayOwner,
                                                     bob.Blinded!.NextPathKey, bob.AmountToForward,
                                                     bob.OutgoingCltvValue));
        var dave = Assert.IsType<IncomingOnionFinal>(
            await _dave.OnionProcessor.ProcessAsync(carol.NextPacket.ToBytes(), s_paymentHash, s_replayOwner,
                                                    carol.Blinded!.NextPathKey, carol.AmountToForward,
                                                    carol.OutgoingCltvValue));

        // Assert
        Assert.True(bob.Blinded.IsIntroduction);
        Assert.False(carol.Blinded.IsIntroduction);
        Assert.False(dave.Blinded!.IsIntroduction);
        Assert.Equal(new ShortChannelId(1, 1, 1), bob.OutgoingShortChannelId);
        Assert.Equal(_dave.NodeId, carol.NextNodeId);
        Assert.False(carol.HasOutgoingShortChannelId);
        Assert.Equal(bobCltv - s_relay.CltvExpiryDelta, bob.OutgoingCltvValue);
        Assert.True(s_relay.TryComputeAmountToForward(bobAmount, out var bobForward));
        Assert.Equal(bobForward, bob.AmountToForward.MilliSatoshi);
        Assert.True(carol.AmountToForward.MilliSatoshi >= AmountMsat);
        Assert.Equal(FinalCltv, carol.OutgoingCltvValue);
        Assert.Equal(path.PathId, dave.Blinded.RecipientData.PathId!.Value.ToArray());
    }

    [Fact]
    public async Task Given_RelayHopBelowItsMinimum_When_Processed_Then_MalformedInvalidOnionBlinding()
    {
        // Arrange: Carol's htlc_minimum_msat is above what reaches her
        var (onion, _) = await BuildAsync(carolConstraints: new BlindedPaymentConstraints(FinalCltv + 1_000,
                                                                                         AmountMsat * 10));
        var bob = Assert.IsType<IncomingOnionForward>(
            await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, s_replayOwner, null,
                                                   LightningMoney.MilliSatoshis(AmountMsat * 2), FinalCltv + 100));

        // Act
        var carol = await _carol.OnionProcessor.ProcessAsync(bob.NextPacket.ToBytes(), s_paymentHash, s_replayOwner,
                                                             bob.Blinded!.NextPathKey, bob.AmountToForward,
                                                             bob.OutgoingCltvValue);

        // Assert
        var malformed = Assert.IsType<IncomingOnionMalformed>(carol);
        Assert.Equal(FailureCode.InvalidOnionBlinding, malformed.FailureCode);
    }

    [Fact]
    public async Task Given_WrongPathKey_When_RelayHopProcesses_Then_MalformedInvalidOnionBlinding()
    {
        // Arrange: Carol gets the first path_key instead of the next one (her onion layer does not decrypt)
        var (onion, path) = await BuildAsync();
        var bob = Assert.IsType<IncomingOnionForward>(
            await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, s_replayOwner, null,
                                                   LightningMoney.MilliSatoshis(AmountMsat * 2), FinalCltv + 100));

        // Act
        var carol = await _carol.OnionProcessor.ProcessAsync(bob.NextPacket.ToBytes(), s_paymentHash, s_replayOwner,
                                                             path.Path.FirstPathKey, bob.AmountToForward,
                                                             bob.OutgoingCltvValue);

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, Assert.IsType<IncomingOnionMalformed>(carol).FailureCode);
    }

    [Fact]
    public async Task Given_NoIncomingAmounts_When_IntroductionProcesses_Then_ForwardWithoutComputedAmounts()
    {
        // Arrange: secret recovery (the switch re-peels to learn the blinded role)
        var (onion, _) = await BuildAsync();

        // Act
        var bob = Assert.IsType<IncomingOnionForward>(
            await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, replayOwner: null));

        // Assert
        Assert.True(bob.Blinded!.IsIntroduction);
        Assert.Null(bob.Blinded.AmountToForward);
        Assert.Throws<InvalidOperationException>(() => bob.AmountToForward);
    }

    [Fact]
    public async Task Given_IncomingAmountBelowTheFeeBase_When_IntroductionProcesses_Then_UpdateFailHtlcInvalidOnionBlinding()
    {
        // Arrange
        var (onion, _) = await BuildAsync();

        // Act
        var result = await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, s_replayOwner, null,
                                                            LightningMoney.MilliSatoshis(999), FinalCltv + 100);

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, Assert.IsType<IncomingOnionFailed>(result).Failure.Code);
    }

    [Fact]
    public async Task Given_ReplayedIntroductionOnion_When_Processed_Then_InvalidOnionBlindingNotTemporaryNodeFailure()
    {
        // Arrange
        var (onion, _) = await BuildAsync();
        await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, s_replayOwner);

        // Act: another HTLC with the same onion
        var result = await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, s_replayOwner with { HtlcId = 1 });

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, Assert.IsType<IncomingOnionFailed>(result).Failure.Code);
    }

    private sealed record DavesPath(BlindedPath Path, byte[] PathId);

    /// <summary>
    /// Dave's path Bob (scid 1x1x1) → Carol (next_node_id Dave) → Dave, and the sender's onion to it.
    /// </summary>
    private async Task<(byte[] Onion, DavesPath Path)> BuildAsync(BlindedPaymentConstraints? carolConstraints = null)
    {
        var blinding = _dave.RouteBlinding;
        var pathId = Enumerable.Repeat((byte)0xD0, 32).ToArray();
        var data = new[]
        {
            new BlindedRecipientData
            {
                ShortChannelId = new ShortChannelId(1, 1, 1),
                PaymentRelay = s_relay,
                PaymentConstraints = new BlindedPaymentConstraints(FinalCltv + 1_000, 1)
            },
            new BlindedRecipientData
            {
                NextNodeId = _dave.NodeId,
                PaymentRelay = s_relay,
                PaymentConstraints = carolConstraints ?? new BlindedPaymentConstraints(FinalCltv + 1_000, 1)
            },
            new BlindedRecipientData { PathId = pathId }
        };
        var path = blinding.CreateBlindedPath([_bob.NodeId, _carol.NodeId, _dave.NodeId],
                                              data.Select(blinding.EncodeRecipientData).ToList(),
                                              Enumerable.Repeat((byte)0x09, 32).ToArray());

        var payloads = new[]
        {
            new HopPayload(new EncryptedRecipientDataTlv(path.Hops[0].EncryptedRecipientData.Span),
                           new CurrentPathKeyTlv(path.FirstPathKey)),
            new HopPayload(new EncryptedRecipientDataTlv(path.Hops[1].EncryptedRecipientData.Span)),
            new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(AmountMsat)),
                           new OutgoingCltvValueTlv(FinalCltv),
                           new EncryptedRecipientDataTlv(path.Hops[2].EncryptedRecipientData.Span),
                           new TotalAmountMsatTlv(LightningMoney.MilliSatoshis(AmountMsat)))
        };
        var hops = new List<OnionHop>
        {
            new(_bob.NodeId, await SerializeAsync(payloads[0])),
            new(path.Hops[1].BlindedNodeId, await SerializeAsync(payloads[1])),
            new(path.Hops[2].BlindedNodeId, await SerializeAsync(payloads[2]))
        };
        var onion = _sender.Sphinx.Construct(hops, Enumerable.Repeat((byte)0x05, 32).ToArray(), s_paymentHash)
                           .ToBytes();
        return (onion, new DavesPath(path, pathId));
    }

    private async Task<byte[]> SerializeAsync(HopPayload payload)
    {
        using var stream = new MemoryStream();
        await _sender.HopPayloadSerializer.SerializeAsync(payload, stream);
        return stream.ToArray();
    }
}