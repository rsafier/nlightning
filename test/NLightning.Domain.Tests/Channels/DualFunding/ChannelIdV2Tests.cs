using System.Security.Cryptography;

namespace NLightning.Domain.Tests.Channels.DualFunding;

using Domain.Channels.DualFunding;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;

/// <summary>
/// BOLT 2 "<c>channel_id</c>, v2": <c>SHA256(lesser-revocation-basepoint || greater-revocation-basepoint)</c>, and the
/// <c>temporary_channel_id</c> of <c>open_channel2</c> with a zeroed basepoint for the non-initiator. The expected ids
/// are the SHA256 of the ordered 66 bytes, computed outside this code base (Python <c>hashlib</c>) over BOLT 3
/// Appendix C's two basepoints.
/// </summary>
public class ChannelIdV2Tests
{
    // BOLT 3 Appendix C: local_revocation_basepoint (secret 1111...) and remote_revocation_basepoint (secret 2222...)
    private static readonly CompactPubKey s_basepointA =
        Convert.FromHexString("036d6caac248af96f6afa7f904f550253a0f3ef3f5aa2fe6838a95b216691468e2");

    private static readonly CompactPubKey s_basepointB =
        Convert.FromHexString("02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27");

    private static readonly ChannelId s_expectedId =
        Convert.FromHexString("1c6ddfb1a4cbf427942efa8d8c8286f0d8697d000dac801dd07180444dd18d9d");

    private static readonly ChannelId s_expectedTemporaryId =
        Convert.FromHexString("bf6905f04017f329fc2d4cebcf4d1a688af654facea605db9cf3736bf99696ea");

    [Fact]
    public void Given_TwoBasepoints_When_Derive_Then_TheIdIsSha256OfTheLesserThenTheGreater()
    {
        // Arrange
        using var sha256 = new BclSha256();

        // Act
        var channelId = ChannelIdV2.Derive(sha256, s_basepointA, s_basepointB);

        // Assert
        Assert.Equal(s_expectedId, channelId);
        Assert.Equal(SHA256.HashData([.. (byte[])s_basepointB, .. (byte[])s_basepointA]), (byte[])channelId);
    }

    [Fact]
    public void Given_TheSameBasepoints_When_DerivedFromEitherSide_Then_BothSidesAgree()
    {
        // Arrange
        using var sha256 = new BclSha256();

        // Act
        var initiatorView = ChannelIdV2.Derive(sha256, s_basepointA, s_basepointB);
        var accepterView = ChannelIdV2.Derive(sha256, s_basepointB, s_basepointA);

        // Assert
        Assert.Equal(initiatorView, accepterView);
    }

    [Fact]
    public void Given_TheInitiatorsBasepoint_When_DeriveTemporary_Then_ZeroesComeFirst()
    {
        // Arrange
        using var sha256 = new BclSha256();

        // Act
        var temporaryId = ChannelIdV2.DeriveTemporary(sha256, s_basepointA);

        // Assert
        Assert.Equal(s_expectedTemporaryId, temporaryId);
        Assert.Equal(SHA256.HashData([.. new byte[33], .. (byte[])s_basepointA]), (byte[])temporaryId);
        Assert.NotEqual(ChannelIdV2.Derive(sha256, s_basepointA, s_basepointB), temporaryId);
    }

    [Fact]
    public void Given_OneKeyForBothSides_When_Derive_Then_Throws()
    {
        // Arrange
        using var sha256 = new BclSha256();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => ChannelIdV2.Derive(sha256, s_basepointA, s_basepointA));
    }

    [Fact]
    public void Given_ASha256_When_DerivedTwice_Then_TheHashStateIsResetBetweenCalls()
    {
        // Arrange
        using var sha256 = new BclSha256();

        // Act
        var first = ChannelIdV2.Derive(sha256, s_basepointA, s_basepointB);
        var second = ChannelIdV2.Derive(sha256, s_basepointA, s_basepointB);

        // Assert
        Assert.Equal(first, second);
    }

    /// <summary>The Domain port over the BCL's incremental SHA-256 (Domain tests use no Infrastructure type).</summary>
    internal sealed class BclSha256 : ISha256
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void AppendData(ReadOnlySpan<byte> data) => _hash.AppendData(data);

        public void GetHashAndReset(Span<byte> hash) => _hash.GetHashAndReset(hash);

        public void Dispose() => _hash.Dispose();
    }
}