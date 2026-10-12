using System.Text;

namespace NLightning.Domain.Tests.Crypto.Hashes;

using Domain.Crypto.Hashes;

public class Sha3Tests
{
    // FIPS 202 / NIST CSRC SHA3-256 examples
    [Theory]
    [InlineData("", "a7ffc6f8bf1ed76651c14756a061d662f580ff4de43b49fa82d80a4b80f8434a")]
    [InlineData("abc", "3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq",
                "41c0dba2a9d6240849100376a8235e2c82e1b9998a999e21db32dd97496d3376")]
    public void Given_ANistVector_When_Hashed_Then_TheDigestMatches(string message, string expectedHex)
    {
        // Act
        var digest = Sha3.Hash256(Encoding.ASCII.GetBytes(message));

        // Assert
        Assert.Equal(expectedHex, Convert.ToHexStringLower(digest));
    }

    [Fact]
    public void Given_200BytesOfA3_When_Hashed_Then_TheMultiBlockDigestMatches()
    {
        // Arrange: NIST's 1600-bit message, longer than one 136-byte block
        var message = Enumerable.Repeat((byte)0xa3, 200).ToArray();

        // Act
        var digest = Sha3.Hash256(message);

        // Assert
        Assert.Equal("79f38adec5c20307a98ef76e8324afbfd46cfd81b22e3973c65fa1bd9de31787",
                     Convert.ToHexStringLower(digest));
    }

    [Theory]
    [InlineData(135)]
    [InlineData(136)]
    [InlineData(137)]
    [InlineData(272)]
    public void Given_AMessageAroundTheBlockSize_When_Hashed_Then_ItMatchesTheBcl(int length)
    {
        // Arrange
        if (!System.Security.Cryptography.SHA3_256.IsSupported)
            Assert.Skip("The platform has no SHA3-256");

        var message = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

        // Act
        var digest = Sha3.Hash256(message);

        // Assert
        Assert.Equal(System.Security.Cryptography.SHA3_256.HashData(message), digest);
    }
}