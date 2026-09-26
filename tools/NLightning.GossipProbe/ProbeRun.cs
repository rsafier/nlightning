using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.GossipProbe;

using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Daemon.Services;
using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Domain.Node.ValueObjects;

/// <summary>
/// One probe run: start the node, load the stored graph (timed), connect to the peers, sample every minute until the
/// stop rule, stop (timed flush) and write the summary.
/// </summary>
public sealed class ProbeRun
{
    private readonly ProbeOptions _options;
    private readonly string _runDirectory;
    private readonly Dictionary<string, object?> _summary = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PeerRecord> _peers = new(StringComparer.Ordinal);
    private readonly List<(double Minutes, int Channels, int Nodes, int Policies)> _history = [];
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastCpu;
    private double _lastWallSeconds;

    public ProbeRun(ProbeOptions options)
    {
        _options = options;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        _runDirectory = Path.Combine(options.Directory, "runs",
                                     options.Label is null ? stamp : $"{stamp}-{options.Label}");
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_runDirectory);
        using var loggerProvider = new ProbeLoggerProvider(Path.Combine(_runDirectory, "probe.log"), _options.LogLevel);
        using var meters = new GossipMeterCollector();
        var traffic = new PeerTraffic();
        traffic.OpenRangeLog(Path.Combine(_runDirectory, "range-replies.csv"));
        var tip = _options.Tip > 0 ? _options.Tip : await EsploraClient.GetTipHeightAsync(_options.EsploraUrl);
        Console.WriteLine($"Run directory {_runDirectory}; mainnet tip {tip}");

        await using var node = new ProbeNode(_options, loggerProvider, traffic, tip);
        _summary["started_utc"] = DateTime.UtcNow;
        _summary["tip"] = tip;
        _summary["peers_configured"] = _options.Peers;
        _summary["database_existed"] = File.Exists(node.DatabasePath);
        _summary["database_bytes_at_start"] = FileSize(node.DatabasePath);
        _summary["wal_bytes_at_start"] = FileSize(node.DatabasePath + "-wal");

        _clock.Start();
        node.Build(_options.SyncPeers ?? _options.Peers.Count);
        _summary["node_id"] = node.Services.GetRequiredService<Domain.Protocol.Interfaces.ISecureKeyManager>()
                                  .GetNodePubKey().ToString();
        var watch = Stopwatch.StartNew();
        await node.MigrateAsync(cancellationToken);
        _summary["migrate_seconds"] = watch.Elapsed.TotalSeconds;

        // Startup load of the persisted graph: time, retained heap and RSS
        var store = node.Services.GetRequiredService<IGraphStore>();
        var beforeHeap = GC.GetTotalMemory(forceFullCollection: true);
        var beforeRss = Process.GetCurrentProcess().WorkingSet64;
        var gossipGraph = ActivatorUtilities.CreateInstance<GossipGraphHostedService>(node.Services);
        watch.Restart();
        await gossipGraph.StartAsync(cancellationToken);
        _summary["graph_load_seconds"] = watch.Elapsed.TotalSeconds;
        _summary["graph_loaded_channels"] = store.ChannelCount;
        _summary["graph_loaded_nodes"] = store.NodeCount;
        _summary["graph_loaded_policies"] = store.PolicyCount;
        var afterHeap = GC.GetTotalMemory(forceFullCollection: true);
        Process.GetCurrentProcess().Refresh();
        _summary["graph_load_retained_heap_mb"] = (afterHeap - beforeHeap) / 1048576.0;
        _summary["graph_load_rss_delta_mb"] = (Process.GetCurrentProcess().WorkingSet64 - beforeRss) / 1048576.0;
        _summary["graph_load_memory_estimate_mb"] = store.GetMemoryEstimate().TotalMegabytes;
        Console.WriteLine($"Graph loaded in {watch.Elapsed.TotalSeconds:F2} s: {store.ChannelCount} channels, "
                        + $"{store.NodeCount} nodes, {store.PolicyCount} policies");

        var peerManager = node.Services.GetRequiredService<IPeerManager>();
        await peerManager.StartAsync(cancellationToken);
        foreach (var peer in _options.Peers)
            _peers[peer.Split('@')[0]] = new PeerRecord(peer);

        await using var samples = new StreamWriter(Path.Combine(_runDirectory, "samples.csv"));
        await using var peerSamples = new StreamWriter(Path.Combine(_runDirectory, "peers.csv"));
        await samples.WriteLineAsync(SampleHeader);
        await peerSamples.WriteLineAsync(PeerHeader);

        var describer = node.Services.GetRequiredService<GossipGraphDescriber>();
        var stopReason = "max duration";
        try
        {
            await ConnectMissingPeersAsync(peerManager);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.SampleSeconds), cancellationToken);
                await SampleAsync(node, describer, meters, peerManager, samples, peerSamples);
                if (ShouldStop(out var reason))
                {
                    stopReason = reason;
                    break;
                }

                await ConnectMissingPeersAsync(peerManager);
            }
        }
        catch (OperationCanceledException)
        {
            stopReason = "cancelled";
        }

        _summary["stop_reason"] = stopReason;
        _summary["run_minutes"] = _clock.Elapsed.TotalMinutes;
        Console.WriteLine($"Stopping ({stopReason}) after {_clock.Elapsed.TotalMinutes:F1} min");

        // Stop as the daemon does; the ingress writes the pending graph changes last (timed)
        watch.Restart();
        await peerManager.StopAsync();
        _summary["peer_manager_stop_seconds"] = watch.Elapsed.TotalSeconds;
        var pendingAtStop = store.PendingChanges;
        watch.Restart();
        await gossipGraph.StopAsync(CancellationToken.None);
        _summary["final_flush_pending_changes"] = pendingAtStop;
        _summary["final_flush_seconds"] = watch.Elapsed.TotalSeconds;
        _summary["final_channels"] = store.ChannelCount;
        _summary["final_nodes"] = store.NodeCount;
        _summary["final_policies"] = store.PolicyCount;
        _summary["database_bytes_at_stop"] = FileSize(node.DatabasePath);
        _summary["wal_bytes_at_stop"] = FileSize(node.DatabasePath + "-wal");
        _summary["funding_lookups"] = node.FundingLookups.Lookups;
        _summary["meter_counters"] = meters.Counters;
        _summary["meter_histograms"] = meters.Histograms.ToDictionary(h => h.Key,
                                                                      h => new { h.Value.Count, h.Value.Sum, h.Value.Max });
        _summary["log_warning_error_counts"] = loggerProvider.Counts;
        _summary["peer_traffic"] = traffic.Snapshot()
                                          .GroupBy(t => ProbeOptions.AliasOf(t.Key.Peer.ToString()))
                                          .ToDictionary(g => g.Key,
                                                        g => g.OrderBy(t => t.Key.Kind, StringComparer.Ordinal)
                                                              .ToDictionary(t => t.Key.Kind, t => t.Value));
        _summary["peers"] = _peers.Values.ToDictionary(p => ProbeOptions.AliasOf(p.NodeId), p => p);
        _summary["time_to_share_of_final_channels_minutes"] = TimeToShare();
        _summary["graph_shape"] = DescribeShape(store);
        var json = JsonSerializer.Serialize(_summary, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(_runDirectory, "summary.json"), json, CancellationToken.None);
        Console.WriteLine($"Summary written to {Path.Combine(_runDirectory, "summary.json")}");
        return 0;
    }

    private const string SampleHeader =
        "utc,elapsed_min,channels,graph_nodes,announced_nodes,policies,channels_without_policy,disabled_policies,"
      + "assumed_or_unverified,spent,pending_writes,memory_estimate_mb,received,accepted,rejected,orphaned,dropped,"
      + "q_ingress,q_orphans,q_retries,q_rate_limited,q_write_behind,peers_connected,sync_complete,rss_mb,"
      + "private_mb,managed_heap_mb,gc_heap_size_mb,gc0,gc1,gc2,cpu_pct_one_core,db_mb,wal_mb,warnings,errors,"
      + "funding_lookups,flushes,flush_seconds_sum,flush_seconds_max";

    private const string PeerHeader =
        "utc,elapsed_min,peer,connected,connects,disconnects_seen,initialized,queries,queries_ex,sync_peer,"
      + "range_running,last_range_sync_utc,peer_filter,our_filter,query_slot_poisoned,pending_work,"
      + "channel_announcement,channel_update,node_announcement,refused_at_door,reply_channel_range,range_scids,"
      + "reply_scids_end,queries_from_peer,filter_from_peer,encoding_zlib_replies";

    private async Task SampleAsync(ProbeNode node, GossipGraphDescriber describer, GossipMeterCollector meters,
                                   IPeerManager peerManager, StreamWriter samples, StreamWriter peerSamples)
    {
        var d = describer.Describe();
        var gauges = meters.ReadGauges();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var wall = _clock.Elapsed.TotalSeconds;
        var cpu = process.TotalProcessorTime;
        var cpuPct = wall > _lastWallSeconds
                         ? (cpu - _lastCpu).TotalSeconds / (wall - _lastWallSeconds) * 100
                         : 0;
        _lastCpu = cpu;
        _lastWallSeconds = wall;
        var gcInfo = GC.GetGCMemoryInfo();
        var connected = peerManager.ListPeers();
        var minutes = _clock.Elapsed.TotalMinutes;
        _history.Add((minutes, d.Channels, d.GraphNodes, d.Policies));
        var flushes = meters.Histograms.Where(h => h.Key.Contains("operation=flush", StringComparison.Ordinal))
                            .Select(h => h.Value).ToList();

        var row = string.Join(',', [
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), F(minutes), I(d.Channels), I(d.GraphNodes),
            I(d.AnnouncedNodes), I(d.Policies), I(d.ChannelsWithoutPolicy), I(d.DisabledPolicies),
            I(d.UnverifiedChannels), I(d.SpentChannels), I(d.PendingWrites), I(d.Memory.TotalMegabytes),
            I(meters.Sum("nlightning.gossip.messages.received")), I(meters.Sum("nlightning.gossip.messages.accepted")),
            I(meters.Sum("nlightning.gossip.messages.rejected")), I(meters.Sum("nlightning.gossip.messages.orphaned")),
            I(meters.Sum("nlightning.gossip.messages.dropped")), I(gauges.GetValueOrDefault("ingress")),
            I(gauges.GetValueOrDefault("orphans")), I(gauges.GetValueOrDefault("retries")),
            I(gauges.GetValueOrDefault("rate_limited")), I(gauges.GetValueOrDefault("graph_write_behind")),
            I(connected.Count), d.Sync?.HasCompletedInitialSync == true ? "1" : "0", Mb(process.WorkingSet64),
            Mb(process.PrivateMemorySize64), Mb(GC.GetTotalMemory(false)), Mb(gcInfo.HeapSizeBytes),
            I(GC.CollectionCount(0)), I(GC.CollectionCount(1)), I(GC.CollectionCount(2)), F(cpuPct),
            Mb(FileSize(node.DatabasePath)), Mb(FileSize(node.DatabasePath + "-wal")),
            I(node.LoggerProvider.Warnings), I(node.LoggerProvider.Errors), I(node.FundingLookups.Lookups),
            I(flushes.Sum(f => f.Count)), F(flushes.Sum(f => f.Sum)), F(flushes.Count == 0 ? 0 : flushes.Max(f => f.Max))
        ]);
        await samples.WriteLineAsync(row);
        await samples.FlushAsync();
        Console.WriteLine($"[{minutes,6:F1} min] channels {d.Channels} nodes {d.GraphNodes} (announced "
                        + $"{d.AnnouncedNodes}) policies {d.Policies} peers {connected.Count} ingress "
                        + $"{gauges.GetValueOrDefault("ingress")} orphans {gauges.GetValueOrDefault("orphans")} "
                        + $"rss {Mb(process.WorkingSet64)} MB cpu {cpuPct:F0}%");

        var states = d.Sync?.Peers ?? [];
        foreach (var record in _peers.Values)
        {
            var isConnected = connected.Any(p => p.NodeId.ToString() == record.NodeId);
            if (record.WasConnected && !isConnected)
                record.DisconnectsSeen++;
            record.WasConnected = isConnected;

            var state = states.FirstOrDefault(s => s.PeerId.ToString() == record.NodeId);
            var id = new CompactPubKey(Convert.FromHexString(record.NodeId));
            var t = node.Traffic;
            var peerRow = string.Join(',', [
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), F(minutes),
                ProbeOptions.AliasOf(record.NodeId), isConnected ? "1" : "0", I(record.Connects),
                I(record.DisconnectsSeen), B(state?.IsInitialized), B(state?.SupportsQueries),
                B(state?.SupportsQueriesEx), B(state?.IsSyncPeer), B(state?.IsRangeSyncRunning),
                state?.LastRangeSyncAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "",
                state?.PeerFilter is { } pf ? $"{pf.FirstTimestamp}+{pf.TimestampRange}" : "",
                state?.OurFilter is { } of ? $"{of.FirstTimestamp}+{of.TimestampRange}" : "",
                B(state?.IsQuerySlotPoisoned), I(state?.PendingWork ?? 0),
                I(t.Get(id, "channel_announcement")), I(t.Get(id, "channel_update")),
                I(t.Get(id, "node_announcement")),
                I(t.Get(id, "channel_announcement.refused_at_door") + t.Get(id, "channel_update.refused_at_door")
                + t.Get(id, "node_announcement.refused_at_door")),
                I(t.Get(id, "reply_channel_range")), I(t.Get(id, "reply_channel_range.scids")),
                I(t.Get(id, "reply_short_channel_ids_end")),
                I(t.Get(id, "query_channel_range") + t.Get(id, "query_short_channel_ids")),
                I(t.Get(id, "gossip_timestamp_filter")), I(t.Get(id, "reply_channel_range.encoding_1"))
            ]);
            await peerSamples.WriteLineAsync(peerRow);
        }

        await peerSamples.FlushAsync();
    }

    private async Task ConnectMissingPeersAsync(IPeerManager peerManager)
    {
        var connected = peerManager.ListPeers().Select(p => p.NodeId.ToString()).ToHashSet(StringComparer.Ordinal);
        var tasks = _peers.Values.Where(p => !connected.Contains(p.NodeId)).Select(async record =>
        {
            record.Attempts++;
            try
            {
                var model = await peerManager.ConnectToPeerAsync(new PeerAddressInfo(record.Address))
                                             .WaitAsync(TimeSpan.FromSeconds(30));
                record.Connects++;
                record.WasConnected = true;
                record.LastError = null;
                record.Features ??= model.Features.ToString();
                record.FirstConnectedAtMinutes ??= _clock.Elapsed.TotalMinutes;
                Console.WriteLine($"Connected to {ProbeOptions.AliasOf(record.NodeId)}");
            }
            catch (Exception e)
            {
                record.LastError = $"{e.GetType().Name}: {e.Message}";
                Console.WriteLine($"Could not connect to {ProbeOptions.AliasOf(record.NodeId)}: {record.LastError}");
            }
        });
        await Task.WhenAll(tasks);
    }

    private bool ShouldStop(out string reason)
    {
        var minutes = _clock.Elapsed.TotalMinutes;
        reason = "max duration";
        if (minutes >= _options.MaxMinutes)
            return true;
        if (minutes < _options.MinMinutes || _history.Count < 2)
            return false;

        // Plateau: channels, nodes and policies within 0.2 % (at least 20) over the last PlateauMinutes
        var window = _history.Where(h => h.Minutes >= minutes - _options.PlateauMinutes).ToList();
        if (window[0].Minutes > minutes - _options.PlateauMinutes + _options.SampleSeconds / 60.0 * 1.5)
            return false;

        bool Flat(Func<(double Minutes, int Channels, int Nodes, int Policies), int> value)
        {
            var values = window.Select(value).ToList();
            return values.Max() - values.Min() <= Math.Max(20, values.Max() / 500);
        }

        reason = $"plateau for {_options.PlateauMinutes} min";
        return Flat(h => h.Channels) && Flat(h => h.Nodes) && Flat(h => h.Policies);
    }

    private Dictionary<string, double?> TimeToShare()
    {
        var result = new Dictionary<string, double?>(StringComparer.Ordinal);
        if (_history.Count == 0)
            return result;

        var final = _history[^1].Channels;
        foreach (var share in (double[])[0.5, 0.9, 0.95, 0.99, 1.0])
        {
            var hit = _history.FirstOrDefault(h => h.Channels >= final * share);
            result[share.ToString("P0", CultureInfo.InvariantCulture)] = hit == default ? null : hit.Minutes;
        }

        return result;
    }

    /// <summary>
    /// How the channels look at the end: policies per channel, stale policies (older than two weeks), channels whose
    /// every policy is stale or missing (candidates for zombies), and the capacity estimate's spread.
    /// </summary>
    private static Dictionary<string, object> DescribeShape(IGraphStore store)
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var twoWeeks = (ulong)TimeSpan.FromDays(14).TotalSeconds;
        int none = 0, one = 0, two = 0, stalePolicies = 0, allStale = 0, disabledBoth = 0, withEstimate = 0;
        ulong estimateSumSat = 0;
        foreach (var channel in store.GetSnapshot().Channels)
        {
            var policies = new[] { channel.Policy1, channel.Policy2 }.Where(p => p is not null).Select(p => p!).ToList();
            switch (policies.Count)
            {
                case 0: none++; break;
                case 1: one++; break;
                default: two++; break;
            }

            var stale = policies.Count(p => p.Timestamp + twoWeeks < now);
            stalePolicies += stale;
            if (stale == policies.Count)
                allStale++;
            if (policies.Count == 2 && policies.All(p => p.IsDisabled))
                disabledBoth++;
            if (channel.EstimatedCapacityMsat is { } estimate)
            {
                withEstimate++;
                estimateSumSat += estimate / 1000;
            }
        }

        return new Dictionary<string, object>
        {
            ["channels_without_policy"] = none,
            ["channels_with_one_policy"] = one,
            ["channels_with_two_policies"] = two,
            ["stale_policies_over_14_days"] = stalePolicies,
            ["channels_without_a_fresh_policy"] = allStale,
            ["channels_disabled_both_ways"] = disabledBoth,
            ["channels_with_capacity_estimate"] = withEstimate,
            ["capacity_estimate_sum_btc"] = estimateSumSat / 100_000_000.0
        };
    }

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);
    private static string B(bool? value) => value switch { true => "1", false => "0", null => "" };

    /// <summary>What the probe knows about one configured peer.</summary>
    public sealed class PeerRecord(string address)
    {
        public string Address { get; } = address;
        public string NodeId { get; } = address.Split('@')[0];
        public int Attempts { get; set; }
        public int Connects { get; set; }
        public int DisconnectsSeen { get; set; }
        public bool WasConnected { get; set; }
        public double? FirstConnectedAtMinutes { get; set; }
        public string? Features { get; set; }
        public string? LastError { get; set; }
    }
}