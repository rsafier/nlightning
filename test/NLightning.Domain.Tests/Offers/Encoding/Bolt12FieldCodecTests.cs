namespace NLightning.Domain.Tests.Offers.Encoding;

using Domain.Offers.Encoding;
using Domain.Offers.Models;
using Domain.Protocol.Onion.Models;

public class Bolt12FieldCodecTests
{
    [Theory]
    [InlineData("", null)]
    [InlineData("00", null)]
    [InlineData("02", null)]
    [InlineData("01", 0)]
    [InlineData("0a", null)]
    [InlineData("04", 2)]
    [InlineData("0100", 8)]
    [InlineData("040000000000000000000000000000", 114)]
    [InlineData("0200000000000000000000000000000000", null)]
    public void Given_FeatureBitmap_When_LookingForUnknownEvenBits_Then_TheLowestIsFound(string hex, int? bit)
    {
        // Act & Assert
        Assert.Equal(bit, Bolt12FieldCodec.FindUnknownEvenBit(Convert.FromHexString(hex)));
    }

    [Fact]
    public void Given_KnownEvenBit_When_LookingForUnknownEvenBits_Then_ItIsSkipped()
    {
        // Arrange: bits 16 and 18
        var features = Convert.FromHexString("050000");

        // Act & Assert
        Assert.Equal(18, Bolt12FieldCodec.FindUnknownEvenBit(features, new HashSet<int> { 16 }));
        Assert.Null(Bolt12FieldCodec.FindUnknownEvenBit(features, new HashSet<int> { 16, 18 }));
    }

    [Theory]
    [InlineData("020000", 17, true)]
    [InlineData("020000", 16, false)]
    [InlineData("020000", 40, false)]
    [InlineData("01", 0, true)]
    public void Given_FeatureBitmap_When_TestingABit_Then_ItIsReadBigEndian(string hex, int bit, bool set)
    {
        // Act & Assert
        Assert.Equal(set, Bolt12FieldCodec.IsBitSet(Convert.FromHexString(hex), bit));
    }

    [Fact]
    public void Given_PayInfo_When_Encoded_Then_ItIsTheSpecLayout()
    {
        // Arrange
        var payInfo = new BlindedPayInfo(1, 2, 3, 4, 5, new byte[] { 0xaa });

        // Act
        var bytes = Bolt12FieldCodec.EncodePayInfos([payInfo]);

        // Assert
        Assert.Equal(Convert.FromHexString("00000001" + "00000002" + "0003" + "0000000000000004" + "0000000000000005"
                                         + "0001" + "aa"), bytes);
        Assert.Equal(Bolt12FieldCodec.PayInfoFixedLength + 1, bytes.Length);
    }

    [Fact]
    public void Given_FallbacksAndBip353Name_When_Encoded_Then_TheyAreTheSpecLayout()
    {
        // Act
        var fallbacks = Bolt12FieldCodec.EncodeFallbacks([new FallbackAddress(1, new byte[] { 0xbb, 0xcc })]);
        var name = Bolt12FieldCodec.EncodeBip353Name(new Bip353Name("a"u8.ToArray(), "bc"u8.ToArray()));

        // Assert
        Assert.Equal(Convert.FromHexString("010002bbcc"), fallbacks);
        Assert.Equal(Convert.FromHexString("0161026263"), name);
    }

    [Fact]
    public void Given_TooLongValues_When_Encoding_Then_ItThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Bolt12FieldCodec.EncodeBip353Name(new Bip353Name(new byte[256], ReadOnlyMemory<byte>.Empty)));
        Assert.Throws<ArgumentException>(() => Bolt12FieldCodec.EncodeFallbacks([new FallbackAddress(0, new byte[65536])]));
        Assert.Throws<ArgumentException>(() => Bolt12FieldCodec.EncodePayInfos([new BlindedPayInfo(0, 0, 0, 0, 0,
                                                                                    new byte[65536])]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bolt12FieldCodec.IsBitSet([1], -1));
    }
}