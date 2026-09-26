using System.Text.Unicode;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Node.Services;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.Gossip.Interfaces;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;

// TODO: Eventually move this to the Application layer
/// <summary>
/// Service for peer communication
/// </summary>
/// <remarks>
/// The transport read loop is already running when the constructor returns, so a channel message can arrive before
/// anyone subscribed to <see cref="OnChannelMessageReceived"/> (e.g. the channel_reestablish LND sends right after
/// init). Such messages are kept, in order, and handed to the first subscriber. Likewise, a subscriber to
/// <see cref="OnDisconnect"/> that comes after the disconnection is told right away, so nobody keeps a dead peer.
/// </remarks>
public sealed class PeerService : IPeerService
{
    /// <summary>
    /// Channel messages kept while nobody is subscribed to <see cref="OnChannelMessageReceived"/>. A peer that sends
    /// more before we subscribe is disconnected.
    /// </summary>
    internal const int MaxPendingChannelMessages = 1024;

    /// <summary>
    /// <c>channel_update</c>s kept while nobody is subscribed to <see cref="OnChannelUpdateReceived"/>; later ones
    /// are dropped (gossip is not critical, and the peer sends a fresh one when its policy changes).
    /// </summary>
    internal const int MaxPendingChannelUpdates = 64;

    /// <summary>
    /// The <c>timestamp_range</c> of the bootstrap <c>gossip_timestamp_filter</c>: with <c>first_timestamp</c> 0 it
    /// asks for every message the peer knows (LND dumps its graph only after a filter, plan BOLT7 G0-T5).
    /// </summary>
    internal const uint FullTimestampRange = uint.MaxValue;

    private readonly IPeerCommunicationService _peerCommunicationService;
    private readonly ILogger<PeerService> _logger;
    private readonly IGossipIngress? _gossipIngress;
    private readonly ChainHash _chainHash;
    private readonly Lock _channelMessageLock = new();
    private readonly Queue<ChannelMessageEventArgs> _pendingChannelMessages = new();
    private readonly Lock _disconnectLock = new();
    private readonly Lock _channelUpdateLock = new();
    private readonly Queue<ChannelUpdateMessage> _pendingChannelUpdates = new();

    /// <summary>
    /// Completes when the peer's init is accepted; fails when the connection closes before that.
    /// </summary>
    private readonly TaskCompletionSource _initReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile bool _isInitialized;
    private EventHandler<ChannelMessageEventArgs>? _onChannelMessageReceived;
    private EventHandler<PeerDisconnectedEventArgs>? _onDisconnect;
    private EventHandler<ChannelUpdateMessage>? _onChannelUpdateReceived;
    private PeerDisconnectedEventArgs? _disconnectedArgs;

    /// <inheritdoc/>
    /// <remarks>Subscribing after the disconnection calls the handler right away (once).</remarks>
    public event EventHandler<PeerDisconnectedEventArgs>? OnDisconnect
    {
        add
        {
            PeerDisconnectedEventArgs? disconnectedArgs;
            lock (_disconnectLock)
            {
                disconnectedArgs = _disconnectedArgs;
                if (disconnectedArgs is null)
                    _onDisconnect += value;
            }

            if (disconnectedArgs is not null)
                value?.Invoke(this, disconnectedArgs);
        }
        remove
        {
            lock (_disconnectLock)
                _onDisconnect -= value;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The first subscriber first gets, in order, the channel messages that arrived while nobody was subscribed.
    /// Handlers run on the transport read loop, one message at a time.
    /// </remarks>
    public event EventHandler<ChannelMessageEventArgs>? OnChannelMessageReceived
    {
        add
        {
            lock (_channelMessageLock)
            {
                _onChannelMessageReceived += value;
                if (value is null)
                    return;

                while (_pendingChannelMessages.TryDequeue(out var args))
                    value(this, args);
            }
        }
        remove
        {
            lock (_channelMessageLock)
                _onChannelMessageReceived -= value;
        }
    }

    /// <inheritdoc/>
    public event EventHandler<AttentionMessageEventArgs>? OnAttentionMessageReceived;

    /// <inheritdoc/>
    public event EventHandler<ChannelUpdateMessage>? OnChannelUpdateReceived
    {
        add
        {
            lock (_channelUpdateLock)
            {
                _onChannelUpdateReceived += value;
                if (value is null)
                    return;

                while (_pendingChannelUpdates.TryDequeue(out var update))
                    value(this, update);
            }
        }
        remove
        {
            lock (_channelUpdateLock)
                _onChannelUpdateReceived -= value;
        }
    }

    /// <inheritdoc/>
    public event EventHandler<Exception>? OnExceptionRaised;

    /// <inheritdoc/>
    public CompactPubKey PeerPubKey => _peerCommunicationService.PeerCompactPubKey;

    /// <inheritdoc />
    /// <remarks>Never set (NL-344): <c>remote_addr</c> is our address, see <see cref="ObservedAddress"/>.</remarks>
    public string? PreferredHost => null;

    /// <inheritdoc />
    public ushort? PreferredPort => null;

    /// <inheritdoc />
    public AddressDescriptor? ObservedAddress { get; private set; }

    public FeatureOptions Features { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset? LastMessageReceivedAt => _peerCommunicationService.LastMessageReceivedAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerService"/> class.
    /// </summary>
    /// <param name="peerCommunicationService">The peer communication service</param>
    /// <param name="features">The feature options</param>
    /// <param name="logger">A logger</param>
    /// <param name="networkTimeout">Network timeout</param>
    /// <param name="gossipIngress">
    /// Where graph gossip (256/257/258) goes, and whether to ask the peer for its graph after init; null drops graph
    /// gossip (the <c>channel_update</c> event still fires).
    /// </param>
    public PeerService(IPeerCommunicationService peerCommunicationService, FeatureOptions features,
                       ILogger<PeerService> logger, TimeSpan networkTimeout, IGossipIngress? gossipIngress = null)
    {
        _peerCommunicationService = peerCommunicationService;
        Features = features;
        _logger = logger;
        _gossipIngress = gossipIngress;
        _chainHash = features.ChainHashes.Any() ? features.ChainHashes.First() : ChainConstants.Main;

        // Nobody has to observe a failed init wait (e.g. a connection that closes before anyone asked)
        _ = _initReceived.Task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                                            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        // Set up event handlers. MessageReceived last: subscribing to it starts reading from the peer (NL-239), and a
        // bad first message disconnects, which must already reach HandleDisconnection.
        _peerCommunicationService.ExceptionRaised += HandleException;
        _peerCommunicationService.DisconnectEvent += HandleDisconnection;
        _peerCommunicationService.MessageReceived += HandleMessage;

        // Initialize communication
        try
        {
            _peerCommunicationService.InitializeAsync(networkTimeout).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // Close the connection: nobody will own this service, and a half-set-up connection that stays open looks
            // alive to the other end (NL-240)
            var connectionException = new ConnectionException("Error initializing peer communication", e);
            _initReceived.TrySetException(connectionException);
            Dispose();
            throw connectionException;
        }
    }

    /// <inheritdoc/>
    public Task WaitForInitAsync(CancellationToken cancellationToken = default)
    {
        return _initReceived.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Disconnects from the peer.
    /// </summary>
    public void Disconnect(Exception? exception = null)
    {
        _logger.LogInformation("Disconnecting peer {peer}", PeerPubKey);
        _peerCommunicationService.Disconnect(exception);
    }

    /// <inheritdoc />
    public Task<bool> PingAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        _peerCommunicationService.PingAsync(timeout, cancellationToken);

    /// <inheritdoc />
    public Task SendMessageAsync(IChannelMessage replyMessage)
    {
        return _peerCommunicationService.SendMessageAsync(replyMessage);
    }

    public Task SendWarningAsync(WarningException we)
    {
        return _peerCommunicationService.SendWarningAsync(we);
    }

    /// <inheritdoc/>
    public Task SendErrorAsync(ErrorMessage errorMessage)
    {
        ArgumentNullException.ThrowIfNull(errorMessage);
        return _peerCommunicationService.SendMessageAsync(errorMessage);
    }

    /// <inheritdoc/>
    public Task SendGossipMessageAsync(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type is < MessageTypes.ChannelAnnouncement or > MessageTypes.GossipTimestampFilter)
            throw new ArgumentException($"{Enum.GetName(message.Type) ?? message.Type.ToString()} is not a gossip message",
                                        nameof(message));

        return _peerCommunicationService.SendMessageAsync(message);
    }

    /// <summary>
    /// Handles messages received from the peer.
    /// </summary>
    private void HandleMessage(object? sender, IMessage? message)
    {
        if (message is null)
            return;

        if (!_isInitialized)
        {
            HandleInitialization(message);
        }
        else if (message is IChannelMessage channelMessage)
        {
            _logger.LogTrace("Received channel message ({messageType}) from peer {peer}",
                             Enum.GetName(message.Type), PeerPubKey);

            RaiseChannelMessage(new ChannelMessageEventArgs(channelMessage, PeerPubKey));
        }
        else if (message is ErrorMessage errorMessage)
        {
            var errorMessageString = string.Empty;
            ChannelId? channelId = null;
            if (errorMessage.Payload.ChannelId != ChannelId.Zero)
                channelId = errorMessage.Payload.ChannelId;

            if (errorMessage.Payload.Data is not null)
            {
                // Try to get utf8 string from error data
                errorMessageString = Utf8.IsValid(errorMessage.Payload.Data)
                                         ? System.Text.Encoding.UTF8.GetString(errorMessage.Payload.Data)
#if NET9_0_OR_GREATER
                                         : Convert.ToHexStringLower(errorMessage.Payload.Data);
#else
                                         : Convert.ToHexString(errorMessage.Payload.Data).ToLowerInvariant();
#endif

                _logger.LogError(
                    "Received error message from peer {peer} for channel {channelId}: {errorMessage}",
                    PeerPubKey, channelId is null ? "" : channelId.ToString(), errorMessageString);
            }

            OnAttentionMessageReceived?.Invoke(
                this, new AttentionMessageEventArgs(errorMessageString, PeerPubKey, channelId));
        }
        else if (message is WarningMessage warningMessage)
        {
            var warningMessageString = string.Empty;
            ChannelId? channelId = null;
            if (warningMessage.Payload.ChannelId != ChannelId.Zero)
                channelId = warningMessage.Payload.ChannelId;

            if (warningMessage.Payload.Data is not null)
            {
                // Try to get utf8 string from error data
                warningMessageString = Utf8.IsValid(warningMessage.Payload.Data)
                                           ? System.Text.Encoding.UTF8.GetString(warningMessage.Payload.Data)
#if NET9_0_OR_GREATER
                                           : Convert.ToHexStringLower(warningMessage.Payload.Data);
#else
                                         : Convert.ToHexString(warningMessage.Payload.Data).ToLowerInvariant();
#endif

                _logger.LogError(
                    "Received error message from peer {peer} for channel {channelId}: {errorMessage}",
                    PeerPubKey, channelId is null ? "" : channelId.ToString(), warningMessageString);
            }

            OnAttentionMessageReceived?.Invoke(
                this, new AttentionMessageEventArgs(warningMessageString, PeerPubKey, channelId));
        }
        else if (message is StfuMessage stfuMessage)
        {
            // Quiescence (BOLT 2, option_quiesce) is not implemented, so we can never reply with our own stfu. The
            // sender now considers the channel quiescing and stops sending updates; the only spec-defined way out is
            // a disconnection. So send a channel-scoped warning and disconnect (the channel is NOT failed).
            _logger.LogWarning("Received stfu for channel {channelId} from peer {peer}, but quiescence is not supported",
                               stfuMessage.Payload.ChannelId, PeerPubKey);

            Disconnect(new ChannelWarningException("Received stfu, but quiescence is not supported",
                                                   stfuMessage.Payload.ChannelId,
                                                   "Quiescence (stfu) is not supported"));
        }
        else if (message is QueryChannelRangeMessage queryChannelRangeMessage)
        {
            // BOLT 7: MUST respond with one or more reply_channel_range. We know no public channels yet.
            _logger.LogDebug("Answering query_channel_range from peer {peer}", PeerPubKey);
            _ = SendGossipReplyAsync(GossipQueryResponder.CreateReply(queryChannelRangeMessage));
        }
        else if (message is QueryShortChannelIdsMessage queryShortChannelIdsMessage)
        {
            // BOLT 7: MUST follow the (here empty) responses with reply_short_channel_ids_end
            _logger.LogDebug("Answering query_short_channel_ids from peer {peer}", PeerPubKey);
            try
            {
                _ = SendGossipReplyAsync(GossipQueryResponder.CreateReply(queryShortChannelIdsMessage));
            }
            catch (WarningException we)
            {
                _logger.LogWarning("Invalid query_short_channel_ids from peer {peer}: {message}", PeerPubKey,
                                   we.Message);
                _ = _peerCommunicationService.SendWarningAsync(we);
            }
        }
        else if (message is ChannelUpdateMessage channelUpdateMessage)
        {
            // BOLT 7: checked (chain, channel, signature) and stored by the subscriber (our own channels, W1-E), and
            // by the graph ingress (public channels, G2-T4), each with its own checks
            _logger.LogDebug("Received channel_update for {shortChannelId} from peer {peer}",
                             channelUpdateMessage.Payload.ShortChannelId, PeerPubKey);
            RaiseChannelUpdate(channelUpdateMessage);
            _gossipIngress?.TryEnqueue(this, channelUpdateMessage);
        }
        else if (message is GossipTimestampFilterMessage)
        {
            // We never relay gossip (and generate none yet), so there is nothing to filter: accept and ignore
            _logger.LogDebug("Ignoring gossip_timestamp_filter from peer {peer}", PeerPubKey);
        }
        else if (message is ChannelAnnouncementMessage or NodeAnnouncementMessage)
        {
            // BOLT 7 graph gossip: validated (signatures, funding output) and stored by the graph ingress (G2-T4),
            // which warns the peer itself; without an ingress (or with the graph disabled) it is dropped.
            // announcement_signatures (259) is a channel message: it takes the IChannelMessage arm above (G0-T2)
            if (_gossipIngress?.TryEnqueue(this, message) != true)
                _logger.LogTrace("Dropping gossip message ({messageType}) from peer {peer}",
                                 Enum.GetName(message.Type), PeerPubKey);
        }
        else if (message is ReplyChannelRangeMessage or ReplyShortChannelIdsEndMessage)
        {
            // We never query yet (G3): accept the message so the connection stays up, and drop it
            _logger.LogDebug("Dropping gossip message ({messageType}) from peer {peer}",
                             Enum.GetName(message.Type), PeerPubKey);
        }
    }

    /// <summary>
    /// Hands a channel message to the subscribers, or keeps it until the first one subscribes. Holding the lock
    /// while calling them keeps the arrival order, also against the replay in the subscription.
    /// </summary>
    private void RaiseChannelMessage(ChannelMessageEventArgs args)
    {
        lock (_channelMessageLock)
        {
            if (_onChannelMessageReceived is not null)
            {
                _onChannelMessageReceived(this, args);
                return;
            }

            if (_pendingChannelMessages.Count < MaxPendingChannelMessages)
            {
                _pendingChannelMessages.Enqueue(args);
                return;
            }
        }

        _logger.LogWarning("Too many channel messages from peer {peer} before we were ready, disconnecting",
                           PeerPubKey);
        Disconnect(new ConnectionException("Too many channel messages before the peer was ready"));
    }

    /// <summary>
    /// Hands a channel_update to the subscribers, or keeps it (up to <see cref="MaxPendingChannelUpdates"/>) until the
    /// first one subscribes.
    /// </summary>
    private void RaiseChannelUpdate(ChannelUpdateMessage message)
    {
        lock (_channelUpdateLock)
        {
            if (_onChannelUpdateReceived is not null)
            {
                _onChannelUpdateReceived(this, message);
                return;
            }

            if (_pendingChannelUpdates.Count < MaxPendingChannelUpdates)
            {
                _pendingChannelUpdates.Enqueue(message);
                return;
            }
        }

        _logger.LogDebug("Dropping channel_update from peer {peer}: too many before anyone listened", PeerPubKey);
    }

    private async Task SendGossipReplyAsync(IMessage reply)
    {
        try
        {
            await _peerCommunicationService.SendMessageAsync(reply);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to send {messageType} to peer {peer}", Enum.GetName(reply.Type),
                               PeerPubKey);
        }
    }

    /// <summary>
    /// Handles exceptions raised by the communication service.
    /// </summary>
    private void HandleException(object? sender, Exception e)
    {
        _logger.LogError(e, "Exception occurred with peer {peer}", PeerPubKey);
        OnExceptionRaised?.Invoke(this, e);
    }

    private void HandleDisconnection(object? sender, Exception? e)
    {
        _logger.LogTrace(e, "Handling disconnection for peer {Peer}", PeerPubKey);
        EventHandler<PeerDisconnectedEventArgs>? handlers;
        PeerDisconnectedEventArgs args;
        lock (_disconnectLock)
        {
            if (_disconnectedArgs is not null)
                return;

            args = _disconnectedArgs = new PeerDisconnectedEventArgs(PeerPubKey, e);
            handlers = _onDisconnect;
        }

        // DisconnectEvent passes null for a disconnection without a reason
        var notInitialized = $"Peer {PeerPubKey} disconnected before its init was accepted";
        _initReceived.TrySetException(e is null
                                          ? new ConnectionException(notInitialized)
                                          : new ConnectionException(notInitialized, e));

        handlers?.Invoke(this, args);
    }

    /// <summary>
    /// Handles the initialization process when receiving the first message.
    /// </summary>
    private void HandleInitialization(IMessage message)
    {
        // Check if the first message is an init message
        if (message.Type != MessageTypes.Init || message is not InitMessage initMessage)
        {
            _logger.LogError("Failed to receive init message from peer {peer}", PeerPubKey);
            // BOLT 1: we must not send anything before receiving init, so just close the connection
            Disconnect(new ConnectionException("Expected init as the first message"));
            return;
        }

        // Check if Features are compatible
        if (!Features.GetNodeFeatures().IsCompatible(initMessage.Payload.FeatureSet, out var negotiatedFeatures)
         || negotiatedFeatures is null)
        {
            _logger.LogError("Peer {peer} is not compatible", PeerPubKey);
            Disconnect(new WarningException("Incompatible features"));
            return;
        }

        // BOLT 1: only close the connection if `networks` has no chain in common with ours
        var networkChainHashes = initMessage.NetworksTlv?.ChainHashes;
        if (networkChainHashes != null && !networkChainHashes.Any(chainHash => Features.ChainHashes.Contains(chainHash)))
        {
            _logger.LogError("Peer {peer} chain is not compatible", PeerPubKey);
            Disconnect(new WarningException("No common chain in networks"));
            return;
        }

        // BOLT 1: remote_addr is the address the peer sees us at. Keep it only as a hint for our own announced
        // addresses, never as the peer's address; an undecodable one is dropped, never fatal (odd TLV, NL-344)
        if (initMessage.RemoteAddressTlv is not null)
        {
            ObservedAddress = initMessage.RemoteAddressTlv.Descriptor;
            _logger.LogDebug("Peer {peer} sees us at {address}", PeerPubKey, ObservedAddress);
        }
        else if (initMessage.UndecodableRemoteAddress is not null)
        {
            _logger.LogWarning("Ignoring the undecodable remote_addr of peer {peer}: {address}", PeerPubKey,
                               Convert.ToHexStringLower(initMessage.UndecodableRemoteAddress));
        }

        Features = FeatureOptions.GetNodeOptions(negotiatedFeatures, initMessage.Extension);
        _logger.LogTrace("Initialization from peer {peer} completed successfully", PeerPubKey);
        _isInitialized = true;
        _initReceived.TrySetResult();

        RequestGossipIfEnabled();
    }

    /// <summary>
    /// The graph bootstrap until the G3 sync exists: a peer that negotiated <c>gossip_queries</c> gets
    /// <c>gossip_timestamp_filter(0, 0xFFFFFFFF)</c> right after init, so it sends us every announcement and update it
    /// knows (LND dumps its graph only after a filter) and then keeps relaying new gossip. A peer without
    /// <c>gossip_queries</c> gets nothing (it sends us its gossip unasked, or not at all); nothing is sent while the
    /// graph is disabled (mainnet by default, plan D12).
    /// </summary>
    private void RequestGossipIfEnabled()
    {
        if (_gossipIngress is not { IsEnabled: true } || Features.GossipQueries == FeatureSupport.No)
            return;

        _logger.LogDebug("Asking peer {peer} for its gossip", PeerPubKey);
        _ = SendGossipReplyAsync(
            new GossipTimestampFilterMessage(new GossipTimestampFilterPayload(_chainHash, 0, FullTimestampRange)));
    }

    public void Dispose()
    {
        _peerCommunicationService.MessageReceived -= HandleMessage;
        _peerCommunicationService.ExceptionRaised -= HandleException;
        _peerCommunicationService.DisconnectEvent -= HandleDisconnection;
        _peerCommunicationService.Dispose();
    }
}