using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain.Anchors;

using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;

/// <summary>
/// The peer's commitment (NL-381, BOLT 5 plan O7-T2): fee-bumped through the anchor keyed to our funding pubkey on it
/// (BOLT 3 <c>to_remote_anchor</c> from our side, the same script as ours), and its anchors swept after 16 blocks.
/// </summary>
/// <remarks>
/// <para>Found in bitcoind's mempool: handed over by the O8 <c>MempoolReactor</c> (<see cref="OnPeerCommitmentInMempool"/>;
/// the hand-over is stored as a pending <see cref="BroadcastPurpose.PeerCommitment"/> row with the bytes, so it
/// survives a restart, NL-390), or, every round of a failed channel (also after a restart, and when our own
/// commitment is refused because the peer's holds the funding output while an HTLC deadline approaches), looked up
/// with <see cref="Infrastructure.Bitcoin.Wallet.Interfaces.IBitcoinChainService.GetTransactionAsync"/> by the txids of the
/// peer's current and next commitments (rebuilt by <see cref="Infrastructure.Bitcoin.Onchain.Interfaces.ICommitmentOutputMapper"/>) and by the
/// parents of our pending children of it; it must spend the channel's funding output. When bitcoind has none of the
/// candidates, the handed-over bytes are still trusted, but only for
/// <see cref="AnchorCpfpOptions.PeerCommitmentMissingBlocks"/> blocks in a row (NL-390: without bitcoind, or below
/// its mempool minimum, the trust in a hand-over is bounded — a reservation whose parent no mempool holds must not
/// live forever).</para>
/// <para>Child: only while it carries untrimmed HTLCs (with the peer's dust limit): the deadline is their earliest
/// <c>cltv_expiry</c>, our stake our <c>to_remote</c> plus those HTLCs; without one the peer's commitment is the
/// peer's to pay for. Then exactly as for ours (<see cref="AnchorCpfpPolicy.DecideChild"/>, RBF every
/// <c>RbfIntervalBlocks</c>, the channel's shared reservation). When bitcoind has the commitment it is published one
/// by one (the parent is already in the mempool); when bitcoind has none of the candidates but the mempool reactor
/// handed its bytes over, the child goes out with those bytes as a package (NL-389). While the peer's commitment
/// holds the funding output, our own commitment gets no child (it cannot enter the mempool).</para>
/// <para>End: once a close is recorded (the chain monitor processed the spend of the funding output), children of any
/// other transaction are abandoned; a pending child of the peer's confirmed commitment is kept with its inputs as ours
/// is (until our anchor is seen spent, or the wait passed). A peer commitment bitcoind no longer has for
/// <see cref="AnchorCpfpOptions.PeerCommitmentMissingBlocks"/> blocks in a row (counted while the monitor is at
/// bitcoind's tip and not halted: without txindex a confirmed transaction out of the mempool is unknown too) has its
/// pending children abandoned. Anchor sweep: the confirmed peer commitment (a close of kind remote, remote-next, future
/// or revoked) 16 blocks deep, read from the block that holds it, swept as ours is.</para>
/// </remarks>
public sealed partial class AnchorCpfpService
{
    /// <inheritdoc />
    public void OnPeerCommitmentInMempool(ChannelId channelId, SignedTransaction commitment, bool isNextCommitment)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        if (!_options.Enabled)
            return;

        _peerCommitments[channelId] = new PeerCommitmentSeen(commitment.TxId,
                                                             (byte[])commitment.RawTxBytes.Clone(), isNextCommitment);
        _ = PersistPeerCommitmentAsync(channelId, commitment.TxId, commitment.RawTxBytes);
        ScheduleCommitmentRound(channelId);
    }

    /// <summary>
    /// Stores the hand-over (NL-390): the peer's commitment bytes as a pending <see cref="BroadcastPurpose.PeerCommitment"/>
    /// row, so a restart keeps the bump even when bitcoind does not have the commitment (below its mempool minimum).
    /// The row is handed to the chain monitor, which sends it again after every block until a processed block holds it
    /// (then it is confirmed), which helps the peer's close propagate. Nothing is stored once a funding spend is recorded
    /// for the channel (NL-779: that commitment, or another, is already confirmed). In the background; a failed store is
    /// logged once (the restart then looks for the commitment in the mempool only). Whether the commitment is the peer's
    /// next one is not stored: it is derived again at the next round from the rebuilt txids.
    /// </summary>
    private async Task PersistPeerCommitmentAsync(ChannelId channelId, TxId txId, byte[] rawTransaction)
    {
        Interlocked.Increment(ref _persistingPeerCommitments);
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var repository = unitOfWork.BroadcastTransactionDbRepository;
            var existing = await repository.GetByTransactionIdAsync(txId);
            if (existing is not null)
            {
                if (existing.Purpose != BroadcastPurpose.PeerCommitment)
                    return;

                // A reorg put the commitment back into the mempool after its row was abandoned
                if (await repository.MarkPendingAsync(txId))
                {
                    await unitOfWork.SaveChangesAsync();
                    existing.MarkPending();
                }

                if (existing.State == BroadcastState.Pending)
                    _blockchainMonitor.TrackPendingBroadcast(existing);
                return;
            }

            // NL-779: the funding output is already spent by a processed block (this commitment confirmed, or another
            // transaction did): a row now would never be confirmed by a block
            if (unitOfWork.OnchainResolutionDbRepository is { } resolutions
             && await resolutions.GetCloseAsync(channelId) is not null)
                return;

            var row = new BroadcastTransactionModel(new SignedTransaction(txId, (byte[])rawTransaction.Clone()),
                                                    BroadcastPurpose.PeerCommitment, channelId,
                                                    _blockchainMonitor.LastProcessedBlockHeight);
            repository.Add(row);
            await unitOfWork.SaveChangesAsync();

            // NL-779: followed by the monitor from now on, so the block that holds it marks it confirmed (a block
            // processed before this point is found by the monitor's lookup when bitcoind refuses the row)
            _blockchainMonitor.TrackPendingBroadcast(row);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogOnce($"{channelId}:{txId}:handover", e,
                    "Cannot store the peer's commitment {TxId} of channel {ChannelId}; a restart then finds it in the "
                  + "mempool only (NL-390)", Display(txId), channelId);
        }
        finally
        {
            Interlocked.Decrement(ref _persistingPeerCommitments);
        }
    }

    /// <summary>The peer's commitment's part of the round (see the remarks); <paramref name="handOvers"/> are the
    /// channel's <see cref="BroadcastPurpose.PeerCommitment"/> rows.</summary>
    private async Task<PathState> RunPeerLockedAsync(ChannelModel channel, IUnitOfWork unitOfWork,
                                                     IReadOnlyList<BroadcastTransactionModel> children,
                                                     IReadOnlyList<BroadcastTransactionModel> handOvers,
                                                     bool otherPending, uint height, RoundResult result,
                                                     CancellationToken cancellationToken)
    {
        var channelId = channel.ChannelId;
        var repository = unitOfWork.BroadcastTransactionDbRepository;
        var pendingChildren = children.Where(c => c.State == BroadcastState.Pending).ToList();

        var close = unitOfWork.OnchainResolutionDbRepository is { } resolutions
                        ? await resolutions.GetCloseAsync(channelId)
                        : null;
        if (close is not null)
        {
            var isPeers = close.Kind is ChannelCloseKind.RemoteCommitment or ChannelCloseKind.RemoteNextCommitment
                                     or ChannelCloseKind.FutureCommitment or ChannelCloseKind.RevokedCommitment;
            var onClose = isPeers
                              ? pendingChildren.Where(c => ParentOf(c) == close.CommitmentTransactionId).ToList()
                              : [];
            var staged = false;
            foreach (var stale in pendingChildren.Except(onClose))
                staged |= await repository.MarkAbandonedAsync(stale.TransactionId);

            if (onClose.Count > 0)
            {
                var settlement = await ChildrenSettlementAsync(channelId, close.CommitmentTransactionId,
                                                               AnchorVoutOf(onClose), close.SpentAtHeight, height);
                if (settlement == ChildrenSettlement.NotYet)
                {
                    if (staged)
                        await unitOfWork.SaveChangesAsync();
                    return PathState.Active;
                }

                await PlanInputsRescueAsync(channelId, unitOfWork, onClose, result, settlement, cancellationToken);
            }

            foreach (var settled in onClose)
                staged |= await repository.MarkAbandonedAsync(settled.TransactionId);
            // The null arm is typed: a bare null here compiles to (TxId)null — the user conversion from byte[] —
            // which throws when the arm is taken
            var keepTxId = isPeers ? close.CommitmentTransactionId : (TxId?)null;
            staged |= await AbandonStaleHandOversAsync(handOvers, repository, keepTxId);
            if (staged)
                await unitOfWork.SaveChangesAsync();

            if (isPeers && _options.SweepAnchors)
                result.Sweep ??= await PlanPeerAnchorSweepAsync(channel, unitOfWork, close,
                                                                children.Any(c => ParentOf(c)
                                                                               == close.CommitmentTransactionId),
                                                                height, cancellationToken);
            else
                _peerCommitments.TryRemove(channelId, out _);

            // The funding output is spent on chain, so no new child is made: a reservation without a child row (a
            // crash between the reservation's save and the child's) goes back too
            return children.Count > 0 || await HoldsUnreleasedReservationAsync(channelId, cancellationToken)
                       ? PathState.Done
                       : PathState.None;
        }

        var found = await FindPeerCommitmentInMempoolAsync(channel, pendingChildren);
        if (found is { } peer)
        {
            result.PeerCommitmentInMempool = true;

            // bitcoind does not have it (below its mempool minimum): the child is sent with the handed-over bytes as a
            // package, like our own commitment's (NL-389) — but only while the hand-over is still trusted (NL-390)
            if (!peer.InMempool && await PeerHandOverGoneAsync(peer.TxId, height))
            {
                _peerCommitments.TryRemove(channelId, out _);
                var goneStaged = false;
                foreach (var stale in pendingChildren)
                    goneStaged |= await repository.MarkAbandonedAsync(stale.TransactionId);
                goneStaged |= await AbandonStaleHandOversAsync(handOvers, repository, null);
                if (goneStaged)
                    await unitOfWork.SaveChangesAsync();
                _logger.LogWarning("The peer's commitment {TxId} of channel {ChannelId} has not entered bitcoind's "
                                 + "mempool for {Blocks} blocks; its anchor children {TxIds} are abandoned and the "
                                 + "wallet inputs released", Display(peer.TxId), channelId,
                                   _options.PeerCommitmentMissingBlocks,
                                   string.Join(", ", pendingChildren.Select(c => Display(c.TransactionId))));
                return PathState.Done;
            }

            if (!peer.InMempool)
                result.PeerPackageParent = (peer.TxId, peer.RawTransaction);

            var staged = false;
            foreach (var stale in pendingChildren.Where(c => ParentOf(c) != peer.TxId))
                staged |= await repository.MarkAbandonedAsync(stale.TransactionId);
            staged |= await AbandonStaleHandOversAsync(handOvers, repository, peer.TxId);

            var own = pendingChildren.Where(c => ParentOf(c) == peer.TxId).ToList();
            PlannedChild? planned = null;
            if (GetPeerDeadlineAndStake(channel, peer.IsNext) is var (deadline, stakeSat))
                planned = await PlanChildAsync(channel,
                                               new ParentCommitment(peer.TxId, peer.RawTransaction, deadline, stakeSat,
                                                                    true), own, !otherPending, height,
                                               cancellationToken);
            else
                LogOnce($"{channelId}:{peer.TxId}:nospec",
                        "The peer's commitment {TxId} of channel {ChannelId} is in the mempool, but we hold no state of "
                      + "it; it is not fee-bumped", Display(peer.TxId), channelId);

            if (planned is not null)
            {
                await StoreChildAsync(channelId, unitOfWork, planned);
                result.PeerChild = planned.Row;
            }
            else
            {
                if (staged)
                    await unitOfWork.SaveChangesAsync();

                // No new child this round: the newest pending one is checked, and packaged with the handed-over bytes
                // when bitcoind does not have it (NL-389)
                if (!peer.InMempool)
                    result.PeerCheckChild = LatestChild(own);
            }

            return PathState.Active;
        }

        if (pendingChildren.Count == 0)
            return children.Count > 0 ? PathState.Done : PathState.None;

        // Our children spend a peer commitment bitcoind does not have: evicted, replaced by ours, or in a block the
        // monitor has not processed yet (then the close settles them)
        if (!await PeerCommitmentGoneAsync(pendingChildren, height))
            return PathState.Active;

        foreach (var child in pendingChildren)
            await repository.MarkAbandonedAsync(child.TransactionId);
        await AbandonStaleHandOversAsync(handOvers, repository, null);
        await unitOfWork.SaveChangesAsync();
        _peerCommitments.TryRemove(channelId, out _);
        _logger.LogWarning("The peer's commitment of channel {ChannelId} left bitcoind's mempool without confirming; "
                         + "its anchor children {TxIds} are abandoned", channelId,
                           string.Join(", ", pendingChildren.Select(c => Display(c.TransactionId))));
        return PathState.Done;
    }

    /// <summary>
    /// True once the handed-over commitment has been missing from bitcoind (or unverifiable: no chain service) for
    /// <see cref="AnchorCpfpOptions.PeerCommitmentMissingBlocks"/> blocks in a row, one count per height, while the
    /// monitor is at bitcoind's tip and not halted: our packages have not got it in, and a reservation whose parent
    /// no mempool holds must not live forever (NL-390). With a chain service every count is a real miss; without one
    /// the trust in the hand-over is simply bounded.
    /// </summary>
    private async Task<bool> PeerHandOverGoneAsync(TxId txId, uint height)
    {
        if (_chainService is not null)
        {
            if (_blockchainMonitor.IsChainProcessingHalted)
                return false;

            try
            {
                if (_blockchainMonitor.LastProcessedBlockHeight < await _chainService.GetCurrentBlockHeightAsync())
                    return false;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return false;
            }
        }

        lock (_peerCommitmentMissing)
        {
            var (lastHeight, count) = _peerCommitmentMissing.GetValueOrDefault(txId);
            if (lastHeight != height || count == 0)
                _peerCommitmentMissing[txId] = (height, count + 1);
            return _peerCommitmentMissing[txId].Count >= Math.Max(1, _options.PeerCommitmentMissingBlocks);
        }
    }

    /// <summary>
    /// Stages abandoning the channel's pending hand-over rows but the one for <paramref name="keepTxId"/> (a
    /// commitment the close or the mempool still holds: its own row confirms, so the monitor retires it). Null
    /// <paramref name="keepTxId"/> abandons every pending one.
    /// </summary>
    private async Task<bool> AbandonStaleHandOversAsync(IReadOnlyList<BroadcastTransactionModel> handOvers,
                                                        IBroadcastTransactionDbRepository repository, TxId? keepTxId)
    {
        var staged = false;
        foreach (var handOver in handOvers)
        {
            if (handOver.TransactionId == keepTxId || handOver.State != BroadcastState.Pending)
                continue;

            staged |= await repository.MarkAbandonedAsync(handOver.TransactionId);
        }

        return staged;
    }

    /// <summary>
    /// True when the fee-input source holds inputs for the channel that this process has not released yet (checked
    /// once released: never again).
    /// </summary>
    private async Task<bool> HoldsUnreleasedReservationAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        if (_feeInputSource is null)
            return false;

        lock (_released)
            if (_released.Contains(channelId))
                return false;

        return (await _feeInputSource.GetReservedAsync(channelId, cancellationToken)).Count > 0;
    }

    /// <summary>
    /// The peer's commitment that spends the funding output in bitcoind's mempool (see the remarks), or null. Without a
    /// chain service only the one the mempool reactor handed over. When bitcoind has none of the candidates, the
    /// handed-over one is still returned (<see cref="FoundPeerCommitment.InMempool"/> false): a commitment below our
    /// mempool minimum never entered it, and its bytes are what the child is packaged with (NL-389).
    /// </summary>
    private async Task<FoundPeerCommitment?> FindPeerCommitmentInMempoolAsync(
        ChannelModel channel, IReadOnlyList<BroadcastTransactionModel> pendingChildren)
    {
        if (channel.FundingOutput is not { TransactionId: { } fundingTxId, Index: { } fundingIndex })
            return null;

        var candidates = new List<(TxId TxId, bool? IsNext)>();
        _peerCommitments.TryGetValue(channel.ChannelId, out var seen);
        if (seen is not null)
        {
            // A hand-over loaded from its row does not say whether the commitment is the peer's next one (NL-390)
            seen = seen.IsNext is { } known ? seen : seen with { IsNext = DerivePeerIsNext(channel, seen.TxId) };
            candidates.Add((seen.TxId, seen.IsNext));
        }

        var current = RebuildPeerCommitmentTxId(channel, false);
        var next = RebuildPeerCommitmentTxId(channel, true);
        foreach (var parent in pendingChildren.Select(ParentOf).OfType<TxId>())
            candidates.Add((parent, parent == current ? false : parent == next ? true : null));
        if (current is { } c)
            candidates.Add((c, false));
        if (next is { } n)
            candidates.Add((n, true));

        var fundingOutPoint = ToOutPoint(fundingTxId, fundingIndex);
        foreach (var (txId, isNext) in candidates.DistinctBy(x => x.TxId))
        {
            var memory = seen is not null && seen.TxId == txId ? seen : null;
            if (_chainService is null)
            {
                if (memory is not null)
                    return new FoundPeerCommitment(txId, memory.RawTransaction, memory.IsNext ?? false, InMempool: true);
                continue;
            }

            Transaction? tx;
            try
            {
                tx = await _chainService.GetTransactionAsync(new uint256(txId));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogOnce($"{channel.ChannelId}:peer-lookup", e,
                        "Cannot ask bitcoind for the peer's commitment of channel {ChannelId}", channel.ChannelId);
                if (memory is not null)
                    return new FoundPeerCommitment(txId, memory.RawTransaction, memory.IsNext ?? false, InMempool: true);
                continue;
            }

            if (tx is not null && tx.GetHash() == new uint256(txId)
             && tx.Inputs.Any(i => i.PrevOut == fundingOutPoint))
            {
                lock (_peerCommitmentMissing)
                    _peerCommitmentMissing.Remove(txId);
                return new FoundPeerCommitment(txId, tx.ToBytes(), memory?.IsNext ?? isNext ?? false, InMempool: true);
            }
        }

        if (seen is not null)
            return new FoundPeerCommitment(seen.TxId, seen.RawTransaction, seen.IsNext ?? false, InMempool: false);

        return null;
    }

    /// <summary>
    /// True once every commitment the pending children spend has been missing from bitcoind for
    /// <see cref="AnchorCpfpOptions.PeerCommitmentMissingBlocks"/> blocks (one count per height, only while the
    /// monitor is at bitcoind's tip and not halted). Never without a chain service or when bitcoind cannot be asked.
    /// </summary>
    private async Task<bool> PeerCommitmentGoneAsync(IReadOnlyList<BroadcastTransactionModel> pendingChildren,
                                                     uint height)
    {
        if (_chainService is null || _blockchainMonitor.IsChainProcessingHalted)
            return false;

        try
        {
            if (_blockchainMonitor.LastProcessedBlockHeight < await _chainService.GetCurrentBlockHeightAsync())
                return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }

        var gone = true;
        foreach (var parent in pendingChildren.Select(ParentOf).OfType<TxId>().Distinct())
        {
            lock (_peerCommitmentMissing)
            {
                var (lastHeight, count) = _peerCommitmentMissing.GetValueOrDefault(parent);
                if (lastHeight != height || count == 0)
                    _peerCommitmentMissing[parent] = (height, count + 1);
                if (_peerCommitmentMissing[parent].Count < Math.Max(1, _options.PeerCommitmentMissingBlocks))
                    gone = false;
            }
        }

        return gone;
    }

    /// <summary>
    /// The sweep of the anchors of the peer's confirmed commitment, once due: its bytes from the mempool reactor's
    /// hand-over, else from the block that holds it.
    /// </summary>
    private async Task<BroadcastTransactionModel?> PlanPeerAnchorSweepAsync(ChannelModel channel, IUnitOfWork unitOfWork,
                                                                    ChannelCloseModel close, bool anyChild, uint height,
                                                                    CancellationToken cancellationToken)
    {
        if (!IsSweepDue(close.SpentAtHeight, height) || IsSwept(close.CommitmentTransactionId))
            return null;

        byte[]? raw = null;
        if (_peerCommitments.TryGetValue(channel.ChannelId, out var seen)
         && seen.TxId == close.CommitmentTransactionId)
            raw = seen.RawTransaction;
        else if (_chainService is not null)
        {
            try
            {
                var block = await _chainService.GetBlockAsync(close.SpentAtHeight);
                var txId = new uint256(close.CommitmentTransactionId);
                raw = block?.Transactions.FirstOrDefault(t => t.GetHash() == txId)?.ToBytes();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogInformation("Cannot read the peer's commitment {TxId} of channel {ChannelId} from block "
                                     + "{Height}: {Reason}", Display(close.CommitmentTransactionId), channel.ChannelId,
                                       close.SpentAtHeight, e.Message);
                return null;
            }
        }

        if (raw is null)
        {
            LogOnce($"{channel.ChannelId}:{close.CommitmentTransactionId}:sweep-read",
                    "Cannot read the peer's commitment {TxId} of channel {ChannelId}; its anchors are not swept",
                    Display(close.CommitmentTransactionId), channel.ChannelId);
            return null;
        }

        var sweep = await PlanAnchorSweepAsync(channel, close.CommitmentTransactionId, raw, true, null,
                                               close.SpentAtHeight, anyChild, height, cancellationToken,
                                               await GetRevokedPeerPointAsync(channel, unitOfWork, close));
        _peerCommitments.TryRemove(channel.ChannelId, out _);
        return sweep;
    }

    /// <summary>The txid of the peer's current (or next) commitment rebuilt from the snapshot; null when unknown.</summary>
    private TxId? RebuildPeerCommitmentTxId(ChannelModel channel, bool next)
    {
        if (_commitmentOutputMapper is null || channel.Commitments is not { } commitments)
            return null;

        var commit = next ? commitments.RemoteNextCommit?.Commit : commitments.RemoteCommit;
        if (commit is null)
            return null;

        try
        {
            return _commitmentOutputMapper.Map(channel, CommitmentTxSpec.FromCommitmentSpec(commit.Spec),
                                               CommitmentCase.Remote, commit.Number, commit.PerCommitmentPoint)
                                          .ExpectedTxId;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogOnce($"{channel.ChannelId}:{commit.Number}:rebuild", e,
                    "Cannot rebuild the peer's commitment {Number} of channel {ChannelId}", commit.Number,
                    channel.ChannelId);
            return null;
        }
    }

    /// <summary>
    /// The deadline (earliest <c>cltv_expiry</c>) and our stake (our <c>to_remote</c> plus the HTLCs, in sat) of the
    /// peer's current or next commitment, counting only the HTLCs with an output on it (the peer's dust limit); null
    /// without that state.
    /// </summary>
    private static (uint? Deadline, ulong StakeSat)? GetPeerDeadlineAndStake(ChannelModel channel, bool next)
    {
        if (channel.Commitments is not { } commitments)
            return null;

        var spec = next ? commitments.RemoteNextCommit?.Commit.Spec : commitments.RemoteCommit.Spec;
        if (spec is null)
            return null;

        var dust = commitments.Params.Remote.DustLimitSatoshis;
        var untrimmed = spec.Htlcs.Where(h => !CommitmentFeeCalculator.IsHtlcTrimmed(spec, h, dust,
                                                                                     commitments.Params.Format))
                            .ToList();
        var htlcMsat = untrimmed.Aggregate(0UL, (sum, h) => checked(sum + h.AmountMsat));
        return (AnchorCpfpPolicy.GetDeadline(untrimmed.Select(h => h.CltvExpiry)),
                (spec.LocalMsat + htlcMsat) / 1000);
    }

    /// <summary>The peer's commitment the mempool reactor saw (memory only). <see cref="IsNext"/> is null for one
    /// loaded from its persisted hand-over row: derived from the rebuilt txids when it is found (NL-390).</summary>
    private sealed record PeerCommitmentSeen(TxId TxId, byte[] RawTransaction, bool? IsNext);

    /// <summary>The peer's commitment found for this round: <see cref="InMempool"/> is false when bitcoind does not have
    /// it (below its mempool minimum; NL-389) and the bytes are the handed-over ones.</summary>
    private readonly record struct FoundPeerCommitment(TxId TxId, byte[] RawTransaction, bool IsNext, bool InMempool);

    /// <summary>Whether <paramref name="txId"/> is the peer's next commitment, guessed from the rebuilt txids (the
    /// current one is the safe guess when neither matches or we hold no state).</summary>
    private bool DerivePeerIsNext(ChannelModel channel, TxId txId) =>
        RebuildPeerCommitmentTxId(channel, true) == txId;
}