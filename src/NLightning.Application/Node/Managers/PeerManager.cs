using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
using Domain.Node.Options;
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
/// A connection that goes down stops its loop (what is still queued is dropped), and the loop of the next connection
/// to the same peer only starts once the previous one has finished, so messages of two connections never interleave.
/// A connection only becomes the peer's session once the init exchange is done (both inits), so neither end ever
/// keeps a connection the other end cannot use (NL-240).
/// A new connection from a peer we are already connected to replaces the old one (as LND and CLN do), except for a
/// simultaneous connect, where both ends keep the connection initiated by the node with the lower pubkey (LND's rule).
/// A peer with active channels that drops is reconnected with backoff unless we disconnected it on purpose.
/// </remarks>
/// <seealso cref="IPeerManager" />
public sealed class PeerManager : IPeerManager
{
    /// <summary>
    /// Channel messages waiting for the inbound loop of one peer. When full, the transport read loop waits, which
    /// pushes back on a peer that sends faster than we process.
    /// </summary>
    private const int InboundQueueCapacity = 1024;

    private static readonly TimeSpan s_stopTimeout = TimeSpan.FromSeconds(5);

    private readonly IChannelManager _channelManager;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<PeerManager> _logger;
    private readonly IPeerServiceFactory _peerServiceFactory;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly ITcpService _tcpService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<CompactPubKey, PeerSession> _peers = new();
    private readonly ConcurrentDictionary<CompactPubKey, Task> _reconnectLoops = new();

    /// <summary>
    /// Guards installing and removing sessions, and <see cref="_inboundLoops"/>.
    /// </summary>
    private readonly Lock _sessionLock = new();

    /// <summary>
    /// The latest inbound loop of each peer (removed when it ends). A new session waits for it before its own loop
    /// starts.
    /// </summary>
    private readonly Dictionary<CompactPubKey, Task> _inboundLoops = new();

    /// <summary>
    /// The session whose inbound loop is running on this async flow: the replies raised while it handles a message
    /// belong to that connection, even if a newer connection of the same peer already replaced it.
    /// </summary>
    private readonly AsyncLocal<PeerSession?> _currentSession = new();

    private CancellationTokenSource _reconnectCts = new();
    private CancellationTokenSource? _cts;
    private CompactPubKey? _localNodeId;
    private volatile bool _stopping;

    /// <summary>
    /// Wait before the first reconnection attempt to a peer with active channels that we could not reach on startup
    /// or that dropped. Doubles after every failed attempt, up to <see cref="ReconnectMaxDelay"/>.
    /// </summary>
    internal TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest wait between two reconnection attempts.
    /// </summary>
    internal TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A connection in the other direction that arrives within this time of the current one is a simultaneous
    /// connect and goes through the pubkey tie-break; a later one replaces the current connection.
    /// </summary>
    internal TimeSpan SimultaneousConnectWindow { get; set; } = TimeSpan.FromSeconds(5);

    public PeerManager(IChannelManager channelManager, IChannelMemoryRepository channelMemoryRepository,
                       ILogger<PeerManager> logger, IPeerServiceFactory peerServiceFactory,
                       ISecureKeyManager secureKeyManager, ITcpService tcpService, IServiceProvider serviceProvider,
                       IOptions<NodeOptions>? nodeOptions = null)
    {
        if (nodeOptions is not null)
        {
            ReconnectInitialDelay = nodeOptions.Value.ReconnectInitialDelay;
            ReconnectMaxDelay = nodeOptions.Value.ReconnectMaxDelay;
        }

        _channelManager = channelManager;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _peerServiceFactory = peerServiceFactory;
        _secureKeyManager = secureKeyManager;
        _tcpService = tcpService;
        _serviceProvider = serviceProvider;

        // Subscribed here (not in StartAsync) so no message raised by the channel manager can miss the outbox
        _channelManager.OnResponseMessageReady += HandleResponseMessageReady;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = false;
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

        // No reconnection from here on (the disconnections below are ours)
        _stopping = true;

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
            using var timeoutTokenSource = new CancellationTokenSource(s_stopTimeout);
            while (!_peers.IsEmpty && !_cts.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeoutTokenSource.Token);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Timeout while waiting for peers to disconnect");
        }

        // Stop the inbound loops that are left and wait for the message being handled, so no handler still persists
        // while the host disposes the services
        foreach (var session in _peers.Values)
            session.Close();

        Task[] inboundLoops;
        lock (_sessionLock)
            inboundLoops = [.. _inboundLoops.Values];

        try
        {
            await Task.WhenAll(inboundLoops).WaitAsync(s_stopTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timeout while waiting for the peers' inbound loops to stop");
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Inbound loop ended with an error");
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
    /// Disconnects right away (an operator action), without waiting for the peer's outbox to drain, and does not
    /// reconnect. Failures while handling the peer's messages disconnect through the outbox instead, after the replies
    /// queued before them.
    /// </remarks>
    public void DisconnectPeer(CompactPubKey pubKey, Exception? exception = null)
    {
        if (_peers.TryGetValue(pubKey, out var session))
        {
            session.SuppressReconnect();
            session.PeerService.Disconnect(exception);
        }
        else
        {
            _logger.LogWarning("Peer {Peer} not found", pubKey);
        }
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
    /// Keeps trying to connect to a peer we have channels with, with exponential backoff, until it is connected (by
    /// us or by itself) or the manager stops.
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

    /// <summary>
    /// Starts reconnecting to a peer that dropped, if it still has active channels with us and neither we nor a newer
    /// connection closed it on purpose.
    /// </summary>
    private void ReconnectIfNeeded(PeerSession session)
    {
        if (_stopping || _cts is null || session.ReconnectSuppressed || _reconnectCts.IsCancellationRequested)
            return;

        var peerId = session.Peer.NodeId;
        var channels = _channelMemoryRepository.FindChannels(c => c.RemoteNodeId == peerId && IsActiveChannel(c));
        if (channels is not { Count: > 0 })
            return;

        _logger.LogInformation("Peer {PeerId} has active channels, reconnecting", peerId);
        StartReconnectLoop(session.Peer);
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

        // The peer's preferred address and features come with its init
        await WaitForInitAsync(peerService);

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

        var session = CreateSession(peer, peerService, isInbound: false);
        if (!TryInstallSession(session))
        {
            // The peer's own connection to us won the tie-break
            session.SuppressReconnect();
            peerService.Disconnect(new ConnectionException($"Already connected to peer {peer.NodeId}"));
            throw new InvalidOperationException($"Already connected to peer {peer.NodeId}");
        }

        await uow.PeerDbRepository.AddOrUpdateAsync(peer);

        return peer;
    }

    /// <summary>
    /// Waits until the peer's init was accepted. On failure the connection is already closed; this releases the
    /// service and throws.
    /// </summary>
    /// <exception cref="ConnectionException">The connection closed before the init exchange was done.</exception>
    private static async Task WaitForInitAsync(IPeerService peerService)
    {
        try
        {
            await peerService.WaitForInitAsync();
        }
        catch (Exception e)
        {
            peerService.Dispose();
            if (e is ConnectionException)
                throw;

            throw new ConnectionException($"Init exchange with peer {peerService.PeerPubKey} failed", e);
        }
    }

    private void HandleNewPeerConnected(object? _, NewPeerConnectedEventArgs args)
    {
        // Off the TCP service's thread: the init exchange takes a round trip
        _ = HandleNewPeerConnectedAsync(args);
    }

    private async Task HandleNewPeerConnectedAsync(NewPeerConnectedEventArgs args)
    {
        try
        {
            // Create the peer
            var peerService = await _peerServiceFactory.CreateConnectingPeerAsync(args.TcpClient);

            _logger.LogTrace("PeerService created for peer {PeerPubKey}", peerService.PeerPubKey);

            // Only a connection whose init exchange is done may become (or replace) the peer's session
            await WaitForInitAsync(peerService);

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

            // Subscribe and install before anything slow (the database below): the peer may already be sending
            var session = CreateSession(peer, peerService, isInbound: true);
            if (!TryInstallSession(session))
            {
                _logger.LogWarning("Keeping our own connection to peer {Peer}, closing the new one", peer.NodeId);
                session.SuppressReconnect();
                peerService.Disconnect(new ConnectionException($"Already connected to peer {peer.NodeId}"));
                return;
            }

            if (preferredHost != "127.0.0.1")
            {
                // Get a context to save the peer to the database
                using var scope = _serviceProvider.CreateScope();
                using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await uow.PeerDbRepository.AddOrUpdateAsync(peer);
                await uow.SaveChangesAsync();
            }
        }
        catch (ConnectionException e)
        {
            // E.g. the other end kept the connection it initiated and closed this one during init
            _logger.LogInformation("Inbound connection from {Host}:{Port} closed before it was set up: {Message}",
                                   args.Host, args.Port, e.Message);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error handling new peer connection from {Host}:{Port}", args.Host, args.Port);
        }
    }

    /// <summary>
    /// Creates the session of a new connection and subscribes to its peer service right away. A peer service keeps
    /// the channel messages that arrived before this and replays a disconnection that already happened, so nothing
    /// the peer sent right after init is lost and a peer that already dropped is not kept.
    /// </summary>
    private PeerSession CreateSession(PeerModel peer, IPeerService peerService, bool isInbound)
    {
        var session = new PeerSession(peer, peerService, new PeerOutbox(peerService, _logger), isInbound);
        session.ChannelMessageHandler = (_, args) => QueueInboundMessage(session, args);
        session.DisconnectHandler = (_, args) => HandleSessionDisconnected(session, args);

        peerService.OnChannelMessageReceived += session.ChannelMessageHandler;
        peerService.OnDisconnect += session.DisconnectHandler;

        return session;
    }

    /// <summary>
    /// Makes <paramref name="session"/> the peer's session and starts its inbound loop (after the previous
    /// connection's loop has finished). An existing session is replaced and disconnected, unless the tie-break keeps
    /// it: then this returns false and the caller closes the new connection.
    /// </summary>
    private bool TryInstallSession(PeerSession session)
    {
        var peerId = session.Peer.NodeId;
        PeerSession? replaced = null;

        lock (_sessionLock)
        {
            if (_peers.TryGetValue(peerId, out var existing))
            {
                if (!ShouldReplace(existing, session))
                    return false;

                replaced = existing;
                replaced.SuppressReconnect();
                replaced.Close();
            }

            _peers[peerId] = session;

            var previousLoop = _inboundLoops.GetValueOrDefault(peerId) ?? Task.CompletedTask;
            session.StartInboundLoop(previousLoop, ProcessInboundMessagesAsync);
            _inboundLoops[peerId] = session.InboundLoop;
            _ = session.InboundLoop.ContinueWith(_ => ForgetInboundLoop(session), CancellationToken.None,
                                                 TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        if (replaced is not null)
        {
            _logger.LogInformation("Peer {Peer} connected again, replacing the previous connection", peerId);
            replaced.PeerService.Disconnect();
        }

        // The connection may have dropped before it was installed (its disconnect handler found nothing to remove)
        if (session.IsDisconnected && TryRemoveSession(session))
            ReconnectIfNeeded(session);

        return true;
    }

    /// <summary>
    /// Whether a new connection replaces the current one. A reconnect (the peer restarted, or its old connection is
    /// half dead) replaces it, as LND and CLN do. For a simultaneous connect (one connection each way, close
    /// together) both ends must keep the same one: the one initiated by the node with the lower pubkey (LND's
    /// <c>shouldDropLocalConnection</c>).
    /// </summary>
    private bool ShouldReplace(PeerSession existing, PeerSession incoming)
    {
        if (existing.IsDisconnected)
            return true;

        // Two inbound: the peer reconnected. Two outbound: two of our own attempts raced, keep the first
        if (existing.IsInbound == incoming.IsInbound)
            return incoming.IsInbound;

        if (DateTime.UtcNow - existing.ConnectedAt > SimultaneousConnectWindow)
            return true;

        _localNodeId ??= _secureKeyManager.GetNodePubKey();
        byte[] localNodeId = _localNodeId.Value;
        byte[] remoteNodeId = incoming.Peer.NodeId;
        var ourKeyIsLower = localNodeId.AsSpan().SequenceCompareTo(remoteNodeId) < 0;
        var weInitiatedIncoming = !incoming.IsInbound;
        return weInitiatedIncoming == ourKeyIsLower;
    }

    private bool TryRemoveSession(PeerSession session)
    {
        lock (_sessionLock)
            return _peers.TryRemove(new KeyValuePair<CompactPubKey, PeerSession>(session.Peer.NodeId, session));
    }

    private void ForgetInboundLoop(PeerSession session)
    {
        lock (_sessionLock)
            if (_inboundLoops.TryGetValue(session.Peer.NodeId, out var loop) && loop == session.InboundLoop)
                _inboundLoops.Remove(session.Peer.NodeId);
    }

    private void HandleSessionDisconnected(PeerSession session, PeerDisconnectedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!session.MarkDisconnected())
            return;

        // Only the session of the connection that went down (a newer one may already have replaced it)
        var removed = TryRemoveSession(session);

        // Stop its inbound loop: messages still queued are dropped, the one being handled finishes
        session.Close();

        _logger.LogInformation("Peer {Peer} disconnected", args.PeerPubKey);

        var peerService = session.PeerService;
        if (session.ChannelMessageHandler is not null)
            peerService.OnChannelMessageReceived -= session.ChannelMessageHandler;
        if (session.DisconnectHandler is not null)
            peerService.OnDisconnect -= session.DisconnectHandler;
        peerService.Dispose();

        if (removed)
            ReconnectIfNeeded(session);
    }

    /// <summary>
    /// Queues a channel message for the session's inbound loop. Runs on the transport read loop, so it never
    /// processes the message itself; it only waits when the queue is full.
    /// </summary>
    private void QueueInboundMessage(PeerSession session, ChannelMessageEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!session.TryQueueInbound(args.Message))
            _logger.LogDebug("Dropping channel message ({messageType}) from peer {Peer}: the connection is closing",
                             Enum.GetName(args.Message.Type), args.PeerPubKey);
    }

    /// <summary>
    /// The peer's inbound loop: one message at a time, in arrival order. <see cref="IChannelManager"/> enqueues the
    /// replies into the outbox before the call returns, so they are always ahead of the next message's replies.
    /// Stops after a failure that disconnects the peer, or when the connection closes (dropping what is still queued).
    /// </summary>
    private async Task ProcessInboundMessagesAsync(PeerSession session, ChannelReader<IChannelMessage> inbound,
                                                   CancellationToken cancellationToken)
    {
        _currentSession.Value = session;
        try
        {
            await foreach (var message in inbound.ReadAllAsync(cancellationToken))
            {
                // ReadAllAsync keeps returning queued items after cancellation; the connection is gone, drop them
                if (cancellationToken.IsCancellationRequested)
                    return;

                if (await ProcessChannelMessageAsync(session, message))
                    continue;

                session.Close();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The connection closed
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

        // A reply raised while an inbound loop handles a message goes to that loop's connection (dropped if it is
        // closed); anything else goes to the peer's current connection
        var session = _currentSession.Value;
        if (session is null || session.Peer.NodeId != args.PeerPubKey)
            _peers.TryGetValue(args.PeerPubKey, out session);

        if (session is null)
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
    /// One connection to a peer: its model, its service, its ordered inbound queue and loop, and its outbox.
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

        private readonly CancellationTokenSource _closeCts = new();
        private int _disconnected;
        private volatile bool _reconnectSuppressed;

        public PeerModel Peer { get; }
        public IPeerService PeerService { get; }
        public PeerOutbox Outbox { get; }

        /// <summary>Whether the peer opened this connection.</summary>
        public bool IsInbound { get; }

        public DateTime ConnectedAt { get; } = DateTime.UtcNow;
        public Task InboundLoop { get; private set; } = Task.CompletedTask;
        public EventHandler<ChannelMessageEventArgs>? ChannelMessageHandler { get; set; }
        public EventHandler<PeerDisconnectedEventArgs>? DisconnectHandler { get; set; }
        public bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;
        public bool ReconnectSuppressed => _reconnectSuppressed;

        public PeerSession(PeerModel peer, IPeerService peerService, PeerOutbox outbox, bool isInbound)
        {
            Peer = peer;
            PeerService = peerService;
            Outbox = outbox;
            IsInbound = isInbound;
        }

        /// <summary>
        /// Starts the inbound loop once <paramref name="previousLoop"/> (the loop of the peer's previous connection)
        /// has finished, so no message of the old connection is handled after one of this connection (plan D2).
        /// </summary>
        public void StartInboundLoop(Task previousLoop,
                                     Func<PeerSession, ChannelReader<IChannelMessage>, CancellationToken, Task> loop)
        {
            var token = _closeCts.Token;
            InboundLoop = Task.Run(async () =>
            {
                try
                {
                    await previousLoop.ConfigureAwait(false);
                }
                catch
                {
                    // That loop's failure is not ours
                }

                await loop(this, _inbound.Reader, token).ConfigureAwait(false);
            }, CancellationToken.None);
        }

        public bool TryQueueInbound(IChannelMessage message)
        {
            if (_inbound.Writer.TryWrite(message))
                return true;

            try
            {
                // Full: make the transport read loop wait (backpressure) instead of dropping or reordering
                _inbound.Writer.WriteAsync(message, _closeCts.Token).AsTask().GetAwaiter().GetResult();
                return true;
            }
            catch (ChannelClosedException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>Returns true only for the first call.</summary>
        public bool MarkDisconnected() => Interlocked.Exchange(ref _disconnected, 1) == 0;

        public void SuppressReconnect() => _reconnectSuppressed = true;

        /// <summary>
        /// Stops accepting inbound messages, stops the inbound loop (queued messages are dropped; the one being
        /// handled finishes) and closes the outbox (what is already queued there is still sent).
        /// </summary>
        public void Close()
        {
            _inbound.Writer.TryComplete();
            _closeCts.Cancel();
            Outbox.Complete();
        }
    }
}