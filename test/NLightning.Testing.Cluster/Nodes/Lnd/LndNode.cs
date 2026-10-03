using Grpc.Core;

namespace NLightning.Testing.Cluster.Nodes.Lnd;

using Run;
using Testing.Lnd;
using Testing.Lnd.Lnrpc;
using Testing.Lnd.Routerrpc;
using Topology;

/// <summary>
/// An LND node deployed in a run: its <see cref="KubeNodeHandle"/> (StatefulSet + PVC, see <see cref="LndWorkload"/>),
/// its credentials, one in-tree gRPC connection (<see cref="Connection"/>, <c>NLightning.Testing.Lnd</c>, the
/// certificate pinned) and the <see cref="ILightningTestPeer"/> facade. Restarts and kills go through the StatefulSet:
/// the node keeps its name, wallet, channels, certificate and macaroon, and the connection stays the same object (it
/// dials the pod's DNS name, which follows the new pod). Also a topology node (<see cref="LndNodeDeployer"/>).
/// </summary>
public sealed class LndNode : ITopologyLightningNode, IDisposable
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(500);

    private readonly KubeNodeHandle _handle;
    private LndNodeConnection? _connection;
    private string? _nodeId;

    private LndNode(KubeNodeHandle handle, LndNodeOptions options)
    {
        _handle = handle;
        Options = options;
    }

    public LndNodeOptions Options { get; }

    public INodeHandle Node => _handle;

    /// <summary>The deployed node's handle (exec, logs, restart).</summary>
    public KubeNodeHandle Handle => _handle;

    public NodeKind Kind => NodeKind.Lnd;

    public string Alias => Options.Alias;

    /// <summary>The certificate and macaroon read at the last (re)connection.</summary>
    public LndCredentials? Credentials { get; private set; }

    /// <summary>
    /// The gRPC connection (the in-tree client, LNUnit.LND's member names: <c>LightningClient</c>,
    /// <c>RouterClient</c>, <c>WalletKitClient</c>, ...). The same object across restarts and kills: its endpoint is
    /// the pod's DNS name (<see cref="GrpcHost"/>) and the certificate it pins lives on the PVC. It is replaced only
    /// when LND wrote another certificate or macaroon (a node whose PVC was lost).
    /// </summary>
    public LndNodeConnection Connection =>
        _connection ?? throw new InvalidOperationException($"{Alias}: gRPC is not connected yet");

    public Lightning.LightningClient Lightning => Connection.LightningClient;

    public Router.RouterClient Router => Connection.RouterClient;

    /// <summary>
    /// Where this process reaches the node's gRPC port: the pod's DNS name (<c>alias-0.alias.ns.svc.cluster.local</c>),
    /// in the cluster and from the host alike (OrbStack resolves it on the Mac, about 120 ms once per connection). It
    /// follows the pod after a restart, which a pod IP does not, so <see cref="Connection"/> survives a restart.
    /// </summary>
    public string GrpcHost => _handle.PodDnsName;

    /// <summary>
    /// Deploys <paramref name="options"/>' node into <paramref name="run"/>, waits until its pod is ready (LND synced
    /// to the chain), reads its credentials and connects. The topology's bitcoind must be up and have mined a recent
    /// block, or LND never reports <c>synced_to_chain</c>.
    /// </summary>
    public static async Task<LndNode> DeployAsync(TestRun run, LndNodeOptions options, TimeSpan readyTimeout,
                                                  CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);

        var handle = await run.DeployAsync(LndWorkload.Build(options), readyTimeout, cancellationToken)
                              .ConfigureAwait(false);
        var node = new LndNode(handle, options);
        try
        {
            await node.ReconnectAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
            return node;
        }
        catch
        {
            node.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the credentials again (a new connection only when they changed), then waits until the server is active
    /// (<c>SERVER_ACTIVE</c>; LND 0.21 stays <c>RPC_ACTIVE</c> on a chain at genesis) and <c>GetInfo</c> answers with
    /// <c>synced_to_chain</c>, and loads the connection's node info (<c>LocalNodePubKey</c>, <c>LocalAlias</c>).
    /// </summary>
    public async Task ReconnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var credentials = await LndCredentials.ReadAsync(_handle, timeout, cancellationToken).ConfigureAwait(false);
        if (_connection is null || !credentials.SameAs(Credentials))
        {
            var previous = _connection;
            _connection = LndNodeConnection.CreateWithoutNodeInfo(credentials.ToSettings(GrpcHost,
                                                                                           LndWorkload.GrpcPort));
            previous?.Dispose();
        }

        Credentials = credentials;
        await WaitServerActiveAsync(Remaining(deadline), cancellationToken).ConfigureAwait(false);
        var info = await WaitSyncedToChainAsync(Remaining(deadline), cancellationToken).ConfigureAwait(false);
        if (_nodeId is not null && _nodeId != info.IdentityPubkey)
            throw new InvalidOperationException($"{Alias}: node id changed from {_nodeId} to {info.IdentityPubkey}");

        _nodeId = info.IdentityPubkey;
        await Connection.RefreshNodeInfoAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls <c>State.GetState</c> until LND reports <c>SERVER_ACTIVE</c> (every RPC served; gRPC errors while LND
    /// starts or while the DNS name still points at the old pod count as not yet).
    /// </summary>
    public Task WaitServerActiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var last = "no answer";
        return Poll.UntilDoneAsync(async ct =>
        {
            try
            {
                var state = await Connection.StateClient.GetStateAsync(new GetStateRequest(),
                                                                       deadline: DateTime.UtcNow.AddSeconds(5),
                                                                       cancellationToken: ct)
                                            .ResponseAsync.ConfigureAwait(false);
                last = state.State.ToString();
                return state.State == WalletState.ServerActive ? null : $"state {last}";
            }
            catch (RpcException e) when (!ct.IsCancellationRequested)
            {
                last = $"{e.StatusCode}: {e.Status.Detail}";
                return last;
            }
        }, timeout, $"{Alias} ({Connection.Host}) SERVER_ACTIVE", cancellationToken, s_pollInterval);
    }

    /// <summary>
    /// Polls <c>GetInfo</c> until it answers with <c>synced_to_chain</c> and, with <paramref name="minBlockHeight"/>,
    /// at least that height (gRPC errors while LND starts count as not yet).
    /// </summary>
    public async Task<GetInfoResponse> WaitSyncedToChainAsync(TimeSpan timeout, CancellationToken cancellationToken,
                                                              uint? minBlockHeight = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = "no answer";
        while (true)
        {
            try
            {
                var info = await Lightning.GetInfoAsync(new GetInfoRequest(), deadline: Deadline(deadline),
                                                        cancellationToken: cancellationToken)
                                          .ResponseAsync.ConfigureAwait(false);
                if (info.SyncedToChain && (minBlockHeight is null || info.BlockHeight >= minBlockHeight))
                    return info;

                last = $"synced_to_chain={info.SyncedToChain} height={info.BlockHeight}";
            }
            catch (RpcException e) when (!cancellationToken.IsCancellationRequested)
            {
                last = $"{e.StatusCode}: {e.Status.Detail}";
            }

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"{Alias} ({Connection.Host}) not synced to chain after {timeout}: {last}");

            await Task.Delay(s_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A graceful restart (same pod name and PVC), then the same connection is ready again (<see cref="ReconnectAsync"/>).</summary>
    public async Task RestartAsync(TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        await _handle.RestartAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
        await ReconnectAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A hard stop (<see cref="INodeHandle.KillAsync"/>: 1 s grace, same pod name and PVC), then the same connection is
    /// ready again (<see cref="ReconnectAsync"/>).
    /// </summary>
    public async Task KillAsync(TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        await _handle.KillAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
        await ReconnectAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetNodeIdAsync(CancellationToken cancellationToken)
    {
        if (_nodeId is not null)
            return _nodeId;

        var info = await Lightning.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken)
                                  .ResponseAsync.ConfigureAwait(false);
        return _nodeId = info.IdentityPubkey;
    }

    /// <summary>The plain alias and p2p port: what the other nodes of the run dial.</summary>
    public async Task<TestPeerAddress> GetAddressAsync(CancellationToken cancellationToken) =>
        new(await GetNodeIdAsync(cancellationToken).ConfigureAwait(false), Alias, LndWorkload.P2pPort);

    public async Task ConnectAsync(TestPeerAddress peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);

        try
        {
            var request = new ConnectPeerRequest
            {
                Addr = new LightningAddress { Pubkey = peer.NodeId, Host = $"{peer.Host}:{peer.Port}" },
                Timeout = 30
            };
            await Lightning.ConnectPeerAsync(request, cancellationToken: cancellationToken)
                           .ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException e) when (e.Status.Detail.Contains("already connected", StringComparison.Ordinal))
        {
            // Connected already: fall through to the init check
        }

        await Poll.UntilAsync(async ct =>
                              {
                                  var peers = await Lightning.ListPeersAsync(new ListPeersRequest(),
                                                                             cancellationToken: ct)
                                                             .ResponseAsync.ConfigureAwait(false);
                                  return peers.Peers.Any(p => p.PubKey == peer.NodeId);
                              }, TimeSpan.FromSeconds(30), s_pollInterval, $"{Alias} connected to {peer}",
                              cancellationToken)
                  .ConfigureAwait(false);
    }

    public async Task DisconnectAsync(string nodeId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await Lightning.DisconnectPeerAsync(new DisconnectPeerRequest { PubKey = nodeId },
                                            cancellationToken: cancellationToken)
                       .ResponseAsync.ConfigureAwait(false);
    }

    public async Task<string> GetNewAddressAsync(CancellationToken cancellationToken)
    {
        var response = await Lightning.NewAddressAsync(new NewAddressRequest { Type = AddressType.WitnessPubkeyHash },
                                                       cancellationToken: cancellationToken)
                                      .ResponseAsync.ConfigureAwait(false);
        return response.Address;
    }

    /// <summary>The block height LND has processed (<c>GetInfo</c>).</summary>
    /// <summary>
    /// The height LND has processed: <c>GetInfo</c>'s block height once it reports <c>synced_to_chain</c>, one less
    /// before. <c>block_height</c> alone is the backend's tip, which LND's wallet may not have reached yet, and an open
    /// then fails with "channels cannot be created before the wallet is fully synced" (seen when the topology's nodes
    /// start with the chain).
    /// </summary>
    public async Task<long> GetBlockHeightAsync(CancellationToken cancellationToken)
    {
        var info = await Lightning.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken)
                                  .ResponseAsync.ConfigureAwait(false);
        return info.SyncedToChain ? info.BlockHeight : Math.Max(0, (long)info.BlockHeight - 1);
    }

    /// <summary>The wallet's confirmed balance in satoshis.</summary>
    public async Task<long> GetConfirmedBalanceSatAsync(CancellationToken cancellationToken)
    {
        var balance = await Lightning.WalletBalanceAsync(new WalletBalanceRequest(),
                                                         cancellationToken: cancellationToken)
                                     .ResponseAsync.ConfigureAwait(false);
        return balance.ConfirmedBalance;
    }

    public async Task<TestChannelOpen> OpenChannelAsync(TestOpenChannelRequest request,
                                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var open = new OpenChannelRequest
        {
            NodePubkey = Google.Protobuf.ByteString.CopyFrom(Convert.FromHexString(request.RemoteNodeId)),
            LocalFundingAmount = request.CapacitySat,
            PushSat = LndMapping.ToWholeSatoshis(request.PushMsat, "The push amount"),
            Private = !request.Announce
        };
        var point = await Lightning.OpenChannelSyncAsync(open, cancellationToken: cancellationToken)
                                   .ResponseAsync.ConfigureAwait(false);
        return new TestChannelOpen(LndMapping.TxIdOf(point), (int)point.OutputIndex);
    }

    public async Task<IReadOnlyList<TestChannel>> ListChannelsAsync(CancellationToken cancellationToken)
    {
        var response = await Lightning.ListChannelsAsync(new ListChannelsRequest(),
                                                         cancellationToken: cancellationToken)
                                      .ResponseAsync.ConfigureAwait(false);
        return response.Channels.Select(LndMapping.ToTestChannel).ToList();
    }

    /// <summary>Waits until the channel funded by <paramref name="fundingTxId"/> is listed and active.</summary>
    public Task<TestChannel> WaitForActiveChannelAsync(string fundingTxId, TimeSpan timeout,
                                                       CancellationToken cancellationToken) =>
        Poll.ForAsync(async ct => (await ListChannelsAsync(ct).ConfigureAwait(false))
                                 .FirstOrDefault(c => c.Active && c.FundingTxId == fundingTxId),
                      timeout, s_pollInterval, $"{Alias}: channel {fundingTxId} active", cancellationToken);

    public async Task<TestInvoice> CreateInvoiceAsync(long? amountMsat, string description,
                                                      CancellationToken cancellationToken)
    {
        var invoice = new Invoice { ValueMsat = amountMsat ?? 0, Memo = description ?? string.Empty };
        var response = await Lightning.AddInvoiceAsync(invoice, cancellationToken: cancellationToken)
                                      .ResponseAsync.ConfigureAwait(false);
        return new TestInvoice(response.PaymentRequest, LndMapping.ToHex(response.RHash));
    }

    /// <summary>
    /// Pays with the router's <c>SendPaymentV2</c> (60 s, at most 1,000 sat of fees) and reads the stream until the
    /// payment succeeds or fails.
    /// </summary>
    public async Task<TestPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bolt11);

        using var call = Router.SendPaymentV2(new SendPaymentRequest
        {
            PaymentRequest = bolt11,
            TimeoutSeconds = 60,
            FeeLimitSat = 1_000
        }, cancellationToken: cancellationToken);
        Payment? last = null;
        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            last = call.ResponseStream.Current;
            switch (last.Status)
            {
                case Payment.Types.PaymentStatus.Succeeded:
                    return new TestPaymentResult(true, last.PaymentPreimage, null);
                case Payment.Types.PaymentStatus.Failed:
                    return new TestPaymentResult(false, null, last.FailureReason.ToString());
            }
        }

        return new TestPaymentResult(false, null, $"stream ended in {last?.Status.ToString() ?? "no update"}");
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    public override string ToString() => $"lnd {_handle}";

    private static TimeSpan Remaining(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        return remaining > TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5);
    }

    /// <summary>A per-call gRPC deadline: at most 10 s, and never past <paramref name="deadline"/> by more than 1 s.</summary>
    private static DateTime Deadline(DateTime deadline)
    {
        var call = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var cap = deadline + TimeSpan.FromSeconds(1);
        return call < cap ? call : cap;
    }
}