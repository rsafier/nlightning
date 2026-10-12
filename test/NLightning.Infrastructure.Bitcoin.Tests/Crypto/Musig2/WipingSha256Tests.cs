using System.Security.Cryptography;
using System.Text;
using TaggedSha256 = NBitcoin.Secp256k1.SHA256;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.Musig2;

using Infrastructure.Bitcoin.Crypto.Musig2;

/// <summary>
/// NL-911: <see cref="WipingSha256"/> is SHA-256 (FIPS 180-4 vectors, every length around the block boundaries and
/// chunked writes against the platform's SHA-256, BIP 340 tagged prefixes against NBitcoin's) and leaves no state
/// behind: after the digest, on dispose and when the caller throws inside its <c>using</c>.
/// </summary>
public class WipingSha256Tests
{
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq",
                "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")]
    [InlineData("abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrs"
              + "mnopqrstnopqrstu", "cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1")]
    public void Given_FipsVectors_When_Hashed_Then_DigestMatches(string message, string expected)
    {
        // Act
        var digest = Hash(Encoding.ASCII.GetBytes(message));

        // Assert
        Assert.Equal(expected, Convert.ToHexStringLower(digest));
    }

    [Fact]
    public void Given_AMillionA_When_HashedInChunks_Then_DigestMatchesTheFipsVector()
    {
        // Arrange
        using var sha = new WipingSha256();
        var chunk = Enumerable.Repeat((byte)'a', 1000).ToArray();
        var digest = new byte[32];

        // Act
        for (var i = 0; i < 1000; i++)
            sha.Write(chunk);
        sha.GetHash(digest);

        // Assert
        Assert.Equal("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0",
                     Convert.ToHexStringLower(digest));
    }

    [Fact]
    public void Given_EveryLengthUpTo300_When_WrittenWholeAndBytewise_Then_TheyMatchThePlatformSha256()
    {
        for (var length = 0; length <= 300; length++)
        {
            // Arrange
            var data = RandomNumberGenerator.GetBytes(length);
            var expected = SHA256.HashData(data);

            // Act
            var whole = Hash(data);
            using var bytewise = new WipingSha256();
            foreach (var b in data)
                bytewise.Write(b);
            var bytewiseDigest = new byte[32];
            bytewise.GetHash(bytewiseDigest);

            // Assert
            Assert.Equal(expected, whole);
            Assert.Equal(expected, bytewiseDigest);
        }
    }

    [Theory]
    [InlineData("MuSig/aux")]
    [InlineData("MuSig/nonce")]
    [InlineData("MuSig/deterministic/nonce")]
    public void Given_ATag_When_CreateTagged_Then_SameDigestAsNBitcoinsTaggedSha256(string tag)
    {
        // Arrange
        var data = RandomNumberGenerator.GetBytes(77);
        using var nbitcoin = new TaggedSha256();
        nbitcoin.InitializeTagged(tag);
        nbitcoin.Write(data);
        var digest = new byte[32];

        // Act
        using (var sha = WipingSha256.CreateTagged(tag))
        {
            sha.Write(data);
            sha.GetHash(digest);
        }

        // Assert
        Assert.Equal(nbitcoin.GetHash(), digest);
    }

    [Fact]
    public void Given_ADigest_When_Taken_Then_TheHasherIsWiped()
    {
        // Arrange
        using var sha = new WipingSha256();
        sha.Write(RandomNumberGenerator.GetBytes(100));
        Assert.False(sha.IsWiped);

        // Act
        sha.GetHash(new byte[32]);

        // Assert
        Assert.True(sha.IsWiped);
    }

    [Fact]
    public void Given_ACallerThatThrows_When_ItsUsingEnds_Then_TheHasherIsWiped()
    {
        // Arrange
        WipingSha256? sha = null;

        void UseAndFail()
        {
            using var inner = new WipingSha256();
            sha = inner;
            inner.Write(RandomNumberGenerator.GetBytes(40));
            throw new InvalidOperationException("the caller failed");
        }

        // Act
        Assert.Throws<InvalidOperationException>(UseAndFail);

        // Assert
        Assert.NotNull(sha);
        Assert.True(sha.IsWiped);
    }

    [Fact]
    public void Given_ATooShortDestination_When_GetHash_Then_ThrowsAndStillWipesNothingTaken()
    {
        // Arrange
        using var sha = new WipingSha256();
        sha.Write([1, 2, 3]);

        // Act / Assert: refused before anything is finished; the using still wipes it
        Assert.Throws<ArgumentException>(() => sha.GetHash(new byte[31]));
        sha.Dispose();
        Assert.True(sha.IsWiped);
    }

    private static byte[] Hash(byte[] data)
    {
        using var sha = new WipingSha256();
        sha.Write(data);
        var digest = new byte[32];
        sha.GetHash(digest);
        return digest;
    }
}