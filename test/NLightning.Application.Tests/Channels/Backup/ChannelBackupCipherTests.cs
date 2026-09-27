using System.Security.Cryptography;
using System.Text;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;

public class ChannelBackupCipherTests
{
    private static readonly byte[] s_nodeKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] s_otherNodeKey = Enumerable.Range(2, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] s_plaintext = Encoding.ASCII.GetBytes("a static channel backup plaintext");

    [Fact]
    public void Given_ANodeKey_When_DerivingTheBackupKey_Then_ItIsHkdfSha256WithTheBackupSaltAndInfo()
    {
        // Arrange: RFC 5869 by hand (one output block): PRK = HMAC(salt, IKM), OKM = HMAC(PRK, info || 0x01)
        var prk = HMACSHA256.HashData(Encoding.ASCII.GetBytes("NLightning static channel backup"), s_nodeKey);
        var expected = HMACSHA256.HashData(prk, Encoding.ASCII.GetBytes("nltg-scb-v1").Concat(new byte[] { 1 })
                                                                                       .ToArray());

        // Act
        var key = ChannelBackupCipher.DeriveKey(s_nodeKey);

        // Assert
        Assert.Equal(expected, key);
        Assert.NotEqual(key, ChannelBackupCipher.DeriveKey(s_otherNodeKey));
        Assert.NotEqual(s_nodeKey, key);
    }

    [Fact]
    public void Given_AFixedNonce_When_Encrypting_Then_TheLayoutIsHeaderNonceCiphertextAndTag()
    {
        // Arrange
        var key = ChannelBackupCipher.DeriveKey(s_nodeKey);
        var nonce = Enumerable.Repeat((byte)0x5A, 24).ToArray();

        // Act
        var blob = ChannelBackupCipher.Encrypt(key, nonce, s_plaintext);

        // Assert
        Assert.Equal("NLSCB"u8.ToArray(), blob[..5]);
        Assert.Equal(1, blob[5]);
        Assert.Equal(nonce, blob[6..30]);
        Assert.Equal(ChannelBackupCipher.Overhead + s_plaintext.Length, blob.Length);
        Assert.Equal(s_plaintext, ChannelBackupCipher.Decrypt(key, blob));
    }

    [Fact]
    public void Given_ThePlaintextTwice_When_Encrypted_Then_TheNoncesDifferAndBothDecrypt()
    {
        // Arrange
        var key = ChannelBackupCipher.DeriveKey(s_nodeKey);

        // Act
        var first = ChannelBackupCipher.Encrypt(key, s_plaintext);
        var second = ChannelBackupCipher.Encrypt(key, s_plaintext);

        // Assert
        Assert.NotEqual(first, second);
        Assert.Equal(s_plaintext, ChannelBackupCipher.Decrypt(key, first));
        Assert.Equal(s_plaintext, ChannelBackupCipher.Decrypt(key, second));
    }

    [Fact]
    public void Given_AnyFlippedBitAfterTheMagic_When_Decrypted_Then_ItIsRefused()
    {
        // Arrange
        var key = ChannelBackupCipher.DeriveKey(s_nodeKey);
        var blob = ChannelBackupCipher.Encrypt(key, s_plaintext);

        // Act / Assert: the version byte is a format error, every other byte (nonce, ciphertext, tag) fails the AEAD
        for (var i = 5; i < blob.Length; i++)
        {
            var tampered = (byte[])blob.Clone();
            tampered[i] ^= 0x01;
            if (i == 5)
                Assert.Throws<ChannelBackupFormatException>(() => ChannelBackupCipher.Decrypt(key, tampered));
            else
                Assert.Throws<ChannelBackupAuthenticationException>(() => ChannelBackupCipher.Decrypt(key, tampered));
        }
    }

    [Fact]
    public void Given_AnotherNodesKey_When_Decrypted_Then_ItIsRefusedAsNotOurs()
    {
        // Arrange
        var blob = ChannelBackupCipher.Encrypt(ChannelBackupCipher.DeriveKey(s_nodeKey), s_plaintext);

        // Act / Assert
        var e = Assert.Throws<ChannelBackupAuthenticationException>(
            () => ChannelBackupCipher.Decrypt(ChannelBackupCipher.DeriveKey(s_otherNodeKey), blob));
        Assert.Contains("another node", e.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(29)]
    [InlineData(45)]
    public void Given_ATruncatedBackup_When_Decrypted_Then_ItIsRefusedAsMalformed(int length)
    {
        // Arrange
        var key = ChannelBackupCipher.DeriveKey(s_nodeKey);
        var blob = ChannelBackupCipher.Encrypt(key, s_plaintext)[..length];

        // Act / Assert
        Assert.Throws<ChannelBackupFormatException>(() => ChannelBackupCipher.Decrypt(key, blob));
    }

    [Fact]
    public void Given_NotABackup_When_Decrypted_Then_ItIsRefusedForItsMagic()
    {
        // Arrange
        var key = ChannelBackupCipher.DeriveKey(s_nodeKey);
        var blob = ChannelBackupCipher.Encrypt(key, s_plaintext);
        blob[0] = (byte)'X';

        // Act / Assert
        var e = Assert.Throws<ChannelBackupFormatException>(() => ChannelBackupCipher.Decrypt(key, blob));
        Assert.Contains("magic", e.Message);
    }
}