using System.Diagnostics;
using System.Globalization;

namespace NLightning.Testing.Cluster.Tests.Live.Faults;

using Cluster.Faults;
using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Run;
using static FaultClusterTestSupport;

/// <summary>
/// The fault injector on two regtest bitcoinds (Polar's 29.0 image, the LND fixture's chain): the chain survives a
/// restart and a crash on the PVC, a paused node stops answering RPC, and a partition holds blocks back once the
/// established P2P connection is dropped. Explicit, <c>Category=Cluster</c>.
/// </summary>
[Trait("Category", "Cluster")]
public class BitcoindFaultClusterTests
{
    private const string DataDir = "/home/bitcoin/.bitcoin";

    // BIP 173's regtest P2WPKH test vector: a valid address with no wallet needed
    private const string MiningAddress = "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080";

    private static readonly string[] s_cli =
        ["bitcoin-cli", "-regtest", "-rpcuser=nltg", "-rpcpassword=nltg"];

    private static NodeWorkload Bitcoind(string name, params string[] extraArgs)
    {
        var workload = new NodeWorkload(name, NodeKind.BitcoinCore, ImageVersions.BitcoinCore)
        {
            Data = new DataVolume(DataDir, "1Gi"),
            ReadinessProbe = Probes.Exec([.. s_cli, "getblockchaininfo"], periodSeconds: 2, timeoutSeconds: 6),
            TerminationGracePeriodSeconds = 30
        };
        foreach (var arg in (string[])
                 [
                     "bitcoind", "-regtest", "-server", "-printtoconsole", "-rpcuser=nltg", "-rpcpassword=nltg",
                     "-fallbackfee=0.0002", "-dnsseed=0", "-listenonion=0", "-disablewallet", .. extraArgs
                 ])
            workload.Args.Add(arg);
        workload.Ports.Add(new WorkloadPort("p2p", 18444));
        return workload.WithProcessFaults();
    }

    private static async Task<ExecResult> CliAsync(INodeHandle node, CancellationToken ct, params string[] args) =>
        await node.ExecAsync([.. s_cli, .. args], ct);

    private static async Task<string> CliTextAsync(INodeHandle node, CancellationToken ct, params string[] args) =>
        (await CliAsync(node, ct, args)).EnsureSuccess($"{node} bitcoin-cli {string.Join(' ', args)}").StdOutText
                                         .Trim();

    private static async Task<int> HeightAsync(INodeHandle node, CancellationToken ct) =>
        int.Parse(await CliTextAsync(node, ct, "getblockcount"), CultureInfo.InvariantCulture);

    private static async Task<int> ConnectionsAsync(INodeHandle node, CancellationToken ct) =>
        int.Parse(await CliTextAsync(node, ct, "getconnectioncount"), CultureInfo.InvariantCulture);

    private static Task<string> MineAsync(INodeHandle node, int blocks, CancellationToken ct) =>
        CliTextAsync(node, ct, "generatetoaddress", blocks.ToString(CultureInfo.InvariantCulture), MiningAddress);

    /// <summary>Asks the peer to connect to the miner until the miner has a connection.</summary>
    private static Task<TimeSpan> ReconnectAsync(INodeHandle peer, INodeHandle miner, string what,
                                                 CancellationToken ct) =>
        EventuallyAsync(async () =>
        {
            if (await ConnectionsAsync(miner, ct) > 0)
                return true;

            // Synchronous: it waits for the connect (blocked by a partition, or aimed at a stale address)
            await CliAsync(peer, ct, "-rpcclienttimeout=15", "addnode", "miner:18444", "onetry");
            return false;
        }, TimeSpan.FromSeconds(90), what, ct);

    [Fact(Explicit = true)]
    public async Task Given_TwoBitcoinds_When_Faulted_Then_TheChainSurvivesAndThePartitionHoldsBlocksBack()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(Options("faults-bitcoind"), ct);
        await using var faults = run.CreateFaultInjector(Log);
        var minerDeploy = run.DeployAsync(Bitcoind("miner"), ReadyTimeout, ct);
        var peer = await run.DeployAsync(Bitcoind("peer", "-addnode=miner:18444"), ReadyTimeout, ct);
        var miner = await minerDeploy;
        await ReconnectAsync(peer, miner, "peer connected to miner", ct);
        await MineAsync(miner, 101, ct);
        await EventuallyAsync(async () => await HeightAsync(peer, ct) == 101, TimeSpan.FromSeconds(30),
                              "peer at 101", ct);
        var tip = await CliTextAsync(miner, ct, "getbestblockhash");

        // Act / Assert: restart keeps the chain on the PVC
        var restart = await faults.RestartAsync(miner, ReadyTimeout, ct);
        Assert.True(restart.NewPod);
        Assert.Equal(101, await HeightAsync(miner, ct));
        Assert.Equal(tip, await CliTextAsync(miner, ct, "getbestblockhash"));

        // Act / Assert: a crash (SIGKILL) restarts it in place; the chain is still there (what was not flushed comes
        // back from disk or from the peer)
        await ReconnectAsync(peer, miner, "peer reconnected after the restart", ct);
        await MineAsync(miner, 1, ct);
        await EventuallyAsync(async () => await HeightAsync(peer, ct) == 102, TimeSpan.FromSeconds(30),
                              "peer at 102", ct);
        var crash = await faults.CrashAsync(miner, ReadyTimeout, ct);
        var heightAfterCrash = await HeightAsync(miner, ct);
        Assert.False(crash.NewPod);
        Assert.True(heightAfterCrash >= 101, $"height after crash {heightAfterCrash}");
        await ReconnectAsync(peer, miner, "peer reconnected after the crash", ct);
        await EventuallyAsync(async () => await HeightAsync(miner, ct) == 102 && await HeightAsync(peer, ct) == 102,
                              TimeSpan.FromSeconds(60), "both at 102 after the crash", ct);

        // Act / Assert: a paused bitcoind does not answer RPC; resumed, it does
        await faults.PauseAsync(miner, ct);
        var rpcWatch = Stopwatch.StartNew();
        var pausedRpc = await CliAsync(miner, ct, "-rpcclienttimeout=5", "getblockcount");
        var pausedRpcTime = rpcWatch.Elapsed;
        await faults.ResumeAsync(miner, ct);
        Assert.False(pausedRpc.Succeeded);
        Assert.True(pausedRpcTime >= TimeSpan.FromSeconds(4), $"the paused RPC failed after {pausedRpcTime}");
        Assert.Equal(102, await HeightAsync(miner, ct));

        // Act: partition the peer from the miner; the established P2P connection survives the policy
        var partition = await faults.PartitionAsync([peer], [miner], null, ct);
        await MineAsync(miner, 1, ct);
        var establishedCarried = await EventuallyAsync(async () => await HeightAsync(peer, ct) == 103,
                                                       TimeSpan.FromSeconds(30),
                                                       "block 103 over the established connection", ct);

        // Drop the connections; the partition now blocks the reconnect
        // (bitcoind drops its connections on its next socket loop, so wait before turning the network back on)
        await CliTextAsync(peer, ct, "setnetworkactive", "false");
        await EventuallyAsync(async () => await ConnectionsAsync(peer, ct) == 0, TimeSpan.FromSeconds(30),
                              "the peer without connections", ct);
        await CliTextAsync(peer, ct, "setnetworkactive", "true");
        await EventuallyAsync(async () => await ConnectionsAsync(miner, ct) == 0, TimeSpan.FromSeconds(30),
                              "the miner without peers", ct);
        var blockedAddnode = await CliAsync(peer, ct, "-rpcclienttimeout=15", "addnode", "miner:18444", "onetry");
        await MineAsync(miner, 5, ct);
        await Task.Delay(TimeSpan.FromSeconds(8), ct);
        var peerHeightPartitioned = await HeightAsync(peer, ct);
        var peerConnectionsPartitioned = await ConnectionsAsync(peer, ct);

        // Heal and reconnect
        await faults.HealAsync(partition, ct);
        var reconnected = await ReconnectAsync(peer, miner, "peer reconnected after the heal", ct);
        var caughtUp = await EventuallyAsync(async () => await HeightAsync(peer, ct) == 108, TimeSpan.FromSeconds(60),
                                             "peer caught up after the heal", ct);

        // Assert
        Assert.Equal(103, peerHeightPartitioned);
        Assert.Equal(0, peerConnectionsPartitioned);
        Assert.Equal(108, await HeightAsync(miner, ct));
        Assert.Equal([FaultKind.Restart, FaultKind.Crash, FaultKind.Pause, FaultKind.Resume, FaultKind.Partition,
                      FaultKind.Heal], faults.Events.Select(e => e.Kind));
        Log($"{run.Namespace}: restart {restart.Duration.TotalSeconds:F1} s, crash {crash.Duration.TotalSeconds:F1} s "
          + $"(height right after: {heightAfterCrash}), paused RPC failed after {pausedRpcTime.TotalSeconds:F1} s, "
          + $"block over the established connection after {establishedCarried.TotalSeconds:F1} s, peer held at "
          + $"{peerHeightPartitioned} for 8 s while the miner was at 108 (addnode while split: exit "
          + $"{blockedAddnode.ExitCode} {blockedAddnode.StdErrText.Trim()}), reconnected "
          + $"{reconnected.TotalSeconds:F1} s and caught up "
          + $"{caughtUp.TotalSeconds:F1} s after the heal");
        foreach (var fault in faults.Events)
            Log($"  {fault}");
    }
}