namespace NLightning.Infrastructure.Bitcoin.Tests.Managers;

using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Managers;

public class SecureKeyManagerTests
{
    [Fact]
    public void Given_PublicKey_When_ComputingNodeSharedSecret_Then_MatchesEcdhWithNodeKey()
    {
        // Arrange
        var ecdh = new Ecdh();
        var remoteKey = ecdh.GenerateKeyPair();
        var nodePrivateKey = ecdh.GenerateKeyPair().PrivKey.Value;
        using var keyManager = new SecureKeyManager((byte[])nodePrivateKey.Clone(), BitcoinNetwork.Regtest,
                                                    Path.Combine(Path.GetTempPath(), "unused.key.json"), 0);
        var expected = new byte[32];
        ecdh.SecP256K1Dh(nodePrivateKey, remoteKey.CompactPubKey, expected);
        var sharedSecret = new byte[32];

        // Act
        keyManager.ComputeNodeSharedSecret(remoteKey.CompactPubKey, sharedSecret);

        // Assert
        Assert.Equal(expected, sharedSecret);
        Assert.Equal(nodePrivateKey, keyManager.GetNodeKeyPair().PrivKey.Value);
    }

    [Fact]
    public void Given_DisposedKeyManager_When_ComputingNodeSharedSecret_Then_ThrowsInvalidOperationException()
    {
        // Arrange
        var ecdh = new Ecdh();
        var remoteKey = ecdh.GenerateKeyPair();
        var keyManager = new SecureKeyManager(ecdh.GenerateKeyPair().PrivKey.Value.ToArray(), BitcoinNetwork.Regtest,
                                              Path.Combine(Path.GetTempPath(), "unused.key.json"), 0);
        keyManager.Dispose();
        var sharedSecret = new byte[32];

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => keyManager.ComputeNodeSharedSecret(remoteKey.CompactPubKey,
                                                                                            sharedSecret));
    }

    [Fact]
    public void Given_InvalidPublicKey_When_ComputingNodeSharedSecret_Then_ThrowsArgumentException()
    {
        // Arrange
        var ecdh = new Ecdh();
        using var keyManager = new SecureKeyManager(ecdh.GenerateKeyPair().PrivKey.Value.ToArray(),
                                                    BitcoinNetwork.Regtest,
                                                    Path.Combine(Path.GetTempPath(), "unused.key.json"), 0);
        var publicKey = new byte[33];
        publicKey[0] = 0x02;
        var sharedSecret = new byte[32];

        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => keyManager.ComputeNodeSharedSecret(publicKey, sharedSecret));
    }

    [Fact]
    public void Given_WarmKeyManager_When_ComputingNodeSharedSecret_Then_AllocatesOnlyTheWipedKeyCopy()
    {
        // Arrange: the peel hot path runs this once per HTLC (twice with a path_key)
        const int iterations = 100;
        var ecdh = new Ecdh();
        byte[] remoteKey = ecdh.GenerateKeyPair().CompactPubKey;
        using var keyManager = new SecureKeyManager(ecdh.GenerateKeyPair().PrivKey.Value.ToArray(),
                                                    BitcoinNetwork.Regtest,
                                                    Path.Combine(Path.GetTempPath(), "unused.key.json"), 0);
        var sharedSecret = new byte[32];
        keyManager.ComputeNodeSharedSecret(remoteKey, sharedSecret);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            keyManager.ComputeNodeSharedSecret(remoteKey, sharedSecret);
        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;

        // Assert: the private key copy and the parsed ECPrivKey, but no hash object or NBitcoin Key/PubKey wrappers
        Assert.True(perCall <= 512, $"Allocated {perCall} bytes per call.");
    }
}