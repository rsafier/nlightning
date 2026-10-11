namespace NLightning.Domain.Tests.Crypto.ValueObjects;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

public class CryptoValueObjectEqualityTests
{
    private static byte[] Bytes(int length, byte fill)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }

    [Fact]
    public void Given_PrivKeysWithSameBytesInDifferentArrays_When_Compared_Then_AreEqualWithSameHashCode()
    {
        // Arrange
        var a = new PrivKey(Bytes(32, 7));
        var b = new PrivKey(Bytes(32, 7));
        var c = new PrivKey(Bytes(32, 8));

        // Act / Assert
        Assert.True(a == b);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Given_CryptoKeyPairsWithSameContent_When_Compared_Then_AreEqual()
    {
        // Arrange
        var pubKey = Bytes(33, 1);
        pubKey[0] = 0x02;
        var a = new CryptoKeyPair(new PrivKey(Bytes(32, 7)), new CompactPubKey(pubKey.ToArray()));
        var b = new CryptoKeyPair(new PrivKey(Bytes(32, 7)), new CompactPubKey(pubKey.ToArray()));

        // Act / Assert
        Assert.Equal(a, b);
    }

    [Fact]
    public void Given_CompactSignaturesWithSameBytesInDifferentArrays_When_Compared_Then_AreEqualWithSameHashCode()
    {
        // Arrange
        var a = new CompactSignature(Bytes(64, 3));
        var b = new CompactSignature(Bytes(64, 3));
        var c = new CompactSignature(Bytes(64, 4));

        // Act / Assert
        Assert.True(a == b);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void Given_SecretsWithSameBytesInDifferentArrays_When_Compared_Then_AreEqual()
    {
        // Arrange
        var a = new Secret(Bytes(32, 5));
        var b = new Secret(Bytes(32, 5));
        var c = new Secret(Bytes(32, 6));

        // Act / Assert
        Assert.True(a == b);
        Assert.True(a != c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Given_DefaultValueObjects_When_HashingAndComparing_Then_DoNotThrow()
    {
        // Arrange
        var txId = default(TxId);
        var hash = default(Hash);
        var secret = default(Secret);
        var privKey = default(PrivKey);
        var chainHash = default(ChainHash);

        // Act / Assert
        Assert.Equal(0, txId.GetHashCode());
        Assert.Equal(0, hash.GetHashCode());
        Assert.Equal(0, secret.GetHashCode());
        Assert.Equal(0, privKey.GetHashCode());
        Assert.Equal(0, chainHash.GetHashCode());
        Assert.True(secret.Equals(default));
        Assert.True(privKey.Equals(default));
        Assert.True(chainHash.Equals(default));
        Assert.False(secret.Equals(Secret.Empty));
        Assert.False(privKey.Equals(new PrivKey(new byte[32])));
        Assert.False(chainHash.Equals(new ChainHash(new byte[32])));
        Assert.False(txId.IsZero);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Given_ArrayOfWrongLength_When_CreatingHashSecretOrTxId_Then_Throws(int length)
    {
        // Act / Assert
        Assert.ThrowsAny<ArgumentException>(() => new Hash(new byte[length]));
        Assert.ThrowsAny<ArgumentException>(() => new Secret(new byte[length]));
        Assert.ThrowsAny<ArgumentException>(() => new TxId(new byte[length]));
    }

    [Fact]
    public void Given_NullArray_When_CreatingHashSecretOrTxId_Then_ThrowsArgumentNullException()
    {
        // Act / Assert
        Assert.Throws<ArgumentNullException>(() => new Hash(null!));
        Assert.Throws<ArgumentNullException>(() => new Secret(null!));
        Assert.Throws<ArgumentNullException>(() => new TxId(null!));
    }

    [Fact]
    public void Given_EqualChainHashesInDifferentArrays_When_Hashing_Then_HashCodesMatch()
    {
        // Arrange
        var a = new ChainHash(Bytes(32, 9));
        var b = new ChainHash(Bytes(32, 9));

        // Act / Assert
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Single(new HashSet<ChainHash> { a, b });
    }

    [Fact]
    public void Given_EqualBaseTlvsInDifferentArrays_When_Hashing_Then_HashCodesMatch()
    {
        // Arrange
        var a = new BaseTlv(new BigSize(1), Bytes(4, 2));
        var b = new BaseTlv(new BigSize(1), Bytes(4, 2));

        // Act / Assert
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Single(new HashSet<BaseTlv> { a, b });
    }

    [Fact]
    public void Given_NullBaseTlv_When_ComparedWithOperators_Then_DoesNotThrow()
    {
        // Arrange
        BaseTlv? nullTlv = null;
        var tlv = new BaseTlv(new BigSize(1), Bytes(1, 1));

        // Act / Assert
        Assert.True(nullTlv == null);
        Assert.False(nullTlv == tlv);
        Assert.True(nullTlv != tlv);
        Assert.False(tlv == nullTlv);
    }
}