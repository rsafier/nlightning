using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Bootstrap;

using Bitcoin.Bootstrap;
using Domain.Crypto.ValueObjects;

public class LightningNodeIdBech32Tests
{
    // Observed live in nodes.lightning.directory's SRV answer
    private const string LiveLabel = "ln1qfzcdxeg3nun56n5q2a8xrwt3yg88xt029q9s7j4cn3z2rn7argcz90kezd";
    private const string LiveNodeIdHex = "0245869b288cf93a6a7402ba730dcb891073996f5140587a55c4e2250e7ee8d181";

    [Fact]
    public void Given_TheLiveLabel_When_Decoded_Then_ItIsTheExpectedCompressedKey()
    {
        // Act
        var decoded = LightningNodeIdBech32.TryDecode(LiveLabel, out var key, out var reason);

        // Assert
        Assert.True(decoded, reason);
        Assert.Equal(LiveNodeIdHex, key.ToString());
    }

    [Fact]
    public void Given_AKey_When_EncodedAndDecoded_Then_ItRoundTrips()
    {
        // Arrange
        var key = new CompactPubKey(new Key().PubKey.ToBytes());

        // Act
        var label = LightningNodeIdBech32.Encode(key);
        var decoded = LightningNodeIdBech32.TryDecode(label, out var roundTripped, out _);

        // Assert
        Assert.Equal(LightningNodeIdBech32.LabelLength, label.Length);
        Assert.StartsWith("ln1", label);
        Assert.True(decoded);
        Assert.Equal(key, roundTripped);
        Assert.Equal(LiveLabel,
                     LightningNodeIdBech32.Encode(new CompactPubKey(Convert.FromHexString(LiveNodeIdHex))));
    }

    [Fact]
    public void Given_UpperOrMixedCase_When_Decoded_Then_ItIsLowerCasedFirst()
    {
        // Arrange (resolvers may randomize the case of names, 0x20 encoding)
        var upper = LiveLabel.ToUpperInvariant();
        var mixed = string.Concat(LiveLabel.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : c));

        // Act & Assert
        Assert.True(LightningNodeIdBech32.TryDecode(upper, out var fromUpper, out _));
        Assert.True(LightningNodeIdBech32.TryDecode(mixed, out var fromMixed, out _));
        Assert.Equal(LiveNodeIdHex, fromUpper.ToString());
        Assert.Equal(LiveNodeIdHex, fromMixed.ToString());
    }

    [Theory]
    [InlineData("lnbc")]
    [InlineData("lntb")]
    public void Given_AnotherHrp_When_Decoded_Then_ItIsRefused(string hrp)
    {
        // Arrange: the same key under an invoice HRP, cut to 62 characters so only the HRP is wrong
        var label = hrp + LiveLabel[2..];
        label = label[..LightningNodeIdBech32.LabelLength];

        // Act
        var decoded = LightningNodeIdBech32.TryDecode(label, out _, out var reason);

        // Assert
        Assert.False(decoded);
        Assert.Contains("human-readable", reason);
    }

    [Fact]
    public void Given_ABech32MChecksum_When_Decoded_Then_ItIsRefused()
    {
        // Arrange
        var fiveBit = LightningNodeIdBech32.ConvertEightToFive(Convert.FromHexString(LiveNodeIdHex));
        var label = LightningNodeIdBech32.EncodeRaw(fiveBit, bech32M: true);

        // Act
        var decoded = LightningNodeIdBech32.TryDecode(label, out _, out var reason);

        // Assert
        Assert.Equal(LightningNodeIdBech32.LabelLength, label.Length);
        Assert.False(decoded);
        Assert.Contains("bech32m", reason);
    }

    [Fact]
    public void Given_ABadChecksum_When_Decoded_Then_ItIsRefused()
    {
        // Arrange: change the last checksum character
        var label = LiveLabel[..^1] + (LiveLabel[^1] == 'q' ? 'p' : 'q');

        // Act
        var decoded = LightningNodeIdBech32.TryDecode(label, out _, out _);

        // Assert
        Assert.False(decoded);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(63)]
    public void Given_AWrongLength_When_Decoded_Then_ItIsRefused(int length)
    {
        // Arrange
        var label = length < LiveLabel.Length ? LiveLabel[..length] : LiveLabel + "q";

        // Act
        var decoded = LightningNodeIdBech32.TryDecode(label, out _, out var reason);

        // Assert
        Assert.False(decoded);
        Assert.Contains("length", reason);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(34)]
    public void Given_APayloadOfAnotherSize_When_Decoded_Then_ItIsRefused(int size)
    {
        // Arrange
        var payload = new byte[size];
        payload[0] = 0x02;
        var label = LightningNodeIdBech32.EncodeRaw(LightningNodeIdBech32.ConvertEightToFive(payload), false);

        // Act
        var decoded = LightningNodeIdBech32.TryDecode(label, out _, out _);

        // Assert
        Assert.NotEqual(LightningNodeIdBech32.LabelLength, label.Length);
        Assert.False(decoded);
    }

    [Fact]
    public void Given_ANonZeroPaddingBit_When_Decoded_Then_ItIsRefused()
    {
        // Arrange: 33 bytes are 264 bits in 53 groups; the last group's low bit is padding
        var fiveBit = LightningNodeIdBech32.ConvertEightToFive(Convert.FromHexString(LiveNodeIdHex));
        fiveBit[^1] |= 1;
        var label = LightningNodeIdBech32.EncodeRaw(fiveBit, false);

        // Act
        var decoded = LightningNodeIdBech32.TryDecode(label, out _, out var reason);

        // Assert
        Assert.False(decoded);
        Assert.Contains("padding", reason);
    }

    [Fact]
    public void Given_Prefix04_When_Decoded_Then_ItIsRefused()
    {
        // Arrange
        var bytes = Convert.FromHexString(LiveNodeIdHex);
        bytes[0] = 0x04;
        var label = LightningNodeIdBech32.EncodeRaw(LightningNodeIdBech32.ConvertEightToFive(bytes), false);

        // Act
        var decoded = LightningNodeIdBech32.TryDecode(label, out _, out var reason);

        // Assert
        Assert.False(decoded);
        Assert.Contains("prefix", reason);
    }

    [Fact]
    public void Given_AnXCoordinateNotOnTheCurve_When_Decoded_Then_ItIsRefused()
    {
        // Arrange: x = 5 has no y on secp256k1 (5^3 + 7 = 132 is not a square mod p)
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = 0x05;
        Assert.False(PubKey.TryCreatePubKey(bytes, out _));
        var label = LightningNodeIdBech32.EncodeRaw(LightningNodeIdBech32.ConvertEightToFive(bytes), false);

        // Act
        var decoded = LightningNodeIdBech32.TryDecode(label, out _, out var reason);

        // Assert
        Assert.False(decoded);
        Assert.Contains("curve", reason);
    }

    [Fact]
    public void Given_AnLPrefixedLabel_When_Decoded_Then_ItIsRefused()
    {
        // Arrange: the "l" condition label is not a virtual host
        var label = "l" + LiveLabel[..^1];

        // Act
        var decoded = LightningNodeIdBech32.TryDecode(label, out _, out _);

        // Assert
        Assert.False(decoded);
        Assert.False(LightningNodeIdBech32.TryDecode("l" + LiveLabel, out _, out _));
    }
}