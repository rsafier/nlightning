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
}