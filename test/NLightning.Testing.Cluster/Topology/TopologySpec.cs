namespace NLightning.Testing.Cluster.Topology;

using Images;
using Kube;
using Nodes;

/// <summary>
/// A declarative topology (plan R8): the nodes, the wallets to fund and the channels to pre-open. Built with
/// <see cref="TopologyBuilder"/>, deployed into a run by <see cref="TopologyDeployer"/>.
/// </summary>
public sealed record TopologySpec(IReadOnlyList<TopologyNodeSpec> Nodes, IReadOnlyList<TopologyFundingSpec> Fundings,
                                  IReadOnlyList<TopologyChannelSpec> Channels)
{
    /// <summary>The chain node (the spike supports exactly one per topology).</summary>
    public TopologyNodeSpec ChainNode => Nodes.Single(n => n.Kind == NodeKind.BitcoinCore);

    /// <summary>The Lightning nodes, in declaration order.</summary>
    public IEnumerable<TopologyNodeSpec> LightningNodes => Nodes.Where(n => IsLightning(n.Kind));

    /// <summary>The node named <paramref name="name"/>.</summary>
    public TopologyNodeSpec GetNode(string name) =>
        Nodes.FirstOrDefault(n => n.Name == name)
     ?? throw new KeyNotFoundException($"The topology has no node named {name}");

    /// <summary>
    /// The problems of the spec (empty when it can be deployed): names, the single chain node, fundings and channels
    /// between Lightning nodes, and every channel's funder funded.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (!KubeNames.IsDns1123Label(node.Name) || node.Name.Length > KubeNames.MaxWorkloadNameLength)
                errors.Add($"node '{node.Name}': not a DNS-1123 label of at most {KubeNames.MaxWorkloadNameLength}"
                         + " characters");
            if (!names.Add(node.Name))
                errors.Add($"node '{node.Name}': declared twice");
            if (node.Kind != NodeKind.BitcoinCore && !IsLightning(node.Kind))
                errors.Add($"node '{node.Name}': kind {node.Kind} is not a chain or Lightning node");
        }

        var chains = Nodes.Count(n => n.Kind == NodeKind.BitcoinCore);
        if (chains != 1)
            errors.Add($"a topology needs exactly one {NodeKind.BitcoinCore} node, it has {chains}");

        var fundedSat = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var funding in Fundings)
        {
            if (!IsLightningNode(funding.Node))
                errors.Add($"funding of '{funding.Node}': not a Lightning node of the topology");
            if (funding.AmountSat <= 0)
                errors.Add($"funding of '{funding.Node}': amount {funding.AmountSat} sat is not positive");
            fundedSat[funding.Node] = fundedSat.GetValueOrDefault(funding.Node) + funding.AmountSat;
        }

        var committedSat = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var channel in Channels)
        {
            var what = $"channel {channel.From} -> {channel.To}";
            if (!IsLightningNode(channel.From))
                errors.Add($"{what}: '{channel.From}' is not a Lightning node of the topology");
            if (!IsLightningNode(channel.To))
                errors.Add($"{what}: '{channel.To}' is not a Lightning node of the topology");
            if (channel.From == channel.To)
                errors.Add($"{what}: a node cannot open a channel to itself");
            if (channel.CapacitySat <= 0)
                errors.Add($"{what}: capacity {channel.CapacitySat} sat is not positive");
            if (channel.PushMsat < 0 || channel.PushMsat > channel.CapacitySat * 1000)
                errors.Add($"{what}: push {channel.PushMsat} msat is outside 0..capacity");
            committedSat[channel.From] = committedSat.GetValueOrDefault(channel.From) + channel.CapacitySat;
        }

        foreach (var (funder, capacity) in committedSat)
        {
            var funded = fundedSat.GetValueOrDefault(funder);
            if (IsLightningNode(funder) && funded <= capacity)
                errors.Add($"node '{funder}' opens {capacity} sat of channels but its wallet is funded with only "
                         + $"{funded} sat (fees and reserves need more)");
        }

        return errors;
    }

    /// <summary>Throws <see cref="ArgumentException"/> with every problem of <see cref="Validate"/>.</summary>
    public TopologySpec EnsureValid()
    {
        var errors = Validate();
        return errors.Count == 0
                   ? this
                   : throw new ArgumentException("Invalid topology: " + string.Join("; ", errors));
    }

    /// <summary>Whether <paramref name="kind"/> is a Lightning implementation.</summary>
    public static bool IsLightning(NodeKind kind) =>
        kind is NodeKind.Lnd or NodeKind.Cln or NodeKind.Eclair or NodeKind.Ldk or NodeKind.NLightning;

    private bool IsLightningNode(string name) => Nodes.Any(n => n.Name == name && IsLightning(n.Kind));
}

/// <summary>One node: its alias, its implementation, an image override and extra flags.</summary>
public sealed record TopologyNodeSpec(string Name, NodeKind Kind, ImageRef? Image = null,
                                      IReadOnlyList<string>? ExtraArgs = null)
{
    public IReadOnlyList<string> Args => ExtraArgs ?? [];
}

/// <summary>On-chain funds sent from the chain node's wallet to a Lightning node's wallet before the channels open.</summary>
public sealed record TopologyFundingSpec(string Node, long AmountSat);

/// <summary>A channel <paramref name="From"/> opens (and funds) to <paramref name="To"/> before the tests start.</summary>
public sealed record TopologyChannelSpec(string From, string To, long CapacitySat, long PushMsat = 0,
                                         bool Announce = false);