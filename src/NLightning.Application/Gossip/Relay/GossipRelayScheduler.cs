using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Relay;

using Domain.Channels.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Interfaces;

/// <inheritdoc cref="IGossipRelayScheduler"/>
/// <remarks>
/// A singleton. The periodic flush starts with the first queued message and runs every
/// <see cref="GossipOptions.OwnGossipFlushInterval"/> from then on, so a peer that connects later gets our gossip at
/// the next flush. What went out is remembered per connection (per <c>IPeerService</c>, weakly): a new connection gets
/// everything again. Our own messages stay queued (the latest per channel, direction and node) for the life of the
/// process; the announcement services queue them again after a restart.
/// </remarks>
public sealed class GossipRelayScheduler : IGossipRelayScheduler, IDisposable
{
    private const int ChannelAnnouncementRank = 0;
    private const int ChannelUpdateRank = 1;
    private const int NodeAnnouncementRank = 2;

    private readonly ILogger<GossipRelayScheduler> _logger;
    private readonly IGossipPeerDirectory _peerDirectory;
    private readonly NodeOptions _nodeOptions;
    private readonly GossipOptions _gossipOptions;
    private readonly TimeProvider _timeProvider;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, OwnEntry> _own = [];
    private readonly ConditionalWeakTable<object, HashSet<string>> _sentPerConnection = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private long _sequence;
    private ITimer? _timer;
    private bool _disposed;

    public GossipRelayScheduler(IGossipPeerDirectory peerDirectory, ILogger<GossipRelayScheduler> logger,
                                IOptions<NodeOptions> nodeOptions, IOptions<GossipOptions>? gossipOptions = null,
                                TimeProvider? timeProvider = null)
    {
        _peerDirectory = peerDirectory;
        _logger = logger;
        _nodeOptions = nodeOptions.Value;
        _gossipOptions = gossipOptions?.Value ?? new GossipOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
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

            // BOLT 7: a channel_announcement never goes out without an update for its channel; 256, then 258, then 257
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
        }
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

            try
            {
                await peer.Service.SendGossipMessageAsync(entry.Message);
                sent.Add(entry.Digest);
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

    /// <summary>One queued message of ours.</summary>
    private sealed record OwnEntry(int Rank, IMessage Message, string Digest, uint Timestamp,
                                   ShortChannelId? ShortChannelId, long Sequence);
}