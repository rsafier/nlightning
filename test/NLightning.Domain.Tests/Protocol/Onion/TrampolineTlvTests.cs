using NLightning.Tests.Utils.Vectors;

namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node;
using Domain.Protocol.Onion.Codecs;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

public class TrampolineTlvTests
{
    private static readonly byte[] s_nodeId =
        Convert.FromHexString("02edabbd16b41c8371b92ef2f04c1185b4f03b6dcd52ba9b78d9d7c89c8f221145");

    private static readonly byte[] s_daveNodeId =
        Convert.FromHexString("032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991");

    // The recipient_blinded_paths value of trampoline-to-blinded-path-payment-onion-test.json [0] (417 bytes)
    private static byte[] RecipientBlindedPathsValue =>
        Convert.FromHexString(Bolt4TrampolineVectors.ToBlindedPathsInner)[^417..];

    [Fact]
    public void Given_TrampolineTypes_When_Read_Then_TheyAreThePr836NumbersAndKnown()
    {
        // Act & Assert
        Assert.Equal(new BigSize(14), OnionPayloadTlvTypes.OutgoingNodeId);
        Assert.Equal(new BigSize(20), OnionPayloadTlvTypes.TrampolineOnionPacket);
        Assert.Equal(new BigSize(21), OnionPayloadTlvTypes.RecipientFeatures);
        Assert.Equal(new BigSize(22), OnionPayloadTlvTypes.RecipientBlindedPaths);
        Assert.Equal(4, OnionPayloadTlvTypes.TrampolineTypes.Count);
        Assert.All(OnionPayloadTlvTypes.TrampolineTypes, type => Assert.Contains(type, OnionPayloadTlvTypes.KnownTypes));
    }

    [Fact]
    public void Given_ANodeId_When_CreatingOutgoingNodeIdTlv_Then_ValueIsThePoint()
    {
        // Act
        var tlv = new OutgoingNodeIdTlv(new CompactPubKey(s_nodeId));

        // Assert
        Assert.Equal(OnionPayloadTlvTypes.OutgoingNodeId, tlv.Type);
        Assert.Equal(s_nodeId, tlv.Value);
        Assert.Equal(new BigSize(33), tlv.Length);
    }

    [Fact]
    public void Given_TheVectorTrampolineOnion_When_CreatingTheTlv_Then_ThePacketHasAVariableSize()
    {
        // Arrange
        var bytes = Convert.FromHexString(Bolt4TrampolineVectors.AliceTrampolineOnion);

        // Act
        var tlv = new TrampolineOnionPacketTlv(bytes);
        var packet = tlv.ToOnionPacket();

        // Assert
        Assert.Equal(bytes, tlv.Value);
        Assert.Equal(161, tlv.HopPayloadsLength);
        Assert.Equal(161, packet.HopPayloadsLength);
        Assert.Equal(bytes, packet.ToBytes());
        Assert.Equal(bytes[1..34], packet.PublicKey.ToArray());
        Assert.Equal(bytes[^32..], packet.Hmac.ToArray());
    }

    [Fact]
    public void Given_AnOnionPacket_When_CreatingTheTlv_Then_TheBytesAreThePacket()
    {
        // Arrange
        var packet = new OnionPacket(0, s_nodeId, new byte[100], new byte[32]);

        // Act
        var tlv = new TrampolineOnionPacketTlv(packet);

        // Assert
        Assert.Equal(packet.ToBytes(), tlv.Value);
        Assert.Equal(packet, tlv.ToOnionPacket());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(66)]
    public void Given_APacketWithoutHopPayloads_When_CreatingTheTlv_Then_Throws(int length)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new TrampolineOnionPacketTlv(new byte[length]));
    }

    [Fact]
    public void Given_ADefaultPacket_When_CreatingTheTlv_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => new TrampolineOnionPacketTlv(default(OnionPacket)));
    }

    [Fact]
    public void Given_RecipientFeatures_When_ReadingTheFeatureSet_Then_ItHoldsExactlyTheWireBits()
    {
        // Arrange (basic_mpp optional, bit 17, as in the vector)
        var tlv = new RecipientFeaturesTlv([0x02, 0x00, 0x00]);

        // Act
        var features = tlv.GetFeatureSet();

        // Assert
        Assert.Equal([17], features.GetSetBits());
        Assert.True(features.IsFeatureSet(Feature.BasicMpp, false));
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00 }, features.GetWireBytes());
    }

    [Fact]
    public void Given_AFeatureSet_When_CreatingRecipientFeatures_Then_TheValueIsItsWireBytes()
    {
        // Arrange
        var features = FeatureSet.DeserializeFromBytes([0x02, 0x00, 0x00]);
        features.SetFeature(Feature.OptionRouteBlinding, false);

        // Act
        var tlv = new RecipientFeaturesTlv(features);

        // Assert
        Assert.Equal(features.GetWireBytes(), tlv.Value);
        Assert.True(tlv.GetFeatureSet().HasSameBits(features));
    }

    [Fact]
    public void Given_TheVectorBlindedPaths_When_DecodingAndEncoding_Then_BytesAreIdentical()
    {
        // Arrange
        var value = RecipientBlindedPathsValue;

        // Act
        var paths = PaymentBlindedPathCodec.DecodeList(value);
        var encoded = PaymentBlindedPathCodec.EncodeList(paths);
        var tlv = new RecipientBlindedPathsTlv(paths);

        // Assert
        Assert.Equal(value, encoded);
        Assert.Equal(value, tlv.Value);
        var path = Assert.Single(paths);
        Assert.Equal(s_daveNodeId, (byte[])path.Path.FirstNode.NodeId!.Value);
        Assert.Equal("02988face71e92c345a068f740191fd8e53be14f0bb957ef730d3c5f76087b960e",
                     Convert.ToHexStringLower((byte[])path.Path.FirstPathKey));
        Assert.Equal(2, path.Path.Hops.Count);
        Assert.Equal("0295d40514096a8be54859e7dfe947b376eaafea8afe5cb4eb2c13ff857ed0b4be",
                     Convert.ToHexStringLower((byte[])path.Path.Hops[0].BlindedNodeId));
        Assert.Equal(43, path.Path.Hops[0].EncryptedRecipientData.Length);
        Assert.Equal(209, path.Path.Hops[1].EncryptedRecipientData.Length);
        Assert.Equal(500U, path.PayInfo.FeeBaseMsat);
        Assert.Equal(1000U, path.PayInfo.FeeProportionalMillionths);
        Assert.Equal((ushort)36, path.PayInfo.CltvExpiryDelta);
        Assert.Equal(1UL, path.PayInfo.HtlcMinimumMsat);
        Assert.Equal(500_000_000UL, path.PayInfo.HtlcMaximumMsat);
        Assert.True(path.PayInfo.Features.IsEmpty);
    }

    [Fact]
    public void Given_TwoPaths_When_EncodingTheList_Then_EachPayInfoFollowsItsPath()
    {
        // Arrange
        var first = PaymentBlindedPathCodec.DecodeList(RecipientBlindedPathsValue)[0];
        var second = first with { PayInfo = new BlindedPayInfo(1, 2, 3, 4, 5, new byte[] { 0x01, 0x00 }) };

        // Act
        var encoded = PaymentBlindedPathCodec.EncodeList([first, second]);
        var decoded = PaymentBlindedPathCodec.DecodeList(encoded);

        // Assert
        Assert.Equal(2 * RecipientBlindedPathsValue.Length + 2, encoded.Length);
        Assert.Equal(2, decoded.Count);
        Assert.Equal(new byte[] { 0x01, 0x00 }, decoded[1].PayInfo.Features.ToArray());
        Assert.Equal(3, decoded[1].PayInfo.CltvExpiryDelta);
        Assert.Equal(encoded, PaymentBlindedPathCodec.EncodeList(decoded));
    }

    [Theory]
    [InlineData(1)] // inside the payinfo
    [InlineData(28)] // the whole payinfo
    [InlineData(400)] // inside the path
    public void Given_ATruncatedValue_When_DecodingTheList_Then_ItIsRefused(int cut)
    {
        // Arrange
        var value = RecipientBlindedPathsValue[..^cut];

        // Act
        var ok = PaymentBlindedPathCodec.TryReadList(value, out var paths, out var reason);

        // Assert
        Assert.False(ok);
        Assert.Null(paths);
        Assert.StartsWith("payment_blinded_path 0:", reason);
        Assert.Throws<FormatException>(() => PaymentBlindedPathCodec.DecodeList(value));
    }

    [Fact]
    public void Given_PayInfoFeaturesRunningPastTheEnd_When_Decoding_Then_ItIsRefused()
    {
        // Arrange: flen = 1 with no feature byte
        var value = RecipientBlindedPathsValue;
        value[^1] = 0x01;

        // Act
        var ok = PaymentBlindedPathCodec.TryReadList(value, out _, out var reason);

        // Assert
        Assert.False(ok);
        Assert.Contains("features run past the end", reason);
    }

    [Fact]
    public void Given_AWirePathWithANodeId_When_ConvertingToTheM5Path_Then_TheFieldsAreKept()
    {
        // Arrange
        var wire = PaymentBlindedPathCodec.DecodeList(RecipientBlindedPathsValue)[0];

        // Act
        var converted = wire.TryToBlindedPaymentPath(out var paymentPath);
        var back = WireBlindedPaymentPath.FromBlindedPaymentPath(paymentPath!);

        // Assert
        Assert.True(converted);
        Assert.Equal(s_daveNodeId, (byte[])paymentPath!.Path.FirstNodeId);
        Assert.Same(wire.PayInfo, paymentPath.PayInfo);
        Assert.Equal(PaymentBlindedPathCodec.EncodeList([wire]), PaymentBlindedPathCodec.EncodeList([back]));
        Assert.Equal(paymentPath.Path.FirstNodeId, wire.ToBlindedPaymentPath(new CompactPubKey(s_daveNodeId)).Path
                                                       .FirstNodeId);
    }

    [Fact]
    public void Given_AWirePathIntroducedByAScid_When_ConvertingToTheM5Path_Then_ItNeedsTheResolvedNodeId()
    {
        // Arrange
        var wire = PaymentBlindedPathCodec.DecodeList(RecipientBlindedPathsValue)[0];
        var bySciddir = wire with
        {
            Path = wire.Path with { FirstNode = SciddirOrPubkey.FromShortChannelId(new(700_000, 1, 0), 1) }
        };

        // Act
        var converted = bySciddir.TryToBlindedPaymentPath(out var paymentPath);
        var resolved = bySciddir.ToBlindedPaymentPath(new CompactPubKey(s_daveNodeId));

        // Assert
        Assert.False(converted);
        Assert.Null(paymentPath);
        Assert.Equal(s_daveNodeId, (byte[])resolved.Path.FirstNodeId);
    }

    [Fact]
    public void Given_TrampolineTlvs_When_CreatingAHopPayload_Then_TheTypedAccessorsAreSet()
    {
        // Arrange
        var onion = Convert.FromHexString(Bolt4TrampolineVectors.AliceTrampolineOnion);
        var paths = PaymentBlindedPathCodec.DecodeList(RecipientBlindedPathsValue);

        // Act
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1_000)),
                                     new OutgoingCltvValueTlv(800_000),
                                     new OutgoingNodeIdTlv(new CompactPubKey(s_nodeId)),
                                     new TrampolineOnionPacketTlv(onion),
                                     new RecipientFeaturesTlv([0x02, 0x00, 0x00]),
                                     new RecipientBlindedPathsTlv(paths));

        // Assert
        Assert.Equal(s_nodeId, (byte[])payload.OutgoingNodeId!.Value);
        Assert.Equal(onion, payload.TrampolineOnionPacket!.Value.ToBytes());
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00 }, payload.RecipientFeatures!.Features.ToArray());
        Assert.Single(payload.RecipientBlindedPaths!);
        Assert.Empty(payload.UnknownTlvs);
        Assert.Equal([2UL, 4UL, 14UL, 20UL, 21UL, 22UL], payload.Tlvs.Select(t => t.Type.Value));
    }

    [Fact]
    public void Given_NoTrampolineTlv_When_CreatingAHopPayload_Then_TheTrampolineAccessorsAreNull()
    {
        // Act
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1_000)));

        // Assert
        Assert.Null(payload.OutgoingNodeId);
        Assert.Null(payload.TrampolineOnionPacket);
        Assert.Null(payload.RecipientFeatures);
        Assert.Null(payload.RecipientBlindedPaths);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    public void Given_ATrampolineTypeAsARawBaseTlv_When_CreatingAHopPayload_Then_Throws(ulong type)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new HopPayload(new BaseTlv(new BigSize(type), [0x01])));
    }
}