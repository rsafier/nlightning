using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Channels.Managers;

using Accounting;
using Backup;
using Close;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Constants;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;
using Gossip.Announcements.Interfaces;
using Handlers;
using Handlers.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using InteractiveTx.Interfaces;
using Interfaces;
using LiquidityAds;
using Onchain.Interfaces;
using Quiescence;
using Reestablish;
using Safety;
using Safety.Interfaces;
using Services;
using Splicing.Interfaces;

public class ChannelManager : IChannelManager, IChannelMessagePublisher
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly Lock _connectionGate = new();
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<ChannelManager> _logger;
    private readonly ILightningSigner _lightningSigner;
    private readonly IServiceProvider _serviceProvider;

    public event EventHandler<ChannelResponseMessageEventArgs>? OnResponseMessageReady;

    public ChannelManager(IBlockchainMonitor blockchainMonitor, IChannelLockProvider channelLockProvider,
                          IChannelMemoryRepository channelMemoryRepository, ILogger<ChannelManager> logger,
                          ILightningSigner lightningSigner, IServiceProvider serviceProvider)
    {
        _blockchainMonitor = blockchainMonitor;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _lightningSigner = lightningSigner;

        blockchainMonitor.OnNewBlockDetected += HandleNewBlockDetected;
        blockchainMonitor.OnTransactionConfirmed += HandleFundingConfirmationAsync;
        blockchainMonitor.OnWatchedOutpointSpent += HandleWatchedOutpointSpent;
    }

    /// <inheritdoc />
    /// <remarks>
    /// After the registration (and outside the channel's lock) the channel's pending domain events are re-derived from
    /// the persisted HTLC states and handed to the HTLC switch (invariant I8): an HTLC locked in before a crash is
    /// resolved, and a settled HTLC is pruned. The switch is idempotent.
    /// </remarks>
    public async Task RegisterExistingChannelAsync(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        using var scope = _serviceProvider.CreateScope();
        using (await _channelLockProvider.AcquireAsync(channel.ChannelId))
            await RegisterExistingChannelLockedAsync(scope, channel);

        await RaiseDomainEventsAsync(scope);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Raises <see cref="OnResponseMessageReady"/> for each message; call it while holding the channel's lock. An
    /// update, <c>commitment_signed</c> or <c>revoke_and_ack</c> of a channel that is not reestablished on the peer's
    /// current connection is dropped (B2-RE-07): the caller checked the link before persisting, but the connection can
    /// be replaced during the save, and the peer's new connection must see our <c>channel_reestablish</c> first. What
    /// was persisted is retransmitted by the reestablish. The check and the raise run under the same gate as
    /// <see cref="OnPeerConnectionChanged"/>, which the peer manager calls before it swaps the connection, so a message
    /// that passes the check is enqueued on the old connection.
    /// </remarks>
    public void Publish(CompactPubKey peerPubKey, IReadOnlyList<IChannelMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var tracker = GetTracker();
        lock (_connectionGate)
        {
            var kept = tracker is null
                           ? messages
                           : messages.Where(m => !IsNormalOperationMessage(m.Type)
                                              || tracker.IsReestablished(m.Payload.ChannelId))
                                     .ToList();
            if (kept.Count != messages.Count)
                _logger.LogInformation(
                    "Dropping {Count} message(s) for peer {Peer}: the connection changed before they went out; the channel_reestablish retransmits them",
                    messages.Count - kept.Count, peerPubKey);

            RaiseResponseMessages(peerPubKey, kept);
        }
    }

    private async Task RegisterExistingChannelLockedAsync(IServiceScope scope, ChannelModel channel)
    {
        if (!await ResumeStartupStateAsync(scope, channel))
            return;

        // Add the channel to the memory repository
        _channelMemoryRepository.AddChannel(channel);

        // A signer with an IChannelSigningInfoSource loads the channel from the database on first use, with its
        // commitment number and the S1 mark of a persisted commitment broadcast (NL-067, NL-343). One without a source
        // (in-process tests) is registered here, before the first connection, with the S1 mark (NL-297)
        if (!SignerLoadsChannels)
        {
            var signingInfo = channel.GetSigningInfo();
            if (await GetBroadcastSignedCommitmentNumberAsync(scope, channel) is { } broadcastNumber)
            {
                signingInfo = signingInfo with { BroadcastSignedCommitmentNumber = broadcastNumber };
                _logger.LogInformation(
                    "Channel {ChannelId} has local commitment {Number} signed for broadcast; the signer never "
                  + "revokes it", channel.ChannelId, broadcastNumber);
            }

            _lightningSigner.RegisterChannel(channel.ChannelId, signingInfo);
        }

        _logger.LogInformation("Loaded channel {channelId} from database", channel.ChannelId);

        // The repository attaches the commitment snapshot. The peer is not connected yet, so its unsigned updates are
        // reverted now and that is persisted before any channel_reestablish (BOLT 2 retransmission, plan §3.11)
        if (channel.Commitments is not null)
        {
            await RevertUncommittedAsync(channel);
            await QueuePendingDomainEventsAsync(scope, channel);
        }

        switch (channel.State)
        {
            case ChannelState.Open when channel.Commitments is null:
                // Opened before the commitment state was wired: channel_ready overwrote the peer's point of its
                // current commitment (NL-232), so no snapshot can be built and HTLC messages are refused on it
                _logger.LogWarning(
                    "Channel {ChannelId} has no commitment state (opened before HTLC support): HTLCs are not possible on it",
                    channel.ChannelId);
                break;
            case ChannelState.Open:
                // Not usable until channel_reestablish on the peer's next connection (OnPeerConnectedAsync, N7)
                break;
            case ChannelState.V1FundingSigned:
                // The funding confirmation resumes from the persisted watch (ConfirmUnconfirmedChannels on the next
                // block, or the blockchain monitor when it reaches the depth)
                _logger.LogInformation("Waiting for the funding of channel {ChannelId} to confirm", channel.ChannelId);
                break;
            case ChannelState.ReadyForThem or ChannelState.ReadyForUs:
                _logger.LogInformation("Waiting for channel {ChannelId} to be ready", channel.ChannelId);
                break;
            case ChannelState.ShuttingDown or ChannelState.Negotiating:
                // The close resumes on the peer's next connection: channel_reestablish re-sends our shutdown
                // (B2-RE-28) and the fee negotiation restarts (B2-RE-29). The peer may broadcast a proposal we signed
                // meanwhile, so the funding output is watched for a spend
                _logger.LogInformation("Channel {ChannelId} is {State}; the close resumes when the peer reconnects",
                                       channel.ChannelId, Enum.GetName(channel.State));
                WatchFundingSpend(channel);
                break;
            case ChannelState.Closing:
                // The agreed closing transaction is persisted and watched; rebroadcast it in case it never went out
                WatchFundingSpend(channel);
                await ResumeClosingAsync(scope, channel);
                break;
            case ChannelState.Failed:
                // Kept in memory so its messages are ignored; its error is re-sent on every connection (B2-RE-05)
                _logger.LogWarning("Channel {ChannelId} was failed; every update on it is refused",
                                   channel.ChannelId);
                break;
            case ChannelState.OnchainResolving:
                // A commitment spent its funding output: the on-chain executor resolves its outputs on every block
                // (BOLT 5); its error is re-sent on every connection like a failed channel's
                _logger.LogWarning("Channel {ChannelId} is resolving on chain; every update on it is refused",
                                   channel.ChannelId);
                break;
        }
    }

    /// <summary>
    /// The lowest local commitment number the channel has a persisted <see cref="BroadcastPurpose.LocalCommitment"/>
    /// broadcast for (the fail-the-channel save, NL-271), or null. The signer's invariant S1 is restored from it
    /// (NL-297). A read failure is logged: the mark is defense in depth (a Failed channel refuses every update anyway).
    /// </summary>
    private async Task<ulong?> GetBroadcastSignedCommitmentNumberAsync(IServiceScope scope, ChannelModel channel)
    {
        if (channel.State < ChannelState.V1FundingSigned)
            return null;

        try
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var broadcasts = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channel.ChannelId);
            var numbers = broadcasts.Where(b => b.Purpose == BroadcastPurpose.LocalCommitment)
                                    .Select(b => b.CommitmentNumber)
                                    .OfType<ulong>()
                                    .ToList();
            return numbers.Count == 0 ? null : numbers.Min();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not read the commitment broadcasts of channel {ChannelId}", channel.ChannelId);
            return null;
        }
    }

    /// <summary>
    /// Decides what a channel loaded at startup resumes as (BOLT2 plan N7-T5), before it is registered.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Closed and Stale channels, and states before funding_created (never persisted), are not registered.</item>
    /// <item>V1FundingCreated is the funder's crash window in <c>FundingSignedMessageHandler</c>: the channel is
    /// persisted, then the funding transaction is watched (persisted) and published, then V1FundingSigned is persisted.
    /// A persisted watch means the transaction may be out, so the channel moves on to V1FundingSigned and waits for
    /// the confirmation. Without one the transaction was never published, and BOLT 2 says a funder that has not
    /// broadcast the funding transaction SHOULD NOT remember the channel: it is persisted Stale and not registered
    /// (NL-048). Known gap: the blockchain monitor saves the watch before it publishes, so a crash or a failed publish
    /// in between leaves a watch for a transaction that never went out, and nothing rebroadcasts it yet.</item>
    /// <item>Every other state is registered as it is.</item>
    /// </list>
    /// </remarks>
    /// <returns>True when the channel must be registered.</returns>
    private async Task<bool> ResumeStartupStateAsync(IServiceScope scope, ChannelModel channel)
    {
        switch (channel.State)
        {
            case ChannelState.Closed or ChannelState.Stale:
                return false;
            case ChannelState.None or ChannelState.V1Opening or ChannelState.V2Opening:
                _logger.LogWarning("Channel {ChannelId} was stored before funding in state {State}; not remembered",
                                   channel.ChannelId, Enum.GetName(channel.State));
                return false;
            case ChannelState.V1FundingCreated:
                return await ResumeInterruptedFundingAsync(scope, channel);
            default:
                return true;
        }
    }

    private async Task<bool> ResumeInterruptedFundingAsync(IServiceScope scope, ChannelModel channel)
    {
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var watched = channel.FundingOutput?.TransactionId is { } fundingTxId
                          ? await unitOfWork.WatchedTransactionDbRepository.GetByTransactionIdAsync(fundingTxId)
                          : null;

        var published = watched is not null && watched.ChannelId == channel.ChannelId;
        channel.UpdateState(published ? ChannelState.V1FundingSigned : ChannelState.Stale);
        await unitOfWork.ChannelDbRepository.UpdateAsync(channel);

        // NL-259: a forgotten funder channel gives its funding up in the same save (a pending funding row would keep
        // its inputs out of coin selection, NL-385, and be sent after every block)
        var abandoned = 0;
        if (!published)
        {
            foreach (var broadcast in await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(
                                          channel.ChannelId))
            {
                if (broadcast.Purpose is BroadcastPurpose.Funding or BroadcastPurpose.Unspecified
                 && await unitOfWork.BroadcastTransactionDbRepository.MarkAbandonedAsync(broadcast.TransactionId))
                    abandoned++;
            }
        }

        await unitOfWork.SaveChangesAsync();

        if (published)
        {
            _logger.LogInformation(
                "Channel {ChannelId} was stopped while its funding transaction was being published; waiting for it to confirm",
                channel.ChannelId);
            return true;
        }

        // ... and releases the wallet outputs locked to it (the locks live in memory only)
        var released = _serviceProvider.GetService<IUtxoMemoryRepository>()?.ReturnUtxosNotSpentOnChannel(
                           channel.ChannelId).Count ?? 0;

        _logger.LogWarning(
            "Channel {ChannelId} was stopped before its funding transaction was published; forgetting it (BOLT 2: a funder that has not broadcast SHOULD NOT remember the channel); {Released} wallet output(s) released, {Abandoned} funding broadcast(s) abandoned",
            channel.ChannelId, released, abandoned);
        return false;
    }

    /// <summary>
    /// Reverts the peer's uncommitted updates of a reloaded channel (<see cref="Domain.Channels.Commitments.ChannelCommitments.RevertUncommitted"/>)
    /// and persists the transition when it changed anything. A failure is logged: the channel stays with its stored
    /// snapshot, and its peer's updates will be refused as repeats until the next attempt.
    /// </summary>
    private async Task RevertUncommittedAsync(ChannelModel channel)
    {
        var result = channel.Commitments!.RevertUncommitted();
        if (result.Transition.IsEmpty)
            return;

        // NL-279: adds the peer sends again with the dropped ids come after our shutdown too; the lowered boundary is
        // saved with the revert
        var boundary = channel.FirstRemoteHtlcIdAfterLocalShutdown;
        var boundaryLowered = channel.LowerFirstRemoteHtlcIdAfterLocalShutdown(result.Next.RemoteNextHtlcId);
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ChannelStateDbRepository.ApplyAsync(result.Next, result.Transition);
            if (boundaryLowered)
                await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
            await unitOfWork.SaveChangesAsync();
            channel.UpdateCommitments(result.Next);
            _channelMemoryRepository.UpdateChannel(channel);

            _logger.LogInformation("Reverted {Count} uncommitted peer update(s) of channel {ChannelId}",
                                   result.Transition.DroppedHtlcs.Count + result.Transition.UpsertedHtlcs.Count,
                                   channel.ChannelId);
        }
        catch (Exception e)
        {
            if (boundaryLowered)
                channel.RestoreFirstRemoteHtlcIdAfterLocalShutdown(boundary);
            _logger.LogError(e, "Failed to revert the uncommitted peer updates of channel {ChannelId}",
                             channel.ChannelId);
        }
    }

    /// <summary>
    /// Queues the events still pending in a reloaded channel (<see cref="ChannelDomainEvents.DerivePending(Domain.Channels.Commitments.ChannelCommitments, IEnumerable{Domain.Channels.Commitments.HtlcRecord})"/>
    /// over its open HTLCs and the settled ones not pruned yet) for the switch. A channel whose records can't be read
    /// or derived (legacy states) is logged and skipped: it must not stop the other channels.
    /// </summary>
    private async Task QueuePendingDomainEventsAsync(IServiceScope scope, ChannelModel channel)
    {
        if (channel.State is not (ChannelState.Open or ChannelState.ShuttingDown))
            return;

        try
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var persisted = await unitOfWork.ChannelStateDbRepository.LoadAsync(channel.ChannelId,
                                                                                channel.Commitments!.Params);
            var events = ChannelDomainEvents.DerivePending(channel.Commitments, persisted?.SettledHtlcs);
            if (events.Count == 0)
                return;

            scope.ServiceProvider.GetRequiredService<ChannelDomainEventQueue>().Enqueue(events);
            _logger.LogInformation("Replaying {Count} pending HTLC event(s) of channel {ChannelId}", events.Count,
                                   channel.ChannelId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not derive the pending HTLC events of channel {ChannelId}", channel.ChannelId);
        }
    }

    /// <inheritdoc />
    public async Task StartOpeningChannelAsync(CompactPubKey peerPubKey, ChannelModel channel,
                                               IChannelMessage openChannelMessage)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(openChannelMessage);

        using var channelLock = await _channelLockProvider.AcquireAsync(channel.ChannelId);

        _channelMemoryRepository.AddTemporaryChannel(peerPubKey, channel);
        RaiseResponseMessages(peerPubKey, [openChannelMessage]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The channel's lock (keyed by the message's channel id, or temporary_channel_id) is held from before the handler
    /// runs until its replies have been raised through <see cref="OnResponseMessageReady"/>, so two messages for one
    /// channel never run concurrently (with each other or with a block event) and their replies keep persist order.
    /// Every <see cref="ChannelErrorException"/> or <see cref="ChannelWarningException"/> leaving this method carries
    /// the channel id (or temporary_channel_id) of <paramref name="message"/>, so the peer gets an `error`/`warning`
    /// scoped to that channel. BOLT 1: an `error` with an all-zero channel_id tells the peer to fail every channel with
    /// us, so a channel-scoped failure must never lose its channel id.
    /// A processed channel_reestablish pins the channel's link to this connection, replays the channel's pending HTLC
    /// events and schedules a signature for what is pending (after the lock; N7, NL-252).
    /// </remarks>
    public async Task HandleChannelMessageAsync(IChannelMessage message, FeatureOptions negotiatedFeatures,
                                                CompactPubKey peerPubKey)
    {
        var channelId = message.Payload.ChannelId;

        // One scope per message, created outside the lock so the transitions' domain events can be handed to the HTLC
        // switch after the lock is released
        using var scope = _serviceProvider.CreateScope();
        var reestablished = false;
        PreparedChannelFailure? preparedFailure = null;
        try
        {
            using (await AcquireMessageLocksAsync(message, channelId))
            {
                var wasOpen = IsOpen(channelId);

                IReadOnlyList<IChannelMessage> replies;
                try
                {
                    replies = await DispatchChannelMessageAsync(scope, message, channelId, negotiatedFeatures,
                                                                peerPubKey);
                }
                catch (ChannelFailedException cfe)
                {
                    // Persist Failed and the error before it is sent (N6-T3 contract), still under the lock; with
                    // MustBroadcast the commitment's broadcast row goes in the same save (NL-271)
                    preparedFailure = await PersistFailedChannelAsync(scope, cfe);
                    throw;
                }

                if (!wasOpen)
                    MarkLinkUpIfOpened(channelId, peerPubKey);

                if (message.Type == MessageTypes.ChannelReestablish)
                    reestablished = await CompleteReestablishAsync(scope, channelId, peerPubKey);

                // BOLT 7 (G1-T4): our announcement_signatures after the reestablish (retransmission on reconnection)
                // or when the channel just turned Open at the announcement depth
                var announcementDue = message.Type == MessageTypes.ChannelReestablish
                                          ? reestablished || GetTracker() is null
                                          : !wasOpen && IsOpen(channelId);
                if (announcementDue)
                    replies = await AppendAnnouncementSignaturesAsync(scope, channelId, peerPubKey, replies);

                replies = await AdvanceCloseAsync(scope, channelId, replies);
                RaiseResponseMessages(peerPubKey, replies);

                if (announcementDue)
                    await CompleteAnnouncementAsync(scope, channelId);
            }

            if (reestablished)
                ScheduleCommit(channelId);
        }
        catch (ChannelFailedException cfe) when (cfe.MustBroadcast)
        {
            // Failed, the error and (NL-271) the commitment's broadcast row are persisted; the lock is released now,
            // so the commitment is published (BOLT 2 B2-RE-14; N9-T4). Without a prepared failure (no failure
            // service under the lock) the failure service takes the lock itself
            await BroadcastFailedChannelAsync(cfe, preparedFailure);
            throw;
        }
        catch (ChannelErrorException cee) when (!IsChannelScoped(cee.ChannelId) && IsChannelScoped(channelId))
        {
            throw new ChannelErrorException(cee.Message, channelId, cee, cee.PeerMessage);
        }
        catch (ChannelWarningException cwe) when (!IsChannelScoped(cwe.ChannelId) && IsChannelScoped(channelId))
        {
            throw new ChannelWarningException(cwe.Message, channelId, cwe, cwe.PeerMessage)
            {
                CloseConnection = cwe.CloseConnection
            };
        }
        finally
        {
            // Events of the transitions that were persisted, even when a later step failed (they are also re-derived
            // from the persisted states on startup)
            await RaiseDomainEventsAsync(scope);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Splicing plan SP1-B-T2 (SP-OP-05/06/07): the whole batch is handled under one acquisition of the channel's lock,
    /// with the same gates as a single <c>commitment_signed</c> (failed channel, B2-RE-07, unknown channel, the
    /// updatable-channel guard) and the same failure handling as <see cref="HandleChannelMessageAsync"/>;
    /// <see cref="ChannelStateTransitionService.ReceiveCommitmentSignedBatchAsync"/> verifies every member before
    /// anything changes, persists, then answers with one <c>revoke_and_ack</c> (and our own signature when due).
    /// </remarks>
    public async Task HandleCommitmentSignedBatchAsync(Domain.Protocol.Models.CommitmentSignedBatch batch,
                                                       FeatureOptions negotiatedFeatures,
                                                       CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var channelId = batch.ChannelId;

        using var scope = _serviceProvider.CreateScope();
        PreparedChannelFailure? preparedFailure = null;
        try
        {
            using (await _channelLockProvider.AcquireAsync(channelId))
            {
                IReadOnlyList<IChannelMessage> replies;
                try
                {
                    // Batches carry splice commitments, whose persistence lands with SP1-C (splicing plan SP1-C-T4):
                    // until option_splice (experimental) is negotiated nothing a batch holds is accepted
                    if (negotiatedFeatures.OptionSplice == FeatureSupport.No)
                        throw new ChannelWarningException(
                            $"[SP-OP-05] commitment_signed batch for {channelId} without option_splice",
                            channelId, "start_batch of commitment_signed without option_splice")
                        { CloseConnection = true };

                    if (batch.Messages.Count == 0 || batch.Messages.Any(m => m.Payload.ChannelId != channelId))
                        throw new ChannelWarningException(
                            $"[SP-OP-04] commitment_signed batch for {channelId} holds another channel's message",
                            channelId, "start_batch holds a message for another channel")
                        { CloseConnection = true };

                    _channelMemoryRepository.TryGetChannelState(channelId, out var currentState);
                    if (currentState is ChannelState.Failed or ChannelState.OnchainResolving
                     && _channelMemoryRepository.TryGetChannel(channelId, out var failed))
                        throw new ChannelFailedException(channelId,
                                                         $"Ignoring a commitment_signed batch on failed channel {channelId}",
                                                         await GetStoredErrorTextAsync(scope, failed));

                    ThrowIfNotReestablished(channelId, currentState, MessageTypes.CommitmentSigned);
                    await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);

                    var transitions = scope.ServiceProvider.GetRequiredService<ChannelStateTransitionService>();
                    var channel = transitions.GetUpdatableChannel(channelId, currentState, "commitment_signed");
                    replies = await transitions.ReceiveCommitmentSignedBatchAsync(channel, batch);
                }
                catch (ChannelFailedException cfe)
                {
                    // Persist Failed and the error before it is sent, still under the lock (N6-T3, NL-271)
                    preparedFailure = await PersistFailedChannelAsync(scope, cfe);
                    throw;
                }

                replies = await AdvanceCloseAsync(scope, channelId, replies);
                RaiseResponseMessages(peerPubKey, replies);
            }
        }
        catch (ChannelFailedException cfe) when (cfe.MustBroadcast)
        {
            await BroadcastFailedChannelAsync(cfe, preparedFailure);
            throw;
        }
        catch (ChannelErrorException cee) when (!IsChannelScoped(cee.ChannelId) && IsChannelScoped(channelId))
        {
            throw new ChannelErrorException(cee.Message, channelId, cee, cee.PeerMessage);
        }
        catch (ChannelWarningException cwe) when (!IsChannelScoped(cwe.ChannelId) && IsChannelScoped(channelId))
        {
            throw new ChannelWarningException(cwe.Message, channelId, cwe, cwe.PeerMessage)
            {
                CloseConnection = cwe.CloseConnection
            };
        }
        finally
        {
            await RaiseDomainEventsAsync(scope);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Each channel is handled under its own lock (one at a time, never two). Failed channels get no
    /// channel_reestablish (BOLT 2: "retransmit the error packet and ignore any other packets for that channel").
    /// Every other channel past funding_signed sends its channel_reestablish now (BOLT 2: "MUST transmit
    /// channel_reestablish for each channel"), channels still waiting for channel_ready included: the exchange is what
    /// retransmits a channel_ready lost with the previous connection (both next_commitment_numbers 1), so two nodes
    /// running this code never stay ReadyForUs/ReadyForThem. A channel whose reestablish can't be built is logged and
    /// skipped: it stays unusable on this connection, the others go on.
    /// </remarks>
    public async Task<IReadOnlyList<ErrorMessage>> OnPeerConnectedAsync(CompactPubKey peerPubKey)
    {
        var tracker = GetTracker();
        tracker?.ResetPeer(peerPubKey);

        // BOLT 7: announcement_signatures are sent again on every reconnection (G1-T4)
        _serviceProvider.GetService<IChannelAnnouncementService>()?.OnPeerConnectionChanged(peerPubKey);

        var errors = new List<ErrorMessage>();
        foreach (var channel in GetPeerChannels(peerPubKey))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                using var channelLock = await _channelLockProvider.AcquireAsync(channel.ChannelId);

                // BOLT 2: the closing negotiation restarts on every reconnection (B2-RE-29)
                _serviceProvider.GetService<ClosingNegotiationRegistry>()?.ResetConnection(channel.ChannelId);
                switch (channel.State)
                {
                    case ChannelState.Failed or ChannelState.OnchainResolving:
                        // A channel restored from a static backup first asks the peer to force close with the BOLT 2
                        // "we lost data" channel_reestablish (B2-RE-14), then gets its error like any failed channel
                        if (RecoveryChannels.IsRecoveryChannel(channel))
                        {
                            RaiseResponseMessages(peerPubKey, [CreateDataLossReestablish(scope, channel)]);
                            _logger.LogWarning("Asking peer {Peer} to force close recovery channel {ChannelId}",
                                               peerPubKey, channel.ChannelId);
                        }

                        errors.Add(await GetStoredErrorAsync(scope, channel));
                        _logger.LogInformation("Re-sending the error of failed channel {ChannelId}",
                                               channel.ChannelId);
                        break;
                    case ChannelState.V1FundingSigned or ChannelState.ReadyForThem or ChannelState.ReadyForUs
                      or ChannelState.Open or ChannelState.ShuttingDown or ChannelState.Negotiating
                      or ChannelState.Closing:
                        if (channel.State is ChannelState.Open or ChannelState.ShuttingDown
                         && channel.Commitments is not null)
                            await RevertUncommittedAsync(channel);

                        var reestablishService = scope.ServiceProvider.GetRequiredService<ReestablishService>();
                        var reestablish = await reestablishService.CreateOwnAsync(channel);
                        tracker?.MarkSent(channel.ChannelId, peerPubKey);
                        RaiseResponseMessages(peerPubKey, [reestablish]);
                        break;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not start the reestablish of channel {ChannelId} with peer {Peer}",
                                 channel.ChannelId, peerPubKey);
            }
        }

        return errors;
    }

    /// <inheritdoc />
    public async Task OnPeerDisconnectedAsync(CompactPubKey peerPubKey)
    {
        // BOLT 2 quiescence (Q-R-04, NL-470): on disconnection the channels of the peer are no longer quiescent, also
        // those whose quiescence was not bound to a connection (requested while the peer was away, or without a peer
        // manager); a dependent protocol waiting for its quiescence learns it through the service
        _serviceProvider.GetService<IQuiescenceService>()?.OnPeerDisconnected(peerPubKey);

        foreach (var channel in GetPeerChannels(peerPubKey))
        {
            if (channel.Commitments is null)
                continue;

            try
            {
                using var channelLock = await _channelLockProvider.AcquireAsync(channel.ChannelId);
                if (channel.State is ChannelState.Open or ChannelState.ShuttingDown or ChannelState.Failed)
                    await RevertUncommittedAsync(channel);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not revert the uncommitted updates of channel {ChannelId}",
                                 channel.ChannelId);
            }
        }

        // BOLT 2 interactive-tx (splicing plan IT4-T1): a negotiation without our commitment_signed is forgotten on
        // disconnection (its wallet reservation released); a stored one stays for the reconnection
        if (_serviceProvider.GetService<IInteractiveTxDriver>() is not { } interactiveTxDriver)
            return;

        foreach (var channelId in interactiveTxDriver.GetChannels(peerPubKey))
        {
            try
            {
                using var channelLock = await _channelLockProvider.AcquireAsync(channelId);
                await interactiveTxDriver.OnDisconnectedAsync(channelId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not end the interactive-tx negotiation of channel {ChannelId} on "
                                  + "disconnection", channelId);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Waits for a <see cref="Publish"/> in progress (see there).</remarks>
    public void OnPeerConnectionChanged(CompactPubKey peerPubKey)
    {
        lock (_connectionGate)
            GetTracker()?.ResetPeer(peerPubKey);

        _serviceProvider.GetService<IChannelAnnouncementService>()?.OnPeerConnectionChanged(peerPubKey);
    }

    private List<ChannelModel> GetPeerChannels(CompactPubKey peerPubKey) =>
        _channelMemoryRepository.FindChannels(c => c.RemoteNodeId == peerPubKey);

    private ReestablishTracker? GetTracker() => _serviceProvider.GetService<ReestablishTracker>();

    /// <summary>
    /// True when the signer loads unknown channels from the database itself (an <see cref="IChannelSigningInfoSource"/>
    /// is registered, NL-067): then the manager never registers channels with it by hand (NL-343).
    /// </summary>
    private bool SignerLoadsChannels => _serviceProvider.GetService<IChannelSigningInfoSource>() is not null;

    /// <summary>
    /// After the peer's channel_reestablish was handled without failure: the channel is reestablished on this
    /// connection (unless the connection changed meanwhile), its link is pinned (NL-252) and its pending HTLC events
    /// are queued for the switch (raised after the lock). Call it under the channel's lock.
    /// </summary>
    /// <returns>True when the channel was reestablished now.</returns>
    private async Task<bool> CompleteReestablishAsync(IServiceScope scope, ChannelId channelId,
                                                      CompactPubKey peerPubKey)
    {
        var tracker = GetTracker();
        if (tracker is null || !tracker.TryMarkReestablished(channelId))
            return false;

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
         || channel.State is not (ChannelState.Open or ChannelState.ShuttingDown or ChannelState.Negotiating))
            return true;

        _serviceProvider.GetService<IPeerLivenessProbe>()?.MarkLinkUp(channelId, peerPubKey);
        if (channel.Commitments is not null)
            await QueuePendingDomainEventsAsync(scope, channel);

        // A stfu owed on this connection (a quiescence requested before the reestablish, NL-470) may go out now: the
        // release runs after this lock, behind the reestablish's replies
        _serviceProvider.GetService<IStfuReleaseScheduler>()?.ScheduleRelease(channelId);

        _logger.LogInformation("Channel {ChannelId} reestablished with peer {Peer}", channelId, peerPubKey);
        return true;
    }

    /// <summary>
    /// After a message was handled on a closing channel (BOLT2 plan N10): our deferred <c>shutdown</c>, the move to
    /// Negotiating once nothing is left, and the funder's opening <c>closing_signed</c>
    /// (<see cref="ChannelCloseCoordinator.AdvanceAsync"/>), appended to the handler's replies. Call it under the
    /// channel's lock. A failure is logged (the next message retries): the peer did nothing wrong.
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> AdvanceCloseAsync(IServiceScope scope, ChannelId channelId,
                                                                        IReadOnlyList<IChannelMessage> replies)
    {
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
         || !ChannelCloseCoordinator.IsNegotiationPhase(channel.State)
         || scope.ServiceProvider.GetService<ChannelCloseCoordinator>() is not { } coordinator)
            return replies;

        try
        {
            var more = await coordinator.AdvanceAsync(channel);
            return more.Count == 0 ? replies : replies.Concat(more).ToList();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not move the close of channel {ChannelId} on", channelId);
            return replies;
        }
    }

    /// <summary>
    /// Startup of a <see cref="ChannelState.Closing"/> channel. Its agreed transaction is persisted with its watch (the
    /// monitor reloads the watch): a watch that already completed (the close was confirmed but not recorded) closes the
    /// channel now; a missing watch (saved by an older build after Closing, and lost in a crash) is created again;
    /// otherwise the transaction is rebroadcast. A failed rebroadcast (already confirmed or in the mempool) is logged.
    /// Call it under the channel's lock.
    /// </summary>
    private async Task ResumeClosingAsync(IServiceScope scope, ChannelModel channel)
    {
        if (channel.ClosingTransaction is not { } closingTransaction)
        {
            _logger.LogWarning("Channel {ChannelId} is closing without a stored closing transaction",
                               channel.ChannelId);
            return;
        }

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var watch = await unitOfWork.WatchedTransactionDbRepository.GetByTransactionIdAsync(closingTransaction.TxId);
        if (watch is { IsCompleted: true })
        {
            await CompleteCloseAsync(scope, channel, watch.FirstSeenAtHeight);
            return;
        }

        if (watch is null)
        {
            _logger.LogWarning("Closing transaction {TxId} of channel {ChannelId} was not watched; watching it again",
                               closingTransaction.TxId, channel.ChannelId);
            await _blockchainMonitor.WatchTransactionAsync(channel.ChannelId, closingTransaction.TxId,
                                                           GetCloseOptions().ConfirmationDepth);
        }

        _logger.LogInformation("Channel {ChannelId} is waiting for closing transaction {TxId} to confirm",
                               channel.ChannelId, closingTransaction.TxId);
        if (scope.ServiceProvider.GetService<IBitcoinChainService>() is not { } chainService)
            return;

        try
        {
            await chainService.SendTransactionAsync(Transaction.Load(closingTransaction.RawTxBytes, Network.Main));
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Rebroadcast of closing transaction {TxId} failed", closingTransaction.TxId);
        }
    }

    /// <summary>
    /// The mutual close transaction reached its depth: the channel is <see cref="ChannelState.Closed"/>, persisted,
    /// and forgotten in memory (and in the close registry). Call it under the channel's lock.
    /// </summary>
    private async Task CompleteCloseAsync(IServiceScope scope, ChannelModel channel, uint? closingTxHeight)
    {
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // The accounting feed's mutual close (NL-602) rides in the save that makes the channel Closed (once: a Closed
        // channel never comes here again); its balance is read before the state moves
        ChannelAccountingEvents.StageMutualClose(unitOfWork, channel, closingTxHeight,
                                                 (_serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System)
                                                .GetUtcNow(), _logger);
        channel.UpdateState(ChannelState.Closed);
        await unitOfWork.ChannelDbRepository.UpdateAsync(channel);

        // BOLT 2 interactive-tx (NL-470): the closed channel's negotiations can never finish, and the table has no FK
        // to Channels, so its rows go in the same save as the Closed state
        await unitOfWork.InteractiveTxSessionDbRepository.DeleteByChannelIdAsync(channel.ChannelId);

        // Liquidity ads (NL-771): the channel's purchases end with it, a close inside a lease noted
        await LiquidityLeases.StageChannelClosedAsync(unitOfWork, channel.ChannelId,
                                                      closingTxHeight ?? _blockchainMonitor.LastProcessedBlockHeight,
                                                      _logger);
        await unitOfWork.SaveChangesAsync();

        _channelMemoryRepository.TryRemoveChannel(channel.ChannelId);
        _serviceProvider.GetService<ClosingNegotiationRegistry>()?.Remove(channel.ChannelId);
        if (channel.FundingOutput is { TransactionId: { } fundingTxId, Index: { } fundingIndex })
            _blockchainMonitor.StopWatchingOutpointSpend(fundingTxId, fundingIndex);
        _logger.LogInformation("Channel {ChannelId} is closed: closing transaction {TxId} confirmed",
                               channel.ChannelId, channel.ClosingTransaction?.TxId);
    }

    private ChannelCloseOptions GetCloseOptions() =>
        _serviceProvider.GetService<IOptions<ChannelCloseOptions>>()?.Value ?? new ChannelCloseOptions();

    /// <summary>Watches the funding output of a closing channel for a spend (<see cref="HandleWatchedOutpointSpent"/>).</summary>
    private void WatchFundingSpend(ChannelModel channel)
    {
        if (channel.FundingOutput is { TransactionId: { } fundingTxId, Index: { } fundingIndex })
            _blockchainMonitor.WatchOutpointSpend(channel.ChannelId, fundingTxId, fundingIndex);
    }

    private void HandleWatchedOutpointSpent(object? sender, OutpointSpentEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        _ = HandleFundingSpentAsync(args);
    }

    /// <summary>
    /// A watched outpoint of a channel was spent (BOLT 5 plan O2-T5). A spend of one of the outputs being resolved goes
    /// to <see cref="IOnchainResolutionExecutor"/>. A funding spend of a closing channel that is a mutual close of this
    /// channel (one input, the funding outpoint; final sequence and no lock time; every output pays one of the two
    /// shutdown scripts) that we did not record (the peer broadcast a proposal we signed and our connection or node
    /// went down before its <c>closing_signed</c> reached us, or it broadcast the other dust variant) becomes the
    /// channel's closing transaction: Closing and its watch (already seen in this block) in one save, so the watch's
    /// depth makes the channel Closed as usual. A Failed channel that signed a closing tx before it failed is closed
    /// by the same watch (NL-312): it stays Failed until the confirmation, then Closed. Any other funding spend goes to
    /// <see cref="IOnchainChannelWatcher"/> (classification, <c>OnchainResolving</c>), after this lock is released.
    /// Idempotent (a replayed block raises it again).
    /// </summary>
    private async Task HandleFundingSpentAsync(OutpointSpentEventArgs args)
    {
        var channelId = args.ChannelId;
        var spend = args.SpendingTransaction;
        try
        {
            var handOver = await RecordMutualCloseSpendAsync(args);
            switch (handOver)
            {
                case SpendHandOver.ResolutionOutput
                    when _serviceProvider.GetService<IOnchainResolutionExecutor>() is { } executor:
                    await executor.HandleOutputSpentAsync(args);
                    break;
                case SpendHandOver.FundingSpend
                    when _serviceProvider.GetService<IOnchainChannelWatcher>() is { } watcher:
                    await watcher.HandleFundingSpentAsync(args);
                    break;
                case SpendHandOver.FundingSpend or SpendHandOver.ResolutionOutput:
                    _logger.LogCritical(
                        "An output of channel {ChannelId} was spent by {TxId}, and no on-chain watcher is registered",
                        channelId, spend.TxId);
                    break;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not handle the spend of an output of channel {ChannelId} by {TxId}",
                             channelId, spend.TxId);
        }
    }

    /// <summary>
    /// NL-534: a pending splice attempt confirmed, so its RBF siblings, which spend the same funding output, can no
    /// longer confirm while it stays in the chain. Their broadcast rows are abandoned at once rather than at the lock
    /// (a few blocks later) or after 12 refusals: until then the chain monitor sent each after every block and bitcoind
    /// refused it (<c>bad-txns-inputs-missingorspent</c>). The attempt that confirmed keeps its pending row, so a reorg
    /// sends it again; the lock still discards the siblings' fundings. Under the channel's lock.
    /// </summary>
    private async Task AbandonLosingSpliceAttemptsAsync(ChannelId channelId, IReadOnlyList<ChannelFunding> siblings,
                                                        TxId winner)
    {
        if (siblings.Count == 0)
            return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var abandoned = new List<TxId>(siblings.Count);
            foreach (var sibling in siblings)
            {
                if (await unitOfWork.BroadcastTransactionDbRepository.MarkAbandonedAsync(sibling.FundingTxId))
                    abandoned.Add(sibling.FundingTxId);
            }

            if (abandoned.Count == 0)
                return;

            await unitOfWork.SaveChangesAsync();
            _logger.LogInformation("Splice {Winner} of channel {ChannelId} confirmed; its other attempts {Siblings} are "
                                 + "no longer rebroadcast", winner, channelId, string.Join(", ", abandoned));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The lock abandons them too
            _logger.LogWarning(e, "Could not abandon the other attempts of splice {Winner} of channel {ChannelId}",
                               winner, channelId);
        }
    }

    /// <summary>What <see cref="RecordMutualCloseSpendAsync"/> leaves to others.</summary>
    private enum SpendHandOver : byte
    {
        None = 0,
        FundingSpend = 1,
        ResolutionOutput = 2
    }

    /// <summary>
    /// Under the channel's lock: records an unrecorded mutual close of a closing channel, or of a Failed one that
    /// signed a closing tx before it failed (NL-312; see <see cref="HandleFundingSpentAsync"/>) and tells what else
    /// the spend needs.
    /// </summary>
    private async Task<SpendHandOver> RecordMutualCloseSpendAsync(OutpointSpentEventArgs args)
    {
        var channelId = args.ChannelId;
        var spend = args.SpendingTransaction;
        {
            using var channelLock = await _channelLockProvider.AcquireAsync(channelId);
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
             || channel.ClosingTransaction?.TxId == spend.TxId)
                return SpendHandOver.None;

            // A splice transaction of this channel spends the funding output (the current one, or the one a lock
            // replaced, on a replayed block) without closing it (splicing plan §3.6, FundingSpendKind.Splice): the
            // channel stays open on its fundings and the lock moves it (SP1). A failed channel, or one resolving on
            // chain (a recorded close the splice replaced after a reorg), hands it to the on-chain watcher: our
            // commitment must go out on the splice funding (splicing plan §3.6, SP2-C-T2)
            if (channel.State is not (ChannelState.Failed or ChannelState.OnchainResolving)
             && _serviceProvider.GetService<Splicing.Interfaces.ISpliceStatePort>() is { } splicePort
             && splicePort.GetFundings(channel) is { } fundings && fundings.Find(spend.TxId) is { } splice)
            {
                _logger.LogInformation("The funding output of channel {ChannelId} was spent by its splice {TxId}",
                                       channelId, spend.TxId);
                if (splice.Status == ChannelFundingStatus.Pending)
                    await AbandonLosingSpliceAttemptsAsync(channelId, fundings.Siblings(spend.TxId), spend.TxId);
                return SpendHandOver.None;
            }

            if (args.SpentTransactionId is { } spentTxId
             && (channel.FundingOutput?.TransactionId is not { } fundingTxId || spentTxId != fundingTxId
              || args.SpentOutputIndex != channel.FundingOutput.Index))
                return SpendHandOver.ResolutionOutput;

            // A Failed channel that signed a closing tx before it failed (Negotiating → Failed) is closed by it too
            // (NL-312): Failed (35) stays until the confirmation, then Closed (40) is strictly increasing
            if (channel.State is not (ChannelState.ShuttingDown or ChannelState.Negotiating or ChannelState.Closing
                                   or ChannelState.Failed)
             || !IsMutualCloseOf(channel, spend))
                return SpendHandOver.FundingSpend;

            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var watch = await unitOfWork.WatchedTransactionDbRepository.GetByTransactionIdAsync(spend.TxId);
            if (watch is null)
            {
                watch = new WatchedTransactionModel(channelId, spend.TxId, GetCloseOptions().ConfirmationDepth);
                watch.SetHeightAndIndex(args.BlockHeight, args.TransactionIndex);
                unitOfWork.WatchedTransactionDbRepository.Add(watch);
            }

            _logger.LogWarning(
                "Channel {ChannelId} ({State}) was closed on chain by mutual close {TxId} (we had {Stored}); waiting for its confirmation",
                channelId, Enum.GetName(channel.State), spend.TxId,
                channel.ClosingTransaction?.TxId.ToString() ?? "none");
            channel.SetClosingTransaction(spend);
            // NL-610: the protocol from the transaction's shape; the closer of a simple close from our output
            var (protocol, localIsCloser) = CloseTermsOf(channel, spend);
            channel.SetCloseTerms(protocol, localIsCloser);
            if (channel.State < ChannelState.Closing)
                channel.UpdateState(ChannelState.Closing);
            await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
            await unitOfWork.SaveChangesAsync();
            _channelMemoryRepository.UpdateChannel(channel);

            if (!watch.IsCompleted)
                _blockchainMonitor.TrackWatchedTransaction(watch);
            if (_serviceProvider.GetService<ClosingNegotiationRegistry>() is { } registry
             && registry.TryGet(channelId, out var entry))
                entry!.CompleteWaiters(spend.TxId);
        }

        return SpendHandOver.None;
    }

    /// <summary>
    /// True when <paramref name="spend"/> has the shape of a BOLT 3 mutual close of <paramref name="channel"/>: its only
    /// input is the funding outpoint. Legacy (<c>closing_signed</c>): sequence 0xFFFFFFFF, lock time 0 and every output
    /// pays one of the two shutdown scripts. <c>option_simple_close</c> (N11): sequence 0xFFFFFFFD, any lock time (the
    /// closer's choice), one or two outputs of which at most one pays a script other than ours: the peer may have
    /// replaced its script since (a later <c>closing_complete</c>'s <c>closer_scriptpubkey</c>, or its
    /// <c>shutdown</c> after a reconnection), so an earlier transaction we signed (the peer's proposal we answered,
    /// ours it completed, or our unanswered one it holds our signatures for) pays a script we no longer store. The
    /// funding output needs our signature and we sign that sequence only on a simple closing transaction (commitments
    /// carry 0x80 in the upper byte, the legacy close 0xFFFFFFFF), and our script never changes.
    /// </summary>
    internal static bool IsMutualCloseOf(ChannelModel channel, SignedTransaction spend)
    {
        if (channel.FundingOutput is not { TransactionId: { } fundingTxId, Index: { } fundingIndex }
         || channel.LocalShutdownScript is not { } localScript || channel.RemoteShutdownScript is not { } remoteScript)
            return false;

        Transaction transaction;
        try
        {
            transaction = Transaction.Load(spend.RawTxBytes, Network.Main);
        }
        catch (Exception)
        {
            return false;
        }

        if (transaction.Inputs.Count != 1 || transaction.Outputs.Count == 0)
            return false;

        var input = transaction.Inputs[0];
        var isLegacy = input.Sequence == Sequence.Final && transaction.LockTime == LockTime.Zero;
        var isSimple = (uint)input.Sequence
                    == Infrastructure.Bitcoin.Builders.ClosingTransactionBuilder.SimpleCloseSequence;
        if (input.PrevOut != new OutPoint(new uint256((byte[])fundingTxId), fundingIndex) || !(isLegacy || isSimple))
            return false;

        byte[] local = localScript;
        byte[] remote = remoteScript;
        if (isSimple)
            return transaction.Outputs.Count <= 2
                && transaction.Outputs.Count(o => !o.ScriptPubKey.ToBytes().AsSpan().SequenceEqual(local)) <= 1;

        return transaction.Outputs.All(o =>
        {
            var script = o.ScriptPubKey.ToBytes();
            return script.AsSpan().SequenceEqual(local) || script.AsSpan().SequenceEqual(remote);
        });
    }

    /// <summary>
    /// The close terms of a mutual close of <paramref name="channel"/> we did not record (NL-610): the protocol from the
    /// input's sequence (0xFFFFFFFF and lock time 0: legacy; <c>SimpleCloseSequence</c>: simple), and for a simple close
    /// the closer from our output (the closee's output is exactly its balance in whole satoshis; ours below that means
    /// we paid the fee; null when we have no output).
    /// </summary>
    internal static (MutualCloseProtocol? Protocol, bool? LocalIsCloser) CloseTermsOf(ChannelModel channel,
                                                                                     SignedTransaction spend)
    {
        Transaction transaction;
        try
        {
            transaction = Transaction.Load(spend.RawTxBytes, Network.Main);
        }
        catch (Exception)
        {
            return (null, null);
        }

        if (transaction.Inputs.Count != 1)
            return (null, null);

        var sequence = (uint)transaction.Inputs[0].Sequence;
        if (sequence == Sequence.Final && transaction.LockTime == LockTime.Zero)
            return (MutualCloseProtocol.Legacy, null);
        if (sequence != Infrastructure.Bitcoin.Builders.ClosingTransactionBuilder.SimpleCloseSequence)
            return (null, null);

        if (channel.LocalShutdownScript is not { } localScript)
            return (MutualCloseProtocol.Simple, null);

        byte[] local = localScript;
        var ourOutput = transaction.Outputs.FirstOrDefault(o => o.ScriptPubKey.ToBytes().AsSpan().SequenceEqual(local));
        return ourOutput is null
                   ? (MutualCloseProtocol.Simple, null)
                   : (MutualCloseProtocol.Simple,
                      (ulong)ourOutput.Value.Satoshi < channel.LocalBalance.MilliSatoshi / 1_000);
    }

    /// <summary>Asks the commit scheduler to sign what is pending (it checks the link and D7 itself).</summary>
    private void ScheduleCommit(ChannelId channelId)
    {
        if (_channelMemoryRepository.TryGetChannel(channelId, out var channel)
         && channel.Commitments is { HasPendingChangesForRemote: true })
            _serviceProvider.GetService<ICommitScheduler>()?.Schedule(channelId);
    }

    /// <summary>
    /// The <c>error</c> we sent when the channel failed, as stored (B2-RE-05), or a new one with the default text for a
    /// channel failed before the error was stored.
    /// </summary>
    private async Task<ErrorMessage> GetStoredErrorAsync(IServiceScope scope, ChannelModel channel)
    {
        if (channel.ErrorSent is { } stored)
        {
            try
            {
                var serializer = scope.ServiceProvider.GetRequiredService<IMessageSerializer>();
                using var stream = new MemoryStream(stored.ToArray(), false);
                if (await serializer.DeserializeMessageAsync(stream) is ErrorMessage errorMessage)
                    return errorMessage;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "The stored error of channel {ChannelId} can't be read", channel.ChannelId);
            }
        }

        return new ErrorMessage(new ErrorPayload(channel.ChannelId, ChannelFailedException.DefaultPeerMessage));
    }

    /// <summary>
    /// The data-loss <c>channel_reestablish</c> of a recovery channel (<see cref="RecoveryChannels"/>), with our point
    /// of commitment 0 (our real current point is unknown; the peer only needs the numbers to fail the channel), or
    /// our payment basepoint when the signer can't give it.
    /// </summary>
    private ChannelReestablishMessage CreateDataLossReestablish(IServiceScope scope, ChannelModel channel)
    {
        CompactPubKey point;
        try
        {
            point = _lightningSigner.GetPerCommitmentPoint(channel.ChannelId, 0);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "No per-commitment point for recovery channel {ChannelId}", channel.ChannelId);
            point = channel.LocalKeySet.PaymentCompactBasepoint;
        }

        return RecoveryChannels.CreateDataLossReestablish(scope.ServiceProvider.GetRequiredService<IMessageFactory>(),
                                                          channel.ChannelId, point);
    }

    /// <summary>The text of the channel's stored <c>error</c> (to send it again in reply to a message).</summary>
    private async Task<string> GetStoredErrorTextAsync(IServiceScope scope, ChannelModel channel)
    {
        var error = await GetStoredErrorAsync(scope, channel);
        return error.Payload.Data is { Length: > 0 } data
                   ? Encoding.UTF8.GetString(data)
                   : ChannelFailedException.DefaultPeerMessage;
    }

    /// <summary>
    /// The lock(s) a message runs under: its channel id, and for funding_created also the real channel id the handler
    /// is about to create (NL-235). The fundee's handler adds the channel to memory and starts watching the funding tx
    /// under the real id, so a block event for that id (funding confirmation) must wait until the handler is done.
    /// </summary>
    /// <remarks>
    /// This is the only place two channel locks are held, always temporary then real. It cannot deadlock: nothing else
    /// holds two locks, and nothing can hold the brand-new real id while waiting for this temporary one.
    /// </remarks>
    private async Task<IDisposable> AcquireMessageLocksAsync(IChannelMessage message, ChannelId channelId)
    {
        var channelLock = await _channelLockProvider.AcquireAsync(channelId);
        if (message is not FundingCreatedMessage fundingCreated
         || _serviceProvider.GetService(typeof(IChannelIdFactory)) is not IChannelIdFactory channelIdFactory)
            return channelLock;

        try
        {
            var realChannelId = channelIdFactory.CreateV1(fundingCreated.Payload.FundingTxId,
                                                          fundingCreated.Payload.FundingOutputIndex);
            if (realChannelId == channelId)
                return channelLock;

            var realChannelLock = await _channelLockProvider.AcquireAsync(realChannelId);
            return new CompositeLock(realChannelLock, channelLock);
        }
        catch
        {
            channelLock.Dispose();
            throw;
        }
    }

    /// <summary>Releases the inner locks in the given order.</summary>
    private sealed class CompositeLock(params IDisposable[] locks) : IDisposable
    {
        public void Dispose()
        {
            foreach (var channelLock in locks)
                channelLock.Dispose();
        }
    }

    /// <summary>
    /// Hands the domain events of the persisted transitions to the HTLC switch, outside every channel lock (the switch
    /// may change channels through <c>IChannelOperations</c>, which take the locks). Without a registered switch they
    /// stay pending in the persisted HTLC states and are replayed on startup. A failing switch is logged: the events
    /// are re-derivable, and the peer must not be disconnected for our own follow-up work.
    /// </summary>
    private async Task RaiseDomainEventsAsync(IServiceScope scope)
    {
        var eventQueue = scope.ServiceProvider.GetService<ChannelDomainEventQueue>();
        if (eventQueue is null)
            return;

        var events = eventQueue.Drain();
        if (events.Count == 0)
            return;

        var htlcSwitch = scope.ServiceProvider.GetService<IHtlcSwitch>();
        if (htlcSwitch is null)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("No HTLC switch registered, {Count} channel event(s) stay pending", events.Count);
            return;
        }

        foreach (var channelEvent in events)
        {
            try
            {
                await htlcSwitch.HandleAsync(channelEvent, CancellationToken.None);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "HTLC switch failed on {Event} for HTLC {HtlcId} of channel {ChannelId}",
                                 channelEvent.GetType().Name, channelEvent.HtlcId, channelEvent.ChannelId);
            }
        }
    }

    /// <summary>
    /// Publishes the commitment of a failure that must broadcast it: completes the failure prepared under the lock, or,
    /// without one, hands the failure to <see cref="IChannelFailureService"/> (which takes the lock itself). Call it
    /// without holding any channel lock. A missing service (tests) or a failed broadcast is logged; the error still goes
    /// out through the exception.
    /// </summary>
    private async Task BroadcastFailedChannelAsync(ChannelFailedException failure, PreparedChannelFailure? prepared)
    {
        var failureService = _serviceProvider.GetService<IChannelFailureService>();
        if (failureService is null)
        {
            _logger.LogWarning("No channel failure service: channel {ChannelId} is failed without a broadcast",
                               failure.FailedChannelId);
            return;
        }

        try
        {
            var outcome = prepared is not null
                              ? await failureService.CompleteFailureAsync(prepared, sendError: false)
                              : await failureService.FailChannelAsync(failure);
            _logger.LogWarning("Failed channel {ChannelId}: {Status} {TxId}", failure.FailedChannelId, outcome.Status,
                               outcome.CommitmentTxId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Broadcasting the commitment of failed channel {ChannelId} failed",
                             failure.FailedChannelId);
        }
    }

    /// <summary>
    /// Fails a channel (BOLT2 plan N6-T3, D10): persists <see cref="ChannelState.Failed"/> and the <c>error</c> it will
    /// be sent (re-sent on reconnection, B2-RE-05) before the exception reaches the send path. With
    /// <see cref="ChannelFailedException.MustBroadcast"/> the failure service does it under this lock, together with the
    /// broadcast row of our signed commitment (NL-271), and the returned failure is published after the lock. A failure
    /// to persist is logged; the error is still sent. Call it under the channel's lock.
    /// </summary>
    private async Task<PreparedChannelFailure?> PersistFailedChannelAsync(IServiceScope scope,
                                                                          ChannelFailedException failure)
    {
        var channelId = failure.FailedChannelId;
        _logger.LogCritical(failure, "Failing channel {ChannelId} ({RequirementId}); broadcast needed: {MustBroadcast}",
                            channelId, failure.RequirementId, failure.MustBroadcast);

        try
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
                return null;

            // Already failed and stored (a message on a failed channel gets the stored error again)
            if (channel.State == ChannelState.Failed && channel.ErrorSent is not null && !failure.MustBroadcast)
                return null;

            // The closing transaction is agreed and out: the channel ends Closed when it confirms, never Failed; a
            // channel resolving on chain is past failing
            if (channel.State is ChannelState.Closing or ChannelState.OnchainResolving)
            {
                _logger.LogWarning("Channel {ChannelId} is {State}; it is not marked failed", channelId,
                                   Enum.GetName(channel.State));
                return null;
            }

            if (failure.MustBroadcast && _serviceProvider.GetService<IChannelFailureService>() is { } failureService)
            {
                var prepared = await failureService.PrepareFailureUnderLockAsync(
                                   channelId, ChannelFailureService.ToRequest(failure));
                if (prepared is not null)
                    return prepared;
            }

            if (channel.State == ChannelState.Failed && channel.ErrorSent is not null)
                return null;

            var messageFactory = scope.ServiceProvider.GetRequiredService<IMessageFactory>();
            var messageSerializer = scope.ServiceProvider.GetRequiredService<IMessageSerializer>();
            var errorMessage = messageFactory.CreateErrorMessage(failure.PeerMessage!, channelId);
            using var errorStream = new MemoryStream();
            await messageSerializer.SerializeAsync(errorMessage, errorStream);

            if (channel.State < ChannelState.Failed)
                channel.UpdateState(ChannelState.Failed);
            channel.MarkErrorSent(errorStream.ToArray());

            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
            await unitOfWork.SaveChangesAsync();
            _channelMemoryRepository.UpdateChannel(channel);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to persist the failed state of channel {ChannelId}", channelId);
        }

        return null;
    }

    private bool IsOpen(ChannelId channelId) =>
        _channelMemoryRepository.TryGetChannelState(channelId, out var state) && state == ChannelState.Open;

    /// <summary>
    /// A channel that just turned Open (both channel_ready exchanged, on this connection) can carry updates on the
    /// peer's current connection: pin it in the <see cref="IPeerLivenessProbe"/> and count it as reestablished there
    /// (channel_ready starts normal operation). A channel loaded at startup or reconnected is pinned after its
    /// channel_reestablish instead (<see cref="CompleteReestablishAsync"/>). Call it while holding the channel's lock.
    /// </summary>
    private void MarkLinkUpIfOpened(ChannelId channelId, CompactPubKey peerPubKey)
    {
        if (!IsOpen(channelId))
            return;

        // Opened on this connection: channel_ready is the start of normal operation, no reestablish needed on it
        GetTracker()?.MarkOpened(channelId, peerPubKey);
        _serviceProvider.GetService<IPeerLivenessProbe>()?.MarkLinkUp(channelId, peerPubKey);
    }

    /// <summary>
    /// Hands <paramref name="messages"/> to the subscribers in order. Call it while holding the channel's lock.
    /// </summary>
    private void RaiseResponseMessages(CompactPubKey peerPubKey, IReadOnlyList<IChannelMessage> messages)
    {
        foreach (var message in messages)
            OnResponseMessageReady?.Invoke(this, new ChannelResponseMessageEventArgs(peerPubKey, message));
    }

    /// <summary>
    /// True when <paramref name="channelId"/> names a single channel (it is set and not all-zero).
    /// </summary>
    private static bool IsChannelScoped(ChannelId? channelId)
    {
        return channelId is not null && channelId.Value != ChannelId.Zero;
    }

    private async Task<IReadOnlyList<IChannelMessage>> DispatchChannelMessageAsync(
        IServiceScope scope, IChannelMessage message, ChannelId channelId, FeatureOptions negotiatedFeatures,
        CompactPubKey peerPubKey)
    {
        // Check if the channel exists on the state dictionary
        _channelMemoryRepository.TryGetChannelState(channelId, out var currentState);

        // BOLT 2: a failed channel re-sends its error and ignores everything else (B2-RE-05); so does a channel
        // resolving on chain (BOLT 5 B5-GEN-04)
        if (currentState is ChannelState.Failed or ChannelState.OnchainResolving
         && _channelMemoryRepository.TryGetChannel(channelId, out var failed))
            throw new ChannelFailedException(channelId,
                                             $"Ignoring {Enum.GetName(message.Type)} on failed channel {channelId}",
                                             await GetStoredErrorTextAsync(scope, failed));

        if (IsNormalOperationMessage(message.Type) || IsCloseMessage(message.Type))
            ThrowIfNotReestablished(channelId, currentState, message.Type);

        // BOLT 2 quiescence (Q-S-04): the peer MUST NOT send an update message after its stfu; warning + close
        ThrowIfUpdateAfterPeerStfu(channelId, message.Type);

        // In this case we can only handle messages that are opening a channel
        switch (message.Type)
        {
            case MessageTypes.OpenChannel:
                // Handle opening channel message
                var openChannel1Message = message as OpenChannel1Message
                                       ?? throw new ChannelErrorException("Error boxing message to OpenChannel1Message",
                                                                          "Sorry, we had an internal error");
                return await GetChannelMessageHandler<OpenChannel1Message>(scope)
                          .HandleAsync(openChannel1Message, currentState, negotiatedFeatures, peerPubKey);

            case MessageTypes.AcceptChannel:
                // Handle the accept channel message
                var acceptChannel1Message = message as AcceptChannel1Message
                                         ?? throw new ChannelErrorException(
                                                "Error boxing message to AcceptChannel1Message",
                                                "Sorry, we had an internal error");
                return await GetChannelMessageHandler<AcceptChannel1Message>(scope)
                          .HandleAsync(acceptChannel1Message, currentState, negotiatedFeatures, peerPubKey);

            case MessageTypes.FundingCreated:
                // Handle the funding-created message
                var fundingCreatedMessage = message as FundingCreatedMessage
                                         ?? throw new ChannelErrorException(
                                                "Error boxing message to FundingCreatedMessage",
                                                "Sorry, we had an internal error");
                return await GetChannelMessageHandler<FundingCreatedMessage>(scope)
                          .HandleAsync(fundingCreatedMessage, currentState, negotiatedFeatures, peerPubKey);

            case MessageTypes.ChannelReady:
                // Handle channel ready message
                var channelReadyMessage = message as ChannelReadyMessage
                                       ?? throw new ChannelErrorException("Error boxing message to ChannelReadyMessage",
                                                                          "Sorry, we had an internal error");
                return await GetChannelMessageHandler<ChannelReadyMessage>(scope)
                          .HandleAsync(channelReadyMessage, currentState, negotiatedFeatures, peerPubKey);

            // BOLT 2 channel establishment v2 (splicing plan wave DF): the accepter's and the opener's side
            case MessageTypes.OpenChannel2:
                return await GetChannelMessageHandler<OpenChannel2Message>(scope)
                          .HandleAsync(Cast<OpenChannel2Message>(message), currentState, negotiatedFeatures,
                                       peerPubKey);

            case MessageTypes.AcceptChannel2:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<AcceptChannel2Message>(scope)
                          .HandleAsync(Cast<AcceptChannel2Message>(message), currentState, negotiatedFeatures,
                                       peerPubKey);

            case MessageTypes.FundingSigned:
                // Handle funding signed message
                var fundingSignedMessage = message as FundingSignedMessage
                                        ?? throw new ChannelErrorException(
                                               "Error boxing message to FundingSignedMessage",
                                               "Sorry, we had an internal error");
                return await GetChannelMessageHandler<FundingSignedMessage>(scope)
                          .HandleAsync(fundingSignedMessage, currentState, negotiatedFeatures, peerPubKey);

            // BOLT 2 normal operation (plan N6-T1): the handlers run the commitment engine and persist every transition
            // before replying (ChannelStateTransitionService)
            case MessageTypes.UpdateAddHtlc:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<UpdateAddHtlcMessage>(scope)
                          .HandleAsync(Cast<UpdateAddHtlcMessage>(message), currentState, negotiatedFeatures,
                                       peerPubKey);

            case MessageTypes.UpdateFulfillHtlc:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<UpdateFulfillHtlcMessage>(scope)
                          .HandleAsync(Cast<UpdateFulfillHtlcMessage>(message), currentState, negotiatedFeatures,
                                       peerPubKey);

            case MessageTypes.UpdateFailHtlc:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<UpdateFailHtlcMessage>(scope)
                          .HandleAsync(Cast<UpdateFailHtlcMessage>(message), currentState, negotiatedFeatures,
                                       peerPubKey);

            case MessageTypes.UpdateFailMalformedHtlc:
                // A failure_code without the BADONION bit gets a warning and a disconnect (the handler checks it first)
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<UpdateFailMalformedHtlcMessage>(scope)
                          .HandleAsync(Cast<UpdateFailMalformedHtlcMessage>(message), currentState,
                                       negotiatedFeatures, peerPubKey);

            case MessageTypes.CommitmentSigned:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                // BOLT 2 splicing (SP-CS-01/02): the commitment_signed for a splice's new funding, sent after the
                // second tx_complete, is not a commitment update (same number, no revoke_and_ack)
                if (scope.ServiceProvider.GetService<ISpliceCommitmentReceiver>() is { } spliceReceiver
                 && spliceReceiver.IsSpliceCommitmentSigned(channelId, Cast<CommitmentSignedMessage>(message)))
                    return await spliceReceiver.HandleSpliceCommitmentSignedAsync(
                               Cast<CommitmentSignedMessage>(message), peerPubKey,
                               scope.ServiceProvider.GetRequiredService<IUnitOfWork>());

                // The first commitment_signed of a dual-funded open (wave DF) belongs to its negotiation
                if (await TryHandleDualFundCommitmentSignedAsync(scope, message, peerPubKey) is { } dualFundReplies)
                    return dualFundReplies;
                return await GetChannelMessageHandler<CommitmentSignedMessage>(scope)
                          .HandleAsync(Cast<CommitmentSignedMessage>(message), currentState, negotiatedFeatures,
                                       peerPubKey);

            case MessageTypes.RevokeAndAck:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<RevokeAndAckMessage>(scope)
                          .HandleAsync(Cast<RevokeAndAckMessage>(message), currentState, negotiatedFeatures,
                                       peerPubKey);

            case MessageTypes.UpdateFee:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<UpdateFeeMessage>(scope)
                          .HandleAsync(Cast<UpdateFeeMessage>(message), currentState, negotiatedFeatures, peerPubKey);

            // BOLT 2 message retransmission (plan N7)
            case MessageTypes.ChannelReestablish:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<ChannelReestablishMessage>(scope)
                          .HandleAsync(Cast<ChannelReestablishMessage>(message), currentState, negotiatedFeatures,
                                       peerPubKey);

            // BOLT 2 channel close (plan N10): shutdown and the legacy closing_signed negotiation
            case MessageTypes.Shutdown:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<ShutdownMessage>(scope)
                          .HandleAsync(Cast<ShutdownMessage>(message), currentState, negotiatedFeatures, peerPubKey);

            case MessageTypes.ClosingSigned:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<ClosingSignedMessage>(scope)
                          .HandleAsync(Cast<ClosingSignedMessage>(message), currentState, negotiatedFeatures,
                                       peerPubKey);

            // BOLT 2 option_simple_close (plan N11)
            case MessageTypes.ClosingComplete:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<ClosingCompleteMessage>(scope)
                          .HandleAsync(Cast<ClosingCompleteMessage>(message), currentState, negotiatedFeatures,
                                       peerPubKey);

            case MessageTypes.ClosingSig:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<ClosingSigMessage>(scope)
                          .HandleAsync(Cast<ClosingSigMessage>(message), currentState, negotiatedFeatures,
                                       peerPubKey);

            // BOLT 7 announcement_signatures of a public channel (plan G1-T3, NL-342)
            case MessageTypes.AnnouncementSignatures:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                return await GetChannelMessageHandler<AnnouncementSignaturesMessage>(scope)
                          .HandleAsync(Cast<AnnouncementSignaturesMessage>(message), currentState,
                                       negotiatedFeatures, peerPubKey);

            // BOLT 2 channel quiescence (splicing plan Q1-T1, NL-019): stfu is a channel message handled under the
            // channel's lock like the updates it stops; like them it needs the channel reestablished on this
            // connection (B2-RE-07)
            case MessageTypes.Stfu:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                ThrowIfNotReestablished(channelId, currentState, message.Type);
                return await GetChannelMessageHandler<StfuMessage>(scope)
                          .HandleAsync(Cast<StfuMessage>(message), currentState, negotiatedFeatures, peerPubKey);

            // BOLT 2 interactive transaction construction (splicing plan IT4-T2): the driver answers tx_abort when no
            // negotiation is in progress, and a failed negotiation ends with tx_abort, never with a channel failure
            case MessageTypes.TxAddInput:
                return await DispatchInteractiveTxMessageAsync<TxAddInputMessage>(scope, message, channelId,
                                                                                  currentState, negotiatedFeatures,
                                                                                  peerPubKey);
            case MessageTypes.TxAddOutput:
                return await DispatchInteractiveTxMessageAsync<TxAddOutputMessage>(scope, message, channelId,
                                                                                   currentState, negotiatedFeatures,
                                                                                   peerPubKey);
            case MessageTypes.TxRemoveInput:
                return await DispatchInteractiveTxMessageAsync<TxRemoveInputMessage>(scope, message, channelId,
                                                                                     currentState, negotiatedFeatures,
                                                                                     peerPubKey);
            case MessageTypes.TxRemoveOutput:
                return await DispatchInteractiveTxMessageAsync<TxRemoveOutputMessage>(scope, message, channelId,
                                                                                      currentState, negotiatedFeatures,
                                                                                      peerPubKey);
            case MessageTypes.TxComplete:
                return await DispatchInteractiveTxMessageAsync<TxCompleteMessage>(scope, message, channelId,
                                                                                  currentState, negotiatedFeatures,
                                                                                  peerPubKey);
            case MessageTypes.TxSignatures:
                return await DispatchInteractiveTxMessageAsync<TxSignaturesMessage>(scope, message, channelId,
                                                                                    currentState, negotiatedFeatures,
                                                                                    peerPubKey);
            case MessageTypes.TxInitRbf:
                return await DispatchInteractiveTxMessageAsync<TxInitRbfMessage>(scope, message, channelId,
                                                                                 currentState, negotiatedFeatures,
                                                                                 peerPubKey);
            case MessageTypes.TxAckRbf:
                return await DispatchInteractiveTxMessageAsync<TxAckRbfMessage>(scope, message, channelId,
                                                                                currentState, negotiatedFeatures,
                                                                                peerPubKey);
            case MessageTypes.TxAbort:
                return await DispatchInteractiveTxMessageAsync<TxAbortMessage>(scope, message, channelId,
                                                                               currentState, negotiatedFeatures,
                                                                               peerPubKey);

            // BOLT 2 channel splicing (splicing plan SP1-D-T2): splice_init/splice_ack are exchanged while quiescent
            // and splice_locked after the splice is signed; like the updates they need the channel reestablished on
            // this connection (B2-RE-07)
            case MessageTypes.SpliceInit:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                ThrowIfNotReestablished(channelId, currentState, message.Type);
                return await GetChannelMessageHandler<SpliceInitMessage>(scope)
                          .HandleAsync(Cast<SpliceInitMessage>(message), currentState, negotiatedFeatures, peerPubKey);
            case MessageTypes.SpliceAck:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                ThrowIfNotReestablished(channelId, currentState, message.Type);
                return await GetChannelMessageHandler<SpliceAckMessage>(scope)
                          .HandleAsync(Cast<SpliceAckMessage>(message), currentState, negotiatedFeatures, peerPubKey);
            case MessageTypes.SpliceLocked:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                ThrowIfNotReestablished(channelId, currentState, message.Type);
                return await GetChannelMessageHandler<SpliceLockedMessage>(scope)
                          .HandleAsync(Cast<SpliceLockedMessage>(message), currentState, negotiatedFeatures,
                                       peerPubKey);

            default:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                throw CreateNotImplementedWarning(message.Type, channelId);
        }
    }

    /// <summary>
    /// An interactive-tx message (types 66-74) of a known channel goes to its handler under the channel's lock; an
    /// unknown channel gets an `error` like every other channel message (BOLT 1).
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> DispatchInteractiveTxMessageAsync<T>(
        IServiceScope scope, IChannelMessage message, ChannelId channelId, ChannelState currentState,
        FeatureOptions negotiatedFeatures, CompactPubKey peerPubKey) where T : class, IChannelMessage
    {
        await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
        return await GetChannelMessageHandler<T>(scope)
                  .HandleAsync(Cast<T>(message), currentState, negotiatedFeatures, peerPubKey);
    }

    /// <summary>
    /// A <c>commitment_signed</c> for the first commitment of a dual-funded open goes to
    /// <see cref="DualFunding.DualFundedOpenService"/>; null when the channel has no such negotiation (normal operation).
    /// </summary>
    private static async Task<IReadOnlyList<IChannelMessage>?> TryHandleDualFundCommitmentSignedAsync(
        IServiceScope scope, IChannelMessage message, CompactPubKey peerPubKey) =>
        scope.ServiceProvider.GetService<DualFunding.DualFundedOpenService>() is { } dualFund
            ? await dualFund.TryHandleCommitmentSignedAsync(Cast<CommitmentSignedMessage>(message), peerPubKey,
                                                            scope.ServiceProvider.GetRequiredService<IUnitOfWork>())
            : null;

    private static bool IsCloseMessage(MessageTypes messageType) =>
        messageType is MessageTypes.Shutdown or MessageTypes.ClosingSigned or MessageTypes.ClosingComplete
                    or MessageTypes.ClosingSig;

    private static bool IsNormalOperationMessage(MessageTypes messageType) =>
        messageType is MessageTypes.UpdateAddHtlc or MessageTypes.UpdateFulfillHtlc or MessageTypes.UpdateFailHtlc
                    or MessageTypes.UpdateFailMalformedHtlc or MessageTypes.CommitmentSigned
                    or MessageTypes.RevokeAndAck or MessageTypes.UpdateFee
                    // start_batch announces the commitment_signed batch after it (SP-OP-03): it is dropped with them,
                    // never sent alone before channel_reestablish (B2-RE-07)
                    or MessageTypes.StartBatch;

    /// <summary>
    /// BOLT 2: after a reconnection nothing but channel_reestablish is exchanged for a channel until both were
    /// processed (B2-RE-07). An update, signature or revocation on an Open channel that was not reestablished (or
    /// opened) on this connection breaks that: warning and close the connection, the next reconnection starts over.
    /// Without a <see cref="ReestablishTracker"/> (in-process tests) nothing is gated.
    /// </summary>
    private void ThrowIfUpdateAfterPeerStfu(ChannelId channelId, MessageTypes messageType)
    {
        if (!QuiescenceRules.IsUpdateMessage(messageType)
         || _serviceProvider.GetService<IQuiescenceService>() is not { } quiescenceService)
            return;

        if (QuiescenceRules.CheckPeerMessage(quiescenceService.GetState(channelId), messageType) is { } violation)
            throw QuiescenceRules.CreateWarning(violation, channelId);
    }

    private void ThrowIfNotReestablished(ChannelId channelId, ChannelState currentState, MessageTypes messageType)
    {
        if (currentState is not (ChannelState.Open or ChannelState.ShuttingDown or ChannelState.Negotiating
                                 or ChannelState.Closing)
         || GetTracker() is not { } tracker || tracker.IsReestablished(channelId))
            return;

        var messageName = Enum.GetName(messageType) ?? ((ushort)messageType).ToString();
        throw new ChannelWarningException($"[B2-RE-07] {messageName} on channel {channelId} before channel_reestablish",
                                          channelId, $"{messageName} before channel_reestablish")
        {
            CloseConnection = true
        };
    }

    /// <summary>
    /// BOLT 1: we SHOULD reply with an `error` for the unknown channel_id to channel messages about channels we don't
    /// know. That tells a peer that still has a channel we lost (e.g. it sends channel_reestablish) to fail it, instead
    /// of keeping its funds locked until an operator force-closes.
    /// </summary>
    /// <remarks>
    /// "Unknown" means: not in the memory repository, not a temporary channel being opened with this peer, and not in
    /// the database (the memory repository does not hold every channel, e.g. stale ones, and a peer's channels are
    /// only registered when it connects). Failing an unknown channel fails nothing on our side.
    /// </remarks>
    private async Task ThrowIfUnknownChannelAsync(IServiceScope scope, ChannelId channelId, CompactPubKey peerPubKey)
    {
        if (!IsChannelScoped(channelId)
         || _channelMemoryRepository.TryGetChannelState(channelId, out _)
         || _channelMemoryRepository.TryGetTemporaryChannelState(peerPubKey, channelId, out _))
            return;

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        if (await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId) is not null)
            return;

        throw new ChannelErrorException($"Channel {channelId} is unknown", channelId, "unknown channel");
    }

    /// <summary>
    /// Interim behavior for channel messages we can't process yet (the splicing messages until wave sp1 routes them).
    /// The HTLC and fee updates, commitment_signed and revoke_and_ack have handlers since N6-T1, shutdown and
    /// closing_signed since N10, closing_complete/closing_sig since N11, the interactive-tx messages 66-74 since IT4-T2
    /// and open_channel2/accept_channel2 since wave DF. Only for channels we know: an unknown channel gets an `error` (see
    /// <see cref="ThrowIfUnknownChannelAsync"/>).
    /// </summary>
    /// <remarks>
    /// We never fail a known channel (nor, through an all-zero channel_id, every channel) because we lack a handler: a
    /// failed channel makes the peer (e.g. LND) force-close it. Instead the message is ignored, the peer gets a
    /// `warning` scoped to the channel, and the connection stays up.
    /// </remarks>
    private static ChannelWarningException CreateNotImplementedWarning(MessageTypes messageType, ChannelId channelId)
    {
        var messageName = Enum.GetName(messageType) ?? ((ushort)messageType).ToString();
        return new ChannelWarningException($"Ignoring {messageName}: not supported yet", channelId,
                                           $"{messageName} is not supported yet, message ignored");
    }

    private static T Cast<T>(IChannelMessage message) where T : class, IChannelMessage =>
        message as T ?? throw new ChannelErrorException($"Error boxing message to {typeof(T).Name}",
                                                        "Sorry, we had an internal error");

    private IChannelMessageHandler<T> GetChannelMessageHandler<T>(IServiceScope scope)
        where T : IChannelMessage
    {
        var handler = scope.ServiceProvider.GetRequiredService<IChannelMessageHandler<T>>() ??
                      throw new ChannelErrorException($"No handler found for message type {typeof(T).FullName}",
                                                      "Sorry, we had an internal error");
        return handler;
    }

    /// <summary>
    /// Persists a channel to the database using a scoped Unit of Work
    /// </summary>
    private async Task PersistChannelAsync(ChannelModel channel)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        try
        {
            // Check if the channel already exists

            _ = await unitOfWork.ChannelDbRepository.GetByIdAsync(channel.ChannelId)
             ?? throw new ChannelWarningException("Channel not found", channel.ChannelId);
            await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
            await unitOfWork.SaveChangesAsync();

            // Remove from dictionaries
            _channelMemoryRepository.TryRemoveChannel(channel.ChannelId);

            _logger.LogDebug("Successfully persisted channel {ChannelId} to database", channel.ChannelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist channel {ChannelId} to database", channel.ChannelId);
            throw;
        }
    }

    private void HandleNewBlockDetected(object? sender, NewBlockEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var currentHeight = (int)args.Height;

        // Deal with stale channels
        ForgetStaleChannels(currentHeight);

        // Deal with channels that are waiting for funding confirmation on start-up
        ConfirmUnconfirmedChannels(currentHeight);

        // Closing channels whose closing transaction reached its depth but were not recorded as Closed
        CompleteConfirmedCloses();

        // BOLT 5: the resolution round of every channel whose funding output was spent (O2-T5)
        _serviceProvider.GetService<IOnchainResolutionExecutor>()?.ScheduleRound(args.Height);

        // BOLT 7: public channels that reached the announcement depth send their announcement_signatures (G1-T4)
        ScheduleAnnouncementRound();
    }

    /// <summary>
    /// The last announcement round started by a block (for tests).
    /// </summary>
    internal Task AnnouncementRound { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// BOLT 7 plan G1-T4: every Open public channel whose announcement is not complete in this process gets its
    /// <c>announcement_signatures</c> sent when due (<see cref="IChannelAnnouncementService.PrepareOwnAnnouncementSignaturesAsync"/>),
    /// each under its own lock, and its announcement assembled once both halves are in. With an announced channel our
    /// <c>node_announcement</c> is refreshed when it is due.
    /// </summary>
    private void ScheduleAnnouncementRound()
    {
        if (_serviceProvider.GetService<IChannelAnnouncementService>() is not { } announcementService)
            return;

        var pending = _channelMemoryRepository
                     .FindChannels(c => c.AnnounceChannel && c.State == ChannelState.Open
                                     && !announcementService.IsAnnouncementComplete(c.ChannelId))
                     .Select(c => c.ChannelId)
                     .ToList();
        if (_channelMemoryRepository.FindChannels(c => announcementService.IsAnnouncementComplete(c.ChannelId))
                                    .Count > 0)
            _serviceProvider.GetService<INodeAnnouncementService>()?.RequestAnnouncement();

        if (pending.Count == 0)
            return;

        AnnouncementRound = Task.Run(async () =>
        {
            foreach (var channelId in pending)
                await SendDueAnnouncementSignaturesAsync(announcementService, channelId);
        });
    }

    /// <summary>
    /// Sends the channel's <c>announcement_signatures</c> when due and completes its announcement, under its lock. A
    /// channel not reestablished on its peer's current connection is left to the reestablish (channel_reestablish goes
    /// first on a connection).
    /// </summary>
    private async Task SendDueAnnouncementSignaturesAsync(IChannelAnnouncementService announcementService,
                                                          ChannelId channelId)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            using var channelLock = await _channelLockProvider.AcquireAsync(channelId);
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
             || channel.State != ChannelState.Open)
                return;

            if (GetTracker() is { } tracker && !tracker.IsReestablished(channelId))
                return;

            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var own = await announcementService.PrepareOwnAnnouncementSignaturesAsync(channel, channel.RemoteNodeId,
                                                                                     unitOfWork);
            if (own is not null)
                Publish(channel.RemoteNodeId, [own]);

            await announcementService.CompleteAnnouncementAsync(channel, unitOfWork);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not send the announcement_signatures of channel {ChannelId}", channelId);
        }
    }

    /// <summary>
    /// <paramref name="replies"/> followed by our <c>announcement_signatures</c> when one is due on this connection.
    /// Call it under the channel's lock. A failure is logged: the next block or reconnection retries.
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> AppendAnnouncementSignaturesAsync(
        IServiceScope scope, ChannelId channelId, CompactPubKey peerPubKey, IReadOnlyList<IChannelMessage> replies)
    {
        if (_serviceProvider.GetService<IChannelAnnouncementService>() is not { } announcementService
         || !_channelMemoryRepository.TryGetChannel(channelId, out var channel) || !channel.AnnounceChannel)
            return replies;

        try
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var own = await announcementService.PrepareOwnAnnouncementSignaturesAsync(channel, peerPubKey,
                                                                                     unitOfWork);
            return own is null ? replies : [.. replies, own];
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not prepare the announcement_signatures of channel {ChannelId}", channelId);
            return replies;
        }
    }

    /// <summary>
    /// Assembles and hands on the channel's announcement when both halves are in (after a restart, or when ours went
    /// out after the peer's arrived). Call it under the channel's lock, after the replies were raised.
    /// </summary>
    private async Task CompleteAnnouncementAsync(IServiceScope scope, ChannelId channelId)
    {
        if (_serviceProvider.GetService<IChannelAnnouncementService>() is not { } announcementService
         || !_channelMemoryRepository.TryGetChannel(channelId, out var channel) || !channel.AnnounceChannel)
            return;

        try
        {
            await announcementService.CompleteAnnouncementAsync(
                channel, scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not complete the announcement of channel {ChannelId}", channelId);
        }
    }

    /// <summary>
    /// Retries the end of a close whose watch already completed (the completion raced a failure, or the watch completed
    /// while the channel was not loaded): the channel becomes Closed through the same path as a confirmation. A Failed
    /// channel that signed a closing tx is retried too (NL-312).
    /// </summary>
    private void CompleteConfirmedCloses()
    {
        var closingChannels = _channelMemoryRepository.FindChannels(
            c => c.State is ChannelState.Closing or ChannelState.Failed && c.ClosingTransaction is not null);
        if (closingChannels.Count == 0)
            return;

        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var channel in closingChannels)
        {
            try
            {
                var closingTxId = channel.ClosingTransaction!.TxId;
                var watch = uow.WatchedTransactionDbRepository.GetByTransactionIdAsync(closingTxId).GetAwaiter()
                               .GetResult();
                if (watch is not { IsCompleted: true, FirstSeenAtHeight: { } height, TransactionIndex: { } index })
                    continue;

                _ = ConfirmFundingAsync(channel.ChannelId, height, index, closingTxId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not check the closing transaction of channel {ChannelId}",
                                 channel.ChannelId);
            }
        }
    }

    private void ForgetStaleChannels(int currentHeight)
    {
        // Channels saved before FundingCreatedMessageHandler recorded the creation height have it as 0; start their
        // timeout now so they can still be forgotten.
        BackfillMissingFundingCreatedHeights(currentHeight);

        var heightLimit = currentHeight - ChannelConstants.MaxUnconfirmedChannelAge;
        if (heightLimit < 0)
        {
            _logger.LogDebug("Block height {BlockHeight} is too low to forget channels", currentHeight);
            return;
        }

        // BOLT 2: only the fundee SHOULD forget a channel whose funding transaction was not seen after 2016 blocks.
        // Only consider channels still awaiting funding confirmation, with a real (non-zero) creation height.
        var staleChannels = _channelMemoryRepository.FindChannels(c => IsAwaitingFundingAndStale(c, heightLimit));

        _logger.LogDebug(
            "Forgetting stale channels created before block height {HeightLimit}, found {StaleChannelCount} channels",
            heightLimit, staleChannels.Count);

        foreach (var staleChannel in staleChannels)
            _ = ForgetStaleChannelAsync(staleChannel, heightLimit, currentHeight);
    }

    /// <summary>
    /// Marks one channel Stale and persists it under the channel's lock, re-checking first: a message or a funding
    /// confirmation may have moved the channel on since it was selected.
    /// </summary>
    private async Task ForgetStaleChannelAsync(ChannelModel staleChannel, int heightLimit, int currentHeight)
    {
        try
        {
            using var channelLock = await _channelLockProvider.AcquireAsync(staleChannel.ChannelId);
            if (!IsAwaitingFundingAndStale(staleChannel, heightLimit))
                return;

            _logger.LogInformation(
                "Forgetting stale channel {ChannelId} with funding created at block height {BlockHeight}",
                staleChannel.ChannelId, staleChannel.FundingCreatedAtBlockHeight);

            // Set states
            staleChannel.UpdateState(ChannelState.Stale);
            _channelMemoryRepository.UpdateChannel(staleChannel);

            // Persist on Db
            await PersistChannelAsync(staleChannel);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to persist stale channel {ChannelId} to database at height {currentHeight}",
                             staleChannel.ChannelId, currentHeight);
        }
    }

    private void BackfillMissingFundingCreatedHeights(int currentHeight)
    {
        if (currentHeight <= 0)
            return;

        var channelsWithoutHeight = _channelMemoryRepository.FindChannels(IsAwaitingFundingWithoutCreationHeight);
        if (channelsWithoutHeight.Count == 0)
            return;

        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        foreach (var channel in channelsWithoutHeight)
        {
            // Mutate and persist under the channel's lock, re-checking first (it may have moved on meanwhile)
            using var channelLock = _channelLockProvider.Acquire(channel.ChannelId);
            if (!IsAwaitingFundingWithoutCreationHeight(channel))
                continue;

            _logger.LogInformation(
                "Channel {ChannelId} has no funding creation height, starting its unconfirmed timeout at {BlockHeight}",
                channel.ChannelId, currentHeight);

            channel.FundingCreatedAtBlockHeight = (uint)currentHeight;
            _channelMemoryRepository.UpdateChannel(channel);

            try
            {
                uow.ChannelDbRepository.UpdateAsync(channel).GetAwaiter().GetResult();
                uow.SaveChangesAsync().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to persist funding creation height for channel {ChannelId}",
                                 channel.ChannelId);
            }
        }
    }

    private static bool IsAwaitingFundingWithoutCreationHeight(ChannelModel channel)
    {
        return !channel.IsInitiator
            && channel.State is ChannelState.V1FundingSigned or ChannelState.ReadyForThem
            && channel.FundingCreatedAtBlockHeight == 0;
    }

    private static bool IsAwaitingFundingAndStale(ChannelModel channel, int heightLimit)
    {
        return !channel.IsInitiator
            && channel.State is ChannelState.V1FundingSigned or ChannelState.ReadyForThem
            && channel.FundingCreatedAtBlockHeight > 0
            && channel.FundingCreatedAtBlockHeight <= heightLimit;
    }

    private void ConfirmUnconfirmedChannels(int currentHeight)
    {
        // Only channels still waiting for our own funding confirmation. ReadyForUs channels were already confirmed
        // for us (and sent channel_ready), so re-running the confirmation for them would bump the commitment number
        // and re-send channel_ready on every block.
        var unconfirmedChannels = _channelMemoryRepository.FindChannels(IsAwaitingOurFundingConfirmation);
        if (unconfirmedChannels.Count == 0)
            return;

        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        foreach (var unconfirmedChannel in unconfirmedChannels)
        {
            if (unconfirmedChannel.FundingOutput?.TransactionId is null)
            {
                _logger.LogError("Channel {ChannelId} has no funding transaction Id, cannot confirm",
                                 unconfirmedChannel.ChannelId);
                continue;
            }

            var watchedTransaction =
                uow.WatchedTransactionDbRepository.GetByTransactionIdAsync(
                    unconfirmedChannel.FundingOutput.TransactionId.Value).GetAwaiter().GetResult();
            if (watchedTransaction is null)
            {
                _logger.LogError("Watched transaction for channel {ChannelId} not found",
                                 unconfirmedChannel.ChannelId);
                continue;
            }

            // Only a watched transaction that already reached its required depth (e.g. while we were offline) is
            // confirmed here; pending ones are confirmed by the blockchain monitor when they reach the depth.
            if (!watchedTransaction.IsCompleted)
            {
                // NL-528: a dual-funded open whose current attempt did not confirm may have confirmed an earlier one
                if (unconfirmedChannel.Version != ChannelVersion.V2
                 || FindCompletedEarlierAttempt(uow, unconfirmedChannel) is not { } earlierAttempt)
                    continue;

                watchedTransaction = earlierAttempt;
            }

            // Create a TransactionConfirmedEventArgs and call the event handler
            var args = new TransactionConfirmedEventArgs(watchedTransaction, (uint)currentHeight);
            HandleFundingConfirmationAsync(this, args);
        }
    }

    /// <summary>
    /// The watch of another fully signed attempt of the dual-funded open of <paramref name="channel"/> that reached its
    /// depth (NL-528), or null.
    /// </summary>
    private static Domain.Bitcoin.Transactions.Models.WatchedTransactionModel? FindCompletedEarlierAttempt(
        IUnitOfWork uow, ChannelModel channel)
    {
        IReadOnlyList<Domain.Protocol.InteractiveTx.InteractiveTxSessionModel> sessions;
        try
        {
            sessions = uow.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channel.ChannelId).GetAwaiter()
                          .GetResult();
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            return null;
        }

        foreach (var txId in DualFunding.DualFundedOpenService.GetFollowableFundingTxIds(sessions))
        {
            if (txId == channel.FundingOutput?.TransactionId)
                continue;

            if (uow.WatchedTransactionDbRepository.GetByTransactionIdAsync(txId).GetAwaiter().GetResult() is
                { IsCompleted: true } watch)
                return watch;
        }

        return null;
    }

    private static bool IsAwaitingOurFundingConfirmation(ChannelModel channel)
    {
        return channel.State is ChannelState.V1FundingSigned or ChannelState.ReadyForThem;
    }

    private void HandleFundingConfirmationAsync(object? sender, TransactionConfirmedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.WatchedTransaction.FirstSeenAtHeight is null)
        {
            _logger.LogError(
                "Received null {nameof_FirstSeenAtHeight} in {nameof_TransactionConfirmedEventArgs} for channel {ChannelId}",
                nameof(args.WatchedTransaction.FirstSeenAtHeight), nameof(TransactionConfirmedEventArgs),
                args.WatchedTransaction.ChannelId);
            return;
        }

        if (args.WatchedTransaction.TransactionIndex is null)
        {
            _logger.LogError(
                "Received null {nameof_FirstSeenAtHeight} in {nameof_TransactionConfirmedEventArgs} for channel {ChannelId}",
                nameof(args.WatchedTransaction.FirstSeenAtHeight), nameof(TransactionConfirmedEventArgs),
                args.WatchedTransaction.ChannelId);
            return;
        }

        _ = ConfirmFundingAsync(args.WatchedTransaction.ChannelId, args.WatchedTransaction.FirstSeenAtHeight.Value,
                                args.WatchedTransaction.TransactionIndex.Value, args.WatchedTransaction.TransactionId);
    }

    /// <summary>
    /// Runs the funding confirmation of one channel under its lock, so it can't interleave with that channel's peer
    /// messages or with another confirmation, and its channel_ready is enqueued before the lock is released.
    /// </summary>
    private async Task ConfirmFundingAsync(ChannelId channelId, uint firstSeenAtHeight, uint transactionIndex,
                                           TxId? confirmedTxId = null)
    {
        try
        {
            using var channelLock = await _channelLockProvider.AcquireAsync(channelId);

            // Create a scope to handle the funding confirmation
            using var scope = _serviceProvider.CreateScope();

            // Check if the transaction is a funding transaction for any channel
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            {
                // Channel isn't found in memory, check the database
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                channel = await uow.ChannelDbRepository.GetByIdAsync(channelId);
                if (channel is null)
                {
                    _logger.LogError("Funding confirmation for unknown channel {ChannelId}", channelId);
                    return;
                }

                // NL-607: the closing transaction of a Closed channel confirmed again after a reorg reversed its
                // mutual close in the accounting feed: recorded again, and the channel stays out of memory
                if (channel.State == ChannelState.Closed)
                {
                    if (confirmedTxId is { } closedTxId && channel.ClosingTransaction?.TxId == closedTxId)
                        await ChannelAccountingEvents.RecordMutualCloseAgainAsync(
                            uow, channel, firstSeenAtHeight,
                            (_serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow(), _logger);
                    return;
                }

                if (!SignerLoadsChannels)
                    _lightningSigner.RegisterChannel(channelId, channel.GetSigningInfo());
                _channelMemoryRepository.AddChannel(channel);
            }

            // The agreed mutual close transaction reached its depth (N10); a Failed channel that signed it before it
            // failed closes too (NL-312; Failed 35 → Closed 40 is strictly increasing)
            if (channel.State is ChannelState.Closing or ChannelState.Failed && confirmedTxId is { } txId
             && channel.ClosingTransaction?.TxId == txId)
            {
                await CompleteCloseAsync(scope, channel, firstSeenAtHeight);
                return;
            }

            // NL-617: a channel that failed (or went on chain) before its funding reached the depth still had its
            // funding confirmed: the accounting feed records it, so the force close leaves a bucket it entered
            if (channel.State is ChannelState.Failed or ChannelState.OnchainResolving && confirmedTxId is { } fundedTxId
             && channel.FundingOutput is { TransactionId: { } fundingTxId, Index: { } fundingIndex }
             && fundingTxId == fundedTxId)
            {
                // NL-771: a dual-funded open's liquidity purchase is booked with it, with the same fee
                var lateUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var lateScid = new ShortChannelId(firstSeenAtHeight, transactionIndex, fundingIndex);
                var occurredAt = (_serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
                var liquidityFeeMsat = await DualFunding.DualFundLiquidityAccounting.StageFundingConfirmedAsync(
                                           lateUnitOfWork, channel, firstSeenAtHeight, lateScid, occurredAt, _logger);
                await ChannelAccountingEvents.RecordLateChannelFundedAsync(
                    lateUnitOfWork, channel, firstSeenAtHeight, lateScid, occurredAt, _logger, liquidityFeeMsat);
                return;
            }

            // Funding confirmation is only processed once per channel
            if (!IsAwaitingOurFundingConfirmation(channel))
            {
                _logger.LogDebug("Ignoring funding confirmation for channel {ChannelId} in state {State}", channelId,
                                 Enum.GetName(channel.State));
                return;
            }

            // NL-528: any fully signed attempt of a dual-funded open's RBF may be the one that confirmed; the channel
            // follows it (and an RBF attempt still running is abandoned) before the confirmation is applied
            var dualFund = scope.ServiceProvider.GetService<DualFunding.DualFundedOpenService>();
            if (channel.Version == ChannelVersion.V2 && confirmedTxId is { } confirmedFundingTxId && dualFund is not null
             && !await dualFund.OnFundingConfirmedAsync(channel, confirmedFundingTxId,
                                                         scope.ServiceProvider.GetRequiredService<IUnitOfWork>()))
                return;

            var fundingConfirmedHandler = scope.ServiceProvider.GetRequiredService<FundingConfirmedMessageHandler>();

            // If we get a response, raise it right away (synchronously, while we hold the channel's lock)
            var remoteNodeId = channel.RemoteNodeId;
            fundingConfirmedHandler.OnMessageReady += (_, message) => RaiseResponseMessages(remoteNodeId, [message]);

            // Add confirmation information to the channel (a channel awaiting its confirmation always knows its
            // funding outpoint: the funder built the transaction, the fundee got it with funding_created)
            channel.FundingCreatedAtBlockHeight = firstSeenAtHeight;
            channel.ShortChannelId = new ShortChannelId(firstSeenAtHeight, transactionIndex,
                                                        channel.FundingOutput!.Index!.Value);

            await fundingConfirmedHandler.HandleAsync(channel);

            // NL-528: the peer's channel_ready that arrived before we knew which attempt confirmed
            if (channel.State == ChannelState.ReadyForUs && dualFund?.TakeDeferredChannelReady(channelId) is
                { } deferred)
                RaiseResponseMessages(remoteNodeId,
                                      await GetChannelMessageHandler<ChannelReadyMessage>(scope)
                                           .HandleAsync(deferred.Message, channel.State, deferred.Features,
                                                        remoteNodeId));

            // A signer without a source only knows the short channel id it was registered with, and needs the real one
            // to sign the channel's announcement (NL-343); one with a source reads it from the database
            if (!SignerLoadsChannels && channel.State is ChannelState.ReadyForUs or ChannelState.Open)
                _lightningSigner.RegisterChannel(channelId, channel.GetSigningInfo());
            MarkLinkUpIfOpened(channelId, remoteNodeId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while handling funding confirmation for channel {channelId}", channelId);
        }
    }
}