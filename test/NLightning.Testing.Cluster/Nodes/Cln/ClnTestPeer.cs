using System.Globalization;

namespace NLightning.Testing.Cluster.Nodes.Cln;

using Kube;
using Topology;

/// <summary>
/// The <see cref="ILightningTestPeer"/> of a Core Lightning node, over <see cref="ClnRpc"/> (lightning-cli in the
/// pod). <see cref="Rpc"/> stays reachable for CLN-specific calls (splicing, offers, <c>dev-*</c>).
/// </summary>
public sealed class ClnTestPeer : ITopologyLightningNode
{
    /// <summary>How long <c>xpay</c> keeps retrying a payment.</summary>
    public const int PayRetrySeconds = 30;

    private string? _nodeId;

    /// <param name="node">The deployed CLN node.</param>
    /// <param name="p2pHost">
    /// The host peers dial: a <see cref="StableNodeAddress"/> name in a topology, the headless Service name when null.
    /// </param>
    /// <param name="rpc">The RPC client; one over <paramref name="node"/> when null.</param>
    public ClnTestPeer(INodeHandle node, string? p2pHost = null, ClnRpc? rpc = null)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        P2PHost = p2pHost ?? node.ServiceDnsName;
        Rpc = rpc ?? new ClnRpc(node);
    }

    /// <summary>The host other nodes dial.</summary>
    public string P2PHost { get; }

    public INodeHandle Node { get; }

    INodeHandle? ILightningTestPeer.Node => Node;

    public ClnRpc Rpc { get; }

    public NodeKind Kind => NodeKind.Cln;

    public string Alias => Node.Name;

    public async Task<string> GetNodeIdAsync(CancellationToken cancellationToken)
    {
        if (_nodeId is not null)
            return _nodeId;

        var info = await Rpc.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        return _nodeId = info["id"]!.GetValue<string>().ToLowerInvariant();
    }

    /// <summary><see cref="P2PHost"/> and the p2p port.</summary>
    public async Task<TestPeerAddress> GetAddressAsync(CancellationToken cancellationToken) =>
        new(await GetNodeIdAsync(cancellationToken).ConfigureAwait(false), P2PHost, ClnNode.P2PPort);

    public async Task ConnectAsync(TestPeerAddress peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        await Rpc.CallAsync("connect", cancellationToken, ("id", peer.NodeId), ("host", peer.Host),
                            ("port", peer.Port))
                 .ConfigureAwait(false);
    }

    public async Task DisconnectAsync(string nodeId, CancellationToken cancellationToken) =>
        await Rpc.CallAsync("disconnect", cancellationToken, ("id", nodeId), ("force", true)).ConfigureAwait(false);

    public async Task<string> GetNewAddressAsync(CancellationToken cancellationToken)
    {
        var result = await Rpc.CallAsync("newaddr", cancellationToken, ("addresstype", "bech32"))
                              .ConfigureAwait(false);
        return result["bech32"]!.GetValue<string>();
    }

    public async Task<TestChannelOpen> OpenChannelAsync(TestOpenChannelRequest request,
                                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await Rpc.CallAsync("fundchannel", cancellationToken, BuildFundChannelParameters(request))
                              .ConfigureAwait(false);
        return new TestChannelOpen(result["txid"]!.GetValue<string>().ToLowerInvariant(),
                                   result["outnum"]?.GetValue<int>());
    }

    public async Task<IReadOnlyList<TestChannel>> ListChannelsAsync(CancellationToken cancellationToken) =>
        ClnJson.ToTestChannels(await Rpc.CallAsync("listpeerchannels", cancellationToken).ConfigureAwait(false));

    public async Task<TestInvoice> CreateInvoiceAsync(long? amountMsat, string description,
                                                      CancellationToken cancellationToken)
    {
        var label = $"nltg-{Guid.NewGuid():N}";
        var result = await Rpc.CallAsync("invoice", cancellationToken,
                                         ("amount_msat", amountMsat is { } msat
                                                             ? msat.ToString(CultureInfo.InvariantCulture)
                                                             : "any"),
                                         ("label", label), ("description", description))
                              .ConfigureAwait(false);
        return new TestInvoice(result["bolt11"]!.GetValue<string>(),
                               result["payment_hash"]!.GetValue<string>().ToLowerInvariant());
    }

    /// <summary>Pays with <c>xpay</c> (retrying for <see cref="PayRetrySeconds"/>); a CLN error is a failed result.</summary>
    public async Task<TestPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Rpc.CallAsync("xpay", cancellationToken, ("invstring", bolt11),
                                             ("retry_for", PayRetrySeconds))
                                  .ConfigureAwait(false);
            return ClnJson.ToPaymentResult(result);
        }
        catch (ClnRpcException e)
        {
            return new TestPaymentResult(false, null, e.Message);
        }
    }

    public async Task<long> GetBlockHeightAsync(CancellationToken cancellationToken) =>
        (await Rpc.GetInfoAsync(cancellationToken).ConfigureAwait(false))["blockheight"]!.GetValue<long>();

    public async Task<long> GetConfirmedBalanceSatAsync(CancellationToken cancellationToken) =>
        ClnJson.ConfirmedFundsSat(await Rpc.CallAsync("listfunds", cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// A crash of the node process only: <c>lightningd</c> gets SIGKILL, the container exits and the kubelet restarts
    /// it in the same pod (same IP, same PVC); returns once the restarted container is ready. Unlike
    /// <see cref="INodeHandle.KillAsync"/> (SIGTERM first, then a new pod), no shutdown code runs. Unlike
    /// <c>FaultInjector.CrashAsync</c>, it needs no shared process namespace (lightningd is not PID 1 in the image).
    /// </summary>
    public async Task CrashAsync(TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        if (Node is not KubeNodeHandle kube)
            throw new NotSupportedException($"{Node} is not a Kubernetes node");

        var before = await ReadRestartCountAsync(kube, cancellationToken).ConfigureAwait(false);
        var kill = await Node.ExecAsync(CrashCommand, cancellationToken).ConfigureAwait(false);
        kill.EnsureSuccess($"kill -9 lightningd in {Node}");
        await Poll.UntilDoneAsync(async ct =>
        {
            var count = await ReadRestartCountAsync(kube, ct).ConfigureAwait(false);
            return count > before ? null : $"restart count still {count}";
        }, readyTimeout, $"{Node}'s container restarted", cancellationToken).ConfigureAwait(false);
        await Node.WaitReadyAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>SIGKILL to the <c>lightningd</c> whose pid is in its pid file (the image has no <c>ps</c>).</summary>
    public static IReadOnlyList<string> CrashCommand { get; } =
    [
        "sh", "-c",
        $"for f in {ClnNode.DataPath}/lightningd-{ClnNode.Network}.pid "
      + $"{ClnNode.DataPath}/{ClnNode.Network}/lightningd-{ClnNode.Network}.pid; do "
      + "[ -f \"$f\" ] && kill -9 \"$(cat \"$f\")\" && exit 0; done; exit 1"
    ];

    public override string ToString() => $"cln {Node}";

    private static async Task<int> ReadRestartCountAsync(KubeNodeHandle node, CancellationToken cancellationToken)
    {
        var pod = await node.Client.TryReadPodAsync(node.Namespace, node.PodName, cancellationToken)
                            .ConfigureAwait(false);
        return pod?.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == node.ContainerName)?.RestartCount ?? 0;
    }

    /// <summary>The <c>fundchannel</c> parameters of an open request.</summary>
    public static (string Key, object Value)[] BuildFundChannelParameters(TestOpenChannelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<(string, object)> parameters =
        [
            ("id", request.RemoteNodeId), ("amount", request.CapacitySat), ("announce", request.Announce)
        ];
        if (request.PushMsat > 0)
            parameters.Add(("push_msat", request.PushMsat));
        return [.. parameters];
    }
}