namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Infrastructure.Bitcoin.Onion.OnionMessages;

/// <summary>
/// OM1-T2: the onion-message writer (OM-S-01..03, 05): size choice, prefix hops, the final hop's records.
/// </summary>
public class OnionMessagePacketBuilderTests
{
    private readonly OnionMessageTestKit _kit = new();

    [Theory]
    [InlineData(1300, OnionMessageConstants.SmallPayloadsLength)]
    [InlineData(1301, OnionMessageConstants.LargePayloadsLength)]
    [InlineData(32768, OnionMessageConstants.LargePayloadsLength)]
    public void Given_FramedHopsOfATotalSize_When_ChoosingThePayloadsLength_Then_SmallestThatFits(int total,
        int expected)
    {
        // Arrange: one hop whose framed size (3-byte bigsize length + payload + 32-byte hmac) is the total
        var hops = new[] { new OnionHop(_kit.NodeIds[0], new byte[total - 32 - 3]) };

        // Act
        var length = OnionMessagePacketBuilder.ChoosePayloadsLength(hops);

        // Assert
        Assert.Equal(expected, length);
    }

    [Fact]
    public void Given_ContentsBeyond32768Bytes_When_Building_Then_Refused()
    {
        // Arrange
        var path = _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..1]);
        var contents = OnionMessageContents.Single(65, new byte[32_768]);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _kit.PacketBuilder.Build([], path, contents, null));
    }

    [Fact]
    public void Given_NoPrefix_When_Building_Then_TheMessageCarriesThePathsFirstPathKeyAndIsDelivered()
    {
        // Arrange: the introduction node is our peer
        var path = _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..3], new byte[] { 7 });
        var contents = OnionMessageContents.Single(OnionMessageConstants.InvoiceRequestType, new byte[] { 1, 2 });

        // Act
        var message = _kit.PacketBuilder.Build([], path, contents, null);
        var results = _kit.UnwrapChain(message);

        // Assert
        Assert.Equal(path.FirstPathKey, message.Payload.PathKey);
        Assert.Equal(OnionMessageConstants.SmallPayloadsLength + OnionConstants.PacketOverheadLength,
                     message.Payload.OnionMessagePacket.Length);
        Assert.Equal(3, results.Count);
        Assert.Equal(OnionMessageUnwrapStatus.Deliver, results[^1].Status);
        Assert.Equal(new byte[] { 7 }, results[^1].PathId!.Value.ToArray());
        var record = Assert.Single(results[^1].Payload!.OtherRecords);
        Assert.Equal(OnionMessageConstants.InvoiceRequestType, record.Type);
    }

    [Fact]
    public void Given_ATwoHopPrefix_When_Building_Then_EveryPrefixHopForwardsWithOnlyEncryptedRecipientData()
    {
        // Arrange: nodes 0 and 1 are unblinded prefix hops, the destination path is 2 -> 3 (OM-S-03, OM-S-05)
        var path = _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[2..], new byte[] { 9 });

        // Act
        var message = _kit.PacketBuilder.Build(_kit.NodeIds[..2], path, OnionMessageContents.Single(1, "hi"u8.ToArray()),
                                               null);
        var results = _kit.UnwrapChain(message);

        // Assert: a non-final hop with any other record would be ignored, so a forward proves only type 4
        Assert.Equal(4, results.Count);
        Assert.Equal([_kit.NodeIds[1], _kit.NodeIds[2], _kit.NodeIds[3]],
                     results[..3].Select(r => r.NextNodeId!.Value));
        Assert.Null(results[0].RecipientData!.NextPathKeyOverride);
        Assert.Equal(path.FirstPathKey, results[1].RecipientData!.NextPathKeyOverride);
        Assert.Equal(path.FirstPathKey, results[1].NextMessage!.Payload.PathKey);
        Assert.Equal(OnionMessageUnwrapStatus.Deliver, results[3].Status);
    }

    [Fact]
    public void Given_AReplyPath_When_Delivered_Then_TheRecipientReadsTheSameBlindedPath()
    {
        // Arrange: a reply path to node 0 through node 1, and one whose introduction is a SCID with direction
        var replyPath = WireBlindedPath.FromBlindedPath(_kit.PathBuilder.CreateMessagePath(
                                                            [_kit.NodeIds[1], _kit.NodeIds[0]], new byte[] { 5 }));
        var scidReplyPath = replyPath with
        {
            FirstNode = SciddirOrPubkey.FromShortChannelId(new ShortChannelId(700_000, 12, 1), 1)
        };
        var path = _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[2..]);

        foreach (var reply in new[] { replyPath, scidReplyPath })
        {
            // Act
            var message = _kit.PacketBuilder.Build([], path, OnionMessageContents.Single(1, new byte[] { 1 }), reply);
            var delivered = _kit.UnwrapChain(message, 2)[^1];

            // Assert
            Assert.Equal(OnionMessageUnwrapStatus.Deliver, delivered.Status);
            var received = delivered.Payload!.ReplyPath!;
            Assert.Equal(reply.FirstNode, received.FirstNode);
            Assert.Equal(reply.FirstPathKey, received.FirstPathKey);
            Assert.Equal(reply.Hops.Select(h => h.BlindedNodeId), received.Hops.Select(h => h.BlindedNodeId));
            Assert.Equal(reply.Hops.Select(h => h.EncryptedRecipientData.ToArray()),
                         received.Hops.Select(h => h.EncryptedRecipientData.ToArray()));
        }
    }

    [Theory]
    [InlineData(OnionMessageConstants.ReplyPathType)]
    [InlineData(OnionMessageConstants.EncryptedRecipientDataType)]
    [InlineData(0UL)]
    [InlineData(6UL)]
    [InlineData(70UL)]
    [InlineData(1000UL)]
    public void Given_ContentsWithAReservedType_When_Building_Then_Refused(ulong type)
    {
        // Arrange
        var path = _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..1]);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
                                             _kit.PacketBuilder.Build([], path, OnionMessageContents.Single(type, new byte[] { 1 }),
                                                                      null));
    }

    [Theory]
    [InlineData(OnionMessageConstants.InvoiceRequestType)]
    [InlineData(OnionMessageConstants.InvoiceType)]
    [InlineData(OnionMessageConstants.InvoiceErrorType)]
    [InlineData(71UL)]
    public void Given_ContentsWithAKnownEvenOrAnOddType_When_Building_Then_TheRecipientGetsThem(ulong type)
    {
        // Arrange
        var path = _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..1]);

        // Act
        var message = _kit.PacketBuilder.Build([], path, OnionMessageContents.Single(type, new byte[] { 1 }), null);
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        Assert.Equal(OnionMessageUnwrapStatus.Deliver, result.Status);
        Assert.Equal(type, Assert.Single(result.Payload!.OtherRecords).Type);
    }

    [Fact]
    public void Given_ContentsWithADuplicateType_When_Building_Then_Refused()
    {
        // Arrange
        var path = _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..1]);
        var contents = new OnionMessageContents([new OnionMessageTlvRecord(65, new byte[] { 1 }),
                                                 new OnionMessageTlvRecord(65, new byte[] { 2 })]);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _kit.PacketBuilder.Build([], path, contents, null));
    }

    [Fact]
    public void Given_ContentsOutOfOrder_When_Building_Then_TheyAreWrittenInAscendingOrder()
    {
        // Arrange
        var path = _kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..1]);
        var contents = new OnionMessageContents([new OnionMessageTlvRecord(67, new byte[] { 2 }),
                                                 new OnionMessageTlvRecord(1, new byte[] { 1 })]);

        // Act
        var delivered = _kit.UnwrapChain(_kit.PacketBuilder.Build([], path, contents, null))[^1];

        // Assert
        Assert.Equal(OnionMessageUnwrapStatus.Deliver, delivered.Status);
        Assert.Equal([1UL, 67UL], delivered.Payload!.OtherRecords.Select(r => r.Type));
    }

    [Fact]
    public void Given_AnEmptyPath_When_Building_Then_Refused()
    {
        // Arrange
        var path = new BlindedPath(_kit.NodeIds[0], _kit.NodeIds[1], []);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _kit.PacketBuilder.Build([], path, new OnionMessageContents([]), null));
    }
}