using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Managers;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
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
using Domain.Serialization.Interfaces;
using Handlers;
using Handlers.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Services;

public class ChannelManager : IChannelManager, IChannelMessagePublisher
{
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<ChannelManager> _logger;
    private readonly ILightningSigner _lightningSigner;
    private readonly IServiceProvider _serviceProvider;

    public event EventHandler<ChannelResponseMessageEventArgs>? OnResponseMessageReady;

    public ChannelManager(IBlockchainMonitor blockchainMonitor, IChannelLockProvider channelLockProvider,
                          IChannelMemoryRepository channelMemoryRepository, ILogger<ChannelManager> logger,
                          ILightningSigner lightningSigner, IServiceProvider serviceProvider)
    {
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _lightningSigner = lightningSigner;

        blockchainMonitor.OnNewBlockDetected += HandleNewBlockDetected;
        blockchainMonitor.OnTransactionConfirmed += HandleFundingConfirmationAsync;
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
    /// <remarks>Raises <see cref="OnResponseMessageReady"/> for each message; call it while holding the channel's lock.
    /// </remarks>
    public void Publish(CompactPubKey peerPubKey, IReadOnlyList<IChannelMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        RaiseResponseMessages(peerPubKey, messages);
    }

    private async Task RegisterExistingChannelLockedAsync(IServiceScope scope, ChannelModel channel)
    {

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
                // TODO: Check if the channel has already been reestablished or if we need to reestablish it (N7)
                break;
            case ChannelState.ReadyForThem or ChannelState.ReadyForUs:
                _logger.LogInformation("Waiting for channel {ChannelId} to be ready", channel.ChannelId);
                break;
            case ChannelState.Failed:
                // Kept in memory so its updates are refused; the error is re-sent on reconnection (B2-RE-05, N7)
                _logger.LogWarning("Channel {ChannelId} was failed; every update on it is refused",
                                   channel.ChannelId);
                break;
            default:
                // TODO: Deal with channels that are Closing, Stale, or any other state
                _logger.LogWarning("We don't know how to deal with {channelState} for channel {ChannelId}",
                                   Enum.GetName(channel.State), channel.ChannelId);
                break;
        }
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
        if (channel.State != ChannelState.Open)
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
    /// </remarks>
    public async Task<IReadOnlyList<IChannelMessage>> HandleChannelMessageAsync(IChannelMessage message,
                                                                                FeatureOptions negotiatedFeatures,
                                                                                CompactPubKey peerPubKey)
    {
        var channelId = message.Payload.ChannelId;

        // One scope per message, created outside the lock so the transitions' domain events can be handed to the HTLC
        // switch after the lock is released
        using var scope = _serviceProvider.CreateScope();
        try
        {
            using var channelLocks = await AcquireMessageLocksAsync(message, channelId);

            IReadOnlyList<IChannelMessage> replies;
            try
            {
                replies = await DispatchChannelMessageAsync(scope, message, channelId, negotiatedFeatures, peerPubKey);
            }
            catch (ChannelFailedException cfe)
            {
                // Persist Failed and the error before it is sent (N6-T3 contract), still under the lock
                await PersistFailedChannelAsync(scope, cfe);
                throw;
            }

            RaiseResponseMessages(peerPubKey, replies);

            return replies;
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

            default:
                await ThrowIfUnknownChannelAsync(scope, channelId, peerPubKey);
                throw CreateNotImplementedWarning(message.Type, channelId);
        }
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
    /// Interim behavior for channel messages we can't process yet: channel_reestablish (BOLT2 plan N7),
    /// shutdown/closing_signed (N10), and the dual-funding messages (the HTLC and fee updates, commitment_signed and
    /// revoke_and_ack have handlers since N6-T1). Only for channels we know: an unknown channel gets an `error` (see
    /// <see cref="ThrowIfUnknownChannelAsync"/>).
    /// </summary>
    /// <remarks>
    /// We never fail a known channel (nor, through an all-zero channel_id, every channel) because we lack a handler: a
    /// failed channel makes the peer (e.g. LND) force-close it. Instead the message is ignored, the peer gets a
    /// `warning` scoped to the channel, and the connection stays up. The peer keeps waiting for our reply; for
    /// channel_reestablish the channel simply stays inactive until N7 lands.
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
                                args.WatchedTransaction.TransactionIndex.Value);
    }

    /// <summary>
    /// Runs the funding confirmation of one channel under its lock, so it can't interleave with that channel's peer
    /// messages or with another confirmation, and its channel_ready is enqueued before the lock is released.
    /// </summary>
    private async Task ConfirmFundingAsync(ChannelId channelId, uint firstSeenAtHeight, uint transactionIndex)
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
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while handling funding confirmation for channel {channelId}", channelId);
        }
    }
}