namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_AValidOnionWithRandomEncryptedRecipientData_When_Unwrapping_Then_IgnoredWithoutThrowing(
        bool finalHop)
    {
        // Arrange: the sender made the onion (the Sphinx HMAC verifies) but the hop's data is 40 random bytes
        var garbage = new byte[40];
        Random.Shared.NextBytes(garbage);
        var message = finalHop
                          ? _kit.BuildRaw([s_pathIdData], (_, _) => Erd(garbage))
                          : _kit.BuildRaw([_kit.NextNodeData(1), s_pathIdData],
                                          (i, erd) => i == 0 ? Erd(garbage) : Erd(erd));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "InvalidOnionBlinding");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    public void Given_AValidOnionWithEncryptedRecipientDataShorterThanTheTag_When_Unwrapping_Then_Ignored(int length)
    {
        // Arrange: shorter than the 16-byte ChaCha20-Poly1305 tag
        var message = _kit.BuildRaw([s_pathIdData], (_, _) => Erd(new byte[length]));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "InvalidOnionBlinding");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Given_AValidOnionWithOneFlippedBitInTheEncryptedRecipientData_When_Unwrapping_Then_Ignored(int index)
    {
        // Arrange: the correctly sized blob with one bit of the ciphertext (first byte) or the tag (last byte) flipped
        var message = _kit.BuildRaw([s_pathIdData], (_, erd) =>
        {
            var tampered = erd.ToArray();
            tampered[index < 0 ? tampered.Length + index : index] ^= 0x01;
            return Erd(tampered);
        });

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "InvalidOnionBlinding");
    }

    [Fact]
    public void Given_EncryptedDataThatDecryptsToAnUnknownEvenRecordOnANonFinalHop_When_Unwrapping_Then_Ignored()
    {
        // Arrange: authentic ciphertext (the path creator's) whose plaintext carries type 16 besides next_node_id
        byte[] data = [.. _kit.NextNodeData(1), 0x10, 0x00];
        var message = _kit.BuildRaw([data, s_pathIdData], (_, erd) => Erd(erd));

        // Act
        var result = _kit.Unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        AssertIgnored(result, "InvalidOnionBlinding");
    }

    [Fact]
    public void Given_AnInvalidNodeKey_When_Unwrapping_Then_ThrowsInsteadOfIgnoring()
    {
        // Arrange: a zero private key is a local fault, not the peer's
        var message = _kit.BuildRaw([s_pathIdData], (_, erd) => Erd(erd));
        var zeroKey = new PrivKey(new byte[32]);

        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => _kit.Unwrapper.Unwrap(message, zeroKey));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_AMessage_When_Unwrapped_Then_ThePeelSharedSecretsAreZeroed(bool valid)
    {
        // Arrange
        var sphinx = new CapturingSphinxService(_kit.Sphinx);
        var unwrapper = new OnionMessageUnwrapper(sphinx, _kit.RouteBlinding);
        var message = _kit.BuildRaw([s_pathIdData], (_, erd) => valid ? Erd(erd) : Erd(new byte[15]));

        // Act
        var result = unwrapper.Unwrap(message, _kit.NodeKeys[0]);

        // Assert
        Assert.Equal(valid ? OnionMessageUnwrapStatus.Deliver : OnionMessageUnwrapStatus.Ignored, result.Status);
        var peeled = sphinx.LastPeeled!;
        Assert.All((byte[])peeled.SharedSecret, b => Assert.Equal(0, b));
        Assert.All((byte[])peeled.PathKeySharedSecret!.Value, b => Assert.Equal(0, b));
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

    private sealed class CapturingSphinxService(ISphinxService inner) : ISphinxService
    {
        public PeeledOnion? LastPeeled { get; private set; }

        public OnionPacket Construct(IReadOnlyList<OnionHop> hops, PrivKey sessionKey, ReadOnlySpan<byte> associatedData,
                                     int hopPayloadsLength, OnionPacketKind packetKind) =>
            inner.Construct(hops, sessionKey, associatedData, hopPayloadsLength, packetKind);

        public ConstructedOnion ConstructWithSharedSecrets(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                                           ReadOnlySpan<byte> associatedData, int hopPayloadsLength,
                                                           OnionPacketKind packetKind) =>
            inner.ConstructWithSharedSecrets(hops, sessionKey, associatedData, hopPayloadsLength, packetKind);

        public IReadOnlyList<Secret> ComputeSharedSecrets(IReadOnlyList<CompactPubKey> nodeIds, PrivKey sessionKey) =>
            inner.ComputeSharedSecrets(nodeIds, sessionKey);

        public PeeledOnion PeelAsLocalNode(OnionPacket packet, ReadOnlySpan<byte> associatedData, CompactPubKey? pathKey,
                                           OnionPacketKind packetKind) =>
            LastPeeled = inner.PeelAsLocalNode(packet, associatedData, pathKey, packetKind);

        public PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                                CompactPubKey? pathKey, OnionPacketKind packetKind) =>
            LastPeeled = inner.Peel(packet, associatedData, nodeKey, pathKey, packetKind);
    }
}