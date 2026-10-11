using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Exceptions;
using NLightning.Infrastructure.Bitcoin.Gossip;
using NLightning.Infrastructure.Bitcoin.Signers;

namespace NLightning.Infrastructure.VlsSigning;

/// <summary>
/// Public channels, wallet withdrawals and LND-style message signing through VLS's purpose-specific APIs (NL-1335):
/// the channel announcement goes to VLS's funding-key and node-key gossip signers bound to the channel and a message to
/// VLS's <c>sign_message</c>. Withdrawals are signed by the reserved-input wallet signing of
/// <c>VlsLightningSigner.Anchors.cs</c> (VLS <c>check_onchain_tx</c>); their destination must be on VLS's allowlist,
/// which only the approval credential extends (<see cref="VlsWalletApprovalClient"/>). No generic digest or key signing.
/// </summary>
public sealed partial class VlsLightningSigner
{
    private const uint SignChannelAnnouncementOperation = 2200;
    private const uint SignMessageOperation = 2201;

    /// <inheritdoc />
    public ChannelAnnouncementSignatures SignChannelAnnouncement(ChannelId channelId,
                                                                 ReadOnlyMemory<byte> unsignedAnnouncement,
                                                                 ShortChannelId shortChannelId)
    {
        var info = Info(channelId);
        if (info.DataLossDetected)
            throw new SignerException("VLS channel has data loss.", channelId, "Internal error");
        if (!info.AnnounceChannel)
            throw new SignerException("Refusing to sign a channel announcement for a private channel", channelId,
                                      "Internal error");

        var announcement = ParseAnnouncement(channelId, unsignedAnnouncement.Span);
        if (announcement.ShortChannelId != shortChannelId)
            throw new SignerException("Refusing to sign a channel announcement of another short channel id",
                                      channelId, "Internal error");

        // The real short channel id as persisted now: the registration may predate the funding confirmation
        var known = signingInfoSource is not null && signingInfoSource.TryGet(channelId, out var persisted)
                        ? persisted.ShortChannelId
                        : info.ShortChannelId;
        if (known is not { } knownShortChannelId || knownShortChannelId != shortChannelId
         || shortChannelId.OutputIndex != info.FundingOutputIndex)
            throw new SignerException("Refusing to sign a channel announcement that does not name the channel's "
                                    + "confirmed funding output", channelId, "Internal error");

        var ourNodeId = GetNodePublicKey();
        var weAreNode1 = announcement.NodeId1 == ourNodeId;
        if (!weAreNode1 && announcement.NodeId2 != ourNodeId)
            throw new SignerException("Refusing to sign a channel announcement that does not name our node id",
                                      channelId, "Internal error");
        var (theirNodeId, ourBitcoinKey, theirBitcoinKey) = weAreNode1
            ? (announcement.NodeId2, announcement.BitcoinKey1, announcement.BitcoinKey2)
            : (announcement.NodeId1, announcement.BitcoinKey2, announcement.BitcoinKey1);
        if ((info.RemoteNodeId is { } remoteNodeId && theirNodeId != remoteNodeId)
         || ourBitcoinKey != info.LocalFundingPubKey || theirBitcoinKey != info.RemoteFundingPubKey)
            throw new SignerException("Refusing to sign a channel announcement that does not match the channel",
                                      channelId, "Internal error");

        // VLS checks the same bindings against its own channel state (peer, funding keys, funding output index)
        var command = Command("sign_channel_announcement", Channel(channelId));
        command["payload"] = Hex(unsignedAnnouncement.ToArray());
        var result = connection.Invoke(SignChannelAnnouncementOperation, command);
        var nodeSignature = Signature(result.GetProperty("node_signature"));
        var bitcoinSignature = Signature(result.GetProperty("bitcoin_signature"));

        // Both signatures cover SHA256d of the announcement after the signatures (BOLT 7)
        Hash hash = SHA256.HashData(SHA256.HashData(unsignedAnnouncement.Span));
        var verifier = new GossipSignatureVerifier();
        if (!verifier.Verify(hash, nodeSignature, ourNodeId) || !verifier.Verify(hash, bitcoinSignature, ourBitcoinKey))
            throw new SignerException("VLS channel announcement signatures do not verify", channelId,
                                      "Internal error");

        return new ChannelAnnouncementSignatures(nodeSignature, bitcoinSignature);
    }

    /// <summary>
    /// LND's <c>signmessage</c> through VLS's <c>sign_message</c>: SHA256d of <c>"Lightning Signed Message:" ||
    /// message</c>, returned as header <c>31 + recovery id</c> then <c>r || s</c>. VLS has no single-SHA256 variant, so
    /// <paramref name="singleHash"/> is refused.
    /// </summary>
    public byte[] SignLightningMessage(ReadOnlySpan<byte> message, bool singleHash)
    {
        if (singleHash)
            throw new NotSupportedException("VLS signs Lightning messages over double SHA256 only.");

        var command = new JsonObject { ["op"] = "sign_message", ["message"] = Hex(message.ToArray()) };
        var result = connection.Invoke(SignMessageOperation, command);
        var signed = Convert.FromHexString(result.GetProperty("signature").GetString()
                                        ?? throw new SignerException("VLS returned no message signature."));
        if (signed.Length != LightningMessageSignature.Length || signed[^1] > 3)
            throw new SignerException("VLS returned a malformed message signature.");

        var signature = new byte[LightningMessageSignature.Length];
        signature[0] = (byte)(27 + 4 + signed[^1]);
        signed.AsSpan(0, 64).CopyTo(signature.AsSpan(1));
        if (LightningMessageSignature.Recover(message, signature) != GetNodePublicKey())
            throw new SignerException("VLS message signature does not recover the node key.");
        return signature;
    }

    private static (ShortChannelId ShortChannelId, CompactPubKey NodeId1, CompactPubKey NodeId2,
        CompactPubKey BitcoinKey1, CompactPubKey BitcoinKey2) ParseAnnouncement(ChannelId channelId,
                                                                                ReadOnlySpan<byte> data)
    {
        const int keyLength = 33;
        if (data.Length < sizeof(ushort))
            throw new SignerException("The channel announcement is too short", channelId, "Internal error");
        var offset = sizeof(ushort) + BinaryPrimitives.ReadUInt16BigEndian(data) + 32;
        if (data.Length < offset + ShortChannelId.Length + 4 * keyLength)
            throw new SignerException("The channel announcement is too short", channelId, "Internal error");

        var shortChannelId = new ShortChannelId(data.Slice(offset, ShortChannelId.Length).ToArray());
        offset += ShortChannelId.Length;
        var keys = new CompactPubKey[4];
        for (var i = 0; i < keys.Length; i++, offset += keyLength)
            keys[i] = new CompactPubKey(data.Slice(offset, keyLength).ToArray());
        if (((ReadOnlySpan<byte>)keys[0]).SequenceCompareTo(keys[1]) >= 0)
            throw new SignerException("Refusing to sign a channel announcement whose node ids are not in ascending "
                                    + "order", channelId, "Internal error");
        return (shortChannelId, keys[0], keys[1], keys[2], keys[3]);
    }
}