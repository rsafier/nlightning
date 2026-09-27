namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Onion.OnionMessages;
using Infrastructure.Bitcoin.Onion.RouteBlinding;

/// <summary>
/// OM1-T3: the onion-message reader's rules (OM-R-02..05, OM-W-03): every "MUST ignore" case is
/// <see cref="OnionMessageUnwrapStatus.Ignored"/>, never an exception.
/// </summary>
public class OnionMessageUnwrapperTests
{
    private static readonly byte[] s_pathIdData = [0x06, 0x01, 0x07];

    private readonly OnionMessageTestKit _kit = new();

    [Fact]
    public void Given_ANonFinalHopWithAnotherRecord_When_Unwrapping_Then_Ignored()
    {
        // Arrange: hop 0 carries an odd record besides encrypted_recipient_data
        var message = _kit.BuildRaw([_kit.NextNodeData(1), s_pathIdData],
                                    (i, erd) => i == 0
                                                    ? [.. OnionMessageTestKit.Tlv(1, [1]), .. Erd(erd)]
                                                    : Erd(erd));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "more than encrypted_recipient_data");
    }

    [Fact]
    public void Given_ANonFinalHopWithAReplyPath_When_Unwrapping_Then_Ignored()
    {
        // Arrange
        var replyPath = OnionMessagePayloadCodec.EncodeBlindedPath(
            WireBlindedPath.FromBlindedPath(_kit.PathBuilder.CreateMessagePath(_kit.NodeIds[..1])));
        var message = _kit.BuildRaw([_kit.NextNodeData(1), s_pathIdData],
                                    (i, erd) => i == 0
                                                    ? [.. OnionMessageTestKit.Tlv(2, replyPath), .. Erd(erd)]
                                                    : Erd(erd));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "more than encrypted_recipient_data");
    }

    [Fact]
    public void Given_ANonFinalHopWithAPathId_When_Unwrapping_Then_Ignored()
    {
        // Arrange (OM-R-04)
        var data = _kit.RouteBlinding.EncodeRecipientData(new BlindedRecipientData
        {
            NextNodeId = _kit.NodeIds[1],
            PathId = new byte[] { 1 }
        });
        var message = _kit.BuildRaw([data, s_pathIdData], (_, erd) => Erd(erd));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "path_id");
    }

    [Fact]
    public void Given_ANonFinalHopWithoutNextHop_When_Unwrapping_Then_Ignored()
    {
        // Arrange
        var message = _kit.BuildRaw([[], s_pathIdData], (_, erd) => Erd(erd));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "no next hop");
    }

    [Fact]
    public void Given_ANonFinalHopWithAShortChannelId_When_Unwrapping_Then_ForwardedByChannel()
    {
        // Arrange
        var scid = new ShortChannelId(800_000, 5, 1);
        var data = _kit.RouteBlinding.EncodeRecipientData(new BlindedRecipientData { ShortChannelId = scid });
        var message = _kit.BuildRaw([data, s_pathIdData], (_, erd) => Erd(erd));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);
        var next = _kit.Unwrapper.Unwrap(result.NextMessage!, _kit.NodeKeys[1]);

        // Assert
        Assert.Equal(OnionMessageUnwrapStatus.Forward, result.Status);
        Assert.Null(result.NextNodeId);
        Assert.Equal(scid, result.NextShortChannelId);
        Assert.Equal(OnionMessageUnwrapStatus.Deliver, next.Status);
    }

    [Fact]
    public void Given_AllowedFeaturesWithABit_When_Unwrapping_Then_Ignored()
    {
        // Arrange (OM-R-03: no feature is defined, so any bit is unknown)
        var data = _kit.RouteBlinding.EncodeRecipientData(new BlindedRecipientData
        {
            PathId = new byte[] { 1 },
            AllowedFeatures = new byte[] { 0x01 }
        });
        var message = _kit.BuildRaw([data], (_, erd) => Erd(erd));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "allowed_features");
    }

    [Fact]
    public void Given_AllowedFeaturesAllZero_When_Unwrapping_Then_Delivered()
    {
        // Arrange
        var data = _kit.RouteBlinding.EncodeRecipientData(new BlindedRecipientData
        {
            AllowedFeatures = new byte[] { 0x00 }
        });
        var message = _kit.BuildRaw([data], (_, erd) => Erd(erd));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        Assert.Equal(OnionMessageUnwrapStatus.Deliver, result.Status);
        Assert.Null(result.PathId);
    }

    [Theory]
    [InlineData(66UL)]
    [InlineData(65UL)]
    [InlineData(257UL)]
    public void Given_AFinalHopWithTwoPayloadFields_When_Unwrapping_Then_Ignored(ulong secondType)
    {
        // Arrange: every type from 64 up is a payload field, odd ones included
        var message = _kit.BuildRaw([s_pathIdData],
                                    (_, erd) => [.. Erd(erd), .. OnionMessageTestKit.Tlv(64, [1]),
                                                 .. OnionMessageTestKit.Tlv(secondType, [2])]);

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "more than one payload field");
    }

    [Fact]
    public void Given_AFinalHopWithOnePayloadFieldAndOddRecords_When_Unwrapping_Then_Delivered()
    {
        // Arrange
        var message = _kit.BuildRaw([s_pathIdData],
                                    (_, erd) => [.. OnionMessageTestKit.Tlv(1, [9]), .. OnionMessageTestKit.Tlv(3, [3]),
                                                 .. Erd(erd), .. OnionMessageTestKit.Tlv(64, [1])]);

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        Assert.Equal(OnionMessageUnwrapStatus.Deliver, result.Status);
        Assert.Equal([1UL, 3UL, 64UL], result.Payload!.OtherRecords.Select(r => r.Type));
        Assert.Equal(new byte[] { 7 }, result.PathId!.Value.ToArray());
    }

    [Theory]
    [InlineData(6UL)]
    [InlineData(70UL)]
    public void Given_AnUnknownEvenType_When_Unwrapping_Then_Ignored(ulong type)
    {
        // Arrange
        var message = _kit.BuildRaw([s_pathIdData],
                                    (_, erd) => [.. Erd(erd), .. OnionMessageTestKit.Tlv(type, [1])]);

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "unknown even");
    }

    [Fact]
    public void Given_RecordsOutOfOrder_When_Unwrapping_Then_Ignored()
    {
        // Arrange
        var message = _kit.BuildRaw([s_pathIdData], (_, erd) => [.. Erd(erd), .. OnionMessageTestKit.Tlv(1, [1])]);

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "strictly increasing");
    }

    [Fact]
    public void Given_AnInvalidReplyPath_When_Unwrapping_Then_Ignored()
    {
        // Arrange: a blinded_path with zero hops
        byte[] replyPath = [.. (byte[])_kit.NodeIds[0], .. (byte[])_kit.NodeIds[1], 0];
        var message = _kit.BuildRaw([s_pathIdData],
                                    (_, erd) => [.. OnionMessageTestKit.Tlv(2, replyPath), .. Erd(erd)]);

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "reply_path");
    }

    [Fact]
    public void Given_AnEmptyFinalPayload_When_Unwrapping_Then_IgnoredForLackOfEncryptedRecipientData()
    {
        // Arrange (OM-W-03: a zero length is a valid, empty onionmsg_payload)
        var message = _kit.BuildRaw([s_pathIdData], (_, _) => []);

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "no encrypted_recipient_data");
    }

    [Fact]
    public void Given_EncryptedDataWithAnUnknownEvenRecord_When_Unwrapping_Then_Ignored()
    {
        // Arrange
        var message = _kit.BuildRaw([[0x10, 0x00]], (_, erd) => Erd(erd));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "InvalidOnionBlinding");
    }

    [Fact]
    public void Given_TheWrongNodeKey_When_Unwrapping_Then_Ignored()
    {
        // Arrange
        var message = _kit.BuildRaw([s_pathIdData], (_, erd) => Erd(erd));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[1]);

        // Assert
        Assert.Equal(OnionMessageUnwrapStatus.Ignored, result.Status);
    }

    [Fact]
    public void Given_APathKeyForAnotherHop_When_Unwrapping_Then_Ignored()
    {
        // Arrange: the right onion with a path_key that is a valid point but not the hop's
        var message = _kit.BuildRaw([s_pathIdData], (_, erd) => Erd(erd));
        var wrongKey = new OnionMessageMessage(new OnionMessagePayload(_kit.NodeIds[1],
                                                                       message.Payload.OnionMessagePacket));

        // Act
        var result = _kit.Unwrapper.Unwrap(wrongKey, _kit.NodeKeys[0]);

        // Assert
        Assert.Equal(OnionMessageUnwrapStatus.Ignored, result.Status);
    }

    [Fact]
    public void Given_ATruncatedPacket_When_Unwrapping_Then_Ignored()
    {
        // Arrange: only the 66-byte overhead, no payloads at all
        var message = new OnionMessageMessage(new OnionMessagePayload(_kit.NodeIds[0], new byte[66]));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        Assert.Equal(OnionMessageUnwrapStatus.Ignored, result.Status);
    }

    [Fact]
    public void Given_AKeyManager_When_UnwrappingAsLocalNode_Then_OneEcdhServesPeelAndUnblinding()
    {
        // Arrange
        var keyManager = new EcdhOnlyKeyManager(_kit.NodeKeys[0]);
        var sphinx = new SphinxService(_kit.Secp256K1Math, keyManager);
        var unwrapper = new OnionMessageUnwrapper(sphinx, new RouteBlindingService(_kit.Secp256K1Math, keyManager));
        var message = _kit.BuildRaw([s_pathIdData], (_, erd) => Erd(erd));

        // Act
        var result = unwrapper.UnwrapAsLocalNode(message);

        // Assert: the path_key ECDH of the peel is reused for the encrypted_recipient_data (plus the onion's own)
        Assert.Equal(OnionMessageUnwrapStatus.Deliver, result.Status);
        Assert.Equal(2, keyManager.EcdhCalls);
    }

    [Fact]
    public void Given_NoKeyManager_When_UnwrappingAsLocalNode_Then_Throws()
    {
        // Arrange
        var message = _kit.BuildRaw([s_pathIdData], (_, erd) => Erd(erd));

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _kit.Unwrapper.UnwrapAsLocalNode(message));
    }

    private static byte[] Erd(byte[] encryptedRecipientData) =>
        OnionMessageTestKit.Tlv(OnionMessageConstants.EncryptedRecipientDataType, encryptedRecipientData);

    private static void AssertIgnored(OnionMessageUnwrapResult result, string reasonFragment)
    {
        Assert.Equal(OnionMessageUnwrapStatus.Ignored, result.Status);
        Assert.Contains(reasonFragment, result.IgnoreReason);
        Assert.Null(result.NextMessage);
        Assert.Null(result.Payload);
    }
}