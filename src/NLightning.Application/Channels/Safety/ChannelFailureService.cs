using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Channels.Safety;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Onchain;
using Onchain.Accounting;
using Onchain.Anchors;
using Onchain.Interfaces;
using Services;

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
public sealed class ChannelFailureService : IChannelFailureService, ISpliceCommitmentBroadcaster, IDisposable
{
    private readonly IAnchorCpfpService? _anchorCpfpService;
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelErrorSender _channelErrorSender;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly LocalCommitmentBroadcastBuilder _commitmentBuilder;
    private readonly ICommitmentTransactionBuilder? _commitmentTransactionBuilder;
    private readonly ICommitmentTransactionModelFactory? _commitmentTransactionModelFactory;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<ChannelFailureService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    // Commitments published by this process, per channel (a repeated failure does not republish every block)
    private readonly ConcurrentDictionary<ChannelId, TxId> _published = new();

    // Failures whose publish failed: retried on every new block until the publish succeeds
    private readonly ConcurrentDictionary<ChannelId, ChannelFailureRequest> _pendingPublishes = new();

    // Splice fundings our commitment was broadcast on after the splice confirmed (SP2-C-T2), per channel
    private readonly ConcurrentDictionary<(ChannelId, TxId), byte> _spliceBroadcasts = new();

    private CancellationTokenSource _stopping = new();
    private Task _resumeTask = Task.CompletedTask;
    private int _retryRunning;
    private int _spliceCheckRunning;
    private int _started;

    public ChannelFailureService(IBlockchainMonitor blockchainMonitor, IChannelErrorSender channelErrorSender,
                                 IChannelLockProvider channelLockProvider,
                                 IChannelMemoryRepository channelMemoryRepository,
                                 LocalCommitmentBroadcastBuilder commitmentBuilder, ILightningSigner lightningSigner,
                                 ILogger<ChannelFailureService> logger, IServiceScopeFactory serviceScopeFactory,
                                 IAnchorCpfpService? anchorCpfpService = null,
                                 ICommitmentTransactionModelFactory? commitmentTransactionModelFactory = null,
                                 ICommitmentTransactionBuilder? commitmentTransactionBuilder = null)
    {
        _anchorCpfpService = anchorCpfpService;
        _commitmentTransactionModelFactory = commitmentTransactionModelFactory;
        _commitmentTransactionBuilder = commitmentTransactionBuilder;
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

        // BOLT 5 plan O7-T2 (B5-FAIL-06): an anchor commitment gets its CPFP child at once, not only at the next block.
        // Only scheduled: this path may run on the peer's inbound loop (ChannelManager's MustBroadcast path sends the
        // error after it returns), which must not wait for a block round's fee estimates and wallet selection
        if (_anchorCpfpService is not null && prepared.Commitment is not null
                                           && prepared.Channel is { ChannelParams.OptionAnchorOutputs: true })
        {
            try
            {
                _anchorCpfpService.ScheduleCommitmentRound(channelId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "The anchor CPFP of failed channel {ChannelId} could not be scheduled; retried at "
                                  + "the next block", channelId);
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
                // NL-604: the commitment's fee is the funding capacity minus its outputs (paid by the funder)
                prepared.Broadcast = new BroadcastTransactionModel(commitment.Transaction,
                                                                   BroadcastPurpose.LocalCommitment,
                                                                   channel.ChannelId,
                                                                   _blockchainMonitor.LastProcessedBlockHeight,
                                                                   commitmentNumber: commitment.CommitmentNumber,
                                                                   fee: OnchainTransactionFees.ForCommitment(
                                                                       commitment.Transaction, channel));
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
        var token = _stopping.Token;
        if (Interlocked.Exchange(ref _spliceCheckRunning, 1) == 0)
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckConfirmedSplicesAsync(token);
                }
                catch (OperationCanceledException)
                {
                    // Stopping
                }
                finally
                {
                    Interlocked.Exchange(ref _spliceCheckRunning, 0);
                }
            }, CancellationToken.None);

        if (_pendingPublishes.IsEmpty || Interlocked.Exchange(ref _retryRunning, 1) == 1)
            return;

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

    #region Force close with a pending splice (splicing plan §3.6, SP2-C-T2)

    /// <summary>
    /// Every block: a failed channel whose pending splice confirmed (its <c>WatchedTransactions</c> row has a block)
    /// gets our commitment on that splice's funding (the one on the funding the splice spent can never confirm), once
    /// per process and funding. The on-chain watcher asks for it as soon as it sees the splice; this covers a restart
    /// between the watcher's save and the broadcast. A channel resolving on chain whose recorded close the watcher
    /// retired for the splice (a reorg, no close left) is checked the same way.
    /// </summary>
    public async Task CheckConfirmedSplicesAsync(CancellationToken cancellationToken = default)
    {
        var failed = _channelMemoryRepository.FindChannels(
            c => c is { State: ChannelState.Failed or ChannelState.OnchainResolving, DataLossDetected: false });
        foreach (var channel in failed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var confirmed = new List<TxId>();
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    if (channel.State == ChannelState.OnchainResolving
                     && await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channel.ChannelId) is not null)
                        continue;

                    var fundings = await OnchainFundings.GetAllAsync(unitOfWork, channel, _logger);
                    foreach (var funding in fundings.Where(f => f.Status == ChannelFundingStatus.Pending
                                                             && !OnchainFundings.IsCurrent(channel, f)
                                                             && !_spliceBroadcasts.ContainsKey(
                                                                    (channel.ChannelId, f.FundingTxId))))
                    {
                        var watch = await unitOfWork.WatchedTransactionDbRepository.GetByTransactionIdAsync(
                                        funding.FundingTxId);
                        if (watch?.FirstSeenAtHeight is not null)
                            confirmed.Add(funding.FundingTxId);
                    }
                }

                foreach (var spliceTxId in confirmed)
                {
                    var outcome = await BroadcastOnSpliceAsync(channel.ChannelId, spliceTxId, cancellationToken);
                    _logger.LogWarning("Failed channel {ChannelId}: splice {SpliceTxId} confirmed; our commitment on it "
                                     + "{Status} {TxId}", channel.ChannelId, Display(spliceTxId), outcome.Status,
                                       Display(outcome.CommitmentTxId));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "Checking the splices of failed channel {ChannelId} failed", channel.ChannelId);
            }
        }
    }

    /// <inheritdoc />
    public async Task<ChannelFailureOutcome> BroadcastOnSpliceAsync(ChannelId channelId, TxId spliceFundingTxId,
                                                                    CancellationToken cancellationToken = default)
    {
        SignedLocalCommitment commitment;
        BroadcastTransactionModel row;
        bool staged;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
             || channel.State is not (ChannelState.Failed or ChannelState.OnchainResolving))
                return new ChannelFailureOutcome(ChannelFailureStatus.NotApplicable, null);

            if (channel.DataLossDetected)
                return new ChannelFailureOutcome(ChannelFailureStatus.RefusedDataLoss, null);

            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // A recorded close stands (the watcher retires one the splice replaced before it asks): a commitment
            // already spends one of the channel's fundings
            if (channel.State == ChannelState.OnchainResolving
             && await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channelId) is not null)
                return new ChannelFailureOutcome(ChannelFailureStatus.NotApplicable, null);

            var fundings = await OnchainFundings.GetAllAsync(unitOfWork, channel, _logger);
            var funding = fundings.FirstOrDefault(f => f.FundingTxId == spliceFundingTxId
                                                    && f.Status == ChannelFundingStatus.Pending
                                                    && !OnchainFundings.IsCurrent(channel, f));
            if (funding is null)
                return new ChannelFailureOutcome(ChannelFailureStatus.NotApplicable, null);

            try
            {
                commitment = await BuildOnFundingAsync(unitOfWork, channel, funding);
            }
            catch (Exception e) when (e is InvalidOperationException or SignerException or ArgumentException
                                          or NotSupportedException)
            {
                _logger.LogCritical(e, "Cannot build our commitment on splice {TxId} of failed channel {ChannelId}",
                                    Display(spliceFundingTxId), channelId);
                return new ChannelFailureOutcome(ChannelFailureStatus.NoBroadcastableCommitment, null);
            }

            var existing = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(
                               commitment.Transaction.TxId);
            staged = existing is null;
            row = existing ?? new BroadcastTransactionModel(commitment.Transaction, BroadcastPurpose.LocalCommitment,
                                                            channelId, _blockchainMonitor.LastProcessedBlockHeight,
                                                            commitmentNumber: commitment.CommitmentNumber,
                                                            fee: OnchainTransactionFees.FromInputs(
                                                                commitment.Transaction, funding.CapacitySatoshis));
            if (staged)
                unitOfWork.BroadcastTransactionDbRepository.Add(row);

            // Our commitment on the funding the splice spent can never confirm now
            if (fundings.FirstOrDefault(f => OnchainFundings.IsCurrent(channel, f)) is { } spent)
            {
                var broadcasts = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channelId);
                var stales = (broadcasts ?? []).Where(b => b.Purpose == BroadcastPurpose.LocalCommitment
                                                        && b.State == BroadcastState.Pending
                                                        && b.TransactionId != commitment.Transaction.TxId
                                                        && SpendsOutpoint(b, spent))
                                               .ToList();
                foreach (var stale in stales)
                    await unitOfWork.BroadcastTransactionDbRepository.MarkAbandonedAsync(stale.TransactionId);
            }

            await unitOfWork.SaveChangesAsync();
            _spliceBroadcasts[(channelId, spliceFundingTxId)] = 0;
        }

        _logger.LogCritical("Failed channel {ChannelId}: splice {SpliceTxId} confirmed instead of our commitment; "
                          + "broadcasting our commitment {Number} on the splice funding ({TxId})", channelId,
                            Display(spliceFundingTxId), commitment.CommitmentNumber,
                            Display(commitment.Transaction.TxId));
        _published.TryRemove(channelId, out _);
        var outcome = await PublishAsync(channelId, commitment, row, staged);
        if (_anchorCpfpService is not null && _channelMemoryRepository.TryGetChannel(channelId, out var loaded)
                                           && loaded.ChannelParams.OptionAnchorOutputs)
        {
            try
            {
                _anchorCpfpService.ScheduleCommitmentRound(channelId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "The anchor CPFP of failed channel {ChannelId} could not be scheduled", channelId);
            }
        }

        return outcome;
    }

    /// <summary>
    /// Our latest local commitment on the pending splice funding <paramref name="funding"/>, signed for broadcast
    /// (SP-I4: the same number as the one signed on the current funding): its spec and the peer's signature from the
    /// engine (in memory), else from the funding's stored slot (SP-I2), built as it was verified
    /// (<c>CommitmentSigningService</c>).
    /// </summary>
    private async Task<SignedLocalCommitment> BuildOnFundingAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                                  ChannelFunding funding)
    {
        if (_commitmentTransactionModelFactory is null || _commitmentTransactionBuilder is null)
            throw new InvalidOperationException("No commitment model factory or builder is registered");

        var commitments = channel.Commitments
                       ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no snapshot");
        CommitmentSpec spec;
        CommitmentSignatures signatures;
        if (commitments.PendingFundings.FirstOrDefault(f => f.FundingTxId == funding.FundingTxId) is { } pending
         && commitments.LocalCommit.SignaturesFor(funding.FundingTxId) is { } inMemory)
        {
            spec = ChannelCommitments.SpecFor(commitments.LocalCommit.Spec, pending);
            signatures = inMemory;
        }
        else
        {
            var stored = await unitOfWork.ChannelFundingDbRepository.GetLocalCommitmentAsync(channel.ChannelId,
                                                                                            funding.FundingTxId);
            if (stored is not { RemoteSignatures: { } storedSignatures }
             || stored.Number != commitments.LocalCommit.Number)
                throw new InvalidOperationException(
                    $"No signed local commitment {commitments.LocalCommit.Number} on splice {funding.FundingTxId}");

            spec = stored.Spec;
            signatures = storedSignatures;
        }

        var number = commitments.LocalCommit.Number;
        var model = _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(
            channel, CommitmentTxSpec.FromCommitmentSpec(spec), CommitmentSide.Local, number);
        model = CommitmentSigningService.WithFunding(model, funding, CommitmentSide.Local);
        var built = _commitmentTransactionBuilder.BuildWithOutputMap(model);
        var signed = _lightningSigner.SignLocalCommitmentForBroadcast(channel.ChannelId, funding.FundingTxId, number,
                                                                      built.Transaction, signatures.Signature);
        return new SignedLocalCommitment(number, signed, built.HtlcOutputsInTxOrder.Count);
    }

    private static bool SpendsOutpoint(BroadcastTransactionModel broadcast, ChannelFunding funding) =>
        ChainTxMapper.TryParse(broadcast.RawTransaction, out var transaction) && transaction is not null
     && transaction.IndexOfInputSpending(funding.FundingTxId, funding.OutputIndex) >= 0;

    #endregion

    /// <summary>A txid in the display (RPC, block explorer) byte order, for logs (NL-275).</summary>
    private static string? Display(TxId? txId) => txId is { } id ? new uint256(id).ToString() : null;

    private static bool IsFundingTransaction(ChannelModel channel, TxId txId) =>
        channel.FundingOutput?.TransactionId is { } fundingTxId && fundingTxId == txId;
}