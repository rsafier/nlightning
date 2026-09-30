using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.GossipProbe;

using Application.Gossip.Graph.Interfaces;
using Domain.Node.Bootstrap;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;

/// <summary>
/// The BOLT 10 run (<c>--bootstrap</c>, NL-113): starts the product's <see cref="IPeerBootstrapService"/> as the daemon
/// does (after <c>PeerManager.StartAsync</c>) and records what it did: <c>bootstrap-seeds.csv</c> (every seed query),
/// <c>bootstrap-dials.csv</c> (every dial), <c>bootstrap-runs.csv</c> (every run) and <c>bootstrap-peers.csv</c> (every
/// peer seen connected, with its init features and, once gossip has it, its alias), rewritten at every sample, and the
/// <c>bootstrap</c> section of <c>summary.json</c>.
/// </summary>
public sealed class BootstrapProbe
{
    private readonly IPeerBootstrapService _service;
    private readonly IDnsSeedClient _seedClient;
    private readonly NodeOptions _nodeOptions;
    private readonly IGraphStore _store;
    private readonly List<object> _census = [];
    private readonly Stopwatch _clock;
    private readonly string _runDirectory;
    private readonly Dictionary<string, SeenPeer> _seen = new(StringComparer.Ordinal);
    private DateTimeOffset _startedAt;
    private double? _firstPeerMinutes;
    private double? _thirdPeerMinutes;
    private double? _syncCompleteMinutes;
    private int _maxConnected;

    public BootstrapProbe(IServiceProvider services, Stopwatch clock, string runDirectory)
    {
        _service = services.GetRequiredService<IPeerBootstrapService>();
        _seedClient = services.GetRequiredService<IDnsSeedClient>();
        _nodeOptions = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NodeOptions>>().Value;
        _store = services.GetRequiredService<IGraphStore>();
        _clock = clock;
        _runDirectory = runDirectory;
    }

    /// <summary>Starts the bootstrap (the daemon's call, after the peer manager started).</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startedAt = DateTimeOffset.UtcNow;
        Console.WriteLine($"BOLT 10 bootstrap started (enabled: {_service.GetStatus().Enabled})");
        return _service.StartAsync(cancellationToken);
    }

    public Task StopAsync() => _service.StopAsync(CancellationToken.None);

    /// <summary>
    /// After the run: every seed of the network asked once more through the product's seed client (system resolver,
    /// then the fallback), so the record says how each seed answers even when the bootstrap needed only one. Not part
    /// of the discovery (the bootstrap has stopped by then).
    /// </summary>
    public async Task CensusAsync(CancellationToken cancellationToken)
    {
        var bootstrap = _nodeOptions.Bootstrap;
        foreach (var seed in bootstrap.GetEffectiveSeeds(_nodeOptions.BitcoinNetwork, out _))
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var result = await _seedClient.QuerySeedAsync(seed, bootstrap.AddressFamilies, bootstrap.MaxPerSeed,
                                                              cancellationToken);
                _census.Add(new
                {
                    Seed = seed,
                    Outcome = result.Outcome.ToString(),
                    Candidates = result.Candidates.Count,
                    IPv4 = result.Candidates.Count(c => c.Address.AddressFamily
                                                     == System.Net.Sockets.AddressFamily.InterNetwork),
                    IPv6 = result.Candidates.Count(c => c.Address.AddressFamily
                                                     == System.Net.Sockets.AddressFamily.InterNetworkV6),
                    Ports = result.Candidates.Select(c => c.Port).Distinct().Order().ToList(),
                    result.Rejected,
                    result.UsedFallbackResolver,
                    SystemResolverOutcome = result.SystemResolverOutcome?.ToString(),
                    ElapsedSeconds = watch.Elapsed.TotalSeconds
                });
                Console.WriteLine($"Seed census {seed}: {result.Outcome}, {result.Candidates.Count} candidates, "
                                + $"fallback {result.UsedFallbackResolver} (system {result.SystemResolverOutcome})");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _census.Add(new { Seed = seed, Error = $"{e.GetType().Name}: {e.Message}" });
            }
        }
    }

    /// <summary>The sample's bootstrap columns: seed queries, candidates, dials, connected, failed, timed out.</summary>
    public string[] SampleColumns()
    {
        var status = _service.GetStatus();
        return
        [
            I(status.SeedQueries.Count), I(status.SeedQueries.Sum(q => q.Candidates)), I(status.Dials.Count),
            I(status.Dials.Count(d => d.Outcome == BootstrapDialOutcome.Connected)),
            I(status.Dials.Count(d => d.Outcome == BootstrapDialOutcome.Failed)),
            I(status.Dials.Count(d => d.Outcome == BootstrapDialOutcome.TimedOut))
        ];
    }

    /// <summary>Notes the connected peers and the sync state, then rewrites the bootstrap CSVs.</summary>
    public async Task SampleAsync(IReadOnlyList<PeerModel> connected, bool syncComplete)
    {
        var minutes = _clock.Elapsed.TotalMinutes;
        foreach (var peer in connected)
        {
            var id = peer.NodeId.ToString();
            if (!_seen.TryGetValue(id, out var seen))
            {
                seen = new SeenPeer(id, $"{peer.Host}:{peer.Port}", minutes);
                _seen[id] = seen;
            }

            seen.LastSeenMinutes = minutes;
            seen.FeatureBits ??= string.Join(' ', peer.Features.GetSetBits());
        }

        _maxConnected = Math.Max(_maxConnected, connected.Count);
        if (syncComplete)
            _syncCompleteMinutes ??= minutes;

        var status = _service.GetStatus();
        var connects = status.Dials.Where(d => d.Outcome == BootstrapDialOutcome.Connected).OrderBy(d => d.At).ToList();
        if (connects.Count > 0)
            _firstPeerMinutes ??= (connects[0].At - _startedAt).TotalMinutes;
        if (connects.Count > 2)
            _thirdPeerMinutes ??= (connects[2].At - _startedAt).TotalMinutes;

        await WriteCsvsAsync(status);
    }

    /// <summary>The <c>bootstrap</c> section of the summary.</summary>
    public Dictionary<string, object?> Summarize()
    {
        var status = _service.GetStatus();
        foreach (var seen in _seen.Values)
        {
            seen.Alias = AliasOf(seen.NodeId);
            seen.AnnouncementFeatureBits = AnnouncementBitsOf(seen.NodeId);
            seen.Implementation = GuessImplementation(seen);
        }

        return new Dictionary<string, object?>
        {
            ["enabled"] = status.Enabled,
            ["started_utc"] = _startedAt,
            ["loop_started_utc"] = status.StartedAt,
            ["loop_finished_utc"] = status.FinishedAt,
            ["end_reason"] = status.EndReason,
            ["runs"] = status.Runs,
            ["seed_queries"] = status.SeedQueries.Select(q => new
            {
                q.Run,
                q.Seed,
                Outcome = q.Outcome.ToString(),
                q.Candidates,
                q.Rejected,
                q.UsedFallbackResolver,
                SystemResolverOutcome = q.SystemResolverOutcome?.ToString(),
                ElapsedSeconds = q.Elapsed.TotalSeconds,
                q.Error
            }),
            ["dials_by_outcome"] = status.Dials.GroupBy(d => d.Outcome.ToString())
                                         .ToDictionary(g => g.Key, g => g.Count()),
            ["dials_by_seed"] = status.Dials.GroupBy(d => d.Candidate.Seed)
                                      .ToDictionary(g => g.Key,
                                                    g => g.GroupBy(d => d.Outcome.ToString())
                                                          .ToDictionary(o => o.Key, o => o.Count())),
            ["dials_by_family"] = status.Dials.GroupBy(d => Family(d.Candidate))
                                        .ToDictionary(g => g.Key,
                                                      g => g.GroupBy(d => d.Outcome.ToString())
                                                            .ToDictionary(o => o.Key, o => o.Count())),
            ["dial_ports"] = status.Dials.GroupBy(d => d.Candidate.Port)
                                   .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count()),
            ["minutes_to_first_peer"] = _firstPeerMinutes,
            ["minutes_to_third_peer"] = _thirdPeerMinutes,
            ["minutes_to_sync_complete"] = _syncCompleteMinutes,
            ["max_peers_connected"] = _maxConnected,
            ["seed_census_after_run"] = _census,
            ["peers_seen"] = _seen.Values.OrderBy(p => p.FirstSeenMinutes).ToList()
        };
    }

    private string AliasOf(string nodeIdHex) => NodeOf(nodeIdHex)?.AliasText ?? "";

    /// <summary>The feature bits of the peer's node_announcement (the full bitmap), empty before gossip has it.</summary>
    private string AnnouncementBitsOf(string nodeIdHex)
    {
        if (NodeOf(nodeIdHex) is not { } node)
            return "";

        var bytes = node.Features.Span;
        var bits = new List<int>();
        for (var i = 0; i < bytes.Length * 8; i++)
            if ((bytes[bytes.Length - 1 - i / 8] & (1 << (i % 8))) != 0)
                bits.Add(i);
        return string.Join(' ', bits);
    }

    private Domain.Gossip.Graph.GraphNode? NodeOf(string nodeIdHex)
    {
        var id = new Domain.Crypto.ValueObjects.CompactPubKey(Convert.FromHexString(nodeIdHex));
        return _store.TryGetNode(id, out var node) ? node : null;
    }

    /// <summary>
    /// LND is identifiable: its node_announcement sets bit 2023 (script_enforced_lease) and its default alias is the
    /// first 20 hex digits of the node id. Other implementations are not told apart here.
    /// </summary>
    private static string GuessImplementation(SeenPeer peer)
    {
        var bits = (peer.AnnouncementFeatureBits ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (bits.Contains("2023") || bits.Contains("2022"))
            return "lnd (bit 2023)";
        if (peer.Alias is { Length: 20 } alias && peer.NodeId.StartsWith(alias, StringComparison.Ordinal))
            return "lnd (default alias)";
        return peer.AnnouncementFeatureBits is { Length: > 0 } ? "not lnd" : "unknown (no node_announcement)";
    }

    private async Task WriteCsvsAsync(PeerBootstrapStatus status)
    {
        var seeds = new List<string>
        {
            "utc,run,seed,outcome,candidates,rejected,used_fallback_resolver,system_resolver_outcome,elapsed_s,error"
        };
        seeds.AddRange(status.SeedQueries.Select(q => string.Join(',', [
            q.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), I(q.Run), q.Seed, q.Outcome.ToString(),
            I(q.Candidates), I(q.Rejected), q.UsedFallbackResolver ? "1" : "0",
            q.SystemResolverOutcome?.ToString() ?? "", F(q.Elapsed.TotalSeconds), Csv(q.Error)
        ])));
        var dials = new List<string> { "utc,run,seed,node_id,address,port,family,outcome,elapsed_s,error" };
        dials.AddRange(status.Dials.Select(d => string.Join(',', [
            d.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), I(d.Run), d.Candidate.Seed,
            d.Candidate.NodeId.ToString(), d.Candidate.Endpoint.Host, I(d.Candidate.Port),
            Family(d.Candidate), d.Outcome.ToString(), F(d.Elapsed.TotalSeconds),
            Csv(d.Error)
        ])));
        var runs = new List<string> { "utc,run,skip_reason,collected,selected,attempted,connected,peers_after" };
        runs.AddRange(status.Runs.Select(r => string.Join(',', [
            r.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), I(r.Run), Csv(r.SkipReason),
            I(r.Collected), I(r.Selected), I(r.Attempted), I(r.Connected), I(r.PeersAfter)
        ])));
        var peers = new List<string>
            { "node_id,address,first_seen_min,last_seen_min,alias,init_feature_bits,announcement_feature_bits" };
        peers.AddRange(_seen.Values.Select(p => string.Join(',', [
            p.NodeId, p.Address, F(p.FirstSeenMinutes), F(p.LastSeenMinutes), Csv(AliasOf(p.NodeId)),
            Csv(p.FeatureBits), Csv(AnnouncementBitsOf(p.NodeId))
        ])));

        await File.WriteAllLinesAsync(Path.Combine(_runDirectory, "bootstrap-seeds.csv"), seeds);
        await File.WriteAllLinesAsync(Path.Combine(_runDirectory, "bootstrap-dials.csv"), dials);
        await File.WriteAllLinesAsync(Path.Combine(_runDirectory, "bootstrap-runs.csv"), runs);
        await File.WriteAllLinesAsync(Path.Combine(_runDirectory, "bootstrap-peers.csv"), peers);
    }

    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);

    // An onion candidate (graph top-up with Node:Tor on) has no IP address
    private static string Family(SeedPeerCandidate candidate) =>
        candidate.OnionHost is null ? candidate.Address.AddressFamily.ToString() : "TorV3";
    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Csv(string? value) =>
        value is null ? "" : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>A peer seen connected during the run.</summary>
    public sealed class SeenPeer(string nodeId, string address, double firstSeenMinutes)
    {
        public string NodeId { get; } = nodeId;
        public string Address { get; } = address;
        public double FirstSeenMinutes { get; } = firstSeenMinutes;
        public double LastSeenMinutes { get; set; }
        public string? FeatureBits { get; set; }
        public string? Alias { get; set; }
        public string? AnnouncementFeatureBits { get; set; }
        public string? Implementation { get; set; }
    }
}