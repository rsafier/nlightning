namespace NLightning.Infrastructure.Tests.Node.PeerStorage;

using Domain.Node.PeerStorage;
using Domain.Protocol.Enums;
using Domain.Protocol.Interfaces;
using Infrastructure.Crypto.Functions;
using Infrastructure.Node.PeerStorage;

public class PeerStorageCipherTests
{
    [Fact]
    public void Given_APlaintext_When_EncryptedAndDecrypted_Then_ItComesBack()
    {
        // Arrange
        var cipher = new PeerStorageCipher(CreateKeyManager(1).Object);
        var plaintext = "channel list"u8.ToArray();

        // Act
        var blob = cipher.Encrypt(plaintext);
        var decrypted = cipher.TryDecrypt(blob);

        // Assert
        Assert.Equal(plaintext, decrypted);
        Assert.Equal(PeerStorageCipher.Version, blob[0]);
        Assert.DoesNotContain("channel list"u8.ToArray(), Chunks(blob, plaintext.Length));
    }

    [Fact]
    public void Given_AMaximalPlaintext_When_Encrypted_Then_TheBlobIsExactlyTheBolt1Maximum()
    {
        // Arrange
        var cipher = new PeerStorageCipher(CreateKeyManager(1).Object);

        // Act
        var blob = cipher.Encrypt(new byte[cipher.MaxPlaintextLength]);

        // Assert
        Assert.Equal(PeerStorageConstants.MaxBlobLength, blob.Length);
        Assert.Throws<ArgumentException>(() => cipher.Encrypt(new byte[cipher.MaxPlaintextLength + 1]));
    }

    [Fact]
    public void Given_TheSamePlaintextTwice_When_Encrypted_Then_TheBlobsDiffer()
    {
        // Arrange
        var cipher = new PeerStorageCipher(CreateKeyManager(1).Object);

        // Act
        var first = cipher.Encrypt(new byte[64]);
        var second = cipher.Encrypt(new byte[64]);

        // Assert: fresh nonce
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(-1)]
    public void Given_AnAlteredBlob_When_Decrypted_Then_Null(int index)
    {
        // Arrange
        var cipher = new PeerStorageCipher(CreateKeyManager(1).Object);
        var blob = cipher.Encrypt(new byte[64]);
        blob[index < 0 ? blob.Length - 1 : index] ^= 0x01;

        // Act & Assert
        Assert.Null(cipher.TryDecrypt(blob));
    }

    [Fact]
    public void Given_ABlobOfAnotherNode_When_Decrypted_Then_Null()
    {
        // Arrange
        var blob = new PeerStorageCipher(CreateKeyManager(1).Object).Encrypt(new byte[64]);
        var other = new PeerStorageCipher(CreateKeyManager(2).Object);

        // Act & Assert
        Assert.Null(other.TryDecrypt(blob));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(40)]
    public void Given_ATooShortBlob_When_Decrypted_Then_Null(int length)
    {
        // Arrange
        var cipher = new PeerStorageCipher(CreateKeyManager(1).Object);
        var blob = new byte[length];
        if (length > 0)
            blob[0] = PeerStorageCipher.Version;

        // Act & Assert
        Assert.Null(cipher.TryDecrypt(blob));
    }

    private static Mock<ISecureKeyManager> CreateKeyManager(byte fill)
    {
        var secret = Enumerable.Repeat(fill, 32).ToArray();
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.EncryptNodeData(It.IsAny<NodeDataPurpose>(), It.IsAny<byte[]>(),
                                                It.IsAny<byte[]>(), It.IsAny<byte[]>()))
                  .Returns((NodeDataPurpose purpose, byte[] nonce, byte[] ad, byte[] plaintext) =>
                               NodeAuxiliaryCrypto.Encrypt(secret, purpose, nonce, ad, plaintext));
        keyManager.Setup(k => k.DecryptNodeData(It.IsAny<NodeDataPurpose>(), It.IsAny<byte[]>(),
                                                It.IsAny<byte[]>(), It.IsAny<byte[]>()))
                  .Returns((NodeDataPurpose purpose, byte[] nonce, byte[] ad, byte[] ciphertext) =>
                               NodeAuxiliaryCrypto.Decrypt(secret, purpose, nonce, ad, ciphertext));
        return keyManager;
    }

    private static IEnumerable<byte[]> Chunks(byte[] data, int length)
    {
        for (var i = 0; i + length <= data.Length; i++)
            yield return data.AsSpan(i, length).ToArray();
    }
}