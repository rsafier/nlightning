namespace NLightning.Bolt11.Tests.Models.TaggedFields;

using Bolt11.Models.TaggedFields;
using Domain.Utils;
using Enums;

public class MinFinalCltvExpiryTaggedFieldTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(31, 1)]
    [InlineData(32, 2)]
    [InlineData(1023, 2)]
    [InlineData(1024, 3)]
    public void Constructor_FromValue_SetsPropertiesCorrectly(ushort expiry, short expectedLength)
    {
        // Act
        var taggedField = new MinFinalCltvExpiryTaggedField(expiry);

        // Assert
        Assert.Equal(TaggedFieldTypes.MinFinalCltvExpiry, taggedField.Type);
        Assert.Equal(expiry, taggedField.Value);
        Assert.Equal(expectedLength, taggedField.Length);
    }

    [Theory]
    [InlineData(1, new byte[] { 0x08 })]
    [InlineData(31, new byte[] { 0xF8 })]
    [InlineData(32, new byte[] { 0x08, 0x00 })]
    [InlineData(1023, new byte[] { 0xFF, 0xC0 })]
    [InlineData(1024, new byte[] { 0x08, 0x00 })]
    public void WriteToBitWriter_WritesCorrectData(ushort expiry, byte[] expectedData)
    {
        // Arrange
        var taggedField = new MinFinalCltvExpiryTaggedField(expiry);
        var bitWriter = new BitWriter(taggedField.Length * 5);

        // Act
        taggedField.WriteToBitWriter(bitWriter);

        // Assert
        var result = bitWriter.ToArray();

        Assert.Equal(expectedData, result);
    }

    [Theory]
    [InlineData(new byte[] { 1 }, 2, new byte[] { 1, 0 })]
    [InlineData(new byte[] { 1, 2 }, 4, new byte[] { 1, 2, 0 })]
    [InlineData(new byte[] { 1, 2, 3 }, 5, new byte[] { 1, 2, 3, 0 })]
    [InlineData(new byte[] { 1, 2, 3, 4 }, 7, new byte[] { 1, 2, 3, 4, 0 })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5 }, 8, new byte[] { 1, 2, 3, 4, 5 })]
    public void FromBitReader_CreatesCorrectlyFromBitReader(byte[] expectedMetadata, short bitLength, byte[] bytes)
    {
        // Arrange
        var bitReader = new BitReader(bytes);

        // Act
        var taggedField = MetadataTaggedField.FromBitReader(bitReader, bitLength);

        // Assert
        Assert.Equal(expectedMetadata, taggedField.Value);
    }

    [Fact]
    public void FromBitReader_ThrowsArgumentException_ForInvalidLength()
    {
        // Arrange
        var buffer = new byte[50];
        var bitReader = new BitReader(buffer);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => MetadataTaggedField.FromBitReader(bitReader, 0));
    }

    [Theory]
    [InlineData(32768, 4)]
    [InlineData(65535, 4)]
    public void Given_ValueNeedingFourGroups_When_RoundTripping_Then_ValueIsPreserved(ushort expiry,
                                                                                     short expectedLength)
    {
        // Arrange
        var taggedField = new MinFinalCltvExpiryTaggedField(expiry);
        var bitWriter = new BitWriter(taggedField.Length * 5);

        // Act
        taggedField.WriteToBitWriter(bitWriter);
        var parsed = MinFinalCltvExpiryTaggedField.FromBitReader(new BitReader(bitWriter.ToArray()),
                                                                 taggedField.Length);

        // Assert
        Assert.Equal(expectedLength, taggedField.Length);
        Assert.NotNull(parsed);
        Assert.Equal(expiry, parsed.Value);
    }

    [Fact]
    public void Given_ValueWiderThan16Bits_When_FromBitReader_Then_ThrowsArgumentException()
    {
        // Arrange
        // 4 groups: 0b10000 00000 00000 00000 = 2^19
        var bitReader = new BitReader([0x80, 0x00, 0x00]);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => MinFinalCltvExpiryTaggedField.FromBitReader(bitReader, 4));
    }

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0)]
    [InlineData(new byte[] { 0x00 }, 1)]
    [InlineData(new byte[] { 0x00, 0x00 }, 2)]
    public void Given_ZeroValue_When_FromBitReader_Then_FieldIsDropped(byte[] bytes, short length)
    {
        // Arrange
        var bitReader = new BitReader(bytes);

        // Act
        var taggedField = MinFinalCltvExpiryTaggedField.FromBitReader(bitReader, length);

        // Assert
        Assert.Null(taggedField);
    }
}