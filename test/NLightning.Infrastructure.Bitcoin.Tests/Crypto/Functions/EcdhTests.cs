using NLightning.Infrastructure.Bitcoin.Crypto.Functions;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.Functions;

public class EcdhTests
{
    // BOLT 4 onion-error-test.json / onion-test.json: session_key and hops[0] private key are both 0x41 x 32
    private static readonly byte[] s_sessionKey = Enumerable.Repeat((byte)0x41, 32).ToArray();
    private static readonly byte[] s_hop0PrivKey = Enumerable.Repeat((byte)0x41, 32).ToArray();

    private static readonly byte[] s_hop0PubKey =
        Convert.FromHexString("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619");

    private static readonly byte[] s_hop0SharedSecret =
        Convert.FromHexString("53eb63ea8a3fec3b3cd433b85cd62a4b145e1dda09391b348c4e1cd36a03ea66");

    [Fact]
    public void Given_Bolt4SessionKeyAndHop0PubKey_When_SecP256K1DhIsCalled_Then_ItMatchesHopSharedSecret()
    {
        // Arrange
        var ecdh = new Ecdh();
        var sharedSecret = new byte[32];

        // Act
        ecdh.SecP256K1Dh(s_sessionKey, s_hop0PubKey, sharedSecret);

        // Assert
        Assert.Equal(s_hop0SharedSecret, sharedSecret);
    }

    [Fact]
    public void Given_Bolt4Hop0PrivKeyAndSessionPubKey_When_SecP256K1DhIsCalled_Then_ItMatchesHopSharedSecret()
    {
        // Arrange
        var ecdh = new Ecdh();
        var sessionPubKey = ecdh.GenerateKeyPair(s_sessionKey).CompactPubKey;
        var sharedSecret = new byte[32];

        // Act
        ecdh.SecP256K1Dh(s_hop0PrivKey, sessionPubKey, sharedSecret);

        // Assert
        Assert.Equal(s_hop0SharedSecret, sharedSecret);
    }

    [Fact]
    public void Given_InvalidPrivateKeyLength_When_GenerateKeyPairIsCalled_Then_ItThrowsArgumentException()
    {
        // Arrange
        var ecdh = new Ecdh();
        var privateKey = new byte[31];

        // Assert
        var exception = Assert.Throws<ArgumentException>(Act);
        Assert.Equal("Invalid private key length", exception.Message);
        return;

        // Act
        void Act() => ecdh.GenerateKeyPair(privateKey);
    }
}