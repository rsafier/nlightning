using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Channels.Managers;

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
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;
using Handlers;
using Handlers.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Reestablish;
using Services;

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

        // Register the channel with the signer (its local commitment number comes from the snapshot, if any)
        _lightningSigner.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());

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
        await unitOfWork.SaveChangesAsync();

        if (published)
        {
            _logger.LogInformation(
                "Channel {ChannelId} was stopped while its funding transaction was being published; waiting for it to confirm",
                channel.ChannelId);
            return true;
        }

        _logger.LogWarning(
            "Channel {ChannelId} was stopped before its funding transaction was published; forgetting it (BOLT 2: a funder that has not broadcast SHOULD NOT remember the channel)",
            channel.ChannelId);
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

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ChannelStateDbRepository.ApplyAsync(result.Next, result.Transition);
            await unitOfWork.SaveChangesAsync();
            channel.UpdateCommitments(result.Next);
            _channelMemoryRepository.UpdateChannel(channel);

            _logger.LogInformation("Reverted {Count} uncommitted peer update(s) of channel {ChannelId}",
                                   result.Transition.DroppedHtlcs.Count + result.Transition.UpsertedHtlcs.Count,
                                   channel.ChannelId);
        }
        catch (Exception e)
        {
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
                    // Persist Failed and the error before it is sent (N6-T3 contract), still under the lock
                    await PersistFailedChannelAsync(scope, cfe);
                    throw;
                }

                if (!wasOpen)
                    MarkLinkUpIfOpened(channelId, peerPubKey);

                if (message.Type == MessageTypes.ChannelReestablish)
                    reestablished = await CompleteReestablishAsync(scope, channelId, peerPubKey);

                replies = await AdvanceCloseAsync(scope, channelId, replies);
                RaiseResponseMessages(peerPubKey, replies);
            }

            if (reestablished)
                ScheduleCommit(channelId);
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
                    case ChannelState.Failed:
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
    }

    /// <inheritdoc />
    /// <remarks>Waits for a <see cref="Publish"/> in progress (see there).</remarks>
    public void OnPeerConnectionChanged(CompactPubKey peerPubKey)
    {
        lock (_connectionGate)
            GetTracker()?.ResetPeer(peerPubKey);
    }

    private List<ChannelModel> GetPeerChannels(CompactPubKey peerPubKey) =>
        _channelMemoryRepository.FindChannels(c => c.RemoteNodeId == peerPubKey);

    private ReestablishTracker? GetTracker() => _serviceProvider.GetService<ReestablishTracker>();

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
            await CompleteCloseAsync(scope, channel);
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
    private async Task CompleteCloseAsync(IServiceScope scope, ChannelModel channel)
    {
        channel.UpdateState(ChannelState.Closed);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
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
    /// The funding output of a closing channel was spent. When the spending transaction is a mutual close of this
    /// channel (one input, the funding outpoint; final sequence and no lock time; every output pays one of the two
    /// shutdown scripts) that we did not record (the peer broadcast a proposal we signed and our connection or node
    /// went down before its <c>closing_signed</c> reached us, or it broadcast the other dust variant), it becomes the
    /// channel's closing transaction: Closing and its watch (already seen in this block) in one save, so the watch's
    /// depth makes the channel Closed as usual. Any other spend is a commitment transaction, which needs BOLT 5
    /// on-chain handling (not implemented): logged. Idempotent (a replayed block raises it again).
    /// </summary>
    private async Task HandleFundingSpentAsync(OutpointSpentEventArgs args)
    {
        var channelId = args.ChannelId;
        var spend = args.SpendingTransaction;
        try
        {
            using var channelLock = await _channelLockProvider.AcquireAsync(channelId);
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
             || channel.ClosingTransaction?.TxId == spend.TxId)
                return;

            if (channel.State is not (ChannelState.ShuttingDown or ChannelState.Negotiating or ChannelState.Closing)
             || !IsMutualCloseOf(channel, spend))
            {
                _logger.LogCritical(
                    "The funding output of channel {ChannelId} ({State}) was spent by {TxId}, which is not a mutual close we know: on-chain handling (BOLT 5) is not implemented",
                    channelId, Enum.GetName(channel.State), spend.TxId);
                return;
            }

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
        catch (Exception e)
        {
            _logger.LogError(e, "Could not record the spend of the funding output of channel {ChannelId} by {TxId}",
                             channelId, spend.TxId);
        }
    }

    /// <summary>
    /// True when <paramref name="spend"/> has the shape of a BOLT 3 mutual close of <paramref name="channel"/>: its only
    /// input is the funding outpoint with sequence 0xFFFFFFFF, lock time 0, and every output pays one of the two
    /// shutdown scripts.
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

        if (transaction.Inputs.Count != 1 || transaction.LockTime != LockTime.Zero || transaction.Outputs.Count == 0)
            return false;

        var input = transaction.Inputs[0];
        if (input.PrevOut != new OutPoint(new uint256((byte[])fundingTxId), fundingIndex)
         || input.Sequence != Sequence.Final)
            return false;

        byte[] local = localScript;
        byte[] remote = remoteScript;
        return transaction.Outputs.All(o =>
        {
            var script = o.ScriptPubKey.ToBytes();
            return script.AsSpan().SequenceEqual(local) || script.AsSpan().SequenceEqual(remote);
        });
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
    /// Fails a channel (BOLT2 plan N6-T3, D10): persists <see cref="ChannelState.Failed"/> and the <c>error</c> it will
    /// be sent (re-sent on reconnection, B2-RE-05) before the exception reaches the send path. A failure to persist is
    /// logged; the error is still sent.
    /// </summary>
    private async Task PersistFailedChannelAsync(IServiceScope scope, ChannelFailedException failure)
    {
        var channelId = failure.FailedChannelId;
        _logger.LogCritical(failure, "Failing channel {ChannelId} ({RequirementId}); broadcast needed: {MustBroadcast}",
                            channelId, failure.RequirementId, failure.MustBroadcast);

        try
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
                return;

            // Already failed and stored (a message on a failed channel gets the stored error again)
            if (channel.State == ChannelState.Failed && channel.ErrorSent is not null)
                return;

            // The closing transaction is agreed and out: the channel ends Closed when it confirms, never Failed
            if (channel.State == ChannelState.Closing)
            {
                _logger.LogWarning("Channel {ChannelId} is closing; it is not marked failed", channelId);
                return;
            }

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

        // BOLT 2: a failed channel re-sends its error and ignores everything else (B2-RE-05)
        if (currentState == ChannelState.Failed && _channelMemoryRepository.TryGetChannel(channelId, out var failed))
            throw new ChannelFailedException(channelId,
                                             $"Ignoring {Enum.GetName(message.Type)} on failed channel {channelId}",
                                             await GetStoredErrorTextAsync(scope, failed));

        if (IsNormalOperationMessage(message.Type) || IsCloseMessage(message.Type))
            ThrowIfNotReestablished(channelId, currentState, message.Type);

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

            default:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                throw CreateNotImplementedWarning(message.Type, channelId);
        }
    }

    private static bool IsCloseMessage(MessageTypes messageType) =>
        messageType is MessageTypes.Shutdown or MessageTypes.ClosingSigned;

    private static bool IsNormalOperationMessage(MessageTypes messageType) =>
        messageType is MessageTypes.UpdateAddHtlc or MessageTypes.UpdateFulfillHtlc or MessageTypes.UpdateFailHtlc
                    or MessageTypes.UpdateFailMalformedHtlc or MessageTypes.CommitmentSigned
                    or MessageTypes.RevokeAndAck or MessageTypes.UpdateFee;

    /// <summary>
    /// BOLT 2: after a reconnection nothing but channel_reestablish is exchanged for a channel until both were
    /// processed (B2-RE-07). An update, signature or revocation on an Open channel that was not reestablished (or
    /// opened) on this connection breaks that: warning and close the connection, the next reconnection starts over.
    /// Without a <see cref="ReestablishTracker"/> (in-process tests) nothing is gated.
    /// </summary>
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
    /// Interim behavior for channel messages we can't process yet: the dual-funding messages and
    /// <c>closing_complete</c>/<c>closing_sig</c> (option_simple_close, N11). The HTLC and fee updates,
    /// commitment_signed and revoke_and_ack have handlers since N6-T1, shutdown and closing_signed since N10. Only for channels we know: an unknown channel gets an `error` (see
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
    }

    /// <summary>
    /// Retries the end of a close whose watch already completed (the completion raced a failure, or the watch completed
    /// while the channel was not loaded): the channel becomes Closed through the same path as a confirmation.
    /// </summary>
    private void CompleteConfirmedCloses()
    {
        var closingChannels = _channelMemoryRepository.FindChannels(c => c.State == ChannelState.Closing
                                                                      && c.ClosingTransaction is not null);
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
                continue;

            // Create a TransactionConfirmedEventArgs and call the event handler
            var args = new TransactionConfirmedEventArgs(watchedTransaction, (uint)currentHeight);
            HandleFundingConfirmationAsync(this, args);
        }
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

                _lightningSigner.RegisterChannel(channelId, channel.GetSigningInfo());
                _channelMemoryRepository.AddChannel(channel);
            }

            // The agreed mutual close transaction reached its depth (N10)
            if (channel.State == ChannelState.Closing && confirmedTxId is { } txId
             && channel.ClosingTransaction?.TxId == txId)
            {
                await CompleteCloseAsync(scope, channel);
                return;
            }

            // Funding confirmation is only processed once per channel
            if (!IsAwaitingOurFundingConfirmation(channel))
            {
                _logger.LogDebug("Ignoring funding confirmation for channel {ChannelId} in state {State}", channelId,
                                 Enum.GetName(channel.State));
                return;
            }

            var fundingConfirmedHandler = scope.ServiceProvider.GetRequiredService<FundingConfirmedMessageHandler>();

            // If we get a response, raise it right away (synchronously, while we hold the channel's lock)
            var remoteNodeId = channel.RemoteNodeId;
            fundingConfirmedHandler.OnMessageReady += (_, message) => RaiseResponseMessages(remoteNodeId, [message]);

            // Add confirmation information to the channel
            channel.FundingCreatedAtBlockHeight = firstSeenAtHeight;
            channel.ShortChannelId = new ShortChannelId(firstSeenAtHeight, transactionIndex,
                                                        channel.FundingOutput.Index!.Value);

            await fundingConfirmedHandler.HandleAsync(channel);
            MarkLinkUpIfOpened(channelId, remoteNodeId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while handling funding confirmation for channel {channelId}", channelId);
        }
    }
}