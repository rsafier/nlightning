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
    private RpcSettings? _rpc;
    private TimeSpan _lastCpu;
    private double _lastWallSeconds;
    private BootstrapProbe? _bootstrap;
    private ThrottledProxy? _proxy;
    private StreamWriter? _relayLog;
    private StreamWriter? _relayPeerLog;
    private StreamWriter? _sinkLog;

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
        using var http = new HttpRequestCollector();
        RpcSettings? rpc = null;
        ChainInfo? chainAtStart = null;
        uint tip;
        if (_options.IsRpc)
        {
            rpc = RpcSettings.Load(_options.RpcEnvFile);
            chainAtStart = await ChainInfo.ReadAsync(rpc);
            tip = _options.Tip > 0
                      ? _options.Tip
                      : _options.SyncTip == "blocks" ? chainAtStart.Blocks : chainAtStart.Headers;
            _summary["chain_at_start"] = chainAtStart;
            Console.WriteLine($"bitcoind: blocks {chainAtStart.Blocks}, headers {chainAtStart.Headers}, IBD "
                            + $"{chainAtStart.InitialBlockDownload}, pruned {chainAtStart.Pruned}");
        }
        else
        {
            tip = _options.Tip > 0 ? _options.Tip : await EsploraClient.GetTipHeightAsync(_options.EsploraUrl);
        }

        Console.WriteLine($"Run directory {_runDirectory}; sync tip {tip}; chain {_options.Chain}");
        _rpc = rpc;

        await using var node = new ProbeNode(_options, loggerProvider, traffic, tip, rpc);
        _summary["started_utc"] = DateTime.UtcNow;
        _summary["tip"] = tip;
        _summary["peers_configured"] = _options.Peers;
        _summary["database_existed"] = File.Exists(node.DatabasePath);
        _summary["database_bytes_at_start"] = FileSize(node.DatabasePath);
        _summary["wal_bytes_at_start"] = FileSize(node.DatabasePath + "-wal");

        _clock.Start();
        // A bootstrap run keeps the product's Gossip:SyncPeers unless --sync-peers is given
        node.Build(_options.Bootstrap ? _options.SyncPeers : _options.SyncPeers ?? _options.Peers.Count);
        _summary["node_id"] = node.Services.GetRequiredService<Domain.Protocol.Interfaces.ISecureKeyManager>()
                                  .GetNodePubKey().ToString();
        Console.WriteLine($"Node id {_summary["node_id"]}, listening on 127.0.0.1:{_options.ListenPort}");
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

        // RPC mode: follow bitcoind's blocks from its current height (the pruner's spend detection)
        using var followerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? followerLoop = null;
        if (node.Follower is { } follower && chainAtStart is not null)
        {
            node.TimedLookups!.OpenLog(Path.Combine(_runDirectory, "lookups.csv"));
            await follower.StartAtAsync(chainAtStart.Blocks);
            followerLoop = Task.Run(() => follower.RunAsync(TimeSpan.FromSeconds(_options.BlockPollSeconds),
                                                            followerCts.Token), CancellationToken.None);
        }

        var peerManager = node.Services.GetRequiredService<IPeerManager>();
        await peerManager.StartAsync(cancellationToken);
        if (_options.Bootstrap)
        {
            // BOLT 10 (NL-113): the daemon's order, the bootstrap after the peer manager; no configured peer
            _bootstrap = new BootstrapProbe(node.Services, _clock, _runDirectory);
            await _bootstrap.StartAsync(cancellationToken);
        }

        ThrottledProxy? proxy = null;
        foreach (var peer in _options.Peers)
        {
            var address = peer;
            if (_options.ReadPhases is { } phases)
            {
                // NL-417 sink with a slow reader: the relayer is reached through the throttled local proxy
                var endpoint = peer.Split('@')[1];
                var colon = endpoint.LastIndexOf(':');
                var target = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(endpoint[..colon]),
                                                       int.Parse(endpoint[(colon + 1)..], CultureInfo.InvariantCulture));
                proxy = new ThrottledProxy(_options.ProxyPort, target, phases, Path.Combine(_runDirectory, "proxy.csv"));
                proxy.Start();
                address = $"{peer.Split('@')[0]}@127.0.0.1:{_options.ProxyPort}";
                Console.WriteLine($"Reading {endpoint} through the throttled proxy on 127.0.0.1:{_options.ProxyPort}");
            }

            _peers[peer.Split('@')[0]] = new PeerRecord(address);
        }

        _proxy = proxy;
        await using var samples = new StreamWriter(Path.Combine(_runDirectory, "samples.csv"));
        await using var peerSamples = new StreamWriter(Path.Combine(_runDirectory, "peers.csv"));
        await samples.WriteLineAsync(SampleHeader);
        await peerSamples.WriteLineAsync(PeerHeader);
        if (node.Traffic.Relay is not null)
        {
            _relayLog = new StreamWriter(Path.Combine(_runDirectory, "relay.csv"));
            _relayPeerLog = new StreamWriter(Path.Combine(_runDirectory, "relay-peers.csv"));
            await _relayLog.WriteLineAsync(RelayHeader);
            await _relayPeerLog.WriteLineAsync(RelayPeerHeader);
        }

        if (node.Traffic.Sink is not null)
        {
            _sinkLog = new StreamWriter(Path.Combine(_runDirectory, "sink.csv"));
            await _sinkLog.WriteLineAsync(SinkHeader);
        }

        var describer = node.Services.GetRequiredService<GossipGraphDescriber>();
        var stopReason = "max duration";
        try
        {
            await ConnectMissingPeersAsync(peerManager);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.SampleSeconds), cancellationToken);
                await SampleAsync(node, describer, meters, peerManager, samples, peerSamples, http);
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

        await followerCts.CancelAsync();
        if (followerLoop is not null)
            await followerLoop;

        _summary["stop_reason"] = stopReason;
        _summary["run_minutes"] = _clock.Elapsed.TotalMinutes;
        Console.WriteLine($"Stopping ({stopReason}) after {_clock.Elapsed.TotalMinutes:F1} min");

        if (_bootstrap is not null)
        {
            await _bootstrap.StopAsync();
            await _bootstrap.CensusAsync(CancellationToken.None);
        }

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
        _summary["final_pending_announcements"] =
            node.Services.GetService<GossipIngress>()?.PendingAnnouncementCount;
        if (node.Services.GetService<GossipMemoryBudget>() is { } finalBudget)
            _summary["memory_budget"] = new Dictionary<string, object?>
            {
                ["budget_mb"] = finalBudget.BudgetBytes / 1048576.0,
                ["over_budget_at_stop"] = finalBudget.IsOverBudget,
                ["refused"] = finalBudget.RefusedCount
            };
        _summary["final_nodes"] = store.NodeCount;
        _summary["final_policies"] = store.PolicyCount;
        _summary["database_bytes_at_stop"] = FileSize(node.DatabasePath);
        _summary["wal_bytes_at_stop"] = FileSize(node.DatabasePath + "-wal");
        _summary["funding_lookups"] = node.FundingLookupCount;
        if (node.TimedLookups is { } timed)
        {
            timed.Flush();
            _summary["chain"] = new Dictionary<string, object?>
            {
                ["lookup_statuses"] = new SortedDictionary<string, long>(timed.Statuses),
                ["verify_latency"] = timed.Verify.Describe(),
                ["lookup_latency"] = timed.Lookup.Describe(),
                ["chain_service_calls"] = node.Chain!.Calls.OrderBy(c => c.Key, StringComparer.Ordinal)
                                              .ToDictionary(c => c.Key, c => c.Value.Describe()),
                ["http_requests"] = http.Requests.Describe(),
                ["http_requests_per_minute"] = http.PerMinute,
                ["follower"] = new
                {
                    node.Follower!.LastBlockHeight,
                    node.Follower.BlocksProcessed,
                    node.Follower.SpentOutpointsRaised,
                    node.Follower.Reorgs,
                    node.Follower.LastError
                },
                ["chain_at_stop"] = await TryReadChainAsync()
            };
            timed.Dispose();
        }
        _summary["meter_counters"] = meters.Counters;
        _summary["meter_histograms"] = meters.Histograms.ToDictionary(h => h.Key,
                                                                      h => new { h.Value.Count, h.Value.Sum, h.Value.Max });
        _summary["log_warning_error_counts"] = loggerProvider.Counts;
        if (traffic.Relay is { } relay)
        {
            _summary["relay"] = relay.Snapshot();
            Console.WriteLine($"Relay to {_options.RelayTo ?? "every peer"}: "
                            + $"{relay.Total("announcement") + relay.Total("update")} sent or queued, "
                            + $"{relay.Total(".refused")} refused by the outbox, "
                            + $"{relay.Total(".echo_to_origin")} echoed to their origin");
        }

        if (traffic.Sink is { } sink)
        {
            var summary = sink.Summarize();
            summary["graph_channels_at_stop"] = store.ChannelCount;
            summary["proxy_forwarded_bytes"] = _proxy?.ForwardedToSink;
            _summary["sink"] = summary;
            Console.WriteLine($"Sink: {sink.Total} gossip messages ({sink.Announcements} 256, {sink.Updates} 258, "
                            + $"{sink.NodeAnnouncements} 257), {sink.Duplicates} duplicates, "
                            + $"{sink.UpdatesBeforeAnnouncement} 258 before its 256, "
                            + $"{sink.NodesBeforeChannel} 257 before a 256 of the node");
        }

        if (_proxy is not null)
            await _proxy.DisposeAsync();
        foreach (var log in new[] { _relayLog, _relayPeerLog, _sinkLog })
            if (log is not null)
                await log.DisposeAsync();

        _summary["peer_traffic"] = traffic.Snapshot()
                                          .GroupBy(t => ProbeOptions.AliasOf(t.Key.Peer.ToString()))
                                          .ToDictionary(g => g.Key,
                                                        g => g.OrderBy(t => t.Key.Kind, StringComparer.Ordinal)
                                                              .ToDictionary(t => t.Key.Kind, t => t.Value));
        if (_bootstrap is not null)
            _summary["bootstrap"] = _bootstrap.Summarize();
        _summary["peers"] = _peers.Values.ToDictionary(p => ProbeOptions.AliasOf(p.NodeId), p => p);
        _summary["time_to_share_of_final_channels_minutes"] = TimeToShare();
        _summary["graph_shape"] = DescribeShape(store);
        _summary["channels_without_policy_diagnosis"] = DiagnoseWithoutPolicy(store, traffic);
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
      + "funding_lookups,flushes,flush_seconds_sum,flush_seconds_max,http_requests,http_failures,"
      + "bitcoind_blocks,follower_height,follower_blocks,verified,spent_marked,pending_announcements,"
      + "q_relay_pending,q_outbox_gossip,over_memory_budget,memory_budget_refused,bootstrap_seed_queries,"
      + "bootstrap_candidates,bootstrap_dials,bootstrap_connected,bootstrap_failed,bootstrap_timed_out";

    private const string PeerHeader =
        "utc,elapsed_min,peer,connected,connects,disconnects_seen,initialized,queries,queries_ex,sync_peer,"
      + "range_running,last_range_sync_utc,peer_filter,our_filter,query_slot_poisoned,pending_work,"
      + "channel_announcement,channel_update,node_announcement,refused_at_door,reply_channel_range,range_scids,"
      + "reply_scids_end,queries_from_peer,filter_from_peer,encoding_zlib_replies";

    private async Task SampleAsync(ProbeNode node, GossipGraphDescriber describer, GossipMeterCollector meters,
                                   IPeerManager peerManager, StreamWriter samples, StreamWriter peerSamples,
                                   HttpRequestCollector? http)
    {
        var d = describer.Describe();
        var chainNow = _rpc is null ? null : await TryReadChainAsync();
        var verified = node.Services.GetRequiredService<IGraphStore>().GetSnapshot().Channels
                           .Count(c => c.Verification == Domain.Gossip.Graph.GraphChannelVerification.Verified);
        var gauges = meters.ReadGauges();
        var budget = node.Services.GetService<GossipMemoryBudget>();
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
            I(node.LoggerProvider.Warnings), I(node.LoggerProvider.Errors), I(node.FundingLookupCount),
            I(flushes.Sum(f => f.Count)), F(flushes.Sum(f => f.Sum)), F(flushes.Count == 0 ? 0 : flushes.Max(f => f.Max)),
            I(http?.Requests.Count ?? 0), I(http?.Requests.Failures ?? 0), I(chainNow?.Blocks ?? 0),
            I(node.Follower?.LastBlockHeight ?? 0), I(node.Follower?.BlocksProcessed ?? 0), I(verified), I(d.SpentChannels),
            I(d.Ingress?.PendingAnnouncements ?? 0), I(gauges.GetValueOrDefault("relay_pending")),
            I(gauges.GetValueOrDefault("outbox_gossip")), budget?.IsOverBudget == true ? "1" : "0",
            I(budget?.RefusedCount ?? 0), .. (_bootstrap?.SampleColumns() ?? ["", "", "", "", "", ""])
        ]);
        node.TimedLookups?.Flush();
        await samples.WriteLineAsync(row);
        await samples.FlushAsync();
        Console.WriteLine($"[{minutes,6:F1} min] channels {d.Channels} nodes {d.GraphNodes} (announced "
                        + $"{d.AnnouncedNodes}) policies {d.Policies} peers {connected.Count} ingress "
                        + $"{gauges.GetValueOrDefault("ingress")} orphans {gauges.GetValueOrDefault("orphans")} pending "
                        + $"{d.Ingress?.PendingAnnouncements ?? 0} relay {gauges.GetValueOrDefault("relay_pending")} "
                        + $"rss {Mb(process.WorkingSet64)} MB cpu {cpuPct:F0}%"
                        + (_rpc is null
                               ? ""
                               : $" verified {verified} lookups {node.FundingLookupCount} rpc {http?.Requests.Count} "
                               + $"bitcoind {chainNow?.Blocks}"));

        if (_bootstrap is not null)
            await _bootstrap.SampleAsync(connected, d.Sync?.HasCompletedInitialSync == true);
        if (_bootstrap is not null || _options.IsRelaying)
        {
            // The peers the bootstrap connected (and, relaying, the peers that connected to us) join peers.csv as they
            // appear; the probe never dials them itself
            foreach (var peer in connected.Where(p => !_peers.ContainsKey(p.NodeId.ToString())))
                _peers[peer.NodeId.ToString()] = new PeerRecord($"{peer.NodeId}@{peer.Host}:{peer.Port}")
                {
                    Connects = 1,
                    FirstConnectedAtMinutes = minutes,
                    Features = peer.Features.ToString(),
                    Discovered = true
                };
        }

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
        await SampleRelayAsync(node, meters, states, connected, minutes, process.WorkingSet64);
        await SampleSinkAsync(node, meters, d, connected.Count, minutes);
    }

    private const string RelayHeader =
        "utc,elapsed_min,relay_on,relay_pending,paused_connections,outbox_messages,outbox_bytes,relayed_total,"
      + "outbox_refused,relay_paused,relay_stalled,dropped_relay_stalled,dropped_relay_backlog_full,"
      + "gauge_paused_connections,gauge_outbox_bytes,peers_connected,peers_with_filter,rss_mb";

    private const string RelayPeerHeader =
        "utc,elapsed_min,peer,connected,peer_filter,sent_256,sent_258,sent_257,refused,echo_to_origin,queued_messages,"
      + "queued_bytes,max_messages,max_bytes,sent_messages";

    private const string SinkHeader =
        "utc,elapsed_min,connected,received_256,received_258,received_257,total,duplicates,update_before_announcement,"
      + "node_before_channel,older_updates,channels,announced_nodes,policies,pending_announcements,orphaned,rejected,"
      + "dropped,refused_at_door,proxy_forwarded_bytes,proxy_phase";

    /// <summary>NL-417 relayer: the relay's state and every peer's relay share and outbox depth.</summary>
    private async Task SampleRelayAsync(ProbeNode node, GossipMeterCollector meters,
                                        IReadOnlyList<Application.Gossip.Sync.GossipSyncPeerState> states,
                                        List<Domain.Node.Models.PeerModel> connected, double minutes, long rss)
    {
        if (_relayLog is null || _relayPeerLog is null || node.Traffic.Relay is not { } relay)
            return;

        var status = (node.Services.GetService<Application.Gossip.Relay.Interfaces.IGossipRelayScheduler>()
                          as Application.Gossip.Relay.GossipRelayScheduler)?.GetStatus();
        var gauges = meters.ReadGauges();
        var utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await _relayLog.WriteLineAsync(string.Join(',', [
            utc, F(minutes), B(status?.IsRelayingOthers), I(status?.PendingMessages ?? 0),
            I(status?.PausedConnections ?? 0), I(status?.OutboxMessages ?? 0), I(status?.OutboxBytes ?? 0),
            I(meters.Sum("nlightning.gossip.messages.relayed")), I(meters.Sum("nlightning.gossip.outbox.refused")),
            I(meters.Sum("nlightning.gossip.relay.paused")), I(meters.Sum("nlightning.gossip.relay.stalled")),
            I(meters.Counters.Where(c => c.Key.StartsWith("nlightning.gossip.messages.dropped", StringComparison.Ordinal)
                                      && c.Key.Contains("relay_stalled", StringComparison.Ordinal)).Sum(c => c.Value)),
            I(meters.Counters.Where(c => c.Key.StartsWith("nlightning.gossip.messages.dropped", StringComparison.Ordinal)
                                      && c.Key.Contains("relay_backlog_full", StringComparison.Ordinal))
                    .Sum(c => c.Value)),
            I(gauges.GetValueOrDefault("nlightning.gossip.relay.paused_connections")),
            I(gauges.GetValueOrDefault("nlightning.gossip.outbox.bytes")), I(connected.Count),
            I(states.Count(s => s.PeerFilter is not null)), Mb(rss)
        ]));
        await _relayLog.FlushAsync();

        foreach (var peer in node.RelaySender?.Peers ?? [])
        {
            var depth = node.RelaySender!.GetDepth(peer);
            var state = states.FirstOrDefault(s => s.PeerId == peer.NodeId);
            await _relayPeerLog.WriteLineAsync(string.Join(',', [
                utc, F(minutes), ProbeOptions.AliasOf(peer.NodeId.ToString()),
                connected.Any(p => p.NodeId == peer.NodeId) ? "1" : "0",
                state?.PeerFilter is { } pf ? $"{pf.FirstTimestamp}+{pf.TimestampRange}" : "",
                I(relay.Get(peer.NodeId, "channel_announcement")), I(relay.Get(peer.NodeId, "channel_update")),
                I(relay.Get(peer.NodeId, "node_announcement")),
                I(relay.Get(peer.NodeId, "channel_announcement.refused") + relay.Get(peer.NodeId, "channel_update.refused")
                + relay.Get(peer.NodeId, "node_announcement.refused")),
                I(relay.Get(peer.NodeId, "channel_announcement.echo_to_origin")
                + relay.Get(peer.NodeId, "channel_update.echo_to_origin")
                + relay.Get(peer.NodeId, "node_announcement.echo_to_origin")),
                I(depth?.QueuedMessages ?? 0), I(depth?.QueuedBytes ?? 0), I(depth?.MaxMessages ?? 0),
                I(depth?.MaxBytes ?? 0), I(depth?.SentMessages ?? 0)
            ]));
        }

        await _relayPeerLog.FlushAsync();
    }

    /// <summary>NL-417 sink: what arrived from the relayer, in which order, and the graph it built.</summary>
    private async Task SampleSinkAsync(ProbeNode node, GossipMeterCollector meters, GraphDescription d,
                                       int connected, double minutes)
    {
        if (_sinkLog is null || node.Traffic.Sink is not { } sink)
            return;

        var refused = node.Traffic.Snapshot().Where(t => t.Key.Kind.EndsWith(".refused_at_door", StringComparison.Ordinal))
                          .Sum(t => t.Value);
        await _sinkLog.WriteLineAsync(string.Join(',', [
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), F(minutes), I(connected),
            I(sink.Announcements), I(sink.Updates), I(sink.NodeAnnouncements), I(sink.Total), I(sink.Duplicates),
            I(sink.UpdatesBeforeAnnouncement), I(sink.NodesBeforeChannel), I(sink.OlderUpdates), I(d.Channels),
            I(d.AnnouncedNodes), I(d.Policies), I(d.Ingress?.PendingAnnouncements ?? 0),
            I(meters.Sum("nlightning.gossip.messages.orphaned")), I(meters.Sum("nlightning.gossip.messages.rejected")),
            I(meters.Sum("nlightning.gossip.messages.dropped")), I(refused), I(_proxy?.ForwardedToSink ?? 0),
            _proxy?.CurrentPhaseLabel ?? ""
        ]));
        await _sinkLog.FlushAsync();
        Console.WriteLine($"  sink: {sink.Total} received (256 {sink.Announcements}, 258 {sink.Updates}, 257 "
                        + $"{sink.NodeAnnouncements}), dup {sink.Duplicates}, 258-before-256 "
                        + $"{sink.UpdatesBeforeAnnouncement}, proxy {_proxy?.ForwardedToSink} B phase "
                        + $"{_proxy?.CurrentPhaseLabel}");
    }

    private async Task ConnectMissingPeersAsync(IPeerManager peerManager)
    {
        // A bootstrap run dials nothing itself: its peers are the bootstrap's
        if (_options.Bootstrap)
            return;

        var connected = peerManager.ListPeers().Select(p => p.NodeId.ToString()).ToHashSet(StringComparer.Ordinal);
        var tasks = _peers.Values.Where(p => !p.Discovered && !connected.Contains(p.NodeId)).Select(async record =>
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

    /// <summary>
    /// For the channels left without a policy: whether any channel_update for them arrived at all, how old the newest
    /// one was, and which peer announced them first.
    /// </summary>
    private static Dictionary<string, object> DiagnoseWithoutPolicy(IGraphStore store, PeerTraffic traffic)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var noUpdate = 0;
        var byAge = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byAnnouncer = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var channel in store.GetSnapshot().Channels.Where(c => c.Policy1 is null && c.Policy2 is null))
        {
            if (!traffic.Channels.TryGetValue(channel.ShortChannelId, out var seen))
            {
                byAnnouncer["(before this run)"] = byAnnouncer.GetValueOrDefault("(before this run)") + 1;
                continue;
            }

            byAnnouncer[seen.FirstAnnouncer ?? "(none)"] = byAnnouncer.GetValueOrDefault(seen.FirstAnnouncer ?? "(none)") + 1;
            if (seen.Updates == 0)
            {
                noUpdate++;
                continue;
            }

            var ageDays = (now - seen.NewestUpdateTimestamp) / 86_400.0;
            var bucket = ageDays switch
            {
                < 0 => "future",
                < 14 => "under 14 days",
                < 30 => "14-30 days",
                < 365 => "30-365 days",
                _ => "over a year"
            };
            byAge[bucket] = byAge.GetValueOrDefault(bucket) + 1;
        }

        return new Dictionary<string, object>
        {
            ["no_update_received"] = noUpdate,
            ["newest_update_age"] = byAge,
            ["first_announced_by"] = byAnnouncer
        };
    }

    /// <summary>bitcoind's getblockchaininfo now (one RPC per sample), or null when it cannot be read.</summary>
    private async Task<ChainInfo?> TryReadChainAsync()
    {
        if (_rpc is null)
            return null;
        try
        {
            return await ChainInfo.ReadAsync(_rpc);
        }
        catch (Exception e)
        {
            Console.WriteLine($"getblockchaininfo failed: {e.GetType().Name}: {e.Message}");
            return null;
        }
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

        /// <summary>Found connected (bootstrap, or an inbound peer of the relayer), never dialed by the probe.</summary>
        public bool Discovered { get; set; }
    }
}