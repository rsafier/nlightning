namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.ValueObjects;

public class InvalidOnionPayloadFailureFactoryTests
{
    [Theory]
    [InlineData(0UL, 0, "000000")]
    [InlineData(6UL, 0x1234, "061234")]
    [InlineData(0xfcUL, 1, "fc0001")]
    [InlineData(0xfdUL, 2, "fd00fd0002")]
    [InlineData(301UL, 21, "fd012d0015")]
    [InlineData(0x10000UL, 3, "fe000100000003")]
    [InlineData(0x100000000UL, 4, "ff00000001000000000004")]
    public void Given_TypeAndOffset_When_Encoding_Then_DataIsBigSizeTypeAndU16Offset(ulong type, int offset,
        string expectedHex)
    {
        // Act
        var data = InvalidOnionPayloadFailureFactory.EncodeData(new BigSize(type), offset);

        // Assert
        Assert.Equal(Convert.FromHexString(expectedHex), data);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(70_000, ushort.MaxValue)]
    public void Given_OutOfRangeOffset_When_Encoding_Then_OffsetIsClamped(int offset, ushort expectedOffset)
    {
        // Act
        var data = InvalidOnionPayloadFailureFactory.EncodeData(new BigSize(2), offset);

        // Assert
        Assert.True(InvalidOnionPayloadFailureFactory.TryDecodeData(data, out _, out var decodedOffset));
        Assert.Equal(expectedOffset, decodedOffset);
    }

    [Theory]
    [InlineData("061234", 6UL, 0x1234)]
    [InlineData("fd012d0015ffff", 301UL, 21)]
    public void Given_ValidData_When_Decoding_Then_TypeAndOffsetAreReturned(string hex, ulong expectedType,
                                                                          ushort expectedOffset)
    {
        // Act
        var result = InvalidOnionPayloadFailureFactory.TryDecodeData(Convert.FromHexString(hex), out var type,
                                                                     out var offset);

        // Assert
        Assert.True(result);
        Assert.Equal(expectedType, type.Value);
        Assert.Equal(expectedOffset, offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("06")]
    [InlineData("0612")]
    [InlineData("fd00")]
    [InlineData("fd00fc0000")]
    [InlineData("fe0000ffff0000")]
    [InlineData("ff00000000ffffffff0000")]
    public void Given_TruncatedOrNonCanonicalData_When_Decoding_Then_ReturnsFalse(string hex)
    {
        // Act
        var result = InvalidOnionPayloadFailureFactory.TryDecodeData(Convert.FromHexString(hex), out _, out _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Given_InnerException_When_Creating_Then_ExceptionCarriesCodeDataAndInner()
    {
        // Arrange
        var inner = new InvalidCastException("bad");

        // Act
        var exception = InvalidOnionPayloadFailureFactory.Create(new BigSize(8), 7, "msg", inner);

        // Assert
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
        Assert.Same(inner, exception.InnerException);
        Assert.Equal(Convert.FromHexString("080007"), exception.FailureData!.Value.ToArray());
    }
}