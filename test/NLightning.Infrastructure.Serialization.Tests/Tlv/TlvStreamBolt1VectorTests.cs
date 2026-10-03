using System.Runtime.Serialization;

namespace NLightning.Infrastructure.Serialization.Tests.Tlv;

using Domain.Protocol.Models;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Helpers;
using Serialization.Tlv;

/// <summary>
/// BOLT 1 Appendix B "Type-Length-Value Test Vectors".
/// </summary>
/// <remarks>
/// The stream-level rules (ordering, unknown even types, truncation, length bounds) are enforced by
/// <see cref="TlvStreamSerializer.DeserializeStrictAsync"/>. The per-value rules of the <c>n1</c> namespace
/// (exact lengths, minimal <c>tu64</c>) are the job of a namespace's converters, so they are adapted here with a small
/// test-local validator applied after the strict stream decode.
/// </remarks>
public class TlvStreamBolt1VectorTests
{
    private static readonly HashSet<BigSize> s_n1KnownTypes = [1UL, 2UL, 3UL, 254UL];
    private static readonly HashSet<BigSize> s_n2KnownTypes = [0UL, 11UL];

    private const string ValidNodeId = "023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb";

    private readonly TlvStreamSerializer _tlvStreamSerializer = new(SerializerHelper.TlvConverterFactory,
                                                                     SerializerHelper.TlvSerializer);

    #region TLV Decoding Failures (any namespace)

    [Theory]
    [InlineData("fd")] // type truncated
    [InlineData("fd01")] // type truncated
    [InlineData("fd0101")] // missing length
    [InlineData("0f fd")] // length truncated
    [InlineData("0f fd26")] // length truncated
    [InlineData("0f fd2602")] // missing value
    [InlineData("12 00")] // unknown even type
    [InlineData("fd0102 00")] // unknown even type
    [InlineData("fe01000002 00")] // unknown even type
    [InlineData("ff0100000000000002 00")] // unknown even type
    public async Task Given_InvalidStreamInAnyNamespace_When_DeserializedStrict_Then_ThrowsSerializationException(
        string hex)
    {
        // Arrange
        var bytes = FromHex(hex);

        // Act & Assert
        await Assert.ThrowsAsync<SerializationException>(() => DecodeN1Async(bytes));
        await Assert.ThrowsAsync<SerializationException>(() => DecodeN2Async(bytes));
    }

    [Theory]
    [InlineData("fd0001 00")] // not minimally encoded type
    [InlineData("0f fd0001 00")] // not minimally encoded length
    public async Task Given_NonMinimalBigSizeInStream_When_DeserializedStrict_Then_ThrowsSerializationException(
        string hex)
    {
        // Arrange
        var bytes = FromHex(hex);

        // Act & Assert
        await Assert.ThrowsAsync<SerializationException>(() => DecodeN1Async(bytes));
        await Assert.ThrowsAsync<SerializationException>(() => DecodeN2Async(bytes));
    }

    [Fact]
    public async Task Given_TruncatedValue_When_DeserializedStrict_Then_ThrowsSerializationException()
    {
        // Arrange: length 0x0201 (513) but only 258 value bytes follow
        var bytes = FromHex("0f fd0201" + new string('0', 258 * 2));

        // Act & Assert
        await Assert.ThrowsAsync<SerializationException>(() => DecodeN1Async(bytes));
        await Assert.ThrowsAsync<SerializationException>(() => DecodeN2Async(bytes));
    }

    #endregion

    #region TLV Decoding Failures (n1 namespace)

    [Theory]
    [InlineData("01 09 ffffffffffffffffff")] // greater than encoding length for tlv1
    [InlineData("01 01 00")] // amount_msat not minimal
    [InlineData("01 02 0001")]
    [InlineData("01 03 000100")]
    [InlineData("01 04 00010000")]
    [InlineData("01 05 0001000000")]
    [InlineData("01 06 000100000000")]
    [InlineData("01 07 00010000000000")]
    [InlineData("01 08 0001000000000000")]
    [InlineData("02 07 01010101010101")] // less than encoding length for tlv2
    [InlineData("02 09 010101010101010101")] // greater than encoding length for tlv2
    [InlineData("03 21 " + ValidNodeId)] // less than encoding length for tlv3
    [InlineData("03 29 " + ValidNodeId + "0000000000000001")]
    [InlineData("03 30 " + ValidNodeId + "000000000000000100000000000001")]
    [InlineData("03 31 043da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb00000000000000010000000000000002")]
    [InlineData("03 32 " + ValidNodeId + "0000000000000001000000000000000001")] // greater than
    [InlineData("fd00fe 00")] // less than encoding length for tlv4
    [InlineData("fd00fe 01 01")]
    [InlineData("fd00fe 03 010101")] // greater than encoding length for tlv4
    [InlineData("00 00")] // unknown even field for n1
    public async Task Given_InvalidN1Stream_When_DeserializedStrict_Then_ThrowsSerializationException(string hex)
    {
        // Arrange
        var bytes = FromHex(hex);

        // Act & Assert
        await Assert.ThrowsAsync<SerializationException>(() => DecodeN1Async(bytes));
    }

    #endregion

    #region TLV Decoding Successes

    [Theory]
    [InlineData("")] // empty message
    [InlineData("21 00")] // unknown odd type
    [InlineData("fd0201 00")]
    [InlineData("fd00fd 00")]
    [InlineData("fd00ff 00")]
    [InlineData("fe02000001 00")]
    [InlineData("ff0200000000000001 00")]
    public async Task Given_ValidStreamInEitherNamespace_When_DeserializedStrict_Then_DecodesAndOnlyUnknownOddRemain(
        string hex)
    {
        // Arrange
        var bytes = FromHex(hex);

        // Act
        var n1 = await DecodeN1Async(bytes);
        var n2 = await DecodeN2Async(bytes);

        // Assert
        Assert.All(n1.GetTlvs(), tlv => Assert.Equal(1UL, tlv.Type.Value % 2));
        Assert.All(n2.GetTlvs(), tlv => Assert.Equal(1UL, tlv.Type.Value % 2));
        Assert.Equal(bytes.Length == 0 ? 0 : 1, n1.GetTlvs().Count());
    }

    [Theory]
    [InlineData("01 00", 0UL)]
    [InlineData("01 01 01", 1UL)]
    [InlineData("01 02 0100", 256UL)]
    [InlineData("01 03 010000", 65536UL)]
    [InlineData("01 04 01000000", 16777216UL)]
    [InlineData("01 05 0100000000", 4294967296UL)]
    [InlineData("01 06 010000000000", 1099511627776UL)]
    [InlineData("01 07 01000000000000", 281474976710656UL)]
    [InlineData("01 08 0100000000000000", 72057594037927936UL)]
    public async Task Given_ValidN1Tlv1_When_DeserializedStrict_Then_AmountMatches(string hex, ulong expectedAmount)
    {
        // Arrange
        var bytes = FromHex(hex);

        // Act
        var stream = await DecodeN1Async(bytes);

        // Assert
        Assert.True(stream.TryGetTlv(1UL, out var tlv));
        Assert.NotNull(tlv);
        Assert.Equal(expectedAmount, ReadU64(tlv.Value));
    }

    [Fact]
    public async Task Given_ValidN1Tlv2_When_DeserializedStrict_Then_ScidMatches()
    {
        // Arrange
        var bytes = FromHex("02 08 0000000000000226");

        // Act
        var stream = await DecodeN1Async(bytes);

        // Assert: scid 0x0x550
        Assert.True(stream.TryGetTlv(2UL, out var tlv));
        Assert.NotNull(tlv);
        Assert.Equal(550UL, ReadU64(tlv.Value));
    }

    [Fact]
    public async Task Given_ValidN1Tlv3_When_DeserializedStrict_Then_ValuesMatch()
    {
        // Arrange
        var bytes = FromHex("03 31 " + ValidNodeId + "00000000000000010000000000000002");

        // Act
        var stream = await DecodeN1Async(bytes);

        // Assert
        Assert.True(stream.TryGetTlv(3UL, out var tlv));
        Assert.NotNull(tlv);
        Assert.Equal(FromHex(ValidNodeId), tlv.Value[..33]);
        Assert.Equal(1UL, ReadU64(tlv.Value[33..41]));
        Assert.Equal(2UL, ReadU64(tlv.Value[41..49]));
    }

    [Fact]
    public async Task Given_ValidN1Tlv4_When_DeserializedStrict_Then_CltvDeltaMatches()
    {
        // Arrange
        var bytes = FromHex("fd00fe 02 0226");

        // Act
        var stream = await DecodeN1Async(bytes);

        // Assert
        Assert.True(stream.TryGetTlv(254UL, out var tlv));
        Assert.NotNull(tlv);
        Assert.Equal(550UL, ReadU64(tlv.Value));
    }

    #endregion

    #region TLV Stream Decoding Failure

    [Theory]
    [InlineData("02 08 0000000000000226 01 01 2a")] // valid records but invalid ordering
    [InlineData("02 08 0000000000000231 02 08 0000000000000451")] // duplicate TLV type
    [InlineData("1f 00 0f 01 2a")] // valid (ignored) records but invalid ordering
    [InlineData("1f 00 1f 01 2a")] // duplicate TLV type (ignored)
    public async Task Given_BadlyOrderedN1Stream_When_DeserializedStrict_Then_ThrowsSerializationException(string hex)
    {
        // Arrange
        var bytes = FromHex(hex);

        // Act & Assert
        await Assert.ThrowsAsync<SerializationException>(() => DecodeN1Async(bytes));
    }

    [Fact]
    public async Task Given_BadlyOrderedN2Stream_When_DeserializedStrict_Then_ThrowsSerializationException()
    {
        // Arrange
        var bytes = FromHex("ffffffffffffffffff 00 00 00");

        // Act & Assert
        await Assert.ThrowsAsync<SerializationException>(() => DecodeN2Async(bytes));
    }

    [Theory]
    [InlineData("fd")]
    [InlineData("0f fd2602")]
    [InlineData("12 00")]
    [InlineData("00 00")]
    [InlineData("01 01 00")]
    public async Task Given_ValidStreamWithInvalidStreamAppended_When_DeserializedStrict_Then_Throws(string hex)
    {
        // Arrange
        var bytes = FromHex("01 00" + hex);

        // Act & Assert
        await Assert.ThrowsAsync<SerializationException>(() => DecodeN1Async(bytes));
    }

    [Fact]
    public async Task Given_HigherValidStreamAppendedToLowerValidStream_When_DeserializedStrict_Then_Decodes()
    {
        // Arrange
        var bytes = FromHex("01 01 01" + "02 08 0000000000000226" + "03 31 " + ValidNodeId
                          + "00000000000000010000000000000002" + "21 00" + "fd00fe 02 0226" + "fd0201 00");

        // Act
        var stream = await DecodeN1Async(bytes);

        // Assert
        Assert.Equal([1UL, 2UL, 3UL, 33UL, 254UL, 513UL], stream.GetTlvs().Select(t => t.Type.Value));
    }

    #endregion

    private async Task<TlvStream> DecodeN1Async(byte[] bytes)
    {
        using var memoryStream = new MemoryStream(bytes);
        var stream = await _tlvStreamSerializer.DeserializeStrictAsync(memoryStream, s_n1KnownTypes);
        ValidateN1(stream);
        return stream;
    }

    private async Task<TlvStream> DecodeN2Async(byte[] bytes)
    {
        using var memoryStream = new MemoryStream(bytes);
        var stream = await _tlvStreamSerializer.DeserializeStrictAsync(memoryStream, s_n2KnownTypes);
        ValidateN2(stream);
        return stream;
    }

    /// <summary>
    /// Stand-in for the n1 namespace's converters: exact lengths and minimal tu64.
    /// </summary>
    /// <remarks>
    /// The node_id check is the compressed-point prefix only; a full on-curve check needs secp256k1, which this
    /// project does not reference.
    /// </remarks>
    private static void ValidateN1(TlvStream stream)
    {
        foreach (var tlv in stream.GetTlvs())
        {
            switch (tlv.Type.Value)
            {
                case 1:
                    ValidateTu(tlv, 8);
                    break;
                case 2 when tlv.Length.Value != 8:
                    throw new SerializationException("tlv2 must be 8 bytes");
                case 3 when tlv.Length.Value != 33 + 8 + 8:
                    throw new SerializationException("tlv3 must be 49 bytes");
                case 3 when tlv.Value[0] is not (0x02 or 0x03):
                    throw new SerializationException("tlv3 node_id is not a valid point");
                case 254 when tlv.Length.Value != 2:
                    throw new SerializationException("tlv4 must be 2 bytes");
            }
        }
    }

    private static void ValidateN2(TlvStream stream)
    {
        foreach (var tlv in stream.GetTlvs())
        {
            switch (tlv.Type.Value)
            {
                case 0:
                    ValidateTu(tlv, 8);
                    break;
                case 11:
                    ValidateTu(tlv, 4);
                    break;
            }
        }
    }

    private static void ValidateTu(BaseTlv tlv, int maxLength)
    {
        if (tlv.Length.Value > (ulong)maxLength)
            throw new SerializationException("truncated integer too long");

        if (tlv.Value.Length > 0 && tlv.Value[0] == 0)
            throw new SerializationException("truncated integer not minimal");
    }

    private static ulong ReadU64(byte[] value)
    {
        ulong result = 0;
        foreach (var b in value)
            result = (result << 8) | b;

        return result;
    }

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty));
}