using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NBitcoin.RPC;

namespace NLightning.Testing.Cluster.Nodes.BitcoinCore;

using Chain;
using Rpc;
using Run;

/// <summary>
/// A deployed regtest bitcoind: its <see cref="KubeNodeHandle"/>, the addresses other pods use (the bare alias inside
/// the run's namespace), the addresses the test process uses (<see cref="RpcRoute"/>), and RPC clients for both
/// transports.
/// </summary>
/// <example>
/// <code>
/// var miner = await BitcoinCoreNode.DeployAsync(run, new BitcoinCoreOptions(), TimeSpan.FromMinutes(2), ct);
/// var chain = new RegtestChain(await miner.ConnectRpcAsync(RpcRoute.Auto, ct));
/// await chain.MineAsync(101, ct);
/// </code>
/// </example>
public sealed class BitcoinCoreNode
{
    /// <summary><c>RPC_CLIENT_NODE_ALREADY_ADDED</c>.</summary>
    private const int AlreadyAddedCode = -23;

    private static readonly TimeSpan s_probeConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long <see cref="RpcRoute.Auto"/> keeps trying the pod IP before it settles for exec.</summary>
    public static readonly TimeSpan DefaultAutoProbeWindow = TimeSpan.FromSeconds(8);

    public BitcoinCoreNode(KubeNodeHandle handle, BitcoinCoreOptions options)
    {
        Handle = handle ?? throw new ArgumentNullException(nameof(handle));
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public KubeNodeHandle Handle { get; }

    public BitcoinCoreOptions Options { get; }

    public string Name => Handle.Name;

    public NetworkCredential Credentials => new(Options.RpcUser, Options.RpcPassword);

    /// <summary>The RPC URL other pods of the run use (<c>http://miner:18443</c>).</summary>
    public string ClusterRpcUrl => $"http://{Name}:{BitcoinCorePorts.Rpc}";

    /// <summary>The P2P address other bitcoind nodes of the run peer with (<c>miner:18444</c>).</summary>
    public string ClusterP2pAddress => $"{Name}:{BitcoinCorePorts.P2p}";

    /// <summary>The raw block feed for pods of the run (LND <c>--bitcoind.zmqpubrawblock</c>).</summary>
    public string ClusterZmqRawBlock => $"tcp://{Name}:{BitcoinCorePorts.ZmqRawBlock}";

    /// <summary>The raw tx feed for pods of the run (LND <c>--bitcoind.zmqpubrawtx</c>, Eclair <c>zmqtx</c>).</summary>
    public string ClusterZmqRawTx => $"tcp://{Name}:{BitcoinCorePorts.ZmqRawTx}";

    /// <summary>The hash block feed for pods of the run (Eclair <c>zmqblock</c>).</summary>
    public string ClusterZmqHashBlock => $"tcp://{Name}:{BitcoinCorePorts.ZmqHashBlock}";

    /// <summary>
    /// Deploys bitcoind into <paramref name="run"/>, waits until RPC answers and creates <see cref="BitcoinCoreOptions.Wallet"/>.
    /// </summary>
    public static async Task<BitcoinCoreNode> DeployAsync(TestRun run, BitcoinCoreOptions options,
                                                          TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);

        var handle = await run.DeployAsync(BitcoinCoreWorkload.Build(options), readyTimeout, cancellationToken)
                              .ConfigureAwait(false);
        var node = new BitcoinCoreNode(handle, options);
        if (options.Wallet is { } wallet)
            await node.CreateRpc(RpcRoute.Exec, null).EnsureWalletAsync(wallet, cancellationToken)
                      .ConfigureAwait(false);

        return node;
    }

    /// <summary>
    /// The host (IP or DNS name) the test process uses for <paramref name="route"/> (<see cref="RpcRoute.PodIp"/> or
    /// <see cref="RpcRoute.ServiceDns"/>). The pod IP is the one seen at the last wait.
    /// </summary>
    public string GetHost(RpcRoute route) =>
        route switch
        {
            RpcRoute.PodIp => Handle.PodIp
                           ?? throw new InvalidOperationException($"{Handle} has no pod IP yet (wait for it first)"),
            RpcRoute.ServiceDns => Handle.ServiceDnsName,
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Only PodIp and ServiceDns have a host")
        };

    /// <summary>
    /// A typed RPC client over <paramref name="route"/> (not <see cref="RpcRoute.Auto"/>: use
    /// <see cref="ConnectRpcAsync"/>). <paramref name="wallet"/> defaults to the node's wallet; pass null explicitly
    /// through <see cref="CreateRpc(RpcRoute, string?)"/> for the node endpoint.
    /// </summary>
    public BitcoinCoreRpcClient CreateRpc(RpcRoute route) => CreateRpc(route, Options.Wallet);

    /// <summary>A typed RPC client over <paramref name="route"/> for <paramref name="wallet"/> (null: node calls only).</summary>
    public BitcoinCoreRpcClient CreateRpc(RpcRoute route, string? wallet) =>
        new(route switch
        {
            RpcRoute.Exec => new ExecCliRpcTransport(Handle, BitcoinCorePorts.Rpc, Options.RpcUser,
                                                     Options.RpcPassword, wallet),
            RpcRoute.PodIp or RpcRoute.ServiceDns => new HttpRpcTransport(() => GetHost(route), BitcoinCorePorts.Rpc,
                                                                          Credentials, wallet),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route,
                                                       "Resolve Auto with ConnectRpcAsync or RpcRouteSelector")
        });

    /// <summary>
    /// A typed RPC client over <paramref name="route"/>, resolving <see cref="RpcRoute.Auto"/> with
    /// <see cref="RpcRouteSelector"/>: in-cluster the Service DNS name; otherwise the pod IP when it answers a TCP
    /// connect within <paramref name="probeWindow"/> (default <see cref="DefaultAutoProbeWindow"/>; OrbStack has been
    /// seen to route a just-ready pod's IP only seconds later); else exec.
    /// </summary>
    public async Task<BitcoinCoreRpcClient> ConnectRpcAsync(RpcRoute route, CancellationToken cancellationToken,
                                                            TimeSpan? probeWindow = null)
    {
        if (route == RpcRoute.Auto)
        {
            var inCluster = KubeClientFactory.DetectSource() == KubeConfigSource.InCluster;
            var podIpReachable = !inCluster
                              && Handle.PodIp is { } ip
                              && await IsReachableAsync(ip, probeWindow ?? DefaultAutoProbeWindow, cancellationToken)
                                    .ConfigureAwait(false);
            route = RpcRouteSelector.Select(route, inCluster, podIpReachable);
        }

        return CreateRpc(route);
    }

    /// <summary>
    /// An NBitcoin client on <paramref name="route"/>'s host, for code built on NBitcoin (the in-process NLightning
    /// node's <c>RegtestBitcoinEndpoint</c>). Its host is fixed: build a new one after a restart moved the pod IP.
    /// </summary>
    public RPCClient CreateNBitcoinClient(RpcRoute route, string? wallet = null) =>
        new HttpRpcTransport(() => GetHost(route), BitcoinCorePorts.Rpc, Credentials, wallet ?? Options.Wallet)
           .GetClient();

    /// <summary>Whether the host process reaches the RPC port by pod IP and by Service DNS name (TCP connect).</summary>
    public async Task<HostRouteProbe> ProbeHostRouteAsync(CancellationToken cancellationToken)
    {
        var podIp = Handle.PodIp;
        var podIpTime = podIp is null
                            ? null
                            : await TryConnectAsync(podIp, BitcoinCorePorts.Rpc, cancellationToken)
                                 .ConfigureAwait(false);
        string? dnsError = null;
        TimeSpan? dnsTime = null;
        try
        {
            dnsTime = await TryConnectAsync(Handle.ServiceDnsName, BitcoinCorePorts.Rpc, cancellationToken,
                                            throwOnFailure: true).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or TimeoutException)
        {
            dnsError = e.Message;
        }

        return new HostRouteProbe(podIp, podIpTime is not null, podIpTime, Handle.ServiceDnsName, dnsTime is not null,
                                  dnsError);
    }

    /// <summary>
    /// Peers this node with <paramref name="peer"/> and waits until it has a connection: <c>addnode add</c> of the
    /// peer's alias (kept across restarts of either pod), and an <c>addnode onetry</c> every 2 s by pod IP until
    /// connected (a name looked up while the peer's Service had no endpoint yet may stay unresolved for a while).
    /// </summary>
    public async Task ConnectToAsync(BitcoinCoreNode peer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var rpc = CreateRpc(RpcRoute.Exec, null);
        try
        {
            await rpc.AddNodeAsync(peer.ClusterP2pAddress, "add", cancellationToken).ConfigureAwait(false);
        }
        catch (BitcoinRpcException e) when (e.Code == AlreadyAddedCode)
        {
            // An -addnode argument already lists it
        }

        var nextTry = DateTime.MinValue;
        await ChainPoll.UntilAsync(async ct =>
                                   {
                                       if (await rpc.GetConnectionCountAsync(ct).ConfigureAwait(false) > 0)
                                           return true;
                                       if (DateTime.UtcNow < nextTry)
                                           return false;

                                       nextTry = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                                       var target = peer.Handle.PodIp is { } ip
                                                        ? $"{ip}:{BitcoinCorePorts.P2p}"
                                                        : peer.ClusterP2pAddress;
                                       await rpc.AddNodeAsync(target, "onetry", ct).ConfigureAwait(false);
                                       return false;
                                   }, timeout, TimeSpan.FromMilliseconds(250), $"{Name} connected to {peer.Name}",
                                   cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tip as a <see cref="ChainFollower"/> of another bitcoind (same height and hash).</summary>
    public ChainFollower AsFollower(IBitcoinCoreRpc rpc) => ChainFollower.Bitcoind(Name, rpc);

    public override string ToString() => Handle.ToString();

    /// <summary>Whether the RPC port answers a TCP connect within <paramref name="window"/> (retried every 500 ms).</summary>
    private static async Task<bool> IsReachableAsync(string host, TimeSpan window, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            if (await TryConnectAsync(host, BitcoinCorePorts.Rpc, cancellationToken).ConfigureAwait(false) is not null)
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The connect time, or null when the port did not answer within the probe timeout.</summary>
    private static async Task<TimeSpan?> TryConnectAsync(string host, int port, CancellationToken cancellationToken,
                                                         bool throwOnFailure = false)
    {
        using var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_probeConnectTimeout);
        var watch = Stopwatch.StartNew();
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return watch.Elapsed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (throwOnFailure)
                throw new TimeoutException($"{host}:{port} did not answer within {s_probeConnectTimeout}");
            return null;
        }
        catch (SocketException) when (!throwOnFailure)
        {
            return null;
        }
    }
}