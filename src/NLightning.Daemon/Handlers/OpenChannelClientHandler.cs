using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Handlers;

using Domain.Bitcoin.Constants;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Protocol.Models;
using Interfaces;

public sealed class OpenChannelClientHandler
    : IClientCommandHandler<OpenChannelClientRequest, OpenChannelClientResponse>
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelManager _channelManager;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelFactory _channelFactory;
    private readonly ILogger<OpenChannelClientHandler> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IPeerManager _peerManager;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;
    private readonly GossipOptions _gossipOptions;
    private readonly NodeOptions _nodeOptions;

    private ChannelId _channelId = ChannelId.Zero;
    private IPeerService? _peerService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.OpenChannel;

    public OpenChannelClientHandler(IBlockchainMonitor blockchainMonitor, IChannelFactory channelFactory,
                                    IChannelManager channelManager, IChannelMemoryRepository channelMemoryRepository,
                                    ILogger<OpenChannelClientHandler> logger, IMessageFactory messageFactory,
                                    IPeerManager peerManager, IUtxoMemoryRepository utxoMemoryRepository,
                                    IOptions<GossipOptions>? gossipOptions = null,
                                    IOptions<NodeOptions>? nodeOptions = null)
    {
        _gossipOptions = gossipOptions?.Value ?? new GossipOptions();
        _nodeOptions = nodeOptions?.Value ?? new NodeOptions();
        _blockchainMonitor = blockchainMonitor;
        _channelFactory = channelFactory;
        _channelManager = channelManager;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _messageFactory = messageFactory;
        _peerManager = peerManager;
        _utxoMemoryRepository = utxoMemoryRepository;
    }

    /// <inheritdoc/>
    public async Task<OpenChannelClientResponse> HandleAsync(OpenChannelClientRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.NodeInfo))
            throw new ClientException(ErrorCodes.InvalidAddress, "Address cannot be empty");

        // NL-216: no new channel while the node does not follow the chain (it could not see the funding confirm)
        if (_blockchainMonitor.IsChainProcessingHalted)
            throw new ClientException(ErrorCodes.InvalidOperation, ChainProcessingHalt.Refusal("openchannel"));

        // BOLT 7 plan D12: public channels stay off on mainnet until the Docker proof of G1 passed
        if (request.IsPublic && _nodeOptions.BitcoinNetwork == BitcoinNetwork.Mainnet
                             && !_gossipOptions.AllowPublicChannelsOnMainnet)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "Public channels are not enabled on mainnet yet "
                                    + "(Gossip:AllowPublicChannelsOnMainnet)");

        if (request.IsPublic && request.IsZeroConfChannel)
            throw new ClientException(ErrorCodes.InvalidOperation, "A public channel can't be zero-conf");

        // Check if either a PeerAddressInfo or a CompactPubKey was provided
        var isPeerAddressInfo = request.NodeInfo.Contains('@') && request.NodeInfo.Contains(':');
        CompactPubKey peerId;

        peerId = isPeerAddressInfo
                     ? new PeerAddress(request.NodeInfo).PubKey
                     : new CompactPubKey(Convert.FromHexString(request.NodeInfo)); // Parse as a hex public key

        // Check if we're connected to the peer
        var peer = _peerManager.GetPeer(peerId)
                ?? await _peerManager.ConnectToPeerAsync(new PeerAddressInfo(request.NodeInfo));

        // Let's check if we have enough funds to open this channel
        var currentHeight = _blockchainMonitor.LastProcessedBlockHeight;
        if (_utxoMemoryRepository.GetConfirmedBalance(currentHeight) < request.FundingAmount)
            throw new ClientException(ErrorCodes.NotEnoughBalance, "We don't have enough balance to open this channel");

        // Since we're connected, let's open the channel
        var channel =
            await _channelFactory.CreateChannelV1AsInitiatorAsync(request, peer.NegotiatedFeatures, peerId);

        // Save the channelId for later
        _channelId = channel.ChannelId;

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Created Temporary Channel {id} with fundingPubKey: {fundingPubKey}", channel.ChannelId,
                             channel.LocalKeySet.FundingCompactPubKey);

        // Select UTXOs and mark them as toSpend for this channel
        _utxoMemoryRepository.LockUtxosToSpendOnChannel(request.FundingAmount, channel.ChannelId);

        // Create a task completion source for the response
        var tsc = new TaskCompletionSource<OpenChannelClientResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            // Create the channel type Tlv; accept_channel must echo exactly this type
            var channelTypeTlv = new ChannelTypeTlv(channel.ChannelParams.ToChannelType());

            // Create UpfrontShutdownScriptTlv if needed
            var upfrontShutdownScriptTlv = channel.LocalUpfrontShutdownScript is not null
                                               ? new UpfrontShutdownScriptTlv(channel.LocalUpfrontShutdownScript.Value)
                                               : new UpfrontShutdownScriptTlv(Array.Empty<byte>());

            // Create the ChannelFlags (NL-341): announce_channel for a public channel, whose channel type the factory
            // built without option_scid_alias (BOLT 2 forbids the two together)
            var channelFlags = new ChannelFlags(channel.AnnounceChannel ? ChannelFlag.AnnounceChannel
                                                                        : ChannelFlag.None);

            // Create the openChannel message
            // funding_satoshis is the whole channel; the pushed part is only the peer's opening balance
            var openChannel1Message = _messageFactory.CreateOpenChannel1Message(
                channel.ChannelId, request.FundingAmount, channel.LocalKeySet.FundingCompactPubKey,
                channel.RemoteBalance, channel.ChannelParams.Local, channel.ChannelParams.FeeRateAmountPerKw,
                channel.LocalKeySet.RevocationCompactBasepoint,
                channel.LocalKeySet.PaymentCompactBasepoint, channel.LocalKeySet.DelayedPaymentCompactBasepoint,
                channel.LocalKeySet.HtlcCompactBasepoint, channel.LocalKeySet.CurrentPerCommitmentCompactPoint,
                channelFlags, channelTypeTlv, upfrontShutdownScriptTlv);

            if (!peer.TryGetPeerService(out _peerService))
                throw new ClientException(ErrorCodes.InvalidOperation, "Error getting peerService from peer");

            // Subscribe to the events before sending the message
            _peerService.OnAttentionMessageReceived += AttentionMessageHandlerEnvelope;
            _peerService.OnDisconnect += PeerDisconnectionEnvelope;
            _peerService.OnExceptionRaised += ExceptionRaisedEnvelope;
            _channelMemoryRepository.OnChannelUpgraded += ChannelUpgradedHandlerEnvelope;

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Sending OpenChannel message to peer {peerId} for channel {channelId}",
                                       peerId,
                                       channel.ChannelId);
            // Stores the temporary channel and queues open_channel on the peer's outbox, under the channel's lock
            await _channelManager.StartOpeningChannelAsync(peerId, channel, openChannel1Message);

            return await tsc.Task;
        }
        catch
        {
            _utxoMemoryRepository.ReturnUtxosNotSpentOnChannel(_channelId);

            throw;
        }
        finally
        {
            //Unsubscribe from the events so we don't have dangling memory
            _peerService?.OnAttentionMessageReceived -= AttentionMessageHandlerEnvelope;
            _peerService?.OnDisconnect -= PeerDisconnectionEnvelope;
            _peerService?.OnExceptionRaised -= ExceptionRaisedEnvelope;
            _channelMemoryRepository.OnChannelUpgraded -= ChannelUpgradedHandlerEnvelope;
        }

        // Envelopes for the events
        void AttentionMessageHandlerEnvelope(object? _, AttentionMessageEventArgs args) =>
            HandleAttentionMessage(args, tsc);

        void PeerDisconnectionEnvelope(object? _, PeerDisconnectedEventArgs args) =>
            HandlePeerDisconnection(args, channel.RemoteNodeId, tsc);

        void ExceptionRaisedEnvelope(object? _, Exception e) =>
            HandleExceptionRaised(e, tsc);

        void ChannelUpgradedHandlerEnvelope(object? _, ChannelUpgradedEventArgs args) =>
            HandleChannelUpgraded(args, tsc);
    }

    private void HandleChannelUpgraded(ChannelUpgradedEventArgs args,
                                       TaskCompletionSource<OpenChannelClientResponse> tsc)
    {
        if (args.OldChannelId != _channelId)
            return;

        tsc.TrySetResult(new OpenChannelClientResponse(args.NewChannelId));

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Channel {oldChannelId} has been upgraded to {channelId}", args.OldChannelId,
                                   args.NewChannelId);
    }

    private void HandleAttentionMessage(AttentionMessageEventArgs args,
                                        TaskCompletionSource<OpenChannelClientResponse> tsc)
    {
        if (args.ChannelId != _channelId)
            return;

        _logger.LogError(
            "Received attention message from peer {peerId} for channel {channelId}: {message}",
            args.PeerPubKey, args.ChannelId, args.Message);

        tsc.TrySetException(new ChannelErrorException($"Error opening channel: {args.Message}"));
    }

    private void HandlePeerDisconnection(PeerDisconnectedEventArgs args, CompactPubKey peerPubKey,
                                         TaskCompletionSource<OpenChannelClientResponse> tsc)
    {
        if (args.PeerPubKey != peerPubKey)
            return;

        _logger.LogError("Peer disconnected without notice");
        tsc.TrySetException(new ConnectionException("Error opening channel: Peer disconnected"));
    }

    private void HandleExceptionRaised(Exception e, TaskCompletionSource<OpenChannelClientResponse> tsc)
    {
        if (e is not ChannelErrorException ce || ce.ChannelId != _channelId)
            return;

        _logger.LogError("Exception raised while opening channel: {message}", e.Message);
        tsc.TrySetException(e);
    }
}