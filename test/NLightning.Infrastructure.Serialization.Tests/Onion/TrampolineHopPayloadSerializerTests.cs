using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Serialization.Tests.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Onion.Codecs;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Helpers;
using Serialization.Onion;

/// <summary>
/// The trampoline hop payload records (BOLTs PR 836: 14, 20, 21, 22) through the real hop payload serializer, byte
/// for byte against the PR's vectors.
/// </summary>
public class TrampolineHopPayloadSerializerTests
{
    private const string EveNodeId = "02edabbd16b41c8371b92ef2f04c1185b4f03b6dcd52ba9b78d9d7c89c8f221145";
    private const string DaveNodeId = "032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991";
    private const string EvePathKey = "02988face71e92c345a068f740191fd8e53be14f0bb957ef730d3c5f76087b960e";

    private readonly HopPayloadSerializer _serializer = new(SerializerHelper.TlvSerializer,
                                                            SerializerHelper.TlvStreamSerializer,
                                                            SerializerHelper.ValueObjectSerializerFactory);

    public static TheoryData<string> TrampolineVectorPayloads => new(
        Bolt4TrampolineVectors.IntermediateInner,
        Bolt4TrampolineVectors.FinalInner,
        Bolt4TrampolineVectors.OuterWithTrampoline,
        Bolt4TrampolineVectors.ToBlindedPathsInner,
        Bolt4TrampolineVectors.BlindedIntermediateTrampolineInner,
        Bolt4TrampolineVectors.BlindedIntroductionInner,
        Bolt4TrampolineVectors.BlindedFinalInner,
        Bolt4TrampolineVectors.BlindedFinalOuter);

    [Theory]
    [MemberData(nameof(TrampolineVectorPayloads))]
    public async Task Given_TrampolineVectorPayload_When_RoundTripping_Then_BytesAreIdentical(string hex)
    {
        // Arrange
        var expected = Convert.FromHexString(hex);
        using var input = new MemoryStream(expected);

        // Act
        var payload = await _serializer.DeserializeWithLengthPrefixAsync(input);
        using var output = new MemoryStream();
        await _serializer.SerializeWithLengthPrefixAsync(payload, output);

        // Assert
        Assert.Equal(expected.Length, input.Position);
        Assert.Equal(expected, output.ToArray());
        Assert.Empty(payload.UnknownTlvs);
    }

    [Fact]
    public async Task Given_IntermediateTrampolineVector_When_Deserializing_Then_FieldsAreRead()
    {
        // Arrange
        using var input = new MemoryStream(Convert.FromHexString(Bolt4TrampolineVectors.IntermediateInner));

        // Act
        var payload = await _serializer.DeserializeWithLengthPrefixAsync(input);

        // Assert
        Assert.Equal(100_000_000UL, payload.AmtToForward!.MilliSatoshi);
        Assert.Equal(800_000U, payload.OutgoingCltvValue);
        Assert.Equal(EveNodeId, Convert.ToHexStringLower((byte[])payload.OutgoingNodeId!.Value));
        Assert.Null(payload.ShortChannelId);
        Assert.Null(payload.PaymentData);
        Assert.Null(payload.TrampolineOnionPacket);
        Assert.True(payload.TryGetRecordOffset(OnionPayloadTlvTypes.OutgoingNodeId, out var offset));
        Assert.Equal(12, offset);
    }

    [Fact]
    public async Task Given_IntermediateTrampolineFields_When_Serializing_Then_BytesMatchTheVector()
    {
        // Arrange
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(100_000_000)),
                                     new OutgoingCltvValueTlv(800_000),
                                     new OutgoingNodeIdTlv(new CompactPubKey(Convert.FromHexString(EveNodeId))));
        using var output = new MemoryStream();

        // Act
        await _serializer.SerializeWithLengthPrefixAsync(payload, output);

        // Assert
        Assert.Equal(Bolt4TrampolineVectors.IntermediateInner, Convert.ToHexStringLower(output.ToArray()));
    }

    [Fact]
    public async Task Given_FinalTrampolineFields_When_Serializing_Then_BytesMatchTheVector()
    {
        // Arrange
        var secret = new Secret(Enumerable.Repeat((byte)0x2a, 32).ToArray());
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(100_000_000)),
                                     new OutgoingCltvValueTlv(800_000),
                                     new PaymentDataTlv(secret, LightningMoney.MilliSatoshis(100_000_000)));
        using var output = new MemoryStream();

        // Act
        await _serializer.SerializeWithLengthPrefixAsync(payload, output);

        // Assert
        Assert.Equal(Bolt4TrampolineVectors.FinalInner, Convert.ToHexStringLower(output.ToArray()));
    }

    [Fact]
    public async Task Given_OuterPayloadWithTrampolineOnion_When_Deserializing_Then_TheVariableSizePacketIsRead()
    {
        // Arrange
        using var input = new MemoryStream(Convert.FromHexString(Bolt4TrampolineVectors.OuterWithTrampoline));

        // Act
        var payload = await _serializer.DeserializeWithLengthPrefixAsync(input);

        // Assert
        var packet = Assert.NotNull(payload.TrampolineOnionPacket);
        Assert.Equal(Bolt4TrampolineVectors.AliceTrampolineOnion, Convert.ToHexStringLower(packet.ToBytes()));
        Assert.Equal(227, packet.Length);
        Assert.Equal(227 - OnionConstants.PacketOverheadLength, packet.HopPayloadsLength);
        Assert.Equal(0, packet.Version);
        Assert.Equal(100_005_000UL, payload.AmtToForward!.MilliSatoshi);
        Assert.Equal(800_250U, payload.OutgoingCltvValue);
        Assert.Equal(Enumerable.Repeat((byte)0x2b, 32).ToArray(), (byte[])payload.PaymentData!.PaymentSecret);
        Assert.Equal(100_005_000UL, payload.PaymentData.TotalMsat.MilliSatoshi);
    }

    [Fact]
    public async Task Given_TrampolineOnion_When_SerializingAnOuterPayload_Then_BytesMatchTheVector()
    {
        // Arrange
        var secret = new Secret(Enumerable.Repeat((byte)0x2b, 32).ToArray());
        var onion = new TrampolineOnionPacketTlv(Convert.FromHexString(Bolt4TrampolineVectors.AliceTrampolineOnion));
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(100_005_000)),
                                     new OutgoingCltvValueTlv(800_250),
                                     new PaymentDataTlv(secret, LightningMoney.MilliSatoshis(100_005_000)),
                                     onion);
        using var output = new MemoryStream();

        // Act
        await _serializer.SerializeWithLengthPrefixAsync(payload, output);

        // Assert
        Assert.Equal(Bolt4TrampolineVectors.OuterWithTrampoline, Convert.ToHexStringLower(output.ToArray()));
    }

    [Fact]
    public async Task Given_PayloadToBlindedPaths_When_Deserializing_Then_FeaturesAndPathsAreRead()
    {
        // Arrange
        using var input = new MemoryStream(Convert.FromHexString(Bolt4TrampolineVectors.ToBlindedPathsInner));

        // Act
        var payload = await _serializer.DeserializeWithLengthPrefixAsync(input);

        // Assert
        Assert.Equal(150_000_000UL, payload.AmtToForward!.MilliSatoshi);
        Assert.Equal(800_000U, payload.OutgoingCltvValue);
        Assert.Null(payload.OutgoingNodeId);
        Assert.NotNull(payload.RecipientFeatures);
        var features = payload.RecipientFeatures;
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00 }, features.Features.ToArray());
        Assert.True(features.GetFeatureSet().IsFeatureSet(Feature.BasicMpp, false));
        var path = Assert.Single(payload.RecipientBlindedPaths!);
        Assert.Equal(DaveNodeId, Convert.ToHexStringLower((byte[])path.Path.FirstNode.NodeId!.Value));
        Assert.Equal(EvePathKey, Convert.ToHexStringLower((byte[])path.Path.FirstPathKey));
        Assert.Equal(2, path.Path.Hops.Count);
        Assert.Equal(new BlindedPayInfo(500, 1000, 36, 1, 500_000_000), path.PayInfo with { Features = default });
        Assert.True(path.PayInfo.Features.IsEmpty);
    }

    [Fact]
    public async Task Given_TheDecodedBlindedPaths_When_SerializingThePayloadAgain_Then_BytesMatchTheVector()
    {
        // Arrange
        using var input = new MemoryStream(Convert.FromHexString(Bolt4TrampolineVectors.ToBlindedPathsInner));
        var decoded = await _serializer.DeserializeWithLengthPrefixAsync(input);
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(150_000_000)),
                                     new OutgoingCltvValueTlv(800_000),
                                     new RecipientFeaturesTlv([0x02, 0x00, 0x00]),
                                     new RecipientBlindedPathsTlv(decoded.RecipientBlindedPaths!));
        using var output = new MemoryStream();

        // Act
        await _serializer.SerializeWithLengthPrefixAsync(payload, output);

        // Assert
        Assert.Equal(Bolt4TrampolineVectors.ToBlindedPathsInner, Convert.ToHexStringLower(output.ToArray()));
    }

    [Fact]
    public async Task Given_BlindedFinalOuterPayload_When_Deserializing_Then_PathKeyAndTrampolineOnionAreRead()
    {
        // Arrange
        using var input = new MemoryStream(Convert.FromHexString(Bolt4TrampolineVectors.BlindedFinalOuter));

        // Act
        var payload = await _serializer.DeserializeWithLengthPrefixAsync(input);

        // Assert
        Assert.Equal("02c952268f1501cf108839f4f5d0fbb41a97de778a6ead8caf161c569bd4df1ad7",
                     Convert.ToHexStringLower((byte[])payload.CurrentPathKey!.Value));
        Assert.NotNull(payload.TrampolineOnionPacket);
        Assert.Null(payload.EncryptedRecipientData);
        Assert.Equal(150_000_000UL, payload.PaymentData!.TotalMsat.MilliSatoshi);
    }

    public static TheoryData<string, ulong> MalformedTrampolineRecords => new()
    {
        // outgoing_node_id of 32 bytes
        { "0e20" + "03" + string.Concat(Enumerable.Repeat("11", 31)), 14UL },
        // outgoing_node_id without a 02/03 prefix
        { "0e21" + "04" + string.Concat(Enumerable.Repeat("11", 32)), 14UL },
        // trampoline_onion_packet of 66 bytes: the overhead and no hop_payloads
        { "1442" + string.Concat(Enumerable.Repeat("00", 66)), 20UL },
        // recipient_blinded_paths whose first path is a truncated sciddir
        { "1601" + "00", 22UL }
    };

    [Theory]
    [MemberData(nameof(MalformedTrampolineRecords))]
    public async Task Given_MalformedTrampolineRecord_When_Deserializing_Then_InvalidOnionPayloadWithTypeAndOffset(
        string recordHex, ulong expectedType)
    {
        // Arrange: amt_to_forward first, so the record starts at offset 3 + the 1-byte length prefix
        var hex = "020101" + recordHex;
        const ushort expectedOffset = 4;

        // Act
        var exception = await Assert.ThrowsAsync<OnionException>(() =>
            _serializer.DeserializeAsync(Convert.FromHexString(hex)));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
        Assert.True(InvalidOnionPayloadFailureFactory.TryDecodeData(exception.FailureData!.Value.Span, out var type,
                                                                    out var offset));
        Assert.Equal(expectedType, type.Value);
        Assert.Equal(expectedOffset, offset);
    }

    [Fact]
    public async Task Given_RecipientBlindedPathsWithATruncatedPayInfo_When_Deserializing_Then_InvalidOnionPayload()
    {
        // Arrange: the vector's 22 with the last byte of its payinfo cut, the TLV length lowered to match
        var value = Convert.FromHexString(Bolt4TrampolineVectors.ToBlindedPathsInner)[^417..^1];
        var hex = "020101" + "16fd01a0" + Convert.ToHexStringLower(value);

        // Act
        var exception = await Assert.ThrowsAsync<OnionException>(() =>
            _serializer.DeserializeAsync(Convert.FromHexString(hex)));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
        Assert.True(InvalidOnionPayloadFailureFactory.TryDecodeData(exception.FailureData!.Value.Span, out var type,
                                                                    out _));
        Assert.Equal(OnionPayloadTlvTypes.RecipientBlindedPaths, type);
        Assert.False(PaymentBlindedPathCodec.TryReadList(value, out _, out _));
    }
}