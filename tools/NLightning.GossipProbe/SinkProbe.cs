using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NLightning.GossipProbe;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;

/// <summary>
/// The NL-417 sink's receive log: every gossip message the relayer sent, in arrival order (the peer service's read
/// loop), with the ordering BOLT 7 asks of a relay: a <c>channel_update</c> only after its <c>channel_announcement</c>,
/// a <c>node_announcement</c> only after a channel of that node, and no version twice.
/// </summary>
public sealed class SinkRecorder
{
    private readonly Lock _lock = new();
    private readonly HashSet<ShortChannelId> _announced = [];
    private readonly HashSet<CompactPubKey> _nodesWithChannel = [];
    private readonly HashSet<string> _versions = new(StringComparer.Ordinal);
    private readonly Dictionary<(ShortChannelId, int), uint> _newestUpdate = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public long Announcements { get; private set; }
    public long Updates { get; private set; }
    public long NodeAnnouncements { get; private set; }
    public long Duplicates { get; private set; }

    /// <summary><c>channel_update</c>s that arrived before any <c>channel_announcement</c> of their channel.</summary>
    public long UpdatesBeforeAnnouncement { get; private set; }

    /// <summary><c>node_announcement</c>s that arrived before any <c>channel_announcement</c> of that node.</summary>
    public long NodesBeforeChannel { get; private set; }

    /// <summary><c>channel_update</c>s older than one already received for the same channel and direction.</summary>
    public long OlderUpdates { get; private set; }

    /// <summary>The first few short channel ids of <see cref="UpdatesBeforeAnnouncement"/>, for the log.</summary>
    public List<string> OrphanSamples { get; } = [];

    /// <summary>Seconds (since the probe started) of the filter sent, the first and the latest message.</summary>
    public double? FilterSentAt { get; private set; }

    public double? FirstMessageAt { get; private set; }
    public double? LastMessageAt { get; private set; }

    public long Total => Announcements + Updates + NodeAnnouncements;

    public void RecordFilterSent()
    {
        lock (_lock)
            FilterSentAt ??= _clock.Elapsed.TotalSeconds;
    }

    public void Record(IMessage message)
    {
        lock (_lock)
        {
            var now = _clock.Elapsed.TotalSeconds;
            switch (message)
            {
                case ChannelAnnouncementMessage a:
                    Announcements++;
                    if (!_versions.Add("256:" + a.Payload.ShortChannelId))
                        Duplicates++;
                    _announced.Add(a.Payload.ShortChannelId);
                    _nodesWithChannel.Add(a.Payload.NodeId1);
                    _nodesWithChannel.Add(a.Payload.NodeId2);
                    break;
                case ChannelUpdateMessage u:
                    Updates++;
                    if (!_versions.Add("258:" + Convert.ToHexString(u.Payload.Signature.Value)))
                        Duplicates++;
                    if (!_announced.Contains(u.Payload.ShortChannelId))
                    {
                        UpdatesBeforeAnnouncement++;
                        if (OrphanSamples.Count < 20)
                            OrphanSamples.Add(u.Payload.ShortChannelId.ToString());
                    }

                    var key = (u.Payload.ShortChannelId, u.Payload.ChannelFlags & 1);
                    if (_newestUpdate.TryGetValue(key, out var newest) && u.Payload.Timestamp < newest)
                        OlderUpdates++;
                    else
                        _newestUpdate[key] = u.Payload.Timestamp;
                    break;
                case NodeAnnouncementMessage n:
                    NodeAnnouncements++;
                    if (!_versions.Add("257:" + Convert.ToHexString(n.Payload.Signature.Value)))
                        Duplicates++;
                    if (!_nodesWithChannel.Contains(n.Payload.NodeId))
                        NodesBeforeChannel++;
                    break;
                default:
                    return;
            }

            FirstMessageAt ??= now;
            LastMessageAt = now;
        }
    }

    public Dictionary<string, object?> Summarize()
    {
        lock (_lock)
            return new Dictionary<string, object?>
            {
                ["channel_announcement"] = Announcements,
                ["channel_update"] = Updates,
                ["node_announcement"] = NodeAnnouncements,
                ["total"] = Total,
                ["duplicates"] = Duplicates,
                ["channel_update_before_its_announcement"] = UpdatesBeforeAnnouncement,
                ["node_announcement_before_a_channel_of_the_node"] = NodesBeforeChannel,
                ["channel_update_older_than_one_received"] = OlderUpdates,
                ["orphan_samples"] = OrphanSamples.ToList(),
                ["distinct_channels_announced"] = _announced.Count,
                ["filter_sent_at_seconds"] = FilterSentAt,
                ["first_message_at_seconds"] = FirstMessageAt,
                ["last_message_at_seconds"] = LastMessageAt
            };
    }
}

/// <summary>
/// The NL-417 sink's filter: the node's own sync is off (no query, no filter), and at each <c>init</c> this sends the
/// peer <c>gossip_timestamp_filter(0, 0xFFFFFFFF)</c>: everything it has, then everything new.
/// </summary>
public sealed class SinkFilterSyncService(IGossipSyncService inner, SinkRecorder recorder) : IGossipSyncService
{
    public void OnPeerInitialized(IPeerService peer)
    {
        inner.OnPeerInitialized(peer);
        _ = Task.Run(async () =>
        {
            try
            {
                await peer.SendGossipMessageAsync(new GossipTimestampFilterMessage(
                                                      new GossipTimestampFilterPayload(
                                                          BitcoinNetwork.Mainnet.ChainHash, 0, uint.MaxValue)));
                recorder.RecordFilterSent();
                Console.WriteLine($"Sent gossip_timestamp_filter(0, 0xFFFFFFFF) to {peer.PeerPubKey}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not send the filter to {peer.PeerPubKey}: {e.GetType().Name}: {e.Message}");
            }
        });
    }

    public void HandleMessage(IPeerService peer, IMessage message) => inner.HandleMessage(peer, message);
}

/// <summary>
/// The NL-417 sink's slow reader: a local TCP proxy between the sink and the relayer that forwards the sink's bytes
/// at once and the relayer's bytes at the rate of the current <see cref="ProbeOptions.ReadPhase"/> (0 stops reading;
/// the relayer's socket buffers fill and its outbox backs up, as behind a slow peer). The phases start at the first
/// connection. <c>proxy.csv</c> gets one row per second.
/// </summary>
public sealed class ThrottledProxy : IAsyncDisposable
{
    private const int ChunkSize = 4096;

    private readonly IReadOnlyList<ProbeOptions.ReadPhase> _phases;
    private readonly IPEndPoint _target;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly StreamWriter _log;
    private readonly Stopwatch _clock = new();
    private readonly ConcurrentBag<Task> _tasks = [];
    private long _forwardedToSink;
    private long _forwardedToRelayer;
    private int _connections;

    public ThrottledProxy(int listenPort, IPEndPoint target, IReadOnlyList<ProbeOptions.ReadPhase> phases,
                          string logPath)
    {
        _phases = phases;
        _target = target;
        _listener = new TcpListener(IPAddress.Loopback, listenPort);
        _log = new StreamWriter(logPath) { AutoFlush = true };
        _log.WriteLine("utc,elapsed_s,phase,bytes_per_second_limit,forwarded_to_sink,forwarded_to_relayer,connections");
    }

    public long ForwardedToSink => Interlocked.Read(ref _forwardedToSink);
    public string CurrentPhaseLabel => PhaseAt(_clock.Elapsed.TotalSeconds) is var (index, limit)
                                           ? $"{index}:{(limit is null ? "max" : limit.Value.ToString(CultureInfo.InvariantCulture))}"
                                           : "";

    public void Start()
    {
        _listener.Start();
        _tasks.Add(Task.Run(AcceptLoopAsync));
        _tasks.Add(Task.Run(LogLoopAsync));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Stopping
        }

        await _log.DisposeAsync();
        _cts.Dispose();
    }

    private (int Index, long? Limit) PhaseAt(double seconds)
    {
        var start = 0.0;
        for (var i = 0; i < _phases.Count; i++)
        {
            if (i == _phases.Count - 1 || seconds < start + _phases[i].Seconds)
                return (i, _phases[i].BytesPerSecond);
            start += _phases[i].Seconds;
        }

        return (0, null);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient sink;
            try
            {
                sink = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            if (!_clock.IsRunning)
                _clock.Start();
            Interlocked.Increment(ref _connections);
            var relayer = new TcpClient { ReceiveBufferSize = 64 * 1024 };
            try
            {
                await relayer.ConnectAsync(_target, _cts.Token);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Proxy: could not reach the relayer: {e.Message}");
                sink.Dispose();
                relayer.Dispose();
                continue;
            }

            sink.SendBufferSize = 64 * 1024;
            Console.WriteLine("Proxy: sink connected, forwarding to the relayer");
            _tasks.Add(Task.Run(() => PumpAsync(sink, relayer)));
        }
    }

    private async Task PumpAsync(TcpClient sink, TcpClient relayer)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var up = CopyAsync(sink.GetStream(), relayer.GetStream(), throttled: false, connection.Token);
        var down = CopyAsync(relayer.GetStream(), sink.GetStream(), throttled: true, connection.Token);
        await Task.WhenAny(up, down);
        await connection.CancelAsync();
        sink.Dispose();
        relayer.Dispose();
        Console.WriteLine("Proxy: connection closed");
    }

    private async Task CopyAsync(Stream from, Stream to, bool throttled, CancellationToken token)
    {
        var buffer = new byte[ChunkSize];
        var windowStart = _clock.Elapsed.TotalSeconds;
        long windowBytes = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var want = buffer.Length;
                if (throttled)
                {
                    // Token bucket per one-second window: stop reading once this second's budget is spent
                    while (true)
                    {
                        var now = _clock.Elapsed.TotalSeconds;
                        if (now - windowStart >= 1)
                        {
                            windowStart = now;
                            windowBytes = 0;
                        }

                        var limit = PhaseAt(now).Limit;
                        if (limit is null)
                            break;

                        var left = limit.Value - windowBytes;
                        if (left > 0)
                        {
                            want = (int)Math.Min(want, left);
                            break;
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(20), token);
                    }
                }

                var read = await from.ReadAsync(buffer.AsMemory(0, want), token);
                if (read == 0)
                    return;

                await to.WriteAsync(buffer.AsMemory(0, read), token);
                windowBytes += read;
                if (throttled)
                    Interlocked.Add(ref _forwardedToSink, read);
                else
                    Interlocked.Add(ref _forwardedToRelayer, read);
            }
        }
        catch
        {
            // The connection ended
        }
    }

    private async Task LogLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token);
            }
            catch
            {
                return;
            }

            if (!_clock.IsRunning)
                continue;

            var seconds = _clock.Elapsed.TotalSeconds;
            var (index, limit) = PhaseAt(seconds);
            await _log.WriteLineAsync(string.Join(',', DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                                                  seconds.ToString("F1", CultureInfo.InvariantCulture), index,
                                                  limit?.ToString(CultureInfo.InvariantCulture) ?? "max",
                                                  ForwardedToSink, Interlocked.Read(ref _forwardedToRelayer),
                                                  _connections));
        }
    }
}