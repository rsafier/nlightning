using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Persistence.Interfaces;
using Interfaces;

/// <summary>
/// The long-poll behind <c>openchannel</c>: answers when the channel's funding is published and when the channel is
/// ready.
/// </summary>
/// <remarks>
/// With <see cref="OpenChannelClientSubscriptionRequest.ReportFundingChanges"/> (NL-535) it answers as soon as the
/// channel, still waiting for its funding (<see cref="ChannelState.V1FundingSigned"/>), runs on a published funding
/// transaction other than the one the client printed last: at once for a dual-funded open whose first attempt was
/// published before the call, and on every RBF attempt of either side (a dual-funded attempt counts once its
/// <c>BroadcastTransactions</c> row exists, so an attempt still being signed is never reported). A channel that
/// reached <see cref="ChannelState.V1FundingSigned"/> before the call answers at once too (NL-295), for every client.
/// A peer that disconnects once the funding is signed no longer fails the wait: the open continues and the channel
/// reestablishes.
/// </remarks>
public class OpenChannelClientSubscriptionHandler :
    IClientCommandHandler<OpenChannelClientSubscriptionRequest, OpenChannelClientSubscriptionResponse>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<OpenChannelClientSubscriptionHandler> _logger;
    private readonly IPeerManager _peerManager;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;
    private readonly IServiceScopeFactory? _serviceScopeFactory;

    private ChannelId _channelId;
    private IPeerService? _peerService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.OpenChannelSubscription;

    public OpenChannelClientSubscriptionHandler(IChannelMemoryRepository channelMemoryRepository,
                                                ILogger<OpenChannelClientSubscriptionHandler> logger,
                                                IPeerManager peerManager, IUtxoMemoryRepository utxoMemoryRepository,
                                                IServiceScopeFactory? serviceScopeFactory = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _peerManager = peerManager;
        _utxoMemoryRepository = utxoMemoryRepository;
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <inheritdoc/>
    public async Task<OpenChannelClientSubscriptionResponse> HandleAsync(OpenChannelClientSubscriptionRequest request,
                                                                         CancellationToken ct)
    {
        if (request.ChannelId == ChannelId.Zero)
            throw new ClientException(ErrorCodes.InvalidChannel, "ChannelId cannot be empty");

        _channelId = request.ChannelId;

        if (!_channelMemoryRepository.TryGetChannel(_channelId, out var channel))
            throw new ClientException(ErrorCodes.InvalidChannel, $"Channel with Id {_channelId} not found");

        // If it's in a state we consider Open, return immediately
        if (IsReady(channel))
            return CreateResponse(channel, ChannelState.ReadyForUs);

        // Once the funding is signed the open no longer needs the peer to be connected (it reestablishes)
        var fundingSigned = channel.State == ChannelState.V1FundingSigned;
        var peer = _peerManager.GetPeer(channel.RemoteNodeId);
        if (peer is null && !fundingSigned)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"Peer with NodeId {channel.RemoteNodeId} is not connected");

        // Check if the channel is already in a state we care about
        var lockedUtxos = _utxoMemoryRepository.GetLockedUtxosForChannel(_channelId);
        if (!fundingSigned && lockedUtxos.Count == 0)
            throw new ClientException(ErrorCodes.InvalidOperation, $"No locked UTXOs found for channel {_channelId}");

        var failure = new TaskCompletionSource<OpenChannelClientSubscriptionResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = Channel.CreateUnbounded<ChannelModel>(new UnboundedChannelOptions { SingleReader = true });
        var remoteNodeId = channel.RemoteNodeId;

        try
        {
            if (peer is not null)
            {
                if (!peer.TryGetPeerService(out _peerService))
                    throw new ClientException(ErrorCodes.InvalidOperation, "Error getting peerService from peer");

                // Subscribe to the events
                _peerService.OnAttentionMessageReceived += AttentionMessageHandlerEnvelope;
                _peerService.OnDisconnect += PeerDisconnectionEnvelope;
                _peerService.OnExceptionRaised += ExceptionRaisedEnvelope;
            }

            _channelMemoryRepository.OnChannelUpdated += ChannelUpdatedHandlerEnvelope;

            // Subscribed first, so an update between the read and the subscription is not lost; a channel that
            // already qualifies answers from its state (NL-295: the signing may predate this call), without waiting
            // for the next update
            if (_channelMemoryRepository.TryGetChannel(_channelId, out var current)
             && await TryAnswerAsync(current, request) is { } immediate)
                return immediate;

            while (true)
            {
                var next = updates.Reader.ReadAsync(ct).AsTask();
                var completed = await Task.WhenAny(next, failure.Task);
                if (completed == failure.Task)
                    return await failure.Task;

                if (await TryAnswerAsync(await next, request) is { } answer)
                    return answer;
            }
        }
        catch
        {
            // A client that stops waiting (Ctrl-C after the txid) leaves the open as it is
            if (ct.IsCancellationRequested
             || !_channelMemoryRepository.TryGetChannel(_channelId, out channel)
             || channel.State is ChannelState.ReadyForUs
                              or ChannelState.ReadyForThem
                              or ChannelState.Open
                              or ChannelState.V1FundingSigned)
                throw;

            _utxoMemoryRepository.ReturnUtxosNotSpentOnChannel(request.ChannelId);

            throw;
        }
        finally
        {
            //Unsubscribe from the events so we don't have dangling memory
            _peerService?.OnAttentionMessageReceived -= AttentionMessageHandlerEnvelope;
            _peerService?.OnDisconnect -= PeerDisconnectionEnvelope;
            _peerService?.OnExceptionRaised -= ExceptionRaisedEnvelope;
            _channelMemoryRepository.OnChannelUpdated -= ChannelUpdatedHandlerEnvelope;
        }

        // Envelopes for the events
        void AttentionMessageHandlerEnvelope(object? _, AttentionMessageEventArgs args) =>
            HandleAttentionMessage(args, failure);

        void PeerDisconnectionEnvelope(object? _, PeerDisconnectedEventArgs args) =>
            HandlePeerDisconnection(args, remoteNodeId, failure);

        void ExceptionRaisedEnvelope(object? _, Exception e) =>
            HandleExceptionRaised(e, failure);

        void ChannelUpdatedHandlerEnvelope(object? _, ChannelUpdatedEventArgs args)
        {
            if (args.Channel.ChannelId == _channelId)
                updates.Writer.TryWrite(args.Channel);
        }
    }

    /// <summary>
    /// The answer for the channel as it is now, or null to keep waiting.
    /// </summary>
    private async Task<OpenChannelClientSubscriptionResponse?> TryAnswerAsync(
        ChannelModel channel, OpenChannelClientSubscriptionRequest request)
    {
        if (IsReady(channel))
            return CreateResponse(channel, ChannelState.ReadyForUs);

        if (channel.State != ChannelState.V1FundingSigned)
            return null;

        // An older client: a channel waiting for its funding is an answer — from its current state (NL-295) or from
        // an update
        if (!request.ReportFundingChanges)
            return CreateResponse(channel, ChannelState.V1FundingSigned);

        if (channel.FundingOutput?.TransactionId is not { } txId || txId == request.KnownFundingTxId)
            return null;

        // A dual-funded RBF attempt moves the channel's funding output while it is being signed; it is reported once
        // it is published (its broadcast row is saved with both tx_signatures)
        if (channel.Version == ChannelVersion.V2 && !await IsPublishedAsync(txId))
            return null;

        return CreateResponse(channel, ChannelState.V1FundingSigned);
    }

    private async Task<bool> IsPublishedAsync(TxId txId)
    {
        if (_serviceScopeFactory is null)
            return true;

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            return await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId) is not null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not read the broadcast of funding {TxId}; reporting it", txId);
            return true;
        }
    }

    private static bool IsReady(ChannelModel channel) =>
        channel.State is ChannelState.ReadyForUs or ChannelState.ReadyForThem or ChannelState.Open;

    private static OpenChannelClientSubscriptionResponse CreateResponse(ChannelModel channel, ChannelState state) =>
        new(channel.ChannelId)
        {
            ChannelState = state,
            TxId = channel.FundingOutput?.TransactionId,
            Index = channel.FundingOutput?.Index
        };

    private void HandleAttentionMessage(AttentionMessageEventArgs args,
                                        TaskCompletionSource<OpenChannelClientSubscriptionResponse> tsc)
    {
        if (args.ChannelId != _channelId)
            return;

        _logger.LogError(
            "Received attention message from peer {peerId} for channel {channelId}: {message}",
            args.PeerPubKey, args.ChannelId, args.Message);

        tsc.TrySetException(new ChannelErrorException($"Error opening channel: {args.Message}"));
    }

    private void HandlePeerDisconnection(PeerDisconnectedEventArgs args, CompactPubKey peerPubKey,
                                         TaskCompletionSource<OpenChannelClientSubscriptionResponse> tsc)
    {
        if (args.PeerPubKey != peerPubKey)
            return;

        // A signed funding survives the disconnection: the channel waits for it and reestablishes (NL-535)
        if (_channelMemoryRepository.TryGetChannel(_channelId, out var channel)
         && channel.State == ChannelState.V1FundingSigned)
        {
            _logger.LogInformation("Peer {Peer} disconnected while channel {ChannelId} waits for its funding; still "
                                 + "waiting", peerPubKey, _channelId);
            return;
        }

        if (args.Exception is null)
        {
            _logger.LogError("Peer disconnected without notice");
            tsc.TrySetException(new ConnectionException("Error opening channel: Peer disconnected"));
        }
        else
        {
            // Get to the bottom of the inner exceptions to fetch the real reason for the disconnection
            var exception = args.Exception;
            while (exception.InnerException is not null)
                exception = exception.InnerException;

            _logger.LogError(args.Exception, "Error opening channel. Error: {message}", exception.Message);
            tsc.TrySetException(new ChannelErrorException($"Error opening channel: {exception.Message}", exception));
        }
    }

    private void HandleExceptionRaised(Exception e, TaskCompletionSource<OpenChannelClientSubscriptionResponse> tsc)
    {
        if (e is not ChannelErrorException ce || ce.ChannelId != _channelId)
            return;

        _logger.LogError("Exception raised while opening channel: {message}", e.Message);
        tsc.TrySetException(e);
    }
}