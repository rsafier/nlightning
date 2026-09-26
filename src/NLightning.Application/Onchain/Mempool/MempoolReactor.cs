using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Onchain.Mempool;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Onchain.Parsers;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Resolvers;

/// <summary>
/// BOLT 5 plan O8 (NL-098): the node's reaction to transactions bitcoind accepted into its mempool that spend a watched
/// output of a channel (<see cref="IBlockchainMonitor.OnWatchedOutpointSpentInMempool"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Preimages</b> (B5-GEN-07: "lower latency for preimage extraction"): every witness item of the transaction
/// that is the preimage of one of our offered HTLCs of the channel (checked against the payment hash) is staged into
/// the HTLC's record (<c>KnownPreimage</c>, one save under the channel's lock, as the resolvers do for a confirmed
/// claim) and <see cref="OutgoingHtlcFulfilled"/> goes to the switch at once, which fulfills the upstream HTLC (or
/// completes our payment). Safe on an unconfirmed transaction: a preimage stays valid whatever happens to it, and the
/// confirmed rounds only find it known already.</item>
/// <item><b>Revoked commitments</b>: a spend of the funding output is classified as a confirmed one is
/// (<see cref="OnchainChannelWatcher.ClassifyAsync"/>); for a revoked commitment the penalties are signed now
/// (<see cref="RevokedCommitResolver.PrepareUnconfirmedPenaltiesAsync"/>), stored as pending broadcasts (the monitor
/// resends them every block) and published right behind it, so they can confirm in the same block. Nothing else is
/// recorded: the close, its rows and the channel's state wait for the confirmation, when the watcher links the rows
/// to these penalties or abandons them (another transaction confirmed). A penalty whose commitment bitcoind no longer
/// knows for <see cref="OnchainMempoolOptions.EvictionGraceBlocks"/> blocks in a row (evicted or replaced) is
/// abandoned. Any other commitment kind is only logged.</item>
/// </list>
/// Work runs one item at a time on a background loop (the monitor's handlers only enqueue), and every step is
/// idempotent: the monitor reports a transaction once, but a restart, a replacement or a new block may bring it back.
/// </remarks>
public sealed class MempoolReactor : IMempoolReactor, IDisposable
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IBitcoinChainService? _chainService;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<MempoolReactor> _logger;
    private readonly OnchainMempoolOptions _options;
    private readonly RevokedCommitResolver? _revokedCommitResolver;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly OnchainChannelWatcher _watcher;
    private readonly ConcurrentDictionary<TxId, PreparedPenalty> _prepared = new();
    private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _inFlight;

    public MempoolReactor(IBlockchainMonitor blockchainMonitor, IChannelLockProvider channelLockProvider,
                          IChannelMemoryRepository channelMemoryRepository, ILogger<MempoolReactor> logger,
                          IOptions<OnchainOptions> options, IServiceScopeFactory serviceScopeFactory,
                          OnchainChannelWatcher watcher, RevokedCommitResolver? revokedCommitResolver = null,
                          IBitcoinChainService? chainService = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _options = options.Value.Mempool;
        _serviceScopeFactory = serviceScopeFactory;
        _watcher = watcher;
        _revokedCommitResolver = revokedCommitResolver;
        _chainService = chainService;
    }

    /// <summary>The revoked commitments (by txid) whose penalties were prepared from the mempool and still wait.</summary>
    internal IReadOnlyCollection<TxId> PreparedCommitments => _prepared.Keys.ToList();

    /// <inheritdoc />
    public void Start()
    {
        if (!_options.Enabled || _cts is not null)
            return;

        _cts = new CancellationTokenSource();
        _blockchainMonitor.OnWatchedOutpointSpentInMempool += OnMempoolSpend;
        _blockchainMonitor.OnNewBlockDetected += OnNewBlock;
        Enqueue(new WorkItem(null, null, true));
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        _blockchainMonitor.OnWatchedOutpointSpentInMempool -= OnMempoolSpend;
        _blockchainMonitor.OnNewBlockDetected -= OnNewBlock;
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Stopping
            }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <summary>Waits until every queued item was handled (tests).</summary>
    internal async Task WhenIdleAsync()
    {
        while (Volatile.Read(ref _inFlight) > 0)
            await Task.Delay(10);
    }

    public void Dispose() => _cts?.Dispose();

    /// <summary>
    /// One unconfirmed spend: preimages first (the most urgent: the upstream HTLC may be about to expire), then, for a
    /// spend of the funding output, the revoked-commitment check.
    /// </summary>
    internal async Task<MempoolReaction> HandleSpendAsync(MempoolSpendEventArgs args,
                                                          CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!ChainTxMapper.TryParse(args.SpendingTransaction.RawTxBytes, out var spend) || spend is null)
        {
            _logger.LogWarning("Unconfirmed spend {TxId} of channel {ChannelId} cannot be parsed",
                               Display(args.SpendingTransaction.TxId), args.ChannelId);
            return MempoolReaction.None;
        }

        if (!_channelMemoryRepository.TryGetChannel(args.ChannelId, out var channel))
            return MempoolReaction.None;

        var fulfilled = await FulfillFromPreimagesAsync(args.ChannelId, spend, cancellationToken);

        IReadOnlyList<TxId> penalties = [];
        FundingSpendKind? kind = null;
        if (!args.SpendsUnconfirmedParent
         && channel.FundingOutput is { TransactionId: { } fundingTxId, Index: { } fundingIndex }
         && args.SpentTransactionId == fundingTxId && args.SpentOutputIndex == fundingIndex)
            (kind, penalties) = await HandleFundingSpendAsync(args.ChannelId, spend, cancellationToken);

        return new MempoolReaction(fulfilled, kind, penalties);
    }

    /// <summary>
    /// Every block: forgets the prepared penalties whose close the watcher recorded (it linked or abandoned them), and
    /// abandons those whose commitment bitcoind has not known for <see cref="OnchainMempoolOptions.EvictionGraceBlocks"/>
    /// blocks in a row.
    /// </summary>
    internal async Task CheckPreparedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (commitmentTxId, prepared) in _prepared.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await HasCloseAsync(prepared.ChannelId))
            {
                _prepared.TryRemove(commitmentTxId, out _);
                continue;
            }

            if (await IsKnownToBitcoindAsync(commitmentTxId))
            {
                prepared.MissingBlocks = 0;
                continue;
            }

            // Without txindex bitcoind cannot serve a confirmed transaction that left its mempool: while the monitor
            // is behind its tip (catch-up) or halted, the commitment may be in a block not processed yet, so that
            // block does not count as missing
            if (!await IsMonitorAtTipAsync())
                continue;

            if (++prepared.MissingBlocks < Math.Max(1, _options.EvictionGraceBlocks))
                continue;

            await AbandonAsync(prepared, cancellationToken);
            _prepared.TryRemove(commitmentTxId, out _);
        }
    }

    /// <summary>
    /// After a restart: every pending penalty (or sweep) of a channel without a recorded close was prepared from the
    /// mempool; it is followed again under the transaction it spends.
    /// </summary>
    internal async Task RestorePreparedAsync()
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var broadcast in await unitOfWork.BroadcastTransactionDbRepository.GetPendingAsync())
        {
            if (broadcast.ChannelId is not { } channelId
             || !OnchainChannelWatcher.IsPreparedPurpose(broadcast.Purpose)
             || await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channelId) is not null
             || !ChainTxMapper.TryParse(broadcast.RawTransaction, out var transaction) || transaction is null
             || transaction.Inputs.Count == 0)
                continue;

            var parent = transaction.Inputs[0].PreviousTxId;
            var prepared = _prepared.GetOrAdd(parent, _ => new PreparedPenalty(channelId, []));
            lock (prepared.TransactionIds)
            {
                if (!prepared.TransactionIds.Contains(broadcast.TransactionId))
                    prepared.TransactionIds.Add(broadcast.TransactionId);
            }
        }

        if (!_prepared.IsEmpty && _logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Following {Count} revoked commitment(s) whose penalties were prepared from the "
                                 + "mempool before the restart", _prepared.Count);
    }

    private void OnMempoolSpend(object? sender, MempoolSpendEventArgs args) => Enqueue(new WorkItem(args, null, false));

    private void OnNewBlock(object? sender, NewBlockEventArgs args) => Enqueue(new WorkItem(null, args.Height, false));

    private void Enqueue(WorkItem item)
    {
        Interlocked.Increment(ref _inFlight);
        if (!_work.Writer.TryWrite(item))
            Interlocked.Decrement(ref _inFlight);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var item in _work.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                if (item.Restore)
                    await RestorePreparedAsync();
                else if (item.Spend is { } spend)
                    await HandleSpendAsync(spend, cancellationToken);
                else if (item.BlockHeight is not null && !_prepared.IsEmpty)
                    await CheckPreparedAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "The mempool reaction failed; the confirmed path still resolves the channel");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>
    /// Stages the preimages the transaction reveals into our offered HTLCs of the channel and raises the fulfills (see
    /// the class remarks). Returns the HTLC ids fulfilled this way.
    /// </summary>
    private async Task<IReadOnlyList<ulong>> FulfillFromPreimagesAsync(ChannelId channelId, ChainTx spend,
                                                                      CancellationToken cancellationToken)
    {
        var candidates = spend.Inputs.Select(i => HtlcWitnessParser.Parse(i.Witness).Preimage)
                              .OfType<Secret>()
                              .Distinct()
                              .ToList();
        if (candidates.Count == 0)
            return [];

        using var scope = _serviceScopeFactory.CreateScope();
        var fulfills = new List<OutgoingHtlcFulfilled>();
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
             || channel.Commitments is not { } commitments)
                return [];

            var updated = new List<HtlcRecord>();
            foreach (var record in commitments.Htlcs.Values.Where(h => h.Direction == HtlcDirection.Outgoing))
            {
                var preimage = candidates.FirstOrDefault(c => Hashes(c, record.PaymentHash));
                if (preimage == default(Secret) || record.KnownPreimage == preimage
                 || record.Removal is { IsFulfill: true })
                    continue;

                updated.Add(record with { KnownPreimage = preimage });
                fulfills.Add(new OutgoingHtlcFulfilled(channelId, record.Id, record.PaymentHash, preimage));
            }

            if (updated.Count == 0)
                return [];

            // The preimage is persisted before any upstream fulfill (BOLT2 I10), as the resolvers do for a confirmed one
            var next = WithRecords(commitments, updated);
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ChannelStateDbRepository.ApplyAsync(next, new ChannelTransition(updated, [], [], false,
                                                                                   false, false, false));
            await unitOfWork.SaveChangesAsync();
            channel.UpdateCommitments(next);
        }

        foreach (var fulfilled in fulfills)
        {
            _logger.LogWarning("Preimage of our HTLC {HtlcId} (hash {PaymentHash}) of channel {ChannelId} seen in "
                             + "unconfirmed transaction {TxId}: fulfilling upstream now (B5-GEN-07)",
                               fulfilled.HtlcId, fulfilled.PaymentHash, channelId, Display(spend.TxId));
            if (scope.ServiceProvider.GetService<IHtlcSwitch>() is not { } htlcSwitch)
                continue;

            try
            {
                await htlcSwitch.HandleAsync(fulfilled, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The preimage is on the record: the switch's replays and the confirmed rounds raise it again
                _logger.LogError(e, "The switch failed on the fulfill of HTLC {HtlcId} of channel {ChannelId}",
                                 fulfilled.HtlcId, channelId);
            }
        }

        return fulfills.Select(f => f.HtlcId).ToList();
    }

    /// <summary>
    /// An unconfirmed spend of the funding output: classified, and for a revoked commitment the penalties are stored
    /// and published (once: a stored one is only published again).
    /// </summary>
    private async Task<(FundingSpendKind? Kind, IReadOnlyList<TxId> Penalties)> HandleFundingSpendAsync(
        ChannelId channelId, ChainTx spend, CancellationToken cancellationToken)
    {
        FundingSpendClassification? classification;
        List<BroadcastTransactionModel> toPublish = [];
        List<AlertAction> alerts = [];
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
             || channel.State is ChannelState.Closed or ChannelState.Stale)
                return (null, []);

            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channelId) is not null)
            {
                // Confirmed already (the block came first): the resolvers own it
                _logger.LogDebug("Unconfirmed funding spend {TxId} of channel {ChannelId}: its close is recorded",
                                 Display(spend.TxId), channelId);
                return (null, []);
            }

            classification = await _watcher.ClassifyAsync(channel, spend, unitOfWork);
            if (classification is null)
                return (null, []);

            _logger.LogWarning("Unconfirmed {Kind} {TxId} (commitment {Number}) spends the funding output of channel "
                             + "{ChannelId}; waiting for it to confirm", classification.Kind, Display(spend.TxId),
                               classification.CommitmentNumber, channelId);
            if (classification is not { Kind: FundingSpendKind.Revoked, CommitmentNumber: { } number })
                return (classification.Kind, []);

            // A penalty abandoned when this commitment left the mempool is revived: the commitment is back, and a
            // penalty built again would have the same txid (lock time 0, the same address, RFC 6979, the same fee)
            var broadcasts = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channelId);
            toPublish = broadcasts.Where(b => b.State is BroadcastState.Pending or BroadcastState.Abandoned
                                           && OnchainChannelWatcher.IsPreparedPurpose(b.Purpose)
                                           && OnchainChannelWatcher.SpendsFrom(b, spend.TxId))
                                  .ToList();
            var revived = await ReviveAbandonedAsync(unitOfWork, toPublish);
            if (toPublish.Count == 0)
            {
                if (_revokedCommitResolver is null)
                {
                    _logger.LogCritical("Channel {ChannelId}: revoked commitment {TxId} is in the mempool but no penalty "
                                      + "resolver is registered; waiting for its confirmation", channelId,
                                        Display(spend.TxId));
                    return (classification.Kind, []);
                }

                var actions = await _revokedCommitResolver.PrepareUnconfirmedPenaltiesAsync(
                                  channelId, spend, number, _blockchainMonitor.LastProcessedBlockHeight,
                                  cancellationToken);
                alerts = actions.OfType<AlertAction>().ToList();
                foreach (var broadcast in actions.OfType<BroadcastAction>().Select(b => b.Transaction))
                {
                    var stored = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(
                                     broadcast.TransactionId);
                    if (stored is null)
                    {
                        unitOfWork.BroadcastTransactionDbRepository.Add(broadcast);
                        toPublish.Add(broadcast);
                    }
                    else if (stored.State is BroadcastState.Pending or BroadcastState.Abandoned)
                    {
                        toPublish.Add(stored);
                    }
                }

                await ReviveAbandonedAsync(unitOfWork, toPublish);

                // Stored before the publish (D4): the monitor resends them after every block until they confirm
                await unitOfWork.SaveChangesAsync();
            }
            else if (revived)
            {
                await unitOfWork.SaveChangesAsync();
            }

            var prepared = _prepared.GetOrAdd(spend.TxId, _ => new PreparedPenalty(channelId, []));
            lock (prepared.TransactionIds)
            {
                foreach (var transaction in toPublish.Where(t => !prepared.TransactionIds.Contains(t.TransactionId)))
                    prepared.TransactionIds.Add(transaction.TransactionId);
            }
        }

        foreach (var alert in alerts)
            _logger.LogCritical("[{RequirementId}] Channel {ChannelId}: {Alert}", alert.RequirementId, channelId,
                                alert.Message);

        var broadcaster = _blockchainMonitor as IChainBroadcaster;
        foreach (var transaction in toPublish)
        {
            var accepted = broadcaster is not null && await broadcaster.PublishAsync(transaction);
            _logger.LogCritical("[B5-REV-01] Channel {ChannelId}: revoked commitment {CommitmentTxId} is in the "
                              + "mempool; {Purpose} {TxId} {Outcome} before it confirmed", channelId,
                                Display(spend.TxId), transaction.Purpose, Display(transaction.TransactionId),
                                accepted ? "broadcast" : "stored (the node refused it for now; resent every block)");
        }

        return (classification.Kind, toPublish.Select(t => t.TransactionId).ToList());
    }

    /// <summary>
    /// Stages every abandoned transaction of the list as pending again (the monitor then resends it after every block);
    /// true when one changed.
    /// </summary>
    private async Task<bool> ReviveAbandonedAsync(IUnitOfWork unitOfWork, IEnumerable<BroadcastTransactionModel> list)
    {
        var revived = false;
        foreach (var abandoned in list.Where(b => b.State == BroadcastState.Abandoned).ToList())
        {
            if (!await unitOfWork.BroadcastTransactionDbRepository.MarkPendingAsync(abandoned.TransactionId))
                continue;

            // The model too: the monitor keeps a published transaction for rebroadcast only while it is pending
            abandoned.MarkPending();
            revived = true;
            _logger.LogWarning("Channel {ChannelId}: {Purpose} {TxId}, abandoned when its revoked commitment left the "
                             + "mempool, is pending again: the commitment is back", abandoned.ChannelId,
                               abandoned.Purpose, Display(abandoned.TransactionId));
        }

        return revived;
    }

    private async Task AbandonAsync(PreparedPenalty prepared, CancellationToken cancellationToken)
    {
        List<TxId> transactionIds;
        lock (prepared.TransactionIds)
            transactionIds = prepared.TransactionIds.ToList();

        using (await _channelLockProvider.AcquireAsync(prepared.ChannelId, cancellationToken))
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(prepared.ChannelId) is not null)
                return; // The watcher recorded the close meanwhile and settled them

            foreach (var transactionId in transactionIds)
                await unitOfWork.BroadcastTransactionDbRepository.MarkAbandonedAsync(transactionId);
            await unitOfWork.SaveChangesAsync();
        }

        _logger.LogWarning("Channel {ChannelId}: the revoked commitment our penalties {TxIds} spend left bitcoind's "
                         + "mempool without confirming; abandoning them (a confirmed spend is resolved as usual)",
                           prepared.ChannelId, string.Join(", ", transactionIds.Select(Display)));
    }

    private async Task<bool> HasCloseAsync(ChannelId channelId)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channelId) is not null;
    }

    /// <summary>
    /// True when bitcoind still has the transaction (mempool, or a block it can serve); also true when it cannot be
    /// asked, so a node that lost bitcoind for a while abandons nothing.
    /// </summary>
    private async Task<bool> IsKnownToBitcoindAsync(TxId txId)
    {
        if (_chainService is null)
            return true;

        try
        {
            return await _chainService.GetTransactionAsync(new uint256((byte[])txId)) is not null;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not ask bitcoind for {TxId}", Display(txId));
            return true;
        }
    }

    /// <summary>
    /// True when the monitor processed bitcoind's tip and is not halted; false when it is behind, halted, or bitcoind
    /// cannot be asked (nothing is abandoned then).
    /// </summary>
    private async Task<bool> IsMonitorAtTipAsync()
    {
        if (_chainService is null)
            return true;
        if (_blockchainMonitor.IsChainProcessingHalted)
            return false;

        try
        {
            return _blockchainMonitor.LastProcessedBlockHeight >= await _chainService.GetCurrentBlockHeightAsync();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not ask bitcoind for its tip");
            return false;
        }
    }

    private static ChannelCommitments WithRecords(ChannelCommitments commitments, IEnumerable<HtlcRecord> records)
    {
        var htlcs = commitments.Htlcs;
        foreach (var record in records)
            htlcs = htlcs.SetItem(record.Key, record);

        return ChannelCommitments.Restore(commitments.ChannelId, commitments.Params, commitments.LocalBalanceMsat,
                                          commitments.RemoteBalanceMsat, htlcs.Values, commitments.FeeUpdates,
                                          commitments.LocalNextHtlcId, commitments.RemoteNextHtlcId,
                                          commitments.LocalCommit, commitments.RemoteCommit,
                                          commitments.RemoteNextCommit, commitments.RemoteNextPerCommitmentPoint);
    }

    private static bool Hashes(Secret preimage, Hash paymentHash) =>
        SHA256.HashData((byte[])preimage).AsSpan().SequenceEqual((byte[])paymentHash);

    /// <summary>A txid in the display (RPC) byte order, for logs (NL-275).</summary>
    private static string Display(TxId txId) => new uint256((byte[])txId).ToString();

    private sealed record WorkItem(MempoolSpendEventArgs? Spend, uint? BlockHeight, bool Restore);

    /// <summary>The penalties prepared for one revoked commitment still unconfirmed.</summary>
    private sealed class PreparedPenalty(ChannelId channelId, List<TxId> transactionIds)
    {
        public ChannelId ChannelId { get; } = channelId;
        public List<TxId> TransactionIds { get; } = transactionIds;
        public int MissingBlocks { get; set; }
    }
}

/// <summary>What <see cref="MempoolReactor"/> did with one unconfirmed spend (for logs and tests).</summary>
/// <param name="FulfilledHtlcIds">Our offered HTLCs whose preimage it revealed (staged and fulfilled upstream).</param>
/// <param name="FundingSpendKind">The classification of a funding spend, when it was one.</param>
/// <param name="PenaltyTransactionIds">The penalties stored and published for a revoked commitment.</param>
public sealed record MempoolReaction(
    IReadOnlyList<ulong> FulfilledHtlcIds,
    FundingSpendKind? FundingSpendKind,
    IReadOnlyList<TxId> PenaltyTransactionIds)
{
    /// <summary>Nothing done.</summary>
    public static MempoolReaction None { get; } = new([], null, []);
}