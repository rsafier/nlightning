namespace NLightning.Domain.Tests.Payments.Keysend;

using Domain.Payments.Keysend;

public class CustomRecordCodecTests
{
    [Fact]
    public void Given_UnsortedRecords_When_EncodingAndDecoding_Then_SortedRoundTripWithCanonicalBigSizes()
    {
        // Arrange
        CustomRecord[] records =
        [
            new(7629169, "podcast"u8),
            new(65536, [0xab, 0xcd]),
            new(uint.MaxValue + 1UL, []),
            new(133773310, [0x01])
        ];

        // Act
        var bytes = CustomRecordCodec.Encode(records);
        var decoded = CustomRecordCodec.Decode(bytes);

        // Assert: fe for types below 2^32, ff above; lengths 1 byte
        Assert.Equal("fe0001000002abcd" + "fe0074697107706f6463617374" + "fe07f937fe0101" + "ff000000010000000000",
                     Convert.ToHexStringLower(bytes));
        Assert.Equal(records.OrderBy(r => r.Type), decoded);
    }

    [Fact]
    public void Given_NoRecords_When_Encoding_Then_EmptyAndDecodesToEmpty()
    {
        // Act
        var bytes = CustomRecordCodec.Encode(null);

        // Assert
        Assert.Empty(bytes);
        Assert.Empty(CustomRecordCodec.Decode(bytes));
    }

    [Theory]
    [InlineData(65535UL)]
    [InlineData(1UL)]
    [InlineData(CustomRecordCodec.KeysendPreimageType)]
    public void Given_ForbiddenType_When_Validating_Then_ArgumentException(ulong type)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => CustomRecordCodec.Validate([new CustomRecord(type, [0x01])]));
    }

    [Fact]
    public void Given_DuplicateType_When_Validating_Then_ArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => CustomRecordCodec.Validate([
            new CustomRecord(65537, [0x01]), new CustomRecord(65537, [0x02])
        ]));
    }

    [Theory]
    [InlineData("fe00010000")] // truncated length
    [InlineData("fe0001000005abcd")] // value longer than the bytes left
    [InlineData("fe0001000100fe0001000000")] // types not increasing
    [InlineData("fd100000")] // type below 65536
    [InlineData("fe0000ffff00")] // non-canonical bigsize (fits fd)
    [InlineData("fe000100")] // truncated bigsize
    public void Given_MalformedBytes_When_Decoding_Then_FormatException(string hex)
    {
        // Act & Assert
        Assert.Throws<FormatException>(() => CustomRecordCodec.Decode(Convert.FromHexString(hex)));
    }

    [Fact]
    public void Given_TwoRecordsWithSameBytes_When_Comparing_Then_EqualByValue()
    {
        // Arrange
        var a = new CustomRecord(65537, [0x01, 0x02]);
        var b = new CustomRecord(65537, new byte[] { 0x01, 0x02 });

        // Assert
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new CustomRecord(65537, [0x01]));
        Assert.Equal("65537=0102", a.ToString());
    }
}