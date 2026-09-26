using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Channels.Safety;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Onchain.Anchors;

/// <summary>
/// The fail-the-channel service (BOLT2 plan N9-T4, D10; BOLT 5 plan O2-T2): persist <see cref="ChannelState.Failed"/>,
/// the <c>error</c> and the broadcast of our fully signed latest local commitment in one save, publish it, send the
/// <c>error</c>.
/// </summary>
/// <remarks>
/// <para>Singleton; see <see cref="IChannelFailureService"/> for the contract. Invariants: only the latest local
/// commitment is ever signed for broadcast (it is read from the snapshot under the channel's lock, and the signer
/// refuses an older number; I4); nothing is broadcast after proven data loss (checked here and by the signer after
/// <c>MarkDataLoss</c>; I12, B2-RE-23).</para>
/// <para>Persist before broadcast (NL-271, BOLT 5 plan invariant S1): under the lock the commitment is built and signed,
/// then Failed, the error and the commitment's <see cref="BroadcastTransactionModel"/> (purpose
/// <see cref="BroadcastPurpose.LocalCommitment"/>, with its commitment number) are saved in <b>one</b> save, so no
/// later update can change it (every update is refused on a Failed channel) and no crash can lose the intent to
/// broadcast it. After the lock it is published through the chain monitor's <see cref="Domain.Onchain.Interfaces.IChainBroadcaster"/>,
/// which also sends it again after every block and at startup until a block holds it. The stored commitment number
/// restores the signer's S1 mark at the next registration (NL-297, <c>ChannelManager</c>).</para>
/// <para>A publish counts as done only when the node accepted it or already has it; a refusal (node down, below the
/// mempool minimum fee, a conflict) is <see cref="ChannelFailureStatus.PublishFailed"/>, and the row stays pending.
/// What happens once the commitment (or the peer's) is on chain is the on-chain watcher's
/// (<c>Application/Onchain/OnchainChannelWatcher</c>): the channel moves to <c>OnchainResolving</c> and then
/// <c>Closed</c> once its outputs are resolved. A Failed channel of an older build (commitment watch, no broadcast
/// row) is resumed at <see cref="Start"/>, which writes the row.</para>
/// </remarks>
public sealed class ChannelFailureService : IChannelFailureService, IDisposable
{
    private readonly IAnchorCpfpService? _anchorCpfpService;
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelErrorSender _channelErrorSender;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly LocalCommitmentBroadcastBuilder _commitmentBuilder;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<ChannelFailureService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

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
                                 IAnchorCpfpService? anchorCpfpService = null)
    {
        _anchorCpfpService = anchorCpfpService;
        _blockchainMonitor = blockchainMonitor;
        _channelErrorSender = channelErrorSender;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _commitmentBuilder = commitmentBuilder;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <inheritdoc />
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _stopping = new CancellationTokenSource();
        _blockchainMonitor.OnNewBlockDetected += HandleNewBlockDetected;

        var token = _stopping.Token;
        _resumeTask = Task.Run(() => ResumeInterruptedBroadcastsAsync(token), CancellationToken.None);
        _anchorCpfpService?.Start();
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) != 1)
            return;

        _blockchainMonitor.OnNewBlockDetected -= HandleNewBlockDetected;
        _stopping.Cancel();
        _anchorCpfpService?.Stop();
    }

    /// <summary>The background resume started by <see cref="Start"/> (tests, diagnostics).</summary>
    public Task WhenResumedAsync() => _resumeTask;

    /// <summary>The channels whose publish failed and is retried on the next block (tests, diagnostics).</summary>
    public bool IsPublishPending(ChannelId channelId) => _pendingPublishes.ContainsKey(channelId);

    /// <summary>
    /// Resumes the broadcast of every loaded <c>Failed</c> channel without data loss that an older build failed: its
    /// commitment watch is stored and not confirmed, and there is no commitment broadcast row (the chain monitor
    /// rebroadcasts the rows on its own). <see cref="Start"/> runs it.
    /// </summary>
    public async Task ResumeInterruptedBroadcastsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var failed = _channelMemoryRepository.FindChannels(
                c => c.State == ChannelState.Failed && !c.DataLossDetected);
            if (failed.Count == 0)
                return;

            var toResume = new List<ChannelId>();
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var pending = await unitOfWork.WatchedTransactionDbRepository.GetAllPendingAsync();
                foreach (var channel in failed)
                {
                    if (!pending.Any(w => w.ChannelId == channel.ChannelId
                                       && !IsFundingTransaction(channel, w.TransactionId)))
                        continue;

                    var broadcasts =
                        await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channel.ChannelId);
                    if (broadcasts.All(b => b.Purpose != BroadcastPurpose.LocalCommitment))
                        toResume.Add(channel.ChannelId);
                }
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
        return FailChannelAsync(failure.FailedChannelId, ToRequest(failure), cancellationToken);
    }

    /// <inheritdoc />
    public Task<ChannelFailureOutcome> FailChannelAsync(ChannelId channelId, ChannelFailureRequest request,
                                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FailCoreAsync(channelId, request, sendError: true, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PreparedChannelFailure> PrepareFailureUnderLockAsync(ChannelId channelId,
                                                                     ChannelFailureRequest request,
                                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PrepareLockedAsync(channelId, request);
    }

    /// <inheritdoc />
    public async Task<ChannelFailureOutcome> CompleteFailureAsync(PreparedChannelFailure prepared, bool sendError,
                                                                  CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.EarlyOutcome is { } early)
            return early;

        ChannelFailureOutcome outcome;
        var channelId = prepared.ChannelId;
        if (prepared is { Commitment: { } commitment, Broadcast: { } broadcast })
        {
            outcome = await PublishAsync(channelId, commitment, broadcast, prepared.BroadcastStaged);
            if (outcome.Status == ChannelFailureStatus.PublishFailed)
                _pendingPublishes[channelId] = prepared.Request with { StillApplies = null };
            else
                _pendingPublishes.TryRemove(channelId, out _);
        }
        else
        {
            outcome = prepared.DecidedOutcome
                   ?? new ChannelFailureOutcome(ChannelFailureStatus.NoBroadcastableCommitment, null);
            if (outcome.Status is ChannelFailureStatus.RefusedDataLoss
                               or ChannelFailureStatus.NoBroadcastableCommitment)
            {
                // Nothing is ever to be broadcast for this channel; a later request without broadcast (an already
                // Failed channel failed again) leaves an earlier failure's publish retry alone
                _pendingPublishes.TryRemove(channelId, out _);
            }
        }

        if (sendError && prepared is { Channel: { } channel, Error: { } error })
            await _channelErrorSender.TrySendAsync(channel.RemoteNodeId, error);

        // BOLT 5 plan O7-T2 (B5-FAIL-06): an anchor commitment gets its CPFP child at once, not only at the next block;
        // after the error, outside the lock, never failing the failure
        if (_anchorCpfpService is not null && prepared.Commitment is not null
                                           && prepared.Channel is { ChannelParams.OptionAnchorOutputs: true })
        {
            try
            {
                await _anchorCpfpService.OnCommitmentBroadcastAsync(channelId, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "The anchor CPFP of failed channel {ChannelId} failed; retried at the next block",
                                 channelId);
            }
        }

        return outcome;
    }

    /// <summary>The request a <see cref="ChannelFailedException"/> stands for.</summary>
    public static ChannelFailureRequest ToRequest(ChannelFailedException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new ChannelFailureRequest(failure.Message,
                                         failure.PeerMessage ?? ChannelFailedException.DefaultPeerMessage,
                                         failure.MustBroadcast, failure.RequirementId);
    }

    private async Task<ChannelFailureOutcome> FailCoreAsync(ChannelId channelId, ChannelFailureRequest request,
                                                            bool sendError, CancellationToken cancellationToken)
    {
        PreparedChannelFailure prepared;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
            prepared = await PrepareLockedAsync(channelId, request);

        return await CompleteFailureAsync(prepared, sendError, cancellationToken);
    }

    /// <summary>Everything done under the channel's lock: checks, build and sign, the one save. Call it locked.</summary>
    private async Task<PreparedChannelFailure> PrepareLockedAsync(ChannelId channelId, ChannelFailureRequest request)
    {
        var prepared = new PreparedChannelFailure(channelId, request);
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            throw new KeyNotFoundException($"Channel {channelId} is not loaded");

        prepared.Channel = channel;
        if (channel.State is ChannelState.Closing or ChannelState.Closed or ChannelState.Stale
                                 or ChannelState.OnchainResolving
         || channel.State < ChannelState.V1FundingSigned)
        {
            // Nothing of this channel can be broadcast any more (closed, or already resolving on chain). A Closing
            // channel is never turned Failed: its agreed mutual close is on its way, and our commitment against it
            // would leave the channel Failed for good once the close confirms (wave 3 invariant, W5 review)
            _pendingPublishes.TryRemove(channelId, out _);
            prepared.EarlyOutcome = new ChannelFailureOutcome(ChannelFailureStatus.NotApplicable, null);
            return prepared;
        }

        // A request whose precondition no longer holds changes nothing, not even the retry of an earlier failure's
        // commitment publish (W4-E review F2)
        if (request.StillApplies is { } stillApplies && !stillApplies(channel))
        {
            prepared.EarlyOutcome = new ChannelFailureOutcome(ChannelFailureStatus.NotApplicable, null);
            return prepared;
        }

        _logger.LogCritical("Failing channel {ChannelId} ({RequirementId}): {Reason}; broadcast: {Broadcast}",
                            channelId, request.RequirementId, request.Reason, request.Broadcast);

        SignedLocalCommitment? commitment = null;
        if (channel.DataLossDetected)
        {
            // I12 / B2-RE-23: the peer holds a newer state; our commitment is revoked from its point of view
            _lightningSigner.MarkDataLoss(channelId);
            if (request.Broadcast)
                _logger.LogCritical("Not broadcasting the commitment of channel {ChannelId}: data loss was detected, "
                                  + "the peer must close it", channelId);
            prepared.DecidedOutcome = new ChannelFailureOutcome(ChannelFailureStatus.RefusedDataLoss, null);
        }
        else if (!request.Broadcast)
        {
            prepared.DecidedOutcome = new ChannelFailureOutcome(ChannelFailureStatus.FailedWithoutBroadcast, null);
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
                prepared.DecidedOutcome =
                    new ChannelFailureOutcome(ChannelFailureStatus.NoBroadcastableCommitment, null);
            }
        }

        // NL-271: Failed, the error and the commitment's broadcast row go in one save, so a crash before the publish
        // leaves the intent recorded and the chain monitor sends it at startup
        using var scope = _serviceScopeFactory.CreateScope();
        await PersistFailedAsync(scope, prepared, commitment);
        return prepared;
    }

    /// <summary>
    /// Persists <see cref="ChannelState.Failed"/> with the error (unless already stored) and, when a commitment is to
    /// be broadcast and has no broadcast row yet, its row, all in one save (NL-271).
    /// </summary>
    private async Task PersistFailedAsync(IServiceScope scope, PreparedChannelFailure prepared,
                                          SignedLocalCommitment? commitment)
    {
        var channel = prepared.Channel!;
        var request = prepared.Request;
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        if (commitment is not null)
        {
            prepared.Commitment = commitment;
            prepared.Broadcast =
                await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(commitment.Transaction.TxId);
            if (prepared.Broadcast is null)
            {
                prepared.Broadcast = new BroadcastTransactionModel(commitment.Transaction,
                                                                   BroadcastPurpose.LocalCommitment,
                                                                   channel.ChannelId,
                                                                   _blockchainMonitor.LastProcessedBlockHeight,
                                                                   commitmentNumber: commitment.CommitmentNumber);
                unitOfWork.BroadcastTransactionDbRepository.Add(prepared.Broadcast);
                prepared.BroadcastStaged = true;
            }
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

        prepared.Error = error;
        if (!persistChannel && !prepared.BroadcastStaged)
            return;

        if (persistChannel)
            await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        await unitOfWork.SaveChangesAsync();
        if (persistChannel)
            _channelMemoryRepository.UpdateChannel(channel);
    }

    /// <summary>The txid this process published for <paramref name="channelId"/>, if any (tests, diagnostics).</summary>
    public bool TryGetPublishedCommitment(ChannelId channelId, out TxId txId) =>
        _published.TryGetValue(channelId, out txId);

    public void Dispose()
    {
        Stop();
        _stopping.Dispose();
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
                                                           BroadcastTransactionModel broadcast, bool staged)
    {
        var txId = commitment.Transaction.TxId;
        if (!staged && _published.TryGetValue(channelId, out var published) && published == txId)
            return new ChannelFailureOutcome(ChannelFailureStatus.Rebroadcast, txId);

        switch (broadcast.State)
        {
            case BroadcastState.Confirmed:
                _published[channelId] = txId;
                return new ChannelFailureOutcome(ChannelFailureStatus.Rebroadcast, txId);
            case BroadcastState.Abandoned or BroadcastState.Replaced:
                _logger.LogWarning("Commitment {TxId} of failed channel {ChannelId} is not published: another "
                                 + "transaction spent the funding output", Display(txId), channelId);
                return new ChannelFailureOutcome(ChannelFailureStatus.Superseded, txId);
        }

        bool accepted;
        try
        {
            accepted = await _blockchainMonitor.PublishAsync(broadcast);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogCritical(e, "Publishing commitment {TxId} of failed channel {ChannelId} failed; it is sent "
                                 + "again after the next block", Display(txId), channelId);
            return new ChannelFailureOutcome(ChannelFailureStatus.PublishFailed, txId);
        }

        if (!accepted)
        {
            _logger.LogCritical("Commitment {TxId} of failed channel {ChannelId} was refused; it is sent again after "
                              + "the next block", Display(txId), channelId);
            return new ChannelFailureOutcome(ChannelFailureStatus.PublishFailed, txId);
        }

        _published[channelId] = txId;
        if (!staged)
            return new ChannelFailureOutcome(ChannelFailureStatus.Rebroadcast, txId);

        _logger.LogCritical("Broadcast local commitment {CommitmentNumber} ({TxId}, {HtlcOutputs} HTLC outputs) of "
                          + "failed channel {ChannelId}", commitment.CommitmentNumber, Display(txId),
                            commitment.HtlcOutputCount, channelId);
        return new ChannelFailureOutcome(ChannelFailureStatus.Broadcast, txId);
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

    /// <summary>A txid in the display (RPC, block explorer) byte order, for logs (NL-275).</summary>
    private static string? Display(TxId? txId) => txId is { } id ? new uint256(id).ToString() : null;

    private static bool IsFundingTransaction(ChannelModel channel, TxId txId) =>
        channel.FundingOutput?.TransactionId is { } fundingTxId && fundingTxId == txId;
}