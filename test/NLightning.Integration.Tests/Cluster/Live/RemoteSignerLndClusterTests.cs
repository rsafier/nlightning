using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Cluster.Live;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.Lnd;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;
using Testing.Cluster.Topology.Lnd;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>Live injected C# signer interoperability against an LND regtest peer.</summary>
[Trait("Category", "Cluster")]
public class RemoteSignerLndClusterTests
{
    private const long WalletSat = 2_000_000;
    private const long CapacitySat = 1_000_000;
    private const long PushMsat = 300_000_000;
    private const long ToPeerMsat = 200_000_000;
    private const long ToUsMsat = 50_000_000;

    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromMinutes(2);

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>
    /// bitcoind + LND + our node: the topology itself opens our v1 channel to LND (with a push, so LND can pay at
    /// once); we pay in both directions, restart the injected signer and node with their signing state and database,
    /// reestablish with LND, pay in both directions again, and confirm a cooperative close.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Given_AnInjectedRemoteSignerAndLnd_When_WePayBothWaysRestartBothAndClose_Then_BothEndsAgree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var run = await TestRun.StartAsync(Options("remote-signer-lnd"), ct);
        var ns = run.Namespace;
        try
        {
            await using var signer = new NLightning.RemoteSigning.Tests.SignerDaemonFixture(injected: true);
            await signer.InitializeAsync();
            using var connection = new NLightning.Infrastructure.RemoteSigning.RemoteSignerConnection(signer.Options());
            var keys = new NLightning.Infrastructure.RemoteSigning.RemoteSecureKeyManager(connection);
            await using var inProcess = new InProcessNodeDeployer
            {
                KeyManager = _ => keys,
                PodFacingHost = Environment.GetEnvironmentVariable("NLTG_ADOPT_NAMESPACE") == "1"
                    ? System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                            .First(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString()
                    : null,
                ConfigureNode = node =>
                {
                    node.RemoteSignerConnection = connection;
                    node.ExtraConfiguration["Signing:Mode"] = "RemoteNative";
                    node.ExtraConfiguration["Signing:SocketPath"] = signer.SocketPath;
                    node.ExtraConfiguration["Signing:AuthTokenFile"] = Path.Combine(signer.DirectoryPath, "token");
                }
            };
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

            // Act: restart the signer and our node; it redials LND (stored as lnd.<ns>.svc.cluster.local) and reestablishes
            var restart = Stopwatch.StartNew();
            var identity = await nltg.GetNodeIdAsync(ct);
            await nltg.TestNode.StopAsync();
            await signer.RestartAsync();
            await nltg.TestNode.StartAsync(ct);
            Assert.Equal(identity, await nltg.GetNodeIdAsync(ct));
            Assert.IsType<NLightning.Infrastructure.RemoteSigning.RemoteLightningSigner>(
                nltg.TestNode.Services.GetRequiredService<NLightning.Domain.Bitcoin.Interfaces.ILightningSigner>());
            Assert.Throws<NotSupportedException>(() => keys.GetNodeKeyPair());
            Assert.False(File.Exists(Path.Combine(signer.DirectoryPath, "node.key")));
            await topology.WaitChannelsActiveAsync(s_stepTimeout, ct);
            var restartedIn = restart.Elapsed;
            var (_, afterRestart, _) = await LndPairTopology.PayAsync(lnd, nltg, ToUsMsat, s_stepTimeout, ct);
            var (_, outgoingAfterRestart, _) = await LndPairTopology.PayAsync(nltg, lnd, ToPeerMsat, s_stepTimeout, ct);

            // Assert: v1 with the push, every payment settled, the balances moved
            var ourChannel = await nltg.FindChannelAsync(fundingTxId, ct);
            Assert.NotNull(ourChannel);
            Assert.Equal(ChannelVersion.V1, ChannelVersionOf(nltg, ourChannel.ChannelId));
            Assert.True(toLnd.Succeeded, toLnd.FailureReason);
            Assert.True(toUs.Succeeded, toUs.FailureReason);
            Assert.True(afterRestart.Succeeded, afterRestart.FailureReason);
            Assert.True(outgoingAfterRestart.Succeeded, outgoingAfterRestart.FailureReason);
            await WaitLocalBalanceAsync(nltg, fundingTxId, CapacitySat * 1000 - PushMsat - 2 * ToPeerMsat + 2 * ToUsMsat, ct);
            await WaitLocalBalanceAsync(lnd, fundingTxId, PushMsat + 2 * ToPeerMsat - 2 * ToUsMsat, ct);
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