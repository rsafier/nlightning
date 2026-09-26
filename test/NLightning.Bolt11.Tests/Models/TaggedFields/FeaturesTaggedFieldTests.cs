namespace NLightning.Bolt11.Tests.Models.TaggedFields;

using Bolt11.Models.TaggedFields;
using Domain.Node;
using Domain.Utils;
using Enums;

public class FeaturesTaggedFieldTests
{
    [Theory]
    [InlineData(new byte[] { 8, 14 }, 3)]
    [InlineData(new byte[] { 9, 15 }, 4)]
    [InlineData(new byte[] { 8, 14, 48 }, 10)]
    [InlineData(new byte[] { 8, 14, 99 }, 20)]
    public void Constructor_FromValue_SetsPropertiesCorrectly(byte[] featureBits, short expectedLength)
    {
        // Arrange
        var features = FeatureSet.DeserializeFromBytes([0x00]);
        foreach (var featureBit in featureBits)
        {
            features.SetFeature(featureBit, true);
        }

        // Act
        var taggedField = new FeaturesTaggedField(features);

        // Assert
        Assert.Equal(TaggedFieldTypes.Features, taggedField.Type);
        Assert.True(features.IsCompatible(taggedField.Value, out var _));
        Assert.Equal(expectedLength, taggedField.Length);
    }

    [Theory]
    // BOLT 11 example `9qrsgq`: b100000100000000 = bits 14 and 8
    [InlineData(new byte[] { 8, 14 }, new byte[] { 0x82, 0x00 })]
    [InlineData(new byte[] { 9, 15 }, new byte[] { 0x08, 0x20, 0x00 })]
    [InlineData(new byte[] { 8, 14, 48 }, new byte[] { 0x40, 0x00, 0x00, 0x00, 0x10, 0x40, 0x00 })]
    [InlineData(new byte[] { 8, 14, 99 },
                new byte[] { 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x10, 0x00 })]
    public void WriteToBitWriter_WritesCorrectData(byte[] featureBits, byte[] expectedData)
    {
        // Arrange
        var features = FeatureSet.DeserializeFromBytes([0x00]);
        foreach (var featureBit in featureBits)
        {
            features.SetFeature(featureBit, true);
        }

        var taggedField = new FeaturesTaggedField(features);
        var bitWriter = new BitWriter(taggedField.Length * 5);

        // Act
        taggedField.WriteToBitWriter(bitWriter);

        // Assert
        var result = bitWriter.ToArray();

        Assert.Equal(expectedData, result);
    }

    [Theory]
    [InlineData(new byte[] { 8, 14 }, 3, new byte[] { 0x82, 0x00 })]
    [InlineData(new byte[] { 9, 15 }, 4, new byte[] { 0x08, 0x20, 0x00 })]
    [InlineData(new byte[] { 8, 14, 48 }, 10, new byte[] { 0x40, 0x00, 0x00, 0x00, 0x10, 0x40, 0x00 })]
    [InlineData(new byte[] { 8, 14, 99 }, 20,
                new byte[] { 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x10, 0x00 })]
    public void FromBitReader_CreatesCorrectlyFromBitReader(byte[] featureBits, short bitLength, byte[] bytes)
    {
        // Arrange
        var bitReader = new BitReader(bytes);

        // Act
        var taggedField = FeaturesTaggedField.FromBitReader(bitReader, bitLength);

        // Assert
        foreach (var featureBit in featureBits)
        {
            Assert.True(taggedField.Value.IsFeatureSet(featureBit, false));
        }
    }

    [Fact]
    public void FromBitReader_ThrowsArgumentException_ForInvalidLength()
    {
        // Arrange
        var buffer = new byte[50];
        var bitReader = new BitReader(buffer);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => FeaturesTaggedField.FromBitReader(bitReader, 0));
    }

    [Fact]
    public void Given_FifteenBitField_When_FromBitReader_Then_BitsAreNotShifted()
    {
        // Arrange
        // `sgq` from the BOLT 11 examples: bits 14 and 8, both compulsory
        var bitReader = new BitReader([0x82, 0x00]);

        // Act
        var taggedField = FeaturesTaggedField.FromBitReader(bitReader, 3);

        // Assert
        Assert.True(taggedField.Value.IsFeatureSet(8, false));
        Assert.True(taggedField.Value.IsFeatureSet(14, false));
        Assert.False(taggedField.Value.IsFeatureSet(9, false));
        Assert.False(taggedField.Value.IsFeatureSet(15, false));
    }

    [Fact]
    public void Given_UnknownEvenBit_When_FromBitReader_Then_ThrowsArgumentExceptionNamingTheBit()
    {
        // Arrange
        // 21 groups (105 bits): bit 100 set (unknown, even)
        var features = FeatureSet.DeserializeFromBytes([0x00]);
        features.SetFeature(100, true);
        var writer = new BitWriter(105);
        features.WriteToBitWriter(writer, 105, false);

        // Act
        var ex = Assert.Throws<ArgumentException>(() => FeaturesTaggedField.FromBitReader(
                                                      new BitReader(writer.ToArray()), 21));

        // Assert
        Assert.Contains("100", ex.Message);
    }

    [Fact]
    public void Given_UnknownOddBit_When_FromBitReader_Then_BitIsIgnored()
    {
        // Arrange
        // 20 groups (100 bits): bits 99 (unknown, odd), 14 and 8
        var features = FeatureSet.DeserializeFromBytes([0x00]);
        features.SetFeature(99, true);
        features.SetFeature(14, true);
        features.SetFeature(8, true);
        var writer = new BitWriter(100);
        features.WriteToBitWriter(writer, 100, false);

        // Act
        var taggedField = FeaturesTaggedField.FromBitReader(new BitReader(writer.ToArray()), 20);

        // Assert
        Assert.True(taggedField.Value.IsFeatureSet(99, false));
    }

    [Fact]
    public void Given_FeatureSetChangedAfterFieldCreated_When_LengthRead_Then_LengthCoversNewBits()
    {
        // Arrange
        var features = FeatureSet.DeserializeFromBytes([0x00]);
        features.SetFeature(8, true);
        var taggedField = new FeaturesTaggedField(features);

        // Act
        features.SetFeature(48, true);

        // Assert
        Assert.Equal(10, taggedField.Length);
    }
}