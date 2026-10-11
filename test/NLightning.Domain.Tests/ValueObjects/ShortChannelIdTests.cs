using System.Buffers.Binary;

namespace NLightning.Domain.Tests.ValueObjects;

using Domain.Channels.ValueObjects;

public class ShortChannelIdTests
{
    private const ulong ExpectedShortChannelId = 956714754222915585;
    private const uint ExpectedBlockHeight = 870127;
    private const uint ExpectedTxIndex = 1237;
    private const ushort ExpectedOutputIndex = 1;
    private const string ExpectedString = "870127x1237x1";

    private readonly byte[] _expectedValue = [0x0D, 0x46, 0xEF, 0x00, 0x04, 0xD5, 0x00, 0x01];

    #region Constructor Tests

    [Fact]
    public void Given_ValidParameters_When_ConstructorCalled_Then_PropertiesAreSetCorrectly()
    {
        // Given
        // When
        var shortChannelId = new ShortChannelId(ExpectedBlockHeight, ExpectedTxIndex, ExpectedOutputIndex);

        // Then
        Assert.Equal(ExpectedBlockHeight, shortChannelId.BlockHeight);
        Assert.Equal(ExpectedTxIndex, shortChannelId.TransactionIndex);
        Assert.Equal(ExpectedOutputIndex, shortChannelId.OutputIndex);
        Assert.Equal(_expectedValue, shortChannelId);
    }

    [Fact]
    public void Given_ValidByteArray_When_ConstructorCalled_Then_PropertiesAreExtractedCorrectly()
    {
        // Given
        // When
        var shortChannelId = new ShortChannelId(_expectedValue);

        // Then
        Assert.Equal(ExpectedBlockHeight, shortChannelId.BlockHeight);
        Assert.Equal(ExpectedTxIndex, shortChannelId.TransactionIndex);
        Assert.Equal(ExpectedOutputIndex, shortChannelId.OutputIndex);
        Assert.Equal(_expectedValue, shortChannelId);
    }

    [Fact]
    public void Given_InvalidByteArrayLength_When_ConstructorCalled_Then_ArgumentExceptionIsThrown()
    {
        // Given
        var invalidByteArray = new byte[] { 0x01, 0x02 }; // only 2 bytes, should be 8

        // When / Then
        var exception = Assert.Throws<ArgumentException>(() => new ShortChannelId(invalidByteArray));
        Assert.Contains("ShortChannelId must be 8 bytes", exception.Message);
    }

    [Fact]
    public void Given_ValidUlong_When_ConstructorCalled_Then_PropertiesAreExtractedCorrectly()
    {
        // Given
        // When
        var shortChannelId = new ShortChannelId(ExpectedShortChannelId);

        // Then
        Assert.Equal(ExpectedBlockHeight, shortChannelId.BlockHeight);
        Assert.Equal(ExpectedTxIndex, shortChannelId.TransactionIndex);
        Assert.Equal(ExpectedOutputIndex, shortChannelId.OutputIndex);
        Assert.Equal(_expectedValue, shortChannelId);
    }

    [Theory]
    // BOLT 7 example: 539268x845x1
    [InlineData(0x083A8400034D0001UL, 539268U, 845U, (ushort)1)]
    // Transaction index above 16 bits and output index above 8 bits
    [InlineData(0x0000010012345678UL, 1U, 0x1234U, (ushort)0x5678)]
    [InlineData(0x000001ABCDEF1234UL, 1U, 0xABCDEFU, (ushort)0x1234)]
    // Maximum values for every field
    [InlineData(0xFFFFFFFFFFFFFFFFUL, 0xFFFFFFU, 0xFFFFFFU, (ushort)0xFFFF)]
    public void Given_Ulong_When_ConstructorCalled_Then_FieldsUseBolt7Widths(ulong value, uint expectedBlockHeight,
                                                                             uint expectedTxIndex,
                                                                             ushort expectedOutputIndex)
    {
        // Arrange
        var expectedBytes = new byte[ShortChannelId.Length];
        BinaryPrimitives.WriteUInt64BigEndian(expectedBytes, value);

        // Act
        var shortChannelId = new ShortChannelId(value);

        // Assert
        Assert.Equal(expectedBlockHeight, shortChannelId.BlockHeight);
        Assert.Equal(expectedTxIndex, shortChannelId.TransactionIndex);
        Assert.Equal(expectedOutputIndex, shortChannelId.OutputIndex);
        Assert.Equal(expectedBytes, (byte[])shortChannelId);
        Assert.Equal(new ShortChannelId(expectedBytes), shortChannelId);
        Assert.Equal(new ShortChannelId(expectedBlockHeight, expectedTxIndex, expectedOutputIndex), shortChannelId);
    }

    [Fact]
    public void Given_Bolt7Example_When_ParsedAndConvertedFromUlong_Then_TheyAreEqual()
    {
        // Arrange
        const ulong bolt7Value = 0x083A8400034D0001UL;

        // Act
        var parsed = ShortChannelId.Parse("539268x845x1");
        ShortChannelId fromUlong = bolt7Value;

        // Assert
        Assert.Equal(parsed, fromUlong);
        Assert.Equal("539268x845x1", fromUlong.ToString());
    }

    [Theory]
    [InlineData(0x1000000U, 0U)]
    [InlineData(0U, 0x1000000U)]
    public void Given_FieldAbove24Bits_When_ConstructorCalled_Then_ArgumentOutOfRangeExceptionIsThrown(
        uint blockHeight, uint txIndex)
    {
        // Arrange
        // Act
        // Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShortChannelId(blockHeight, txIndex, 0));
    }

    #endregion

    #region Parse Tests

    [Fact]
    public void Given_ValidString_When_ParseCalled_Then_PropertiesMatch()
    {
        // Given
        // When
        var shortChannelId = ShortChannelId.Parse(ExpectedString);

        // Then
        Assert.Equal(ExpectedBlockHeight, shortChannelId.BlockHeight);
        Assert.Equal(ExpectedTxIndex, shortChannelId.TransactionIndex);
        Assert.Equal(ExpectedOutputIndex, shortChannelId.OutputIndex);
        Assert.Equal(_expectedValue, shortChannelId);
    }

    [Fact]
    public void Given_InvalidStringFormat_When_ParseCalled_Then_FormatExceptionIsThrown()
    {
        // Given
        var invalidString = "this-is-not-valid";

        // When / Then
        Assert.Throws<FormatException>(() => ShortChannelId.Parse(invalidString));
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void Given_ValidShortChannelId_When_ToStringCalled_Then_FormattedCorrectly()
    {
        // Given
        var shortChannelId = new ShortChannelId(ExpectedBlockHeight, ExpectedTxIndex, ExpectedOutputIndex);

        // When
        var result = shortChannelId.ToString();

        // Then
        Assert.Equal(ExpectedString, result);
    }

    #endregion

    #region Equality Tests

    [Fact]
    public void Given_TwoIdenticalShortChannelIds_When_ComparingEquality_Then_TheyAreEqual()
    {
        // Given
        var scid1 = new ShortChannelId(123, 45, 6);
        var scid2 = new ShortChannelId(123, 45, 6);

        // When
        var areEqual = scid1 == scid2;

        // Then
        Assert.True(areEqual);
        Assert.True(scid1.Equals(scid2));
        Assert.Equal(scid1.GetHashCode(), scid2.GetHashCode());
    }

    [Fact]
    public void Given_TwoDifferentShortChannelIds_When_ComparingEquality_Then_TheyAreNotEqual()
    {
        // Given
        var scid1 = new ShortChannelId(123, 45, 6);
        var scid2 = new ShortChannelId(321, 54, 6);

        // When
        var areEqual = scid1 == scid2;

        // Then
        Assert.False(areEqual);
        Assert.False(scid1.Equals(scid2));
    }

    #endregion
}