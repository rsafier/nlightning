namespace NLightning.Domain.Tests.Protocol.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;

using static OffersTestVectorValues;

/// <summary>
/// <c>blinded_path</c> and <c>sciddir_or_pubkey</c> against independent wire bytes: the <c>offer_paths</c> values of
/// the official <c>bolt12/offers-test.json</c> (OM0-T3), decoded field by field and re-encoded byte-exact.
/// </summary>
public class BlindedPathOffersVectorTests
{
    private static CompactPubKey Point02 => new(Convert.FromHexString(Point02Hex));

    private static WireBlindedPath VectorPath(SciddirOrPubkey firstNode, byte secondHopFill) =>
        new(firstNode, Point02,
            [
                new BlindedPathHop(Point02, new byte[16]),
                new BlindedPathHop(Point02, Enumerable.Repeat(secondHopFill, 8).ToArray())
            ]);

    private static WireBlindedPath BobPath() =>
        VectorPath(SciddirOrPubkey.FromNodeId(new CompactPubKey(Convert.FromHexString(BobNodeIdHex))), 0x11);

    private static void AssertVectorHops(WireBlindedPath path, byte secondHopFill)
    {
        Assert.Equal(Point02, path.FirstPathKey);
        Assert.Equal(2, path.Hops.Count);
        Assert.Equal(Point02, path.Hops[0].BlindedNodeId);
        Assert.Equal(new byte[16], path.Hops[0].EncryptedRecipientData.ToArray());
        Assert.Equal(Point02, path.Hops[1].BlindedNodeId);
        Assert.Equal(Enumerable.Repeat(secondHopFill, 8).ToArray(), path.Hops[1].EncryptedRecipientData.ToArray());
    }

    [Fact]
    public void Given_NodeIdIntroductionVector_When_Decoded_Then_EveryFieldMatches()
    {
        // Act
        var path = BlindedPathCodec.Decode(Convert.FromHexString(NodeIdIntroductionPathHex));

        // Assert
        Assert.True(path.FirstNode.IsNodeId);
        Assert.Equal(Convert.FromHexString(BobNodeIdHex), (byte[])path.FirstNode.NodeId!.Value);
        AssertVectorHops(path, 0x11);
    }

    [Fact]
    public void Given_NodeIdIntroductionPath_When_Encoded_Then_WireIsByteExact()
    {
        // Act
        var bytes = BlindedPathCodec.Encode(BobPath());

        // Assert
        Assert.Equal(Convert.FromHexString(NodeIdIntroductionPathHex), bytes);
    }

    [Fact]
    public void Given_ScidIntroductionVector_When_Decoded_Then_DirectionAndScidAreRead()
    {
        // Act
        var path = BlindedPathCodec.Decode(Convert.FromHexString(ScidIntroductionPathHex));

        // Assert: "short_channel_id is 0x0x42, direction is 0"
        Assert.False(path.FirstNode.IsNodeId);
        Assert.Equal((byte)0, path.FirstNode.Direction);
        Assert.Equal(new ShortChannelId(0, 0, 42), path.FirstNode.ShortChannelId);
        AssertVectorHops(path, 0x11);
    }

    [Fact]
    public void Given_ScidIntroductionPath_When_Encoded_Then_WireIsByteExact()
    {
        // Arrange
        var path = VectorPath(SciddirOrPubkey.FromShortChannelId(new ShortChannelId(0, 0, 42), 0), 0x11);

        // Act
        var bytes = BlindedPathCodec.Encode(path);

        // Assert
        Assert.Equal(Convert.FromHexString(ScidIntroductionPathHex), bytes);
    }

    [Fact]
    public void Given_TwoPathsVector_When_ReadAsList_Then_BothPathsMatch()
    {
        // Act
        var ok = BlindedPathCodec.TryReadList(Convert.FromHexString(TwoPathsHex), out var paths, out var reason);

        // Assert: the second path is "via 1x2x3 (direction 1)"
        Assert.True(ok, reason);
        Assert.Equal(2, paths!.Count);
        Assert.Equal(Convert.FromHexString(BobNodeIdHex), (byte[])paths[0].FirstNode.NodeId!.Value);
        AssertVectorHops(paths[0], 0x11);
        Assert.Equal((byte)1, paths[1].FirstNode.Direction);
        Assert.Equal(new ShortChannelId(1, 2, 3), paths[1].FirstNode.ShortChannelId);
        AssertVectorHops(paths[1], 0x22);
    }

    [Fact]
    public void Given_TwoPaths_When_EncodedAsList_Then_WireIsByteExact()
    {
        // Arrange
        var second = VectorPath(SciddirOrPubkey.FromShortChannelId(new ShortChannelId(1, 2, 3), 1), 0x22);

        // Act
        var bytes = BlindedPathCodec.EncodeList([BobPath(), second]);

        // Assert
        Assert.Equal(Convert.FromHexString(TwoPathsHex), bytes);
    }

    [Fact]
    public void Given_SecondPathEmptyVector_When_ReadAsList_Then_RefusedAtTheSecondPath()
    {
        // Act
        var ok = BlindedPathCodec.TryReadList(Convert.FromHexString(SecondPathEmptyHex), out var paths,
                                              out var reason);

        // Assert
        Assert.False(ok);
        Assert.Null(paths);
        Assert.StartsWith("blinded_path 1:", reason);
        Assert.Contains("num_hops 0", reason);
    }

    public static TheoryData<string, int> VectorIntroductionNodes => new()
    {
        { NodeIdIntroductionPathHex[..66], SciddirOrPubkeyCodec.NodeIdFormLength },
        { ScidIntroductionPathHex[..18], SciddirOrPubkeyCodec.ShortChannelIdFormLength },
        { TwoPathsHex[322..340],SciddirOrPubkeyCodec.ShortChannelIdFormLength }
    };

    [Theory]
    [MemberData(nameof(VectorIntroductionNodes))]
    public void Given_VectorFirstNodeId_When_SciddirOrPubkeyRoundTripped_Then_BytesAreIdentical(string hex, int length)
    {
        // Arrange: the leading sciddir_or_pubkey of each vector path, followed by its first_path_key
        var bytes = Convert.FromHexString(hex[..(length * 2)]);
        var withTail = Convert.FromHexString(hex[..(length * 2)] + Point02Hex);

        // Act
        var value = SciddirOrPubkeyCodec.Decode(bytes);
        var readOk = SciddirOrPubkeyCodec.TryRead(withTail, out var read, out var bytesRead, out _);

        // Assert
        Assert.Equal(bytes, SciddirOrPubkeyCodec.Encode(value));
        Assert.True(readOk);
        Assert.Equal(length, bytesRead);
        Assert.Equal(value, read);
    }
}