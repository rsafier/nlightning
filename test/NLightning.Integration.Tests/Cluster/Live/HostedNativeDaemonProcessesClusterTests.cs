using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NLightning.Client.Ipc;
using NLightning.Daemon.Contracts.Utilities;
using NLightning.Daemon.Hosting;
using NLightning.Daemon.Interfaces;
using NLightning.Domain.Money;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.RemoteSigning.Tests;
using NLightning.Testing.Cluster.Nodes.Lnd;
using NLightning.Testing.Cluster.Run;
using NLightning.Testing.Cluster.Topology;
using NLightning.Testing.Lnd.Lnrpc;
using ClusterPoll = NLightning.Testing.Cluster.Poll;

namespace NLightning.Integration.Tests.Cluster.Live;

/// <summary>Exercises actual hosted daemon processes through their authenticated public IPC API.</summary>
[Trait("Category", "Cluster")]
public sealed class HostedNativeDaemonProcessesClusterTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);
    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_TwoDaemonProcesses_When_SignerAStops_Then_BKeepsSigningAndRestartsPreserveIsolatedWallets()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("hosted-native-processes")
            with
        { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        using var topology = await new TopologyBuilder
        { Log = Log, ReadyTimeout = TimeSpan.FromMinutes(4), StepTimeout = s_timeout }
            .AddBitcoinCore("miner").AddLnd("alice").AddLnd("bob").BuildAsync(run, ct);
        await using var signers = new HostedNativeSignerSupervisor();
        var first = await signers.AddAsync("first", new string('a', 64));
        var second = await signers.AddAsync("second", new string('b', 64));
        using var a = new RemoteSignerConnection(first.Options());
        using var b = new RemoteSignerConnection(second.Options());
        var launchA = Configure(first, a, topology.Chain);
        var launchB = Configure(second, b, topology.Chain);
        var manifest = new HostedNodeIsolationManifest([launchA.Enrollment, launchB.Enrollment]);
        await using var daemons = new HostedNativeNodeProcessSupervisor(manifest);
        await daemons.StartAsync([launchA, launchB], ct);
        var processA = daemons.Node(a.Context.NodeId);
        var processB = daemons.Node(b.Context.NodeId);
        Assert.NotNull(processA.ProcessId);
        Assert.NotNull(processB.ProcessId);
        Assert.NotEqual(processA.ProcessId, processB.ProcessId);
        await using var clientA = Client(launchA);
        await using var clientB = Client(launchB);
        var infoA = await clientA.GetNodeInfoAsync(ct);
        var infoB = await clientB.GetNodeInfoAsync(ct);
        Assert.Equal(a.Context.NodeId, infoA.NodeId);
        Assert.Equal(a.Context.OwnerId, infoA.OwnerId);
        Assert.Equal(a.Context.SignerId, infoA.SignerId);
        Assert.Equal(a.Identity.NodePublicKey, infoA.PubKey);
        Assert.Equal(b.Context.NodeId, infoB.NodeId);
        Assert.Equal(b.Context.OwnerId, infoB.OwnerId);
        Assert.Equal(b.Context.SignerId, infoB.SignerId);
        Assert.Equal(b.Identity.NodePublicKey, infoB.PubKey);
        Assert.NotEqual(infoA.PubKey, infoB.PubKey);
        Assert.NotEqual(launchA.Enrollment.PrivateDatabasePath, launchB.Enrollment.PrivateDatabasePath);
        await using var mismatched = new NamedPipeIpcClient(launchB.Enrollment.NodeIpcPath,
                                                           launchA.Enrollment.NodeCredentialPath);
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => mismatched.GetNodeInfoAsync(ct));
        Assert.Contains("auth_failed", denied.Message);
        var addressA = (await clientA.GetAddressAsync("p2wpkh", ct)).AddressP2Wpkh;
        var addressB = (await clientB.GetAddressAsync("p2wpkh", ct)).AddressP2Wpkh;
        Assert.NotNull(addressA);
        Assert.NotNull(addressB);
        Assert.NotEqual(addressA, addressB);
        await topology.Chain.SendToAddressAsync(addressA, 200_000, ct);
        await topology.Chain.SendToAddressAsync(addressB, 400_000, ct);
        await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
        await WaitBalanceAsync(clientA, 200_000, ct);
        await WaitBalanceAsync(clientB, 400_000, ct);
        await AssertInvoiceAsync(clientA, topology.Node<LndNode>("alice"), infoA.PubKey.ToString(), ct);
        await AssertInvoiceAsync(clientB, topology.Node<LndNode>("bob"), infoB.PubKey.ToString(), ct);

        await first.StopAsync();
        using var blocked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        blocked.CancelAfter(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<InvalidOperationException>(() => clientA.CreateInvoiceAsync(
            LightningMoney.MilliSatoshis(10_000_123), "signer A unavailable", null, blocked.Token));
        await AssertInvoiceAsync(clientB, topology.Node<LndNode>("bob"), infoB.PubKey.ToString(), ct);
        await topology.MineAndSyncAsync(1, ct);
        await WaitBalanceAsync(clientB, 400_000, ct);
        await processA.StopAsync(ct);
        await first.RestartAsync();
        await processA.RestartAsync(ct);
        await processB.RestartAsync(ct);
        Assert.Equal(infoA.PubKey, (await clientA.GetNodeInfoAsync(ct)).PubKey);
        Assert.Equal(infoB.PubKey, (await clientB.GetNodeInfoAsync(ct)).PubKey);
        await WaitBalanceAsync(clientA, 200_000, ct);
        await WaitBalanceAsync(clientB, 400_000, ct);
        await AssertInvoiceAsync(clientA, topology.Node<LndNode>("alice"), infoA.PubKey.ToString(), ct);
        await AssertInvoiceAsync(clientB, topology.Node<LndNode>("bob"), infoB.PubKey.ToString(), ct);
        Assert.False(File.Exists(Path.Combine(first.DirectoryPath, "node.key")));
        Assert.False(File.Exists(Path.Combine(second.DirectoryPath, "node.key")));
        Log($"{run.Namespace}: two real daemon processes retained separate IPC authority, wallets and identities; signer A outage preserved B signing");
    }

    private static NamedPipeIpcClient Client(HostedDaemonLaunch launch) =>
        new(launch.Enrollment.NodeIpcPath, launch.Enrollment.NodeCredentialPath);

    private static Task WaitBalanceAsync(NamedPipeIpcClient client, ulong expectedSat, CancellationToken ct) =>
        ClusterPoll.UntilDoneAsync(async cancellation =>
        {
            var balance = await client.GetWalletBalance(cancellation);
            return balance.ConfirmedBalance.Satoshi == checked((long)expectedSat) ? null : "daemon wallet has not processed its own deposit";
        }, s_timeout, "isolated daemon confirmed wallet", ct);

    private static async Task AssertInvoiceAsync(NamedPipeIpcClient client, LndNode peer, string identity, CancellationToken ct)
    {
        var invoice = await client.CreateInvoiceAsync(LightningMoney.MilliSatoshis(10_000_123), "actual hosted daemon", null, ct);
        Assert.NotNull(invoice.Invoice.Bolt11);
        var decoded = await peer.Lightning.DecodePayReqAsync(new PayReqString { PayReq = invoice.Invoice.Bolt11 }, cancellationToken: ct);
        Assert.Equal(identity, decoded.Destination);
        Assert.Equal(10_000_123, decoded.NumMsat);
    }

    private static HostedDaemonLaunch Configure(HostedNativeSignerSupervisor.HostedSigner signer,
                                                RemoteSignerConnection connection, ITopologyChain chain)
    {
        var directory = Path.Combine(signer.DirectoryPath, "node");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var port = ReservePort();
        var database = Path.Combine(directory, "node.db");
        var context = connection.Context;
        var enrollment = new HostedNativeNodeEnrollment(
            new HostedSignerEnrollment(context, signer.StatePath, signer.SocketPath, Path.Combine(signer.DirectoryPath, "token")),
            database, NodeUtils.GetCookieFilePath(directory), NodeUtils.GetNamedPipeFilePath(directory),
            [new HostedPeerListener("127.0.0.1", port)]);
        var configuration = new
        {
            Daemon = false,
            Node = new
            {
                Network = context.Network,
                Daemon = false,
                ListenAddresses = new[] { $"127.0.0.1:{port}" },
                FeeUpdates = new { Enabled = false },
                Bootstrap = new { Enabled = false }
            },
            Signing = new
            {
                Mode = "RemoteNative",
                context.NodeId,
                context.OwnerId,
                context.SignerId,
                ExpectedNodePublicKey = context.NodePublicKey.ToString(),
                SocketPath = signer.SocketPath,
                AuthTokenFile = Path.Combine(signer.DirectoryPath, "token")
            },
            Database = new { Provider = "Sqlite", ConnectionString = $"Data Source={database}", RunMigrations = true },
            Bitcoin = new
            {
                RpcEndpoint = $"http://{chain.RpcHost}:{chain.RpcPort}",
                chain.RpcUser,
                chain.RpcPassword,
                Notifications = "Poll",
                PollInterval = "00:00:01",
                TipPollInterval = "00:00:01"
            },
            FeeEstimation = new { Source = "Fixed", FixedFeeRatePerKw = 2_500, CacheFile = Path.Combine(directory, "fees.json") },
            Accounting = new { Prices = new { Source = "None" } },
            Gossip = new { Enabled = false, SyncEnabled = false, RelayEnabled = false }
        };
        var path = Path.Combine(directory, "appsettings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(configuration));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new HostedDaemonLaunch(enrollment, path, Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet")),
                                      typeof(IClientCommandHandler<,>).Assembly.Location);
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}