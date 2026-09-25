using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion;

using Domain.Protocol.Onion.Constants;
using Infrastructure.Bitcoin.Onion;

public class SphinxKeyGeneratorTests
{
    // BOLT 4 onion-error-test.json hops[0]
    private static readonly byte[] s_hop0SharedSecret =
        Convert.FromHexString("53eb63ea8a3fec3b3cd433b85cd62a4b145e1dda09391b348c4e1cd36a03ea66");

    private static readonly byte[] s_hop0AmmagKey =
        Convert.FromHexString("3761ba4d3e726d8abb16cba5950ee976b84937b61b7ad09e741724d7dee12eb5");

    private static readonly byte[] s_hop0PubKey =
        Convert.FromHexString("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619");

    [Fact]
    public void Given_Hop0SharedSecret_When_DerivingAmmagKey_Then_MatchesBolt4Vector()
    {
        // Arrange
        using var keyGenerator = new SphinxKeyGenerator();

        // Act
        var ammagKey = keyGenerator.DeriveKey(OnionConstants.Ammag, s_hop0SharedSecret);

        // Assert
        Assert.Equal(s_hop0AmmagKey, ammagKey);
    }

    [Theory]
    [InlineData("rho")]
    [InlineData("mu")]
    [InlineData("um")]
    [InlineData("pad")]
    [InlineData("ammag")]
    [InlineData("ammagext")]
    [InlineData("blinded_node_id")]
    public void Given_Label_When_DerivingKey_Then_EqualsHmacSha256KeyedWithLabel(string label)
    {
        // Arrange
        using var keyGenerator = new SphinxKeyGenerator();
        var labelBytes = System.Text.Encoding.ASCII.GetBytes(label);
        var expected = HMACSHA256.HashData(labelBytes, s_hop0SharedSecret);

        // Act
        var key = keyGenerator.DeriveKey(labelBytes, s_hop0SharedSecret);

        // Assert
        Assert.Equal(expected, key);
    }

    [Fact]
    public void Given_EphemeralKeyAndSecret_When_ComputingBlindingFactor_Then_EqualsSha256OfConcatenation()
    {
        // Arrange
        using var keyGenerator = new SphinxKeyGenerator();
        var expected = SHA256.HashData(s_hop0PubKey.Concat(s_hop0SharedSecret).ToArray());

        // Act
        var blindingFactor = keyGenerator.ComputeBlindingFactor(s_hop0PubKey, s_hop0SharedSecret);

        // Assert
        Assert.Equal(expected, blindingFactor);
    }

    [Fact]
    public void Given_ShortSecret_When_DerivingKey_Then_ThrowsArgumentException()
    {
        // Arrange
        using var keyGenerator = new SphinxKeyGenerator();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => keyGenerator.DeriveKey(OnionConstants.Rho, new byte[31]));
    }

    [Fact]
    public void Given_EmptyLabel_When_DerivingKey_Then_ThrowsArgumentException()
    {
        // Arrange
        using var keyGenerator = new SphinxKeyGenerator();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => keyGenerator.DeriveKey(ReadOnlySpan<byte>.Empty, s_hop0SharedSecret));
    }

    [Fact]
    public void Given_WrongLengthEphemeralKey_When_ComputingBlindingFactor_Then_ThrowsArgumentException()
    {
        // Arrange
        using var keyGenerator = new SphinxKeyGenerator();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => keyGenerator.ComputeBlindingFactor(new byte[32], s_hop0SharedSecret));
    }

    [Fact]
    public void Given_Data_When_ComputingSha256_Then_EqualsBclSha256()
    {
        // Arrange
        using var keyGenerator = new SphinxKeyGenerator();
        var data = Enumerable.Range(0, 1366).Select(i => (byte)i).ToArray();

        // Act
        var hash = keyGenerator.ComputeSha256(data);

        // Assert
        Assert.Equal(SHA256.HashData(data), hash);
    }

    [Fact]
    public void Given_ValidCompressedKey_When_CheckingPublicKey_Then_ReturnsTrue()
    {
        // Act & Assert
        Assert.True(SphinxKeyGenerator.IsValidPublicKey(s_hop0PubKey));
    }

    [Theory]
    [InlineData("04eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619")] // bad prefix
    [InlineData("02ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")] // x not on curve
    [InlineData("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f2836866")] // 32 bytes
    public void Given_InvalidKey_When_CheckingPublicKey_Then_ReturnsFalse(string hex)
    {
        // Act & Assert
        Assert.False(SphinxKeyGenerator.IsValidPublicKey(Convert.FromHexString(hex)));
    }
}