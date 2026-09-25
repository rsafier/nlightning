using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Node.Managers;

using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Constants;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Infrastructure.Protocol.Models;
using Infrastructure.Transport.Events;
using Infrastructure.Transport.Interfaces;
using Services;

/// <summary>
/// Service for managing peers.
/// </summary>
/// <remarks>
/// Each connected peer gets one ordered inbound loop and one ordered <see cref="PeerOutbox"/> (BOLT2 plan D2, §3.7):
/// its channel messages are handled one at a time, in arrival order, and every reply (and every message raised through
/// <see cref="IChannelManager.OnResponseMessageReady"/>) is sent through the outbox in the order it was enqueued.
/// </remarks>
/// <seealso cref="IPeerManager" />
public sealed class PeerManager : IPeerManager
{
    /// <summary>
    /// Channel messages waiting for the inbound loop of one peer. When full, the transport read loop waits, which
    /// pushes back on a peer that sends faster than we process.
    /// </summary>
    private const int InboundQueueCapacity = 1024;

    private readonly IChannelManager _channelManager;
    private readonly ILogger<PeerManager> _logger;
    private readonly IPeerServiceFactory _peerServiceFactory;
    private readonly ITcpService _tcpService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<CompactPubKey, PeerSession> _peers = new();
    private readonly ConcurrentDictionary<CompactPubKey, Task> _reconnectLoops = new();
    private CancellationTokenSource _reconnectCts = new();

    private CancellationTokenSource? _cts;

    /// <summary>
    /// Wait before the first reconnection attempt to a peer we could not reach on startup. Doubles after every
    /// failed attempt, up to <see cref="ReconnectMaxDelay"/>.
    /// </summary>
    internal TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest wait between two reconnection attempts.
    /// </summary>
    internal TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromMinutes(10);

    public PeerManager(IChannelManager channelManager, ILogger<PeerManager> logger,
                       IPeerServiceFactory peerServiceFactory, ITcpService tcpService, IServiceProvider serviceProvider)
    {
        _channelManager = channelManager;
        _logger = logger;
        _peerServiceFactory = peerServiceFactory;
        _tcpService = tcpService;
        _serviceProvider = serviceProvider;

        // Subscribed here (not in StartAsync) so no message raised by the channel manager can miss the outbox
        _channelManager.OnResponseMessageReady += HandleResponseMessageReady;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        _tcpService.OnNewPeerConnected += HandleNewPeerConnected;

        // Load peers and initialize the channel manager
        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var peers = await uow.GetPeersForStartupAsync();

        // Register every channel of every peer (memory and signer) before the first connection: a peer may send
        // channel_reestablish right after init (NL-201). Channels of unreachable peers are registered too, so
        // blockchain events (funding confirmations, stale-channel handling) still apply to them (NL-052).
        foreach (var peer in peers)
            await RegisterExistingChannelsAsync(peer);

        foreach (var peer in peers)
        {
            try
            {
                _ = await ConnectToPeerAsync(peer.PeerAddressInfo, uow);
                continue;
            }
            catch (InvalidOperationException)
            {
                // Already connected (the peer connected to us first)
                continue;
            }
            catch (ConnectionException)
            {
                _logger.LogWarning("Unable to connect to peer {PeerId} on startup", peer.NodeId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error connecting to peer {PeerId} on startup", peer.NodeId);
            }

            if (HasActiveChannels(peer))
                StartReconnectLoop(peer);
        }

        await uow.SaveChangesAsync();

        await _tcpService.StartListeningAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            throw new InvalidOperationException($"{nameof(PeerManager)} is not running");

        // Stop accepting connections and release the listening sockets, so a restart can bind the same port
        try
        {
            await _tcpService.StopListeningAsync();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Error stopping the TCP listeners");
        }

        // Stop reconnecting first, so no loop connects a peer while we disconnect them
        await _reconnectCts.CancelAsync();
        try
        {
            await Task.WhenAll(_reconnectLoops.Values);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Reconnect loop ended with an error");
        }

        foreach (var peerKey in _peers.Keys)
            try
            {
                DisconnectPeer(peerKey);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Error disconnecting peer {Peer}", peerKey);
            }

        try
        {
            // Give it a 5-second timeout to disconnect all peers
            var timeoutTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!_peers.IsEmpty && !_cts.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(1), timeoutTokenSource.Token);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Timeout while waiting for peers to disconnect");
        }

        await _cts.CancelAsync();
    }

    /// <inheritdoc />
    /// <exception cref="ConnectionException">Thrown when the connection to the peer fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the connection to the peer already exists.</exception>
    public async Task<PeerModel> ConnectToPeerAsync(PeerAddressInfo peerAddressInfo)
    {
        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var peer = await ConnectToPeerAsync(peerAddressInfo, uow);

        await uow.SaveChangesAsync();

        return peer;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Disconnects right away (an operator action), without waiting for the peer's outbox to drain. Failures while
    /// handling the peer's messages disconnect through the outbox instead, after the replies queued before them.
    /// </remarks>
    public void DisconnectPeer(CompactPubKey pubKey, Exception? exception = null)
    {
        if (_peers.TryGetValue(pubKey, out var session))
            session.PeerService.Disconnect(exception);
        else
            _logger.LogWarning("Peer {Peer} not found", pubKey);
    }

    public List<PeerModel> ListPeers()
    {
        return _peers.Values.Select(session => session.Peer).ToList();
    }

    public PeerModel? GetPeer(CompactPubKey peerId)
    {
        return _peers.TryGetValue(peerId, out var session) ? session.Peer : null;
    }

    /// <summary>
    /// Registers (and waits for) every channel of <paramref name="peer"/> that is not Closed or Stale. A channel that
    /// fails to register is logged and skipped; the others are still registered.
    /// </summary>
    private async Task RegisterExistingChannelsAsync(PeerModel peer)
    {
        if (peer.Channels is not { Count: > 0 })
            return;

        foreach (var channel in peer.Channels.Where(IsActiveChannel))
        {
            try
            {
                await _channelManager.RegisterExistingChannelAsync(channel);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error registering channel {ChannelId} for peer {PeerId} on startup",
                                 channel.ChannelId, peer.NodeId);
            }
        }
    }

    private static bool IsActiveChannel(ChannelModel channel) =>
        channel.State is not (ChannelState.Closed or ChannelState.Stale);

    private static bool HasActiveChannels(PeerModel peer) =>
        peer.Channels is { Count: > 0 } && peer.Channels.Any(IsActiveChannel);

    /// <summary>
    /// Keeps trying to connect to a peer we could not reach on startup (it has channels with us), with exponential
    /// backoff, until it is connected (by us or by itself) or the manager stops.
    /// </summary>
    private void StartReconnectLoop(PeerModel peer)
    {
        var token = _reconnectCts.Token;
        _reconnectLoops.GetOrAdd(peer.NodeId, _ => Task.Run(() => ReconnectWithBackoffAsync(peer, token), token));
    }

    private async Task ReconnectWithBackoffAsync(PeerModel peer, CancellationToken cancellationToken)
    {
        try
        {
            var delay = ReconnectInitialDelay;
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, cancellationToken);

                if (_peers.ContainsKey(peer.NodeId))
                    return;

                try
                {
                    await ConnectToPeerAsync(peer.PeerAddressInfo);
                    _logger.LogInformation("Reconnected to peer {PeerId}", peer.NodeId);
                    return;
                }
                catch (InvalidOperationException)
                {
                    // The peer connected to us in the meantime
                    return;
                }
                catch (Exception e)
                {
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, ReconnectMaxDelay.Ticks));
                    _logger.LogDebug(e, "Unable to reconnect to peer {PeerId}, retrying in {Delay}", peer.NodeId,
                                     delay);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping
        }
        finally
        {
            _reconnectLoops.TryRemove(peer.NodeId, out _);
        }
    }

    private async Task<PeerModel> ConnectToPeerAsync(PeerAddressInfo peerAddressInfo, IUnitOfWork uow)
    {
        // Convert and validate the address
        var peerAddress = new PeerAddress(peerAddressInfo.Address);

        // Check if we're already connected to the peer
        if (_peers.ContainsKey(peerAddress.PubKey))
        {
            throw new InvalidOperationException($"Already connected to peer {peerAddress.PubKey}");
        }

        // Connect to the peer
        var connectedPeer = await _tcpService.ConnectToPeerAsync(peerAddress);

        var peerService = await _peerServiceFactory.CreateConnectedPeerAsync(connectedPeer.CompactPubKey,
                                                                             connectedPeer.TcpClient);

        var preferredHost = connectedPeer.Host;
        var preferredPort = connectedPeer.Port;

        // Check if the node has set it's preferred address
        if (peerService.PreferredHost is not null)
            preferredHost = peerService.PreferredHost;

        if (peerService.PreferredPort is not null)
            preferredPort = peerService.PreferredPort.Value;

        var peer = new PeerModel(connectedPeer.CompactPubKey, preferredHost, preferredPort,
                                 connectedPeer.TcpClient.Client.ProtocolType == ProtocolType.IPv6 ? "IPv6" : "IPv4")
        {
            LastSeenAt = DateTime.UtcNow
        };
        peer.SetPeerService(peerService);

        if (!TryAddSession(peer, peerService))
        {
            // Another connection to the same peer won the race
            peerService.Disconnect(new ConnectionException($"Already connected to peer {peer.NodeId}"));
            throw new InvalidOperationException($"Already connected to peer {peer.NodeId}");
        }

        await uow.PeerDbRepository.AddOrUpdateAsync(peer);

        return peer;
    }

    private void HandleNewPeerConnected(object? _, NewPeerConnectedEventArgs args)
    {
        try
        {
            // Create the peer
            var peerService = _peerServiceFactory.CreateConnectingPeerAsync(args.TcpClient).GetAwaiter().GetResult();

            _logger.LogTrace("PeerService created for peer {PeerPubKey}", peerService.PeerPubKey);

            var preferredHost = args.Host;
            var preferredPort = NodeConstants.DefaultPort;

            // Check if the node has set it's preferred address
            if (peerService.PreferredHost is not null)
                preferredHost = peerService.PreferredHost;

            if (peerService.PreferredPort is not null)
                preferredPort = peerService.PreferredPort.Value;

            var peer = new PeerModel(peerService.PeerPubKey, preferredHost, preferredPort,
                                     args.TcpClient.Client.ProtocolType == ProtocolType.IPv6 ? "IPv6" : "IPv4")
            {
                LastSeenAt = DateTime.UtcNow
            };
            peer.SetPeerService(peerService);

            if (preferredHost != "127.0.0.1")
            {
                // Get a context to save the peer to the database
                using var scope = _serviceProvider.CreateScope();
                using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                uow.PeerDbRepository.AddOrUpdateAsync(peer);
                uow.SaveChanges();
            }

            if (!TryAddSession(peer, peerService))
            {
                _logger.LogWarning("Already connected to peer {Peer}, closing the new connection", peer.NodeId);
                peerService.Disconnect(new ConnectionException($"Already connected to peer {peer.NodeId}"));
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error handling new peer connection from {Host}:{Port}", args.Host, args.Port);
        }
    }

    /// <summary>
    /// Registers the peer with its inbound loop and outbox, then subscribes to its events (so every message we get
    /// from it finds its session). Returns false when the peer is already connected.
    /// </summary>
    private bool TryAddSession(PeerModel peer, IPeerService peerService)
    {
        var session = new PeerSession(peer, peerService, new PeerOutbox(peerService, _logger));
        if (!_peers.TryAdd(peer.NodeId, session))
        {
            session.Close();
            return false;
        }

        session.StartInboundLoop(ProcessInboundMessagesAsync);

        peerService.OnDisconnect += HandlePeerDisconnection;
        peerService.OnChannelMessageReceived += HandlePeerChannelMessage;

        return true;
    }

    private void HandlePeerDisconnection(object? sender, PeerDisconnectedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Only remove the session of the connection that went down (a newer one may already have replaced it)
        if (_peers.TryGetValue(args.PeerPubKey, out var session)
         && (sender is null || ReferenceEquals(session.PeerService, sender))
         && _peers.TryRemove(new KeyValuePair<CompactPubKey, PeerSession>(args.PeerPubKey, session)))
            session.Close();

        _logger.LogInformation("Peer {Peer} disconnected", args.PeerPubKey);

        if (sender is IPeerService peerService)
        {
            peerService.OnDisconnect -= HandlePeerDisconnection;
            peerService.OnChannelMessageReceived -= HandlePeerChannelMessage;
            peerService.Dispose();
        }
        else
        {
            _logger.LogWarning("Peer {Peer} disconnected, but we were unable to detach event handlers",
                               args.PeerPubKey);
        }
    }

    /// <summary>
    /// Queues a channel message for the peer's inbound loop. Runs on the transport read loop, so it never processes
    /// the message itself; it only waits when the queue is full.
    /// </summary>
    private void HandlePeerChannelMessage(object? _, ChannelMessageEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!_peers.TryGetValue(args.PeerPubKey, out var session))
        {
            _logger.LogWarning("Dropping channel message ({messageType}) from unknown peer {Peer}",
                               Enum.GetName(args.Message.Type), args.PeerPubKey);
            return;
        }

        if (!session.TryQueueInbound(args.Message))
            _logger.LogDebug("Dropping channel message ({messageType}) from peer {Peer}: the peer is disconnecting",
                             Enum.GetName(args.Message.Type), args.PeerPubKey);
    }

    /// <summary>
    /// The peer's inbound loop: one message at a time, in arrival order. <see cref="IChannelManager"/> enqueues the
    /// replies into the outbox before the call returns, so they are always ahead of the next message's replies.
    /// Stops after a failure that disconnects the peer.
    /// </summary>
    private async Task ProcessInboundMessagesAsync(PeerSession session, ChannelReader<IChannelMessage> inbound)
    {
        await foreach (var message in inbound.ReadAllAsync())
        {
            if (await ProcessChannelMessageAsync(session, message))
                continue;

            session.Close();
            return;
        }
    }

    /// <summary>
    /// Handles one channel message. Returns false when the peer is being disconnected.
    /// </summary>
    private async Task<bool> ProcessChannelMessageAsync(PeerSession session, IChannelMessage message)
    {
        try
        {
            await _channelManager.HandleChannelMessageAsync(message, session.PeerService.Features,
                                                            session.PeerService.PeerPubKey);
            return true;
        }
        catch (Exception e)
        {
            // The id (or temporary id) of the channel this message belongs to, so failures stay scoped to that channel
            return HandleChannelMessageFailure(session, e, message.Type, message.Payload?.ChannelId);
        }
    }

    /// <summary>
    /// Turns a failure to handle a channel message into a message for the peer, sent through its outbox after
    /// whatever was already queued. Returns false when the peer is being disconnected.
    /// </summary>
    /// <remarks>
    /// BOLT 1: an `error` with an all-zero channel_id makes the peer fail every channel with us, so a channel failure
    /// always carries <paramref name="channelId"/>:
    /// - <see cref="ChannelErrorException"/>: `error` for the channel, then disconnect.
    /// - <see cref="ChannelWarningException"/> (includes the messages we don't implement yet): `warning` for the
    ///   channel, and the connection stays up, unless <see cref="ChannelWarningException.CloseConnection"/> is set
    ///   (then the warning is sent and the peer disconnected).
    /// - Any other exception (an internal failure): `warning` for the channel, then disconnect, so the channel is not
    ///   failed because of our own bug.
    /// </remarks>
    private bool HandleChannelMessageFailure(PeerSession session, Exception exception, MessageTypes messageType,
                                             ChannelId? channelId)
    {
        var peerPubKey = session.PeerService.PeerPubKey;

        if (exception is ChannelErrorException cee)
        {
            _logger.LogError(
                "Error handling channel message ({messageType}) from peer {peer}: {message}",
                Enum.GetName(messageType), peerPubKey,
                !string.IsNullOrEmpty(cee.PeerMessage)
                    ? cee.PeerMessage
                    : cee.Message);

            if (!IsChannelScoped(cee.ChannelId) && IsChannelScoped(channelId))
                cee = new ChannelErrorException(cee.Message, channelId, cee, cee.PeerMessage);

            session.Outbox.TryEnqueueDisconnect(cee);
            return false;
        }

        if (exception is ChannelWarningException cwe)
        {
            _logger.LogWarning(
                "Error handling channel message ({messageType}) from peer {peer}: {message}",
                Enum.GetName(messageType), peerPubKey,
                !string.IsNullOrEmpty(cwe.PeerMessage)
                    ? cwe.PeerMessage
                    : cwe.Message);

            if (!IsChannelScoped(cwe.ChannelId) && IsChannelScoped(channelId))
                cwe = new ChannelWarningException(cwe.Message, channelId, cwe, cwe.PeerMessage)
                {
                    CloseConnection = cwe.CloseConnection
                };

            if (cwe.CloseConnection)
            {
                // "Send a `warning` and close the connection": Disconnect sends the warning first
                session.Outbox.TryEnqueueDisconnect(cwe);
                return false;
            }

            session.Outbox.TryEnqueueWarning(cwe);
            return true;
        }

        _logger.LogError(exception, "Error handling channel message ({messageType}) from peer {peer}",
                         Enum.GetName(messageType), peerPubKey);

        // Our own failure: tell the peer (never with an `error`, which would fail the channel) and disconnect
        var warning = IsChannelScoped(channelId)
                          ? new ChannelWarningException($"Internal error handling {Enum.GetName(messageType)}",
                                                        channelId, exception, "Sorry, we had an internal error")
                          : new WarningException("Sorry, we had an internal error");
        session.Outbox.TryEnqueueDisconnect(warning);
        return false;
    }

    /// <summary>
    /// True when <paramref name="channelId"/> names a single channel (it is set and not all-zero).
    /// </summary>
    private static bool IsChannelScoped(ChannelId? channelId)
    {
        return channelId is not null && channelId.Value != ChannelId.Zero;
    }

    /// <summary>
    /// Enqueues a message raised by the channel manager (a reply or an event such as channel_ready). Runs while the
    /// channel manager holds the channel's lock, so it must never block or throw.
    /// </summary>
    private void HandleResponseMessageReady(object? sender, ChannelResponseMessageEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!_peers.TryGetValue(args.PeerPubKey, out var session))
        {
            _logger.LogWarning("Peer {Peer} not connected, dropping {messageType}", args.PeerPubKey,
                               Enum.GetName(args.ResponseMessage.Type));
            return;
        }

        if (!session.Outbox.TryEnqueue(args.ResponseMessage))
            _logger.LogWarning("Peer {Peer} is disconnecting, dropping {messageType}", args.PeerPubKey,
                               Enum.GetName(args.ResponseMessage.Type));
    }

    /// <summary>
    /// A connected peer: its model, its service, its ordered inbound queue and its outbox.
    /// </summary>
    private sealed class PeerSession
    {
        private readonly Channel<IChannelMessage> _inbound = Channel.CreateBounded<IChannelMessage>(
            new BoundedChannelOptions(InboundQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        public PeerModel Peer { get; }
        public IPeerService PeerService { get; }
        public PeerOutbox Outbox { get; }

        public PeerSession(PeerModel peer, IPeerService peerService, PeerOutbox outbox)
        {
            Peer = peer;
            PeerService = peerService;
            Outbox = outbox;
        }

        public void StartInboundLoop(Func<PeerSession, ChannelReader<IChannelMessage>, Task> loop)
        {
            _ = Task.Run(() => loop(this, _inbound.Reader));
        }

        public bool TryQueueInbound(IChannelMessage message)
        {
            if (_inbound.Writer.TryWrite(message))
                return true;

            try
            {
                // Full: make the transport read loop wait (backpressure) instead of dropping or reordering
                _inbound.Writer.WriteAsync(message).AsTask().GetAwaiter().GetResult();
                return true;
            }
            catch (ChannelClosedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Stops accepting inbound messages and closes the outbox (what is already queued there is still sent).
        /// </summary>
        public void Close()
        {
            _inbound.Writer.TryComplete();
            Outbox.Complete();
        }
    }
}