namespace NLightning.Infrastructure.Serialization.Tests.Onion;

using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Helpers;
using Serialization.Onion;

public class HopPayloadSerializerTests
{
    // BOLT 4 onion-test.json hop payloads (with their bigsize length prefix).
    private const string Hop0 = "1202023a98040205dc06080000000000000001";

    private const string Hop1 =
        "52020236b00402057806080000000000000002fd02013c0102030405060708090a0b0c0d0e0f0102030405060708090a0b0c0d0e0f"
      + "0102030405060708090a0b0c0d0e0f0102030405060708090a0b0c0d0e0f";

    private const string Hop2 = "12020230d4040204e206080000000000000003";
    private const string Hop3 = "1202022710040203e806080000000000000004";

    private static readonly string s_hop4 =
        "fd011002022710040203e8082224a33562c54507a9334e79f0dc4f17d407e6d7c61f0e2f3d0d38599502f617042710fd012de0"
      + string.Concat(Enumerable.Repeat("2a", 224));

    private readonly HopPayloadSerializer _serializer = new(SerializerHelper.TlvSerializer,
                                                            SerializerHelper.TlvStreamSerializer,
                                                            SerializerHelper.TlvConverterFactory,
                                                            SerializerHelper.ValueObjectSerializerFactory);

    public static TheoryData<string> VectorPayloads => new(Hop0, Hop1, Hop2, Hop3, s_hop4);

    [Theory]
    [MemberData(nameof(VectorPayloads))]
    public async Task Given_VectorPayloadWithLengthPrefix_When_RoundTripping_Then_BytesAreIdentical(string hex)
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
    }

    [Theory]
    [MemberData(nameof(VectorPayloads))]
    public async Task Given_VectorPayloadWithoutLengthPrefix_When_RoundTripping_Then_BytesAreIdentical(string hex)
    {
        // Arrange
        var prefixed = Convert.FromHexString(hex);
        var prefixLength = prefixed[0] == 0xfd ? 3 : 1;
        var expected = prefixed[prefixLength..];

        // Act
        var payload = await _serializer.DeserializeAsync(expected);
        using var output = new MemoryStream();
        await _serializer.SerializeAsync(payload, output);

        // Assert
        Assert.Equal(expected, output.ToArray());
    }

    [Fact]
    public async Task Given_Hop0Payload_When_Deserializing_Then_TypedFieldsAndOffsetsAreSet()
    {
        // Arrange
        var raw = Convert.FromHexString(Hop0)[1..];

        // Act
        var payload = await _serializer.DeserializeAsync(raw);

        // Assert
        Assert.Equal(15_000UL, payload.AmtToForward!.MilliSatoshi);
        Assert.Equal(1500U, payload.OutgoingCltvValue);
        Assert.Equal(new ShortChannelId(0, 0, 1), payload.ShortChannelId);
        Assert.Empty(payload.UnknownTlvs);
        // Offsets count the 1-byte length prefix the peeler stripped (BOLT 4: offset in the decrypted byte stream)
        AssertOffset(payload, OnionPayloadTlvTypes.AmtToForward, 1);
        AssertOffset(payload, OnionPayloadTlvTypes.OutgoingCltvValue, 5);
        AssertOffset(payload, OnionPayloadTlvTypes.ShortChannelId, 9);
    }

    [Fact]
    public async Task Given_Hop1PayloadWithLengthPrefix_When_Deserializing_Then_UnknownOddTlvIsKeptAndOffsetsIncludePrefix()
    {
        // Arrange
        using var input = new MemoryStream(Convert.FromHexString(Hop1));

        // Act
        var payload = await _serializer.DeserializeWithLengthPrefixAsync(input);

        // Assert
        var unknown = Assert.Single(payload.UnknownTlvs);
        Assert.Equal(typeof(BaseTlv), unknown.GetType());
        Assert.Equal(513UL, unknown.Type.Value);
        Assert.Equal(60, unknown.Value.Length);
        AssertOffset(payload, OnionPayloadTlvTypes.AmtToForward, 1);
        AssertOffset(payload, new BigSize(513), 19);
    }

    [Fact]
    public async Task Given_Hop4Payload_When_Deserializing_Then_PaymentDataAndUnknownOddTlvAreRead()
    {
        // Arrange
        using var input = new MemoryStream(Convert.FromHexString(s_hop4));

        // Act
        var payload = await _serializer.DeserializeWithLengthPrefixAsync(input);

        // Assert
        Assert.NotNull(payload.PaymentData);
        Assert.Equal(10_000UL, payload.PaymentData.TotalMsat.MilliSatoshi);
        Assert.Equal(Convert.FromHexString("24a33562c54507a9334e79f0dc4f17d407e6d7c61f0e2f3d0d38599502f61704"),
                     (byte[])payload.PaymentData.PaymentSecret);
        Assert.Null(payload.ShortChannelId);
        var unknown = Assert.Single(payload.UnknownTlvs);
        Assert.Equal(301UL, unknown.Type.Value);
        Assert.All(unknown.Value, b => Assert.Equal(0x2a, b));
    }

    [Fact]
    public async Task Given_TrailingHmacAfterPayload_When_DeserializingWithLengthPrefix_Then_StreamStopsAfterPayload()
    {
        // Arrange
        var bytes = Convert.FromHexString(Hop0 + new string('f', 64));
        using var input = new MemoryStream(bytes);

        // Act
        await _serializer.DeserializeWithLengthPrefixAsync(input);

        // Assert
        Assert.Equal(0x13, input.Position);
    }

    [Fact]
    public async Task Given_TypedPayload_When_Serializing_Then_BytesMatchVector()
    {
        // Arrange
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(15_000)),
                                     new OutgoingCltvValueTlv(1500),
                                     new OnionShortChannelIdTlv(new ShortChannelId(0, 0, 1)));
        using var output = new MemoryStream();

        // Act
        await _serializer.SerializeWithLengthPrefixAsync(payload, output);

        // Assert
        Assert.Equal(Convert.FromHexString(Hop0), output.ToArray());
    }

    [Theory]
    [InlineData("020101140100", 20UL, 4)] // unknown even type 20
    [InlineData("040101020101", 2UL, 4)] // types not increasing
    [InlineData("020101020102", 2UL, 4)] // duplicate type
    [InlineData("02020001", 2UL, 1)] // non-minimal tu64
    [InlineData("0203000001", 2UL, 1)] // tu64 with leading zero
    [InlineData("0607000000000000000001", 6UL, 1)] // short_channel_id wrong length
    [InlineData("0201010c21040000000000000000000000000000000000000000000000000000000000000000", 12UL, 4)] // current_path_key with a bad prefix
    [InlineData("020501", 2UL, 1)] // length exceeds remaining bytes
    [InlineData("020101fd", 0UL, 0)] // truncated type
    [InlineData("fd00020101", 0UL, 0)] // non-canonical type
    [InlineData("02", 0UL, 0)] // shorter than 2 bytes
    public async Task Given_InvalidRawPayload_When_Deserializing_Then_ThrowsInvalidOnionPayloadWithTypeAndOffset(
        string hex, ulong expectedType, ushort expectedOffset)
    {
        // Act
        var exception = await Assert.ThrowsAsync<OnionException>(() =>
            _serializer.DeserializeAsync(Convert.FromHexString(hex)));

        // Assert
        AssertInvalidOnionPayload(exception, expectedType, expectedOffset);
    }

    [Theory]
    [InlineData("00")] // legacy length 0
    [InlineData("0102")] // reserved length 1
    [InlineData("05020101")] // length exceeds remaining bytes
    [InlineData("fd00")] // truncated bigsize length
    [InlineData("fd0010")] // non-canonical bigsize length
    [InlineData("")] // empty
    public async Task Given_InvalidLengthPrefix_When_Deserializing_Then_ThrowsInvalidOnionPayloadWithTypeZero(
        string hex)
    {
        // Arrange
        using var input = new MemoryStream(Convert.FromHexString(hex));

        // Act
        var exception = await Assert.ThrowsAsync<OnionException>(() =>
            _serializer.DeserializeWithLengthPrefixAsync(input));

        // Assert
        AssertInvalidOnionPayload(exception, 0, 0);
    }

    [Fact]
    public async Task Given_InvalidRecordAfterLengthPrefix_When_Deserializing_Then_OffsetIncludesPrefix()
    {
        // Arrange
        using var input = new MemoryStream(Convert.FromHexString("06020101140100"));

        // Act
        var exception = await Assert.ThrowsAsync<OnionException>(() =>
            _serializer.DeserializeWithLengthPrefixAsync(input));

        // Assert
        AssertInvalidOnionPayload(exception, 20, 4);
    }

    [Fact]
    public async Task Given_EmptyPayload_When_Serializing_Then_ThrowsArgumentException()
    {
        // Arrange
        using var output = new MemoryStream();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _serializer.SerializeWithLengthPrefixAsync(new HopPayload(),
                                                                                                 output));
        await Assert.ThrowsAsync<ArgumentException>(() => _serializer.SerializeAsync(new HopPayload(), output));
    }

    [Fact]
    public async Task Given_ArraySegmentBackedMemory_When_Deserializing_Then_OnlyTheSliceIsRead()
    {
        // Arrange
        var bytes = Convert.FromHexString("ff" + Hop0[2..] + "ff");
        var slice = new ReadOnlyMemory<byte>(bytes, 1, bytes.Length - 2);

        // Act
        var payload = await _serializer.DeserializeAsync(slice);

        // Assert
        Assert.Equal(new ShortChannelId(0, 0, 1), payload.ShortChannelId);
        AssertOffset(payload, OnionPayloadTlvTypes.ShortChannelId, 9);
    }

    public static TheoryData<string> FramedVectorPayloads => new()
    {
        Hop1, // 1-byte length prefix
        s_hop4 // 3-byte length prefix
    };

    [Theory]
    [MemberData(nameof(FramedVectorPayloads))]
    public async Task Given_SamePayload_When_DeserializingWithAndWithoutPrefix_Then_OffsetsMatch(string framedHex)
    {
        // Arrange
        var framed = Convert.FromHexString(framedHex);
        var prefixLength = framed[0] == 0xfd ? 3 : 1;
        using var input = new MemoryStream(framed);

        // Act
        var withPrefix = await _serializer.DeserializeWithLengthPrefixAsync(input);
        var withoutPrefix = await _serializer.DeserializeAsync(framed.AsMemory(prefixLength));

        // Assert
        foreach (var tlv in withPrefix.Tlvs)
        {
            Assert.True(withPrefix.TryGetRecordOffset(tlv.Type, out var expected));
            Assert.True(expected >= prefixLength);
            AssertOffset(withoutPrefix, tlv.Type, expected);
        }
    }

    [Fact]
    public async Task Given_InvalidRecordInLongPayload_When_DeserializingWithoutPrefix_Then_OffsetCountsThreeBytePrefix()
    {
        // Arrange: 254 bytes (so a 3-byte length prefix): odd type 1 with 250 bytes, then unknown even type 20
        var payload = new byte[254];
        payload[0] = 0x01;
        payload[1] = 0xfa;
        payload[252] = 0x14;
        payload[253] = 0x00;

        // Act
        var exception = await Assert.ThrowsAsync<OnionException>(() => _serializer.DeserializeAsync(payload));

        // Assert: record 20 starts at payload offset 252, i.e. offset 255 in the decrypted byte stream
        AssertInvalidOnionPayload(exception, 20, 255);
    }

    private static void AssertOffset(HopPayload payload, BigSize type, int expectedOffset)
    {
        Assert.True(payload.TryGetRecordOffset(type, out var offset));
        Assert.Equal(expectedOffset, offset);
    }

    private static void AssertInvalidOnionPayload(OnionException exception, ulong expectedType, ushort expectedOffset)
    {
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
        Assert.NotNull(exception.FailureData);
        Assert.True(InvalidOnionPayloadFailureFactory.TryDecodeData(exception.FailureData.Value.Span,
                                                                    out var type, out var offset));
        Assert.Equal(expectedType, type.Value);
        Assert.Equal(expectedOffset, offset);
    }
}