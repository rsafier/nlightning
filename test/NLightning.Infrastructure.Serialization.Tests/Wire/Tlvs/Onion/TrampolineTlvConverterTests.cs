using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node;
using Domain.Protocol.Onion.Codecs;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class TrampolineTlvConverterTests
{
    private static readonly byte[] s_nodeId =
        Convert.FromHexString("02edabbd16b41c8371b92ef2f04c1185b4f03b6dcd52ba9b78d9d7c89c8f221145");

    // The recipient_blinded_paths value of trampoline-to-blinded-path-payment-onion-test.json [0] (417 bytes)
    private static byte[] RecipientBlindedPathsValue =>
        Convert.FromHexString(Bolt4TrampolineVectors.ToBlindedPathsInner)[^417..];

    #region outgoing_node_id (14)

    [Fact]
    public void Given_OutgoingNodeIdTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect()
    {
        // Arrange
        var expectedBaseTlv = new BaseTlv(OnionPayloadTlvTypes.OutgoingNodeId, s_nodeId.ToArray());
        var expectedTlv = new OutgoingNodeIdTlv(new CompactPubKey(s_nodeId));
        var converter = new WireRegistry().GetTlvDefinition<OutgoingNodeIdTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedTlv);
        var tlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(s_nodeId, (byte[])tlv.OutgoingNodeId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(34)]
    public void Given_WrongLength_When_ConvertingOutgoingNodeId_Then_Throws(int length)
    {
        // Arrange
        var value = new byte[length];
        if (length > 0)
            value[0] = 0x02;
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.OutgoingNodeId, value);
        var converter = new WireRegistry().GetTlvDefinition<OutgoingNodeIdTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(baseTlv));
    }

    [Fact]
    public void Given_BadPrefix_When_ConvertingOutgoingNodeId_Then_Throws()
    {
        // Arrange
        var value = s_nodeId.ToArray();
        value[0] = 0x04;
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.OutgoingNodeId, value);
        var converter = new WireRegistry().GetTlvDefinition<OutgoingNodeIdTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(baseTlv));
    }

    [Fact]
    public void Given_WrongType_When_ConvertingOutgoingNodeId_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.CurrentPathKey, s_nodeId.ToArray());
        var converter = new WireRegistry().GetTlvDefinition<OutgoingNodeIdTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(baseTlv));
    }

    #endregion

    #region trampoline_onion_packet (20)

    [Fact]
    public void Given_TheVectorTrampolineOnion_When_ConvertingToBaseAndBack_Then_ResultIsCorrect()
    {
        // Arrange
        var packet = Convert.FromHexString(Bolt4TrampolineVectors.AliceTrampolineOnion);
        var expectedBaseTlv = new BaseTlv(OnionPayloadTlvTypes.TrampolineOnionPacket, packet.ToArray());
        var expectedTlv = new TrampolineOnionPacketTlv(packet);
        var converter = new WireRegistry().GetTlvDefinition<TrampolineOnionPacketTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedTlv);
        var tlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.Equal(packet.Length - OnionConstants.PacketOverheadLength, tlv.HopPayloadsLength);
        Assert.Equal(packet, tlv.ToOnionPacket().ToBytes());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(66)]
    public void Given_PacketShorterThanTheOverheadPlusOne_When_ConvertingTrampolineOnion_Then_Throws(int length)
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.TrampolineOnionPacket, new byte[length]);
        var converter = new WireRegistry().GetTlvDefinition<TrampolineOnionPacketTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(baseTlv));
    }

    [Fact]
    public void Given_TheShortestPacket_When_ConvertingTrampolineOnion_Then_OneByteOfHopPayloads()
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.TrampolineOnionPacket,
                                  new byte[TrampolineOnionPacketTlv.MinLength]);
        var converter = new WireRegistry().GetTlvDefinition<TrampolineOnionPacketTlv>()!;

        // Act
        var tlv = converter.Decode(baseTlv);

        // Assert
        Assert.Equal(1, tlv.HopPayloadsLength);
        Assert.Equal(1, tlv.ToOnionPacket().HopPayloadsLength);
    }

    [Fact]
    public void Given_WrongType_When_ConvertingTrampolineOnion_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.PaymentMetadata, new byte[100]);
        var converter = new WireRegistry().GetTlvDefinition<TrampolineOnionPacketTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(baseTlv));
    }

    #endregion

    #region recipient_features (21)

    [Fact]
    public void Given_RecipientFeaturesTlv_When_ConvertingToBaseAndBack_Then_ResultIsCorrect()
    {
        // Arrange (basic_mpp, as in trampoline-to-blinded-path-payment-onion-test.json)
        var expectedBaseTlv = new BaseTlv(OnionPayloadTlvTypes.RecipientFeatures, [0x02, 0x00, 0x00]);
        var expectedTlv = new RecipientFeaturesTlv([0x02, 0x00, 0x00]);
        var converter = new WireRegistry().GetTlvDefinition<RecipientFeaturesTlv>()!;

        // Act
        var baseTlv = converter.Encode(expectedTlv);
        var tlv = converter.Decode(expectedBaseTlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        Assert.Equal(expectedTlv, tlv);
        Assert.True(tlv.GetFeatureSet().IsFeatureSet(Feature.BasicMpp, false));
    }

    [Fact]
    public void Given_EmptyRecipientFeatures_When_Converting_Then_NoBitIsSet()
    {
        // Arrange
        var converter = new WireRegistry().GetTlvDefinition<RecipientFeaturesTlv>()!;

        // Act
        var tlv = converter.Decode(new BaseTlv(OnionPayloadTlvTypes.RecipientFeatures, []));

        // Assert
        Assert.True(tlv.Features.IsEmpty);
        Assert.Empty(tlv.GetFeatureSet().GetSetBits());
    }

    [Fact]
    public void Given_WrongType_When_ConvertingRecipientFeatures_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.RecipientBlindedPaths, [0x02]);
        var converter = new WireRegistry().GetTlvDefinition<RecipientFeaturesTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(baseTlv));
    }

    #endregion

    #region recipient_blinded_paths (22)

    [Fact]
    public void Given_TheVectorBlindedPaths_When_ConvertingToBaseAndBack_Then_BytesAreIdentical()
    {
        // Arrange
        var value = RecipientBlindedPathsValue;
        var expectedBaseTlv = new BaseTlv(OnionPayloadTlvTypes.RecipientBlindedPaths, value.ToArray());
        var converter = new WireRegistry().GetTlvDefinition<RecipientBlindedPathsTlv>()!;

        // Act
        var tlv = converter.Decode(expectedBaseTlv);
        var baseTlv = converter.Encode(tlv);

        // Assert
        Assert.Equal(expectedBaseTlv, baseTlv);
        var path = Assert.Single(tlv.Paths);
        Assert.Equal(2, path.Path.Hops.Count);
        Assert.Equal(500U, path.PayInfo.FeeBaseMsat);
        Assert.Equal(500_000_000UL, path.PayInfo.HtlcMaximumMsat);
    }

    [Fact]
    public void Given_ATruncatedPayInfo_When_ConvertingBlindedPaths_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.RecipientBlindedPaths, RecipientBlindedPathsValue[..^1]);
        var converter = new WireRegistry().GetTlvDefinition<RecipientBlindedPathsTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(baseTlv));
    }

    [Fact]
    public void Given_NoPath_When_ConvertingBlindedPaths_Then_AnEmptyListIsRead()
    {
        // Arrange
        var converter = new WireRegistry().GetTlvDefinition<RecipientBlindedPathsTlv>()!;

        // Act
        var tlv = converter.Decode(new BaseTlv(OnionPayloadTlvTypes.RecipientBlindedPaths, []));

        // Assert
        Assert.Empty(tlv.Paths);
        Assert.Empty(PaymentBlindedPathCodec.EncodeList(tlv.Paths));
    }

    [Fact]
    public void Given_WrongType_When_ConvertingBlindedPaths_Then_Throws()
    {
        // Arrange
        var baseTlv = new BaseTlv(OnionPayloadTlvTypes.RecipientFeatures, RecipientBlindedPathsValue);
        var converter = new WireRegistry().GetTlvDefinition<RecipientBlindedPathsTlv>()!;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(baseTlv));
    }

    #endregion

    [Fact]
    public void Given_AFeatureSet_When_CreatingRecipientFeatures_Then_ItIsEncodedBigEndian()
    {
        // Arrange
        var features = FeatureSet.DeserializeFromBytes([0x02, 0x00, 0x00]);

        // Act
        var tlv = new RecipientFeaturesTlv(features);

        // Assert
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00 }, tlv.Value);
    }
}