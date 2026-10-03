using System.Diagnostics;

namespace NLightning.Integration.Tests.Cluster.Live;

using Infrastructure.Bitcoin.Wallet;
using Testing.Cluster.Diagnostics;
using Testing.Cluster.Faults;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes.BitcoinCore;
using Testing.Cluster.Nodes.Cln;
using Testing.Cluster.Reach;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>
/// Our chain monitor losing bitcoind's ZMQ feeds while RPC keeps working (test harness phase 4, new coverage; the
/// end-to-end proof of the tip poll, NL-775): bitcoind restarts in place behind a policy that leaves only its RPC and
/// P2P ports open, so our ZMQ subscriber cannot come back. The node follows the chain over RPC alone (the tip poll
/// catches up), opens a channel to CLN and sees it confirm, and pays over it; after the heal the subscriber reconnects
/// and new blocks arrive over ZMQ again.
/// </summary>
/// <remarks>
/// Explicit and <c>Category=Cluster</c>: <c>scripts/run-cluster.sh -n 1 -p integration --class
/// NLightning.Integration.Tests.Cluster.Live.ChainMonitorZmqClusterTests</c>. The node's tip poll runs every
/// <see cref="TipPollInterval"/> here (1 s in the other cluster tests): a block processed well within it came over ZMQ,
/// and <see cref="BlockchainMonitorService.TipPollCatchUps"/> counts the catch-ups.
/// </remarks>
[Trait("Category", "Cluster")]
public class ChainMonitorZmqClusterTests
{
    private const long WalletSat = 2_000_000;
    private const long CapacitySat = 500_000;
    private const long PaymentMsat = 20_000_000;

    /// <summary>The node's tip poll interval in this test.</summary>
    private static readonly TimeSpan s_tipPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>A block processed within this came over ZMQ: the poll catches up only after two polls behind.</summary>
    private static readonly TimeSpan s_zmqDelivery = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_poll = TimeSpan.FromMilliseconds(100);

    public static TimeSpan TipPollInterval => s_tipPollInterval;

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_BitcoindRestartedWithItsZmqPortsCut_When_BlocksAreMined_Then_OurNodeFollowsOverRpcAndZmqResumesAfterTheHeal()
    {
        // Arrange: bitcoind + our node (tip poll every 5 s) + CLN, our wallet funded, no channel yet
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("zmq-loss") with
        {
            Quota = NamespaceQuota.Spike,
            Log = Log
        }, ct);
        try
        {
            await using var inProcess = new InProcessNodeDeployer();
            var builder = new TopologyBuilder
            {
                Log = Log,
                Storage = NodeStorage.Ephemeral,
                ReadyTimeout = TimeSpan.FromMinutes(4),
                StepTimeout = s_stepTimeout
            };
            using var topology = await builder.AddBitcoinCore("miner")
                                              .AddNLightning("nltg", $"Bitcoin:TipPollInterval={s_tipPollInterval:c}")
                                              .AddCln("cln")
                                              .UseInProcessNodes(inProcess)
                                              .FundWallet("nltg", WalletSat)
                                              .BuildAsync(run, ct);
            await using var faults = run.CreateFaultInjector(Log);
            await run.CaptureOnFailureAsync("zmq-loss", async () =>
            {
                var nltg = topology.InProcessNode("nltg");
                var cln = topology.Node<ClnTestPeer>("cln");
                var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
                var miner = chain.Node;
                var monitor = Assert.IsType<BlockchainMonitorService>(nltg.TestNode.BlockchainMonitor);
                Log($"{run.Namespace}: topology in {watch.Elapsed.TotalSeconds:F1} s");

                // Act/Assert: the baseline, a block over ZMQ
                await WaitZmqSubscribedAsync(miner, ct);
                var catchUpsBefore = monitor.TipPollCatchUps;
                var overZmq = await MineAndTimeAsync(topology, nltg, ct);
                Assert.True(overZmq < s_zmqDelivery, $"the baseline block took {overZmq} (not over ZMQ?)");
                Assert.Equal(catchUpsBefore, monitor.TipPollCatchUps);

                // Act: only RPC and P2P stay open, then bitcoind restarts in place (same pod IP, same chain) and every
                // connection to it drops; our ZMQ subscriber cannot come back
                var cut = await faults.LimitIngressPortsAsync(miner, [BitcoinCorePorts.Rpc, BitcoinCorePorts.P2p], ct);
                var restart = await faults.RestartInPlaceAsync(
                                  miner, BitcoinCoreWorkload.CliCommand(chain.Bitcoin.Options, null, "stop"),
                                  TimeSpan.FromMinutes(2), ct);
                var subscribersWhileCut = await TcpConnectionTable.CountEstablishedAsync(
                                              miner, BitcoinCorePorts.ZmqRawBlock, ct);

                // Assert: blocks still arrive, through the tip poll
                var overRpc = await MineAndTimeAsync(topology, nltg, ct, 3);
                var catchUpsWhileCut = monitor.TipPollCatchUps - catchUpsBefore;

                // Act/Assert: a channel opened and confirmed while ZMQ is down, and a payment over it
                var clnAddress = await cln.GetAddressAsync(ct);
                await TopologyDeployer.ConnectAsync(nltg, clnAddress, s_stepTimeout, ct);
                var open = await nltg.OpenChannelAsync(
                               new Testing.Cluster.Nodes.TestOpenChannelRequest(clnAddress.NodeId, CapacitySat), ct);
                await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
                await TopologyDeployer.WaitChannelActiveAsync(nltg, clnAddress.NodeId, open.FundingTxId, s_stepTimeout,
                                                              ct);
                await TopologyDeployer.WaitChannelActiveAsync(cln, nltg.TestNode.NodeIdHex, open.FundingTxId,
                                                              s_stepTimeout, ct);
                var invoice = await cln.CreateInvoiceAsync(PaymentMsat, "paid while ZMQ is down", ct);
                var paid = await nltg.PayInvoiceAsync(invoice.Bolt11, ct);
                var subscribersAfterOpen = await TcpConnectionTable.CountEstablishedAsync(
                                               miner, BitcoinCorePorts.ZmqRawBlock, ct);
                var catchUpsCut = monitor.TipPollCatchUps - catchUpsBefore;

                // Act: heal; the subscriber reconnects by itself
                await faults.HealAsync(cut, ct);
                var heal = Stopwatch.StartNew();
                await WaitZmqSubscribedAsync(miner, ct);
                var resubscribed = heal.Elapsed;
                var catchUpsAfterHeal = monitor.TipPollCatchUps;
                var afterHeal = await MineAndTimeAsync(topology, nltg, ct);

                // Assert
                Assert.False(restart.NewPod);
                Assert.Equal(restart.PodIpBefore, restart.PodIpAfter);
                Assert.Equal(0, subscribersWhileCut);
                Assert.Equal(0, subscribersAfterOpen);
                Assert.True(catchUpsWhileCut >= 1, $"no tip poll catch-up while ZMQ was cut ({catchUpsWhileCut})");
                Assert.True(paid.Succeeded, paid.FailureReason);
                Assert.True(afterHeal < s_zmqDelivery, $"the block after the heal took {afterHeal} (ZMQ not back?)");
                Assert.Equal(catchUpsAfterHeal, monitor.TipPollCatchUps);

                Log($"{run.Namespace}: baseline block over ZMQ in {overZmq.TotalMilliseconds:F0} ms; bitcoind restarted "
                  + $"in place in {restart.Duration.TotalSeconds:F1} s (restarts {restart.RestartCountBefore} -> "
                  + $"{restart.RestartCountAfter}); 3 blocks over RPC in {overRpc.TotalSeconds:F1} s "
                  + $"({catchUpsWhileCut} catch-up(s)); channel opened, confirmed and paid with ZMQ down "
                  + $"({catchUpsCut} catch-ups in all); ZMQ back {resubscribed.TotalSeconds:F1} s after the heal, next "
                  + $"block in {afterHeal.TotalMilliseconds:F0} ms");
                foreach (var fault in faults.Events)
                    Log($"  {fault}");
            });
        }
        finally
        {
            await run.DisposeAsync();
        }
    }

    /// <summary>Mines <paramref name="blocks"/> blocks and returns how long our node took to process the tip.</summary>
    private static async Task<TimeSpan> MineAndTimeAsync(TestTopology topology, InProcessNode nltg,
                                                         CancellationToken ct, int blocks = 1)
    {
        await topology.Chain.MineAsync(blocks, ct);
        var tip = await topology.Chain.GetBlockCountAsync(ct);
        var watch = Stopwatch.StartNew();
        await ClusterPoll.UntilAsync(async c => await nltg.GetBlockHeightAsync(c) >= tip, s_stepTimeout, s_poll,
                                     $"our node at {tip}", ct);
        return watch.Elapsed;
    }

    /// <summary>Until bitcoind holds an established connection on its raw-block ZMQ port (our subscriber).</summary>
    private static Task WaitZmqSubscribedAsync(Testing.Cluster.Nodes.INodeHandle miner, CancellationToken ct) =>
        ClusterPoll.UntilAsync(async c => await TcpConnectionTable.CountEstablishedAsync(
                                              miner, BitcoinCorePorts.ZmqRawBlock, c) > 0,
                               s_stepTimeout, TimeSpan.FromMilliseconds(250), "a ZMQ subscriber on bitcoind", ct);
}