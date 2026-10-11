using Microsoft.Extensions.Logging;

namespace NLightning.Application.Gossip.Graph;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Graph;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Validation;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Interfaces;
using Metrics;

/// <summary>
/// Taproot gossip (BOLTs PR #1059 draft <c>4eef3dfa</c>, NL-878): <c>channel_announcement_2</c> (267),
/// <c>channel_update_2</c> (271) and <c>node_announcement_2</c> (269), next to the BOLT 7 messages and kept apart from
/// them (a channel or node announced with both keeps both, see <see cref="GraphGossipVersions"/>).
/// </summary>
/// <remarks>
/// <para>
/// Only while we advertise <c>option_gossip_v2</c> (<see cref="IsGossipV2Enabled"/>; experimental, off by default):
/// otherwise <see cref="TryEnqueue"/> drops the v2 messages silently.
/// </para>
/// <para>
/// <c>channel_announcement_2</c>: the pure checks (<see cref="GossipV2Validator"/>). With both bitcoin keys (the 4-key
/// form) the MuSig2 signature is checked next, without the output (<see cref="IGossipV2SignatureVerifier.CheckChannelSignature"/>:
/// the aggregate of the four keys does not depend on it), so a forged announcement warns and closes before it costs
/// a chain lookup (NL-1140). Then the funding output the short channel id names
/// (<see cref="IFundingOutputLookup.LookupAsync"/>: P2WSH and P2TR are both accepted, at
/// <see cref="GossipGraphOptions.GetAnnouncementDepth"/> confirmations), which must be the announced outpoint and hold
/// at least the announced capacity, then the channel proof (<see cref="IGossipV2SignatureVerifier.CheckChannelProof"/>:
/// the output against the keys). A 4-key announcement is stored at once with the announced capacity; a channel known
/// from a BOLT 7 <c>channel_announcement</c> of the same nodes gets the v2 announcement added. A keyless announcement
/// (the 3-key form, whose signature needs the output key) of a new channel waits, without a lookup, in a
/// <see cref="PendingAnnouncementIndex"/> of its own (the BOLT 7 index's bounds, <see cref="GossipGraphOptions.MaxPendingAnnouncements"/>
/// and <see cref="GossipGraphOptions.PendingAnnouncementTtl"/>) until its first valid <c>channel_update_2</c>, which
/// promotes it (<see cref="PromoteV2Async"/>: then the lookup and the proof), as NL-406 does for BOLT 7. A
/// contradicting output never scores the peer (as for v1, NL-371); a bad signature warns and closes.
/// </para>
/// <para>
/// <c>channel_update_2</c>: the update of a channel announced with either protocol (else an orphan until its
/// announcement), signed (BIP 340) by the node its <c>sciddir</c> direction names, dated within
/// [<c>tip − max_backdate_blocks</c>, tip] and above the stored v2 update. It is rate limited like a
/// <c>channel_update</c>, in a bucket of its own. <c>node_announcement_2</c>: of a node with a channel of either
/// protocol (else an orphan), signed, dated above the stored one and within the backdate window; no rate limit beyond
/// the strictly increasing block heights (the draft's natural rate limit).
/// </para>
/// <para>
/// Not done for v2 (NL-1140): the B7-CA-04 blacklist of a conflicting announcement, and the
/// <see cref="GossipGraphOptions.AssumeChannelValid"/> and <c>SkipUnavailable</c> shortcuts (a v2 proof needs the
/// output). Pending node announcements of either version wait for promotion (NL-1146).
/// </para>
/// </remarks>
public sealed partial class GossipIngress
{
    private readonly IGossipV2SignatureVerifier? _v2SignatureVerifier;

    /// <summary>The keyless <c>channel_announcement_2</c>s waiting for their first <c>channel_update_2</c> (NL-1140).</summary>
    private readonly PendingAnnouncementIndex _pendingV2;
    private readonly Infrastructure.Bitcoin.Wallet.Interfaces.IBlockchainMonitor? _blockchainMonitor;

    /// <summary>
    /// The direction offset of a <c>channel_update_2</c>'s rate-limit bucket and kept rate-limited message: v2 updates
    /// never share the slot of a v1 update of the same channel direction (their timestamps are block heights).
    /// </summary>
    private const byte V2DirectionOffset = 2;

    /// <summary>
    /// True when taproot gossip is handled: the graph is on, a v2 signature verifier is registered and we advertise
    /// <c>option_gossip_v2</c> (<c>Features:OptionGossipV2</c> with <c>Features:AllowExperimentalFeatures</c>).
    /// </summary>
    public bool IsGossipV2Enabled =>
        IsEnabled && _v2SignatureVerifier is not null && _nodeOptions.Features.IsGossipV2Advertised;

    /// <summary>
    /// The keyless <c>channel_announcement_2</c>s kept outside the graph until their first valid
    /// <c>channel_update_2</c> (NL-1140; expired ones included until the next write-behind round).
    /// </summary>
    public int PendingAnnouncement2Count => _pendingV2.Count;

    /// <summary>The keyless v2 announcements without update (for tests).</summary>
    internal PendingAnnouncementIndex PendingAnnouncementsV2 => _pendingV2;

    /// <summary>Our chain tip from the chain monitor, or null before it has one.</summary>
    private uint? TipHeight => _blockchainMonitor?.LastProcessedBlockHeight is { } height and > 0 ? height : null;

    /// <inheritdoc />
    public void AddOwnChannelAnnouncement2(ChannelAnnouncement2Payload announcement, LightningMoney capacity)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        ArgumentNullException.ThrowIfNull(capacity);
        EnqueueOwn(new OwnGossipMessage(new ChannelAnnouncement2Message(announcement), capacity));
    }

    /// <inheritdoc />
    public void AddOwnChannelUpdate2(ChannelUpdate2Payload update)
    {
        ArgumentNullException.ThrowIfNull(update);
        EnqueueOwn(new OwnGossipMessage(new ChannelUpdate2Message(update), null));
    }

    /// <inheritdoc />
    public void AddOwnNodeAnnouncement2(NodeAnnouncement2Payload announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        EnqueueOwn(new OwnGossipMessage(new NodeAnnouncement2Message(announcement), null));
    }

    /// <summary>
    /// Our own v2 gossip (the own-gossip loop, via <see cref="ApplyOwnAsync"/>): a <c>channel_announcement_2</c> is
    /// stored as <see cref="GraphChannelVerification.Own"/> (no chain lookup) or added to our channel's BOLT 7
    /// announcement; a <c>channel_update_2</c> is applied or waits for its announcement; a <c>node_announcement_2</c>
    /// goes into memory only (like our <c>node_announcement</c>).
    /// </summary>
    private async Task ApplyOwnV2Async(IMessage message, LightningMoney? capacity,
                                       CancellationToken cancellationToken)
    {
        switch (message)
        {
            case ChannelAnnouncement2Message { Payload: var announcement }:
                {
                    TxId? fundingTxId = null;
                    var ours = _channelMemoryRepository?
                              .FindChannels(c => c.ShortChannelId == announcement.ShortChannelId)
                              .FirstOrDefault();
                    if (ours?.FundingOutput is { } fundingOutput)
                        fundingTxId = fundingOutput.TransactionId;

                    var raw = announcement.GetBytes();
                    if (_store.TryGetChannel(announcement.ShortChannelId, out var known))
                    {
                        // A re-signed announcement (fresh MuSig2 nonces) is as valid as the stored one: keep that
                        if (!known.HasV2
                         && _store.TryAddAnnouncementVersion(announcement.ShortChannelId, GraphGossipVersions.V2, raw,
                                                             announcement.BitcoinKey1, announcement.BitcoinKey2))
                            await ReplayV2OrphansAsync(known, cancellationToken);
                        break;
                    }

                    var capacitySat = capacity is null ? announcement.CapacitySatoshis : (ulong)capacity.Satoshi;
                    var channel = new GraphChannel(announcement.ShortChannelId, announcement.NodeId1,
                                                   announcement.NodeId2, announcement.BitcoinKey1,
                                                   announcement.BitcoinKey2, capacitySat,
                                                   announcement.Features ?? ReadOnlyMemory<byte>.Empty,
                                                   GraphChannelVerification.Own)
                    {
                        Versions = GraphGossipVersions.V2,
                        RawAnnouncement2 = raw
                    };
                    await AddChannelAndReplayAsync(channel, fundingTxId, cancellationToken);
                    break;
                }
            case ChannelUpdate2Message { Payload: var update } update2:
                {
                    GraphChannel? channel;
                    lock (_orphanGate)
                    {
                        if (!_store.TryGetChannel(update.ShortChannelId, out channel))
                        {
                            _orphans.AddUpdate2(update2, null, out _);
                            return;
                        }
                    }

                    _store.TryApplyPolicy(update.ShortChannelId,
                                          GraphPolicy.FromChannelUpdate2(update, channel.CapacityMsat) with
                                          {
                                              RawUpdate = update.GetBytes()
                                          });
                    break;
                }
            case NodeAnnouncement2Message { Payload: var announcement }:
                _store.TryApplyOwnNode2(GraphNode.FromNodeAnnouncement2(announcement, announcement.GetBytes()));
                break;
        }
    }

    private async Task<GossipIngressResult> ProcessChannelAnnouncement2Async(
        IPeerService? origin, ChannelAnnouncement2Message message, int attempt, CancellationToken cancellationToken)
    {
        var announcement = message.Payload;
        var raw = announcement.GetBytes();
        if (attempt == 0 && _recentMessages.Contains((ushort)MessageTypes.ChannelAnnouncement2, raw))
            return GossipIngressResult.Ignored("an exact duplicate", GossipRejectReason.AlreadyKnown);

        _store.TryGetChannel(announcement.ShortChannelId, out var known);
        var validation = GossipV2Validator.ValidateChannelAnnouncement2(announcement, CreateV2Context(), known);
        switch (validation.Outcome)
        {
            case GossipValidationOutcome.Warn:
                Remember(MessageTypes.ChannelAnnouncement2, raw);
                return await WarnAsync(origin, validation.Reason,
                                       $"Invalid channel_announcement_2 for {announcement.ShortChannelId}: "
                                     + validation.Reason, validation.CloseConnection);
            case GossipValidationOutcome.Ignore:
                if (validation.Reason is GossipRejectReason.AlreadyKnown or GossipRejectReason.UnknownChain)
                    Remember(MessageTypes.ChannelAnnouncement2, raw);
                return GossipIngressResult.Ignored(validation.Reason.ToString(), validation.Reason);
        }

        if (known is null)
        {
            if (_store.ChannelCount >= _options.MaxChannels)
                return GraphFull($"the graph holds {_options.MaxChannels} channels",
                                 announcement.ShortChannelId.ToString());

            if (_memoryBudget?.RefuseNew("channels") is { } overBudget)
            {
                MarkBudgetRefused(announcement.ShortChannelId);
                return overBudget;
            }
        }

        if (announcement.BitcoinKey1 is null || announcement.BitcoinKey2 is null)
        {
            // NL-1140: the 3-key form's signature needs the output key, so a new channel's announcement waits for its
            // first channel_update_2 before it costs a lookup (as NL-406 for BOLT 7)
            if (known is null)
                return await KeepPendingV2Async(origin, announcement, raw, cancellationToken);
        }
        else
        {
            // NL-1140: the aggregate of the four keys does not depend on the output: a forgery is dropped before the
            // lookup
            switch (_v2SignatureVerifier!.CheckChannelSignature(announcement))
            {
                case GossipV2ProofResult.Valid:
                    break;
                case GossipV2ProofResult.MalformedProof:
                    Remember(MessageTypes.ChannelAnnouncement2, raw);
                    return await WarnAsync(origin, GossipRejectReason.None,
                                           $"Malformed channel proof in channel_announcement_2 for "
                                         + $"{announcement.ShortChannelId}", closeConnection: false);
                default:
                    // A bad signature, or keys that aggregate to nothing (no valid signature exists for them)
                    Remember(MessageTypes.ChannelAnnouncement2, raw);
                    return await WarnAsync(origin, GossipRejectReason.None,
                                           $"Invalid signature in channel_announcement_2 for "
                                         + $"{announcement.ShortChannelId}", closeConnection: true,
                                           GossipMetricReasons.InvalidSignature);
            }
        }

        var check = await LookupV2FundingAsync(announcement, origin?.PeerPubKey, cancellationToken);
        if (check.Failure is { } failure)
            return failure.Outcome == GossipIngressOutcome.Deferred ? failure : RejectV2Announcement(raw, failure);

        var proof = _v2SignatureVerifier!.CheckChannelProof(announcement, check.ScriptPubKey);
        switch (proof)
        {
            case GossipV2ProofResult.Valid:
                break;
            case GossipV2ProofResult.BadSignature:
                Remember(MessageTypes.ChannelAnnouncement2, raw);
                return await WarnAsync(origin, GossipRejectReason.None,
                                       $"Invalid signature in channel_announcement_2 for {announcement.ShortChannelId}",
                                       closeConnection: true, GossipMetricReasons.InvalidSignature);
            case GossipV2ProofResult.MalformedProof:
                Remember(MessageTypes.ChannelAnnouncement2, raw);
                return await WarnAsync(origin, GossipRejectReason.None,
                                       $"Malformed channel proof in channel_announcement_2 for "
                                     + $"{announcement.ShortChannelId}", closeConnection: false);
            default:
                // The keys (or the 3-key form) do not derive the funding output: the chain contradicts the
                // announcement, which proves it false but blames no relaying peer (NL-371)
                return RejectV2Announcement(raw, FundingCheckFailed(origin?.PeerPubKey, announcement.ShortChannelId,
                                                                    FundingOutputStatus.ScriptMismatch));
        }

        Remember(MessageTypes.ChannelAnnouncement2, raw);
        if (known is not null)
        {
            // The same channel announced with BOLT 7 before: both are kept (the validator checked the node ids)
            if (!_store.TryAddAnnouncementVersion(announcement.ShortChannelId, GraphGossipVersions.V2, raw,
                                                  announcement.BitcoinKey1, announcement.BitcoinKey2))
                return GossipIngressResult.Ignored("already known", GossipRejectReason.AlreadyKnown);

            RecordAccepted(GossipAcceptedKey.ChannelAnnouncement2(announcement.ShortChannelId));
            _metrics?.RecordAccepted(MessageTypes.ChannelAnnouncement2);
            await ReplayV2OrphansAsync(known, cancellationToken);
            return GossipIngressResult.Accepted("added to the channel_announcement of the same channel");
        }

        var channel = new GraphChannel(announcement.ShortChannelId, announcement.NodeId1, announcement.NodeId2,
                                       announcement.BitcoinKey1, announcement.BitcoinKey2,
                                       announcement.CapacitySatoshis,
                                       announcement.Features ?? ReadOnlyMemory<byte>.Empty)
        {
            Versions = GraphGossipVersions.V2,
            RawAnnouncement2 = raw
        };
        if (!await AddChannelAndReplayAsync(channel, check.TransactionId, cancellationToken))
            return GossipIngressResult.Ignored("already known", GossipRejectReason.AlreadyKnown);

        RecordAccepted(GossipAcceptedKey.ChannelAnnouncement2(announcement.ShortChannelId));
        _metrics?.RecordAccepted(MessageTypes.ChannelAnnouncement2);
        return GossipIngressResult.Accepted(validation.Routable ? "routable" : "not routable");
    }

    /// <summary>
    /// The draft's chain checks of a <c>channel_announcement_2</c>: the short channel id's output, which must be the
    /// announced outpoint, unspent, at the announcement depth, holding at least the announced capacity. The failure is
    /// a <see cref="GossipIngressOutcome.Deferred"/> result for a transient answer or too few confirmations, else a
    /// permanent refusal that blames nobody (NL-371); on success the output's script, for the channel proof.
    /// </summary>
    private async Task<V2FundingCheck> LookupV2FundingAsync(ChannelAnnouncement2Payload announcement,
                                                            CompactPubKey? originNodeId,
                                                            CancellationToken cancellationToken)
    {
        var lookup = await _fundingOutputLookup.LookupAsync(announcement.ShortChannelId, cancellationToken);
        _metrics?.RecordChainLookup(GossipMetrics.TagValue(lookup.Status), lookup.FromKeptAnswer);
        if (lookup.Status != FundingOutputStatus.Found)
            return V2FundingCheck.Failed(lookup.IsTransient
                                             ? GossipIngressResult.Deferred($"the chain lookup returned {lookup.Status}")
                                             : FundingCheckFailed(originNodeId, announcement.ShortChannelId,
                                                                  lookup.Status));

        if (lookup.Confirmations < AnnouncementDepth)
            return V2FundingCheck.Failed(GossipIngressResult.Deferred(
                $"the funding output has {lookup.Confirmations} confirmations, {AnnouncementDepth} needed"));

        if (lookup.TransactionId != announcement.FundingTxId
         || announcement.FundingOutputIndex != announcement.ShortChannelId.OutputIndex)
            return V2FundingCheck.Failed(FundingCheckFailed(originNodeId, announcement.ShortChannelId,
                                                            FundingOutputStatus.ScriptMismatch));

        if ((ulong)lookup.Amount!.Satoshi < announcement.CapacitySatoshis)
            return V2FundingCheck.Failed(FundingCheckFailed(originNodeId, announcement.ShortChannelId,
                                                            FundingOutputStatus.AmountMismatch));

        return new V2FundingCheck(null, lookup.ScriptPubKey ?? [], lookup.TransactionId);
    }

    /// <summary>
    /// NL-1140: a keyless <c>channel_announcement_2</c> of a new channel waits in <see cref="_pendingV2"/>, without a
    /// chain lookup, until its first valid <c>channel_update_2</c>; updates that arrived before it are replayed at once
    /// (the first valid one promotes it). Mirrors <c>KeepPendingAsync</c> (NL-406, NL-418).
    /// </summary>
    private async Task<GossipIngressResult> KeepPendingV2Async(IPeerService? origin,
                                                              ChannelAnnouncement2Payload announcement, byte[] raw,
                                                              CancellationToken cancellationToken)
    {
        if (_pendingV2.ContainsRaw(announcement.ShortChannelId, raw))
            return GossipIngressResult.Ignored("already waiting for its first channel_update_2",
                                               GossipRejectReason.AlreadyKnown);

        var entry = new PendingAnnouncement(announcement.ShortChannelId, raw, origin?.PeerPubKey,
                                            _timeProvider.GetUtcNow())
        {
            IsV2 = true
        };
        IReadOnlyList<OrphanEntry<ChannelUpdate2Message>> waiting;
        PendingAddOutcome added;
        lock (_orphanGate)
        {
            if (_store.TryGetChannel(announcement.ShortChannelId, out _))
                return GossipIngressResult.Ignored("already known", GossipRejectReason.AlreadyKnown);

            added = _pendingV2.Add(entry);
            if (added == PendingAddOutcome.Refused && _orphans.HasUpdates2(announcement.ShortChannelId))
            {
                // NL-418: a kept channel_update_2 matched none of the candidates, so they are all forgeries
                if (_pendingV2.Remove(announcement.ShortChannelId))
                {
                    _metrics?.RecordDropped(GossipMetricReasons.PendingCandidatesEvicted);
                    added = _pendingV2.Add(entry);
                }
            }

            waiting = added == PendingAddOutcome.Refused
                          ? []
                          : _orphans.TakeUpdates2(announcement.ShortChannelId);
        }

        if (added == PendingAddOutcome.Refused)
        {
            _metrics?.RecordDropped(GossipMetricReasons.PendingCandidatesFull);
            return GossipIngressResult.Limited(
                $"{PendingAnnouncementIndex.MaxCandidatesPerChannel} other announcements wait for the same short "
              + "channel id", GossipMetricReasons.PendingCandidatesFull);
        }

        if (added == PendingAddOutcome.AddedWithEviction)
        {
            _metrics?.RecordDropped(GossipMetricReasons.PendingFull);
            var count = Interlocked.Increment(ref _pendingEvictedCount);
            if (count == 1 || count % 1_000 == 0)
                _logger.LogWarning("{Max} channel_announcement_2s without a channel_update_2 are kept; the oldest of "
                                 + "the peer holding the most made room ({Count} so far)",
                                   _options.MaxPendingAnnouncements, count);
        }

        foreach (var orphan in waiting)
            await ReplayAsync(orphan.Origin, orphan.Message, cancellationToken);

        return _store.TryGetChannel(announcement.ShortChannelId, out _)
                   ? GossipIngressResult.Accepted("promoted by a waiting channel_update_2")
                   : GossipIngressResult.Pending("waiting for its first channel_update_2");
    }

    /// <summary>
    /// NL-1140: the first <c>channel_update_2</c> of a pending keyless <c>channel_announcement_2</c>. The update is
    /// checked against the candidates (its fields, then the BIP 340 signature of the node its direction names) before
    /// the chosen announcement's funding output is looked up and its proof checked; then the channel enters the graph
    /// (and what waited for it is replayed) and the update is applied as for any stored channel. An update whose
    /// signature matches no candidate waits as an orphan and its short channel id goes to the sync; a proof that fails
    /// against the output drops the candidate without blaming the update's sender (the announcement came from another
    /// peer, maybe long gone).
    /// </summary>
    private async Task<GossipIngressResult> PromoteV2Async(IPeerService? origin, ChannelUpdate2Message message,
                                                           IReadOnlyList<PendingAnnouncement> candidates, int attempt,
                                                           GossipValidationContext context,
                                                           CancellationToken cancellationToken)
    {
        var update = message.Payload;
        var first = candidates[0];
        var validation = GossipV2Validator.ValidateChannelUpdate2(
            update, context, first.ToUncheckedChannel2(first.ParseAnnouncement2()));
        if (validation.Outcome == GossipValidationOutcome.Ignore)
            return GossipIngressResult.Ignored(validation.Reason.ToString(), validation.Reason);

        if (validation.Outcome == GossipValidationOutcome.Warn)
            return await WarnAsync(origin, validation.Reason,
                                   $"Invalid channel_update_2 for {update.ShortChannelId}: {validation.Reason}",
                                   validation.CloseConnection);

        PendingAnnouncement? pending = null;
        ChannelAnnouncement2Payload? announcement = null;
        CompactPubKey signer = default;
        var signatureHash = update.GetSignatureHash();
        var tried = new HashSet<CompactPubKey>();
        foreach (var candidate in candidates)
        {
            var parsed = candidate.ParseAnnouncement2();
            var candidateSigner = update.Direction == 0 ? parsed.NodeId1 : parsed.NodeId2;
            if (!tried.Add(candidateSigner)
             || !_v2SignatureVerifier!.VerifyBip340(signatureHash, update.Signature, candidateSigner))
                continue;

            pending = candidate;
            announcement = parsed;
            signer = candidateSigner;
            break;
        }

        if (pending is null || announcement is null)
        {
            lock (_orphanGate)
            {
                if (!_orphans.AddUpdate2(message, origin, out var full) && full)
                    _metrics?.RecordDropped(GossipMetricReasons.OrphanCacheFull);
            }

            MarkMissed(update.ShortChannelId);
            return GossipIngressResult.Orphaned("not signed by the node of any pending channel_announcement_2");
        }

        if (_store.IsBanned(signer))
            return GossipIngressResult.Ignored("the node is banned", GossipRejectReason.BlacklistedNode);

        if (_store.ChannelCount >= _options.MaxChannels)
            return GraphFull($"the graph holds {_options.MaxChannels} channels", update.ShortChannelId.ToString());

        if (_memoryBudget?.RefuseNew("channels") is { } overBudget)
        {
            MarkBudgetRefused(update.ShortChannelId);
            return overBudget;
        }

        var check = await LookupV2FundingAsync(announcement, pending.OriginNodeId, cancellationToken);
        var failure = check.Failure;
        if (failure is null)
        {
            switch (_v2SignatureVerifier!.CheckChannelProof(announcement, check.ScriptPubKey))
            {
                case GossipV2ProofResult.Valid:
                    break;
                case GossipV2ProofResult.BadSignature or GossipV2ProofResult.MalformedProof:
                    // The announcement's own sender answers for its signature, not the update's sender
                    _logger.LogDebug("The pending channel_announcement_2 {ShortChannelId} from peer {Peer} has an "
                                   + "invalid channel proof", update.ShortChannelId,
                                     pending.OriginNodeId?.ToString() ?? "us");
                    ScoreMisbehaviour(pending.OriginNodeId, null, "invalid promoted channel_announcement_2 proof",
                                      disconnect: false);
                    failure = GossipIngressResult.Limited("invalid channel proof",
                                                          GossipMetricReasons.InvalidSignature);
                    break;
                default:
                    failure = FundingCheckFailed(pending.OriginNodeId, update.ShortChannelId,
                                                 FundingOutputStatus.ScriptMismatch);
                    break;
            }
        }

        if (failure is not null)
        {
            if (failure.Outcome != GossipIngressOutcome.Deferred)
            {
                // Permanent: this candidate is false; the others (if any) stay
                _pendingV2.Remove(pending);
                Remember(MessageTypes.ChannelAnnouncement2, pending.Raw);
                _metrics?.RecordRejected(MessageTypes.ChannelAnnouncement2, failure.MetricReason);
            }

            return failure;
        }

        Remember(MessageTypes.ChannelAnnouncement2, pending.Raw);
        var channel = new GraphChannel(announcement.ShortChannelId, announcement.NodeId1, announcement.NodeId2,
                                       announcement.BitcoinKey1, announcement.BitcoinKey2,
                                       announcement.CapacitySatoshis,
                                       announcement.Features ?? ReadOnlyMemory<byte>.Empty)
        {
            Versions = GraphGossipVersions.V2,
            RawAnnouncement2 = pending.Raw
        };
        if (await AddChannelAndReplayAsync(channel, check.TransactionId, cancellationToken))
        {
            RecordAccepted(GossipAcceptedKey.ChannelAnnouncement2(update.ShortChannelId));
            _metrics?.RecordAccepted(MessageTypes.ChannelAnnouncement2);
        }

        // The channel is in the graph now: apply the update as for any channel
        return await ProcessChannelUpdate2Async(origin, message, attempt, cancellationToken);
    }

    /// <summary>The chain checks of a <c>channel_announcement_2</c>: a failure, or the output's script and txid.</summary>
    private sealed record V2FundingCheck(GossipIngressResult? Failure, byte[] ScriptPubKey, TxId? TransactionId)
    {
        public static V2FundingCheck Failed(GossipIngressResult failure) => new(failure, [], null);
    }

    private GossipIngressResult RejectV2Announcement(byte[] raw, GossipIngressResult result)
    {
        Remember(MessageTypes.ChannelAnnouncement2, raw);
        return result;
    }

    private async Task<GossipIngressResult> ProcessChannelUpdate2Async(IPeerService? origin,
                                                                      ChannelUpdate2Message message, int attempt,
                                                                      CancellationToken cancellationToken)
    {
        var update = message.Payload;
        var raw = update.GetBytes();
        if (attempt == 0 && _recentMessages.Contains((ushort)MessageTypes.ChannelUpdate2, raw))
            return GossipIngressResult.Ignored("an exact duplicate", GossipRejectReason.DuplicateUpdate);

        var context = CreateV2Context();
        if (!_store.TryGetChannel(update.ShortChannelId, out var channel))
        {
            if (update.ChainHash != context.ChainHash)
                return GossipIngressResult.Ignored("another chain", GossipRejectReason.UnknownChain);

            IReadOnlyList<PendingAnnouncement> candidates = [];
            lock (_orphanGate)
            {
                if (!_store.TryGetChannel(update.ShortChannelId, out channel)
                 && (candidates = _pendingV2.GetCandidates(update.ShortChannelId)).Count == 0)
                {
                    if (!_orphans.AddUpdate2(message, origin, out var full) && full)
                        _metrics?.RecordDropped(GossipMetricReasons.OrphanCacheFull);
                    return GossipIngressResult.Orphaned("the channel is not in the graph yet");
                }
            }

            // NL-1140: the first update of a keyless channel_announcement_2 that waits for one
            if (channel is null)
                return await PromoteV2Async(origin, message, candidates, attempt, context, cancellationToken);
        }

        var validation = GossipV2Validator.ValidateChannelUpdate2(update, context, channel);
        if (validation.Outcome == GossipValidationOutcome.Ignore)
        {
            if (validation.Reason is GossipRejectReason.DuplicateUpdate or GossipRejectReason.OutdatedUpdate)
                Remember(MessageTypes.ChannelUpdate2, raw);
            return GossipIngressResult.Ignored(validation.Reason.ToString(), validation.Reason);
        }

        if (validation.Outcome == GossipValidationOutcome.Warn)
            return await WarnAsync(origin, validation.Reason,
                                   $"Invalid channel_update_2 for {update.ShortChannelId}: {validation.Reason}",
                                   validation.CloseConnection);

        // The draft: signing_node_id is node_id_1 for direction 0, node_id_2 otherwise
        var signer = update.Direction == 0 ? channel.NodeId1 : channel.NodeId2;
        if (_store.IsBanned(signer))
            return GossipIngressResult.Ignored("the node is banned", GossipRejectReason.BlacklistedNode);

        if (!_v2SignatureVerifier!.VerifyBip340(update.GetSignatureHash(), update.Signature, signer))
        {
            Remember(MessageTypes.ChannelUpdate2, raw);
            return await WarnAsync(origin, GossipRejectReason.None,
                                   $"Invalid signature in channel_update_2 for {update.ShortChannelId}",
                                   closeConnection: true, GossipMetricReasons.InvalidSignature);
        }

        var slot = (byte)(update.Direction + V2DirectionOffset);
        if (!_rateLimiter.TryAcquireUpdate(update.ShortChannelId, slot))
        {
            KeepLimited(_limitedUpdates, (update.ShortChannelId, slot), origin, message, update.BlockHeight);
            return GossipIngressResult.Limited("the channel direction's update rate is spent",
                                               GossipMetricReasons.RateLimited);
        }

        Remember(MessageTypes.ChannelUpdate2, raw);
        var policy = GraphPolicy.FromChannelUpdate2(update, channel.CapacityMsat) with { RawUpdate = raw };
        if (!_store.TryApplyPolicy(update.ShortChannelId, policy))
            return GossipIngressResult.Ignored("not newer", GossipRejectReason.OutdatedUpdate);

        RecordAccepted(GossipAcceptedKey.ChannelUpdate2(update.ShortChannelId, update.Direction));
        return GossipIngressResult.Accepted(validation.Routable ? "routable" : "not routable");
    }

    private async Task<GossipIngressResult> ProcessNodeAnnouncement2Async(
        IPeerService? origin, NodeAnnouncement2Message message, int attempt, CancellationToken cancellationToken)
    {
        var announcement = message.Payload;
        var raw = announcement.GetBytes();
        if (attempt == 0 && _recentMessages.Contains((ushort)MessageTypes.NodeAnnouncement2, raw))
            return GossipIngressResult.Ignored("an exact duplicate", GossipRejectReason.NotNewer);

        if (_store.IsBanned(announcement.NodeId))
            return GossipIngressResult.Ignored("the node is banned", GossipRejectReason.BlacklistedNode);

        var context = CreateV2Context();
        _store.TryGetNode(announcement.NodeId, out var stored);
        var validation = GossipV2Validator.ValidateNodeAnnouncement2(
            announcement, context, _store.NodeHasChannels(announcement.NodeId),
            stored is { HasV2: true } ? stored.BlockHeight : null);
        switch (validation.Outcome)
        {
            case GossipValidationOutcome.Warn:
                Remember(MessageTypes.NodeAnnouncement2, raw);
                return await WarnAsync(origin, validation.Reason,
                                       $"Invalid node_announcement_2 of {announcement.NodeId}: {validation.Reason}",
                                       validation.CloseConnection);
            case GossipValidationOutcome.Ignore when validation.Reason == GossipRejectReason.UnknownNode:
                {
                    bool nowKnown;
                    lock (_orphanGate)
                    {
                        nowKnown = _store.NodeHasChannels(announcement.NodeId);
                        if (!nowKnown && !_orphans.AddNodeAnnouncement2(message, origin, out var full) && full)
                            _metrics?.RecordDropped(GossipMetricReasons.OrphanCacheFull);
                    }

                    return nowKnown
                               ? await ProcessNodeAnnouncement2Async(origin, message, attempt, cancellationToken)
                               : GossipIngressResult.Orphaned("the node has no channel yet");
                }
            case GossipValidationOutcome.Ignore:
                return GossipIngressResult.Ignored(validation.Reason.ToString(), validation.Reason);
        }

        if (stored is null && _store.NodeCount >= _options.MaxNodes)
            return GraphFull($"the graph holds {_options.MaxNodes} nodes", announcement.NodeId.ToString());

        if (stored is null && _memoryBudget?.RefuseNew("nodes") is { } overBudgetNode)
            return overBudgetNode;

        if (!_v2SignatureVerifier!.VerifyBip340(announcement.GetSignatureHash(), announcement.Signature,
                                                announcement.NodeId))
        {
            Remember(MessageTypes.NodeAnnouncement2, raw);
            return await WarnAsync(origin, GossipRejectReason.None,
                                   $"Invalid signature in node_announcement_2 of {announcement.NodeId}",
                                   closeConnection: true, GossipMetricReasons.InvalidSignature);
        }

        Remember(MessageTypes.NodeAnnouncement2, raw);
        if (!_store.TryApplyNode2(GraphNode.FromNodeAnnouncement2(announcement, raw)))
            return GossipIngressResult.Ignored("not newer", GossipRejectReason.NotNewer);

        RecordAccepted(GossipAcceptedKey.NodeAnnouncement2(announcement.NodeId));
        return GossipIngressResult.Accepted(validation.Routable ? "routable" : "not routable");
    }

    /// <summary>
    /// A BOLT 7 <c>channel_announcement</c> of a channel known only from its <c>channel_announcement_2</c>, naming the
    /// same nodes (NL-878): checked like any (the pure checks, its four signatures, the P2WSH funding output at depth)
    /// and added to the channel, whose BOLT 7 updates waiting as orphans are then replayed. Other node ids are a
    /// conflict, ignored.
    /// </summary>
    private async Task<GossipIngressResult> AddV1AnnouncementToV2ChannelAsync(
        IPeerService? origin, ChannelAnnouncementPayload announcement, byte[] raw, GraphChannel known,
        CancellationToken cancellationToken)
    {
        if (known.NodeId1 != announcement.NodeId1 || known.NodeId2 != announcement.NodeId2)
            return GossipIngressResult.Ignored(GossipRejectReason.ConflictingAnnouncement.ToString(),
                                               GossipRejectReason.ConflictingAnnouncement);

        if (!VerifyChannelAnnouncement(announcement))
        {
            Remember(MessageTypes.ChannelAnnouncement, raw);
            return await WarnAsync(origin, GossipRejectReason.None,
                                   $"Invalid signature in channel_announcement for {announcement.ShortChannelId}",
                                   closeConnection: true, GossipMetricReasons.InvalidSignature);
        }

        var lookup = await _fundingOutputLookup.VerifyAsync(announcement.ShortChannelId, announcement.BitcoinKey1,
                                                            announcement.BitcoinKey2,
                                                            cancellationToken: cancellationToken);
        _metrics?.RecordChainLookup(GossipMetrics.TagValue(lookup.Status), lookup.FromKeptAnswer);
        if (lookup.Status != FundingOutputStatus.Found)
        {
            if (lookup.IsTransient)
                return GossipIngressResult.Deferred($"the chain lookup returned {lookup.Status}");

            Remember(MessageTypes.ChannelAnnouncement, raw);
            return FundingCheckFailed(origin?.PeerPubKey, announcement.ShortChannelId, lookup.Status);
        }

        if (lookup.Confirmations < AnnouncementDepth)
            return GossipIngressResult.Deferred(
                $"the funding output has {lookup.Confirmations} confirmations, {AnnouncementDepth} needed");

        Remember(MessageTypes.ChannelAnnouncement, raw);
        if (!_store.TryAddAnnouncementVersion(announcement.ShortChannelId, GraphGossipVersions.V1, raw,
                                              announcement.BitcoinKey1, announcement.BitcoinKey2))
            return GossipIngressResult.Ignored("already known", GossipRejectReason.AlreadyKnown);

        RecordAccepted(GossipAcceptedKey.ChannelAnnouncement(announcement.ShortChannelId));
        IReadOnlyList<OrphanEntry<ChannelUpdateMessage>> updates;
        lock (_orphanGate)
            updates = _orphans.TakeUpdates(announcement.ShortChannelId);
        foreach (var entry in updates)
            await ReplayAsync(entry.Origin, entry.Message, cancellationToken);

        return GossipIngressResult.Accepted("added to the channel_announcement_2 of the same channel");
    }

    /// <summary>
    /// Replays the v2 updates and node announcements that waited for <paramref name="channel"/> (its v2 announcement
    /// was added to a channel already in the graph, so <see cref="AddChannelAndReplayAsync"/> did not run).
    /// </summary>
    private async Task ReplayV2OrphansAsync(GraphChannel channel, CancellationToken cancellationToken)
    {
        IReadOnlyList<OrphanEntry<ChannelUpdate2Message>> updates;
        var nodes = new List<OrphanEntry<NodeAnnouncement2Message>>(2);
        lock (_orphanGate)
        {
            updates = _orphans.TakeUpdates2(channel.ShortChannelId);
            foreach (var nodeId in (ReadOnlySpan<CompactPubKey>)[channel.NodeId1, channel.NodeId2])
            {
                if (_orphans.TakeNodeAnnouncement2(nodeId) is { } entry)
                    nodes.Add(entry);
            }
        }

        foreach (var entry in updates)
            await ReplayAsync(entry.Origin, entry.Message, cancellationToken);
        foreach (var entry in nodes)
            await ReplayAsync(entry.Origin, entry.Message, cancellationToken);
    }

    /// <summary>The validation context of taproot gossip: <see cref="CreateContext"/> with our chain tip.</summary>
    private GossipValidationContext CreateV2Context() => CreateContext() with { TipHeight = TipHeight };
}