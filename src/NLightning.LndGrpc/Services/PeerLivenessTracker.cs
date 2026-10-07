namespace NLightning.LndGrpc.Services;

using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Events;
using Domain.Node.Interfaces;

/// <summary>
/// What LND's channel event store and peer flap tracking report and the node does not keep (NL-1249): when each peer
/// went online and offline since this server started (LND's <c>flap_count</c>, <c>last_flap_ns</c>, a channel's
/// <c>uptime</c>), and when each channel opened after that (the start of its <c>lifetime</c>). Memory only: a restart
/// starts monitoring again, as LND's lifetime does; LND also persists its flap counts, these restart at 0.
/// </summary>
internal sealed class PeerLivenessTracker
{
    /// <summary>Online intervals kept per peer; older ones are dropped (a flapping peer's uptime then understates).</summary>
    private const int MaxIntervals = 1_000;

    private readonly object _lock = new();
    private readonly Dictionary<CompactPubKey, PeerHistory> _peers = new();
    private readonly Dictionary<ChannelId, DateTimeOffset> _channelsAdded = new();
    private readonly TimeProvider _timeProvider;

    public PeerLivenessTracker(IPeerManager peerManager, IChannelMemoryRepository channels, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        StartedAt = timeProvider.GetUtcNow();
        foreach (var peer in peerManager.ListPeers() ?? [])
            History(peer.NodeId).Online(StartedAt, isFlap: false);

        peerManager.OnPeerStateChanged += PeerStateChanged;
        channels.OnChannelAdded += ChannelAdded;
    }

    /// <summary>When monitoring started (this server's start).</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>When monitoring of <paramref name="channelId"/> started: when it was created, or this server's start.</summary>
    public DateTimeOffset MonitoredSince(ChannelId channelId)
    {
        lock (_lock)
            return _channelsAdded.TryGetValue(channelId, out var added) ? added : StartedAt;
    }

    /// <summary>How long <paramref name="peer"/> was online since <paramref name="since"/>.</summary>
    public TimeSpan Uptime(CompactPubKey peer, DateTimeOffset since)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
            return _peers.TryGetValue(peer, out var history) ? history.OnlineBetween(since, now) : TimeSpan.Zero;
    }

    /// <summary>The number of times <paramref name="peer"/> came online or went offline, and when it last did.</summary>
    public (int Count, DateTimeOffset? Last) Flaps(CompactPubKey peer)
    {
        lock (_lock)
            return _peers.TryGetValue(peer, out var history) ? (history.FlapCount, history.LastFlap) : (0, null);
    }

    private void PeerStateChanged(object? sender, PeerStateChangedEventArgs args)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            var history = History(args.PeerPubKey);
            if (args.Online)
                history.Online(now, isFlap: true);
            else
                history.Offline(now);
        }
    }

    private void ChannelAdded(object? sender, ChannelUpdatedEventArgs args)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
            _channelsAdded.TryAdd(args.Channel.ChannelId, now);
    }

    private PeerHistory History(CompactPubKey peer)
    {
        if (!_peers.TryGetValue(peer, out var history))
            _peers[peer] = history = new PeerHistory();
        return history;
    }

    /// <summary>One peer's online intervals and flaps (guarded by the tracker's lock).</summary>
    private sealed class PeerHistory
    {
        private readonly List<(DateTimeOffset Start, DateTimeOffset? End)> _intervals = [];

        public int FlapCount { get; private set; }

        public DateTimeOffset? LastFlap { get; private set; }

        public void Online(DateTimeOffset at, bool isFlap)
        {
            if (_intervals.Count > 0 && _intervals[^1].End is null)
                return;

            _intervals.Add((at, null));
            if (_intervals.Count > MaxIntervals)
                _intervals.RemoveAt(0);
            if (isFlap)
                Flap(at);
        }

        public void Offline(DateTimeOffset at)
        {
            if (_intervals.Count == 0 || _intervals[^1].End is not null)
                return;

            _intervals[^1] = (_intervals[^1].Start, at);
            Flap(at);
        }

        public TimeSpan OnlineBetween(DateTimeOffset since, DateTimeOffset now)
        {
            var total = TimeSpan.Zero;
            foreach (var (start, end) in _intervals)
            {
                var from = start > since ? start : since;
                var to = end ?? now;
                if (to > from)
                    total += to - from;
            }

            return total;
        }

        private void Flap(DateTimeOffset at)
        {
            FlapCount++;
            LastFlap = at;
        }
    }
}