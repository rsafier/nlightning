namespace NLightning.Testing.Cluster.Topology.Lnd;

using Kube;

/// <summary>
/// The LND regtest network as data: the LND nodes (alias and extra flags), the channels they open to each other at
/// start (funder, peer, capacity, push and the funder's routing policy) and how their wallets are funded.
/// <see cref="Default"/> is the network the Docker suites' <c>LightningRegtestNetworkFixture</c> builds with
/// LNUnit (test harness phase 3); <see cref="LndRegtestNetwork"/> deploys it.
/// </summary>
/// <param name="ChainName">The bitcoind's alias (Service name), <c>miner</c> as in the Docker fixture.</param>
/// <param name="WalletUtxoSat">The amount of each wallet output a node gets before the opens.</param>
/// <param name="WalletUtxoCount">
/// How many such outputs each node gets: one per open it funds at most, since the opens are confirmed together and an
/// open spends confirmed outputs only.
/// </param>
/// <param name="FundingSatPerVbyte">The fee rate of the funding transactions (LNUnit's 10 sat/vB).</param>
/// <param name="AnnounceChannels">Whether the startup channels are public (LNUnit's opens are).</param>
/// <param name="MinerReserveBlocks">
/// Blocks mined to the miner's wallet (and then matured with 100 blocks to the burn address) before the fundings, so
/// the wallet can fund every node and still has coins for the tests: LNUnit's miner had matured about 100 coinbases.
/// </param>
public sealed record LndRegtestNetworkSpec(IReadOnlyList<LndRegtestNodeSpec> Nodes,
                                           IReadOnlyList<LndRegtestChannelSpec> Channels,
                                           string ChainName = "miner",
                                           long WalletUtxoSat = LndRegtestNetworkSpec.LnUnitWalletUtxoSat,
                                           int WalletUtxoCount = 2,
                                           ulong FundingSatPerVbyte = 10,
                                           bool AnnounceChannels = true,
                                           int MinerReserveBlocks = 30)
{
    /// <summary>What LNUnit sends each LND node, twice: 42.69 BTC.</summary>
    public const long LnUnitWalletUtxoSat = 4_269_000_000;

    /// <summary>The capacity of every startup channel of the Docker fixture (10,000,000 sat).</summary>
    public const long DefaultCapacitySat = 10_000_000;

    /// <summary>The push of the channels that push (1,000,000 sat).</summary>
    public const long DefaultPushSat = 1_000_000;

    /// <summary>
    /// The Docker fixture's network: <c>alice</c> (with <c>--protocol.rbf-coop-close</c>, LND's
    /// <c>option_simple_close</c>, and <c>--accept-keysend</c>), <c>bob</c>, <c>carol</c> and <c>david</c> (no
    /// channels; the ABCD payee), with alice → bob (10M sat), bob → alice (10M, 1M pushed), carol → alice (10M, 1M
    /// pushed) and carol → bob (10M, 1M pushed), each funder's side at 0 msat base fee, 0 ppm and a CLTV delta of 40
    /// (LNUnit's channel defaults).
    /// </summary>
    public static LndRegtestNetworkSpec Default { get; } = new(
    [
        new LndRegtestNodeSpec("alice", ["--protocol.rbf-coop-close", "--accept-keysend"]),
        new LndRegtestNodeSpec("bob"),
        new LndRegtestNodeSpec("carol"),
        new LndRegtestNodeSpec("david")
    ],
    [
        new LndRegtestChannelSpec("alice", "bob", DefaultCapacitySat),
        new LndRegtestChannelSpec("bob", "alice", DefaultCapacitySat, DefaultPushSat),
        new LndRegtestChannelSpec("carol", "alice", DefaultCapacitySat, DefaultPushSat),
        new LndRegtestChannelSpec("carol", "bob", DefaultCapacitySat, DefaultPushSat)
    ]);

    /// <summary>The LND aliases, in declaration order.</summary>
    public IReadOnlyList<string> Aliases => Nodes.Select(n => n.Alias).ToList();

    /// <summary>The node <paramref name="alias"/>.</summary>
    public LndRegtestNodeSpec GetNode(string alias) =>
        Nodes.FirstOrDefault(n => n.Alias == alias)
     ?? throw new KeyNotFoundException($"The LND regtest network has no node named {alias}");

    /// <summary>
    /// The problems of the spec (empty when it can be deployed): DNS-1123 aliases, unique and not the chain's, channels
    /// between two different nodes of the spec, capacity and push, at most <see cref="WalletUtxoCount"/> opens per
    /// funder, each funder's outputs larger than any channel it opens, the miner's reserve.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        if (!KubeNames.IsDns1123Label(ChainName))
            errors.Add($"chain '{ChainName}': not a DNS-1123 label");
        foreach (var node in Nodes)
        {
            if (!KubeNames.IsDns1123Label(node.Alias) || node.Alias.Length > KubeNames.MaxWorkloadNameLength)
                errors.Add($"node '{node.Alias}': not a DNS-1123 label of at most {KubeNames.MaxWorkloadNameLength}"
                         + " characters");
            if (!aliases.Add(node.Alias))
                errors.Add($"node '{node.Alias}': declared twice");
            if (node.Alias == ChainName)
                errors.Add($"node '{node.Alias}': the chain node's name");
        }

        if (Nodes.Count == 0)
            errors.Add("no LND node");
        if (WalletUtxoSat <= 0)
            errors.Add($"wallet output of {WalletUtxoSat} sat is not positive");
        if (WalletUtxoCount < 0)
            errors.Add($"wallet output count {WalletUtxoCount} is negative");
        if (MinerReserveBlocks < 0)
            errors.Add($"miner reserve of {MinerReserveBlocks} blocks is negative");

        var opens = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var channel in Channels)
        {
            var what = $"channel {channel.From} -> {channel.To}";
            if (!aliases.Contains(channel.From))
                errors.Add($"{what}: '{channel.From}' is not a node of the network");
            if (!aliases.Contains(channel.To))
                errors.Add($"{what}: '{channel.To}' is not a node of the network");
            if (channel.From == channel.To)
                errors.Add($"{what}: a node cannot open a channel to itself");
            if (channel.CapacitySat <= 0)
                errors.Add($"{what}: capacity {channel.CapacitySat} sat is not positive");
            if (channel.PushSat < 0 || channel.PushSat >= channel.CapacitySat)
                errors.Add($"{what}: push {channel.PushSat} sat is outside 0..capacity");
            if (channel.CapacitySat >= WalletUtxoSat)
                errors.Add($"{what}: capacity {channel.CapacitySat} sat does not fit one wallet output of "
                         + $"{WalletUtxoSat} sat");
            opens[channel.From] = opens.GetValueOrDefault(channel.From) + 1;
        }

        foreach (var (funder, count) in opens.Where(o => o.Value > WalletUtxoCount))
            errors.Add($"node '{funder}' funds {count} channels but gets only {WalletUtxoCount} wallet outputs (the "
                     + "opens are confirmed together and each spends a confirmed output)");

        return errors;
    }

    /// <summary>Throws <see cref="ArgumentException"/> with every problem of <see cref="Validate"/>.</summary>
    public LndRegtestNetworkSpec EnsureValid()
    {
        var errors = Validate();
        return errors.Count == 0
                   ? this
                   : throw new ArgumentException("Invalid LND regtest network: " + string.Join("; ", errors));
    }
}

/// <summary>One LND node of the network: its alias and the flags it gets on top of the harness's LND defaults.</summary>
public sealed record LndRegtestNodeSpec(string Alias, IReadOnlyList<string>? ExtraArgs = null)
{
    public IReadOnlyList<string> Args => ExtraArgs ?? [];
}

/// <summary>
/// A channel <paramref name="From"/> opens to <paramref name="To"/> at start, with <paramref name="PushSat"/> pushed to
/// <paramref name="To"/>; <paramref name="FunderPolicy"/> is set on the funder's side once it confirmed (null: LND's
/// defaults stay).
/// </summary>
public sealed record LndRegtestChannelSpec(string From, string To, long CapacitySat, long PushSat = 0,
                                           LndChannelPolicy? FunderPolicy = null)
{
    /// <summary>The funder's policy, LNUnit's default (0 msat, 0 ppm, delta 40) when none is given.</summary>
    public LndChannelPolicy Policy => FunderPolicy ?? LndChannelPolicy.LnUnitDefault;
}

/// <summary>An LND channel policy (what <c>UpdateChannelPolicy</c> sets on one side).</summary>
public sealed record LndChannelPolicy(long BaseFeeMsat, uint FeeRatePpm, uint TimeLockDelta)
{
    /// <summary>LNUnit's <c>Channel</c> defaults: no fee and a CLTV delta of 40.</summary>
    public static LndChannelPolicy LnUnitDefault { get; } = new(0, 0, 40);
}