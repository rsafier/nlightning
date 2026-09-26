namespace NLightning.Application.Gossip.Announcements;

using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Builds the <c>channel_announcement</c> of one of our public channels (BOLT 7): the unsigned message both sides sign
/// in <c>announcement_signatures</c>, the signature checks of the peer's half, and the assembled message with all four
/// signatures.
/// </summary>
/// <remarks>
/// Both ends must build the same bytes, since each signs the double-SHA256 of the message from offset 256: no features
/// (LND and CLN announce an empty feature vector), no trailing data, <c>node_id_1</c> the lesser of the two compressed
/// node ids and <c>bitcoin_key_N</c> the funding key of <c>node_id_N</c>.
/// </remarks>
public static class ChannelAnnouncementBuilder
{
    /// <summary>
    /// The channel's announcement for <paramref name="shortChannelId"/> with four empty signatures
    /// (<see cref="ChannelAnnouncementPayload.EmptySignature"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel has no remote key set yet, or its node ids are
    /// equal.</exception>
    public static ChannelAnnouncementPayload BuildUnsigned(ChannelModel channel, ShortChannelId shortChannelId,
                                                           CompactPubKey ourNodeId, ChainHash chainHash)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var remoteKeySet = channel.RemoteKeySet
                        ?? throw new InvalidOperationException(
                               $"Channel {channel.ChannelId} has no remote keys yet: nothing to announce");

        var ourFundingKey = channel.LocalKeySet.FundingCompactPubKey;
        var theirFundingKey = remoteKeySet.FundingCompactPubKey;
        var weAreNode1 = IsNode1(ourNodeId, channel.RemoteNodeId);

        var empty = ChannelAnnouncementPayload.EmptySignature;
        return weAreNode1
                   ? new ChannelAnnouncementPayload(empty, empty, empty, empty, ReadOnlyMemory<byte>.Empty, chainHash,
                                                    shortChannelId, ourNodeId, channel.RemoteNodeId, ourFundingKey,
                                                    theirFundingKey)
                   : new ChannelAnnouncementPayload(empty, empty, empty, empty, ReadOnlyMemory<byte>.Empty, chainHash,
                                                    shortChannelId, channel.RemoteNodeId, ourNodeId, theirFundingKey,
                                                    ourFundingKey);
    }

    /// <summary>
    /// The two checks of the peer's <c>announcement_signatures</c>: its node signature by its node id and its bitcoin
    /// signature by its funding key, both over <paramref name="unsigned"/>'s signature hash.
    /// </summary>
    public static IReadOnlyList<GossipSignatureCheck> GetRemoteSignatureChecks(ChannelAnnouncementPayload unsigned,
                                                                               ChannelModel channel,
                                                                               ChannelAnnouncementSignatures remote)
    {
        ArgumentNullException.ThrowIfNull(unsigned);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(remote);
        var remoteKeySet = channel.RemoteKeySet
                        ?? throw new InvalidOperationException(
                               $"Channel {channel.ChannelId} has no remote keys yet: nothing to verify");

        var hash = unsigned.GetSignatureHash();
        return
        [
            new GossipSignatureCheck(hash, remote.NodeSignature, channel.RemoteNodeId),
            new GossipSignatureCheck(hash, remote.BitcoinSignature, remoteKeySet.FundingCompactPubKey)
        ];
    }

    /// <summary>
    /// The four checks of an assembled announcement: <c>node_signature_N</c> by <c>node_id_N</c> and
    /// <c>bitcoin_signature_N</c> by <c>bitcoin_key_N</c>.
    /// </summary>
    public static IReadOnlyList<GossipSignatureCheck> GetAllSignatureChecks(ChannelAnnouncementPayload announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        var hash = announcement.GetSignatureHash();
        return
        [
            new GossipSignatureCheck(hash, announcement.NodeSignature1, announcement.NodeId1),
            new GossipSignatureCheck(hash, announcement.NodeSignature2, announcement.NodeId2),
            new GossipSignatureCheck(hash, announcement.BitcoinSignature1, announcement.BitcoinKey1),
            new GossipSignatureCheck(hash, announcement.BitcoinSignature2, announcement.BitcoinKey2)
        ];
    }

    /// <summary>
    /// <paramref name="unsigned"/> with both halves in their slots: ours in slot 1 when our node id is the lesser.
    /// </summary>
    public static ChannelAnnouncementPayload Assemble(ChannelAnnouncementPayload unsigned, CompactPubKey ourNodeId,
                                                      ChannelAnnouncementSignatures local,
                                                      ChannelAnnouncementSignatures remote)
    {
        ArgumentNullException.ThrowIfNull(unsigned);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        return unsigned.NodeId1 == ourNodeId
                   ? unsigned.WithSignatures(local.NodeSignature, remote.NodeSignature, local.BitcoinSignature,
                                             remote.BitcoinSignature)
                   : unsigned.WithSignatures(remote.NodeSignature, local.NodeSignature, remote.BitcoinSignature,
                                             local.BitcoinSignature);
    }

    /// <summary>
    /// True when <paramref name="ourNodeId"/> is <c>node_id_1</c>: the lesser of the two compressed keys, compared
    /// as unsigned bytes (BOLT 7).
    /// </summary>
    /// <exception cref="InvalidOperationException">Both node ids are equal.</exception>
    public static bool IsNode1(CompactPubKey ourNodeId, CompactPubKey remoteNodeId)
    {
        var order = ((ReadOnlySpan<byte>)ourNodeId).SequenceCompareTo(remoteNodeId);
        if (order == 0)
            throw new InvalidOperationException("A channel with ourselves can't be announced");

        return order < 0;
    }
}