using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Cluster.Live;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.Cln;
using Testing.Cluster.Nodes.Lnd;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;
using Testing.Cluster.Topology.Lnd;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>
/// Our in-process node inside a cluster topology (test harness phase 2, lane A): bitcoind and one CLN or LND pod in a
/// run namespace, our <see cref="InProcessNode"/> in the test process on the same chain. Our node dials the pod (pods
/// reach us at <c>host.orb.internal</c> but show up as 127.0.0.1, NL-497), opens, pays and is paid, and closes
/// cooperatively; the LND proof also restarts our node in between.
/// </summary>
/// <remarks>
/// Explicit and <c>Category=Cluster</c>: they need a cluster (OrbStack's locally). Run them with
/// <c>scripts/run-cluster.sh --project integration --class NLightning.Integration.Tests.Cluster.Live.InProcessNodeClusterTests</c>
/// (<c>-n 3</c> for three runs at once), or the built test assembly with
/// <c>-explicit only -trait Category=Cluster</c>. Not under the <c>Docker</c> namespace, so the Docker filters and
/// runner scripts never pick them up, and they never need the Docker lock.
/// </remarks>
[Trait("Category", "Cluster")]
public class InProcessNodeClusterTests
{
    private const long WalletSat = 2_000_000;
    private const long CapacitySat = 1_000_000;
    private const long PushMsat = 300_000_000;
    private const long ToPeerMsat = 200_000_000;
    private const long ToUsMsat = 50_000_000;

    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromMinutes(2);

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>
    /// bitcoind + CLN (with <c>--experimental-dual-fund</c>) + our node: we dial CLN at its stable ClusterIP name, open a
    /// <b>dual-funded</b> channel (v2, our contribution only), pay CLN, CLN pays us back, and we close cooperatively:
    /// the same closing transaction confirms and both ends see the channel closed.
    /// </summary>
    /// <remarks>
    /// Runs with the default features: on a dual-funded channel CLN v26.06.8 sets a P2TR <c>close_to</c> script (in
    /// <c>accept_channel2</c> and <c>shutdown</c>), a form BOLT 2 allows only with <c>option_shutdown_anysegwit</c>,
    /// which we advertise by default since NL-776 (before, our node refused CLN's <c>shutdown</c> and the close
    /// stalled in <c>ShuttingDown</c> / <c>CLOSINGD_SIGEXCHANGE</c>).
    /// </remarks>
    [Fact(Explicit = true)]
    public async Task Given_OurNodeAndAClnPod_When_WeOpenDualFundedPayBothWaysAndClose_Then_BothEndsAgree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var run = await TestRun.StartAsync(Options("nltg-cln"), ct);
        var ns = run.Namespace;
        try
        {
            await using var inProcess = new InProcessNodeDeployer();
            using var topology = await Builder(inProcess)
                                      .AddCln("cln", extraArgs: ["--experimental-dual-fund"])
                                      .BuildAsync(run, ct);
            var built = watch.Elapsed;
            var nltg = topology.InProcessNode("nltg");
            var cln = topology.Node<ClnTestPeer>("cln");
            var clnId = await cln.GetNodeIdAsync(ct);
            var clnAddress = await cln.GetAddressAsync(ct);

            // Act: connect (we dial), open v2, confirm
            await TopologyDeployer.ConnectAsync(nltg, clnAddress, s_stepTimeout, ct);
            var open = await nltg.OpenChannelAsync(new TestOpenChannelRequest(clnId, CapacitySat),
                                                   InProcessOpenMode.DualFund, ct);
            await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
            await WaitActiveBothEndsAsync(nltg, cln, open.Open.FundingTxId, ct);
            var opened = watch.Elapsed;

            // Act: pay both ways (CLN has nothing on its side until we pay it: a v2 open has no push)
            var (_, toCln, toClnAttempts) = await LndPairTopology.PayAsync(nltg, cln, ToPeerMsat, s_stepTimeout, ct);
            var (_, toUs, toUsAttempts) = await LndPairTopology.PayAsync(cln, nltg, ToUsMsat, s_stepTimeout, ct);
            var paid = watch.Elapsed;

            // Assert: a v2 channel, each payment settled, the balances moved
            Assert.Equal(ChannelVersion.V2, ChannelVersionOf(nltg, open.ChannelId));
            Assert.True(toCln.Succeeded, toCln.FailureReason);
            Assert.True(toUs.Succeeded, toUs.FailureReason);
            var ours = await WaitLocalBalanceAsync(nltg, open.Open.FundingTxId, CapacitySat * 1000 - ToPeerMsat + ToUsMsat,
                                                   ct);
            Assert.Equal(clnId, ours.RemoteNodeId);
            var theirs = await WaitLocalBalanceAsync(cln, open.Open.FundingTxId, ToPeerMsat - ToUsMsat, ct);
            Assert.Equal(ours.ShortChannelId, theirs.ShortChannelId);

            // Act: close cooperatively, confirm
            var closingTxId = await nltg.CloseChannelAsync(open.ChannelId, ct);
            await MineClosingAsync(topology, closingTxId, ct);

            // Assert: closed on both ends
            await ClusterPoll.UntilDoneAsync(async c =>
            {
                var channel = await nltg.FindChannelAsync(open.Open.FundingTxId, c);
                return channel is null || channel.State == ChannelState.Closed ? null : $"ours {channel.State}";
            }, s_stepTimeout, "our channel closed", ct);
            await ClusterPoll.UntilDoneAsync(async c =>
            {
                var channels = await cln.Rpc.CallAsync("listpeerchannels", c);
                var channel = channels["channels"]?.AsArray()
                                                  .FirstOrDefault(x => x?["funding_txid"]?.GetValue<string>()
                                                                    == open.Open.FundingTxId);
                var state = channel?["state"]?.GetValue<string>();
                return channel is null || state is "ONCHAIN" or "CLOSED" ? null : $"CLN {state}";
            }, s_stepTimeout, "CLN sees the mutual close on chain", ct);

            Log($"{ns}: built in {built.TotalSeconds:F1} s, v2 channel active at {opened.TotalSeconds:F1} s "
              + $"(scid {ours.ShortChannelId}), paid CLN in {toClnAttempts} and CLN paid us in {toUsAttempts} "
              + $"attempt(s) by {paid.TotalSeconds:F1} s, closed by {closingTxId} at {watch.Elapsed.TotalSeconds:F1} s");
        }
        finally
        {
            await DisposeRunAsync(run, watch);
        }
    }

    /// <summary>
    /// bitcoind + LND + our node: the topology itself opens our v1 channel to LND (with a push, so LND can pay at
    /// once); we pay LND, LND pays us, our node restarts (same key, database and port) and redials LND by its Service
    /// name, LND pays us again, and we close cooperatively.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Given_OurNodeAndAnLndPod_When_WePayBothWaysRestartAndClose_Then_BothEndsAgree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var run = await TestRun.StartAsync(Options("nltg-lnd"), ct);
        var ns = run.Namespace;
        try
        {
            await using var inProcess = new InProcessNodeDeployer();
            using var topology = await Builder(inProcess)
                                      .AddLnd("lnd")
                                      .FundWallet("lnd", WalletSat)
                                      .AddChannel("nltg", "lnd", CapacitySat, PushMsat)
                                      .BuildAsync(run, ct);
            var built = watch.Elapsed;
            var nltg = topology.InProcessNode("nltg");
            var lnd = topology.Node<LndNode>("lnd");
            var channel = Assert.Single(topology.Channels);
            var fundingTxId = channel.Open.FundingTxId;

            // Act: pay both ways (LND may need a retry while its router learns the fresh channel, NL-319)
            var (_, toLnd, toLndAttempts) = await LndPairTopology.PayAsync(nltg, lnd, ToPeerMsat, s_stepTimeout, ct);
            var (_, toUs, toUsAttempts) = await LndPairTopology.PayAsync(lnd, nltg, ToUsMsat, s_stepTimeout, ct);
            var paid = watch.Elapsed;

            // Act: restart our node; it redials LND (stored as lnd.<ns>.svc.cluster.local) and reestablishes
            var restart = Stopwatch.StartNew();
            await nltg.RestartAsync(ct);
            await topology.WaitChannelsActiveAsync(s_stepTimeout, ct);
            var restartedIn = restart.Elapsed;
            var (_, afterRestart, _) = await LndPairTopology.PayAsync(lnd, nltg, ToUsMsat, s_stepTimeout, ct);

            // Assert: v1 with the push, every payment settled, the balances moved
            var ourChannel = await nltg.FindChannelAsync(fundingTxId, ct);
            Assert.NotNull(ourChannel);
            Assert.Equal(ChannelVersion.V1, ChannelVersionOf(nltg, ourChannel.ChannelId));
            Assert.True(toLnd.Succeeded, toLnd.FailureReason);
            Assert.True(toUs.Succeeded, toUs.FailureReason);
            Assert.True(afterRestart.Succeeded, afterRestart.FailureReason);
            await WaitLocalBalanceAsync(nltg, fundingTxId, CapacitySat * 1000 - PushMsat - ToPeerMsat + 2 * ToUsMsat, ct);
            await WaitLocalBalanceAsync(lnd, fundingTxId, PushMsat + ToPeerMsat - 2 * ToUsMsat, ct);
            Assert.NotNull(channel.ShortChannelId);
            Assert.Equal(channel.ShortChannelId, ourChannel.ShortChannelId?.ToString());

            // Act: close cooperatively, confirm
            var closingTxId = await nltg.CloseChannelAsync(ourChannel.ChannelId, ct);
            await MineClosingAsync(topology, closingTxId, ct);

            // Assert: closed on both ends (LND lists it among its closed channels with our closing transaction)
            await ClusterPoll.UntilDoneAsync(async c =>
            {
                var ours = await nltg.FindChannelAsync(fundingTxId, c);
                return ours is null || ours.State == ChannelState.Closed ? null : $"ours {ours.State}";
            }, s_stepTimeout, "our channel closed", ct);
            var closed = await ClusterPoll.ForAsync(async c =>
            {
                var response = await lnd.Lightning.ClosedChannelsAsync(new ClosedChannelsRequest(),
                                                                       cancellationToken: c).ResponseAsync;
                return response.Channels.FirstOrDefault(x => x.ChannelPoint.StartsWith(fundingTxId,
                                                                                        StringComparison.Ordinal));
            }, s_stepTimeout, TimeSpan.FromMilliseconds(500), "LND lists the channel closed", ct);
            Assert.Equal(closingTxId, closed.ClosingTxHash);
            Assert.Equal(ChannelCloseSummary.Types.ClosureType.CooperativeClose, closed.CloseType);

            Log($"{ns}: built in {built.TotalSeconds:F1} s (v1 channel {channel.ShortChannelId}), paid LND in "
              + $"{toLndAttempts} and LND paid us in {toUsAttempts} attempt(s) by {paid.TotalSeconds:F1} s, restart to "
              + $"channel active {restartedIn.TotalSeconds:F1} s, closed by {closingTxId} at "
              + $"{watch.Elapsed.TotalSeconds:F1} s");
        }
        finally
        {
            await DisposeRunAsync(run, watch);
        }
    }

    private static TestRunOptions Options(string suite) =>
        TestRunOptions.FromEnvironment(suite) with { Quota = NamespaceQuota.Spike, Log = Log };

    /// <summary>bitcoind <c>miner</c> and our funded node <c>nltg</c>; the test adds the pod peer.</summary>
    private static TopologyBuilder Builder(InProcessNodeDeployer inProcess) =>
        new TopologyBuilder { Log = Log, ReadyTimeout = TimeSpan.FromMinutes(4), StepTimeout = s_stepTimeout }
           .AddBitcoinCore("miner")
           .AddNLightning("nltg")
           .UseInProcessNodes(inProcess)
           .FundWallet("nltg", WalletSat);

    private static async Task WaitActiveBothEndsAsync(ITopologyLightningNode a, ITopologyLightningNode b,
                                                      string fundingTxId, CancellationToken ct)
    {
        var aId = await a.GetNodeIdAsync(ct);
        var bId = await b.GetNodeIdAsync(ct);
        await TopologyDeployer.WaitChannelActiveAsync(a, bId, fundingTxId, s_stepTimeout, ct);
        await TopologyDeployer.WaitChannelActiveAsync(b, aId, fundingTxId, s_stepTimeout, ct);
    }

    /// <summary>
    /// Waits until <paramref name="node"/>'s side of the channel holds <paramref name="expectedMsat"/>: a payer has the
    /// preimage before the commitment dance that moves the balances is over.
    /// </summary>
    private static Task<TestChannel> WaitLocalBalanceAsync(ILightningTestPeer node, string fundingTxId,
                                                           long expectedMsat, CancellationToken ct) =>
        ClusterPoll.ForAsync(async c =>
        {
            var channel = (await node.ListChannelsAsync(c)).FirstOrDefault(x => x.FundingTxId == fundingTxId);
            return channel?.LocalBalanceMsat == expectedMsat ? channel : null;
        }, s_stepTimeout, TimeSpan.FromMilliseconds(250), $"{node.Alias}'s balance {expectedMsat} msat", ct);

    /// <summary>Waits until the closing transaction is in bitcoind's mempool, then buries it 6 deep.</summary>
    private static async Task MineClosingAsync(TestTopology topology, string closingTxId, CancellationToken ct)
    {
        var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
        await chain.Chain.WaitForMempoolAsync(closingTxId, ct, s_stepTimeout);
        await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
    }

    private static ChannelVersion ChannelVersionOf(InProcessNode node, ChannelId channelId) =>
        node.TestNode.Services.GetRequiredService<IChannelMemoryRepository>().TryGetChannel(channelId, out var channel)
            ? channel.Version
            : throw new InvalidOperationException($"{node.Alias} has no channel {channelId} in memory");

    private static async Task DisposeRunAsync(TestRun run, Stopwatch watch)
    {
        var ns = run.Namespace;
        watch.Restart();
        await run.DisposeAsync();
        Log($"{ns}: disposed in {watch.Elapsed.TotalSeconds:F1} s (the namespace terminates in the background)");
    }
}