using System.Security.Cryptography;

namespace NLightning.Application.Tests.Payments.Onion;

using Application.Payments.Onion;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// NL-875 TR2-T1: <see cref="IncomingOnionProcessor"/> classifies an HTLC whose outer onion ends at us with a
/// <c>trampoline_onion_packet</c> (BOLTs PR 836), with real Sphinx and trampoline onions: the outer onion is a one-hop
/// onion from the sender to Carol (or Dave, Eve), the trampoline onion is built with <c>ITrampolineOnionService</c>.
/// </summary>
public class TrampolineOnionProcessorTests : IDisposable
{
    private static readonly Hash s_paymentHash = Enumerable.Repeat((byte)0x7A, 32).ToArray();
    private static readonly OnionReplayOwner s_replayOwner = new(ChannelId.Zero, 0, 2_000);
    private static readonly Secret s_invoiceSecret = Enumerable.Repeat((byte)0x5E, 32).ToArray();
    private static readonly Secret s_outerSecret = Enumerable.Repeat((byte)0x0E, 32).ToArray();
    private static readonly BlindedPaymentRelay s_relay = new(40, 100, 1_000);
    private static readonly byte[] s_pathId = Enumerable.Repeat((byte)0xD7, 32).ToArray();
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(1_000_000);
    private const uint FinalCltv = 700;

    private readonly PaymentsTestNode _sender = new("sender", 0x61);
    private readonly PaymentsTestNode _carol = new("carol", 0x62);
    private readonly PaymentsTestNode _dave = new("dave", 0x63);
    private readonly PaymentsTestNode _eve = new("eve", 0x64);

    public TrampolineOnionProcessorTests()
    {
        foreach (var node in new[] { _carol, _dave, _eve })
        {
            node.EnableTrampoline();
            node.Options.Features.OptionRouteBlinding = FeatureSupport.Optional;
        }
    }

    public void Dispose()
    {
        _sender.Dispose();
        _carol.Dispose();
        _dave.Dispose();
        _eve.Dispose();
    }

    #region Final and relay

    [Fact]
    public async Task Given_ATrampolineOnionEndingAtUs_When_Processed_Then_TrampolineFinalWithTheMergedPayload()
    {
        // Arrange: the outer onion carries one part of 400,000 msat, promising 1,000,000 msat in total
        var part = LightningMoney.MilliSatoshis(400_000);
        var trampoline = await BuildTrampolineAsync((_carol.NodeId, FinalInner()));
        var outer = await BuildOuterAsync(_carol.NodeId, OuterPayload(trampoline, part, FinalCltv + 10, s_amount));

        // Act
        var result = await ProcessAsync(_carol, outer, part, FinalCltv + 10);

        // Assert: both secrets, the inner payload's secret, total and expiry with the HTLC's own amount
        var final = Assert.IsType<IncomingOnionTrampolineFinal>(result);
        Assert.Equal(outer.SharedSecrets[0], final.OuterSharedSecret);
        Assert.Equal(trampoline.SharedSecrets[0], final.TrampolineSharedSecret);
        Assert.Equal(final.OuterSharedSecret, final.SharedSecretOrNull);
        Assert.Null(final.Blinded);
        Assert.False(final.IsBlindedPastIntroduction);
        Assert.Equal(part, final.MergedPayload.AmtToForward);
        Assert.Equal(FinalCltv, final.MergedPayload.OutgoingCltvValue);
        Assert.Equal(s_invoiceSecret, final.MergedPayload.PaymentData!.PaymentSecret);
        Assert.Equal(s_amount, final.MergedPayload.PaymentData.TotalMsat);
        Assert.Null(final.MergedPayload.TrampolineOnionPacket);
        Assert.Equal(final.MergedPayload, final.ToFinal().Payload);
        Assert.Equal(final.OuterSharedSecret, final.ToFinal().SharedSecret);
    }

    [Fact]
    public async Task Given_ATrampolineOnionNamingTheNextTrampoline_When_Processed_Then_RelayWithTheNextPacket()
    {
        // Arrange: Carol relays to Dave, the recipient
        var trampoline = await BuildTrampolineAsync(
                             (_carol.NodeId, new HopPayload(new AmtToForwardTlv(s_amount),
                                                            new OutgoingCltvValueTlv(FinalCltv),
                                                            new OutgoingNodeIdTlv(_dave.NodeId))),
                             (_dave.NodeId, FinalInner()));
        var incoming = s_amount + LightningMoney.MilliSatoshis(5_000);
        var outer = await BuildOuterAsync(_carol.NodeId, OuterPayload(trampoline, incoming, FinalCltv + 600, incoming));

        // Act
        var result = await ProcessAsync(_carol, outer, incoming, FinalCltv + 600);

        // Assert: what to relay, and a next packet only Dave can peel, as the final trampoline node
        var relay = Assert.IsType<IncomingOnionTrampolineRelay>(result);
        Assert.Equal(_dave.NodeId, relay.NextNodeId);
        Assert.Null(relay.NextPathKey);
        Assert.Null(relay.RecipientBlindedPaths);
        Assert.Equal(s_amount, relay.AmountToForward);
        Assert.Equal(FinalCltv, relay.OutgoingCltvValue);
        Assert.Equal(incoming, relay.IncomingTotal);
        Assert.Equal(trampoline.SharedSecrets[0], relay.TrampolineSharedSecret);
        Assert.Equal(trampoline.HopPayloadsLength, relay.NextTrampolinePacket.HopPayloadsLength);

        var atDave = _dave.TrampolineOnion.Peel(relay.NextTrampolinePacket, s_paymentHash);
        Assert.True(atDave.IsFinal);
        Assert.Equal(trampoline.SharedSecrets[1], atDave.SharedSecret);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Given_TrampolineNotAdvertised_When_Processed_Then_InvalidOnionPayloadForTlv20AsBefore(
        bool configured, bool experimentalAllowed)
    {
        // Arrange: a node with the feature off, or configured but still gated as experimental
        using var node = new PaymentsTestNode("plain", 0x65);
        node.Options.Features.OptionTrampolineRouting = configured ? FeatureSupport.Optional : FeatureSupport.No;
        node.Options.Features.AllowExperimentalFeatures = experimentalAllowed;
        var trampoline = await BuildTrampolineAsync((node.NodeId, FinalInner()));
        var outer = await BuildOuterAsync(node.NodeId, OuterPayload(trampoline, s_amount, FinalCltv, s_amount));

        // Act
        var result = await ProcessAsync(node, outer, s_amount, FinalCltv);

        // Assert: TLV 20 is an unknown even type, failed with the outer secret only
        Assert.False(node.OnionProcessor.ProcessesTrampoline);
        var failed = Assert.IsType<IncomingOnionFailed>(result);
        Assert.Equal(outer.SharedSecrets[0], failed.SharedSecret);
        AssertInvalidOnionPayload(failed.Failure, OnionPayloadTlvTypes.TrampolineOnionPacket);
    }

    #endregion

    #region Inner failures

    [Fact]
    public async Task Given_AnInnerCltvAboveTheOuterOne_When_Processed_Then_DoubleWrappedFinalIncorrectCltvExpiry()
    {
        // Arrange: the trampoline payload asks for a later expiry than the outer onion gives
        var trampoline = await BuildTrampolineAsync((_carol.NodeId, FinalInner(cltv: FinalCltv + 1)));
        var outer = await BuildOuterAsync(_carol.NodeId, OuterPayload(trampoline, s_amount, FinalCltv, s_amount));

        // Act
        var result = await ProcessAsync(_carol, outer, s_amount, FinalCltv);

        // Assert: created with both secrets, read by the payer at the trampoline layer
        var failed = Assert.IsType<IncomingOnionTrampolineFailed>(result);
        Assert.Equal(FailureCode.FinalIncorrectCltvExpiry, failed.Failure.Code);
        var decrypted = DecryptAtPayer(outer, trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.FinalIncorrectCltvExpiry, decrypted.Code);
    }

    [Fact]
    public async Task Given_AnOuterTotalBelowTheInnerAmount_When_Processed_Then_DoubleWrappedFinalIncorrectHtlcAmount()
    {
        // Arrange: the outer onion promises 1 msat less than the trampoline payload forwards
        var trampoline = await BuildTrampolineAsync((_carol.NodeId, FinalInner()));
        var promised = s_amount - LightningMoney.MilliSatoshis(1);
        var outer = await BuildOuterAsync(_carol.NodeId, OuterPayload(trampoline, promised, FinalCltv, promised));

        // Act
        var result = await ProcessAsync(_carol, outer, promised, FinalCltv);

        // Assert
        var failed = Assert.IsType<IncomingOnionTrampolineFailed>(result);
        Assert.Equal(FailureCode.FinalIncorrectHtlcAmount, failed.Failure.Code);
        Assert.Equal(FailureCode.FinalIncorrectHtlcAmount, DecryptAtPayer(outer, trampoline, failed).Code);
    }

    [Fact]
    public async Task Given_AnUnknownEvenInnerType_When_Processed_Then_DoubleWrappedInvalidOnionPayload()
    {
        // Arrange: an even type below the custom range nobody knows
        var inner = new HopPayload(new AmtToForwardTlv(s_amount), new OutgoingCltvValueTlv(FinalCltv),
                                   new PaymentDataTlv(s_invoiceSecret, s_amount),
                                   new BaseTlv(new BigSize(100), [0x01]));
        var trampoline = await BuildTrampolineAsync((_carol.NodeId, inner));
        var outer = await BuildOuterAsync(_carol.NodeId, OuterPayload(trampoline, s_amount, FinalCltv, s_amount));

        // Act
        var result = await ProcessAsync(_carol, outer, s_amount, FinalCltv);

        // Assert
        var failed = Assert.IsType<IncomingOnionTrampolineFailed>(result);
        AssertInvalidOnionPayload(failed.Failure, new BigSize(100));
        var decrypted = DecryptAtPayer(outer, trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(FailureCode.InvalidOnionPayload, decrypted.Code);
    }

    [Fact]
    public async Task Given_AFinalInnerPayloadWithoutPaymentData_When_Processed_Then_DoubleWrappedInvalidOnionPayload()
    {
        // Arrange: the payer forgot the invoice's payment_secret
        var trampoline = await BuildTrampolineAsync(
                             (_carol.NodeId, new HopPayload(new AmtToForwardTlv(s_amount),
                                                            new OutgoingCltvValueTlv(FinalCltv))));
        var outer = await BuildOuterAsync(_carol.NodeId, OuterPayload(trampoline, s_amount, FinalCltv, s_amount));

        // Act
        var result = await ProcessAsync(_carol, outer, s_amount, FinalCltv);

        // Assert
        var failed = Assert.IsType<IncomingOnionTrampolineFailed>(result);
        AssertInvalidOnionPayload(failed.Failure, OnionPayloadTlvTypes.PaymentData);
    }

    [Fact]
    public async Task Given_ATrampolineOnionWithABadHmac_When_Processed_Then_TheOuterHopReportsTlv20()
    {
        // Arrange: one byte of the trampoline payloads flipped; the outer onion stays valid
        var trampoline = await BuildTrampolineAsync((_carol.NodeId, FinalInner()));
        var bytes = trampoline.ToTlvValue();
        bytes[40] ^= 0x01;
        var corrupt = new OnionPacket(bytes, trampoline.HopPayloadsLength);
        var outer = await BuildOuterAsync(_carol.NodeId,
                                          new HopPayload(new AmtToForwardTlv(s_amount),
                                                         new OutgoingCltvValueTlv(FinalCltv),
                                                         new TrampolineOnionPacketTlv(corrupt)));

        // Act
        var result = await ProcessAsync(_carol, outer, s_amount, FinalCltv);

        // Assert: no trampoline secret to encrypt with: the outer final hop names its TLV 20, never malformed
        var failed = Assert.IsType<IncomingOnionFailed>(result);
        Assert.Equal(outer.SharedSecrets[0], failed.SharedSecret);
        AssertInvalidOnionPayload(failed.Failure, OnionPayloadTlvTypes.TrampolineOnionPacket);
    }

    #endregion

    #region Blinded trampoline hops

    [Fact]
    public async Task Given_ABlindedTrampolineRoute_When_TheIntroductionThenTheRecipientProcess_Then_RelayThenFinal()
    {
        // Arrange: Eve's path Dave → Eve, every blinded hop a trampoline hop (BOLTs PR 836, payer side TR-R-07)
        var (trampoline, daveOuter, daveAmount, daveCltv) = await BuildBlindedTrampolineAsync(dummyHops: 0);

        // Act: Dave, the introduction node (the path key in his trampoline payload)
        var dave = Assert.IsType<IncomingOnionTrampolineRelay>(
            await ProcessAsync(_dave, daveOuter, daveAmount, daveCltv));

        // Assert: relay to Eve with the next path key, the amount and expiry from payment_relay
        Assert.True(dave.Blinded!.IsIntroduction);
        Assert.Equal(_eve.NodeId, dave.NextNodeId);
        Assert.NotNull(dave.NextPathKey);
        Assert.Equal(daveCltv - s_relay.CltvExpiryDelta, dave.OutgoingCltvValue);
        Assert.True(s_relay.TryComputeAmountToForward(daveAmount.MilliSatoshi, out var forwardMsat));
        Assert.Equal(forwardMsat, dave.AmountToForward!.MilliSatoshi);
        Assert.True(dave.AmountToForward >= s_amount);

        // Act: Eve, with Dave's path key in her outer payload
        var eveOuter = await BuildOuterAsync(_eve.NodeId, EveOuterPayload(dave, dave.NextPathKey!.Value));
        var eve = Assert.IsType<IncomingOnionTrampolineFinal>(
            await ProcessAsync(_eve, eveOuter, dave.AmountToForward, dave.OutgoingCltvValue!.Value));

        // Assert: the recipient past the introduction node, her path_id, a blinded merged payload
        Assert.False(eve.Blinded!.IsIntroduction);
        Assert.True(eve.IsBlindedPastIntroduction);
        Assert.Equal(0, eve.Blinded.DummyHops);
        Assert.Equal(s_pathId, eve.Blinded.RecipientData.PathId!.Value.ToArray());
        Assert.True(eve.MergedPayload.IsBlinded);
        Assert.Equal(dave.AmountToForward, eve.MergedPayload.AmtToForward);
        Assert.Equal(s_amount, eve.MergedPayload.TotalAmountMsat);
        Assert.Equal(FinalCltv, eve.MergedPayload.OutgoingCltvValue);
        Assert.Equal(SHA256.HashData(dave.NextTrampolinePacket.ToBytes()), eve.TrampolineOnionSha256);
        Assert.Equal(trampoline.SharedSecrets[1], eve.TrampolineSharedSecret);
    }

    [Fact]
    public async Task Given_ADummyTrampolineHopOfOurOwnPath_When_TheRecipientProcesses_Then_FinalAfterOneSelfRelay()
    {
        // Arrange: Eve's path Dave → Eve → Eve (a dummy hop, as our own BlindedPathBuilder makes them, NL-440)
        var (_, daveOuter, daveAmount, daveCltv) = await BuildBlindedTrampolineAsync(dummyHops: 1);
        var dave = Assert.IsType<IncomingOnionTrampolineRelay>(
            await ProcessAsync(_dave, daveOuter, daveAmount, daveCltv));
        var eveOuter = await BuildOuterAsync(_eve.NodeId, EveOuterPayload(dave, dave.NextPathKey!.Value));

        // Act
        var result = await ProcessAsync(_eve, eveOuter, dave.AmountToForward!, dave.OutgoingCltvValue!.Value);

        // Assert: both of Eve's trampoline layers peeled for one HTLC
        var eve = Assert.IsType<IncomingOnionTrampolineFinal>(result);
        Assert.Equal(1, eve.Blinded!.DummyHops);
        Assert.Equal(s_pathId, eve.Blinded.RecipientData.PathId!.Value.ToArray());
        Assert.Equal(FinalCltv, eve.MergedPayload.OutgoingCltvValue);
    }

    [Fact]
    public async Task Given_AWrongOuterPathKeyPastTheIntroduction_When_Processed_Then_MalformedInvalidOnionBlinding()
    {
        // Arrange: Eve gets a path key that does not unlock her trampoline layer
        var (_, daveOuter, daveAmount, daveCltv) = await BuildBlindedTrampolineAsync(dummyHops: 0);
        var dave = Assert.IsType<IncomingOnionTrampolineRelay>(
            await ProcessAsync(_dave, daveOuter, daveAmount, daveCltv));
        var eveOuter = await BuildOuterAsync(_eve.NodeId, EveOuterPayload(dave, _sender.NodeId));

        // Act
        var result = await ProcessAsync(_eve, eveOuter, dave.AmountToForward!, dave.OutgoingCltvValue!.Value);

        // Assert: the PR 836 blinded error vector's answer, with the trampoline packet's sha256
        var malformed = Assert.IsType<IncomingOnionMalformed>(result);
        Assert.Equal(FailureCode.InvalidOnionBlinding, malformed.FailureCode);
        Assert.Equal(SHA256.HashData(dave.NextTrampolinePacket.ToBytes()), malformed.Sha256OfOnion.ToArray());
    }

    [Fact]
    public async Task Given_UnreadableRecipientDataAtTheIntroduction_When_Processed_Then_DoubleWrappedInvalidOnionBlinding()
    {
        // Arrange: Dave's encrypted_recipient_data is garbage
        var trampoline = await BuildTrampolineAsync(
                             (_dave.NodeId, new HopPayload(new EncryptedRecipientDataTlv(new byte[40]),
                                                           new CurrentPathKeyTlv(_sender.NodeId))),
                             (_eve.NodeId, FinalInner()));
        var outer = await BuildOuterAsync(_dave.NodeId, OuterPayload(trampoline, s_amount, FinalCltv + 40, s_amount));

        // Act
        var result = await ProcessAsync(_dave, outer, s_amount, FinalCltv + 40);

        // Assert: the introduction node answers with its own invalid_onion_blinding, readable by the payer
        var failed = Assert.IsType<IncomingOnionTrampolineFailed>(result);
        Assert.Equal(FailureCode.InvalidOnionBlinding, failed.Failure.Code);
        var decrypted = DecryptAtPayer(outer, trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(FailureCode.InvalidOnionBlinding, decrypted.Code);
    }

    #endregion

    #region Helpers

    private static HopPayload FinalInner(uint cltv = FinalCltv) =>
        new(new AmtToForwardTlv(s_amount), new OutgoingCltvValueTlv(cltv),
            new PaymentDataTlv(s_invoiceSecret, s_amount));

    private static HopPayload OuterPayload(TrampolineOnion trampoline, LightningMoney amount, uint cltv,
                                           LightningMoney total) =>
        new(new AmtToForwardTlv(amount), new OutgoingCltvValueTlv(cltv), new PaymentDataTlv(s_outerSecret, total),
            new TrampolineOnionPacketTlv(trampoline.Packet));

    private static HopPayload EveOuterPayload(IncomingOnionTrampolineRelay dave, CompactPubKey pathKey) =>
        new(new AmtToForwardTlv(dave.AmountToForward!), new OutgoingCltvValueTlv(dave.OutgoingCltvValue!.Value),
            new CurrentPathKeyTlv(pathKey), new TrampolineOnionPacketTlv(dave.NextTrampolinePacket));

    private async Task<TrampolineOnion> BuildTrampolineAsync(params (CompactPubKey NodeId, HopPayload Payload)[] hops)
    {
        var onionHops = new List<OnionHop>();
        foreach (var (nodeId, payload) in hops)
            onionHops.Add(new OnionHop(nodeId, await SerializeAsync(payload)));

        return _sender.TrampolineOnion.Build(onionHops, Enumerable.Repeat((byte)0x2B, 32).ToArray(), s_paymentHash,
                                             TrampolineOnionSizePolicy.Exact);
    }

    private async Task<ConstructedOnion> BuildOuterAsync(CompactPubKey target, HopPayload payload) =>
        _sender.Sphinx.ConstructWithSharedSecrets([new OnionHop(target, await SerializeAsync(payload))],
                                                  Enumerable.Repeat((byte)0x1C, 32).ToArray(), s_paymentHash);

    private static Task<IncomingOnionResult> ProcessAsync(PaymentsTestNode node, ConstructedOnion outer,
                                                          LightningMoney amount, uint cltv) =>
        node.OnionProcessor.ProcessAsync(outer.Packet.ToBytes(), s_paymentHash, s_replayOwner, null, amount, cltv);

    /// <summary>
    /// Eve's blinded path Dave → Eve (x <paramref name="dummyHops"/> relaying to herself) → Eve, its hops as trampoline
    /// hops, and the sender's outer onion to Dave carrying it.
    /// </summary>
    private async Task<(TrampolineOnion Trampoline, ConstructedOnion DaveOuter, LightningMoney DaveAmount, uint
        DaveCltv)> BuildBlindedTrampolineAsync(int dummyHops)
    {
        var blinding = _eve.RouteBlinding;
        var constraints = new BlindedPaymentConstraints(FinalCltv + 10_000, 1);
        var nodeIds = new List<CompactPubKey> { _dave.NodeId };
        var data = new List<BlindedRecipientData>
        {
            new() { NextNodeId = _eve.NodeId, PaymentRelay = s_relay, PaymentConstraints = constraints }
        };
        for (var i = 0; i < dummyHops; i++)
        {
            nodeIds.Add(_eve.NodeId);
            data.Add(new BlindedRecipientData
            {
                NextNodeId = _eve.NodeId,
                PaymentRelay = s_relay,
                PaymentConstraints = constraints
            });
        }

        nodeIds.Add(_eve.NodeId);
        data.Add(new BlindedRecipientData { PathId = s_pathId });
        var path = blinding.CreateBlindedPath(nodeIds, data.Select(blinding.EncodeRecipientData).ToList(),
                                              Enumerable.Repeat((byte)0x0B, 32).ToArray());

        var hops = new List<(CompactPubKey, HopPayload)>
        {
            (_dave.NodeId, new HopPayload(new EncryptedRecipientDataTlv(path.Hops[0].EncryptedRecipientData.Span),
                                          new CurrentPathKeyTlv(path.FirstPathKey)))
        };
        for (var i = 1; i < path.Hops.Count - 1; i++)
            hops.Add((path.Hops[i].BlindedNodeId,
                      new HopPayload(new EncryptedRecipientDataTlv(path.Hops[i].EncryptedRecipientData.Span))));
        hops.Add((path.Hops[^1].BlindedNodeId,
                  new HopPayload(new AmtToForwardTlv(s_amount), new OutgoingCltvValueTlv(FinalCltv),
                                 new EncryptedRecipientDataTlv(path.Hops[^1].EncryptedRecipientData.Span),
                                 new TotalAmountMsatTlv(s_amount))));
        var trampoline = await BuildTrampolineAsync(hops.ToArray());

        // Enough for every relay of the path
        var daveAmount = s_amount + LightningMoney.MilliSatoshis(10_000);
        var daveCltv = FinalCltv + (uint)(dummyHops + 1) * s_relay.CltvExpiryDelta;
        var daveOuter = await BuildOuterAsync(_dave.NodeId,
                                              new HopPayload(new AmtToForwardTlv(daveAmount),
                                                             new OutgoingCltvValueTlv(daveCltv),
                                                             new TrampolineOnionPacketTlv(trampoline.Packet)));
        return (trampoline, daveOuter, daveAmount, daveCltv);
    }

    private TrampolineDecryptedFailure DecryptAtPayer(ConstructedOnion outer, TrampolineOnion trampoline,
                                                      IncomingOnionTrampolineFailed failed)
    {
        var packet = _carol.TrampolineFailureOnion.CreateTrampolineErrorPacket(
            failed.TrampolineSharedSecret, failed.OuterSharedSecret, failed.Failure);
        var decrypted = _sender.TrampolineFailureOnion.DecryptTrampolineErrorPacket(outer.SharedSecrets,
                                                                                    trampoline.SharedSecrets, packet);
        Assert.NotNull(decrypted);
        return decrypted;
    }

    private static void AssertInvalidOnionPayload(FailureMessage failure, BigSize type)
    {
        Assert.Equal(FailureCode.InvalidOnionPayload, failure.Code);
        Assert.True(InvalidOnionPayloadFailureFactory.TryDecodeData(failure.Data.Span, out var actualType, out _));
        Assert.Equal(type, actualType);
    }

    private async Task<byte[]> SerializeAsync(HopPayload payload)
    {
        using var stream = new MemoryStream();
        await _sender.HopPayloadSerializer.SerializeAsync(payload, stream);
        return stream.ToArray();
    }

    #endregion
}