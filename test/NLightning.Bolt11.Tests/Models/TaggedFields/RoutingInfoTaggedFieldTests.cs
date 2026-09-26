namespace NLightning.Bolt11.Tests.Models.TaggedFields;

using Bolt11.Models.TaggedFields;
using Constants;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Models;
using Domain.Utils;
using Enums;

public class RoutingInfoTaggedFieldTests
{
    private static RoutingInfo BuildKnownRoutingInfo()
    {
        var pubkey = new CompactPubKey([
            0x02,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
            0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20
        ]);

        var scid = ShortChannelId.Parse("539268x845x1");

        const uint feeBaseMsat = 1000;
        const uint feeProportionalMillionths = 250;
        const ushort cltvDelta = 40;

        return new RoutingInfo(pubkey, scid, feeBaseMsat, feeProportionalMillionths, cltvDelta);
    }

    private static RoutingInfoCollection BuildKnownCollection(int entries)
    {
        var col = new RoutingInfoCollection();
        for (var i = 0; i < entries; i++)
        {
            var baseRi = BuildKnownRoutingInfo();
            var variedScid = new ShortChannelId(baseRi.ShortChannelId.BlockHeight,
                                                baseRi.ShortChannelId.TransactionIndex + (uint)i,
                                                (ushort)(baseRi.ShortChannelId.OutputIndex + i));
            col.Add(new RoutingInfo(baseRi.CompactPubKey,
                                    variedScid,
                                    baseRi.FeeBaseMsat + (uint)i,
                                    baseRi.FeeProportionalMillionths + (uint)i,
                                    (ushort)(baseRi.CltvExpiryDelta + i)));
        }

        return col;
    }

    [Fact]
    public void Constructor_FromValue_SetsPropertiesCorrectly()
    {
        // Arrange
        var collection = BuildKnownCollection(1);

        // Act
        var field = new RoutingInfoTaggedField(collection);

        // Assert
        Assert.Equal(TaggedFieldTypes.RoutingInfo, field.Type);
        // Length should be (n * 408 + n * 2) / 5 for n entries
        Assert.Equal((short)((1 * TaggedFieldConstants.RoutingInfoLength + 1 * 2) / 5), field.Length);
        Assert.True(field.IsValid());
    }

    [Fact]
    public void WriteToBitWriter_And_FromBitReader_RoundTrip_SingleEntry()
    {
        // Arrange
        var expected = BuildKnownCollection(1);
        var field = new RoutingInfoTaggedField(expected);
        var writer = new BitWriter(field.Length * 5);

        // Act
        field.WriteToBitWriter(writer);
        var reader = new BitReader(writer.ToArray());
        var parsed = RoutingInfoTaggedField.FromBitReader(reader, field.Length);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(expected.Count, parsed.Value.Count);
        Assert.Equal(expected[0].CompactPubKey, parsed.Value[0].CompactPubKey);
        Assert.Equal(expected[0].ShortChannelId, parsed.Value[0].ShortChannelId);
        Assert.Equal(expected[0].FeeBaseMsat, parsed.Value[0].FeeBaseMsat);
        Assert.Equal(expected[0].FeeProportionalMillionths, parsed.Value[0].FeeProportionalMillionths);
        Assert.Equal(expected[0].CltvExpiryDelta, parsed.Value[0].CltvExpiryDelta);
    }

    [Fact]
    public void WriteToBitWriter_And_FromBitReader_RoundTrip_MultipleEntries()
    {
        // Arrange
        var expected = BuildKnownCollection(2);
        var field = new RoutingInfoTaggedField(expected);
        var writer = new BitWriter(field.Length * 5);

        // Act
        field.WriteToBitWriter(writer);
        var reader = new BitReader(writer.ToArray());
        var parsed = RoutingInfoTaggedField.FromBitReader(reader, field.Length);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(expected.Count, parsed.Value.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].CompactPubKey, parsed.Value[i].CompactPubKey);
            Assert.Equal(expected[i].ShortChannelId, parsed.Value[i].ShortChannelId);
            Assert.Equal(expected[i].FeeBaseMsat, parsed.Value[i].FeeBaseMsat);
            Assert.Equal(expected[i].FeeProportionalMillionths, parsed.Value[i].FeeProportionalMillionths);
            Assert.Equal(expected[i].CltvExpiryDelta, parsed.Value[i].CltvExpiryDelta);
        }
    }

    [Theory]
    [InlineData(80)] // 400 bits: less than one entry
    [InlineData(84)] // 420 bits: one entry plus 12 bits, more than byte padding
    public void Given_LengthNotWholeEntries_When_FromBitReader_Then_ThrowsArgumentException(short length)
    {
        // Arrange
        var reader = new BitReader(new byte[(length * 5 + 7) / 8]);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => RoutingInfoTaggedField.FromBitReader(reader, length));
    }

    [Fact]
    public void Given_MaxUnsignedValues_When_RoundTripping_Then_FieldIsValidAndValuesArePreserved()
    {
        // Arrange
        // fee_base_msat and fee_proportional_millionths are u32, cltv_expiry_delta is u16 (BOLT 11)
        var baseRi = BuildKnownRoutingInfo();
        var collection = new RoutingInfoCollection
        {
            new RoutingInfo(baseRi.CompactPubKey, baseRi.ShortChannelId, uint.MaxValue, 0x8000_0000u,
                            ushort.MaxValue)
        };
        var field = new RoutingInfoTaggedField(collection);
        var writer = new BitWriter(field.Length * 5);

        // Act
        field.WriteToBitWriter(writer);
        var parsed = RoutingInfoTaggedField.FromBitReader(new BitReader(writer.ToArray()), field.Length);

        // Assert
        Assert.True(field.IsValid());
        Assert.NotNull(parsed);
        Assert.True(parsed.IsValid());
        Assert.Equal(uint.MaxValue, parsed.Value[0].FeeBaseMsat);
        Assert.Equal(0x8000_0000u, parsed.Value[0].FeeProportionalMillionths);
        Assert.Equal(ushort.MaxValue, parsed.Value[0].CltvExpiryDelta);
    }

    [Theory]
    [InlineData(1, 82)]
    [InlineData(2, 164)]
    [InlineData(3, 245)]
    [InlineData(12, 980)]
    public void Given_Entries_When_Constructed_Then_LengthIsMinimal(int entries, short expectedLength)
    {
        // Arrange
        var collection = BuildKnownCollection(entries);

        // Act
        var field = new RoutingInfoTaggedField(collection);
        var writer = new BitWriter(field.Length * 5);
        field.WriteToBitWriter(writer);
        var parsed = RoutingInfoTaggedField.FromBitReader(new BitReader(writer.ToArray()), field.Length);

        // Assert
        Assert.Equal(expectedLength, field.Length);
        Assert.False(writer.HasMoreBits(1));
        Assert.NotNull(parsed);
        Assert.Equal(entries, parsed.Value.Count);
    }
}