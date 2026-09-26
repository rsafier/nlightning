using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Relay;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Graph.Interfaces;
using Interfaces;
using Metrics;
using Sync.Interfaces;

/// <inheritdoc cref="IGossipRelayScheduler"/>
/// <remarks>
/// <para>
/// A singleton with two paths. <b>Our own gossip</b> (G1-T7): the periodic flush starts with the first queued message
/// and runs every <see cref="GossipOptions.OwnGossipFlushInterval"/> from then on, so a peer that connects later gets
/// our gossip at the next flush. What went out is remembered per connection (per <c>IPeerService</c>, weakly): a new
/// connection gets everything again. Our own messages stay queued (the latest per channel, direction and node) for the
/// life of the process; the announcement services queue them again after a restart.
/// </para>
/// <para>
/// <b>Other nodes' gossip</b> (G3-T3, see the other part of this class) is relayed only when a graph, the sync manager
/// (the peers' filters) and <see cref="GossipRelayOptions.IsRelayEnabledFor"/> are there.
/// </para>
/// <para>
/// Everything goes out through <see cref="IGossipPeerSender"/>: the peer's <c>PeerOutbox</c> when the peer manager
/// offers it (NL-351), else the peer service directly.
/// </para>
/// </remarks>
public sealed partial class GossipRelayScheduler : IGossipRelayScheduler, IDisposable
{
    private const int ChannelAnnouncementRank = 0;
    private const int ChannelUpdateRank = 1;
    private const int NodeAnnouncementRank = 2;

    private readonly ILogger<GossipRelayScheduler> _logger;
    private readonly IGossipPeerDirectory _peerDirectory;
    private readonly IGossipPeerSender _sender;
    private readonly NodeOptions _nodeOptions;
    private readonly GossipOptions _gossipOptions;
    private readonly TimeProvider _timeProvider;
    private readonly GossipMetrics? _metrics;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, OwnEntry> _own = [];
    private readonly ConditionalWeakTable<object, HashSet<string>> _sentPerConnection = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private long _sequence;
    private ITimer? _timer;
    private bool _disposed;

    /// <param name="peerDirectory">The connected peers.</param>
    /// <param name="logger">A logger.</param>
    /// <param name="nodeOptions">Our chain.</param>
    /// <param name="gossipOptions">The flush interval of our own gossip.</param>
    /// <param name="timeProvider">The clock (flushes, staggering, backlog pacing).</param>
    /// <param name="sender">How a message goes to a connection (default: the peer service directly).</param>
    /// <param name="graphStore">The graph whose accepted gossip is relayed (null: only our own gossip).</param>
    /// <param name="syncManager">The peers' <c>gossip_timestamp_filter</c>s (null: only our own gossip).</param>
    /// <param name="originTracker">Which peer sent which message (null: no origin suppression).</param>
    /// <param name="relayOptions">The relay settings.</param>
    /// <param name="secureKeyManager">Our node id: our own messages are left to the own path.</param>
    /// <param name="metrics">Where relayed and dropped messages are counted (null: nowhere).</param>
    public GossipRelayScheduler(IGossipPeerDirectory peerDirectory, ILogger<GossipRelayScheduler> logger,
                                IOptions<NodeOptions> nodeOptions, IOptions<GossipOptions>? gossipOptions = null,
                                TimeProvider? timeProvider = null, IGossipPeerSender? sender = null,
                                IGraphStore? graphStore = null, IGossipSyncManager? syncManager = null,
                                GossipOriginTracker? originTracker = null,
                                IOptions<GossipRelayOptions>? relayOptions = null,
                                ISecureKeyManager? secureKeyManager = null, GossipMetrics? metrics = null)
    {
        _peerDirectory = peerDirectory;
        _logger = logger;
        _nodeOptions = nodeOptions.Value;
        _gossipOptions = gossipOptions?.Value ?? new GossipOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sender = sender ?? new PeerGossipSender();
        _graphStore = graphStore;
        _syncManager = syncManager;
        _originTracker = originTracker;
        _relayOptions = relayOptions?.Value ?? new GossipRelayOptions();
        _ourNodeId = secureKeyManager?.GetNodePubKey();
        _metrics = metrics;
        metrics?.RegisterQueue("relay_pending", () => _relayPeers.Sum(p => (long)p.Value.PendingCount));

        if (IsRelayingOthers)
            _syncManager!.FilterReceived += OnFilterReceived;
    }

    /// <inheritdoc />
    public void EnqueueOwnChannelAnnouncement(ChannelAnnouncementPayload announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        Enqueue($"256:{announcement.ShortChannelId}", ChannelAnnouncementRank, 0, announcement.ShortChannelId,
                new ChannelAnnouncementMessage(announcement), announcement.GetBytes());
    }

    /// <inheritdoc />
    public void EnqueueOwnChannelUpdate(ChannelUpdatePayload update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Enqueue($"258:{update.ShortChannelId}:{(update.Direction ? 1 : 0)}", ChannelUpdateRank, update.Timestamp,
                update.ShortChannelId, new ChannelUpdateMessage(update), update.GetBytes());
    }

    /// <inheritdoc />
    public void EnqueueOwnNodeAnnouncement(NodeAnnouncementPayload announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        Enqueue($"257:{announcement.NodeId}", NodeAnnouncementRank, announcement.Timestamp, null,
                new NodeAnnouncementMessage(announcement), announcement.GetBytes());
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken);
        try
        {
            List<OwnEntry> entries;
            lock (_lock)
                entries = _own.Values.ToList();

            // BOLT 7: a channel_announcement never goes out without an update for its channel, and our
            // node_announcement never before one of our channel_announcements; 256, then 258, then 257
            var updated = entries.Where(e => e.Rank == ChannelUpdateRank).Select(e => e.ShortChannelId!.Value)
                                 .ToHashSet();
            var ordered = entries.Where(e => e.Rank != ChannelAnnouncementRank
                                          || updated.Contains(e.ShortChannelId!.Value))
                                 .OrderBy(e => e.Rank).ThenBy(e => e.Sequence)
                                 .ToList();
            if (ordered.Count == 0)
                return;

            var current = entries.Select(e => e.Digest).ToHashSet();
            foreach (var peer in _peerDirectory.GetConnectedPeers())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsOnOurChain(peer))
                    continue;

                var sent = _sentPerConnection.GetValue(peer.Service, _ => []);
                sent.IntersectWith(current);
                await SendToPeerAsync(peer, ordered, sent);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _relayTimer?.Dispose();
            _relayTimer = null;
        }

        if (IsRelayingOthers)
            _syncManager!.FilterReceived -= OnFilterReceived;
    }

    private void Enqueue(string key, int rank, uint timestamp, ShortChannelId? shortChannelId, IMessage message,
                         byte[] bytes)
    {
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        lock (_lock)
        {
            if (_own.TryGetValue(key, out var existing)
             && (existing.Digest == digest || (rank != ChannelAnnouncementRank && timestamp <= existing.Timestamp)))
                return;

            _own[key] = new OwnEntry(rank, message, digest, timestamp, shortChannelId, ++_sequence);
            if (_timer is null && !_disposed)
            {
                var interval = _gossipOptions.OwnGossipFlushInterval > TimeSpan.Zero
                                   ? _gossipOptions.OwnGossipFlushInterval
                                   : TimeSpan.FromSeconds(60);
                _timer = _timeProvider.CreateTimer(_ => _ = FlushInBackgroundAsync(), null, interval, interval);
            }
        }

        _logger.LogDebug("Queued our {MessageType} ({Key}) for the next gossip flush", message.Type, key);
    }

    private async Task SendToPeerAsync(GossipPeer peer, List<OwnEntry> ordered, HashSet<string> sent)
    {
        foreach (var entry in ordered)
        {
            if (sent.Contains(entry.Digest))
                continue;

            // BOLT 7: a node_announcement for a node with no known channel is ignored, so it waits until one of our
            // channel_announcements went out on this connection (earlier, or above in this flush: 256 ranks first)
            if (entry.Rank == NodeAnnouncementRank
             && !ordered.Any(e => e.Rank == ChannelAnnouncementRank && sent.Contains(e.Digest)))
                continue;

            try
            {
                if (!await _sender.SendAsync(peer, entry.Message))
                    return;

                sent.Add(entry.Digest);
                _metrics?.RecordRelayed(entry.Message.Type, "own");
            }
            catch (Exception e)
            {
                // The connection is going away: the next connection gets everything again
                _logger.LogDebug(e, "Could not send our {MessageType} to peer {Peer}", entry.Message.Type,
                                 peer.NodeId);
                return;
            }
        }
    }

    /// <summary>BOLT 7: SHOULD NOT send gossip to a peer whose <c>init</c> networks exclude our chain.</summary>
    private bool IsOnOurChain(GossipPeer peer)
    {
        var chains = peer.Service.Features.ChainHashes.ToList();
        return chains.Count == 0 || chains.Contains(_nodeOptions.BitcoinNetwork.ChainHash);
    }

    private async Task FlushInBackgroundAsync()
    {
        try
        {
            await FlushAsync();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Our gossip flush failed; the next one retries");
        }
    }

    private bool IsOurs(CompactPubKey nodeId) => _ourNodeId is { } ours && ours == nodeId;

    /// <summary>One queued message of ours.</summary>
    private sealed record OwnEntry(int Rank, IMessage Message, string Digest, uint Timestamp,
                                   ShortChannelId? ShortChannelId, long Sequence);
}