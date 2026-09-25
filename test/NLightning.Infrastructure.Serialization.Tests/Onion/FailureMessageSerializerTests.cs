using System.Buffers.Binary;
using System.Runtime.Serialization;

namespace NLightning.Infrastructure.Serialization.Tests.Onion;

using Domain.Money;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Serialization.Onion;

public class FailureMessageSerializerTests
{
    private static readonly byte[] s_sha256OfOnion = Enumerable.Repeat((byte)0x5a, 32).ToArray();
    private static readonly byte[] s_channelUpdate = Enumerable.Range(0, 136).Select(i => (byte)i).ToArray();

    private readonly FailureMessageSerializer _serializer = new();

    /// <summary>
    /// One message per BOLT 4 failure code, with the expected <c>failuremsg</c> hex.
    /// </summary>
    public static TheoryData<FailureCode, string> AllCodes => new()
    {
        { FailureCode.TemporaryNodeFailure, "2002" },
        { FailureCode.PermanentNodeFailure, "6002" },
        { FailureCode.RequiredNodeFeatureMissing, "6003" },
        { FailureCode.InvalidOnionVersion, "c004" + Hex(s_sha256OfOnion) },
        { FailureCode.InvalidOnionHmac, "c005" + Hex(s_sha256OfOnion) },
        { FailureCode.InvalidOnionKey, "c006" + Hex(s_sha256OfOnion) },
        { FailureCode.TemporaryChannelFailure, "1007" + "0088" + Hex(s_channelUpdate) },
        { FailureCode.PermanentChannelFailure, "4008" },
        { FailureCode.RequiredChannelFeatureMissing, "4009" },
        { FailureCode.UnknownNextPeer, "400a" },
        { FailureCode.AmountBelowMinimum, "100b" + "00000000000003e8" + "0088" + Hex(s_channelUpdate) },
        { FailureCode.FeeInsufficient, "100c" + "00000000000007d0" + "0000" },
        { FailureCode.IncorrectCltvExpiry, "100d" + "000c3500" + "0088" + Hex(s_channelUpdate) },
        { FailureCode.ExpiryTooSoon, "100e" + "0000" },
        { FailureCode.IncorrectOrUnknownPaymentDetails, "400f" + "0000000000000064" + "000c3500" },
        { FailureCode.FinalIncorrectCltvExpiry, "0012" + "00000090" },
        { FailureCode.FinalIncorrectHtlcAmount, "0013" + "0000000000002710" },
        { FailureCode.ChannelDisabled, "1014" + "0000" + "0088" + Hex(s_channelUpdate) },
        { FailureCode.ExpiryTooFar, "0015" },
        { FailureCode.InvalidOnionPayload, "4016" + "fd012d" + "0015" },
        { FailureCode.MppTimeout, "0017" },
        { FailureCode.InvalidOnionBlinding, "c018" + Hex(s_sha256OfOnion) }
    };

    [Fact]
    public void Given_AllCodes_When_Enumerating_Then_EveryDefinedCodeIsCovered()
    {
        // Act
        var covered = AllCodes.Select(row => row.Data.Item1).ToHashSet();

        // Assert
        Assert.Equal(Enum.GetValues<FailureCode>().ToHashSet(), covered);
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Given_EveryCode_When_Serializing_Then_BytesMatchBolt4Layout(FailureCode code, string expectedHex)
    {
        // Arrange
        var message = Create(code);

        // Act
        var bytes = _serializer.Serialize(message);

        // Assert
        Assert.Equal(expectedHex, Hex(bytes));
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Given_EveryCode_When_RoundTripping_Then_MessageIsPreserved(FailureCode code, string hex)
    {
        // Act
        var message = _serializer.Deserialize(Convert.FromHexString(hex));

        // Assert
        Assert.Equal(code, message.Code);
        Assert.Null(message.Extension);
        Assert.Equal(Create(code).Data.ToArray(), message.Data.ToArray());
        Assert.Equal(hex, Hex(_serializer.Serialize(message)));
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Given_EveryCodeWithTlvExtension_When_RoundTripping_Then_ExtensionIsPreserved(FailureCode code,
                                                                                            string hex)
    {
        // Arrange: 34001 (0xfd84d1) as in the BOLT 4 trace, plus a small odd type
        var extension = new TlvStream();
        extension.Add(new BaseTlv(new BigSize(1), [0x01, 0x02]),
                      new BaseTlv(new BigSize(34001), Enumerable.Repeat((byte)0x80, 300).ToArray()));
        var message = Create(code).WithExtension(extension);
        var expectedHex = hex + "01020102" + "fd84d1fd012c" + string.Concat(Enumerable.Repeat("80", 300));

        // Act
        var bytes = _serializer.Serialize(message);
        var parsed = _serializer.Deserialize(bytes);

        // Assert
        Assert.Equal(expectedHex, Hex(bytes));
        Assert.Equal(code, parsed.Code);
        Assert.NotNull(parsed.Extension);
        Assert.Equal(extension.GetTlvs(), parsed.Extension.GetTlvs());
        Assert.Equal(expectedHex, Hex(_serializer.Serialize(parsed)));
    }

    [Theory]
    [InlineData("400f0000000000000064000c3500ff")] // truncated TLV type
    [InlineData("400f0000000000000064000c3500030501")] // TLV length past the end
    [InlineData("400f0000000000000064000c3500050103010101")] // types not increasing
    [InlineData("400f0000000000000064000c3500fd000301aa")] // non-canonical bigsize type
    [InlineData("400f0000000000000064000c3500020100")] // unknown even type
    [InlineData("400f0000000000000064000c350000")] // a single zero byte (type 0, missing length)
    public void Given_ExtraBytesThatAreNotAValidTlvStream_When_Deserializing_Then_TheyAreIgnored(string hex)
    {
        // Act
        var message = _serializer.Deserialize(Convert.FromHexString(hex));

        // Assert: BOLT 4 origin MUST ignore extra bytes in failuremsg
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, message.Code);
        Assert.Equal(LightningMoney.MilliSatoshis(100UL), message.HtlcAmount);
        Assert.Equal(800_000u, message.Height);
        Assert.Null(message.Extension);
    }

    [Fact]
    public void Given_UnknownCode_When_RoundTripping_Then_AllBytesAreData()
    {
        // Arrange
        const string hex = "7777010203";

        // Act
        var message = _serializer.Deserialize(Convert.FromHexString(hex));

        // Assert
        Assert.Equal((FailureCode)0x7777, message.Code);
        Assert.False(message.IsKnownCode);
        Assert.Equal("010203", Hex(message.Data.Span));
        Assert.Null(message.Extension);
        Assert.Equal(hex, Hex(_serializer.Serialize(message)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("20")]
    [InlineData("c005aabb")] // sha256_of_onion truncated
    [InlineData("100700ff")] // channel_update shorter than len
    [InlineData("400f0000000000000064")] // height missing
    [InlineData("4016fd")] // bigsize type truncated
    [InlineData("0012000090")] // cltv_expiry truncated
    public void Given_TruncatedKnownData_When_Deserializing_Then_ThrowsAndTryReturnsFalse(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act / Assert
        Assert.Throws<SerializationException>(() => _serializer.Deserialize(bytes));
        Assert.False(_serializer.TryDeserialize(bytes, out var message));
        Assert.Null(message);
    }

    [Fact]
    public void Given_ShortMessage_When_SerializingErrorPayload_Then_PaddedTo256()
    {
        // Act
        var payload = _serializer.SerializeErrorPayload(FailureMessage.TemporaryNodeFailure());

        // Assert: 0002 2002 00fe || 254 zero bytes (onion-error-test.json hops[4].payload)
        Assert.Equal(2 + 2 + 2 + 254, payload.Length);
        Assert.Equal("0002200200fe", Hex(payload.AsSpan(0, 6)));
        Assert.All(payload.Skip(6), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Given_MessageLongerThanMinimum_When_SerializingErrorPayload_Then_PadIsEmpty()
    {
        // Arrange: 2 + 8 + 2 + 300 = 312 bytes
        var message = FailureMessage.AmountBelowMinimum(LightningMoney.MilliSatoshis(1UL), new byte[300]);

        // Act
        var payload = _serializer.SerializeErrorPayload(message);

        // Assert
        Assert.Equal(2 + 312 + 2, payload.Length);
        Assert.Equal(312, BinaryPrimitives.ReadUInt16BigEndian(payload));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2 + 312)));
    }

    [Fact]
    public void Given_LargerMinimum_When_SerializingErrorPayload_Then_FailureLenPlusPadLenEqualsIt()
    {
        // Act
        var payload = _serializer.SerializeErrorPayload(FailureMessage.MppTimeout(), 1024);

        // Assert
        Assert.Equal(1024 + 4, payload.Length);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(payload));
        Assert.Equal(1022, BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4)));
    }

    [Fact]
    public void Given_MinimumBelow256_When_SerializingErrorPayload_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _serializer.SerializeErrorPayload(FailureMessage.MppTimeout(), OnionConstants.MinFailurePadLength - 1));
    }

    [Fact]
    public void Given_PacketOver32768_When_SerializingErrorPayload_Then_Throws()
    {
        // Arrange: the largest message that fits, then one byte more
        var fits = FailureMessage.TemporaryChannelFailure(
            new byte[FailureMessageSerializer.MaxFailureMessageLength - 4]);
        var tooBig = FailureMessage.TemporaryChannelFailure(
            new byte[FailureMessageSerializer.MaxFailureMessageLength - 3]);

        // Act
        var payload = _serializer.SerializeErrorPayload(fits);

        // Assert
        Assert.Equal(OnionConstants.MaxErrorPacketLength - OnionConstants.HmacLength, payload.Length);
        Assert.Throws<ArgumentException>(() => _serializer.SerializeErrorPayload(tooBig));
        Assert.Throws<ArgumentException>(() =>
            _serializer.SerializeErrorPayload(FailureMessage.MppTimeout(), OnionConstants.MaxErrorPacketLength));
    }

    [Fact]
    public void Given_FramedPayload_When_ReadingErrorPayload_Then_FailureMessageIsExtracted()
    {
        // Arrange
        var message = FailureMessage.FinalIncorrectCltvExpiry(144);
        var payload = _serializer.SerializeErrorPayload(message);

        // Act
        var ok = _serializer.TryReadErrorPayload(payload, out var failureMessage);

        // Assert
        Assert.True(ok);
        Assert.Equal("001200000090", Hex(failureMessage.Span));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("0003aabb")]
    public void Given_BadFraming_When_ReadingErrorPayload_Then_ReturnsFalse(string hex)
    {
        // Act
        var ok = _serializer.TryReadErrorPayload(Convert.FromHexString(hex), out var failureMessage);

        // Assert
        Assert.False(ok);
        Assert.True(failureMessage.IsEmpty);
    }

    [Fact]
    public void Given_PayloadWithoutPadLength_When_ReadingErrorPayload_Then_PaddingIsNotChecked()
    {
        // Act
        var ok = _serializer.TryReadErrorPayload(Convert.FromHexString("00022002"), out var failureMessage);

        // Assert
        Assert.True(ok);
        Assert.Equal("2002", Hex(failureMessage.Span));
    }

    private static FailureMessage Create(FailureCode code)
    {
        return code switch
        {
            FailureCode.TemporaryNodeFailure => FailureMessage.TemporaryNodeFailure(),
            FailureCode.PermanentNodeFailure => FailureMessage.PermanentNodeFailure(),
            FailureCode.RequiredNodeFeatureMissing => FailureMessage.RequiredNodeFeatureMissing(),
            FailureCode.InvalidOnionVersion => FailureMessage.InvalidOnionVersion(s_sha256OfOnion),
            FailureCode.InvalidOnionHmac => FailureMessage.InvalidOnionHmac(s_sha256OfOnion),
            FailureCode.InvalidOnionKey => FailureMessage.InvalidOnionKey(s_sha256OfOnion),
            FailureCode.TemporaryChannelFailure => FailureMessage.TemporaryChannelFailure(s_channelUpdate),
            FailureCode.PermanentChannelFailure => FailureMessage.PermanentChannelFailure(),
            FailureCode.RequiredChannelFeatureMissing => FailureMessage.RequiredChannelFeatureMissing(),
            FailureCode.UnknownNextPeer => FailureMessage.UnknownNextPeer(),
            FailureCode.AmountBelowMinimum =>
                FailureMessage.AmountBelowMinimum(LightningMoney.MilliSatoshis(1000UL), s_channelUpdate),
            FailureCode.FeeInsufficient => FailureMessage.FeeInsufficient(LightningMoney.MilliSatoshis(2000UL)),
            FailureCode.IncorrectCltvExpiry => FailureMessage.IncorrectCltvExpiry(800_000, s_channelUpdate),
            FailureCode.ExpiryTooSoon => FailureMessage.ExpiryTooSoon(),
            FailureCode.IncorrectOrUnknownPaymentDetails =>
                FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(100UL), 800_000),
            FailureCode.FinalIncorrectCltvExpiry => FailureMessage.FinalIncorrectCltvExpiry(144),
            FailureCode.FinalIncorrectHtlcAmount =>
                FailureMessage.FinalIncorrectHtlcAmount(LightningMoney.MilliSatoshis(10_000UL)),
            FailureCode.ChannelDisabled => FailureMessage.ChannelDisabled(0, s_channelUpdate),
            FailureCode.ExpiryTooFar => FailureMessage.ExpiryTooFar(),
            FailureCode.InvalidOnionPayload => FailureMessage.InvalidOnionPayload(new BigSize(301), 21),
            FailureCode.MppTimeout => FailureMessage.MppTimeout(),
            FailureCode.InvalidOnionBlinding => FailureMessage.InvalidOnionBlinding(s_sha256OfOnion),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
        };
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
}