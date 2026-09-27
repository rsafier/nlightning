namespace NLightning.Application.Tests.OnionMessages;

using Application.OnionMessages;
using Application.Tests.Payments;
using Domain.Channels.ValueObjects;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;

public class OnionMessagePayloadCodecTests
{
    private static readonly WireBlindedPath s_scidPath = new(
        SciddirOrPubkey.FromShortChannelId(new ShortChannelId(700_000, 3, 1), 1),
        new TestNodeKeyManager(5).NodeId,
        [
            new BlindedPathHop(new TestNodeKeyManager(6).NodeId, new byte[] { 1, 2, 3 }),
            new BlindedPathHop(new TestNodeKeyManager(7).NodeId, new byte[20])
        ]);

    [Fact]
    public void Given_AllKnownFields_When_EncodedAndDecoded_Then_RoundTrips()
    {
        // Arrange
        var tlvs = new OnionMessageTlvs(s_scidPath, new byte[] { 9, 9 },
                                        [new OnionMessageTlvRecord(33, new byte[] { 1 }),
                                         new OnionMessageTlvRecord(64, new byte[300])]);

        // Act
        var encoded = OnionMessagePayloadCodec.Encode(tlvs);
        var ok = OnionMessagePayloadCodec.TryDecode(encoded, out var decoded);

        // Assert
        Assert.True(ok);
        Assert.NotNull(decoded);
        Assert.Equal(new byte[] { 9, 9 }, decoded.EncryptedRecipientData!.Value.ToArray());
        Assert.Equal([33UL, 64UL], decoded.OtherRecords.Select(r => r.Type));
        Assert.Equal(300, decoded.OtherRecords[1].Value.Length);
        var path = decoded.ReplyPath!;
        Assert.Equal(new ShortChannelId(700_000, 3, 1), path.FirstNode.ShortChannelId);
        Assert.Equal(1, path.FirstNode.Direction);
        Assert.Equal(s_scidPath.FirstPathKey, path.FirstPathKey);
        Assert.Equal(2, path.Hops.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, path.Hops[0].EncryptedRecipientData.ToArray());
        Assert.Equal(encoded, OnionMessagePayloadCodec.Encode(decoded));
    }

    [Theory]
    [InlineData("0401aa0201bb")] // types not increasing
    [InlineData("0402aa")] // length past the end
    [InlineData("0401aa0401bb")] // repeated type
    [InlineData("4601aa")] // unknown even type 70
    [InlineData("fd00040100")] // non-minimal bigsize type
    [InlineData("0200")] // empty reply_path
    public void Given_AnInvalidStream_When_Decoded_Then_Refused(string hex)
    {
        // Act
        var ok = OnionMessagePayloadCodec.TryDecode(Convert.FromHexString(hex), out var decoded);

        // Assert
        Assert.False(ok);
        Assert.Null(decoded);
    }

    [Fact]
    public void Given_AnEmptyStream_When_Decoded_Then_NoFields()
    {
        // Act
        var ok = OnionMessagePayloadCodec.TryDecode([], out var decoded);

        // Assert
        Assert.True(ok);
        Assert.Null(decoded!.EncryptedRecipientData);
        Assert.Empty(decoded.OtherRecords);
    }

    [Fact]
    public void Given_ABlindedPathWithZeroHops_When_Decoded_Then_Refused()
    {
        // Arrange
        var encoded = OnionMessagePayloadCodec.EncodeBlindedPath(s_scidPath);
        encoded[9 + 33] = 0;

        // Act
        var ok = OnionMessagePayloadCodec.TryDecodeBlindedPath(encoded.AsSpan(0, 9 + 33 + 1), out _, out _);

        // Assert
        Assert.False(ok);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0xff)]
    public void Given_AFirstNodeWithAnUnknownPrefix_When_Decoded_Then_Refused(byte prefix)
    {
        // Arrange
        var encoded = OnionMessagePayloadCodec.EncodeBlindedPath(s_scidPath);
        encoded[0] = prefix;

        // Act
        var ok = OnionMessagePayloadCodec.TryDecodeBlindedPath(encoded, out _, out _);

        // Assert
        Assert.False(ok);
    }

    [Fact]
    public void Given_ATruncatedHop_When_Decoded_Then_Refused()
    {
        // Arrange
        var encoded = OnionMessagePayloadCodec.EncodeBlindedPath(s_scidPath);

        // Act
        var ok = OnionMessagePayloadCodec.TryDecodeBlindedPath(encoded.AsSpan(0, encoded.Length - 1), out _, out _);

        // Assert
        Assert.False(ok);
    }

    [Fact]
    public void Given_ANodeIdFirstNode_When_RoundTripped_Then_Equal()
    {
        // Arrange
        var path = s_scidPath with { FirstNode = SciddirOrPubkey.FromNodeId(new TestNodeKeyManager(8).NodeId) };

        // Act
        var ok = OnionMessagePayloadCodec.TryDecodeBlindedPath(OnionMessagePayloadCodec.EncodeBlindedPath(path),
                                                               out var decoded, out var consumed);

        // Assert
        Assert.True(ok);
        Assert.Equal(path.FirstNode.NodeId, decoded!.FirstNode.NodeId);
        Assert.Equal(33 + 33 + 1 + 2 * (33 + 2) + 3 + 20, consumed);
    }

    [Fact]
    public void Given_ContentsWithReplyPathType_When_Encoded_Then_Throws()
    {
        // Arrange
        var tlvs = new OnionMessageTlvs(null, null, [new OnionMessageTlvRecord(2, new byte[] { 1 })]);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => OnionMessagePayloadCodec.Encode(tlvs));
    }
}