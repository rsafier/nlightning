using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain;

using Channels.Safety.Interfaces;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Classifiers;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
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
/// and every peer message (the stored error is sent again). An unknown spend does too, with no output: nothing can be
/// swept, and the channel closes after the irrevocable depth. A mutual close is left to the channel manager (it only
/// arrives for a channel in its close negotiation).</para>
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

    public OnchainChannelWatcher(IChannelErrorSender channelErrorSender, IChannelLockProvider channelLockProvider,
                                 IChannelMemoryRepository channelMemoryRepository,
                                 ICommitmentOutputMapper commitmentOutputMapper, IOnchainResolutionExecutor executor,
                                 ILogger<OnchainChannelWatcher> logger, IOutpointWatcher outpointWatcher,
                                 IServiceScopeFactory serviceScopeFactory)
    {
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

        Recorded recorded;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            {
                _logger.LogDebug("Funding spend {TxId} of channel {ChannelId}: the channel is not loaded",
                                 Display(spend.TxId), channelId);
                return null;
            }

            if (channel.FundingOutput is not { TransactionId: { } fundingTxId, Index: { } fundingIndex }
             || args.SpentTransactionId is { } spentTxId
             && (spentTxId != fundingTxId || args.SpentOutputIndex != fundingIndex))
                return null;

            if (channel.State is ChannelState.Closed or ChannelState.Stale)
                return null;

            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
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

            if (existing is not null)
                _logger.LogCritical("The funding output of channel {ChannelId} is now spent by {TxId}, not by the "
                                  + "recorded {Recorded} (reorg): recording the new spend", channelId,
                                    Display(spend.TxId), Display(existing.CommitmentTransactionId));

            var context = await BuildContextAsync(channel, fundingTxId, fundingIndex, unitOfWork);
            var classification = FundingSpendClassifier.Classify(spend, context);
            switch (classification.Kind)
            {
                case FundingSpendKind.NotFundingSpend:
                    return null;
                case FundingSpendKind.Mutual:
                    _logger.LogInformation("The funding output of channel {ChannelId} was spent by mutual close "
                                         + "{TxId}; the close path records it", channelId, Display(spend.TxId));
                    return null;
            }

            var closeKind = ToCloseKind(classification.Kind);
            var (descriptors, point, unmapped) = await MapOutputsAsync(scope, channel, classification, spend);
            if (existing is not null)
                await RetireReplacedCloseAsync(unitOfWork, channelId, existing, spend.TxId);
            recorded = await PersistAsync(scope, channel, args, spend, classification, closeKind, descriptors, point,
                                          unmapped);
        }

        foreach (var watch in recorded.NewWatches)
            _outpointWatcher.TrackWatchedOutpoint(watch);

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
        // final); nothing re-checks it against the new close's HTLC outputs, so the operator is told
        var resolvedHtlcs = rows.Where(r => r.HtlcId is not null && r.TransactionId != newSpend
                                         && r.State is OutputResolutionState.Resolved
                                                    or OutputResolutionState.Irrevocable)
                                .Select(r => $"{r.HtlcDirection} {r.HtlcId}")
                                .ToList();
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

        _logger.LogWarning("Channel {ChannelId}: the {Count} output(s) of the reorged-out close {TxId} are ignored and "
                         + "its pending transactions abandoned", channelId, rows.Count,
                           Display(old.CommitmentTransactionId));
    }

    /// <summary>
    /// Classifies an unconfirmed spend of the channel's funding output (BOLT 5 plan O8) exactly as a confirmed one is
    /// classified, without recording anything. Call it under the channel's lock; null when the channel has no funding
    /// output.
    /// </summary>
    internal async Task<FundingSpendClassification?> ClassifyAsync(ChannelModel channel, ChainTx spend,
                                                                   IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(spend);
        if (channel.FundingOutput is not { TransactionId: { } fundingTxId, Index: { } fundingIndex })
            return null;

        var context = await BuildContextAsync(channel, fundingTxId, fundingIndex, unitOfWork);
        return FundingSpendClassifier.Classify(spend, context);
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

    /// <summary>What the classifier compares the spend with (candidates rebuilt from the persisted state).</summary>
    private async Task<FundingSpendContext> BuildContextAsync(ChannelModel channel, TxId fundingTxId,
                                                              uint fundingIndex, IUnitOfWork unitOfWork)
    {
        CommitmentCandidate? local = null, remote = null, remoteNext = null;

        // Our commitment: the one the failure service signed (its broadcast row), else the rebuilt latest one
        try
        {
            var broadcasts = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channel.ChannelId);
            var signed = broadcasts.LastOrDefault(b => b is
            {
                Purpose: BroadcastPurpose.LocalCommitment, CommitmentNumber: not null
            });
            if (signed is not null)
                local = new CommitmentCandidate(signed.CommitmentNumber!.Value, signed.TransactionId);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not read the commitment broadcasts of channel {ChannelId}", channel.ChannelId);
        }

        local ??= TryCandidate(channel, CommitmentCase.Local, LocalSource(channel));
        remote = TryCandidate(channel, CommitmentCase.Remote, RemoteSource(channel));
        if (channel.Commitments?.RemoteNextCommit is { Commit: var next })
            remoteNext = TryCandidate(channel, CommitmentCase.Remote,
                                      (CommitmentTxSpec.FromCommitmentSpec(next.Spec), next.Number,
                                       next.PerCommitmentPoint));

        var closingTxIds = channel.ClosingTransaction is { } closing ? new[] { closing.TxId } : null;
        return new FundingSpendContext(fundingTxId, fundingIndex,
                                       channel.CommitmentNumber
                                    ?? throw new InvalidOperationException(
                                           $"Channel {channel.ChannelId} has no commitment number obscurer"),
                                       local, remote, remoteNext, closingTxIds,
                                       channel.LocalShutdownScript is { } localScript ? (byte[])localScript : null,
                                       channel.RemoteShutdownScript is { } remoteScript
                                           ? (byte[])remoteScript
                                           : null);
    }

    private CommitmentCandidate? TryCandidate(ChannelModel channel, CommitmentCase commitmentCase,
                                              (CommitmentTxSpec Spec, ulong Number, CompactPubKey? Point)? source)
    {
        if (source is not { } s)
            return null;

        try
        {
            var map = _commitmentOutputMapper.Map(channel, s.Spec, commitmentCase, s.Number, s.Point);
            return new CommitmentCandidate(s.Number, map.ExpectedTxId);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not rebuild {Case} commitment {Number} of channel {ChannelId}",
                               commitmentCase, s.Number, channel.ChannelId);
            return null;
        }
    }

    /// <summary>Our latest local commitment (as <c>LocalCommitmentBroadcastBuilder</c> builds it).</summary>
    private static (CommitmentTxSpec, ulong, CompactPubKey?)? LocalSource(ChannelModel channel)
    {
        if (channel.Commitments is { } commitments)
            return (CommitmentTxSpec.FromCommitmentSpec(commitments.LocalCommit.Spec), commitments.LocalCommit.Number,
                    null);

        return (CommitmentTxSpec.FromChannel(channel), channel.LocalCommitmentNumber, null);
    }

    /// <summary>The peer's current commitment; without a snapshot only while the key set still holds its point.</summary>
    private static (CommitmentTxSpec, ulong, CompactPubKey?)? RemoteSource(ChannelModel channel)
    {
        if (channel.Commitments is { } commitments)
            return (CommitmentTxSpec.FromCommitmentSpec(commitments.RemoteCommit.Spec),
                    commitments.RemoteCommit.Number, commitments.RemoteCommit.PerCommitmentPoint);

        if (channel.RemoteKeySet is { } remoteKeySet
         && remoteKeySet.CurrentPerCommitmentIndex == PerCommitmentIndex.From(channel.RemoteCommitmentNumber))
            return (CommitmentTxSpec.FromChannel(channel), channel.RemoteCommitmentNumber,
                    remoteKeySet.CurrentPerCommitmentCompactPoint);

        return null;
    }

    /// <summary>
    /// The outputs of the commitment on chain (§3.3). A peer commitment that can't be mapped still yields our
    /// <c>to_remote</c> (static_remotekey: found by script whatever the number, B5-RMT-03).
    /// </summary>
    private async Task<(IReadOnlyList<CommitmentOutputDescriptor> Outputs, CompactPubKey? Point, string? Unmapped)>
        MapOutputsAsync(IServiceScope scope, ChannelModel channel, FundingSpendClassification classification,
                        ChainTx spend)
    {
        var number = classification.CommitmentNumber ?? 0;
        try
        {
            CommitmentOutputMap? map = null;
            var predatesLog = false;
            switch (classification.Kind)
            {
                case FundingSpendKind.LocalCommit when LocalSource(channel) is var (spec, _, _):
                    map = _commitmentOutputMapper.Map(channel, spec, CommitmentCase.Local, number, null, spend);
                    break;
                case FundingSpendKind.RemoteCommit when RemoteSource(channel) is var (spec, _, point):
                    map = _commitmentOutputMapper.Map(channel, spec, CommitmentCase.Remote, number, point, spend);
                    break;
                case FundingSpendKind.RemoteNextCommit
                    when channel.Commitments?.RemoteNextCommit is { Commit: var next }:
                    map = _commitmentOutputMapper.Map(channel, CommitmentTxSpec.FromCommitmentSpec(next.Spec),
                                                      CommitmentCase.Remote, number, next.PerCommitmentPoint, spend);
                    break;
                case FundingSpendKind.Revoked:
                    (map, predatesLog) = await MapRevokedAsync(scope, channel, number, spend);
                    break;
            }

            var outputs = map?.Outputs.ToList() ?? [];
            if (classification.Kind is not (FundingSpendKind.LocalCommit or FundingSpendKind.Unknown)
             && outputs.All(o => o.Kind != OutputDescriptorKind.PaymentToRemote))
                outputs.AddRange(FindToRemote(channel, spend).Where(r => outputs.All(o => o.Vout != r.Vout)));

            var ordered = outputs.OrderBy(o => o.Vout).ToList();
            return (ordered, map?.PerCommitmentPoint, DescribeUnmapped(spend, map, ordered, predatesLog));
        }
        catch (Exception e)
        {
            _logger.LogCritical(e, "Could not map the outputs of {Kind} {TxId} of channel {ChannelId}",
                                classification.Kind, Display(spend.TxId), channel.ChannelId);
            return classification.Kind is FundingSpendKind.LocalCommit or FundingSpendKind.Unknown
                       ? ([], null, null)
                       : (FindToRemote(channel, spend), null, null);
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
    /// from the revocation log (only commitments with HTLCs have an entry). Without an entry the outputs are mapped by
    /// script from a stand-in spec without HTLCs: <c>to_local</c> and <c>to_remote</c> scripts do not depend on the
    /// amounts.
    /// </summary>
    private async Task<(CommitmentOutputMap? Map, bool PredatesLog)> MapRevokedAsync(
        IServiceScope scope, ChannelModel channel, ulong number, ChainTx spend)
    {
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var factory = scope.ServiceProvider.GetService<ISecretStorageServiceFactory>();
        if (factory is null)
            return (null, false);

        CompactPubKey point;
        using (var shachain = factory.CreatePerCommitmentStorage())
        {
            shachain.Load(await unitOfWork.RemoteShachainDbRepository.GetByChannelIdAsync(channel.ChannelId));
            byte[] secret = shachain.DeriveOldSecret(PerCommitmentIndex.From(number));
            using var key = new Key(secret);
            point = key.PubKey.ToBytes();
        }

        var logged = await unitOfWork.RevokedCommitmentDbRepository.GetAsync(channel.ChannelId, number);
        var predatesLog = logged is null
                       && number < await unitOfWork.RevokedCommitmentDbRepository.GetLogStartAsync(channel.ChannelId);
        var fundingMsat = (ulong)channel.FundingOutput!.Amount.Satoshi * 1_000;
        var spec = logged is not null
                       ? CommitmentTxSpec.FromCommitmentSpec(logged.Spec)
                       : new CommitmentTxSpec(fundingMsat / 2, fundingMsat / 2,
                                              (ulong)channel.ChannelParams.FeeRateAmountPerKw.Satoshi);
        return (_commitmentOutputMapper.Map(channel, spec, CommitmentCase.Revoked, number, point, spend), predatesLog);
    }

    private async Task<Recorded> PersistAsync(IServiceScope scope, ChannelModel channel, OutpointSpentEventArgs args,
                                              ChainTx spend, FundingSpendClassification classification,
                                              ChannelCloseKind closeKind,
                                              IReadOnlyList<CommitmentOutputDescriptor> descriptors,
                                              CompactPubKey? point, string? unmapped)
    {
        var channelId = channel.ChannelId;
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var close = new ChannelCloseModel(channelId, closeKind, spend.TxId, classification.CommitmentNumber,
                                          args.BlockHeight, args.BlockHash ?? Hash.Empty, DateTimeOffset.UtcNow);
        await unitOfWork.OnchainResolutionDbRepository.UpsertCloseAsync(close);

        var ours = descriptors.Where(d => d.IsOurs).ToList();
        var broadcasts = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channelId);
        var prepared = await SettlePreparedAsync(unitOfWork, channelId, broadcasts, spend.TxId);
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

        // B5-GEN-04: the peer gets an error unless the channel already failed with one (re-sent on reconnection)
        ErrorMessage? errorToSend = null;
        if (channel.ErrorSent is null)
        {
            var messageFactory = scope.ServiceProvider.GetRequiredService<IMessageFactory>();
            var messageSerializer = scope.ServiceProvider.GetRequiredService<IMessageSerializer>();
            errorToSend = messageFactory.CreateErrorMessage(OnchainErrorMessage, channelId);
            using var errorStream = new MemoryStream();
            await messageSerializer.SerializeAsync(errorToSend, errorStream);
            channel.MarkErrorSent(errorStream.ToArray());
        }

        var previousState = channel.State;
        if (channel.State < ChannelState.OnchainResolving)
            channel.UpdateState(ChannelState.OnchainResolving);
        await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        await unitOfWork.SaveChangesAsync();
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
                            channel.RemoteNodeId, alerts);
    }

    /// <summary>
    /// O8: the transactions prepared from the mempool before this funding spend confirmed. The ones that spend outputs
    /// of <paramref name="spend"/> are returned by the vout they spend (the rows name them), also when they already
    /// confirmed (a penalty mined in the same block as the commitment: the monitor marks it confirmed before this
    /// runs); pending ones that spend another transaction (a revoked commitment that was replaced or evicted) are
    /// abandoned in this save.
    /// </summary>
    private async Task<Dictionary<uint, TxId>> SettlePreparedAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                                   IReadOnlyList<BroadcastTransactionModel> broadcasts,
                                                                   TxId spend)
    {
        var spentBy = new Dictionary<uint, TxId>();
        foreach (var broadcast in broadcasts.Where(b => b.State is (BroadcastState.Pending or BroadcastState.Confirmed)
                                                     && IsPreparedPurpose(b.Purpose)))
        {
            if (!ChainTxMapper.TryParse(broadcast.RawTransaction, out var transaction) || transaction is null)
                continue;

            var fromSpend = transaction.Inputs.Where(i => i.PreviousTxId == spend).ToList();
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

    private sealed record Recorded(
        FundingSpendOutcome Outcome,
        IReadOnlyList<WatchedOutpointModel> NewWatches,
        ErrorMessage? ErrorToSend,
        CompactPubKey Peer,
        IReadOnlyList<AlertAction> Alerts);
}