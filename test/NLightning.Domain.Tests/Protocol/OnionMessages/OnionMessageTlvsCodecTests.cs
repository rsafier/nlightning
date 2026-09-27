namespace NLightning.Domain.Tests.Protocol.OnionMessages;

using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;

using static OnionMessageVectorValues;

/// <summary>
/// BOLT 4 <c>onionmsg_tlv</c> (OM0-T2, OM-W-04): strict decoding (increasing types, minimal bigsize, unknown even
/// refused, unknown odd kept), encoding in type order and the final-hop payload field count (OM-R-07).
/// </summary>
public class OnionMessageTlvsCodecTests
{
    [Fact]
    public void Given_DaveVectorPayload_When_Decoded_Then_HelloAndEncryptedRecipientData()
    {
        // Arrange
        var bytes = Convert.FromHexString(DaveOnionMessageTlvHex);

        // Act
        var tlvs = OnionMessageTlvsCodec.Decode(bytes);

        // Assert
        Assert.Null(tlvs.ReplyPath);
        Assert.Equal(Convert.FromHexString(Hop3EncryptedRecipientDataHex), tlvs.EncryptedRecipientData!.Value.ToArray());
        var hello = Assert.Single(tlvs.OtherRecords);
        Assert.Equal(1UL, hello.Type);
        Assert.Equal("hello"u8.ToArray(), hello.Value.ToArray());
        Assert.Equal(0, OnionMessageTlvsCodec.CountPayloadFields(tlvs));
        Assert.Equal(bytes, OnionMessageTlvsCodec.Encode(tlvs));
    }

    [Fact]
    public void Given_ReplyPathAndPayloadField_When_RoundTripped_Then_BytesAreIdentical()
    {
        // Arrange: reply_path = the vector's route, erd 0xaabb, invoice_request 0x01, odd 71 empty
        var route = Convert.FromHexString(RouteWireHex);
        var hex = "02fd" + route.Length.ToString("x4") + RouteWireHex + "0402aabb" + "400101" + "4700";
        var bytes = Convert.FromHexString(hex);

        // Act
        var tlvs = OnionMessageTlvsCodec.Decode(bytes);

        // Assert
        Assert.NotNull(tlvs.ReplyPath);
        Assert.Equal(4, tlvs.ReplyPath.Hops.Count);
        Assert.Equal(new byte[] { 0xaa, 0xbb }, tlvs.EncryptedRecipientData!.Value.ToArray());
        Assert.Equal([OnionMessageConstants.InvoiceRequestType, 71UL], tlvs.OtherRecords.Select(r => r.Type));
        Assert.Equal(2, OnionMessageTlvsCodec.CountPayloadFields(tlvs));
        Assert.Equal(bytes, OnionMessageTlvsCodec.Encode(tlvs));
    }

    [Fact]
    public void Given_EmptyStream_When_Decoded_Then_EmptyPayload()
    {
        // Act
        var tlvs = OnionMessageTlvsCodec.Decode([]);

        // Assert
        Assert.Null(tlvs.ReplyPath);
        Assert.Null(tlvs.EncryptedRecipientData);
        Assert.Empty(tlvs.OtherRecords);
        Assert.Empty(OnionMessageTlvsCodec.Encode(tlvs));
    }

    [Theory]
    [InlineData("0400")] // empty encrypted_recipient_data
    [InlineData("4000" + "4200" + "4400")] // invoice_request (64), invoice (66), invoice_error (68)
    [InlineData("0100" + "0300" + "fd01010100")] // unknown odd types 1, 3 and 257
    [InlineData("ff" + "ffffffffffffffff" + "00")] // unknown odd type 2^64-1
    public void Given_WellFormedStream_When_RoundTripped_Then_BytesAreIdentical(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var ok = OnionMessageTlvsCodec.TryDecode(bytes, out var tlvs, out var reason);

        // Assert
        Assert.True(ok, reason);
        Assert.Equal(bytes, OnionMessageTlvsCodec.Encode(tlvs!));
    }

    public static TheoryData<string, string> RefusedStreams => new()
    {
        { "unknown even type 6", "0600" },
        { "unknown even type 70", "4600" },
        { "unknown even type 0", "0000" },
        { "types not increasing", "0402aabb" + "0100" },
        { "duplicate type", "0100" + "0100" },
        { "non-minimal type (fd00 01)", "fd000100" },
        { "non-minimal length (fd00 01)", "01fd000100" },
        { "length past the end", "0103aabb" },
        { "truncated type", "fd01" },
        { "missing length", "01" },
        { "malformed reply_path (num_hops 0)", "0243" + FirstNodeIdHex + FirstPathKeyHex + "00" },
        { "reply_path shorter than a sciddir_or_pubkey", "020100" },
        { "reply_path with trailing bytes", "02fd01d4" + RouteWireHex + "00" }
    };

    [Theory]
    [MemberData(nameof(RefusedStreams))]
    public void Given_MalformedStream_When_Decoded_Then_Refused(string defect, string hex)
    {
        // Act
        var ok = OnionMessageTlvsCodec.TryDecode(Convert.FromHexString(hex), out var tlvs, out var reason);

        // Assert
        Assert.False(ok, defect);
        Assert.Null(tlvs);
        Assert.NotNull(reason);
        Assert.Throws<FormatException>(() => OnionMessageTlvsCodec.Decode(Convert.FromHexString(hex)));
    }

    [Fact]
    public void Given_RecordsOutOfOrder_When_Encoded_Then_WrittenInTypeOrder()
    {
        // Arrange: type 1 must come before reply_path (2) and encrypted_recipient_data (4)
        var tlvs = new OnionMessageTlvs(null, new byte[] { 0xcc },
                                        [
                                            new OnionMessageTlvRecord(65, new byte[] { 0x01 }),
                                            new OnionMessageTlvRecord(1, new byte[] { 0x02 })
                                        ]);

        // Act
        var bytes = OnionMessageTlvsCodec.Encode(tlvs);

        // Assert
        Assert.Equal(Convert.FromHexString("010102" + "0401cc" + "410101"), bytes);
    }

    [Theory]
    [InlineData(OnionMessageConstants.ReplyPathType)]
    [InlineData(OnionMessageConstants.EncryptedRecipientDataType)]
    public void Given_OwnPropertyTypeInOtherRecords_When_Encoded_Then_Throws(ulong type)
    {
        // Arrange
        var tlvs = new OnionMessageTlvs(null, null, [new OnionMessageTlvRecord(type, new byte[] { 0x01 })]);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => OnionMessageTlvsCodec.Encode(tlvs));
    }

    [Fact]
    public void Given_DuplicateOtherRecord_When_Encoded_Then_Throws()
    {
        // Arrange
        var tlvs = new OnionMessageTlvs(null, null,
                                        [
                                            new OnionMessageTlvRecord(64, new byte[] { 0x01 }),
                                            new OnionMessageTlvRecord(64, new byte[] { 0x02 })
                                        ]);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => OnionMessageTlvsCodec.Encode(tlvs));
    }

    [Theory]
    [InlineData(new ulong[] { }, 0)]
    [InlineData(new ulong[] { 1, 3, 63 }, 0)]
    [InlineData(new ulong[] { 64 }, 1)]
    [InlineData(new ulong[] { 66 }, 1)]
    [InlineData(new ulong[] { 68 }, 1)]
    [InlineData(new ulong[] { 65 }, 1)] // an odd test field from 64 up is a payload field too
    [InlineData(new ulong[] { 1, 64, 66 }, 2)]
    [InlineData(new ulong[] { 64, 68, 1001 }, 3)]
    public void Given_Records_When_CountingPayloadFields_Then_TypesFrom64Count(ulong[] types, int expected)
    {
        // Arrange
        var records = types.Select(t => new OnionMessageTlvRecord(t, ReadOnlyMemory<byte>.Empty)).ToList();

        // Act
        var count = OnionMessageTlvsCodec.CountPayloadFields(new OnionMessageTlvs(null, null, records));

        // Assert
        Assert.Equal(expected, count);
    }

    [Fact]
    public void Given_Tlvs_When_ConvertedToContents_Then_OtherRecordsOnly()
    {
        // Arrange
        var tlvs = OnionMessageTlvsCodec.Decode(Convert.FromHexString(DaveOnionMessageTlvHex));

        // Act
        var contents = OnionMessageTlvsCodec.ToContents(tlvs);

        // Assert
        Assert.Same(tlvs.OtherRecords, contents.Records);
    }
}