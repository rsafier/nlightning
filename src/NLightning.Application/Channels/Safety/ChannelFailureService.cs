using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;

namespace NLightning.Application.Channels.Safety;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// The fail-the-channel service (BOLT2 plan N9-T4, D10; partial NL-094): persist <see cref="ChannelState.Failed"/> and
/// the <c>error</c>, build and fully sign our latest local commitment, publish it, send the <c>error</c>; and, once the
/// commitment confirms, persist <see cref="ChannelState.Closed"/>.
/// </summary>
/// <remarks>
/// <para>Singleton; see <see cref="IChannelFailureService"/> for the contract. Invariants: only the latest local
/// commitment is ever signed for broadcast (it is read from the snapshot under the channel's lock, and the signer
/// refuses an older number; I4); nothing is broadcast after proven data loss (checked here and by the signer after
/// <c>MarkDataLoss</c>; I12, B2-RE-23).</para>
/// <para>Persist before broadcast: under the lock the commitment is built and signed, then Failed, the error and the
/// watch of the commitment txid are saved in <b>one</b> save (NL-271, BOLT 5 plan invariant S1), so no later update
/// can change it (every update is refused on a Failed channel) and no crash can lose the intent to broadcast it; after
/// the lock the monitor follows the watch (<see cref="IBlockchainMonitor.TrackWatchedTransaction"/>) and the
/// transaction is published (<see cref="IBlockchainMonitor.PublishTransactionAsync"/>). A broadcast
/// repeated in the same process (a new block while the HTLC is still past its deadline) is skipped; after a restart
/// the stored watch makes it a rebroadcast (<see cref="IBitcoinChainService.SendTransactionAsync"/>).</para>
/// <para>A publish counts as done only when the node accepted it or already knows the transaction (in its mempool or
/// chain, <see cref="IsKnownToNodeAsync"/>); any other refusal (node down, below the mempool minimum fee, a conflict)
/// is <see cref="ChannelFailureStatus.PublishFailed"/> and, once <see cref="Start"/> ran, retried on every new block
/// until it succeeds. <see cref="Start"/> also resumes, in the background, the broadcast of every loaded Failed
/// channel (no data loss) that has an unconfirmed commitment watch: a publish interrupted by a crash or a node outage
/// before the restart.</para>
/// </remarks>
public sealed class ChannelFailureService : IChannelFailureService, IDisposable
{
    // bitcoind rejections that mean it already has the transaction (mempool or chain)
    private static readonly string[] s_alreadyKnownRejections =
    [
        "txn-already-in-mempool", "txn-already-known", "txn-same-nonwitness-data-in-mempool",
        "already in block chain"
    ];

    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelErrorSender _channelErrorSender;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly LocalCommitmentBroadcastBuilder _commitmentBuilder;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<ChannelFailureService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ChannelSafetyOptions _options;
    private readonly Network _network;

    // Commitments published by this process, per channel (a repeated failure does not republish every block)
    private readonly ConcurrentDictionary<ChannelId, TxId> _published = new();

    // Failures whose publish failed: retried on every new block until the publish succeeds
    private readonly ConcurrentDictionary<ChannelId, ChannelFailureRequest> _pendingPublishes = new();

    private CancellationTokenSource _stopping = new();
    private Task _resumeTask = Task.CompletedTask;
    private int _retryRunning;
    private int _started;

    public ChannelFailureService(IBlockchainMonitor blockchainMonitor, IChannelErrorSender channelErrorSender,
                                 IChannelLockProvider channelLockProvider,
                                 IChannelMemoryRepository channelMemoryRepository,
                                 LocalCommitmentBroadcastBuilder commitmentBuilder, ILightningSigner lightningSigner,
                                 ILogger<ChannelFailureService> logger, IServiceScopeFactory serviceScopeFactory,
                                 IServiceProvider serviceProvider, IOptions<NodeOptions> nodeOptions,
                                 IOptions<ChannelSafetyOptions>? safetyOptions = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _channelErrorSender = channelErrorSender;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _commitmentBuilder = commitmentBuilder;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _serviceProvider = serviceProvider;
        _options = safetyOptions?.Value ?? new ChannelSafetyOptions();
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ?? Network.RegTest;
    }

    /// <inheritdoc />
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _stopping = new CancellationTokenSource();
        _blockchainMonitor.OnTransactionConfirmed += HandleTransactionConfirmed;
        _blockchainMonitor.OnNewBlockDetected += HandleNewBlockDetected;

        var token = _stopping.Token;
        _resumeTask = Task.Run(() => ResumeInterruptedBroadcastsAsync(token), CancellationToken.None);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) != 1)
            return;

        _blockchainMonitor.OnTransactionConfirmed -= HandleTransactionConfirmed;
        _blockchainMonitor.OnNewBlockDetected -= HandleNewBlockDetected;
        _stopping.Cancel();
    }

    /// <summary>The background resume started by <see cref="Start"/> (tests, diagnostics).</summary>
    public Task WhenResumedAsync() => _resumeTask;

    /// <summary>The channels whose publish failed and is retried on the next block (tests, diagnostics).</summary>
    public bool IsPublishPending(ChannelId channelId) => _pendingPublishes.ContainsKey(channelId);

    /// <summary>
    /// Resumes the broadcast of every loaded <c>Failed</c> channel without data loss whose commitment watch is stored
    /// but not confirmed (a publish interrupted by a crash, or refused before a restart). <see cref="Start"/> runs it.
    /// </summary>
    public async Task ResumeInterruptedBroadcastsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var failed = _channelMemoryRepository.FindChannels(
                c => c.State == ChannelState.Failed && !c.DataLossDetected);
            if (failed.Count == 0)
                return;

            List<ChannelId> toResume;
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var pending = await unitOfWork.WatchedTransactionDbRepository.GetAllPendingAsync();
                toResume = failed.Where(c => pending.Any(w => w.ChannelId == c.ChannelId
                                                           && !IsFundingTransaction(c, w.TransactionId)))
                                 .Select(c => c.ChannelId)
                                 .ToList();
            }

            foreach (var channelId in toResume)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await FailCoreAsync(channelId,
                                                  new ChannelFailureRequest("resuming the broadcast after a restart",
                                                                            ChannelFailedException.DefaultPeerMessage),
                                                  sendError: false, cancellationToken);
                _logger.LogWarning("Resumed the broadcast of failed channel {ChannelId}: {Status} {TxId}", channelId,
                                   outcome.Status, Display(outcome.CommitmentTxId));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Resuming the broadcasts of failed channels failed");
        }
    }

    /// <summary>Retries every failed publish once (what a new block triggers after <see cref="Start"/>).</summary>
    public async Task RetryPendingPublishesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (channelId, request) in _pendingPublishes.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await FailCoreAsync(channelId, request, sendError: false, cancellationToken);
                if (outcome.Status != ChannelFailureStatus.PublishFailed)
                    _logger.LogWarning("Publish of the commitment of failed channel {ChannelId} retried: {Status} "
                                     + "{TxId}", channelId, outcome.Status, Display(outcome.CommitmentTxId));
            }
            catch (KeyNotFoundException)
            {
                _pendingPublishes.TryRemove(channelId, out _);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "Retrying the publish of failed channel {ChannelId} failed", channelId);
            }
        }
    }

    /// <inheritdoc />
    public Task<ChannelFailureOutcome> FailChannelAsync(ChannelFailedException failure,
                                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return FailChannelAsync(failure.FailedChannelId,
                                new ChannelFailureRequest(failure.Message,
                                                          failure.PeerMessage
                                                       ?? ChannelFailedException.DefaultPeerMessage,
                                                          failure.MustBroadcast, failure.RequirementId),
                                cancellationToken);
    }

    /// <inheritdoc />
    public Task<ChannelFailureOutcome> FailChannelAsync(ChannelId channelId, ChannelFailureRequest request,
                                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FailCoreAsync(channelId, request, sendError: true, cancellationToken);
    }

    private async Task<ChannelFailureOutcome> FailCoreAsync(ChannelId channelId, ChannelFailureRequest request,
                                                            bool sendError, CancellationToken cancellationToken)
    {
        ErrorMessage error;
        ChannelModel channel;
        SignedLocalCommitment? commitment = null;
        WatchedTransactionModel? stagedWatch = null;
        ChannelFailureOutcome? outcome = null;

        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var loaded))
                throw new KeyNotFoundException($"Channel {channelId} is not loaded");

            channel = loaded;
            if (channel.State is ChannelState.Closed or ChannelState.Stale
             || channel.State < ChannelState.V1FundingSigned)
            {
                // Nothing of this channel can be broadcast any more
                _pendingPublishes.TryRemove(channelId, out _);
                return new ChannelFailureOutcome(ChannelFailureStatus.NotApplicable, null);
            }

            // A request whose precondition no longer holds changes nothing, not even the retry of an earlier
            // failure's commitment publish (W4-E review F2)
            if (request.StillApplies is { } stillApplies && !stillApplies(channel))
                return new ChannelFailureOutcome(ChannelFailureStatus.NotApplicable, null);

            _logger.LogCritical("Failing channel {ChannelId} ({RequirementId}): {Reason}; broadcast: {Broadcast}",
                                channelId, request.RequirementId, request.Reason, request.Broadcast);

            if (channel.DataLossDetected)
            {
                // I12 / B2-RE-23: the peer holds a newer state; our commitment is revoked from its point of view
                _lightningSigner.MarkDataLoss(channelId);
                if (request.Broadcast)
                    _logger.LogCritical("Not broadcasting the commitment of channel {ChannelId}: data loss was "
                                      + "detected, the peer must close it", channelId);
                outcome = new ChannelFailureOutcome(ChannelFailureStatus.RefusedDataLoss, null);
            }
            else if (!request.Broadcast)
            {
                outcome = new ChannelFailureOutcome(ChannelFailureStatus.FailedWithoutBroadcast, null);
            }
            else
            {
                try
                {
                    // Built under the lock, from the snapshot the Failed save below freezes (Failed refuses every
                    // update), so the commitment and the recorded intent to broadcast it can't diverge
                    commitment = _commitmentBuilder.Build(channel);
                }
                catch (Exception e) when (e is InvalidOperationException or SignerException)
                {
                    _logger.LogCritical(e, "Cannot build a broadcastable commitment for failed channel {ChannelId}",
                                        channelId);
                    outcome = new ChannelFailureOutcome(ChannelFailureStatus.NoBroadcastableCommitment, null);
                }
            }

            // NL-271: Failed, the error and the watch of the commitment to broadcast go in one save, so a crash
            // before the publish leaves the intent recorded and the start-up resume sends it
            using var scope = _serviceScopeFactory.CreateScope();
            (error, stagedWatch) = await PersistFailedAsync(scope, channel, request, commitment);
        }

        if (commitment is not null)
        {
            outcome = await PublishAsync(channelId, commitment, stagedWatch);
            if (outcome.Status == ChannelFailureStatus.PublishFailed)
                _pendingPublishes[channelId] = request with { StillApplies = null };
            else
                _pendingPublishes.TryRemove(channelId, out _);
        }
        else if (outcome!.Status is ChannelFailureStatus.RefusedDataLoss
                 or ChannelFailureStatus.NoBroadcastableCommitment)
        {
            // Nothing is ever to be broadcast for this channel; a later request without broadcast (an already Failed
            // channel failed again) leaves an earlier failure's publish retry alone
            _pendingPublishes.TryRemove(channelId, out _);
        }

        if (sendError)
            await _channelErrorSender.TrySendAsync(channel.RemoteNodeId, error);

        return outcome!;
    }

    /// <summary>The txid this process published for <paramref name="channelId"/>, if any (tests, diagnostics).</summary>
    public bool TryGetPublishedCommitment(ChannelId channelId, out TxId txId) =>
        _published.TryGetValue(channelId, out txId);

    public void Dispose()
    {
        Stop();
        _stopping.Dispose();
    }

    /// <summary>
    /// Persists <see cref="ChannelState.Failed"/> with the error (unless already stored) and, when a commitment is to
    /// be broadcast and its watch is not stored yet, the watch of its txid, all in one save (NL-271).
    /// </summary>
    /// <returns>The error to send and the watch staged by this save (null when none was needed).</returns>
    private async Task<(ErrorMessage Error, WatchedTransactionModel? StagedWatch)> PersistFailedAsync(
        IServiceScope scope, ChannelModel channel, ChannelFailureRequest request, SignedLocalCommitment? commitment)
    {
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        WatchedTransactionModel? watch = null;
        if (commitment is not null
         && await unitOfWork.WatchedTransactionDbRepository.GetByTransactionIdAsync(commitment.Transaction.TxId)
                is null)
        {
            watch = new WatchedTransactionModel(channel.ChannelId, commitment.Transaction.TxId,
                                                Math.Max(1, _options.CommitmentConfirmationDepth));
            unitOfWork.WatchedTransactionDbRepository.Add(watch);
        }

        var error = await GetStoredErrorAsync(scope, channel);
        var persistChannel = error is null;
        if (error is null)
        {
            var messageFactory = scope.ServiceProvider.GetRequiredService<IMessageFactory>();
            var messageSerializer = scope.ServiceProvider.GetRequiredService<IMessageSerializer>();
            error = messageFactory.CreateErrorMessage(request.PeerMessage, channel.ChannelId);
            if (channel is { State: ChannelState.Failed, ErrorSent: not null })
            {
                // A stored error that can't be read: keep it, send a fresh one
                persistChannel = false;
            }
            else
            {
                using var errorStream = new MemoryStream();
                await messageSerializer.SerializeAsync(error, errorStream);

                if (channel.State < ChannelState.Failed)
                    channel.UpdateState(ChannelState.Failed);
                channel.MarkErrorSent(errorStream.ToArray());
            }
        }

        if (!persistChannel && watch is null)
            return (error, null);

        if (persistChannel)
            await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        await unitOfWork.SaveChangesAsync();
        if (persistChannel)
            _channelMemoryRepository.UpdateChannel(channel);
        return (error, watch);
    }

    /// <summary>The stored error of a channel failed before (a handler's ChannelFailedException, or an earlier call).
    /// </summary>
    private async Task<ErrorMessage?> GetStoredErrorAsync(IServiceScope scope, ChannelModel channel)
    {
        if (channel.State != ChannelState.Failed || channel.ErrorSent is not { } stored)
            return null;

        try
        {
            var messageSerializer = scope.ServiceProvider.GetRequiredService<IMessageSerializer>();
            using var storedStream = new MemoryStream(stored.ToArray(), false);
            if (await messageSerializer.DeserializeMessageAsync(storedStream) is ErrorMessage storedError)
                return storedError;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "The stored error of channel {ChannelId} can't be read", channel.ChannelId);
        }

        return null;
    }

    private async Task<ChannelFailureOutcome> PublishAsync(ChannelId channelId, SignedLocalCommitment commitment,
                                                           WatchedTransactionModel? stagedWatch)
    {
        var txId = commitment.Transaction.TxId;
        if (stagedWatch is not null)
            return await PublishFirstAsync(channelId, commitment, stagedWatch);

        if (_published.TryGetValue(channelId, out var published) && published == txId)
            return new ChannelFailureOutcome(ChannelFailureStatus.Rebroadcast, txId);

        try
        {
            bool watched;
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                watched = await unitOfWork.WatchedTransactionDbRepository.GetByTransactionIdAsync(txId) is not null;
            }

            if (watched)
            {
                // Published or attempted before (an earlier run, or a publish that failed after its watch was
                // saved): send it again, the watch is already stored
                var chainService = _serviceProvider.GetRequiredService<IBitcoinChainService>();
                try
                {
                    await chainService.SendTransactionAsync(Transaction.Load(commitment.Transaction.RawTxBytes,
                                                                             _network));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    if (!await IsKnownToNodeAsync(chainService, txId, e))
                    {
                        _logger.LogCritical(e, "Rebroadcast of commitment {TxId} of failed channel {ChannelId} was "
                                             + "refused; retrying on the next block", Display(txId), channelId);
                        return new ChannelFailureOutcome(ChannelFailureStatus.PublishFailed, txId);
                    }

                    _logger.LogInformation("Commitment {TxId} of channel {ChannelId} is already known to the node",
                                           Display(txId), channelId);
                }

                _published[channelId] = txId;
                return new ChannelFailureOutcome(ChannelFailureStatus.Rebroadcast, txId);
            }

            // Not watched and nothing staged: a watch that disappeared (e.g. pruned); record it again, then publish
            await _blockchainMonitor.PublishAndWatchTransactionAsync(channelId, commitment.Transaction,
                                                                     Math.Max(1, _options.CommitmentConfirmationDepth));
            return Published(channelId, commitment);
        }
        catch (Exception e)
        {
            _logger.LogCritical(e, "Publishing commitment {TxId} of failed channel {ChannelId} failed", Display(txId),
                                channelId);
            return new ChannelFailureOutcome(ChannelFailureStatus.PublishFailed, txId);
        }
    }

    /// <summary>
    /// The first publish of a commitment whose watch was saved with Failed (NL-271): the monitor follows that watch,
    /// then the transaction is sent. A refusal counts as done only when the node already has it.
    /// </summary>
    private async Task<ChannelFailureOutcome> PublishFirstAsync(ChannelId channelId, SignedLocalCommitment commitment,
                                                                WatchedTransactionModel watch)
    {
        var txId = commitment.Transaction.TxId;
        try
        {
            _blockchainMonitor.TrackWatchedTransaction(watch);
            await _blockchainMonitor.PublishTransactionAsync(commitment.Transaction);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var chainService = _serviceProvider.GetService<IBitcoinChainService>();
            if (chainService is null || !await IsKnownToNodeAsync(chainService, txId, e))
            {
                _logger.LogCritical(e, "Publishing commitment {TxId} of failed channel {ChannelId} failed; retrying "
                                     + "on the next block (its watch is stored)", Display(txId), channelId);
                return new ChannelFailureOutcome(ChannelFailureStatus.PublishFailed, txId);
            }
        }

        return Published(channelId, commitment);
    }

    private ChannelFailureOutcome Published(ChannelId channelId, SignedLocalCommitment commitment)
    {
        var txId = commitment.Transaction.TxId;
        _published[channelId] = txId;
        _logger.LogCritical("Broadcast local commitment {CommitmentNumber} ({TxId}, {HtlcOutputs} HTLC outputs) "
                          + "of failed channel {ChannelId}; its outputs are not swept yet (BOLT 5, NL-094)",
                            commitment.CommitmentNumber, Display(txId), commitment.HtlcOutputCount,
                            channelId);
        return new ChannelFailureOutcome(ChannelFailureStatus.Broadcast, txId);
    }

    /// <summary>
    /// True when a refused send still means the node has the transaction: bitcoind's "already in the chain"
    /// (RPC -27) or "already in the mempool" rejections, or the transaction found through <c>getrawtransaction</c>.
    /// Every other refusal (node unreachable, fee too low, missing or conflicting inputs) is a failed publish.
    /// </summary>
    private async Task<bool> IsKnownToNodeAsync(IBitcoinChainService chainService, TxId txId, Exception sendError)
    {
        if (sendError is RPCException { RPCCode: RPCErrorCode.RPC_VERIFY_ALREADY_IN_CHAIN })
            return true;

        var message = sendError.Message;
        if (s_alreadyKnownRejections.Any(r => message.Contains(r, StringComparison.OrdinalIgnoreCase)))
            return true;

        try
        {
            return await chainService.GetTransactionAsync(new uint256(txId)) is not null;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Cannot check whether commitment {TxId} is known to the node", Display(txId));
            return false;
        }
    }

    private void HandleNewBlockDetected(object? sender, NewBlockEventArgs args)
    {
        if (_pendingPublishes.IsEmpty || Interlocked.Exchange(ref _retryRunning, 1) == 1)
            return;

        var token = _stopping.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await RetryPendingPublishesAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Stopping
            }
            finally
            {
                Interlocked.Exchange(ref _retryRunning, 0);
            }
        }, CancellationToken.None);
    }

    private void HandleTransactionConfirmed(object? sender, TransactionConfirmedEventArgs args)
    {
        var watched = args.WatchedTransaction;
        if (!_channelMemoryRepository.TryGetChannel(watched.ChannelId, out var channel)
         || channel.State != ChannelState.Failed || IsFundingTransaction(channel, watched.TransactionId))
            return;

        _ = CloseAsync(watched.ChannelId, watched.TransactionId, args.Height);
    }

    /// <summary>A txid in the display (RPC, block explorer) byte order, for logs (NL-275).</summary>
    private static string? Display(TxId? txId) => txId is { } id ? new uint256(id).ToString() : null;

    private static bool IsFundingTransaction(ChannelModel channel, TxId txId) =>
        channel.FundingOutput?.TransactionId is { } fundingTxId && fundingTxId == txId;

    /// <summary>
    /// Our commitment (a watched transaction of a Failed channel other than its funding) confirmed: the channel is
    /// closed. Its outputs still need BOLT 5 (NL-094).
    /// </summary>
    private async Task CloseAsync(ChannelId channelId, TxId txId, uint height)
    {
        try
        {
            using var channelLock = await _channelLockProvider.AcquireAsync(channelId);
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
             || channel.State != ChannelState.Failed)
                return;

            channel.UpdateState(ChannelState.Closed);
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
            await unitOfWork.SaveChangesAsync();
            _channelMemoryRepository.UpdateChannel(channel);

            _logger.LogWarning("Channel {ChannelId} closed: our commitment {TxId} confirmed at height {Height}; "
                             + "to_local and HTLC outputs are not swept (BOLT 5, NL-094)", channelId, Display(txId), height);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to persist the close of channel {ChannelId}", channelId);
        }
    }
}