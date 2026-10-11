using System.Collections.Concurrent;
using System.Net;
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
using Domain.Enums;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.Gossip.Enums;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Models;
using Domain.Node.Bootstrap;
using Domain.Node.Constants;
using Domain.Node.Events;
using Domain.Node.Fencing;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Payloads;
using Gossip.Events;
using Gossip.Graph.Interfaces;
using Gossip.Interfaces;
using Gossip.Metrics;
using Gossip.Sync;
using Infrastructure.Node.ValueObjects;
using Infrastructure.Protocol.Models;
using Infrastructure.Transport.Events;
using Infrastructure.Transport.Interfaces;
using OnionMessages;
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
/// With an <see cref="IChannelUpdateService"/>, our <c>channel_update</c>s go out through the peer's outbox and the
/// peer's are handed to that service (BOLT 7 direct exchange, W1-E).
/// Onion messages (BOLT 4, wave M6) go out through <see cref="IPeerOnionMessageOutbox"/>: only to a peer that is
/// connected and negotiated <c>option_onion_messages</c>, on its outbox's bounded low-priority class; a peer that
/// drops has its <see cref="IOnionMessageRateLimiter"/> bucket released.
/// </remarks>
/// <seealso cref="IPeerManager" />
public sealed class PeerManager : IPeerManager, IPeerGossipOutbox, IPeerOnionMessageOutbox
{
    /// <inheritdoc />
    public event EventHandler<PeerStateChangedEventArgs>? OnPeerStateChanged;

    /// <summary>
    /// Channel messages waiting for the inbound loop of one peer. When full, the peer's receive path (the message
    /// service's consumer, and behind it the transport read loop) waits, which pushes back on a peer that sends
    /// faster than we process.
    /// </summary>
    private const int InboundQueueCapacity = 1024;

    /// <summary>
    /// BOLT 2 "Batching channel messages": a <c>start_batch</c> with a larger <c>batch_size</c> is a warning and close.
    /// </summary>
    private const int MaxBatchSize = 20;

    /// <summary>
    /// Stored peers dialed at the same time on startup (NL-576).
    /// </summary>
    private const int MaxParallelStartupDials = 16;

    private static readonly TimeSpan s_stopTimeout = TimeSpan.FromSeconds(5);

    private readonly IChannelManager _channelManager;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<PeerManager> _logger;
    private readonly IPeerServiceFactory _peerServiceFactory;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly ITcpService _tcpService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IChannelUpdateService? _channelUpdateService;
    private readonly Lazy<GossipOutboxSettings> _gossipOutboxSettings;
    private readonly Lazy<IOnionMessageRateLimiter?> _onionMessageRateLimiter;
    private readonly Lazy<IGraphStore?> _graphStore;

    // The optional node write fence every outbox asks before a send (NL-1341); none registered = nothing checked
    private readonly Lazy<INodeWriteFence?> _writeFence;
    private readonly ConcurrentDictionary<CompactPubKey, PeerSession> _peers = new();
    private readonly ConcurrentDictionary<CompactPubKey, Task> _reconnectLoops = new();
    private long _droppedOutboxOnionMessages;
    private long _refusedOutboxGossip;

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

    /// <summary>
    /// Inbound connections that are still being set up (init exchange, install, database save); StopAsync waits for
    /// them so none installs a session or writes to the database behind it.
    /// </summary>
    private readonly ConcurrentDictionary<Task, byte> _inboundSetups = new();

    /// <summary>
    /// One gate per peer id for the peer-row saves of the two connect paths (NL-524): both read the row, decide
    /// insert or update, and flush, so without the gate a simultaneous inbound and outbound connection can both see
    /// no row and both insert it (UNIQUE constraint on Peers.NodeId).
    /// </summary>
    private readonly ConcurrentDictionary<CompactPubKey, SemaphoreSlim> _peerRowSaveGates = new();
    private readonly TorOptions _torOptions;

    private CancellationTokenSource _reconnectCts = new();

    /// <summary>
    /// Cancelled as soon as <see cref="StopAsync"/> begins: ends the init waits of connections still being set up.
    /// </summary>
    private CancellationTokenSource _stoppingCts = new();

    private CancellationTokenSource? _cts;
    private Task _startupDials = Task.CompletedTask;
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

    /// <summary>
    /// How long <see cref="StartAsync"/> waits for the startup dials of the stored peers (<c>Node:NetworkTimeout</c>).
    /// A dial still running then goes on in the background (an onion peer may take <c>Node:Tor:ConnectTimeout</c>),
    /// so offline peers never hold back what the host starts after the peer manager (NL-576).
    /// </summary>
    internal TimeSpan StartupDialWait { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a connection waits for the peer's <c>channel_reestablish</c> before it is closed so the reconnect
    /// backoff dials again (<c>Node:ReestablishTimeout</c>, NL-796); <see cref="TimeSpan.Zero"/> = no deadline.
    /// </summary>
    internal TimeSpan ReestablishTimeout { get; set; } = NodeOptions.DefaultReestablishTimeout;

    public PeerManager(IChannelManager channelManager, IChannelMemoryRepository channelMemoryRepository,
                       ILogger<PeerManager> logger, IPeerServiceFactory peerServiceFactory,
                       ISecureKeyManager secureKeyManager, ITcpService tcpService, IServiceProvider serviceProvider,
                       IOptions<NodeOptions>? nodeOptions = null,
                       IChannelUpdateService? channelUpdateService = null)
    {
        if (nodeOptions is not null)
        {
            ReconnectInitialDelay = nodeOptions.Value.ReconnectInitialDelay;
            ReconnectMaxDelay = nodeOptions.Value.ReconnectMaxDelay;
            StartupDialWait = nodeOptions.Value.NetworkTimeout;
            ReestablishTimeout = nodeOptions.Value.ReestablishTimeout;
        }

        _torOptions = nodeOptions?.Value.Tor ?? new TorOptions();

        _channelManager = channelManager;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _onionMessageRateLimiter = new Lazy<IOnionMessageRateLimiter?>(ResolveOnionMessageRateLimiter);
        _peerServiceFactory = peerServiceFactory;
        _secureKeyManager = secureKeyManager;
        _tcpService = tcpService;
        _serviceProvider = serviceProvider;

        // Subscribed here (not in StartAsync) so no message raised by the channel manager can miss the outbox
        _channelManager.OnResponseMessageReady += HandleResponseMessageReady;

        _channelUpdateService = channelUpdateService;
        _channelUpdateService?.OnChannelUpdateReady += HandleChannelUpdateReady;
        _gossipOutboxSettings = new Lazy<GossipOutboxSettings>(ResolveGossipOutboxSettings);
        _graphStore = new Lazy<IGraphStore?>(ResolveGraphStore);
        _writeFence = new Lazy<INodeWriteFence?>(() => _serviceProvider.GetService<INodeWriteFence>());
    }

    /// <summary>The gossip messages waiting in every current connection's outbox (NL-360; the metric's gauge).</summary>
    public long QueuedOutboxGossipCount => _peers.Values.Sum(s => (long)s.Outbox.QueuedGossipCount);

    /// <summary>The bytes of gossip waiting in every current connection's outbox (NL-360).</summary>
    public long QueuedOutboxGossipBytes => _peers.Values.Sum(s => s.Outbox.QueuedGossipBytes);

    /// <summary>The own and relayed gossip messages a full outbox refused since the start (NL-360).</summary>
    public long RefusedOutboxGossipCount => Interlocked.Read(ref _refusedOutboxGossip);

    /// <summary>The onion messages waiting in every current connection's outbox.</summary>
    public long QueuedOutboxOnionMessageCount => _peers.Values.Sum(s => (long)s.Outbox.QueuedOnionMessageCount);

    /// <summary>
    /// The onion messages each connection's outbox holds before it drops more (BOLT12 plan §3.4
    /// <c>OnionMessages:MaxOutboxPerPeer</c>); read when a connection is set up.
    /// </summary>
    public int MaxOutboxOnionMessagesPerPeer { get; set; } = PeerOutbox.DefaultMaxQueuedOnionMessages;

    /// <summary>
    /// The onion messages every connection's outbox refused because it was full, since the node started (the
    /// <c>dropped{reason=outbox_full}</c> count for the onion message metrics; it survives the connections).
    /// </summary>
    public long DroppedOutboxOnionMessageCount => Interlocked.Read(ref _droppedOutboxOnionMessages);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = false;
        _stoppingCts = new CancellationTokenSource();
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

        // Dial the stored peers in parallel and wait for them at most StartupDialWait: the host starts the chain
        // monitor and the HTLC deadline monitor after us, and an offline peer (an onion dial waits up to
        // Tor:ConnectTimeout) must not hold them back. A dial still running goes on in the background and hands a
        // failure to the reconnect loop like any other (NL-576)
        // Not disposed: dials still waiting on it outlive StartAsync (it holds no wait handle)
        var dialGate = new SemaphoreSlim(MaxParallelStartupDials);
        var dials = new List<Task>();
        // The peers seen last first: at most MaxParallelStartupDials dial at once, so a live peer listed behind many
        // that never answer waited for all of them, NetworkTimeout per round of dials (NL-1357)
        foreach (var peer in peers.OrderByDescending(p => p.LastSeenAt))
        {
            // A peer we know no address of (it connected to us from a loopback address, NL-497): its channels are
            // registered above and we wait for it to connect again
            if (peer.IsInboundOnly)
            {
                _logger.LogInformation("Not dialing peer {PeerId}: it only connects to us", peer.NodeId);
                continue;
            }

            dials.Add(DialOnStartupAsync(peer, dialGate));
        }

        _startupDials = Task.WhenAll(dials);
        var waited = await Task.WhenAny(_startupDials, Task.Delay(StartupDialWait, _cts.Token));
        if (waited != _startupDials)
            _logger.LogInformation("Startup dials still running after {Wait}; they continue in the background",
                                   StartupDialWait);

        await uow.SaveChangesAsync();

        await _tcpService.StartListeningAsync(_cts.Token);
    }

    /// <summary>
    /// One startup dial (NL-576): at most <see cref="MaxParallelStartupDials"/> at a time, cancelled by
    /// <see cref="StopAsync"/>; a peer with active channels that could not be reached goes to the reconnect loop.
    /// </summary>
    private async Task DialOnStartupAsync(PeerModel peer, SemaphoreSlim dialGate)
    {
        var token = _reconnectCts.Token;
        try
        {
            await dialGate.WaitAsync(token);
            try
            {
                _ = await DialPeerAsync(peer.PeerAddressInfo, token);
                return;
            }
            finally
            {
                dialGate.Release();
            }
        }
        catch (InvalidOperationException)
        {
            // Already connected (the peer connected to us first)
            return;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopping
            return;
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

    public async Task StopAsync()
    {
        if (_cts is null)
            throw new InvalidOperationException($"{nameof(PeerManager)} is not running");

        // No reconnection from here on (the disconnections below are ours). Set under the session lock: a session is
        // either installed before this (and disconnected below) or refused by TryInstallSession.
        lock (_sessionLock)
            _stopping = true;

        await _stoppingCts.CancelAsync();

        // Stop accepting connections and release the listening sockets, so a restart can bind the same port
        try
        {
            await _tcpService.StopListeningAsync();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Error stopping the TCP listeners");
        }

        // Stop reconnecting first, so no loop connects a peer while we disconnect them; startup dials still running
        // are cancelled too, and start no loop once cancelled (NL-576)
        await _reconnectCts.CancelAsync();
        try
        {
            await _startupDials;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Startup dial ended with an error");
        }

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

        // Inbound connections still being set up: their init wait is cancelled and they can no longer install a
        // session, but one installed just before the stop may still be saving its peer
        try
        {
            await Task.WhenAll(_inboundSetups.Keys).WaitAsync(s_stopTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timeout while waiting for inbound connections being set up");
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Inbound connection setup ended with an error");
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
    public Task<PeerModel> ConnectToPeerAsync(PeerAddressInfo peerAddressInfo) =>
        DialPeerAsync(peerAddressInfo, CancellationToken.None);

    /// <inheritdoc />
    /// <exception cref="ConnectionException">Thrown when the connection to the peer fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the connection to the peer already exists.</exception>
    public async Task<PeerModel> DialPeerAsync(PeerAddressInfo peerAddressInfo, CancellationToken cancellationToken)
    {
        return await ConnectPeerCoreAsync(peerAddressInfo, cancellationToken);
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
        if (token.IsCancellationRequested)
            return;

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

        // A peer we cannot dial reconnects to us (NL-497), unless we saved a dialable address of it before it
        // connected from a loopback address: that one is dialed
        var dialable = session.Peer.IsInboundOnly ? session.DialablePeer : session.Peer;
        if (dialable is null)
            return;

        var peerId = session.Peer.NodeId;
        var channels = _channelMemoryRepository.FindChannels(c => c.RemoteNodeId == peerId && IsActiveChannel(c));
        if (channels is not { Count: > 0 })
            return;

        _logger.LogInformation("Peer {PeerId} has active channels, reconnecting", peerId);
        StartReconnectLoop(dialable);
    }

    private async Task<PeerModel> ConnectPeerCoreAsync(PeerAddressInfo peerAddressInfo,
                                                       CancellationToken cancellationToken)
    {
        // Convert and validate the address
        var peerAddress = new PeerAddress(peerAddressInfo.Address);

        // Check if we're already connected to the peer
        if (_peers.ContainsKey(peerAddress.PubKey))
        {
            throw new InvalidOperationException($"Already connected to peer {peerAddress.PubKey}");
        }

        // Connect to the peer. A cancelled dial closes whatever it opened (the TCP connect itself is bounded by
        // NetworkTimeout), so it never outlives its caller's timeout as a session
        cancellationToken.ThrowIfCancellationRequested();
        var connectTask = _tcpService.ConnectToPeerAsync(peerAddress);
        ConnectedPeer connectedPeer;
        try
        {
            connectedPeer = await connectTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = connectTask.ContinueWith(t => t.Result.TcpClient.Dispose(), CancellationToken.None,
                                         TaskContinuationOptions.OnlyOnRanToCompletion
                                       | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }

        var createTask = _peerServiceFactory.CreateConnectedPeerAsync(connectedPeer.CompactPubKey,
                                                                      connectedPeer.TcpClient);
        IPeerService peerService;
        try
        {
            peerService = await createTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the socket ends the handshake; a service built anyway is released
            connectedPeer.TcpClient.Dispose();
            _ = createTask.ContinueWith(t => t.Result.Dispose(), CancellationToken.None,
                                        TaskContinuationOptions.OnlyOnRanToCompletion
                                      | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }

        // The peer's preferred address and features come with its init
        await WaitForInitAsync(peerService, cancellationToken);

        // BOLT 1 has no preferred address (init remote_addr is our address, NL-344): keep the one we connected to
        var peer = new PeerModel(connectedPeer.CompactPubKey, connectedPeer.Host, connectedPeer.Port,
                                 ToPeerType(peerAddress.Type))
        {
            LastSeenAt = DateTime.UtcNow
        };
        peer.SetPeerService(peerService);

        var session = CreateSession(peer, peerService, isInbound: false);
        switch (TryInstallSession(session))
        {
            case InstallResult.Stopping:
                session.SuppressReconnect();
                peerService.Disconnect(new ConnectionException("Shutting down"));
                throw new ConnectionException($"Not keeping the connection to peer {peer.NodeId}: stopping");
            case InstallResult.KeptExisting:
                // The peer's own connection to us won the tie-break
                session.SuppressReconnect();
                peerService.Disconnect(new ConnectionException($"Already connected to peer {peer.NodeId}"));
                throw new InvalidOperationException($"Already connected to peer {peer.NodeId}");
        }

        // The peer's row is saved under its save gate (NL-524), committed before the gate is released
        await SavePeerRowAsync(peer.NodeId, async uow =>
        {
            await uow.PeerDbRepository.AddOrUpdateAsync(peer);
            return null;
        });

        return peer;
    }

    /// <summary>
    /// Saves a peer row under the peer's save gate (NL-524): the outbound and inbound connect paths both read the
    /// row, decide insert or update, and flush, so the gate makes that whole sequence atomic per peer — the second
    /// path to save a row the first one just committed reads it and updates instead of inserting a duplicate
    /// (UNIQUE constraint on Peers.NodeId). Each save gets its own scope, so the row is committed before the gate
    /// is released and what one path wrote is what the other path reads.
    /// </summary>
    private async Task<PeerModel?> SavePeerRowAsync(CompactPubKey nodeId, Func<IUnitOfWork, Task<PeerModel?>> save)
    {
        var gate = _peerRowSaveGates.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            using var scope = _serviceProvider.CreateScope();
            using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var saved = await save(uow);
            await uow.SaveChangesAsync();
            return saved;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Waits until the peer's init was accepted. On failure the connection is already closed; this releases the
    /// service and throws. A connection whose init arrives after <see cref="StopAsync"/> began is closed too, so no
    /// session is installed behind the stop.
    /// </summary>
    /// <exception cref="ConnectionException">
    /// The connection closed before the init exchange was done, or the manager is stopping.
    /// </exception>
    private async Task WaitForInitAsync(IPeerService peerService, CancellationToken cancellationToken = default)
    {
        var stoppingToken = _stoppingCts.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cancellationToken);
        try
        {
            await peerService.WaitForInitAsync(linked.Token).WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            peerService.Disconnect(new ConnectionException("Shutting down"));
            peerService.Dispose();
            throw new ConnectionException($"Not keeping the connection to peer {peerService.PeerPubKey}: stopping");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up (a dial timeout): the connection is not kept
            peerService.Disconnect(new ConnectionException("Dial cancelled"));
            peerService.Dispose();
            throw;
        }
        catch (Exception e)
        {
            peerService.Dispose();
            if (e is ConnectionException)
                throw;

            throw new ConnectionException($"Init exchange with peer {peerService.PeerPubKey} failed", e);
        }

        if (!_stopping)
            return;

        peerService.Disconnect(new ConnectionException("Shutting down"));
        peerService.Dispose();
        throw new ConnectionException($"Not keeping the connection to peer {peerService.PeerPubKey}: stopping");
    }

    private void HandleNewPeerConnected(object? _, NewPeerConnectedEventArgs args)
    {
        // Not awaited on the TCP service's thread: the init exchange takes a round trip
        _ = TrackInboundSetupAsync(args);
    }

    /// <summary>
    /// Runs the setup of an inbound connection, registered in <see cref="_inboundSetups"/> before any of it runs, so a
    /// setup that can still install a session is always one <see cref="StopAsync"/> waits for.
    /// </summary>
    private async Task TrackInboundSetupAsync(NewPeerConnectedEventArgs args)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inboundSetups.TryAdd(done.Task, 0);
        try
        {
            await HandleNewPeerConnectedAsync(args);
        }
        finally
        {
            _inboundSetups.TryRemove(done.Task, out _);
            done.SetResult();
        }
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

            // An inbound peer's listening address is unknown (init remote_addr is our address, NL-344): the row
            // keeps a saved one (NL-514) or is saved from its node_announcement, else at this host with the default
            // port. From a loopback address (a local tunnel, Tor on the same host) that host is not the peer's: it
            // is saved as inbound-only, never dialed (NL-497)
            var isInboundOnly = IsLoopback(args.Host);
            var peer = new PeerModel(peerService.PeerPubKey, args.Host, NodeConstants.DefaultPort,
                                     args.TcpClient.Client.ProtocolType == ProtocolType.IPv6 ? "IPv6" : "IPv4")
            {
                LastSeenAt = DateTime.UtcNow,
                IsInboundOnly = isInboundOnly
            };
            peer.SetPeerService(peerService);

            // Subscribe and install before anything slow (the database below): the peer may already be sending
            var session = CreateSession(peer, peerService, isInbound: true);
            switch (TryInstallSession(session))
            {
                case InstallResult.Stopping:
                    _logger.LogInformation("Closing the new connection of peer {Peer}: stopping", peer.NodeId);
                    session.SuppressReconnect();
                    peerService.Disconnect(new ConnectionException("Shutting down"));
                    return;
                case InstallResult.KeptExisting:
                    _logger.LogWarning("Keeping our own connection to peer {Peer}, closing the new one", peer.NodeId);
                    session.SuppressReconnect();
                    peerService.Disconnect(new ConnectionException($"Already connected to peer {peer.NodeId}"));
                    return;
            }

            // Every inbound peer is saved, so its channels are registered at our next start (NL-497)
            session.DialablePeer = await SaveInboundPeerAsync(peer);
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
    /// Saves a peer that connected to us, under its save gate (NL-524). A saved dialable row survives every inbound
    /// connection (NL-514): where the peer connected from is not where it listens, so only the row's last-seen time
    /// moves. Without a dialable row, a loopback peer is saved inbound-only, without an address (empty host, port 0),
    /// so nothing that reads the peer rows (the static channel backup, a restore) takes the loopback host for the
    /// peer's (NL-497), unless Tor is on and its <c>node_announcement</c> names an address we can dial (an onion
    /// service, or an IP): a peer that reached our onion service is then saved there and dialed back (NL-579); any
    /// other peer is saved at the address of its <c>node_announcement</c> when the graph has one (NL-514), else at the
    /// host it connected from with the default port (its listening port is unknown until it announces itself).
    /// </summary>
    /// <returns>The saved dialable row of an inbound-only peer (kept or from its announcement), the one the reconnect
    /// loop dials; else null.</returns>
    private Task<PeerModel?> SaveInboundPeerAsync(PeerModel peer)
    {
        return SavePeerRowAsync(peer.NodeId, async uow =>
        {
            var saved = await uow.PeerDbRepository.GetByNodeIdAsync(peer.NodeId);
            if (saved is { IsInboundOnly: false })
            {
                saved.LastSeenAt = peer.LastSeenAt;
                uow.PeerDbRepository.Update(saved);
                return saved;
            }

            if (peer.IsInboundOnly)
            {
                // Tor delivers our onion service's connections from loopback: the peer's announcement says where to
                // dial it back (NL-579)
                if (_torOptions.IsEnabled && TryGetAnnouncedPeer(peer.NodeId) is { } announced)
                {
                    announced.LastSeenAt = peer.LastSeenAt;
                    await uow.PeerDbRepository.AddOrUpdateAsync(announced);
                    return announced;
                }

                await uow.PeerDbRepository.AddOrUpdateAsync(new PeerModel(peer.NodeId, string.Empty, 0, peer.Type)
                {
                    LastSeenAt = peer.LastSeenAt,
                    IsInboundOnly = true
                });
                return null;
            }

            await uow.PeerDbRepository.AddOrUpdateAsync(TryGetAnnouncedPeer(peer.NodeId) ?? peer);
            return null;
        });
    }

    /// <summary>
    /// The peer's listening address from its <c>node_announcement</c> in the graph (NL-514): its first announced IPv4
    /// or IPv6 address that we could dial (port 0, unspecified and multicast ones refused; a loopback one never, it is
    /// not the peer's listening address any more than a loopback connection is), or its Tor v3 onion service when Tor
    /// is on (first in Tor-only mode, after the IP addresses otherwise). Null without a graph, for an unannounced node,
    /// or when it announced nothing we can dial.
    /// </summary>
    private PeerModel? TryGetAnnouncedPeer(CompactPubKey nodeId)
    {
        var graphStore = _graphStore.Value;
        if (graphStore is null || !graphStore.TryGetNode(nodeId, out var node))
            return null;

        PeerModel? ip = null;
        PeerModel? onion = null;
        foreach (var descriptor in node.Addresses)
        {
            if (descriptor.Type == AddressDescriptorType.TorV3)
            {
                if (onion is null && _torOptions.CanDial(descriptor.Type) && descriptor.Port > 0
                                  && OnionV3Address.IsValid(descriptor.Address))
                    onion = new PeerModel(nodeId, descriptor.Host, descriptor.Port, ToPeerType(descriptor.Type));

                continue;
            }

            if (ip is not null || descriptor.Type is not (AddressDescriptorType.IPv4 or AddressDescriptorType.IPv6))
                continue;

            var address = new IPAddress(descriptor.Address);
            if (!SeedAddressFilter.IsUsable(address, descriptor.Port, allowNonRoutable: true, out _)
             || IsLoopback(address.ToString()))
                continue;

            ip = new PeerModel(nodeId, address.ToString(), descriptor.Port, ToPeerType(descriptor.Type));
        }

        return _torOptions.IsTorOnly ? onion ?? ip : ip ?? onion;
    }

    /// <summary>
    /// The <see cref="PeerModel.Type"/> of an address type: <c>IPv4</c>, <c>IPv6</c>, <c>TorV3</c> or <c>DNS</c>.
    /// </summary>
    internal static string ToPeerType(AddressDescriptorType type) => type switch
    {
        AddressDescriptorType.IPv6 => "IPv6",
        AddressDescriptorType.TorV3 => "TorV3",
        AddressDescriptorType.Dns => "DNS",
        _ => "IPv4"
    };

    /// <summary>
    /// Whether a connection came from this host: a loopback IP address (IPv4-mapped included) or <c>localhost</c>.
    /// </summary>
    internal static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
     || (IPAddress.TryParse(host, out var address)
      && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));

    /// <summary>
    /// Creates the session of a new connection and subscribes to its peer service right away. A peer service keeps
    /// the channel messages that arrived before this and replays a disconnection that already happened, so nothing
    /// the peer sent right after init is lost and a peer that already dropped is not kept.
    /// </summary>
    private PeerSession CreateSession(PeerModel peer, IPeerService peerService, bool isInbound)
    {
        var (maxQueuedGossip, maxQueuedGossipBytes, metrics) = _gossipOutboxSettings.Value;
        var outbox = new PeerOutbox(peerService, _logger, maxQueuedGossip,
                                    () =>
                                    {
                                        Interlocked.Increment(ref _refusedOutboxGossip);
                                        metrics?.RecordOutboxRefused();
                                    },
                                    MaxOutboxOnionMessagesPerPeer,
                                    () => Interlocked.Increment(ref _droppedOutboxOnionMessages),
                                    maxQueuedGossipBytes, _writeFence.Value);
        var session = new PeerSession(peer, peerService, outbox, isInbound);
        session.ChannelMessageHandler = (_, args) => QueueInboundMessage(session, args);
        session.DisconnectHandler = (_, args) => HandleSessionDisconnected(session, args);

        peerService.OnChannelMessageReceived += session.ChannelMessageHandler;
        peerService.OnDisconnect += session.DisconnectHandler;

        if (_channelUpdateService is not null)
        {
            session.ChannelUpdateHandler = (_, message) => HandleRemoteChannelUpdate(session, message);
            peerService.OnChannelUpdateReceived += session.ChannelUpdateHandler;
        }

        return session;
    }

    /// <summary>
    /// Makes <paramref name="session"/> the peer's session and starts its inbound loop (after the previous
    /// connection's loop has finished). An existing session is replaced and disconnected, unless the tie-break keeps
    /// it (<see cref="InstallResult.KeptExisting"/>). Once <see cref="StopAsync"/> began nothing is installed
    /// (<see cref="InstallResult.Stopping"/>). In both cases the caller closes the new connection.
    /// </summary>
    private InstallResult TryInstallSession(PeerSession session)
    {
        var peerId = session.Peer.NodeId;
        PeerSession? replaced = null;

        lock (_sessionLock)
        {
            // Checked under the lock StopAsync sets it under: its disconnect pass then sees every installed session
            if (_stopping)
                return InstallResult.Stopping;

            if (_peers.TryGetValue(peerId, out var existing))
            {
                if (!ShouldReplace(existing, session))
                    return InstallResult.KeptExisting;

                replaced = existing;
                NotifyPeerState(peerId, false);
                replaced.SuppressReconnect();
                replaced.Close();
            }

            // None of the peer's channels is reestablished on the new connection (BOLT 2), before it is visible
            _channelManager.OnPeerConnectionChanged(peerId);
            _peers[peerId] = session;
            if (!session.IsDisconnected)
                NotifyPeerState(peerId, true);

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
        {
            ReconnectIfNeeded(session);
            return InstallResult.Installed;
        }

        SendChannelUpdates(session);
        return InstallResult.Installed;
    }

    /// <summary>
    /// Gives a new connection our <c>channel_update</c> for every open channel with the peer (after a reconnect or a
    /// restart the peer may not have our current policy). Runs on another task: each update waits for its channel's
    /// lock.
    /// </summary>
    private void SendChannelUpdates(PeerSession session)
    {
        if (_channelUpdateService is null)
            return;

        var peerId = session.Peer.NodeId;
        _ = Task.Run(async () =>
        {
            try
            {
                await _channelUpdateService.SendChannelUpdatesToPeerAsync(peerId);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to send our channel_updates to peer {Peer}", peerId);
            }
        });
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
        {
            var removed = _peers.TryRemove(new KeyValuePair<CompactPubKey, PeerSession>(session.Peer.NodeId, session));
            if (removed)
                NotifyPeerState(session.Peer.NodeId, false);
            return removed;
        }
    }

    private void NotifyPeerState(CompactPubKey peerId, bool online)
    {
        if (OnPeerStateChanged is not { } handlers)
            return;
        foreach (var callback in handlers.GetInvocationList().Cast<EventHandler<PeerStateChangedEventArgs>>())
            try
            {
                callback(this, new PeerStateChangedEventArgs(peerId, online));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Peer lifecycle observer failed for {PeerId}", peerId);
            }
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
        if (session.ChannelUpdateHandler is not null)
            peerService.OnChannelUpdateReceived -= session.ChannelUpdateHandler;
        peerService.Dispose();

        if (!removed)
            return;

        ReleaseOnionMessageRateLimit(session.Peer.NodeId);

        // NL-392: an open that has not reached funding_created (or, as opener, accept_channel) does not survive the
        // connection (BOLT 2), so its temporary channel goes with it
        var forgotten = _channelMemoryRepository.RemoveTemporaryChannels(session.Peer.NodeId);
        if (forgotten is { Count: > 0 })
            _logger.LogInformation("Forgot {Count} temporary channel(s) of peer {Peer} on disconnection",
                                   forgotten.Count, session.Peer.NodeId);

        _channelManager.OnPeerConnectionChanged(session.Peer.NodeId);
        ReconnectIfNeeded(session);
    }

    /// <summary>
    /// Queues a channel message for the session's inbound loop. Runs on the peer's receive path (the message
    /// service's per-peer consumer, NL-108), so it never processes the message itself; it only waits when the queue
    /// is full.
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
            // Before any other channel message on this connection: channel_reestablish (or the stored error) for
            // every channel with the peer (BOLT 2 Message Retransmission)
            if (!cancellationToken.IsCancellationRequested)
                await StartReestablishAsync(session);

            // The start_batch group being collected on this connection (a batch never spans two connections)
            var batching = new InboundBatchState();

            await foreach (var message in inbound.ReadAllAsync(cancellationToken))
            {
                // ReadAllAsync keeps returning queued items after cancellation; the connection is gone, drop them
                if (cancellationToken.IsCancellationRequested)
                    return;

                if (await ProcessInboundMessageAsync(session, message, batching))
                    continue;

                session.Close();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The connection closed
        }
        finally
        {
            // The last message of this connection was handled: undo the peer's uncommitted updates before the next
            // connection's loop (which waits for this one) sends channel_reestablish
            await RevertPeerUpdatesAsync(session);
        }
    }

    /// <summary>
    /// Raises our <c>channel_reestablish</c> for the peer's channels (through the outbox, via the channel manager) and
    /// queues the stored error of its failed channels. A failure is logged: the channels stay unusable on this
    /// connection, and the peer is not disconnected for our own error.
    /// </summary>
    private async Task StartReestablishAsync(PeerSession session)
    {
        try
        {
            var errors = await _channelManager.OnPeerConnectedAsync(session.Peer.NodeId);
            if (errors is null)
                return;

            foreach (var error in errors)
                if (!session.Outbox.TryEnqueueError(error))
                    _logger.LogWarning("Peer {Peer} is disconnecting, dropping the error of channel {ChannelId}",
                                       session.Peer.NodeId, error.Payload.ChannelId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to start channel_reestablish with peer {Peer}", session.Peer.NodeId);
        }
        finally
        {
            ArmReestablishDeadline(session);
        }
    }

    /// <summary>
    /// Starts the connection's <c>channel_reestablish</c> deadline (NL-796): the timer belongs to the session and is
    /// disposed when the session closes, so it never acts on a newer connection.
    /// </summary>
    private void ArmReestablishDeadline(PeerSession session)
    {
        var timeout = ReestablishTimeout;
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan || session.IsDisconnected)
            return;

        // A failure to arm the deadline must never fault the connection's inbound loop (NL-891): log it and go on
        // without one
        try
        {
            var timeProvider = _serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
            session.SetReestablishDeadline(timeProvider.CreateTimer(_ => OnReestablishDeadline(session, timeout), null,
                                                                    timeout, Timeout.InfiniteTimeSpan));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not arm the channel_reestablish deadline ({Timeout}) of peer {Peer}", timeout,
                             session.Peer.NodeId);
        }
    }

    /// <summary>
    /// The connection's deadline passed (NL-796): when a channel of the peer that should carry updates still waits for
    /// the peer's <c>channel_reestablish</c>, one <c>warning</c> goes out after what is queued and the connection
    /// closes. The session is not marked as closed on purpose, so the reconnect backoff dials the peer again. The
    /// channel stays gated until a connection's reestablish completes; nothing else about it changes.
    /// </summary>
    private void OnReestablishDeadline(PeerSession session, TimeSpan timeout)
    {
        try
        {
            var peerId = session.Peer.NodeId;
            if (session.IsDisconnected || _stopping || !_peers.TryGetValue(peerId, out var current)
             || !ReferenceEquals(current, session))
                return;

            var waiting = _channelManager.GetChannelsAwaitingPeerReestablish(peerId);
            if (waiting is not { Count: > 0 })
                return;

            _logger.LogWarning(
                "Peer {Peer} sent no channel_reestablish within {Timeout} for {Count} channel(s) ({Channels}); closing the connection to dial again",
                peerId, timeout, waiting.Count, string.Join(", ", waiting));
            session.Outbox.TryEnqueueDisconnect(new WarningException(
                                                    $"No channel_reestablish within {timeout.TotalSeconds:0} s; reconnecting"));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "The channel_reestablish deadline of peer {Peer} failed", session.Peer.NodeId);
        }
    }

    private async Task RevertPeerUpdatesAsync(PeerSession session)
    {
        try
        {
            await _channelManager.OnPeerDisconnectedAsync(session.Peer.NodeId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to revert the uncommitted updates of peer {Peer}", session.Peer.NodeId);
        }
    }

    /// <summary>
    /// Routes one inbound message: into the <c>start_batch</c> group being collected, as the start of a new group, or
    /// to the channel manager. Returns false when the peer is being disconnected.
    /// </summary>
    /// <remarks>
    /// BOLT 2 "Batching channel messages" (splicing plan SP-OP-04, D15), receiver side:
    /// "If `batch_size` is not strictly greater than 1: MUST ignore the `start_batch` message. SHOULD send a
    /// `warning`." "If `batch_size` is strictly greater than 20: MUST send a `warning` and close the connection, or
    /// send an `error` and fail the channel." "MUST group the next `batch_size` messages and process them together."
    /// "If one of those messages is not for the specified `channel_id`: MUST send a `warning` and close the
    /// connection, or send an `error` and fail the channel." "If `message_type` is missing or not set to the type for
    /// `commitment_signed`: MUST ignore the `start_batch` message and process the following messages sequentially."
    /// We always take the warning-and-close option, which leaves the channel usable after a reconnection. A grouped
    /// message that is not a <c>commitment_signed</c> breaks the sender's "MUST send `batch_size`
    /// `commitment_signed` messages ... without any other unrelated messages in-between" and is treated the same way.
    /// A complete group is handed to <see cref="IChannelManager.HandleCommitmentSignedBatchAsync"/> in one call.
    /// A <c>start_batch</c> from a peer without negotiated <c>option_splice</c> is dropped like any unknown odd message.
    /// </remarks>
    private async Task<bool> ProcessInboundMessageAsync(PeerSession session, IChannelMessage message,
                                                        InboundBatchState batching)
    {
        if (batching.Pending is { } pending)
            return await AddToBatchAsync(session, message, batching, pending);

        if (message is StartBatchMessage startBatch)
        {
            // Only a spliced channel batches commitment_signed. Without negotiated option_splice, start_batch (127,
            // odd) stays an unknown message: dropped, and what follows is processed one by one as before the
            // splicing wire existed, so no batch reaches a channel manager that cannot handle one
            if (session.PeerService.Features.OptionSplice == FeatureSupport.No)
            {
                _logger.LogDebug("Ignoring start_batch of channel {ChannelId} from peer {Peer}: option_splice not "
                               + "negotiated", startBatch.Payload.ChannelId, session.Peer.NodeId);
                return true;
            }

            return StartBatch(session, startBatch, batching);
        }

        return await ProcessChannelMessageAsync(session, message);
    }

    /// <summary>
    /// Applies the receiver rules of a <c>start_batch</c> (SP-OP-04) and starts collecting its group when it announces
    /// a batch of <c>commitment_signed</c>. Returns false when the peer is being disconnected.
    /// </summary>
    private bool StartBatch(PeerSession session, StartBatchMessage startBatch, InboundBatchState batching)
    {
        var channelId = startBatch.Payload.ChannelId;
        var batchSize = startBatch.Payload.BatchSize;

        if (batchSize <= 1)
        {
            var warning = new ChannelWarningException($"Ignoring start_batch with batch_size {batchSize}", channelId,
                                                      "start_batch batch_size must be greater than 1");
            return HandleChannelMessageFailure(session, warning, MessageTypes.StartBatch, channelId);
        }

        if (batchSize > MaxBatchSize)
        {
            var warning = new ChannelWarningException($"start_batch with batch_size {batchSize}", channelId,
                                                      $"start_batch batch_size must be at most {MaxBatchSize}")
            {
                CloseConnection = true
            };
            return HandleChannelMessageFailure(session, warning, MessageTypes.StartBatch, channelId);
        }

        if (startBatch.MessageTypeTlv?.MessageType != (ushort)MessageTypes.CommitmentSigned)
        {
            _logger.LogDebug(
                "Ignoring start_batch of channel {ChannelId} from peer {Peer} with message_type {MessageType}: "
              + "processing the following messages one by one", channelId, session.Peer.NodeId,
                startBatch.MessageTypeTlv?.MessageType.ToString() ?? "missing");
            return true;
        }

        batching.Pending = new PendingBatch(channelId, batchSize);
        return true;
    }

    /// <summary>
    /// Adds a message to the group being collected and hands the group to the channel manager once it holds
    /// <c>batch_size</c> messages. Returns false when the peer is being disconnected.
    /// </summary>
    private async Task<bool> AddToBatchAsync(PeerSession session, IChannelMessage message,
                                             InboundBatchState batching, PendingBatch pending)
    {
        var messageChannelId = message.Payload?.ChannelId;
        if (messageChannelId is null || messageChannelId.Value != pending.ChannelId)
        {
            batching.Pending = null;
            var warning = new ChannelWarningException(
                $"{Enum.GetName(message.Type)} for channel {messageChannelId} inside a start_batch of channel "
              + $"{pending.ChannelId}", pending.ChannelId, "start_batch message for another channel")
            {
                CloseConnection = true
            };
            return HandleChannelMessageFailure(session, warning, message.Type, pending.ChannelId);
        }

        if (message is not CommitmentSignedMessage commitmentSigned)
        {
            batching.Pending = null;
            var warning = new ChannelWarningException(
                $"{Enum.GetName(message.Type)} inside a start_batch of commitment_signed", pending.ChannelId,
                "start_batch must be followed by commitment_signed only")
            {
                CloseConnection = true
            };
            return HandleChannelMessageFailure(session, warning, message.Type, pending.ChannelId);
        }

        pending.Messages.Add(commitmentSigned);
        if (pending.Messages.Count < pending.BatchSize)
            return true;

        batching.Pending = null;
        try
        {
            await _channelManager.HandleCommitmentSignedBatchAsync(
                new CommitmentSignedBatch(pending.ChannelId, pending.Messages), session.PeerService.Features,
                session.PeerService.PeerPubKey);
            return true;
        }
        catch (Exception e)
        {
            return HandleChannelMessageFailure(session, e, MessageTypes.CommitmentSigned, pending.ChannelId);
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

        if (exception is ChannelFailedException cfe)
        {
            // The channel is failed (and persisted as such): send its error and keep the connection, so the peer's
            // other channels go on (BOLT 1: the sender MAY close the connection; BOLT 2 re-sends it on reconnection)
            _logger.LogError("Channel {ChannelId} of peer {Peer} is failed ({messageType}): {message}",
                             cfe.FailedChannelId, peerPubKey, Enum.GetName(messageType), cfe.Message);

            var errorMessage = new ErrorMessage(new ErrorPayload(cfe.FailedChannelId,
                                                                 cfe.PeerMessage
                                                              ?? ChannelFailedException.DefaultPeerMessage));
            if (!session.Outbox.TryEnqueueError(errorMessage))
                _logger.LogWarning("Peer {Peer} is disconnecting, dropping the error of channel {ChannelId}", peerPubKey,
                                   cfe.FailedChannelId);
            return true;
        }

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
            // A funding-lock message raised while the peer is away (e.g. a confirmation processed at startup, before
            // the connection) is routine: channel_reestablish carries it on the next connection
            var level = args.ResponseMessage.Type is MessageTypes.SpliceLocked or MessageTypes.ChannelReady
                                                  or MessageTypes.AnnouncementSignatures
                            ? LogLevel.Information
                            : LogLevel.Warning;
            if (_logger.IsEnabled(level))
                _logger.Log(level, "Peer {Peer} not connected, dropping {messageType}", args.PeerPubKey,
                            Enum.GetName(args.ResponseMessage.Type));
            return;
        }

        if (!session.Outbox.TryEnqueue(args.ResponseMessage))
            _logger.LogWarning("Peer {Peer} is disconnecting, dropping {messageType}", args.PeerPubKey,
                               Enum.GetName(args.ResponseMessage.Type));
    }

    /// <summary>
    /// Enqueues our <c>channel_update</c> for the peer's current connection. Runs while the channel's lock is held, so
    /// it must never block or throw. A peer that is not connected does not get it now; its next connection does
    /// (<see cref="SendChannelUpdates"/>).
    /// </summary>
    private void HandleChannelUpdateReady(object? sender, ChannelUpdateReadyEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!_peers.TryGetValue(args.PeerPubKey, out var session))
        {
            _logger.LogInformation("Peer {Peer} not connected, not sending channel_update for {ShortChannelId}",
                                   args.PeerPubKey, args.Message.Payload.ShortChannelId);
            return;
        }

        if (!session.Outbox.TryEnqueueGossip(args.Message))
            _logger.LogWarning("Peer {Peer} is disconnecting, dropping channel_update for {ShortChannelId}",
                               args.PeerPubKey, args.Message.Payload.ShortChannelId);
    }

    /// <summary>
    /// Hands a <c>channel_update</c> from the peer to the channel update service. Runs on the peer's receive path
    /// (the message service's per-peer consumer).
    /// </summary>
    private void HandleRemoteChannelUpdate(PeerSession session, ChannelUpdateMessage message)
    {
        try
        {
            _channelUpdateService?.HandleRemoteChannelUpdate(session.Peer.NodeId, message);
        }
        catch (Exception e)
        {
            // Gossip is never worth a connection
            _logger.LogWarning(e, "Error handling channel_update from peer {Peer}", session.Peer.NodeId);
        }
    }

    /// <summary>
    /// Outcome of <see cref="TryInstallSession"/>.
    /// </summary>
    private enum InstallResult
    {
        Installed,
        KeptExisting,
        Stopping
    }

    /// <summary>
    /// Queues gossip on the outbox of <paramref name="connection"/> (NL-351): only while it is still the peer's current
    /// connection, so our own and relayed gossip keeps FIFO order with that connection's channel messages. Capped
    /// (NL-360): <see cref="GossipEnqueueResult.Full"/> when it would take the outbox's gossip over
    /// <see cref="GossipSyncOptions.MaxOutboxGossipPerPeer"/> messages or
    /// <see cref="GossipSyncOptions.MaxOutboxGossipBytesPerPeer"/> bytes (a peer that reads slowly); channel messages are
    /// never refused for it.
    /// </summary>
    public GossipEnqueueResult EnqueueGossip(IPeerService connection, IMessage message, int size) =>
        TryGetCurrentSession(connection) is { } session
            ? session.Outbox.EnqueueGossip(message, size)
            : GossipEnqueueResult.Gone;

    /// <inheritdoc />
    public GossipOutboxDepth? GetGossipDepth(IPeerService connection) =>
        TryGetCurrentSession(connection)?.Outbox.GossipDepth;

    private PeerSession? TryGetCurrentSession(IPeerService connection) =>
        _peers.TryGetValue(connection.PeerPubKey, out var session) && ReferenceEquals(session.PeerService, connection)
            ? session
            : null;

    /// <inheritdoc />
    public bool CanSendOnionMessage(CompactPubKey peerNodeId) =>
        _peers.TryGetValue(peerNodeId, out var session) && CanSendOnionMessage(session);

    /// <inheritdoc />
    /// <remarks>
    /// Only the peer's current connection; never connects (BOLT12 plan D6). Bounded by
    /// <see cref="MaxOutboxOnionMessagesPerPeer"/> per connection, behind every other queued message.
    /// </remarks>
    public bool TryEnqueueOnionMessage(CompactPubKey peerNodeId, OnionMessageMessage message) =>
        message is not null
        && _peers.TryGetValue(peerNodeId, out var session)
        && CanSendOnionMessage(session)
        && session.Outbox.TryEnqueueOnionMessage(message);

    /// <inheritdoc />
    /// <remarks>
    /// The peer's current connection only: a channel <c>error</c> keeps the connection up (BOLT 1), so it is queued on
    /// the outbox like any channel message and follows everything queued for the peer before it (NL-273).
    /// </remarks>
    public bool TryEnqueueChannelError(CompactPubKey peerNodeId, ErrorMessage error) =>
        error is not null
        && _peers.TryGetValue(peerNodeId, out var session)
        && !session.IsDisconnected
        && session.Outbox.TryEnqueueError(error);

    private static bool CanSendOnionMessage(PeerSession session) =>
        !session.IsDisconnected && session.PeerService.Features.OptionOnionMessages != FeatureSupport.No;

    /// <summary>
    /// Releases a disconnected peer's onion message rate-limit bucket (the limiter keeps one that is still in debt, so
    /// reconnecting does not refill it).
    /// </summary>
    private void ReleaseOnionMessageRateLimit(CompactPubKey peerNodeId)
    {
        try
        {
            _onionMessageRateLimiter.Value?.RemovePeer(peerNodeId);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not release the onion message rate limit of peer {Peer}", peerNodeId);
        }
    }

    /// <summary>
    /// The registered onion message rate limiter, or null; resolved on first use, as the provider may still be
    /// building singletons while this one is constructed.
    /// </summary>
    private IOnionMessageRateLimiter? ResolveOnionMessageRateLimiter()
    {
        try
        {
            // The outbox's queue depth joins the onion message meter (NL-446), like the gossip one (NL-360)
            _serviceProvider.GetService<OnionMessageMetrics>()?
                .RegisterQueue("outbox", () => QueuedOutboxOnionMessageCount);
            return _serviceProvider.GetService<IOnionMessageRateLimiter>();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not resolve the onion message rate limiter");
            return null;
        }
    }

    /// <summary>
    /// The graph (which holds the peers' <c>node_announcement</c>s), or null when it is off: an optional singleton
    /// (<c>Gossip:Enabled</c>), read for the listening address of an inbound peer without a dialable row (NL-514).
    /// </summary>
    private IGraphStore? ResolveGraphStore()
    {
        try
        {
            return _serviceProvider.GetService<IGraphStore>();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not resolve the graph store");
            return null;
        }
    }

    /// <summary>
    /// The outbox gossip caps from <see cref="GossipSyncOptions"/> (the <c>Gossip</c> section; its defaults without a
    /// registration) and the gossip meter, which also gets the <c>outbox_gossip</c> queue gauge and the
    /// <c>nlightning.gossip.outbox.bytes</c> gauge. Resolved once, at the first session: the provider may still be
    /// building singletons while this one is constructed.
    /// </summary>
    private GossipOutboxSettings ResolveGossipOutboxSettings()
    {
        var defaults = new GossipSyncOptions();
        var maxQueuedGossip = defaults.MaxOutboxGossipPerPeer;
        var maxQueuedGossipBytes = defaults.MaxOutboxGossipBytesPerPeer;
        GossipMetrics? metrics = null;
        try
        {
            if (_serviceProvider.GetService<IOptions<GossipSyncOptions>>() is { } options)
            {
                maxQueuedGossip = options.Value.MaxOutboxGossipPerPeer;
                maxQueuedGossipBytes = options.Value.MaxOutboxGossipBytesPerPeer;
            }

            metrics = _serviceProvider.GetService<GossipMetrics>();
            metrics?.RegisterQueue("outbox_gossip", () => QueuedOutboxGossipCount);
            metrics?.RegisterOutboxBytes(() => QueuedOutboxGossipBytes);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not read the outbox gossip settings; using the default caps");
        }

        return new GossipOutboxSettings(maxQueuedGossip, maxQueuedGossipBytes, metrics);
    }

    /// <summary>The gossip caps of every outbox and the meter their refusals go to (NL-360).</summary>
    private sealed record GossipOutboxSettings(int MaxQueuedGossip, long MaxQueuedGossipBytes, GossipMetrics? Metrics);

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
        private ITimer? _reestablishDeadline;
        private int _disconnected;
        private volatile bool _reconnectSuppressed;
        private PeerModel? _dialablePeer;

        public PeerModel Peer { get; }
        public IPeerService PeerService { get; }
        public PeerOutbox Outbox { get; }

        /// <summary>Whether the peer opened this connection.</summary>
        public bool IsInbound { get; }

        public DateTime ConnectedAt { get; } = DateTime.UtcNow;
        public Task InboundLoop { get; private set; } = Task.CompletedTask;
        public EventHandler<ChannelMessageEventArgs>? ChannelMessageHandler { get; set; }
        public EventHandler<PeerDisconnectedEventArgs>? DisconnectHandler { get; set; }
        public EventHandler<ChannelUpdateMessage>? ChannelUpdateHandler { get; set; }
        public bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;
        public bool ReconnectSuppressed => _reconnectSuppressed;

        /// <summary>
        /// The saved dialable row of an inbound-only peer (NL-497): the reconnect loop dials it when this connection
        /// drops. Null for a peer we know no address of.
        /// </summary>
        public PeerModel? DialablePeer
        {
            get => Volatile.Read(ref _dialablePeer);
            set => Volatile.Write(ref _dialablePeer, value);
        }

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
                // Full: make the receive path wait (backpressure) instead of dropping or reordering
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
        /// Keeps the connection's <c>channel_reestablish</c> deadline (NL-796) until <see cref="Close"/> disposes it
        /// (at once when the session is already closed).
        /// </summary>
        public void SetReestablishDeadline(ITimer timer)
        {
            Interlocked.Exchange(ref _reestablishDeadline, timer)?.Dispose();
            if (_closeCts.IsCancellationRequested)
                Interlocked.Exchange(ref _reestablishDeadline, null)?.Dispose();
        }

        /// <summary>
        /// Stops accepting inbound messages, stops the inbound loop (queued messages are dropped; the one being
        /// handled finishes) and closes the outbox (what is already queued there is still sent).
        /// </summary>
        public void Close()
        {
            _inbound.Writer.TryComplete();
            _closeCts.Cancel();
            Interlocked.Exchange(ref _reestablishDeadline, null)?.Dispose();
            Outbox.Complete();
        }
    }

    /// <summary>
    /// The <c>start_batch</c> group a connection's inbound loop is collecting, if any.
    /// </summary>
    private sealed class InboundBatchState
    {
        public PendingBatch? Pending { get; set; }
    }

    /// <summary>
    /// A <c>start_batch</c> of <c>commitment_signed</c> waiting for its <see cref="BatchSize"/> messages.
    /// </summary>
    private sealed class PendingBatch(ChannelId channelId, int batchSize)
    {
        public ChannelId ChannelId { get; } = channelId;
        public int BatchSize { get; } = batchSize;
        public List<CommitmentSignedMessage> Messages { get; } = new(batchSize);
    }
}