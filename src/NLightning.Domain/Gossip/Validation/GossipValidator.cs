namespace NLightning.Domain.Gossip.Validation;

using Addresses;
using Crypto.Constants;
using Crypto.ValueObjects;
using Graph;
using Protocol.Payloads;

/// <summary>
/// The pure BOLT 7 receiver checks of incoming gossip (plan BOLT7 §3.3 stage 1, G2-T1): everything that needs no
/// signature verification and no chain lookup. Signatures (<c>IGossipSignatureVerifier</c>) and the funding output
/// (<c>IFundingOutputLookup</c>) are later stages of the ingress pipeline, and a message accepted here can still be
/// rejected there.
/// </summary>
/// <remarks>
/// Each result names the plan requirement it implements (<c>B7-CA-03</c>, <c>B7-CA-04</c>, <c>B7-NA-03</c>,
/// <c>B7-NA-04</c>, <c>B7-CU-02</c>, <c>B7-CU-03</c>, <c>B7-PR-02</c>). Deviation from the plan's summary: an
/// unknown even feature bit in a <c>channel_announcement</c> does not make us ignore it; BOLT 7 only says we MUST NOT
/// route through the channel, so it is accepted with <see cref="GossipValidationResult.Routable"/> false.
/// </remarks>
public static class GossipValidator
{
    /// <summary>
    /// The pure checks of a <c>channel_announcement</c>, in the BOLT 7 order: node id order (warning, B7-CA-03),
    /// key format (warning), chain (ignore), depth when the tip is known (ignore below
    /// <c>MinConfirmations - DepthToleranceBlocks</c>), blacklist (ignore, B7-CA-04), then the known channel with the
    /// same short channel id: the same announcement is <see cref="GossipRejectReason.AlreadyKnown"/>, a different one
    /// is <see cref="GossipRejectReason.ConflictingAnnouncement"/> with
    /// <see cref="GossipValidationResult.MayBlacklist"/> (BOLT 7: blacklist both node pairs). Unknown even features:
    /// accepted, not routable.
    /// </summary>
    /// <param name="fields">The announcement.</param>
    /// <param name="context">Our chain, clock and tip.</param>
    /// <param name="knownChannel">The graph channel with the same short channel id, if any.</param>
    public static GossipValidationResult ValidateChannelAnnouncement(ChannelAnnouncementFields fields,
                                                                     GossipValidationContext context,
                                                                     GraphChannel? knownChannel = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(context);

        if (!IsCompressedPubKey(fields.NodeId1.Span) || !IsCompressedPubKey(fields.NodeId2.Span)
                                                     || !IsCompressedPubKey(fields.BitcoinKey1.Span)
                                                     || !IsCompressedPubKey(fields.BitcoinKey2.Span))
            return GossipValidationResult.Warn(GossipRejectReason.InvalidPublicKey, "B7-CA-03");

        if (GraphChannel.CompareNodeIds(fields.NodeId1.Span, fields.NodeId2.Span) >= 0)
            return GossipValidationResult.Warn(GossipRejectReason.NodeIdsNotOrdered, "B7-CA-03");

        if (fields.ChainHash != context.ChainHash)
            return GossipValidationResult.Ignore(GossipRejectReason.UnknownChain, "B7-CA-03");

        if (context.TipHeight is { } tip)
        {
            var height = fields.ShortChannelId.BlockHeight;
            var confirmations = tip >= height ? (ulong)tip - height + 1 : 0;
            var required = context.MinConfirmations > context.DepthToleranceBlocks
                               ? context.MinConfirmations - context.DepthToleranceBlocks
                               : 1;
            if (confirmations < required)
                return GossipValidationResult.Ignore(GossipRejectReason.InsufficientDepth, "B7-CA-03");
        }

        var nodeId1 = new CompactPubKey(fields.NodeId1.ToArray());
        var nodeId2 = new CompactPubKey(fields.NodeId2.ToArray());
        if (context.IsBlacklisted is { } isBlacklisted && (isBlacklisted(nodeId1) || isBlacklisted(nodeId2)))
            return GossipValidationResult.Ignore(GossipRejectReason.BlacklistedNode, "B7-CA-04");

        if (knownChannel is not null)
        {
            var same = knownChannel.NodeId1 == nodeId1
                    && knownChannel.NodeId2 == nodeId2
                    && ((ReadOnlySpan<byte>)knownChannel.BitcoinKey1).SequenceEqual(fields.BitcoinKey1.Span)
                    && ((ReadOnlySpan<byte>)knownChannel.BitcoinKey2).SequenceEqual(fields.BitcoinKey2.Span);
            return same
                       ? GossipValidationResult.Ignore(GossipRejectReason.AlreadyKnown, "B7-CA-05")
                       : GossipValidationResult.Ignore(GossipRejectReason.ConflictingAnnouncement, "B7-CA-04",
                                                       mayBlacklist: true);
        }

        var routable = !GossipFeatures.HasUnknownEvenBits(fields.Features.Span);
        return GossipValidationResult.Accept("B7-CA-03", routable: routable);
    }

    /// <summary>
    /// The pure checks of a <c>node_announcement</c> (B7-NA-03): an invalid node id is a warning; a node without a
    /// known channel, or a timestamp not above the stored one, is ignored; <c>addrlen</c> too short for the known
    /// descriptors is a warning (the message is not applied); the address list is filtered by
    /// <see cref="AddressDescriptorCodec.DecodeList"/>, and more than one DNS descriptor makes it not forwardable.
    /// Unknown even features: accepted, not routable (B7-NA-04).
    /// </summary>
    /// <param name="fields">The announcement.</param>
    /// <param name="nodeHasChannels">The node is an end of a known channel.</param>
    /// <param name="lastTimestamp">The timestamp of the stored announcement of that node, if any.</param>
    /// <param name="addresses">The decoded, filtered address list (null when the node id is invalid).</param>
    public static GossipValidationResult ValidateNodeAnnouncement(NodeAnnouncementFields fields,
                                                                  bool nodeHasChannels, uint? lastTimestamp,
                                                                  out AddressListDecodeResult? addresses)
    {
        ArgumentNullException.ThrowIfNull(fields);
        addresses = null;

        if (!IsCompressedPubKey(fields.NodeId.Span))
            return GossipValidationResult.Warn(GossipRejectReason.InvalidPublicKey, "B7-NA-03");

        addresses = AddressDescriptorCodec.DecodeList(fields.Addresses.Span);
        if (addresses.IsMalformed)
            return GossipValidationResult.Warn(GossipRejectReason.MalformedAddresses, "B7-NA-03");

        if (!nodeHasChannels)
            return GossipValidationResult.Ignore(GossipRejectReason.UnknownNode, "B7-NA-03");

        if (lastTimestamp is { } last && fields.Timestamp <= last)
            return GossipValidationResult.Ignore(GossipRejectReason.NotNewer, "B7-NA-03");

        var routable = !GossipFeatures.HasUnknownEvenBits(fields.Features.Span);
        return GossipValidationResult.Accept(routable ? "B7-NA-03" : "B7-NA-04",
                                             forwardable: !addresses.HasMultipleDns, routable: routable);
    }

    /// <summary>
    /// The pure checks of a <c>channel_update</c> (B7-CU-02, B7-CU-03, B7-PR-02), in the BOLT 7 order: unknown chain
    /// (ignore); no announcement and not our channel (ignore: an orphan the ingress may cache); spent without
    /// <c>disable</c> (ignore); same timestamp with the same fields (ignore) or different ones (ignore, may
    /// blacklist); older (ignore); too far in the future (ignore); stale (ignore, when
    /// <see cref="GossipValidationContext.IgnoreStaleUpdates"/>). Accepted updates with
    /// <c>htlc_maximum_msat &lt; htlc_minimum_msat</c>, or a maximum above the known capacity (may blacklist), are
    /// not routable. <c>dont_forward</c> and an unannounced own channel make the update not forwardable (a
    /// disabled update of a spent channel stays forwardable, as BOLT 7 allows).
    /// </summary>
    /// <param name="update">The update.</param>
    /// <param name="context">Our chain and clock.</param>
    /// <param name="channel">The graph channel with the update's short channel id, if any.</param>
    /// <param name="isOwnChannel">The short channel id names one of our own channels (public or not).</param>
    /// <param name="lastPolicy">The stored policy of the update's direction; null reads it from
    /// <paramref name="channel"/>.</param>
    public static GossipValidationResult ValidateChannelUpdate(ChannelUpdatePayload update,
                                                               GossipValidationContext context,
                                                               GraphChannel? channel, bool isOwnChannel = false,
                                                               GraphPolicy? lastPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(context);

        if (update.ChainHash != context.ChainHash)
            return GossipValidationResult.Ignore(GossipRejectReason.UnknownChain, "B7-CU-02");

        if (channel is null && !isOwnChannel)
            return GossipValidationResult.Ignore(GossipRejectReason.UnknownChannel, "B7-CU-02");

        var spent = channel?.SpentAtHeight is not null;
        if (spent && !update.IsDisabled)
            return GossipValidationResult.Ignore(GossipRejectReason.ChannelSpent, "B7-CU-02");

        lastPolicy ??= channel?.GetPolicy(update.Direction ? (byte)1 : (byte)0);
        if (lastPolicy is not null)
        {
            if (update.Timestamp == lastPolicy.Timestamp)
                return lastPolicy.HasSameFieldsAs(update)
                           ? GossipValidationResult.Ignore(GossipRejectReason.DuplicateUpdate, "B7-CU-02")
                           : GossipValidationResult.Ignore(GossipRejectReason.ConflictingSameTimestamp, "B7-CU-02",
                                                           mayBlacklist: true);

            if (update.Timestamp < lastPolicy.Timestamp)
                return GossipValidationResult.Ignore(GossipRejectReason.OutdatedUpdate, "B7-CU-02");
        }

        if (update.Timestamp > context.NowUnixSeconds + (ulong)context.MaxFutureSkew.TotalSeconds)
            return GossipValidationResult.Ignore(GossipRejectReason.TimestampTooFarInFuture, "B7-CU-02");

        if (context.IgnoreStaleUpdates
         && (ulong)update.Timestamp + (ulong)context.StaleAfter.TotalSeconds < context.NowUnixSeconds)
            return GossipValidationResult.Ignore(GossipRejectReason.StaleUpdate, "B7-PR-02");

        var aboveCapacity = channel?.CapacityMsat is { } capacity && update.HtlcMaximumMsat > capacity;
        var routable = update.HtlcMaximumMsat >= update.HtlcMinimumMsat && !aboveCapacity;
        var forwardable = channel is not null && !update.DontForward;
        return GossipValidationResult.Accept(routable ? "B7-CU-02" : "B7-CU-03", forwardable, routable,
                                             mayBlacklist: aboveCapacity);
    }

    /// <summary>
    /// True when <paramref name="key"/> has the shape of a compressed secp256k1 public key (33 bytes, prefix 0x02 or
    /// 0x03). Whether it is a point on the curve is checked by the signature stage.
    /// </summary>
    public static bool IsCompressedPubKey(ReadOnlySpan<byte> key) =>
        key.Length == CryptoConstants.CompactPubkeyLen && key[0] is 0x02 or 0x03;
}