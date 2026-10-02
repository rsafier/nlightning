using System.Diagnostics;

namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Nodes.Cln;
using Cluster.Nodes.Lnd;
using Cluster.Run;
using Cluster.Topology;
using Cluster.Topology.Lnd;

/// <summary>
/// The integrated harness on a real cluster: one declarative topology with an LND and a CLN node on the shared bitcoind
/// (<see cref="BitcoinCoreTopologyChain"/>), a channel each way, payments both ways, one more block seen by both nodes,
/// and the run's namespace deleted. Explicit (see <see cref="ClusterSmokeTests"/> for how to run them).
/// </summary>
[Trait("Category", "Cluster")]
public class MixedTopologyTests
{
    private const long ChannelCapacitySat = 1_000_000;
    private const long PushMsat = 100_000_000;
    private const long PaymentMsat = 20_000_000;

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_AnLndAndAClnNode_When_TheTopologyIsBuilt_Then_EachPaysTheOtherOverItsOwnChannel()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var options = TestRunOptions.FromEnvironment("mixed") with { Quota = NamespaceQuota.Spike, Log = Log };
        var run = await TestRun.StartAsync(options, ct);
        var ns = run.Namespace;
        try
        {
            // Act: build (bitcoind, LND alice and CLN bob, a channel each way)
            using var topology = await new TopologyBuilder { Log = Log, ReadyTimeout = TimeSpan.FromMinutes(4) }
                                      .AddBitcoinCore("miner")
                                      .AddLnd("alice")
                                      .AddCln("bob")
                                      .FundWallet("alice", 2_000_000)
                                      .FundWallet("bob", 2_000_000)
                                      .AddChannel("alice", "bob", ChannelCapacitySat, PushMsat)
                                      .AddChannel("bob", "alice", ChannelCapacitySat, PushMsat)
                                      .BuildAsync(run, ct);
            var builtAfter = watch.Elapsed;
            var alice = topology.Node<LndNode>("alice");
            var bob = topology.Node<ClnTestPeer>("bob");
            var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);

            // Act: pay both ways (LND may need a retry while its router learns the fresh channel, NL-319)
            var (_, toBob, toBobAttempts) =
                await LndPairTopology.PayAsync(alice, bob, PaymentMsat, TimeSpan.FromMinutes(1), ct);
            var (_, toAlice, toAliceAttempts) =
                await LndPairTopology.PayAsync(bob, alice, PaymentMsat, TimeSpan.FromMinutes(1), ct);
            var tip = await topology.MineAndSyncAsync(1, ct);

            // Assert
            Assert.Equal(2, topology.Channels.Count);
            Assert.All(topology.Channels, c => Assert.NotNull(c.ShortChannelId));
            Assert.True(toBob.Succeeded, toBob.FailureReason);
            Assert.True(toAlice.Succeeded, toAlice.FailureReason);
            Assert.Equal("miner", chain.RpcHost);
            Assert.Equal(tip, (await chain.Chain.GetTipAsync(ct)).Height);
            Assert.Equal(tip, await alice.GetBlockHeightAsync(ct));
            Assert.Equal(tip, await bob.GetBlockHeightAsync(ct));
            Assert.Equal(2, (await alice.ListChannelsAsync(ct)).Count(c => c.Active));
            Log($"{ns}: LND + CLN topology built in {builtAfter.TotalSeconds:F1} s; alice -> bob after "
              + $"{toBobAttempts} attempt(s), bob -> alice after {toAliceAttempts} attempt(s); tip {tip}");
        }
        finally
        {
            watch.Restart();
            await run.DisposeAsync();
            Log($"{ns}: deleted in {watch.Elapsed.TotalSeconds:F1} s");
        }

        // Assert: the namespace is gone
        using var client = KubeClientFactory.Create();
        Assert.Null(await RunNamespace.TryReadAsync(client, ns, ct));
    }
}