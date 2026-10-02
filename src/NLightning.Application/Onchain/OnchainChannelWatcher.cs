using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain;

using Accounting;
using Channels.Safety.Interfaces;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Classifiers;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Onchain.Interfaces;
using Interfaces;

/// <summary>
/// The on-chain side of a channel when its funding output is spent (BOLT 5 plan O2-T5, D7; resolves the detection half
/// of NL-272): under the channel's lock it classifies the spend (<see cref="FundingSpendClassifier"/>), maps the
/// commitment's outputs (<see cref="ICommitmentOutputMapper"/>) and, in <b>one save</b>, records the close
/// (<see cref="ChannelCloseModel"/>), one <see cref="OutputResolutionModel"/> and one persisted resolution-output watch
/// per output of ours, abandons our own pending commitment broadcast when the peer's transaction won, stores the
/// channel's <c>error</c> and moves the channel to <see cref="ChannelState.OnchainResolving"/>. After the lock it follows
/// the new watches, sends the <c>error</c> to a connected peer (B5-GEN-04), raises the alerts (B5-GEN-06, B5-RMT-03) and
/// runs the channel's first resolution round.
/// </summary>
/// <remarks>
/// <para>Every commitment kind moves the channel to <c>OnchainResolving</c> (37), which refuses every channel operation
/// and every peer message (the stored error is sent again). An unknown spend does too, with no output here: the remote
/// commitment resolver then treats it like a commitment it cannot rebuild (every output watched, a <c>to_remote</c> of
/// ours swept, our offered HTLCs failed upstream once expired and reasonably deep, NL-320), and the channel closes once
/// those outputs are irrevocable or ignored. A mutual close is left to the channel manager (it only
/// arrives for a channel in its close negotiation, or a Failed one that signed a closing tx before it failed,
/// NL-312).</para>
/// <para>Idempotent: the chain monitor raises a spend again for a replayed block; a spend already recorded changes
/// nothing. A different spend recorded before (a reorg) is recorded over it in one save that also ignores the old
/// close's output rows and abandons the channel's other pending transactions (NL-292, O6-T3).</para>
/// <para>Transactions prepared from the mempool (BOLT 5 plan O8: a penalty of a revoked commitment signed before it
/// confirmed, <c>Mempool/MempoolReactor</c>) are settled in the same save: the rows of the outputs such a transaction
/// spends name it as their resolving transaction (<see cref="OutputResolutionState.Broadcast"/>), so the resolver builds
/// nothing twice, and one that spends another transaction than the confirmed spend is abandoned.</para>
/// </remarks>
public sealed class OnchainChannelWatcher : IOnchainChannelWatcher
{
    /// <summary>The <c>error</c> text the peer gets when we see the channel closed on chain.</summary>
    public const string OnchainErrorMessage = "channel closed on chain";

    private readonly IChannelErrorSender _channelErrorSender;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ICommitmentOutputMapper _commitmentOutputMapper;
    private readonly IOnchainResolutionExecutor _executor;
    private readonly ILogger<OnchainChannelWatcher> _logger;
    private readonly IOutpointWatcher _outpointWatcher;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    public OnchainChannelWatcher(IChannelErrorSender channelErrorSender, IChannelLockProvider channelLockProvider,
                                 IChannelMemoryRepository channelMemoryRepository,
                                 ICommitmentOutputMapper commitmentOutputMapper, IOnchainResolutionExecutor executor,
                                 ILogger<OnchainChannelWatcher> logger, IOutpointWatcher outpointWatcher,
                                 IServiceScopeFactory serviceScopeFactory, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _channelErrorSender = channelErrorSender;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _commitmentOutputMapper = commitmentOutputMapper;
        _executor = executor;
        _logger = logger;
        _outpointWatcher = outpointWatcher;
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <inheritdoc />
    public async Task<FundingSpendOutcome?> HandleFundingSpentAsync(OutpointSpentEventArgs args,
                                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var channelId = args.ChannelId;
        if (!ChainTxMapper.TryParse(args.SpendingTransaction.RawTxBytes, out var spend) || spend is null)
        {
            _logger.LogError("The funding spend {TxId} of channel {ChannelId} can't be parsed",
                             Display(args.SpendingTransaction.TxId), channelId);
            return null;
        }

        Recorded? recorded = null;
        ChannelFunding? spliceToBroadcastOn = null;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            {
                _logger.LogDebug("Funding spend {TxId} of channel {ChannelId}: the channel is not loaded",
                                 Display(spend.TxId), channelId);
                return null;
            }

            if (channel.FundingOutput is not { TransactionId: not null, Index: not null }
             || channel.State is ChannelState.Closed or ChannelState.Stale)
                return null;

            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // Splicing plan §3.6: a spend of any funding of the channel (current, pending splice, retired) is judged
            // against that funding; an outpoint that is none of them is not ours to judge
            var fundings = await OnchainFundings.GetAllAsync(unitOfWork, channel, _logger);
            if (args.SpentTransactionId is { } spentTxId
             && fundings.All(f => f.FundingTxId != spentTxId
                               || (args.SpentOutputIndex is { } spentIndex && spentIndex != f.OutputIndex)))
                return null;

            var existing = await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channelId);
            if (existing is not null && existing.CommitmentTransactionId == spend.TxId)
            {
                var outputs = await unitOfWork.OnchainResolutionDbRepository.GetOutputsByChannelIdAsync(channelId);
                var blockHash = args.BlockHash ?? existing.BlockHash;
                if (existing.SpentAtHeight != args.BlockHeight || !existing.BlockHash.Equals(blockHash))
                {
                    // The same transaction confirmed again in another block (a reorg): the irrevocable depth counts
                    // from the new block. The outputs and watches stay as they are (same transaction).
                    _logger.LogWarning("Funding spend {TxId} of channel {ChannelId} is now at height {Height} (was "
                                     + "{Previous}, reorg): recording the new block", Display(spend.TxId), channelId,
                                       args.BlockHeight, existing.SpentAtHeight);
                    await unitOfWork.OnchainResolutionDbRepository.UpsertCloseAsync(existing with
                    {
                        SpentAtHeight = args.BlockHeight,
                        BlockHash = blockHash
                    });
                    await unitOfWork.SaveChangesAsync();
                }

                return new FundingSpendOutcome(existing.Kind, outputs.Count, Replayed: true);
            }

            var factory = scope.ServiceProvider.GetService<ICommitmentTransactionModelFactory>();
            var contexts = await BuildContextsAsync(channel, fundings, unitOfWork, factory);
            var match = FundingSpendClassifier.ClassifyAny(spend, contexts.Select(c => c.Context).ToList());
            if (match is null)
                return null;

            var spentFunding = contexts.First(c => ReferenceEquals(c.Context, match.Context)).Funding;
            var classification = match.Classification;
            switch (classification.Kind)
            {
                case FundingSpendKind.NotFundingSpend:
                    return null;
                case FundingSpendKind.Mutual:
                    _logger.LogInformation("The funding output of channel {ChannelId} was spent by mutual close "
                                         + "{TxId}; the close path records it", channelId, Display(spend.TxId));
                    return null;
                case FundingSpendKind.Splice:
                    spliceToBroadcastOn =
                        await OnSpliceConfirmedAsync(unitOfWork, channel, fundings, spentFunding, spend, existing);
                    break;
                default:
                    if (existing is not null)
                        _logger.LogCritical("The funding output of channel {ChannelId} is now spent by {TxId}, not by "
                                          + "the recorded {Recorded} (reorg): recording the new spend", channelId,
                                            Display(spend.TxId), Display(existing.CommitmentTransactionId));

                    if (!OnchainFundings.IsCurrent(channel, spentFunding))
                        _logger.LogCritical("Channel {ChannelId}: {Kind} {TxId} spends funding {FundingTxId} "
                                          + "({Status}), not the current one", channelId, classification.Kind,
                                            Display(spend.TxId), Display(spentFunding.FundingTxId),
                                            spentFunding.Status);

                    var closeKind = ToCloseKind(classification.Kind);
                    var (descriptors, point, unmapped, spec) =
                        await MapOutputsAsync(scope, channel, classification, spend, spentFunding, fundings, factory);
                    if (existing is not null)
                        await RetireReplacedCloseAsync(unitOfWork, channelId, existing, spend.TxId);
                    recorded = await PersistAsync(scope, channel, args, spend, classification, closeKind, descriptors,
                                                  point, unmapped, spentFunding, fundings, spec);
                    break;
            }
        }

        if (spliceToBroadcastOn is not null)
        {
            // SP2-C-T2 (SP-I4): our commitment on the funding the splice spent can never confirm; the one on the
            // splice funding can, at the same number
            await BroadcastOnSpliceAsync(channelId, spliceToBroadcastOn, cancellationToken);
            return null;
        }

        if (recorded is null)
            return null;

        foreach (var watch in recorded.NewWatches)
            _outpointWatcher.TrackWatchedOutpoint(watch);

        // O8: a prepared transaction made pending again goes out now (the monitor then resends it after every block)
        if (_outpointWatcher is IChainBroadcaster broadcaster)
            foreach (var transaction in recorded.Revived)
            {
                try
                {
                    if (!await broadcaster.PublishAsync(transaction))
                        _logger.LogWarning("{Purpose} {TxId} of channel {ChannelId} was refused; it is sent again "
                                         + "after the next block", transaction.Purpose,
                                           Display(transaction.TransactionId), channelId);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogError(e, "Publishing {Purpose} {TxId} of channel {ChannelId} failed", transaction.Purpose,
                                     Display(transaction.TransactionId), channelId);
                }
            }

        if (recorded.ErrorToSend is { } error)
            await _channelErrorSender.TrySendAsync(recorded.Peer, error);

        foreach (var alert in recorded.Alerts)
            _logger.LogCritical("[{RequirementId}] Channel {ChannelId}: {Alert}", alert.RequirementId, channelId,
                                alert.Message);

        try
        {
            // The chain monitor went on processing blocks while this spend was classified: an output of the commitment
            // spent in its own block or in a block processed before the watches were tracked is found here (BOLT 5:
            // monitor every output that is not irrevocably resolved)
            await _executor.CatchUpSpendsAsync(channelId, recorded.NewWatches, args.BlockHeight, cancellationToken);
            await _executor.ResolveChannelAsync(channelId, args.BlockHeight, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "The first resolution round of channel {ChannelId} failed; the next block retries it",
                             channelId);
        }

        return recorded.Outcome;
    }

    /// <summary>
    /// A splice transaction of the channel confirmed (splicing plan §3.6, SP2-C-T1): not a close; the channel stays on
    /// its fundings and the lock moves it. When the channel failed meanwhile (our commitment on the funding the splice
    /// spends can no longer confirm), or a close recorded before was reorged out for it, the pending splice funding is
    /// returned: our commitment on it must be broadcast instead (SP2-C-T2).
    /// </summary>
    /// <remarks>
    /// The reorged-out close: a close of the current funding discarded every pending splice in its save
    /// (<see cref="DiscardConflictingSplicesAsync"/>), so a <see cref="ChannelFundingStatus.Discarded"/> splice that
    /// confirms double-spends the recorded close, which is no longer in the chain. In one save before anything is
    /// published (NL-292 on the splice path): the old close's rows are ignored and its pending transactions abandoned
    /// (<see cref="RetireReplacedCloseAsync"/>), the close record is removed (the executor would otherwise revive our
    /// commitment on the spent funding) and the splice funding is <see cref="ChannelFundingStatus.Pending"/> again. A
    /// recorded close next to a splice that is still pending spent another funding (the splice's own output): it stands.
    /// </remarks>
    private async Task<ChannelFunding?> OnSpliceConfirmedAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                               IReadOnlyList<ChannelFunding> fundings,
                                                               ChannelFunding spentFunding, ChainTx spend,
                                                               ChannelCloseModel? existing)
    {
        var splice = fundings.FirstOrDefault(f => f.FundingTxId == spend.TxId);
        _logger.LogInformation("The funding output {FundingTxId} of channel {ChannelId} was spent by its splice {TxId} "
                             + "({Status})", Display(spentFunding.FundingTxId), channel.ChannelId,
                               Display(spend.TxId), splice?.Status);
        if (splice is null || splice.Status is ChannelFundingStatus.Current or ChannelFundingStatus.Replaced)
            return null;

        if (existing is null)
        {
            if (splice.Status == ChannelFundingStatus.Discarded)
                _logger.LogCritical("[B5-GEN-06] Channel {ChannelId}: the discarded splice {TxId} confirmed; its "
                                  + "funding output holds the channel's funds now", channel.ChannelId,
                                    Display(spend.TxId));

            return channel.State is ChannelState.Failed or ChannelState.OnchainResolving ? splice : null;
        }

        if (splice.Status != ChannelFundingStatus.Discarded)
        {
            _logger.LogInformation("Channel {ChannelId}: splice {TxId} confirmed next to the recorded close {Recorded}, "
                                 + "which spends another funding", channel.ChannelId, Display(spend.TxId),
                                   Display(existing.CommitmentTransactionId));
            return null;
        }

        _logger.LogCritical("[B5-GEN-06] Channel {ChannelId}: the recorded close {Recorded} left the chain and the "
                          + "discarded splice {TxId} spends the funding output instead; retiring the close and "
                          + "broadcasting our commitment on the splice funding", channel.ChannelId,
                            Display(existing.CommitmentTransactionId), Display(spend.TxId));
        await RetireReplacedCloseAsync(unitOfWork, channel.ChannelId, existing, spend.TxId);
        await unitOfWork.OnchainResolutionDbRepository.DeleteCloseAsync(channel.ChannelId);
        splice = splice with { Status = ChannelFundingStatus.Pending };
        await unitOfWork.ChannelFundingDbRepository.UpsertAsync(channel.ChannelId, splice);
        await unitOfWork.SaveChangesAsync();
        return splice;
    }

    /// <summary>
    /// Hands the confirmed splice funding to <see cref="ISpliceCommitmentBroadcaster"/> (the failure service), outside
    /// the channel's lock. A failure is logged: the failure service also checks every block.
    /// </summary>
    private async Task BroadcastOnSpliceAsync(ChannelId channelId, ChannelFunding splice,
                                              CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<ISpliceCommitmentBroadcaster>() is not { } broadcaster)
            {
                _logger.LogCritical("Channel {ChannelId}: splice {TxId} confirmed after the channel failed, and no "
                                  + "splice commitment broadcaster is registered", channelId,
                                    Display(splice.FundingTxId));
                return;
            }

            await broadcaster.BroadcastOnSpliceAsync(channelId, splice.FundingTxId, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Broadcasting our commitment on splice {TxId} of channel {ChannelId} failed",
                             Display(splice.FundingTxId), channelId);
        }
    }

    /// <summary>
    /// O6-T3 (NL-292): another transaction now spends the funding output than the one recorded (the old one was reorged
    /// out). Every output row of the old close is <see cref="OutputResolutionState.Ignored"/> (its transaction is not on
    /// chain) and every pending broadcast of the channel but the new spend is abandoned (it spends outputs of a
    /// transaction that is not on chain, or conflicts with the new spend), in the save that records the new close.
    /// </summary>
    private async Task RetireReplacedCloseAsync(IUnitOfWork unitOfWork, ChannelId channelId, ChannelCloseModel old,
                                                TxId newSpend)
    {
        var rows = await unitOfWork.OnchainResolutionDbRepository.GetOutputsByChannelIdAsync(channelId);

        // An HTLC of the old close already resolved may have been removed upstream (a fail at reasonable depth is
        // final); nothing re-checks it against the new close's HTLC outputs — the switch alerts when a preimage of
        // such an HTLC shows up on the new close (NL-330) — so the operator is told what was told upstream
        var resolvedHtlcs = new List<string>();
        foreach (var row in rows)
        {
            if (row.HtlcId is not { } htlcId || row.TransactionId == newSpend
             || row.State is not (OutputResolutionState.Resolved or OutputResolutionState.Irrevocable))
                continue;

            var outcome = row.HtlcDirection == HtlcDirection.Outgoing
                ? await HtlcUpstreamOutcomeReader.ReadAsync(unitOfWork, channelId, htlcId)
                : HtlcUpstreamOutcome.Unknown;
            var told = outcome switch
            {
                HtlcUpstreamOutcome.Failed => " (failed upstream)",
                HtlcUpstreamOutcome.Fulfilled => " (fulfilled upstream)",
                _ => string.Empty
            };
            resolvedHtlcs.Add($"{row.HtlcDirection} {htlcId}{told}");
        }

        if (resolvedHtlcs.Count > 0)
            _logger.LogCritical("[B5-GEN-06] Channel {ChannelId}: the reorged-out close {TxId} had already resolved "
                              + "HTLC output(s) {Htlcs}; their upstream removal (a fail at reasonable depth) may not "
                              + "match the new close {NewTxId}, check them by hand", channelId,
                                Display(old.CommitmentTransactionId), string.Join(", ", resolvedHtlcs),
                                Display(newSpend));

        foreach (var row in rows.Where(r => r.TransactionId != newSpend
                                         && r.State is not (OutputResolutionState.Ignored
                                                            or OutputResolutionState.Irrevocable)))
            await unitOfWork.OnchainResolutionDbRepository.UpsertOutputAsync(row with
            {
                State = OutputResolutionState.Ignored
            });

        // A transaction prepared from the mempool for the new spend (O8) is kept: PersistAsync links it to the rows
        var broadcasts = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channelId);
        foreach (var stale in broadcasts.Where(b => b.State == BroadcastState.Pending && b.TransactionId != newSpend
                                                 && !SpendsFrom(b, newSpend)))
            await unitOfWork.BroadcastTransactionDbRepository.MarkAbandonedAsync(stale.TransactionId);

        // NL-602: the old close and the resolutions of its outputs are negated in the same save
        await StageRetiredCloseReversalsAsync(unitOfWork, channelId, old, rows, newSpend);

        _logger.LogWarning("Channel {ChannelId}: the {Count} output(s) of the reorged-out close {TxId} are ignored and "
                         + "its pending transactions abandoned", channelId, rows.Count,
                           Display(old.CommitmentTransactionId));
    }

    /// <summary>
    /// Stages the <see cref="AccountingEventKind.ChannelForceClosed"/> event of a close being recorded (see
    /// <see cref="OnchainAccounting"/>). Never throws: a failure is logged and the close is recorded without it.
    /// </summary>
    private async Task StageForceClosedEventAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                  ChannelCloseKind closeKind, ChainTx spend, ulong? commitmentNumber,
                                                  uint height, IReadOnlyList<CommitmentOutputDescriptor> descriptors,
                                                  CommitmentTxSpec? spec, ChannelFunding spentFunding)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } accounting)
                return;

            var key = await OnchainAccounting.NewKeyAsync(
                          accounting, AccountingEventKeys.ChannelForceClosed(channel.ChannelId, spend.TxId),
                          CancellationToken.None);
            if (key is null)
                return;

            accounting.Add(OnchainAccounting.ForceClosed(channel, key, closeKind, spend, commitmentNumber, height,
                                                         descriptors, spec, LocalSource(channel)?.Spec, spentFunding,
                                                         _timeProvider.GetUtcNow()));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Channel {ChannelId}: the accounting event of close {TxId} could not be staged",
                               channel.ChannelId, Display(spend.TxId));
        }
    }

    /// <summary>
    /// Stages the <see cref="AccountingEventKind.Reversal"/>s of a close another transaction replaced: the
    /// resolutions of its outputs (resolved or given up) and the close itself. Never throws.
    /// </summary>
    private async Task StageRetiredCloseReversalsAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                       ChannelCloseModel old,
                                                       IReadOnlyList<OutputResolutionModel> rows, TxId newSpend)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } accounting)
                return;

            var now = _timeProvider.GetUtcNow();
            foreach (var row in rows.Where(r => r.TransactionId != newSpend))
            {
                AccountingEventModel? resolution = null;
                if (row.State == OutputResolutionState.Resolved)
                {
                    foreach (var key in OnchainAccounting.ResolutionKeys(row.TransactionId, row.OutputIndex))
                        if ((resolution = await OnchainAccounting.FindAsync(accounting, key, row.ResolvedHeight,
                                                                           CancellationToken.None)) is not null)
                            break;
                }
                else if (row.State == OutputResolutionState.Ignored)
                {
                    resolution = await accounting.GetByKeyAsync(
                                     AccountingEventKeys.OutputIgnored(row.TransactionId, row.OutputIndex));
                }

                if (resolution is not null)
                    await OnchainAccounting.StageReversalAsync(accounting, resolution,
                                                               row.ResolvedHeight ?? old.SpentAtHeight, now,
                                                               CancellationToken.None);
            }

            var close = await OnchainAccounting.FindAsync(
                            accounting, AccountingEventKeys.ChannelForceClosed(channelId, old.CommitmentTransactionId),
                            old.SpentAtHeight, CancellationToken.None);
            if (close is not null)
                await OnchainAccounting.StageReversalAsync(accounting, close, old.SpentAtHeight, now,
                                                           CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Channel {ChannelId}: the accounting reversals of the replaced close {TxId} could not "
                                + "be staged", channelId, Display(old.CommitmentTransactionId));
        }
    }

    /// <summary>
    /// Classifies an unconfirmed spend of the channel's funding output (BOLT 5 plan O8) exactly as a confirmed one is
    /// classified, without recording anything. Call it under the channel's lock; null when the channel has no funding
    /// output.
    /// </summary>
    internal async Task<FundingSpendClassification?> ClassifyAsync(ChannelModel channel, ChainTx spend,
                                                                   IUnitOfWork unitOfWork,
                                                                   ICommitmentTransactionModelFactory? factory = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(spend);
        var fundings = await OnchainFundings.GetAllAsync(unitOfWork, channel, _logger);
        if (fundings.Count == 0)
            return null;

        var contexts = await BuildContextsAsync(channel, fundings, unitOfWork, factory);
        return FundingSpendClassifier.ClassifyAny(spend, contexts.Select(c => c.Context).ToList())?.Classification
            ?? new FundingSpendClassification(FundingSpendKind.NotFundingSpend, null, false,
                                              "The transaction spends no funding output of the channel");
    }

    /// <summary>
    /// The purposes of the transactions the mempool reactor prepares before the funding spend confirms (O8); every
    /// other purpose (our commitment, the funding transaction, an HTLC transaction) is never prepared that way.
    /// </summary>
    internal static bool IsPreparedPurpose(BroadcastPurpose purpose) =>
        purpose is BroadcastPurpose.Penalty or BroadcastPurpose.Sweep or BroadcastPurpose.HtlcClaim;

    /// <summary>True when an input of <paramref name="broadcast"/> spends an output of <paramref name="parent"/>.</summary>
    internal static bool SpendsFrom(BroadcastTransactionModel broadcast, TxId parent) =>
        ChainTxMapper.TryParse(broadcast.RawTransaction, out var transaction) && transaction is not null
     && transaction.Inputs.Any(i => i.PreviousTxId == parent);

    /// <summary>
    /// What the classifier compares the spend with, one context per funding (splicing plan §3.6): candidates rebuilt
    /// from the persisted state on that funding's outpoint and keys. The current funding first.
    /// </summary>
    /// <remarks>
    /// Commitment numbers are shared by every funding (SP-I3), so every context carries the channel's current and next
    /// peer numbers; a commitment on a funding we cannot rebuild keeps the number with a txid that matches nothing, so a
    /// breach of it is still recognized (Revoked) and its outputs are mapped by script.
    /// </remarks>
    private async Task<IReadOnlyList<(ChannelFunding Funding, FundingSpendContext Context)>> BuildContextsAsync(
        ChannelModel channel, IReadOnlyList<ChannelFunding> fundings, IUnitOfWork unitOfWork,
        ICommitmentTransactionModelFactory? factory)
    {
        var commitmentNumber = channel.CommitmentNumber
                            ?? throw new InvalidOperationException(
                                   $"Channel {channel.ChannelId} has no commitment number obscurer");

        // Our commitments the failure service signed (their broadcast rows), per funding they spend
        IReadOnlyList<BroadcastTransactionModel> signed = [];
        try
        {
            var broadcasts = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channel.ChannelId);
            signed = broadcasts.Where(b => b is { Purpose: BroadcastPurpose.LocalCommitment, CommitmentNumber: not null })
                               .ToList();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not read the commitment broadcasts of channel {ChannelId}", channel.ChannelId);
        }

        var closingTxIds = channel.ClosingTransaction is { } closing ? new[] { closing.TxId } : null;
        var localScript = channel.LocalShutdownScript is { } local ? (byte[])local : null;
        var remoteScript = channel.RemoteShutdownScript is { } remote ? (byte[])remote : null;
        var contexts = new List<(ChannelFunding, FundingSpendContext)>();
        foreach (var funding in fundings)
        {
            var sources = await GetSourcesAsync(unitOfWork, channel, funding);
            var localCandidate = SignedCandidate(signed, funding)
                              ?? TryCandidate(channel, funding, factory, CommitmentCase.Local, sources.Local);
            var remoteCandidate = TryCandidate(channel, funding, factory, CommitmentCase.Remote, sources.Remote);
            var remoteNextCandidate = TryCandidate(channel, funding, factory, CommitmentCase.Remote,
                                                   sources.RemoteNext);
            if (!OnchainFundings.IsCurrent(channel, funding) && channel.Commitments is { } commitments)
            {
                remoteCandidate ??= new CommitmentCandidate(commitments.RemoteCommit.Number, TxId.Zero);
                if (commitments.RemoteNextCommit is { Commit.Number: var nextNumber })
                    remoteNextCandidate ??= new CommitmentCandidate(nextNumber, TxId.Zero);
            }

            contexts.Add((funding,
                          new FundingSpendContext(funding.FundingTxId, funding.OutputIndex, commitmentNumber,
                                                  localCandidate, remoteCandidate, remoteNextCandidate, closingTxIds,
                                                  localScript, remoteScript,
                                                  OnchainFundings.SpliceTxIdsFor(fundings, funding))));
        }

        return contexts;
    }

    /// <summary>Our commitment on <paramref name="funding"/> signed for broadcast (its row), if any.</summary>
    private static CommitmentCandidate? SignedCandidate(IReadOnlyList<BroadcastTransactionModel> signed,
                                                        ChannelFunding funding)
    {
        var row = signed.LastOrDefault(b => ChainTxMapper.TryParse(b.RawTransaction, out var tx) && tx is not null
                                         && tx.IndexOfInputSpending(funding.FundingTxId, funding.OutputIndex) >= 0);
        return row is null ? null : new CommitmentCandidate(row.CommitmentNumber!.Value, row.TransactionId);
    }

    private CommitmentCandidate? TryCandidate(ChannelModel channel, ChannelFunding funding,
                                              ICommitmentTransactionModelFactory? factory,
                                              CommitmentCase commitmentCase, CommitmentSource? source)
    {
        if (source is not { } s)
            return null;

        try
        {
            var map = OnchainFundings.Map(_commitmentOutputMapper, factory, channel, funding, s.Spec, commitmentCase,
                                          s.Number, s.Point);
            return new CommitmentCandidate(s.Number, map.ExpectedTxId);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not rebuild {Case} commitment {Number} of channel {ChannelId} on funding "
                                + "{FundingTxId}", commitmentCase, s.Number, channel.ChannelId,
                               Display(funding.FundingTxId));
            return null;
        }
    }

    /// <summary>
    /// The commitments of <paramref name="funding"/>: on the current funding the state machine's; on a pending splice
    /// the state machine's moved by its deltas (in memory), else its stored slots (kept in step by every transition,
    /// SP-I2), only at the channel's current numbers; on a retired funding only a peer commitment the engine still
    /// knows it was signed on (in memory).
    /// </summary>
    private static async Task<FundingSources> GetSourcesAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                              ChannelFunding funding)
    {
        if (OnchainFundings.IsCurrent(channel, funding))
            return new FundingSources(LocalSource(channel), RemoteSource(channel), RemoteNextSource(channel));

        if (channel.Commitments is not { } commitments)
            return new FundingSources(null, null, null);

        var txId = funding.FundingTxId;
        CommitmentSource? local = null, remote = null, remoteNext = null;
        if (commitments.PendingFundings.FirstOrDefault(f => f.FundingTxId == txId) is { } pending)
        {
            if (commitments.LocalCommit.SignaturesFor(txId) is not null)
                local = Source(ChannelCommitments.SpecFor(commitments.LocalCommit.Spec, pending),
                               commitments.LocalCommit.Number, null);
            remote = Source(ChannelCommitments.SpecFor(commitments.RemoteCommit.Spec, pending),
                            commitments.RemoteCommit.Number, commitments.RemoteCommit.PerCommitmentPoint);
            if (commitments.RemoteNextCommit is { } next && next.SignaturesFor(txId) is not null)
                remoteNext = Source(ChannelCommitments.SpecFor(next.Commit.Spec, pending), next.Commit.Number,
                                    next.Commit.PerCommitmentPoint);
            return new FundingSources(local, remote, remoteNext);
        }

        if (commitments.RemoteCommit.SignedOnFundings?.FirstOrDefault(f => f.FundingTxId == txId) is { } signedOn)
            remote = Source(ChannelCommitments.SpecFor(commitments.RemoteCommit.Spec, signedOn),
                            commitments.RemoteCommit.Number, commitments.RemoteCommit.PerCommitmentPoint);
        if (commitments.RemoteNextCommit is { } unacked
         && unacked.Commit.SignedOnFundings?.FirstOrDefault(f => f.FundingTxId == txId) is { } nextSignedOn)
            remoteNext = Source(ChannelCommitments.SpecFor(unacked.Commit.Spec, nextSignedOn), unacked.Commit.Number,
                                unacked.Commit.PerCommitmentPoint);
        if (funding.Status != ChannelFundingStatus.Pending)
            return new FundingSources(null, remote, remoteNext);

        // A pending splice the engine no longer holds (a restart): its stored slots, at the channel's numbers only
        try
        {
            if (unitOfWork.ChannelFundingDbRepository is { } repository)
            {
                if (await repository.GetLocalCommitmentAsync(channel.ChannelId, txId) is { RemoteSignatures: not null }
                        storedLocal
                 && storedLocal.Number == commitments.LocalCommit.Number)
                    local = Source(storedLocal.Spec, storedLocal.Number, null);
                if (remote is null && await repository.GetRemoteCommitmentAsync(channel.ChannelId, txId) is
                    { Commit: var storedRemote }
                 && storedRemote.Number == commitments.RemoteCommit.Number)
                    remote = Source(storedRemote.Spec, storedRemote.Number, storedRemote.PerCommitmentPoint);
                if (remoteNext is null && commitments.RemoteNextCommit is { } current
                 && await repository.GetRemoteNextCommitmentAsync(channel.ChannelId, txId) is { } storedNext
                 && storedNext.Commit.Number == current.Commit.Number)
                    remoteNext = Source(storedNext.Commit.Spec, storedNext.Commit.Number,
                                        storedNext.Commit.PerCommitmentPoint);
            }
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException or InvalidOperationException)
        {
            // No stored slots: the commitments of this funding are recognized by number only
        }

        return new FundingSources(local, remote, remoteNext);
    }

    private static CommitmentSource Source(CommitmentSpec spec, ulong number, CompactPubKey? point) =>
        new(CommitmentTxSpec.FromCommitmentSpec(spec), number, point);

    /// <summary>The peer's commitment awaiting its <c>revoke_and_ack</c> on the current funding.</summary>
    private static CommitmentSource? RemoteNextSource(ChannelModel channel) =>
        channel.Commitments?.RemoteNextCommit is { Commit: var next }
            ? Source(next.Spec, next.Number, next.PerCommitmentPoint)
            : null;

    /// <summary>Our latest local commitment (as <c>LocalCommitmentBroadcastBuilder</c> builds it).</summary>
    private static CommitmentSource? LocalSource(ChannelModel channel)
    {
        if (channel.Commitments is { } commitments)
            return Source(commitments.LocalCommit.Spec, commitments.LocalCommit.Number, null);

        return new CommitmentSource(CommitmentTxSpec.FromChannel(channel), channel.LocalCommitmentNumber, null);
    }

    /// <summary>The peer's current commitment; without a snapshot only while the key set still holds its point.</summary>
    private static CommitmentSource? RemoteSource(ChannelModel channel)
    {
        if (channel.Commitments is { } commitments)
            return Source(commitments.RemoteCommit.Spec, commitments.RemoteCommit.Number,
                          commitments.RemoteCommit.PerCommitmentPoint);

        if (channel.RemoteKeySet is { } remoteKeySet
         && remoteKeySet.CurrentPerCommitmentIndex == PerCommitmentIndex.From(channel.RemoteCommitmentNumber))
            return new CommitmentSource(CommitmentTxSpec.FromChannel(channel), channel.RemoteCommitmentNumber,
                                        remoteKeySet.CurrentPerCommitmentCompactPoint);

        return null;
    }

    /// <summary>
    /// The outputs of the commitment on chain (§3.3), rebuilt on the funding it spends (splicing plan §3.6). A peer
    /// commitment that can't be mapped still yields our <c>to_remote</c> (static_remotekey: found by script whatever
    /// the number, B5-RMT-03).
    /// </summary>
    private async Task<(IReadOnlyList<CommitmentOutputDescriptor> Outputs, CompactPubKey? Point, string? Unmapped,
            CommitmentTxSpec? Spec)>
        MapOutputsAsync(IServiceScope scope, ChannelModel channel, FundingSpendClassification classification,
                        ChainTx spend, ChannelFunding funding, IReadOnlyList<ChannelFunding> fundings,
                        ICommitmentTransactionModelFactory? factory)
    {
        var number = classification.CommitmentNumber ?? 0;
        try
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var sources = await GetSourcesAsync(unitOfWork, channel, funding);

            // A commitment of a funding we can't rebuild on (a retired one after a restart) is mapped by script from
            // the state machine's spec: its HTLC and to_remote scripts do not depend on the funding
            var current = OnchainFundings.IsCurrent(channel, funding)
                              ? sources
                              : new FundingSources(sources.Local ?? LocalSource(channel),
                                                   sources.Remote ?? RemoteSource(channel),
                                                   sources.RemoteNext ?? RemoteNextSource(channel));
            CommitmentOutputMap? map = null;
            CommitmentTxSpec? balanceSpec = null;
            var predatesLog = false;
            switch (classification.Kind)
            {
                case FundingSpendKind.LocalCommit when current.Local is var (spec, _, _):
                    map = OnchainFundings.Map(_commitmentOutputMapper, factory, channel, funding, spec,
                                              CommitmentCase.Local, number, null, spend);
                    balanceSpec = spec;
                    break;
                case FundingSpendKind.RemoteCommit when current.Remote is var (spec, _, point):
                    map = OnchainFundings.Map(_commitmentOutputMapper, factory, channel, funding, spec,
                                              CommitmentCase.Remote, number, point, spend);
                    balanceSpec = spec;
                    break;
                case FundingSpendKind.RemoteNextCommit when current.RemoteNext is var (spec, _, point):
                    map = OnchainFundings.Map(_commitmentOutputMapper, factory, channel, funding, spec,
                                              CommitmentCase.Remote, number, point, spend);
                    balanceSpec = spec;
                    break;
                case FundingSpendKind.Revoked:
                    (map, predatesLog, balanceSpec) = await MapRevokedAsync(scope, channel, number, spend, funding,
                                                                            fundings, factory);
                    break;
            }

            var outputs = map?.Outputs.ToList() ?? [];
            if (classification.Kind is not (FundingSpendKind.LocalCommit or FundingSpendKind.Unknown)
             && outputs.All(o => o.Kind != OutputDescriptorKind.PaymentToRemote))
                outputs.AddRange(FindToRemote(channel, spend).Where(r => outputs.All(o => o.Vout != r.Vout)));

            var ordered = outputs.OrderBy(o => o.Vout).ToList();
            return (ordered, map?.PerCommitmentPoint, DescribeUnmapped(spend, map, ordered, predatesLog),
                    map is null ? null : balanceSpec);
        }
        catch (Exception e)
        {
            _logger.LogCritical(e, "Could not map the outputs of {Kind} {TxId} of channel {ChannelId}",
                                classification.Kind, Display(spend.TxId), channel.ChannelId);
            return classification.Kind is FundingSpendKind.LocalCommit or FundingSpendKind.Unknown
                       ? ([], null, null, null)
                       : (FindToRemote(channel, spend), null, null, null);
        }
    }

    /// <summary>
    /// The outputs of the commitment on chain that match nothing we expect (with their amounts), or null: an output
    /// we can't identify may be an HTLC (a revoked commitment older than the revocation log had HTLCs we no longer
    /// know), so the operator is told funds may be at risk (B5-GEN-06 style).
    /// </summary>
    private static string? DescribeUnmapped(ChainTx spend, CommitmentOutputMap? map,
                                            IReadOnlyList<CommitmentOutputDescriptor> outputs, bool predatesLog)
    {
        if (map is null || map.UnmappedVouts.Count == 0)
            return null;

        var unmapped = map.UnmappedVouts.Where(v => outputs.All(o => o.Vout != v)).ToList();
        if (unmapped.Count == 0)
            return null;

        var described = string.Join(", ", unmapped.Select(v => v < spend.Outputs.Count
                                                                   ? $"{v} ({spend.Outputs[(int)v].AmountSat} sat)"
                                                                   : v.ToString()));
        return $"{unmapped.Count} output(s) of {Display(spend.TxId)} match no expected output: {described}"
             + (predatesLog
                    ? "; the commitment predates the revocation log, so its HTLC outputs can't be rebuilt"
                    : string.Empty)
             + ": they are not resolved, funds may be lost";
    }

    private IReadOnlyList<CommitmentOutputDescriptor> FindToRemote(ChannelModel channel, ChainTx spend) =>
        _commitmentOutputMapper.FindPaymentToRemote(spend, channel.LocalKeySet.PaymentCompactBasepoint,
                                                    channel.ChannelParams.OptionAnchorOutputs);

    /// <summary>
    /// A revoked commitment: the point is <c>secret * G</c> with the peer's secret from our shachain; the spec comes
    /// from the revocation log of the funding it spends (NL-479, SP-I5; only commitments with HTLCs have an entry).
    /// Without an entry the outputs are mapped by script from a stand-in spec without HTLCs at that funding's
    /// capacity: <c>to_local</c> and <c>to_remote</c> scripts do not depend on the amounts. The spec is returned only
    /// when it came from the log (the stand-in's balances are not the commitment's).
    /// </summary>
    private async Task<(CommitmentOutputMap? Map, bool PredatesLog, CommitmentTxSpec? Spec)> MapRevokedAsync(
        IServiceScope scope, ChannelModel channel, ulong number, ChainTx spend, ChannelFunding funding,
        IReadOnlyList<ChannelFunding> fundings, ICommitmentTransactionModelFactory? modelFactory)
    {
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var factory = scope.ServiceProvider.GetService<ISecretStorageServiceFactory>();
        if (factory is null)
            return (null, false, null);

        CompactPubKey point;
        using (var shachain = factory.CreatePerCommitmentStorage())
        {
            shachain.Load(await unitOfWork.RemoteShachainDbRepository.GetByChannelIdAsync(channel.ChannelId));
            byte[] secret = shachain.DeriveOldSecret(PerCommitmentIndex.From(number));
            using var key = new Key(secret);
            point = key.PubKey.ToBytes();
        }

        var logged = await GetRevokedAsync(unitOfWork, channel.ChannelId, funding, fundings, number);
        var predatesLog = logged is null
                       && number < await unitOfWork.RevokedCommitmentDbRepository.GetLogStartAsync(channel.ChannelId);
        var fundingMsat = funding.CapacityMsat;
        var spec = logged is not null
                       ? CommitmentTxSpec.FromCommitmentSpec(logged.Spec)
                       : new CommitmentTxSpec(fundingMsat / 2, fundingMsat / 2,
                                              (ulong)channel.ChannelParams.FeeRateAmountPerKw.Satoshi);
        return (OnchainFundings.Map(_commitmentOutputMapper, modelFactory, channel, funding, spec,
                                    CommitmentCase.Revoked, number, point, spend), predatesLog,
                logged is not null ? spec : null);
    }

    /// <summary>
    /// The revocation log entry of commitment <paramref name="number"/> on <paramref name="funding"/> (NL-479). A log
    /// that keeps no per-funding rows (test doubles) is read by number when the channel has a single funding.
    /// </summary>
    internal static async Task<RevokedCommitmentModel?> GetRevokedAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                                      ChannelFunding funding,
                                                                      IReadOnlyList<ChannelFunding> fundings,
                                                                      ulong number)
    {
        var log = unitOfWork.RevokedCommitmentDbRepository;
        var logged = await log.GetAsync(channelId, funding.FundingTxId, number);
        if (logged is not null)
            return logged;

        // No entry on the spent funding (a restart dropped the engine's pending fundings and SignedOnFundings, so the
        // revocation was logged on the current funding only): the HTLC set and HTLC scripts are the same on every
        // funding, so another funding's entry, rebased on the spent funding's balances, maps the same HTLC outputs
        var other = await log.GetAsync(channelId, number);
        if (other is null || fundings.Count <= 1 || other.FundingTxId is not { } otherTxId
         || otherTxId == funding.FundingTxId)
            return other;

        return new RevokedCommitmentModel(channelId, number, Rebase(other.Spec, otherTxId, funding, fundings))
        {
            FundingTxId = funding.FundingTxId
        };
    }

    /// <summary>
    /// <paramref name="spec"/>, logged on the funding <paramref name="fromTxId"/>, with the main balances of
    /// <paramref name="to"/> (the deltas are relative to the current funding); the spec itself when the balances can't
    /// be derived (the HTLC outputs still map, and <c>to_local</c>/<c>to_remote</c> are matched by script).
    /// </summary>
    private static CommitmentSpec Rebase(CommitmentSpec spec, TxId fromTxId, ChannelFunding to,
                                         IReadOnlyList<ChannelFunding> fundings)
    {
        var from = fundings.FirstOrDefault(f => f.FundingTxId == fromTxId);
        var fromLocal = from?.LocalBalanceDeltaMsat ?? 0;
        var fromRemote = from?.RemoteBalanceDeltaMsat ?? 0;
        try
        {
            var local = checked((long)spec.LocalMsat - fromLocal + to.LocalBalanceDeltaMsat);
            var remote = checked((long)spec.RemoteMsat - fromRemote + to.RemoteBalanceDeltaMsat);
            return local < 0 || remote < 0
                       ? spec
                       : new CommitmentSpec(spec.Holder, spec.FeeratePerKw, (ulong)local, (ulong)remote, spec.Htlcs);
        }
        catch (OverflowException)
        {
            return spec;
        }
    }

    private async Task<Recorded> PersistAsync(IServiceScope scope, ChannelModel channel, OutpointSpentEventArgs args,
                                              ChainTx spend, FundingSpendClassification classification,
                                              ChannelCloseKind closeKind,
                                              IReadOnlyList<CommitmentOutputDescriptor> descriptors,
                                              CompactPubKey? point, string? unmapped, ChannelFunding spentFunding,
                                              IReadOnlyList<ChannelFunding> fundings, CommitmentTxSpec? spec)
    {
        var channelId = channel.ChannelId;
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var close = new ChannelCloseModel(channelId, closeKind, spend.TxId, classification.CommitmentNumber,
                                          args.BlockHeight, args.BlockHash ?? Hash.Empty, DateTimeOffset.UtcNow);
        await unitOfWork.OnchainResolutionDbRepository.UpsertCloseAsync(close);

        var ours = descriptors.Where(d => d.IsOurs).ToList();
        var broadcasts = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channelId);
        var revived = new List<BroadcastTransactionModel>();
        var prepared = await SettlePreparedAsync(unitOfWork, channelId, broadcasts, spend.TxId, revived);
        var newWatches = new List<WatchedOutpointModel>();
        foreach (var descriptor in ours)
        {
            var row = new OutputResolutionModel
            {
                TransactionId = spend.TxId,
                OutputIndex = descriptor.Vout,
                ChannelId = channelId,
                Descriptor = descriptor.Kind,
                DescriptorData = OutputDescriptorData.FromDescriptor(descriptor, point).Encode(),
                HtlcDirection = descriptor.Htlc?.Direction,
                HtlcId = descriptor.Htlc?.Id
            };

            // O8: already spent by a transaction prepared while the commitment was in the mempool (our penalty)
            if (prepared.TryGetValue(descriptor.Vout, out var resolving))
                row = row with
                {
                    State = OutputResolutionState.Broadcast,
                    ResolvingTransactionId = resolving,
                    DeadlineHeight = GetRevokedDeadline(args.BlockHeight, descriptor)
                };

            await unitOfWork.OnchainResolutionDbRepository.UpsertOutputAsync(row);

            if (await unitOfWork.WatchedOutpointDbRepository.GetAsync(spend.TxId, descriptor.Vout) is not null)
                continue;

            var watch = new WatchedOutpointModel(spend.TxId, descriptor.Vout, channelId,
                                                 WatchedOutpointPurpose.ResolutionOutput);
            unitOfWork.WatchedOutpointDbRepository.Add(watch);
            newWatches.Add(watch);
        }

        // The peer's transaction (or another of ours) won the funding output: our commitment can never confirm
        foreach (var stale in broadcasts.Where(b => b.Purpose == BroadcastPurpose.LocalCommitment
                                                 && b.State == BroadcastState.Pending
                                                 && b.TransactionId != spend.TxId))
            await unitOfWork.BroadcastTransactionDbRepository.MarkAbandonedAsync(stale.TransactionId);

        await DiscardConflictingSplicesAsync(unitOfWork, channel, spend, spentFunding, fundings, broadcasts);

        // B5-GEN-04: the peer gets an error unless the channel already failed with one (re-sent on reconnection)
        ErrorMessage? errorToSend = null;
        byte[]? errorBytes = null;
        if (channel.ErrorSent is null)
        {
            var messageFactory = scope.ServiceProvider.GetRequiredService<IMessageFactory>();
            var messageSerializer = scope.ServiceProvider.GetRequiredService<IMessageSerializer>();
            errorToSend = messageFactory.CreateErrorMessage(OnchainErrorMessage, channelId);
            using var errorStream = new MemoryStream();
            await messageSerializer.SerializeAsync(errorToSend, errorStream);
            errorBytes = errorStream.ToArray();
        }

        // The stored error and OnchainResolving are staged on the row's copy read from the database: the shared
        // model changes only after the save (as the executor stages Closed), so a failed save leaves the memory
        // where it was and the replayed block records the close (NL-307)
        var previousState = channel.State;
        var stored = await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId)
                  ?? throw new InvalidOperationException(
                         $"Channel {channelId} closed on chain but is not in the database");
        if (errorBytes is not null)
            stored.MarkErrorSent(errorBytes);
        if (stored.State < ChannelState.OnchainResolving)
            stored.UpdateState(ChannelState.OnchainResolving);
        await unitOfWork.ChannelDbRepository.UpdateAsync(stored);

        // NL-602: our channel balance moves to pending on-chain funds in the save that records the close
        await StageForceClosedEventAsync(unitOfWork, channel, closeKind, spend, classification.CommitmentNumber,
                                         args.BlockHeight, descriptors, spec, spentFunding);
        await unitOfWork.SaveChangesAsync();

        if (errorBytes is not null)
            channel.MarkErrorSent(errorBytes);
        if (channel.State < ChannelState.OnchainResolving)
            channel.UpdateState(ChannelState.OnchainResolving);
        _channelMemoryRepository.UpdateChannel(channel);

        _logger.LogCritical("Channel {ChannelId} ({State}) closed on chain by {Kind} {TxId} (commitment {Number}) at "
                          + "height {Height}; {Count} output(s) to resolve", channelId, Enum.GetName(previousState),
                            closeKind, Display(spend.TxId), classification.CommitmentNumber, args.BlockHeight,
                            ours.Count);

        var alerts = new List<AlertAction>();
        switch (closeKind)
        {
            case ChannelCloseKind.Unknown:
                alerts.Add(new AlertAction("B5-GEN-06",
                                           $"the funding output was spent by {Display(spend.TxId)}, which is no "
                                         + $"known commitment or close ({classification.Reason}): funds may be lost"));
                break;
            case ChannelCloseKind.FutureCommitment:
                alerts.Add(new AlertAction("B5-RMT-03",
                                           $"the peer broadcast commitment {classification.CommitmentNumber}, newer "
                                         + "than any we know (we lost data): only to_remote can be swept"));
                break;
        }

        if (unmapped is not null)
            alerts.Add(new AlertAction("B5-GEN-06", unmapped));

        return new Recorded(new FundingSpendOutcome(closeKind, ours.Count, false), newWatches, errorToSend,
                            channel.RemoteNodeId, alerts, revived);
    }

    /// <summary>
    /// A commitment spent the funding output: every pending splice of that funding can no longer confirm (splicing
    /// plan §3.6: it double-spends the same output). Its funding is <see cref="ChannelFundingStatus.Discarded"/> and its
    /// pending broadcast abandoned, in the close's save; the revocation data of the discarded funding is kept (SP-I5).
    /// </summary>
    private async Task DiscardConflictingSplicesAsync(IUnitOfWork unitOfWork, ChannelModel channel, ChainTx spend,
                                                      ChannelFunding spentFunding,
                                                      IReadOnlyList<ChannelFunding> fundings,
                                                      IReadOnlyList<BroadcastTransactionModel> broadcasts)
    {
        foreach (var conflicting in broadcasts.Where(b => b.State == BroadcastState.Pending
                                                       && b.Purpose is BroadcastPurpose.Splice
                                                                        or BroadcastPurpose.Funding
                                                       && b.TransactionId != spend.TxId
                                                       && SpendsOutpoint(b, spentFunding)))
        {
            _logger.LogWarning("Channel {ChannelId}: splice {TxId} can no longer confirm ({Commitment} spent its "
                             + "input); abandoning it", channel.ChannelId, Display(conflicting.TransactionId),
                               Display(spend.TxId));
            await unitOfWork.BroadcastTransactionDbRepository.MarkAbandonedAsync(conflicting.TransactionId);
        }

        var pending = fundings.Where(f => f.Status == ChannelFundingStatus.Pending
                                       && f.FundingTxId != spentFunding.FundingTxId)
                              .ToList();
        if (pending.Count == 0 || !OnchainFundings.IsCurrent(channel, spentFunding))
            return;

        try
        {
            if (unitOfWork.ChannelFundingDbRepository is not { } repository)
                return;

            foreach (var funding in pending)
                await repository.UpsertAsync(channel.ChannelId, funding with { Status = ChannelFundingStatus.Discarded });
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            // No funding rows to update
        }
    }

    private static bool SpendsOutpoint(BroadcastTransactionModel broadcast, ChannelFunding funding) =>
        ChainTxMapper.TryParse(broadcast.RawTransaction, out var transaction) && transaction is not null
     && transaction.IndexOfInputSpending(funding.FundingTxId, funding.OutputIndex) >= 0;

    /// <summary>
    /// O8: the transactions prepared from the mempool before this funding spend confirmed. The ones that spend outputs
    /// of <paramref name="spend"/> are returned by the vout they spend (the rows name them), also when they already
    /// confirmed (a penalty mined in the same block as the commitment: the monitor marks it confirmed before this
    /// runs); pending ones that spend another transaction (a revoked commitment that was replaced or evicted) are
    /// abandoned in this save.
    /// </summary>
    private async Task<Dictionary<uint, TxId>> SettlePreparedAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                                   IReadOnlyList<BroadcastTransactionModel> broadcasts,
                                                                   TxId spend,
                                                                   List<BroadcastTransactionModel> revived)
    {
        var spentBy = new Dictionary<uint, TxId>();

        // The abandoned ones last: one is revived only for outputs no live transaction spends
        foreach (var broadcast in broadcasts.Where(b => b.State is BroadcastState.Pending or BroadcastState.Confirmed
                                                                    or BroadcastState.Abandoned
                                                     && IsPreparedPurpose(b.Purpose))
                                            .OrderBy(b => b.State == BroadcastState.Abandoned))
        {
            if (!ChainTxMapper.TryParse(broadcast.RawTransaction, out var transaction) || transaction is null)
                continue;

            var fromSpend = transaction.Inputs.Where(i => i.PreviousTxId == spend).ToList();
            if (broadcast.State == BroadcastState.Abandoned)
            {
                // Abandoned when this commitment left the mempool (or while the monitor lagged), and it confirmed
                // after all: pending again, or the resolver's penalty would find it abandoned and never send it (the
                // same txid: lock time 0, the channel's one penalty address (RevokedCommitDataSource), RFC 6979
                // signatures, often the same fee)
                if (fromSpend.Count == 0 || fromSpend.Any(i => spentBy.ContainsKey(i.PreviousVout))
                 || !await unitOfWork.BroadcastTransactionDbRepository.MarkPendingAsync(broadcast.TransactionId))
                    continue;

                broadcast.MarkPending();
                revived.Add(broadcast);
                _logger.LogWarning("Channel {ChannelId}: {Purpose} {TxId}, abandoned before the revoked commitment "
                                 + "{Spend} confirmed, is pending again", channelId, broadcast.Purpose,
                                   Display(broadcast.TransactionId), Display(spend));
            }

            if (fromSpend.Count == 0)
            {
                if (broadcast.State != BroadcastState.Pending)
                    continue;

                _logger.LogWarning("Channel {ChannelId}: {Purpose} {TxId}, prepared from the mempool, spends a "
                                 + "transaction that did not confirm; abandoning it", channelId, broadcast.Purpose,
                                   Display(broadcast.TransactionId));
                await unitOfWork.BroadcastTransactionDbRepository.MarkAbandonedAsync(broadcast.TransactionId);
                continue;
            }

            foreach (var input in fromSpend)
                spentBy[input.PreviousVout] = broadcast.TransactionId;
            _logger.LogInformation("Channel {ChannelId}: {Purpose} {TxId}, prepared from the mempool, resolves "
                                 + "{Count} output(s) of the confirmed {Spend}", channelId, broadcast.Purpose,
                                   Display(broadcast.TransactionId), fromSpend.Count, Display(spend));
        }

        return spentBy;
    }

    /// <summary>
    /// The deadline the penalty resolver gives a revoked output (its <c>to_local</c> once its CSV expires, an HTLC
    /// output at its <c>cltv_expiry</c>); null for any other output.
    /// </summary>
    private static uint? GetRevokedDeadline(uint confirmedAt, CommitmentOutputDescriptor descriptor) =>
        descriptor.Kind switch
        {
            OutputDescriptorKind.RevokedToLocal => confirmedAt + descriptor.CsvDelay,
            OutputDescriptorKind.RevokedHtlc => descriptor.Htlc?.CltvExpiry,
            _ => null
        };

    private static ChannelCloseKind ToCloseKind(FundingSpendKind kind) => kind switch
    {
        FundingSpendKind.LocalCommit => ChannelCloseKind.LocalCommitment,
        FundingSpendKind.RemoteCommit => ChannelCloseKind.RemoteCommitment,
        FundingSpendKind.RemoteNextCommit => ChannelCloseKind.RemoteNextCommitment,
        FundingSpendKind.Revoked => ChannelCloseKind.RevokedCommitment,
        FundingSpendKind.FutureRemote => ChannelCloseKind.FutureCommitment,
        FundingSpendKind.Mutual => ChannelCloseKind.Mutual,
        _ => ChannelCloseKind.Unknown
    };

    /// <summary>A txid in the display (RPC, block explorer) byte order, for logs (NL-275).</summary>
    private static string Display(TxId txId) => new uint256(txId).ToString();

    /// <summary>A commitment to rebuild: its content, number and (peer commitments) per-commitment point.</summary>
    private readonly record struct CommitmentSource(CommitmentTxSpec Spec, ulong Number, CompactPubKey? Point);

    /// <summary>The commitments of one funding that can be rebuilt.</summary>
    private sealed record FundingSources(CommitmentSource? Local, CommitmentSource? Remote,
                                         CommitmentSource? RemoteNext);

    private sealed record Recorded(
        FundingSpendOutcome Outcome,
        IReadOnlyList<WatchedOutpointModel> NewWatches,
        ErrorMessage? ErrorToSend,
        CompactPubKey Peer,
        IReadOnlyList<AlertAction> Alerts,
        IReadOnlyList<BroadcastTransactionModel> Revived);
}