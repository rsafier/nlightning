using Grpc.Core;
using Lnrpc;
using Routerrpc;

namespace NLightning.Testing.Cluster.Nodes.Lnd;

using Run;

/// <summary>
/// An LND node deployed in a run: its <see cref="KubeNodeHandle"/> (StatefulSet + PVC, see <see cref="LndWorkload"/>),
/// its credentials, a pinned gRPC connection (<see cref="Grpc"/>) and the <see cref="ILightningTestPeer"/> facade.
/// Restarts and kills go through the StatefulSet and reconnect: the node keeps its name, wallet and channels.
/// </summary>
public sealed class LndNode : ILightningTestPeer, IDisposable
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(500);

    private readonly KubeNodeHandle _handle;
    private LndGrpcConnection? _grpc;
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

    /// <summary>The current gRPC connection (replaced after a restart).</summary>
    public LndGrpcConnection Grpc =>
        _grpc ?? throw new InvalidOperationException($"{Alias}: gRPC is not connected yet");

    public Lightning.LightningClient Lightning => Grpc.Lightning;

    public Router.RouterClient Router => Grpc.Router;

    /// <summary>
    /// Where this process reaches the node's gRPC port: the pod's stable DNS name when the test runs in the cluster,
    /// the pod IP from the host (OrbStack routes pod IPs to the Mac).
    /// </summary>
    public string GrpcHost =>
        KubeClientFactory.DetectSource() == KubeConfigSource.InCluster
            ? _handle.PodDnsName
            : _handle.PodIp ?? throw new InvalidOperationException($"{Alias}: the pod IP is not known yet");

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
    /// Reads the credentials again, opens a new gRPC connection to the current pod and waits until <c>GetInfo</c>
    /// answers with <c>synced_to_chain</c>.
    /// </summary>
    public async Task ReconnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        Credentials = await LndCredentials.ReadAsync(_handle, timeout, cancellationToken).ConfigureAwait(false);
        var previous = _grpc;
        _grpc = new LndGrpcConnection(GrpcHost, LndWorkload.GrpcPort, Credentials);
        previous?.Dispose();

        var info = await WaitSyncedToChainAsync(Remaining(deadline), cancellationToken).ConfigureAwait(false);
        if (_nodeId is not null && _nodeId != info.IdentityPubkey)
            throw new InvalidOperationException($"{Alias}: node id changed from {_nodeId} to {info.IdentityPubkey}");

        _nodeId = info.IdentityPubkey;
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
                throw new TimeoutException($"{Alias} ({Grpc.Endpoint}) not synced to chain after {timeout}: {last}");

            await Task.Delay(s_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A graceful restart (same pod name and PVC), then a new gRPC connection.</summary>
    public async Task RestartAsync(TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        await _handle.RestartAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
        await ReconnectAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A crash (grace 0, same pod name and PVC), then a new gRPC connection.</summary>
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

        await WaitAsync(async () =>
                        {
                            var peers = await Lightning.ListPeersAsync(new ListPeersRequest(),
                                                                       cancellationToken: cancellationToken)
                                                       .ResponseAsync.ConfigureAwait(false);
                            return peers.Peers.Any(p => p.PubKey == peer.NodeId);
                        }, TimeSpan.FromSeconds(30), $"{Alias} connected to {peer}", cancellationToken)
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
        WaitForAsync(async () => (await ListChannelsAsync(cancellationToken).ConfigureAwait(false))
                                .FirstOrDefault(c => c.Active && c.FundingTxId == fundingTxId),
                     timeout, $"{Alias}: channel {fundingTxId} active", cancellationToken);

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
        _grpc?.Dispose();
        _grpc = null;
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

    private static Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout, string what,
                                  CancellationToken cancellationToken) =>
        WaitForAsync(async () => await condition().ConfigureAwait(false) ? what : null, timeout, what,
                     cancellationToken);

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, string what,
                                                 CancellationToken cancellationToken) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var value = await probe().ConfigureAwait(false);
            if (value is not null)
                return value;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Timed out after {timeout}: {what}");

            await Task.Delay(s_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}