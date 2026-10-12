namespace NLightning.Domain.Tests.Protocol.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;

using static OnionMessageVectorValues;

/// <summary>
/// BOLT 4 <c>blinded_path</c> (OM0-T3, OM-W-05): the vector's <c>route</c>, <c>num_hops</c> 0 and <c>enclen</c>
/// overflow, and the mapping to M5's <see cref="BlindedPath"/>.
/// </summary>
public class BlindedPathCodecTests
{
    private static readonly CompactPubKey s_otherNodeId =
        new(Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c"));

    private static readonly string[] s_hopNodeIds =
        [Hop0BlindedNodeIdHex, Hop1BlindedNodeIdHex, Hop2BlindedNodeIdHex, Hop3BlindedNodeIdHex];

    private static readonly string[] s_hopData =
    [
        Hop0EncryptedRecipientDataHex, Hop1EncryptedRecipientDataHex, Hop2EncryptedRecipientDataHex,
        Hop3EncryptedRecipientDataHex
    ];

    private static WireBlindedPath VectorRoute() =>
        new(SciddirOrPubkey.FromNodeId(new CompactPubKey(Convert.FromHexString(FirstNodeIdHex))),
            new CompactPubKey(Convert.FromHexString(FirstPathKeyHex)),
            s_hopNodeIds.Zip(s_hopData, (n, d) => new BlindedPathHop(new CompactPubKey(Convert.FromHexString(n)),
                                                                     Convert.FromHexString(d)))
                        .ToList());

    [Fact]
    public void Given_VectorRoute_When_Decoded_Then_EveryFieldMatches()
    {
        // Arrange
        var wire = Convert.FromHexString(RouteWireHex);

        // Act
        var path = BlindedPathCodec.Decode(wire);

        // Assert
        Assert.Equal(Convert.FromHexString(FirstNodeIdHex), (byte[])path.FirstNode.NodeId!.Value);
        Assert.Equal(Convert.FromHexString(FirstPathKeyHex), (byte[])path.FirstPathKey);
        Assert.Equal(4, path.Hops.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(Convert.FromHexString(s_hopNodeIds[i]), (byte[])path.Hops[i].BlindedNodeId);
            Assert.Equal(Convert.FromHexString(s_hopData[i]), path.Hops[i].EncryptedRecipientData.ToArray());
        }
    }

    [Fact]
    public void Given_VectorRoute_When_Encoded_Then_WireIsByteExact()
    {
        // Act
        var bytes = BlindedPathCodec.Encode(VectorRoute());

        // Assert
        Assert.Equal(Convert.FromHexString(RouteWireHex), bytes);
        Assert.Equal(bytes.Length, BlindedPathCodec.GetLength(VectorRoute()));
    }

    [Fact]
    public void Given_ScidIntroductionNode_When_RoundTripped_Then_BytesAreIdentical()
    {
        // Arrange: the vector's route with first_node_id = direction 1 of SCID 700000x1x2
        var route = VectorRoute() with
        {
            FirstNode = SciddirOrPubkey.FromShortChannelId(new ShortChannelId(700_000, 1, 2), 1)
        };

        // Act
        var bytes = BlindedPathCodec.Encode(route);
        var decoded = BlindedPathCodec.Decode(bytes);

        // Assert
        Assert.Equal(Convert.FromHexString(RouteWireHex).Length - 24, bytes.Length);
        Assert.Equal((byte)1, bytes[0]);
        Assert.Equal(new ShortChannelId(700_000, 1, 2), decoded.FirstNode.ShortChannelId);
        Assert.Equal((byte)1, decoded.FirstNode.Direction);
        Assert.Equal(bytes, BlindedPathCodec.Encode(decoded));
    }

    [Fact]
    public void Given_NumHopsZero_When_Decoded_Then_Refused()
    {
        // Arrange: first_node_id, first_path_key, num_hops 0
        var wire = Convert.FromHexString(FirstNodeIdHex + FirstPathKeyHex + "00");

        // Act
        var ok = BlindedPathCodec.TryDecode(wire, out var path, out var reason);

        // Assert
        Assert.False(ok);
        Assert.Null(path);
        Assert.Contains("num_hops 0", reason);
    }

    [Fact]
    public void Given_EnclenPastTheEnd_When_Decoded_Then_Refused()
    {
        // Arrange: one hop whose enclen says 3 bytes, carrying 2
        var wire = Convert.FromHexString(FirstNodeIdHex + FirstPathKeyHex + "01" + Hop0BlindedNodeIdHex + "0003aabb");

        // Act
        var ok = BlindedPathCodec.TryDecode(wire, out _, out var reason);

        // Assert
        Assert.False(ok);
        Assert.Contains("enclen", reason);
    }

    [Fact]
    public void Given_MoreHopsThanNumHopsCarries_When_Decoded_Then_Refused()
    {
        // Arrange: num_hops 2, one hop present
        var wire = Convert.FromHexString(FirstNodeIdHex + FirstPathKeyHex + "02" + Hop0BlindedNodeIdHex + "0001aa");

        // Act & Assert
        Assert.False(BlindedPathCodec.TryDecode(wire, out _, out _));
    }

    public static TheoryData<string, string> MalformedPaths => new()
    {
        { "first_node_id prefix", "04" + FirstNodeIdHex[2..] + FirstPathKeyHex + "01" + Hop0BlindedNodeIdHex + "0000" },
        { "first_path_key prefix", FirstNodeIdHex + "05" + FirstPathKeyHex[2..] + "01" + Hop0BlindedNodeIdHex + "0000" },
        { "blinded_node_id prefix", FirstNodeIdHex + FirstPathKeyHex + "01" + "00" + Hop0BlindedNodeIdHex[2..] + "0000" },
        { "truncated first_path_key", FirstNodeIdHex + FirstPathKeyHex[..40] },
        { "no num_hops", FirstNodeIdHex + FirstPathKeyHex },
        { "truncated enclen", FirstNodeIdHex + FirstPathKeyHex + "01" + Hop0BlindedNodeIdHex + "00" },
        { "empty", "" }
    };

    [Theory]
    [MemberData(nameof(MalformedPaths))]
    public void Given_MalformedPath_When_Decoded_Then_Refused(string defect, string hex)
    {
        // Act
        var ok = BlindedPathCodec.TryDecode(Convert.FromHexString(hex), out var path, out var reason);

        // Assert
        Assert.False(ok, defect);
        Assert.Null(path);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Given_TrailingBytes_When_DecodedExactly_Then_Refused()
    {
        // Arrange
        var wire = Convert.FromHexString(RouteWireHex + "00");

        // Act
        var ok = BlindedPathCodec.TryDecode(wire, out _, out var reason);
        var readOk = BlindedPathCodec.TryRead(wire, out _, out var bytesRead, out _);

        // Assert
        Assert.False(ok);
        Assert.Contains("follow", reason);
        Assert.True(readOk);
        Assert.Equal(wire.Length - 1, bytesRead);
        Assert.Throws<FormatException>(() => BlindedPathCodec.Decode(wire));
    }

    [Fact]
    public void Given_EmptyEncryptedRecipientData_When_RoundTripped_Then_Kept()
    {
        // Arrange
        var wire = Convert.FromHexString(FirstNodeIdHex + FirstPathKeyHex + "01" + Hop0BlindedNodeIdHex + "0000");

        // Act
        var path = BlindedPathCodec.Decode(wire);

        // Assert
        Assert.Equal(0, path.Hops[0].EncryptedRecipientData.Length);
        Assert.Equal(wire, BlindedPathCodec.Encode(path));
    }

    [Fact]
    public void Given_TwoPaths_When_ListRoundTripped_Then_BothAreKept()
    {
        // Arrange
        var second = VectorRoute() with { Hops = [VectorRoute().Hops[3]] };
        var bytes = BlindedPathCodec.EncodeList([VectorRoute(), second]);

        // Act
        var ok = BlindedPathCodec.TryReadList(bytes, out var paths, out _);

        // Assert
        Assert.True(ok);
        Assert.Equal(2, paths!.Count);
        Assert.Equal(4, paths[0].Hops.Count);
        Assert.Single(paths[1].Hops);
        Assert.Equal(bytes, BlindedPathCodec.EncodeList(paths));
    }

    [Fact]
    public void Given_EmptyList_When_Read_Then_NoPath()
    {
        // Act
        var ok = BlindedPathCodec.TryReadList([], out var paths, out _);

        // Assert
        Assert.True(ok);
        Assert.Empty(paths!);
    }

    [Fact]
    public void Given_ListWithABadSecondPath_When_Read_Then_RefusedWithItsIndex()
    {
        // Arrange
        var bytes = Convert.FromHexString(RouteWireHex + FirstNodeIdHex + FirstPathKeyHex + "00");

        // Act
        var ok = BlindedPathCodec.TryReadList(bytes, out var paths, out var reason);

        // Assert
        Assert.False(ok);
        Assert.Null(paths);
        Assert.StartsWith("blinded_path 1:", reason);
    }

    public static TheoryData<int> UnencodableHopCounts => new() { 0, 256 };

    [Theory]
    [MemberData(nameof(UnencodableHopCounts))]
    public void Given_HopCountOutOfRange_When_Encoded_Then_Throws(int hopCount)
    {
        // Arrange
        var hop = VectorRoute().Hops[0];
        var route = VectorRoute() with { Hops = Enumerable.Repeat(hop, hopCount).ToList() };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => BlindedPathCodec.Encode(route));
    }

    [Fact]
    public void Given_EncryptedDataLongerThanU16_When_Encoded_Then_Throws()
    {
        // Arrange
        var hop = new BlindedPathHop(VectorRoute().Hops[0].BlindedNodeId, new byte[ushort.MaxValue + 1]);
        var route = VectorRoute() with { Hops = [hop] };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => BlindedPathCodec.Encode(route));
    }

    [Fact]
    public void Given_255Hops_When_RoundTripped_Then_Kept()
    {
        // Arrange
        var hop = VectorRoute().Hops[1];
        var route = VectorRoute() with { Hops = Enumerable.Repeat(hop, 255).ToList() };

        // Act
        var decoded = BlindedPathCodec.Decode(BlindedPathCodec.Encode(route));

        // Assert
        Assert.Equal(255, decoded.Hops.Count);
    }

    [Fact]
    public void Given_NodeIdIntroduction_When_MappedToM5_Then_SameFields()
    {
        // Arrange
        var route = VectorRoute();

        // Act
        var ok = BlindedPathCodec.TryToBlindedPath(route, out var blindedPath);
        var back = WireBlindedPath.FromBlindedPath(blindedPath!);

        // Assert
        Assert.True(ok);
        Assert.Equal(route.FirstNode.NodeId, blindedPath!.FirstNodeId);
        Assert.Equal(route.FirstPathKey, blindedPath.FirstPathKey);
        Assert.Same(route.Hops, blindedPath.Hops);
        Assert.Equal(BlindedPathCodec.Encode(route), BlindedPathCodec.Encode(back));
    }

    [Fact]
    public void Given_ScidIntroduction_When_MappedToM5_Then_NeedsAResolvedNodeId()
    {
        // Arrange
        var route = VectorRoute() with
        {
            FirstNode = SciddirOrPubkey.FromShortChannelId(new ShortChannelId(1, 2, 3), 0)
        };

        // Act
        var ok = BlindedPathCodec.TryToBlindedPath(route, out var unresolved);
        var resolved = BlindedPathCodec.ToBlindedPath(route, s_otherNodeId);

        // Assert
        Assert.False(ok);
        Assert.Null(unresolved);
        Assert.Equal(s_otherNodeId, resolved.FirstNodeId);
        Assert.Equal(route.FirstPathKey, resolved.FirstPathKey);
    }

    [Fact]
    public void Given_NodeIdIntroduction_When_ResolvedToAnotherNode_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => BlindedPathCodec.ToBlindedPath(VectorRoute(), s_otherNodeId));
    }
}