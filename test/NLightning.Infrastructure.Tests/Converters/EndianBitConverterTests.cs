namespace NLightning.Infrastructure.Tests.Converters;

using Infrastructure.Converters;

public class EndianBitConverterTests
{
    #region ULong

    [Fact]
    public void Given_UlongValue_When_ConvertedToBigEndianBytes_Then_ReturnsCorrectByteArray()
    {
        // Given
        const ulong value = 0x0123;

        // When
        var result = EndianBitConverter.GetBytesBigEndian(value);

        // Then
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x23 }, result);
    }

    [Fact]
    public void Given_UlongValue_When_ConvertedToLittleEndianBytes_Then_ReturnsCorrectByteArray()
    {
        // Given
        const ulong value = 0x0123;

        // When
        var result = EndianBitConverter.GetBytesLittleEndian(value);

        // Then
        Assert.Equal(new byte[] { 0x23, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, result);
    }

    [Fact]
    public void Given_BigEndianBytes_When_ConvertedToUlong_Then_ReturnsCorrectValue()
    {
        // Given
        var bytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x23 };

        // When
        var result = EndianBitConverter.ToUInt64BigEndian(bytes);

        // Then
        Assert.Equal(0x0123UL, result);
    }

    [Fact]
    public void Given_LittleEndianBytes_When_ConvertedToUlong_Then_ReturnsCorrectValue()
    {
        // Given
        var bytes = new byte[] { 0x23, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        // When
        var result = EndianBitConverter.ToUInt64LittleEndian(bytes);

        // Then
        Assert.Equal(0x0123UL, result);
    }

    [Fact]
    public void Given_ShortByteArray_When_NotPaddedToUInt64BigEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = new byte[] { 0x01, 0x23 };

        // When & Then
        Assert.Throws<ArgumentException>(() => EndianBitConverter.ToUInt64BigEndian(bytes));
    }

    [Fact]
    public void Given_ShortByteArray_When_NotPaddedToUInt64LittleEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = new byte[] { 0x01, 0x23 };

        // When & Then
        Assert.Throws<ArgumentException>(() => EndianBitConverter.ToUInt64LittleEndian(bytes));
    }

    [Fact]
    public void Given_EmptyByteArray_When_ConvertedToUInt64BigEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = Array.Empty<byte>();

        // When & Then
        Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ToUInt64BigEndian(bytes));
    }

    [Fact]
    public void Given_EmptyByteArray_When_ConvertedToUInt64LittleEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = Array.Empty<byte>();

        // When & Then
        Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ToUInt64LittleEndian(bytes));
    }

    #endregion

    #region Long

    [Fact]
    public void Given_LongValue_When_ConvertedToBigEndianBytes_Then_ReturnsCorrectByteArray()
    {
        // Given
        const long value = 0x0123;

        // When
        var result = EndianBitConverter.GetBytesBigEndian(value);

        // Then
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x23 }, result);
    }

    [Fact]
    public void Given_LongValue_When_ConvertedToLittleEndianBytes_Then_ReturnsCorrectByteArray()
    {
        // Given
        const long value = 0x0123;

        // When
        var result = EndianBitConverter.GetBytesLittleEndian(value);

        // Then
        Assert.Equal(new byte[] { 0x23, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, result);
    }

    [Fact]
    public void Given_BigEndianBytes_When_ConvertedToLong_Then_ReturnsCorrectValue()
    {
        // Given
        var bytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x23 };

        // When
        var result = EndianBitConverter.ToInt64BigEndian(bytes);

        // Then
        Assert.Equal(0x0123L, result);
    }

    [Fact]
    public void Given_LittleEndianBytes_When_ConvertedToLong_Then_ReturnsCorrectValue()
    {
        // Given
        var bytes = new byte[] { 0x23, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        // When
        var result = EndianBitConverter.ToInt64LittleEndian(bytes);

        // Then
        Assert.Equal(0x0123L, result);
    }

    [Fact]
    public void Given_ShortByteArray_When_NotPaddedToInt64BigEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = new byte[] { 0x01, 0x23 };

        // When & Then
        Assert.Throws<ArgumentException>(() => EndianBitConverter.ToInt64BigEndian(bytes));
    }

    [Fact]
    public void Given_ShortByteArray_When_NotPaddedToInt64LittleEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = new byte[] { 0x01, 0x23 };

        // When & Then
        Assert.Throws<ArgumentException>(() => EndianBitConverter.ToInt64LittleEndian(bytes));
    }

    [Fact]
    public void Given_EmptyByteArray_When_ConvertedToInt64BigEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = Array.Empty<byte>();

        // When & Then
        Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ToInt64BigEndian(bytes));
    }

    [Fact]
    public void Given_EmptyByteArray_When_ConvertedToInt64LittleEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = Array.Empty<byte>();

        // When & Then
        Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ToInt64LittleEndian(bytes));
    }

    #endregion

    #region UInt

    [Fact]
    public void Given_UintValue_When_ConvertedToBigEndianBytes_Then_ReturnsCorrectByteArray()
    {
        // Given
        const uint value = 0x0123;

        // When
        var result = EndianBitConverter.GetBytesBigEndian(value);

        // Then
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x23 }, result);
    }

    [Fact]
    public void Given_UintValue_When_ConvertedToLittleEndianBytes_Then_ReturnsCorrectByteArray()
    {
        // Given
        const uint value = 0x0123;

        // When
        var result = EndianBitConverter.GetBytesLittleEndian(value);

        // Then
        Assert.Equal(new byte[] { 0x23, 0x01, 0x00, 0x00 }, result);
    }

    [Fact]
    public void Given_BigEndianBytes_When_ConvertedToUint_Then_ReturnsCorrectValue()
    {
        // Given
        var bytes = new byte[] { 0x00, 0x00, 0x01, 0x23 };

        // When
        var result = EndianBitConverter.ToUInt32BigEndian(bytes);

        // Then
        Assert.Equal(0x0123U, result);
    }

    [Fact]
    public void Given_LittleEndianBytes_When_ConvertedToUint_Then_ReturnsCorrectValue()
    {
        // Given
        var bytes = new byte[] { 0x23, 0x01, 0x00, 0x00 };

        // When
        var result = EndianBitConverter.ToUInt32LittleEndian(bytes);

        // Then
        Assert.Equal(0x0123U, result);
    }

    [Fact]
    public void Given_ShortByteArray_When_NotPaddedToUInt32BigEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = new byte[] { 0x01, 0x23 };

        // When & Then
        Assert.Throws<ArgumentException>(() => EndianBitConverter.ToUInt32BigEndian(bytes));
    }

    [Fact]
    public void Given_ShortByteArray_When_NotPaddedToUInt32LittleEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = new byte[] { 0x01, 0x23 };

        // When & Then
        Assert.Throws<ArgumentException>(() => EndianBitConverter.ToUInt32LittleEndian(bytes));
    }

    [Fact]
    public void Given_EmptyByteArray_When_ConvertedToUInt32BigEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = Array.Empty<byte>();

        // When & Then
        Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ToUInt32BigEndian(bytes));
    }

    [Fact]
    public void Given_EmptyByteArray_When_ConvertedToUInt32LittleEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = Array.Empty<byte>();

        // When & Then
        Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ToUInt32LittleEndian(bytes));
    }

    #endregion

    #region Int

    [Fact]
    public void Given_IntValue_When_ConvertedToBigEndianBytes_Then_ReturnsCorrectByteArray()
    {
        // Given
        const int value = 0x0123;

        // When
        var result = EndianBitConverter.GetBytesBigEndian(value);

        // Then
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x23 }, result);
    }

    [Fact]
    public void Given_IntValue_When_ConvertedToLittleEndianBytes_Then_ReturnsCorrectByteArray()
    {
        // Given
        const int value = 0x0123;

        // When
        var result = EndianBitConverter.GetBytesLittleEndian(value);

        // Then
        Assert.Equal(new byte[] { 0x23, 0x01 }, result);
    }

    [Fact]
    public void Given_BigEndianBytes_When_ConvertedToInt_Then_ReturnsCorrectValue()
    {
        // Given
        var bytes = new byte[] { 0x00, 0x00, 0x01, 0x23 };

        // When
        var result = EndianBitConverter.ToInt32BigEndian(bytes);

        // Then
        Assert.Equal(0x0123, result);
    }

    [Fact]
    public void Given_LittleEndianBytes_When_ConvertedToInt_Then_ReturnsCorrectValue()
    {
        // Given
        var bytes = new byte[] { 0x23, 0x01, 0x00, 0x00 };

        // When
        var result = EndianBitConverter.ToInt32LittleEndian(bytes);

        // Then
        Assert.Equal(0x0123, result);
    }

    [Fact]
    public void Given_ShortByteArray_When_NotPaddedToInt32BigEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = new byte[] { 0x01, 0x23 };

        // When & Then
        Assert.Throws<ArgumentException>(() => EndianBitConverter.ToInt32BigEndian(bytes));
    }

    [Fact]
    public void Given_ShortByteArray_When_NotPaddedToInt32LittleEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = new byte[] { 0x01, 0x23 };

        // When & Then
        Assert.Throws<ArgumentException>(() => EndianBitConverter.ToInt32LittleEndian(bytes));
    }

    [Fact]
    public void Given_EmptyByteArray_When_ConvertedToInt32BigEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = Array.Empty<byte>();

        // When & Then
        Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ToInt32BigEndian(bytes));
    }

    [Fact]
    public void Given_EmptyByteArray_When_ConvertedToInt32LittleEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = Array.Empty<byte>();

        // When & Then
        Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ToInt32LittleEndian(bytes));
    }

    #endregion

    #region UShort

    [Fact]
    public void Given_UshortValue_When_ConvertedToBigEndianBytes_Then_ReturnsCorrectByteArray()
    {
        // Given
        const ushort value = 0x01;

        // When
        var result = EndianBitConverter.GetBytesBigEndian(value);

        // Then
        Assert.Equal(new byte[] { 0x00, 0x01 }, result);
    }

    [Fact]
    public void Given_UshortValue_When_ConvertedToLittleEndianBytes_Then_ReturnsCorrectByteArray()
    {
        // Given
        const ushort value = 0x01;

        // When
        var result = EndianBitConverter.GetBytesLittleEndian(value);

        // Then
        Assert.Equal(new byte[] { 0x01, 0x00 }, result);
    }

    [Fact]
    public void Given_BigEndianBytes_When_ConvertedToUshort_Then_ReturnsCorrectValue()
    {
        // Given
        var bytes = new byte[] { 0x00, 0x01 };

        // When
        var result = EndianBitConverter.ToUInt16BigEndian(bytes);

        // Then
        Assert.Equal((ushort)0x01, result);
    }

    [Fact]
    public void Given_LittleEndianBytes_When_ConvertedToUshort_Then_ReturnsCorrectValue()
    {
        // Given
        var bytes = new byte[] { 0x01, 0x00 };

        // When
        var result = EndianBitConverter.ToUInt16LittleEndian(bytes);

        // Then
        Assert.Equal((ushort)0x01, result);
    }

    [Fact]
    public void Given_ShortByteArray_When_NotPaddedToUInt16BigEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = new byte[] { 0x01 };

        // When & Then
        Assert.Throws<ArgumentException>(() => EndianBitConverter.ToUInt16BigEndian(bytes));
    }

    [Fact]
    public void Given_ShortByteArray_When_NotPaddedToUInt16LittleEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = new byte[] { 0x01 };

        // When & Then
        Assert.Throws<ArgumentException>(() => EndianBitConverter.ToUInt16LittleEndian(bytes));
    }

    [Fact]
    public void Given_EmptyByteArray_When_ConvertedToUInt16BigEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = Array.Empty<byte>();

        // When & Then
        Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ToUInt16BigEndian(bytes));
    }

    [Fact]
    public void Given_EmptyByteArray_When_ConvertedToUInt16LittleEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = Array.Empty<byte>();

        // When & Then
        Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ToUInt16LittleEndian(bytes));
    }

    #endregion

    #region Short

    [Fact]
    public void Given_ShortValue_When_ConvertedToBigEndianBytes_Then_ReturnsCorrectByteArray()
    {
        // Given
        const short value = 0x01;

        // When
        var result = EndianBitConverter.GetBytesBigEndian(value);

        // Then
        Assert.Equal(new byte[] { 0x00, 0x01 }, result);
    }

    [Fact]
    public void Given_BigEndianBytes_When_ConvertedToShort_Then_ReturnsCorrectValue()
    {
        // Given
        var bytes = new byte[] { 0x00, 0x01 };

        // When
        var result = EndianBitConverter.ToInt16BigEndian(bytes);

        // Then
        Assert.Equal((short)0x01, result);
    }

    [Fact]
    public void Given_LittleEndianBytes_When_ConvertedToShort_Then_ReturnsCorrectValue()
    {
        // Given
        var bytes = new byte[] { 0x01, 0x00 };

        // When
        var result = EndianBitConverter.ToInt16LittleEndian(bytes);

        // Then
        Assert.Equal((short)0x01, result);
    }

    [Fact]
    public void Given_ShortByteArray_When_NotPaddedToInt16BigEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = new byte[] { 0x01 };

        // When & Then
        Assert.Throws<ArgumentException>(() => EndianBitConverter.ToInt16BigEndian(bytes));
    }

    [Fact]
    public void Given_ShortByteArray_When_NotPaddedToInt16LittleEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = new byte[] { 0x01 };

        // When & Then
        Assert.Throws<ArgumentException>(() => EndianBitConverter.ToInt16LittleEndian(bytes));
    }

    [Fact]
    public void Given_EmptyByteArray_When_ConvertedToInt16BigEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = Array.Empty<byte>();

        // When & Then
        Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ToInt16BigEndian(bytes));
    }

    [Fact]
    public void Given_EmptyByteArray_When_ConvertedToInt16LittleEndian_Then_ThrowsArgumentException()
    {
        // Given
        var bytes = Array.Empty<byte>();

        // When & Then
        Assert.Throws<ArgumentOutOfRangeException>(() => EndianBitConverter.ToInt16LittleEndian(bytes));
    }

    #endregion
}