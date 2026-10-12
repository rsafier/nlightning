namespace NLightning.Testing.Cluster.Nodes.Eclair;

using Run;
using Topology;

/// <summary>
/// The <see cref="ILightningTestPeer"/> of an Eclair node, over its JSON API (<see cref="Api"/>, also reachable for
/// Eclair-specific calls: splicing, offers, RBF). The API is called at the pod's IP from the host and at its pod DNS
/// name inside the cluster (<see cref="ApiHostFor"/>); <see cref="RestartAsync"/> moves it to the new pod.
/// </summary>
public sealed class EclairTestPeer : ITopologyLightningNode, IDisposable
{
    private readonly bool _inCluster;
    private string? _nodeId;

    /// <param name="node">The deployed Eclair node (ready, so it has a pod IP).</param>
    /// <param name="apiPassword">The API password (<see cref="EclairNodeOptions.ApiPassword"/>).</param>
    /// <param name="p2pHost">
    /// The host peers dial: a <see cref="StableNodeAddress"/> name in a topology, the headless Service name when null.
    /// </param>
    /// <param name="inCluster">Whether this process runs in the cluster; null detects it
    /// (<see cref="KubeClientFactory.DetectSource"/>).</param>
    public EclairTestPeer(KubeNodeHandle node, string apiPassword, string? p2pHost = null, bool? inCluster = null)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        _inCluster = inCluster ?? KubeClientFactory.DetectSource() == KubeConfigSource.InCluster;
        P2PHost = p2pHost ?? node.ServiceDnsName;
        Api = new EclairApi(EclairApi.BuildBaseAddress(ApiHostFor(node, _inCluster)), apiPassword);
    }

    /// <summary>The host other nodes dial.</summary>
    public string P2PHost { get; }

    public KubeNodeHandle Node { get; }

    INodeHandle? ILightningTestPeer.Node => Node;

    /// <summary>The JSON API, at the node's current address.</summary>
    public EclairApi Api { get; }

    public NodeKind Kind => NodeKind.Eclair;

    public string Alias => Node.Name;

    /// <summary>
    /// Where this process calls the API of <paramref name="node"/>: its pod DNS name in the cluster, its pod IP on the
    /// host (no DNS, no Service routing delay).
    /// </summary>
    public static string ApiHostFor(INodeHandle node, bool inCluster)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (inCluster)
            return node.PodDnsName;

        return node.PodIp ?? throw new InvalidOperationException($"{node} has no pod IP yet (wait until it is ready)");
    }

    public async Task<string> GetNodeIdAsync(CancellationToken cancellationToken)
    {
        if (_nodeId is not null)
            return _nodeId;

        var info = await Api.CallAsync("getinfo", cancellationToken).ConfigureAwait(false);
        return _nodeId = info!["nodeId"]!.GetValue<string>().ToLowerInvariant();
    }

    /// <summary><see cref="P2PHost"/> and the p2p port.</summary>
    public async Task<TestPeerAddress> GetAddressAsync(CancellationToken cancellationToken) =>
        new(await GetNodeIdAsync(cancellationToken).ConfigureAwait(false), P2PHost, EclairNode.P2PPort);

    /// <summary><c>connect uri=...</c>, then until <c>peers</c> lists the peer <c>CONNECTED</c>.</summary>
    public async Task ConnectAsync(TestPeerAddress peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        await Api.CallAsync("connect", cancellationToken, ("uri", peer.ToString())).ConfigureAwait(false);
        await Poll.UntilAsync(ct => IsConnectedAsync(peer.NodeId, ct), TimeSpan.FromSeconds(30), Poll.DefaultInterval,
                              $"{this} connected to {peer.NodeId}", cancellationToken)
                  .ConfigureAwait(false);
    }

    public async Task DisconnectAsync(string nodeId, CancellationToken cancellationToken) =>
        await Api.CallAsync("disconnect", cancellationToken, ("nodeId", nodeId)).ConfigureAwait(false);

    /// <summary>Whether <c>peers</c> lists <paramref name="nodeId"/> as <c>CONNECTED</c>.</summary>
    public async Task<bool> IsConnectedAsync(string nodeId, CancellationToken cancellationToken)
    {
        var peers = await Api.CallAsync("peers", cancellationToken).ConfigureAwait(false);
        return peers?.AsArray().Any(p => p?["nodeId"]?.GetValue<string>() == nodeId
                                      && p["state"]?.GetValue<string>() == "CONNECTED") == true;
    }

    public async Task<string> GetNewAddressAsync(CancellationToken cancellationToken) =>
        (await Api.CallAsync("getnewaddress", cancellationToken).ConfigureAwait(false))!.GetValue<string>();

    /// <summary>
    /// <c>open</c> (anchors channel type, the Docker suite's fee budget) and the funding txid of its answer; the
    /// output index is read from <c>channels</c> when Eclair lists it.
    /// </summary>
    public async Task<TestChannelOpen> OpenChannelAsync(TestOpenChannelRequest request,
                                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var answer = await Api.CallAsync("open", cancellationToken, BuildOpenParameters(request))
                              .ConfigureAwait(false);
        var text = answer?.ToString() ?? string.Empty;
        var txId = EclairJson.FundingTxIdOfOpen(text)
                ?? throw new EclairApiException("open", 200, $"no funding txid in the answer: {text}");
        var channel = (await ListChannelsAsync(cancellationToken).ConfigureAwait(false))
           .FirstOrDefault(c => c.FundingTxId == txId);
        return new TestChannelOpen(txId, channel?.OutputIndex);
    }

    /// <summary>The <c>open</c> parameters of a request (a push only when there is one).</summary>
    public static (string Name, object? Value)[] BuildOpenParameters(TestOpenChannelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return
        [
            ("nodeId", request.RemoteNodeId), ("fundingSatoshis", request.CapacitySat),
            ("pushMsat", request.PushMsat > 0 ? request.PushMsat : null),
            ("channelType", "anchor_outputs_zero_fee_htlc_tx"), ("announceChannel", request.Announce),
            ("fundingFeeBudgetSatoshis", 10_000L), ("openTimeoutSeconds", 60)
        ];
    }

    public async Task<IReadOnlyList<TestChannel>> ListChannelsAsync(CancellationToken cancellationToken) =>
        EclairJson.ToTestChannels(await Api.CallAsync("channels", cancellationToken).ConfigureAwait(false));

    public async Task<TestInvoice> CreateInvoiceAsync(long? amountMsat, string description,
                                                      CancellationToken cancellationToken)
    {
        var result = await Api.CallAsync("createinvoice", cancellationToken, ("amountMsat", amountMsat),
                                         ("description", description))
                              .ConfigureAwait(false);
        return new TestInvoice(result!["serialized"]!.GetValue<string>(),
                               result["paymentHash"]!.GetValue<string>().ToLowerInvariant());
    }

    /// <summary><c>payinvoice blocking=true</c>; an API error is a failed result.</summary>
    public async Task<TestPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Api.CallAsync("payinvoice", cancellationToken, ("invoice", bolt11),
                                             ("blocking", true), ("maxAttempts", 3))
                                  .ConfigureAwait(false);
            return EclairJson.ToPaymentResult(result);
        }
        catch (EclairApiException e)
        {
            return new TestPaymentResult(false, null, e.Message);
        }
    }

    public async Task<long> GetBlockHeightAsync(CancellationToken cancellationToken) =>
        (await Api.CallAsync("getinfo", cancellationToken).ConfigureAwait(false))!["blockHeight"]!.GetValue<long>();

    public async Task<long> GetConfirmedBalanceSatAsync(CancellationToken cancellationToken)
    {
        var balance = await Api.CallAsync("onchainbalance", cancellationToken).ConfigureAwait(false);
        return balance?["confirmed"]?.GetValue<long>() ?? 0;
    }

    /// <summary>
    /// A graceful restart (the preStop drains the stable Service, then the JVM stops on SIGTERM; the new pod starts on
    /// the same PVC), then the API is moved to the new pod and called until it answers.
    /// </summary>
    public async Task RestartAsync(TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        await Node.RestartAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
        Api.Retarget(EclairApi.BuildBaseAddress(ApiHostFor(Node, _inCluster)));
        await Poll.UntilAsync(async ct =>
        {
            try
            {
                await Api.CallAsync("getinfo", ct).ConfigureAwait(false);
                return true;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (EclairApiException)
            {
                return false;
            }
        }, readyTimeout, Poll.DefaultInterval, $"{this} answers getinfo after its restart", cancellationToken)
                  .ConfigureAwait(false);
    }

    public void Dispose() => Api.Dispose();

    public override string ToString() => $"eclair {Node}";
}