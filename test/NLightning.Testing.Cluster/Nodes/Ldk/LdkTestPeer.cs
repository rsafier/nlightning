using System.Globalization;

namespace NLightning.Testing.Cluster.Nodes.Ldk;

using Topology;

/// <summary>
/// The <see cref="ILightningTestPeer"/> of an ldk-server node, over <see cref="LdkRpc"/> (ldk-server-cli in the pod).
/// <see cref="Rpc"/> stays reachable for LDK-specific calls (splicing, offers, hold invoices, the graph).
/// </summary>
public sealed class LdkTestPeer : ITopologyLightningNode
{
    /// <summary>How long <c>pay --wait</c> waits for a payment's outcome.</summary>
    public const int PayTimeoutSeconds = 60;

    /// <summary>How long an open waits for LDK to list the funding outpoint.</summary>
    private static readonly TimeSpan s_fundingTimeout = TimeSpan.FromSeconds(60);

    private string? _nodeId;

    /// <param name="node">The deployed ldk-server node.</param>
    /// <param name="p2pHost">
    /// The host peers dial: the node's stable ClusterIP in a topology (<see cref="StableNodeAddress"/>), the headless
    /// Service name when null.
    /// </param>
    /// <param name="rpc">The CLI client; one over <paramref name="node"/> when null.</param>
    public LdkTestPeer(INodeHandle node, string? p2pHost = null, LdkRpc? rpc = null)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        P2PHost = p2pHost ?? node.ServiceDnsName;
        Rpc = rpc ?? new LdkRpc(node);
    }

    /// <summary>The host other nodes (and the test process) dial.</summary>
    public string P2PHost { get; }

    public INodeHandle Node { get; }

    INodeHandle? ILightningTestPeer.Node => Node;

    public LdkRpc Rpc { get; }

    public NodeKind Kind => NodeKind.Ldk;

    public string Alias => Node.Name;

    public async Task<string> GetNodeIdAsync(CancellationToken cancellationToken)
    {
        if (_nodeId is not null)
            return _nodeId;

        var info = await Rpc.GetNodeInfoAsync(cancellationToken).ConfigureAwait(false);
        return _nodeId = info["node_id"]!.GetValue<string>().ToLowerInvariant();
    }

    /// <summary><see cref="P2PHost"/> and the p2p port.</summary>
    public async Task<TestPeerAddress> GetAddressAsync(CancellationToken cancellationToken) =>
        new(await GetNodeIdAsync(cancellationToken).ConfigureAwait(false), P2PHost, LdkNode.P2PPort);

    /// <summary><c>connect-peer id@host:port --persist</c>, then until <c>list-peers</c> shows it connected.</summary>
    public async Task ConnectAsync(TestPeerAddress peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        await Rpc.RunAsync("connect-peer", cancellationToken, peer.ToString(), "--persist").ConfigureAwait(false);
        await Poll.UntilAsync(async ct => LdkJson.IsConnected(await Rpc.RunAsync("list-peers", ct)
                                                                       .ConfigureAwait(false), peer.NodeId),
                              TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250),
                              $"{Alias} connected to {peer.NodeId}", cancellationToken)
                  .ConfigureAwait(false);
    }

    public async Task DisconnectAsync(string nodeId, CancellationToken cancellationToken) =>
        await Rpc.RunAsync("disconnect-peer", cancellationToken, nodeId).ConfigureAwait(false);

    public async Task<string> GetNewAddressAsync(CancellationToken cancellationToken) =>
        (await Rpc.RunAsync("onchain-receive", cancellationToken).ConfigureAwait(false))["address"]!
       .GetValue<string>();

    /// <summary>
    /// <c>open-channel</c> to the connected peer at the address <c>list-peers</c> has for it, then until LDK lists the
    /// funding outpoint (LDK answers with its <c>user_channel_id</c> before the funding exists).
    /// </summary>
    public async Task<TestChannelOpen> OpenChannelAsync(TestOpenChannelRequest request,
                                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var peer = LdkJson.FindPeer(await Rpc.RunAsync("list-peers", cancellationToken).ConfigureAwait(false),
                                    request.RemoteNodeId)
                ?? throw new InvalidOperationException($"{Alias} is not connected to {request.RemoteNodeId}");
        var address = peer["address"]?.GetValue<string>()
                   ?? throw new InvalidOperationException($"{Alias} has no address of {request.RemoteNodeId}");
        var opened = await Rpc.RunAsync("open-channel", cancellationToken,
                                        [.. BuildOpenChannelArgs(request, address)])
                              .ConfigureAwait(false);
        var userChannelId = opened["user_channel_id"]!.GetValue<string>();
        TestChannelOpen? open = null;
        await Poll.UntilAsync(async ct =>
        {
            var channels = (await Rpc.RunAsync("list-channels", ct).ConfigureAwait(false))["channels"]?.AsArray() ?? [];
            var channel = channels.FirstOrDefault(c => c?["user_channel_id"]?.GetValue<string>() == userChannelId);
            if (channel?["funding_txo"] is not { } funding)
                return false;

            open = new TestChannelOpen(funding["txid"]!.GetValue<string>().ToLowerInvariant(),
                                       funding["vout"] is { } vout ? (int)LdkJson.ReadLong(vout) : null);
            return true;
        }, s_fundingTimeout, TimeSpan.FromMilliseconds(250), $"{Alias}'s channel {userChannelId} funded",
                              cancellationToken)
                  .ConfigureAwait(false);
        return open!;
    }

    public async Task<IReadOnlyList<TestChannel>> ListChannelsAsync(CancellationToken cancellationToken) =>
        LdkJson.ToTestChannels(await Rpc.RunAsync("list-channels", cancellationToken).ConfigureAwait(false));

    public async Task<TestInvoice> CreateInvoiceAsync(long? amountMsat, string description,
                                                      CancellationToken cancellationToken)
    {
        string[] args = amountMsat is { } msat
                            ? [$"{msat.ToString(CultureInfo.InvariantCulture)}msat", "-d", description]
                            : ["-d", description];
        var result = await Rpc.RunAsync("bolt11-receive", cancellationToken, args).ConfigureAwait(false);
        return new TestInvoice(result["invoice"]!.GetValue<string>(),
                               result["payment_hash"]!.GetValue<string>().ToLowerInvariant());
    }

    /// <summary><c>pay --wait</c> (<see cref="PayTimeoutSeconds"/>); a failed or timed-out payment is a failed result.</summary>
    public async Task<TestPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Rpc.RunAsync("pay", cancellationToken, bolt11, "--wait", "--wait-timeout",
                                            PayTimeoutSeconds.ToString(CultureInfo.InvariantCulture))
                                  .ConfigureAwait(false);
            return LdkJson.ToPaymentResult(result);
        }
        catch (LdkRpcException e)
        {
            return new TestPaymentResult(false, null, e.Message);
        }
    }

    public async Task<long> GetBlockHeightAsync(CancellationToken cancellationToken) =>
        LdkJson.BlockHeight(await Rpc.GetNodeInfoAsync(cancellationToken).ConfigureAwait(false));

    public async Task<long> GetConfirmedBalanceSatAsync(CancellationToken cancellationToken) =>
        LdkJson.SpendableOnchainSat(await Rpc.RunAsync("get-balances", cancellationToken).ConfigureAwait(false));

    public override string ToString() => $"ldk {Node}";

    /// <summary>The <c>open-channel</c> arguments of an open request to a peer at <paramref name="address"/>.</summary>
    public static IReadOnlyList<string> BuildOpenChannelArgs(TestOpenChannelRequest request, string address)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> args =
        [
            request.RemoteNodeId, address, $"{request.CapacitySat.ToString(CultureInfo.InvariantCulture)}sat"
        ];
        if (request.PushMsat > 0)
            args.AddRange(["--push-to-counterparty", $"{request.PushMsat.ToString(CultureInfo.InvariantCulture)}msat"]);
        if (request.Announce)
            args.Add("--announce-channel");
        return args;
    }
}