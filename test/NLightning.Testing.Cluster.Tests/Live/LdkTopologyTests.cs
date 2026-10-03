using System.Diagnostics;

namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes.Cln;
using Cluster.Nodes.Ldk;
using Cluster.Run;
using Cluster.Topology;
using Cluster.Topology.Lnd;

/// <summary>
/// The ldk-server node on a real cluster (test harness phase 4): a topology with bitcoind 31.1 (the LDK fixture's
/// chain), an LDK node (the local <c>nltg-ldk-server</c> image, on a PVC, announcing its stable ClusterIP) and a CLN
/// node; LDK opens a channel to CLN, payments flow both ways through the facade, then LDK restarts on its PVC (same node
/// id), reconnects to CLN through its persisted peer, and pays again. Explicit (see <see cref="ClusterSmokeTests"/>).
/// </summary>
[Trait("Category", "Cluster")]
public class LdkTopologyTests
{
    private const long ChannelCapacitySat = 1_000_000;
    private const long PushMsat = 100_000_000;
    private const long PaymentMsat = 20_000_000;

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_AnLdkAndAClnNode_When_LdkOpensAndRestarts_Then_PaymentsFlowBothWaysBeforeAndAfter()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var options = TestRunOptions.FromEnvironment("ldk-topology") with { Quota = NamespaceQuota.Spike, Log = Log };
        var run = await TestRun.StartAsync(options, ct);
        var ns = run.Namespace;
        try
        {
            // Act: build (bitcoind, LDK and CLN, LDK's channel to CLN with a push)
            using var topology = await new TopologyBuilder { Log = Log, ReadyTimeout = TimeSpan.FromMinutes(4) }
                                      .AddBitcoinCore("miner", ImageVersions.BitcoinCore31, storage: NodeStorage.Ephemeral)
                                      .AddLdk("ldk", extraArgs: ["alias=nltg-ldk"])
                                      .AddCln("cln", storage: NodeStorage.Ephemeral)
                                      .FundWallet("ldk", 2_000_000)
                                      .AddChannel("ldk", "cln", ChannelCapacitySat, PushMsat)
                                      .BuildAsync(run, ct);
            var builtAfter = watch.Elapsed;
            var ldk = topology.Node<LdkTestPeer>("ldk");
            var cln = topology.Node<ClnTestPeer>("cln");
            var ldkId = await ldk.GetNodeIdAsync(ct);
            var clnId = await cln.GetNodeIdAsync(ct);
            var info = await ldk.Rpc.GetNodeInfoAsync(ct);
            Log($"{ns}: LDK get-node-info {info.ToJsonString()}");
            Log($"{ns}: LDK list-channels {(await ldk.Rpc.RunAsync("list-channels", ct)).ToJsonString()}");
            Log($"{ns}: LDK list-peers {(await ldk.Rpc.RunAsync("list-peers", ct)).ToJsonString()}");

            // Act: pay both ways
            var (_, toCln, _) = await LndPairTopology.PayAsync(ldk, cln, PaymentMsat, TimeSpan.FromMinutes(1), ct);
            var (_, toLdk, _) = await LndPairTopology.PayAsync(cln, ldk, PaymentMsat, TimeSpan.FromMinutes(1), ct);

            // Act: restart LDK on its PVC
            watch.Restart();
            await ldk.Node.RestartAsync(TimeSpan.FromMinutes(3), ct);
            var restartedAfter = watch.Elapsed;
            Assert.Equal(ldkId, (await ldk.Rpc.GetNodeInfoAsync(ct))["node_id"]!.GetValue<string>());
            var channel = Assert.Single(topology.Channels);
            await TopologyDeployer.WaitChannelActiveAsync(ldk, clnId, channel.Open.FundingTxId, TimeSpan.FromMinutes(2),
                                                          ct);
            await TopologyDeployer.WaitChannelActiveAsync(cln, ldkId, channel.Open.FundingTxId, TimeSpan.FromMinutes(2),
                                                          ct);
            var (_, again, _) = await LndPairTopology.PayAsync(cln, ldk, PaymentMsat, TimeSpan.FromMinutes(1), ct);
            var tip = await topology.MineAndSyncAsync(1, ct);

            // Assert
            Assert.True(toCln.Succeeded, toCln.FailureReason);
            Assert.True(toLdk.Succeeded, toLdk.FailureReason);
            Assert.True(again.Succeeded, again.FailureReason);
            Assert.NotNull(channel.ShortChannelId);
            Assert.Equal(tip, await ldk.GetBlockHeightAsync(ct));
            var ours = Assert.Single(await ldk.ListChannelsAsync(ct));
            Assert.Equal(ChannelCapacitySat, ours.CapacitySat);
            Assert.Equal(clnId, ours.RemoteNodeId);
            Assert.Equal(channel.ShortChannelId, ours.ShortChannelId);
            Assert.Equal("nltg-ldk", info["node_alias"]?.GetValue<string>());
            Assert.Equal([$"{ldk.P2PHost}:9735"], info["announcement_addresses"]!.AsArray().Select(a => a!.GetValue<string>()));
            Log($"{ns}: LDK + CLN topology built in {builtAfter.TotalSeconds:F1} s; LDK restarted in "
              + $"{restartedAfter.TotalSeconds:F1} s; LDK at {ldk.P2PHost}; LDK balance {ours.LocalBalanceMsat} msat");
        }
        finally
        {
            watch.Restart();
            await run.DisposeAsync();
            Log($"{ns}: deletion issued in {watch.Elapsed.TotalSeconds:F1} s");
        }

        // Assert: the namespace is going (in the background)
        await RunAssertions.AssertDeletedOrTerminatingAsync(ns, ct);
    }
}