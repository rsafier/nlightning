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
/// NL-440, BOLT 4 "Route Blinding": the writer "MAY add additional dummy hops at the end of the path (which it will
/// ignore on receipt) to obscure the path length". Dave's path Bob (scid) → Dave → Dave (dummy) ... → Dave: each of his
/// hops but the last says <c>next_node_id</c> = Dave, and <see cref="IncomingOnionProcessor"/> peels them all for the
/// one HTLC it receives, with real Sphinx onions and blinding crypto.
/// </summary>
public class DummyHopOnionProcessorTests : IDisposable
{
    private static readonly Hash s_paymentHash = Enumerable.Repeat((byte)0x66, 32).ToArray();
    private static readonly OnionReplayOwner s_replayOwner = new(ChannelId.Zero, 0, 2_000);
    private static readonly BlindedPaymentRelay s_relay = new(40, 100, 1_000);
    private static readonly byte[] s_pathId = Enumerable.Repeat((byte)0xD1, 32).ToArray();
    private const uint FinalCltv = 700;
    private const ulong AmountMsat = 1_000_000;

    private readonly PaymentsTestNode _sender = new("sender", 0x51);
    private readonly PaymentsTestNode _bob = new("bob", 0x52);
    private readonly PaymentsTestNode _carol = new("carol", 0x53);
    private readonly PaymentsTestNode _dave = new("dave", 0x54);

    public DummyHopOnionProcessorTests()
    {
        foreach (var node in new[] { _bob, _carol, _dave })
            node.Options.Features.OptionRouteBlinding = FeatureSupport.Optional;
    }

    public void Dispose()
    {
        _sender.Dispose();
        _bob.Dispose();
        _carol.Dispose();
        _dave.Dispose();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Given_DummyHopsAfterOurHop_When_DaveProcessesTheHtlc_Then_FinalWithTheLastLayersPathId(
        int dummyHops)
    {
        // Arrange
        var onion = await BuildAsync(dummyHops);
        var (bobAmount, bobCltv) = IncomingAtBob(dummyHops);
        var bob = Assert.IsType<IncomingOnionForward>(
            await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, s_replayOwner, null,
                                                   LightningMoney.MilliSatoshis(bobAmount), bobCltv));

        // Act: one HTLC reaches Dave; every dummy layer is peeled by him
        var result = await _dave.OnionProcessor.ProcessAsync(bob.NextPacket.ToBytes(), s_paymentHash, s_replayOwner,
                                                             bob.Blinded!.NextPathKey, bob.AmountToForward,
                                                             bob.OutgoingCltvValue);

        // Assert: the final layer's path_id, what the final hop would have received after every dummy relay, and
        // the dummy hops' fees and deltas kept by Dave
        var final = Assert.IsType<IncomingOnionFinal>(result);
        Assert.Equal(dummyHops, final.Blinded!.DummyHops);
        Assert.False(final.Blinded.IsIntroduction);
        Assert.Equal(s_pathId, final.Blinded.RecipientData.PathId!.Value.ToArray());
        Assert.True(final.Blinded.ReceivedAmount!.MilliSatoshi >= AmountMsat);
        Assert.True(final.Blinded.ReceivedAmount < bob.AmountToForward);
        Assert.Equal(FinalCltv, final.Blinded.ReceivedCltvExpiry);
        Assert.Equal(AmountMsat, final.Payload.AmtToForward!.MilliSatoshi);
    }

    [Fact]
    public async Task Given_ADummyHopsConstraintExceeded_When_DaveProcesses_Then_MalformedInvalidOnionBlinding()
    {
        // Arrange: the dummy hop's max_cltv_expiry is below the expiry that reaches it
        var onion = await BuildAsync(1, dummyConstraints: new BlindedPaymentConstraints(FinalCltv, 1));
        var (bobAmount, bobCltv) = IncomingAtBob(1);
        var bob = Assert.IsType<IncomingOnionForward>(
            await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, s_replayOwner, null,
                                                   LightningMoney.MilliSatoshis(bobAmount), bobCltv));

        // Act
        var result = await _dave.OnionProcessor.ProcessAsync(bob.NextPacket.ToBytes(), s_paymentHash, s_replayOwner,
                                                             bob.Blinded!.NextPathKey, bob.AmountToForward,
                                                             bob.OutgoingCltvValue);

        // Assert: BOLT 4 "the path_key came in update_add_htlc": every failure is invalid_onion_blinding, malformed
        Assert.Equal(FailureCode.InvalidOnionBlinding, Assert.IsType<IncomingOnionMalformed>(result).FailureCode);
    }

    [Fact]
    public async Task Given_ADummyHopWhoseFeeExceedsTheAmount_When_DaveProcesses_Then_MalformedInvalidOnionBlinding()
    {
        // Arrange: the amount reaching the dummy hop does not cover its fee_base_msat
        var onion = await BuildAsync(1, dummyRelay: new BlindedPaymentRelay(40, 0, uint.MaxValue));
        var (bobAmount, bobCltv) = IncomingAtBob(1);
        var bob = Assert.IsType<IncomingOnionForward>(
            await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, s_replayOwner, null,
                                                   LightningMoney.MilliSatoshis(bobAmount), bobCltv));

        // Act
        var result = await _dave.OnionProcessor.ProcessAsync(bob.NextPacket.ToBytes(), s_paymentHash, s_replayOwner,
                                                             bob.Blinded!.NextPathKey, bob.AmountToForward,
                                                             bob.OutgoingCltvValue);

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, Assert.IsType<IncomingOnionMalformed>(result).FailureCode);
    }

    [Fact]
    public async Task Given_OurHopRelaysToUsThenToCarol_When_DaveProcesses_Then_ForwardToCarolAfterOneSelfRelay()
    {
        // Arrange: a path that passes through Dave twice and ends at Carol (a hop relaying to ourselves is not only a
        // dummy hop at the end: it is peeled wherever it is)
        var onion = await BuildAsync(2, recipient: _carol);
        var (bobAmount, bobCltv) = IncomingAtBob(2);
        var bob = Assert.IsType<IncomingOnionForward>(
            await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, s_replayOwner, null,
                                                   LightningMoney.MilliSatoshis(bobAmount), bobCltv));

        // Act
        var result = await _dave.OnionProcessor.ProcessAsync(bob.NextPacket.ToBytes(), s_paymentHash, s_replayOwner,
                                                             bob.Blinded!.NextPathKey, bob.AmountToForward,
                                                             bob.OutgoingCltvValue);

        // Assert
        var forward = Assert.IsType<IncomingOnionForward>(result);
        Assert.Equal(1, forward.Blinded!.DummyHops);
        Assert.Equal(_carol.NodeId, forward.NextNodeId);
        Assert.Equal(FinalCltv, forward.OutgoingCltvValue);
    }

    [Fact]
    public async Task Given_ASecretRecovery_When_DaveReprocessesWithoutAmounts_Then_FinalWithoutReceivedAmounts()
    {
        // Arrange: the switch re-peels a stored HTLC without its amounts (secret recovery)
        var onion = await BuildAsync(2);
        var bob = Assert.IsType<IncomingOnionForward>(
            await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, replayOwner: null));

        // Act
        var result = await _dave.OnionProcessor.ProcessAsync(bob.NextPacket.ToBytes(), s_paymentHash, null,
                                                             bob.Blinded!.NextPathKey);

        // Assert
        var final = Assert.IsType<IncomingOnionFinal>(result);
        Assert.Equal(2, final.Blinded!.DummyHops);
        Assert.Null(final.Blinded.ReceivedAmount);
        Assert.Null(final.Blinded.ReceivedCltvExpiry);
    }

    private static (ulong Amount, uint Cltv) IncomingAtBob(int relayHopsAfterBob)
    {
        // Bob, then relayHopsAfterBob relaying hops of the same policy, each with the BOLT 4 relay formula upwards
        var amount = AmountMsat;
        var cltv = FinalCltv;
        for (var i = 0; i < relayHopsAfterBob + 1; i++)
        {
            amount = checked(amount + s_relay.FeeBaseMsat + (amount * s_relay.FeeProportionalMillionths + 999_999)
                                                             / 1_000_000 + 1);
            cltv += s_relay.CltvExpiryDelta;
        }

        return (amount, cltv);
    }

    /// <summary>
    /// Dave's path Bob (scid 1x1x1) → Dave → Dave x <paramref name="selfRelays"/> ... → <paramref name="recipient"/>
    /// (Dave by default), and the sender's onion to it.
    /// </summary>
    private async Task<byte[]> BuildAsync(int selfRelays, BlindedPaymentConstraints? dummyConstraints = null,
                                          BlindedPaymentRelay? dummyRelay = null, PaymentsTestNode? recipient = null)
    {
        recipient ??= _dave;
        var blinding = _dave.RouteBlinding;
        var data = new List<BlindedRecipientData>
        {
            new()
            {
                ShortChannelId = new ShortChannelId(1, 1, 1),
                PaymentRelay = s_relay,
                PaymentConstraints = new BlindedPaymentConstraints(FinalCltv + 1_000, 1)
            }
        };
        var nodeIds = new List<CompactPubKey> { _bob.NodeId };
        for (var i = 0; i < selfRelays; i++)
        {
            nodeIds.Add(_dave.NodeId);
            var last = i == selfRelays - 1;
            data.Add(new BlindedRecipientData
            {
                NextNodeId = last && recipient != _dave ? recipient.NodeId : _dave.NodeId,
                PaymentRelay = dummyRelay ?? s_relay,
                PaymentConstraints = dummyConstraints ?? new BlindedPaymentConstraints(FinalCltv + 1_000, 1)
            });
        }

        if (recipient != _dave)
        {
            // Carol's path: Dave's last relay names her, and she is the final hop
            nodeIds.Add(recipient.NodeId);
        }
        else
        {
            nodeIds.Add(_dave.NodeId);
        }

        data.Add(new BlindedRecipientData { PathId = s_pathId });
        var path = blinding.CreateBlindedPath(nodeIds, data.Select(blinding.EncodeRecipientData).ToList(),
                                              Enumerable.Repeat((byte)0x0A, 32).ToArray());

        var hops = new List<OnionHop>
        {
            new(_bob.NodeId, await SerializeAsync(new HopPayload(
                                                      new EncryptedRecipientDataTlv(
                                                          path.Hops[0].EncryptedRecipientData.Span),
                                                      new CurrentPathKeyTlv(path.FirstPathKey))))
        };
        for (var i = 1; i < path.Hops.Count - 1; i++)
            hops.Add(new OnionHop(path.Hops[i].BlindedNodeId,
                                  await SerializeAsync(new HopPayload(
                                                           new EncryptedRecipientDataTlv(
                                                               path.Hops[i].EncryptedRecipientData.Span)))));
        hops.Add(new OnionHop(path.Hops[^1].BlindedNodeId,
                              await SerializeAsync(new HopPayload(
                                                       new AmtToForwardTlv(LightningMoney.MilliSatoshis(AmountMsat)),
                                                       new OutgoingCltvValueTlv(FinalCltv),
                                                       new EncryptedRecipientDataTlv(
                                                           path.Hops[^1].EncryptedRecipientData.Span),
                                                       new TotalAmountMsatTlv(
                                                           LightningMoney.MilliSatoshis(AmountMsat))))));
        return _sender.Sphinx.Construct(hops, Enumerable.Repeat((byte)0x06, 32).ToArray(), s_paymentHash).ToBytes();
    }

    private async Task<byte[]> SerializeAsync(HopPayload payload)
    {
        using var stream = new MemoryStream();
        await _sender.HopPayloadSerializer.SerializeAsync(payload, stream);
        return stream.ToArray();
    }
}