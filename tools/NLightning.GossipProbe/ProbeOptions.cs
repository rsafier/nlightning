using System.Globalization;
using Microsoft.Extensions.Logging;

namespace NLightning.GossipProbe;

/// <summary>The probe's command line (see README.md).</summary>
public sealed class ProbeOptions
{
    /// <summary>
    /// Well-known reachable mainnet nodes (IPv4 clearnet), checked against mempool.space on 2026-09-26: Eclair (ACINQ),
    /// LND (bfx-lnd0, LNBiG Hub-1) and Core Lightning (Blockstream Store, noserver4u).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPeers =
    [
        "03864ef025fde8fb587d989186ce6a4a186895ee44a926bfc370e2c366597a3f8f@3.33.236.230:9735",
        "033d8656219478701227199cbd6f670335c8d408a92ae88b962c49d4dc0e83e025@34.65.85.39:9735",
        "034ea80f8b148c750463546bd999bf7321a0e6dfc60aaf84bd0400a2e8d376c0d5@213.174.156.79:9735",
        "02df5ffe895c778e10f7742a6c5b8a0cefbe9465df58b92fadeb883752c8107c8f@35.232.170.67:9735",
        "0380ef0209ff1b46c38a37cd40f613d1dae3eba481a909459d6c1434a0e56e5d8c@89.58.53.211:9735"
    ];

    /// <summary>Aliases of the default peers, for the logs.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownAliases = new Dictionary<string, string>
    {
        ["03864ef025fde8fb587d989186ce6a4a186895ee44a926bfc370e2c366597a3f8f"] = "ACINQ(eclair)",
        ["033d8656219478701227199cbd6f670335c8d408a92ae88b962c49d4dc0e83e025"] = "bfx-lnd0(lnd)",
        ["034ea80f8b148c750463546bd999bf7321a0e6dfc60aaf84bd0400a2e8d376c0d5"] = "LNBiG-Hub-1(lnd)",
        ["02df5ffe895c778e10f7742a6c5b8a0cefbe9465df58b92fadeb883752c8107c8f"] = "Blockstream-Store(cln)",
        ["0380ef0209ff1b46c38a37cd40f613d1dae3eba481a909459d6c1434a0e56e5d8c"] = "noserver4u(cln)"
    };

    public string Command { get; private set; } = "run";

    public string Directory { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nltg-gossip-probe");

    public List<string> Peers { get; } = [];
    public double MaxMinutes { get; private set; } = 120;
    public double MinMinutes { get; private set; } = 60;
    public double PlateauMinutes { get; private set; } = 15;
    public int SampleSeconds { get; private set; } = 60;
    public int? SyncPeers { get; private set; }
    public uint Tip { get; private set; }
    public int ListenPort { get; private set; } = 19735;
    public LogLevel LogLevel { get; private set; } = LogLevel.Information;
    public string? Label { get; private set; }
    public int VerifySample { get; private set; } = 300;
    public double VerifyRequestsPerSecond { get; private set; } = 2;
    public string EsploraUrl { get; private set; } = "https://mempool.space/api";

    /// <summary><c>stub</c> (no bitcoind, <c>Gossip:AssumeChannelValid</c>) or <c>rpc</c> (the owner's bitcoind, D3).</summary>
    public string Chain { get; private set; } = "stub";

    public string RpcEnvFile { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nltg-gossip-probe",
                     "mainnet-rpc.env");

    /// <summary><c>Gossip:ChainLookupConcurrency</c> (null: the product default).</summary>
    public int? ChainConcurrency { get; private set; }

    /// <summary><c>Gossip:ChainLookupsPerSecond</c> (null: the product default).</summary>
    public int? ChainRate { get; private set; }

    /// <summary><c>headers</c> (default: the range sync asks up to bitcoind's header height) or <c>blocks</c>.</summary>
    public string SyncTip { get; private set; } = "headers";

    public int BlockPollSeconds { get; private set; } = 15;
    public int MaxBlocksPerPoll { get; private set; } = 10;
    public bool IsRpc => Chain == "rpc";

    /// <summary>
    /// The relay run (D12): the node id (or a default peer's alias prefix, e.g. <c>ACINQ</c>) of the one peer that gets
    /// our relay of other nodes' gossip; null (the default) keeps the relay off.
    /// </summary>
    public string? RelayTo { get; private set; }

    /// <summary>
    /// The BOLT 10 run (NL-113): no configured peer; the product's <c>PeerBootstrapService</c> (the daemon's code path,
    /// <c>Node:Bootstrap</c> at its mainnet default) finds the peers through the DNS seeds, and the probe never dials.
    /// </summary>
    public bool Bootstrap { get; private set; }

    /// <summary>
    /// The NL-417 relayer (<c>--relay-to-all</c>, with <c>--chain rpc</c>): the relay of other nodes' gossip is on toward
    /// every connected peer (the product's peer directory, nothing filtered), as a node with <c>Gossip:RelayEnabled</c>
    /// on runs it; works with <c>--bootstrap</c> and with configured peers.
    /// </summary>
    public bool RelayToAll { get; private set; }

    /// <summary>
    /// The NL-417 sink (<c>--sink</c>): a gossip-only receiver of one peer (the relayer): the sync is off (no query, no
    /// filter of its own), relay off, and at each <c>init</c> the probe sends that peer
    /// <c>gossip_timestamp_filter(0, 0xFFFFFFFF)</c> (everything); the order of every received message is checked.
    /// </summary>
    public bool Sink { get; private set; }

    /// <summary>
    /// The sink's read throttle (<c>--read-phases</c>): comma-separated <c>seconds:bytes-per-second</c> phases, counted
    /// from the first connection through the probe's local TCP proxy; <c>0</c> stops reading, <c>max</c> is
    /// unthrottled; the last phase lasts until the end. Null: no proxy, the sink connects directly.
    /// </summary>
    public IReadOnlyList<ReadPhase>? ReadPhases { get; private set; }

    /// <summary>The sink's proxy listen port (<c>--proxy-port</c>, on 127.0.0.1).</summary>
    public int ProxyPort { get; private set; } = 19835;

    /// <summary>Extra configuration keys (<c>--set Key=Value</c>, repeatable), applied last.</summary>
    public Dictionary<string, string?> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the relay of other nodes' gossip is on (<c>--relay-to</c> or <c>--relay-to-all</c>).</summary>
    public bool IsRelaying => RelayTo is not null || RelayToAll;

    /// <summary>One phase of the sink's read throttle; <see cref="BytesPerSecond"/> null means unthrottled.</summary>
    public sealed record ReadPhase(double Seconds, long? BytesPerSecond);

    public static string Usage =>
        """
        nltg gossip probe (test harness; mainnet, gossip-only, no channels, no funds)

        Usage:
          GossipProbe run    [--dir <path>] [--peer <id@ip:port>]... [--max-minutes 120] [--min-minutes 60]
                             [--plateau-minutes 15] [--sample-seconds 60] [--sync-peers <n>] [--tip <height>]
                             [--listen-port 19735] [--log-level Information|Debug] [--label <text>]
                             [--chain stub|rpc] [--rpc-env <file>] [--chain-concurrency <n>] [--chain-rate <n/s>]
                             [--sync-tip headers|blocks] [--block-poll-seconds 15] [--max-blocks-per-poll 10]
                             [--relay-to <node id|alias>] [--relay-to-all] [--bootstrap] [--set <Key=Value>]...
                             [--sink] [--read-phases <sec:bytes/s,...>] [--proxy-port 19835]
          GossipProbe verify [--dir <path>] [--sample 300] [--rate 2] [--esplora https://mempool.space/api]
          GossipProbe chaininfo [--rpc-env <file>]

        run     connects to the peers (default: 5 well-known nodes), syncs the mainnet graph with
                Gossip:AssumeChannelValid and relay off, and samples it every --sample-seconds into
                <dir>/runs/<UTC time>/ until --min-minutes passed and the graph did not grow for --plateau-minutes,
                or --max-minutes. The graph stays in <dir>/probe.db, so a second run measures the reload.
                With --chain rpc the funding outputs are checked against the bitcoind of --rpc-env (the product's
                FundingOutputLookup, AssumeChannelValid off) and new blocks are followed for the spend detection.
                --relay-to (needs --chain rpc) turns the relay of other nodes' gossip on toward that one peer only.
                --bootstrap takes no peer: the node finds its peers through the BOLT 10 DNS seeds (the product's
                PeerBootstrapService, as the daemon starts it) and syncs from them; bootstrap-*.csv record it.
                --relay-to-all (needs --chain rpc) relays other nodes' gossip to every connected peer (NL-417 relayer).
                --sink (one --peer) syncs nothing itself and asks that peer for everything with
                gossip_timestamp_filter(0, 0xFFFFFFFF); --read-phases reads it through a throttled local proxy
                (e.g. 300:50000,150:0,0:max); sink.csv and proxy.csv record what arrived (NL-417 sink).
                --set Key=Value overrides one configuration key (e.g. Gossip:RelayStallTimeout=00:01:30).
        chaininfo  checks that this process reaches the bitcoind of --rpc-env (getblockchaininfo).
        verify  checks a random sample of the stored channels' funding outputs against an Esplora API (at most
                --rate requests per second; stops on HTTP 429).
        """;

    public static ProbeOptions? Parse(string[] args)
    {
        var options = new ProbeOptions();
        var i = 0;
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            options.Command = args[i++];
        if (options.Command is not ("run" or "verify" or "chaininfo"))
            return null;

        for (; i < args.Length; i++)
        {
            var name = args[i];
            if (name == "--bootstrap")
            {
                options.Bootstrap = true;
                continue;
            }

            if (name == "--relay-to-all")
            {
                options.RelayToAll = true;
                continue;
            }

            if (name == "--sink")
            {
                options.Sink = true;
                continue;
            }

            if (i + 1 >= args.Length)
                return null;

            var value = args[++i];
            switch (name)
            {
                case "--dir": options.Directory = Path.GetFullPath(value); break;
                case "--peer": options.Peers.Add(value); break;
                case "--max-minutes": options.MaxMinutes = ParseDouble(value); break;
                case "--min-minutes": options.MinMinutes = ParseDouble(value); break;
                case "--plateau-minutes": options.PlateauMinutes = ParseDouble(value); break;
                case "--sample-seconds": options.SampleSeconds = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--sync-peers": options.SyncPeers = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--tip": options.Tip = uint.Parse(value, CultureInfo.InvariantCulture); break;
                case "--listen-port": options.ListenPort = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--log-level": options.LogLevel = Enum.Parse<LogLevel>(value, ignoreCase: true); break;
                case "--label": options.Label = value; break;
                case "--sample": options.VerifySample = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--rate": options.VerifyRequestsPerSecond = ParseDouble(value); break;
                case "--esplora": options.EsploraUrl = value.TrimEnd('/'); break;
                case "--chain" when value is "stub" or "rpc": options.Chain = value; break;
                case "--rpc-env": options.RpcEnvFile = Path.GetFullPath(value); break;
                case "--chain-concurrency":
                    options.ChainConcurrency = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--chain-rate": options.ChainRate = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--sync-tip" when value is "headers" or "blocks": options.SyncTip = value; break;
                case "--block-poll-seconds":
                    options.BlockPollSeconds = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--max-blocks-per-poll":
                    options.MaxBlocksPerPoll = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--relay-to": options.RelayTo = ResolvePeer(value); break;
                case "--set" when value.IndexOf('=') > 0:
                    options.Overrides[value[..value.IndexOf('=')]] = value[(value.IndexOf('=') + 1)..];
                    break;
                case "--read-phases": options.ReadPhases = ParsePhases(value); break;
                case "--proxy-port": options.ProxyPort = int.Parse(value, CultureInfo.InvariantCulture); break;
                default: return null;
            }
        }

        if (options.RelayTo is not null && options.RelayToAll)
            return null;
        if (options.Sink)
            // The sink listens to exactly one peer, never relays and never bootstraps
            return options.Peers.Count == 1 && !options.IsRelaying && !options.Bootstrap ? options : null;
        if (options.ReadPhases is not null)
            return null;

        if (options.Bootstrap)
            return options.Peers.Count == 0 && options.RelayTo is null ? options : null;

        if (options.Peers.Count == 0)
            options.Peers.AddRange(DefaultPeers);
        return options;
    }

    public static string AliasOf(string nodeIdHex) =>
        KnownAliases.TryGetValue(nodeIdHex, out var alias) ? alias : nodeIdHex[..16];

    private static string ResolvePeer(string value)
    {
        var match = KnownAliases.FirstOrDefault(
            a => a.Value.StartsWith(value, StringComparison.OrdinalIgnoreCase));
        return match.Key ?? value.ToLowerInvariant();
    }

    private static List<ReadPhase> ParsePhases(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(phase =>
             {
                 var parts = phase.Split(':');
                 return new ReadPhase(ParseDouble(parts[0]),
                                      parts[1] == "max" ? null : long.Parse(parts[1], CultureInfo.InvariantCulture));
             })
             .ToList();

    private static double ParseDouble(string value) => double.Parse(value, CultureInfo.InvariantCulture);
}