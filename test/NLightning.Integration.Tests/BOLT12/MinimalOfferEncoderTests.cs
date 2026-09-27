using NLightning.Tests.Utils.Bolt12;

namespace NLightning.Integration.Tests.BOLT12;

using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;

/// <summary>
/// <see cref="MinimalOfferEncoder"/> (the offers Proof M6 hands to CLN's <c>fetchinvoice</c>) against the BOLT 12
/// <c>offers-test.json</c> strings. Kept out of the Docker namespace so the CI run (<c>FullyQualifiedName!~Docker</c>)
/// covers it.
/// </summary>
public sealed class MinimalOfferEncoderTests
{
    private const string BobIssuerId = "02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619";
    private const string Description = "Test vectors";

    [Fact]
    public void Given_OnlyAnIssuerId_When_Encoded_Then_EqualsTheMinimalOfferVector()
    {
        // Arrange
        var issuer = Convert.FromHexString(BobIssuerId);

        // Act
        var offer = MinimalOfferEncoder.Encode(null, null, issuer);

        // Assert
        Assert.Equal("lno1zcss9mk8y3wkklfvevcrszlmu23kfrxh49px20665dqwmn4p72pksese", offer);
    }

    [Fact]
    public void Given_ADescription_When_Encoded_Then_EqualsTheVector()
    {
        // Arrange
        var issuer = Convert.FromHexString(BobIssuerId);

        // Act
        var offer = MinimalOfferEncoder.Encode(null, Description, issuer);

        // Assert
        Assert.Equal("lno1pgx9getnwss8vetrw3hhyuckyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd5xvxg", offer);
    }

    [Fact]
    public void Given_TheTestnetChain_When_Encoded_Then_EqualsTheVector()
    {
        // Arrange
        var chain = Convert.FromHexString("43497fd7f826957108f4a30fd9cec3aeba79972084e90ead01ea330900000000");

        // Act
        var offer = MinimalOfferEncoder.Encode(chain, Description, Convert.FromHexString(BobIssuerId));

        // Assert
        Assert.Equal("lno1qgsyxjtl6luzd9t3pr62xr7eemp6awnejusgf6gw45q75vcfqqqqqqq2p32x2um5ypmx2cm5dae8x93pqthvwfzadd7"
                   + "jejes8q9lhc4rvjxd022zv5l44g6qah82ru5rdpnpj", offer);
    }

    [Fact]
    public void Given_ABlindedPathViaBob_When_Encoded_Then_EqualsTheVector()
    {
        // Arrange
        var path = VectorPath(SciddirOrPubkey.FromNodeId(
                                  Convert.FromHexString(
                                      "0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c")));

        // Act
        var offer = MinimalOfferEncoder.Encode(null, Description, Convert.FromHexString(BobIssuerId), [path]);

        // Assert
        Assert.Equal("lno1pgx9getnwss8vetrw3hhyucs5ypjgef743p5fzqq9nqxh0ah7y87rzv3ud0eleps9kl2d5348hq2k8qzqgpqyqszqgpqyq"
                   + "szqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgqpqqqq"
                   + "qqqqqqqqqqqqqqqqqqqqqqqzqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqqzq3zyg3zyg3zyg3vggzam"
                   + "rjghtt05kvkvpcp0a79gmy3nt6jsn98ad2xs8de6sl9qmgvcvs", offer);
    }

    [Fact]
    public void Given_ABlindedPathWithASciddir_When_Encoded_Then_EqualsTheVector()
    {
        // Arrange: short_channel_id 0x0x42, direction 0
        var path = VectorPath(SciddirOrPubkey.FromShortChannelId(42UL, 0));

        // Act
        var offer = MinimalOfferEncoder.Encode(null, Description, Convert.FromHexString(BobIssuerId), [path]);

        // Assert
        Assert.Equal("lno1pgx9getnwss8vetrw3hhyucs3yqqqqqqqqqqqqp2qgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpq"
                   + "yqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqqyqqqqqqqqqqqqqqqqqqqqqqqqqqqgpqyqszqgpqyq"
                   + "szqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqqgzyg3zyg3zyg3z93pqthvwfzadd7jejes8q9lhc4rvjxd022zv5l44g6q"
                   + "ah82ru5rdpnpj", offer);
    }

    [Fact]
    public void Given_NoIssuerAndNoPath_When_Encoded_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => MinimalOfferEncoder.Encode(null, Description, null));
    }

    private static WireBlindedPath VectorPath(SciddirOrPubkey firstNode)
    {
        var twos = Enumerable.Repeat((byte)0x02, 33).ToArray();
        return new WireBlindedPath(firstNode, twos,
                                   [
                                       new BlindedPathHop(twos, new byte[16]),
                                       new BlindedPathHop(twos, Enumerable.Repeat((byte)0x11, 8).ToArray())
                                   ]);
    }
}