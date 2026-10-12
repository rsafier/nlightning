using System.Globalization;
using k8s.Models;

namespace NLightning.Testing.Cluster.Diagnostics;

using Nodes;
using Nodes.BitcoinCore;
using Nodes.Cln;
using Nodes.Eclair;
using Nodes.Ldk;
using Nodes.Lnd;
using Run;

/// <summary>One read-only command a dump runs in a node's container, and the file its output goes to.</summary>
public sealed record NodeStateCommand(string FileName, IReadOnlyList<string> Command);

/// <summary>
/// The node-specific state a dump records, by the pod's <see cref="RunLabels.Kind"/> label: bitcoind
/// <c>getblockchaininfo</c>/<c>getpeerinfo</c>/<c>getmempoolinfo</c>, CLN <c>getinfo</c>/<c>listpeerchannels</c>/
/// <c>listfunds</c>, LND <c>getinfo</c>/<c>listchannels</c>/<c>pendingchannels</c>/<c>listpeers</c>, Eclair
/// <c>getinfo</c>/<c>channels</c>/<c>peers</c>/<c>onchainbalance</c> (its API, the password from the container's
/// environment), ldk-server <c>get-node-info</c>/<c>list-channels</c>/<c>list-peers</c>/<c>get-balances</c>. Only
/// commands that
/// print state: nothing reads a macaroon, <c>hsm_secret</c>, a seed or a TLS key, and the output is still masked
/// (<see cref="SecretRedactor"/>).
/// </summary>
public static class NodeStateCommands
{
    /// <summary>The container a node's commands run in: the one named by its <see cref="RunLabels.Node"/> label (the
    /// container name of a <c>NodeWorkload</c>), else the pod's first container.</summary>
    public static string? ContainerOf(V1Pod pod)
    {
        ArgumentNullException.ThrowIfNull(pod);
        var containers = pod.Spec?.Containers ?? [];
        if (pod.Metadata?.Labels?.TryGetValue(RunLabels.Node, out var node) == true
         && containers.Any(c => c.Name == node))
            return node;

        return containers.FirstOrDefault()?.Name;
    }

    /// <summary>The kind from the pod's <see cref="RunLabels.Kind"/> label, or <see cref="NodeKind.Other"/>.</summary>
    public static NodeKind KindOf(V1Pod pod)
    {
        ArgumentNullException.ThrowIfNull(pod);
        if (pod.Metadata?.Labels?.TryGetValue(RunLabels.Kind, out var value) != true)
            return NodeKind.Other;

        return Enum.GetValues<NodeKind>().FirstOrDefault(k => RunLabels.KindValue(k) == value, NodeKind.Other);
    }

    /// <summary>The commands for <paramref name="pod"/> (none for a kind without state commands).</summary>
    public static IReadOnlyList<NodeStateCommand> For(V1Pod pod)
    {
        ArgumentNullException.ThrowIfNull(pod);
        var container = ContainerOf(pod);
        var spec = pod.Spec?.Containers?.FirstOrDefault(c => c.Name == container);
        return KindOf(pod) switch
        {
            NodeKind.BitcoinCore => BitcoinCore(spec),
            NodeKind.Cln => Cln(),
            NodeKind.Lnd => Lnd(),
            NodeKind.Eclair => Eclair(),
            NodeKind.Ldk => Ldk(),
            _ => []
        };
    }

    private static IReadOnlyList<NodeStateCommand> BitcoinCore(V1Container? container)
    {
        // The node's own RPC settings, from its command line (the password is not written: the command is not)
        var args = (container?.Command ?? []).Concat(container?.Args ?? []).ToList();
        string? Option(string name) =>
            args.LastOrDefault(a => a.StartsWith($"-{name}=", StringComparison.Ordinal))?[(name.Length + 2)..];

        var chain = args.Contains("-regtest") ? "regtest" : Option("chain") ?? "regtest";
        var port = Option("rpcport") ?? BitcoinCorePorts.Rpc.ToString(CultureInfo.InvariantCulture);
        var cli = new List<string> { "bitcoin-cli", $"-chain={chain}", $"-rpcport={port}", "-rpcclienttimeout=15" };
        if (Option("rpcuser") is { } user)
            cli.Add($"-rpcuser={user}");
        if (Option("rpcpassword") is { } password)
            cli.Add($"-rpcpassword={password}");

        return
        [
            new NodeStateCommand("getblockchaininfo.json", [.. cli, "getblockchaininfo"]),
            new NodeStateCommand("getpeerinfo.json", [.. cli, "getpeerinfo"]),
            new NodeStateCommand("getmempoolinfo.json", [.. cli, "getmempoolinfo"])
        ];
    }

    private static IReadOnlyList<NodeStateCommand> Cln()
    {
        string[] cli = ["lightning-cli", $"--network={ClnNode.Network}", "--notifications=none"];
        return
        [
            new NodeStateCommand("getinfo.json", [.. cli, "getinfo"]),
            new NodeStateCommand("listpeerchannels.json", [.. cli, "listpeerchannels"]),
            new NodeStateCommand("listfunds.json", [.. cli, "listfunds"])
        ];
    }

    private static IReadOnlyList<NodeStateCommand> Eclair()
    {
        static NodeStateCommand Call(string method) =>
            new($"{method}.json",
                [
                    "sh", "-c",
                    $"curl -s --max-time 15 -u \":${EclairNode.ApiPasswordVariable}\" -d '' "
                  + $"http://127.0.0.1:{EclairNode.ApiPort.ToString(CultureInfo.InvariantCulture)}/{method}"
                ]);

        return [Call("getinfo"), Call("channels"), Call("peers"), Call("onchainbalance")];
    }

    private static IReadOnlyList<NodeStateCommand> Ldk() =>
    [
        new NodeStateCommand("get-node-info.json", LdkNode.CliCommand("get-node-info")),
        new NodeStateCommand("list-channels.json", LdkNode.CliCommand("list-channels")),
        new NodeStateCommand("list-peers.json", LdkNode.CliCommand("list-peers")),
        new NodeStateCommand("get-balances.json", LdkNode.CliCommand("get-balances"))
    ];

    private static IReadOnlyList<NodeStateCommand> Lnd()
    {
        string[] cli =
        [
            "lncli", $"--lnddir={LndWorkload.LndDir}", "--network=regtest",
            $"--rpcserver=localhost:{LndWorkload.GrpcPort.ToString(CultureInfo.InvariantCulture)}"
        ];
        return
        [
            new NodeStateCommand("getinfo.json", [.. cli, "getinfo"]),
            new NodeStateCommand("listchannels.json", [.. cli, "listchannels"]),
            new NodeStateCommand("pendingchannels.json", [.. cli, "pendingchannels"]),
            new NodeStateCommand("listpeers.json", [.. cli, "listpeers"])
        ];
    }
}