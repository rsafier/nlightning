namespace NLightning.Domain.Gossip.Validation;

using Graph;
using Protocol.Payloads;

/// <summary>
/// The pure receiver checks of taproot gossip (BOLTs PR #1059 draft <c>4eef3dfa</c>, NL-878): everything about a
/// <c>channel_announcement_2</c>, <c>channel_update_2</c> or <c>node_announcement_2</c> that needs neither a signature
/// check nor the chain. The required records, the <c>sciddir</c> form and the alias rules are already enforced by the
/// payload parse; the funding output and the MuSig2 channel proof (<c>IGossipV2SignatureVerifier</c>) and the BIP 340
/// signatures are later stages of the ingress pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Block heights replace timestamps (the draft's "Block-height fields"): an update or node announcement dated below
/// <c>tip − max_backdate_blocks</c> (2016) is ignored (receiver MUST), one dated above the tip is ignored too (the
/// sender MUST NOT; <see cref="GossipValidationContext.DepthToleranceBlocks"/> blocks of slack, as our tip may lag), a
/// <c>channel_update_2</c> below the channel's funding block (its short channel id's height, which the sender MUST
/// NOT go below) is ignored, and a newer block height wins. With an unknown tip
/// (<see cref="GossipValidationContext.TipHeight"/> null) the tip checks are skipped.
/// </para>
/// <para>
/// A channel known from a BOLT 7 <c>channel_announcement</c> only accepts a <c>channel_announcement_2</c> of the same
/// node ids (the two protocols announce the same channel side by side, the draft's "Interaction with BOLT 7"); other
/// node ids are a conflict, as in BOLT 7.
/// </para>
/// </remarks>
public static class GossipV2Validator
{
    /// <summary>
    /// The pure checks of a <c>channel_announcement_2</c>: node id order (warning), chain (ignore), depth when the tip
    /// is known (ignore), blacklist (ignore), then the known channel with the same short channel id: the same
    /// announcement is <see cref="GossipRejectReason.AlreadyKnown"/>; another <c>channel_announcement_2</c>, or a
    /// v1-only channel of other node ids, is <see cref="GossipRejectReason.ConflictingAnnouncement"/> (may blacklist
    /// other node ids once <paramref name="signatureVerified"/>); a v1-only channel of the same node ids accepts it
    /// (both kept). Unknown even features: accepted, not routable.
    /// </summary>
    public static GossipValidationResult ValidateChannelAnnouncement2(ChannelAnnouncement2Payload announcement,
                                                                      GossipValidationContext context,
                                                                      GraphChannel? knownChannel = null,
                                                                      bool signatureVerified = false)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        ArgumentNullException.ThrowIfNull(context);

        if (GraphChannel.CompareNodeIds(announcement.NodeId1, announcement.NodeId2) >= 0)
            return GossipValidationResult.Warn(GossipRejectReason.NodeIdsNotOrdered, "B7V2-CA");

        if (announcement.ChainHash != context.ChainHash)
            return GossipValidationResult.Ignore(GossipRejectReason.UnknownChain, "B7V2-CA");

        if (context.TipHeight is { } tip)
        {
            var height = announcement.ShortChannelId.BlockHeight;
            var confirmations = tip >= height ? (ulong)tip - height + 1 : 0;
            var required = context.MinConfirmations > context.DepthToleranceBlocks
                               ? context.MinConfirmations - context.DepthToleranceBlocks
                               : 1;
            if (confirmations < required)
                return GossipValidationResult.Ignore(GossipRejectReason.InsufficientDepth, "B7V2-CA");
        }

        if (context.IsBlacklisted is { } isBlacklisted
         && (isBlacklisted(announcement.NodeId1) || isBlacklisted(announcement.NodeId2)))
            return GossipValidationResult.Ignore(GossipRejectReason.BlacklistedNode, "B7V2-CA");

        if (knownChannel is not null)
        {
            var sameNodes = knownChannel.NodeId1 == announcement.NodeId1
                         && knownChannel.NodeId2 == announcement.NodeId2;
            if (knownChannel.HasV2)
            {
                if (sameNodes && knownChannel.RawAnnouncement2.Span.SequenceEqual(announcement.GetBytes()))
                    return GossipValidationResult.Ignore(GossipRejectReason.AlreadyKnown, "B7V2-CA");

                return GossipValidationResult.Ignore(GossipRejectReason.ConflictingAnnouncement, "B7V2-CA",
                                                     mayBlacklist: signatureVerified && !sameNodes);
            }

            if (!sameNodes)
                return GossipValidationResult.Ignore(GossipRejectReason.ConflictingAnnouncement, "B7V2-CA",
                                                     mayBlacklist: signatureVerified);
        }

        var routable = announcement.Features is not { } features
                    || !GossipFeatures.HasUnknownEvenBits(features.Span);
        return GossipValidationResult.Accept("B7V2-CA", routable: routable);
    }

    /// <summary>
    /// The pure checks of a <c>channel_update_2</c>, in the draft's order: unknown chain (ignore); no announcement
    /// (of either protocol) and not our channel (ignore: an orphan the ingress may keep); spent without a disable
    /// flag (ignore); a block height below the channel's funding block, above the tip or below
    /// <c>tip − max_backdate_blocks</c> (ignore); the stored v2 policy's height again with the same records (ignore,
    /// duplicate) or different ones (ignore, may blacklist once <paramref name="signatureVerified"/>); older
    /// (ignore). Accepted updates whose (resolved) <c>htlc_maximum_msat</c> is below the minimum or above the capacity
    /// are not routable.
    /// </summary>
    /// <param name="update">The update.</param>
    /// <param name="context">Our chain, clock and tip.</param>
    /// <param name="channel">The graph channel with the update's short channel id, if any.</param>
    /// <param name="isOwnChannel">The short channel id names one of our own channels.</param>
    /// <param name="signatureVerified">The update's BIP 340 signature was verified.</param>
    public static GossipValidationResult ValidateChannelUpdate2(ChannelUpdate2Payload update,
                                                                GossipValidationContext context,
                                                                GraphChannel? channel, bool isOwnChannel = false,
                                                                bool signatureVerified = false)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(context);

        if (update.ChainHash != context.ChainHash)
            return GossipValidationResult.Ignore(GossipRejectReason.UnknownChain, "B7V2-CU");

        if (channel is null && !isOwnChannel)
            return GossipValidationResult.Ignore(GossipRejectReason.UnknownChannel, "B7V2-CU");

        if (channel?.SpentAtHeight is not null && !update.IsDisabled)
            return GossipValidationResult.Ignore(GossipRejectReason.ChannelSpent, "B7V2-CU");

        if (update.BlockHeight < update.ShortChannelId.BlockHeight)
            return GossipValidationResult.Ignore(GossipRejectReason.OutdatedUpdate, "B7V2-CU");

        if (IsAboveTip(update.BlockHeight, context))
            return GossipValidationResult.Ignore(GossipRejectReason.TimestampTooFarInFuture, "B7V2-CU");

        if (GraphChannel.IsBlockHeightStale(update.BlockHeight, context.TipHeight))
            return GossipValidationResult.Ignore(GossipRejectReason.StaleUpdate, "B7V2-CU");

        if (channel?.GetPolicy(update.Direction, 2) is { } last)
        {
            if (update.BlockHeight == last.Timestamp)
            {
                var sameRecords = !last.RawUpdate.IsEmpty
                                       ? ChannelUpdate2Payload.Parse(last.RawUpdate.Span).GetChecksumData()
                                                              .AsSpan().SequenceEqual(update.GetChecksumData())
                                       : SameFields(last, update, channel.CapacityMsat);
                return sameRecords
                           ? GossipValidationResult.Ignore(GossipRejectReason.DuplicateUpdate, "B7V2-CU")
                           : GossipValidationResult.Ignore(GossipRejectReason.ConflictingSameTimestamp, "B7V2-CU",
                                                           mayBlacklist: signatureVerified);
            }

            if (update.BlockHeight < last.Timestamp)
                return GossipValidationResult.Ignore(GossipRejectReason.OutdatedUpdate, "B7V2-CU");
        }

        var capacityMsat = channel?.CapacityMsat;
        var maximum = update.HtlcMaximumMsat ?? (capacityMsat ?? 0) / 2;
        var aboveCapacity = capacityMsat is { } capacity && maximum > capacity;
        var routable = maximum >= update.HtlcMinimumMsat && maximum > 0 && !aboveCapacity;
        return GossipValidationResult.Accept("B7V2-CU", forwardable: channel is not null, routable: routable,
                                             mayBlacklist: signatureVerified && aboveCapacity);
    }

    /// <summary>
    /// The pure checks of a <c>node_announcement_2</c>: a node without a channel of either protocol is ignored (the
    /// ingress keeps it as an orphan), a block height not above the stored <c>node_announcement_2</c>'s, above the tip
    /// or below <c>tip − max_backdate_blocks</c> is ignored. Unknown even features: accepted, not routable.
    /// </summary>
    /// <param name="announcement">The announcement.</param>
    /// <param name="context">Our tip.</param>
    /// <param name="nodeHasChannels">The node is an end of a known channel (v1 or v2).</param>
    /// <param name="lastBlockHeight">The block height of the stored <c>node_announcement_2</c>, if any.</param>
    public static GossipValidationResult ValidateNodeAnnouncement2(NodeAnnouncement2Payload announcement,
                                                                   GossipValidationContext context,
                                                                   bool nodeHasChannels, uint? lastBlockHeight)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        ArgumentNullException.ThrowIfNull(context);

        if (!nodeHasChannels)
            return GossipValidationResult.Ignore(GossipRejectReason.UnknownNode, "B7V2-NA");

        if (lastBlockHeight is { } last && announcement.BlockHeight <= last)
            return GossipValidationResult.Ignore(GossipRejectReason.NotNewer, "B7V2-NA");

        if (IsAboveTip(announcement.BlockHeight, context))
            return GossipValidationResult.Ignore(GossipRejectReason.TimestampTooFarInFuture, "B7V2-NA");

        if (GraphChannel.IsBlockHeightStale(announcement.BlockHeight, context.TipHeight))
            return GossipValidationResult.Ignore(GossipRejectReason.StaleUpdate, "B7V2-NA");

        var routable = !GossipFeatures.HasUnknownEvenBits(announcement.Features.Span);
        return GossipValidationResult.Accept("B7V2-NA", routable: routable);
    }

    /// <summary>
    /// True when <paramref name="blockHeight"/> is above our tip plus
    /// <see cref="GossipValidationContext.DepthToleranceBlocks"/> (our tip may lag a block); false without a tip.
    /// </summary>
    private static bool IsAboveTip(uint blockHeight, GossipValidationContext context) =>
        context.TipHeight is { } tip && blockHeight > (ulong)tip + context.DepthToleranceBlocks;

    private static bool SameFields(GraphPolicy policy, ChannelUpdate2Payload update, ulong? capacityMsat)
    {
        var other = GraphPolicy.FromChannelUpdate2(update, capacityMsat);
        return policy with { RawUpdate = default } == other;
    }
}