using System.Diagnostics;

namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Kube;
using Cluster.Run;
using Cluster.Topology;
using Cluster.Topology.Lnd;

/// <summary>
/// The startup cuts measured (test harness phase 2): a pair of the same implementation on the shared bitcoind with a
/// channel, built with the Lightning nodes in a second wave (the spike's way) or in the chain's wave, on PVCs or on
/// <c>emptyDir</c>s, then a payment and the run's disposal. Each row logs its build time per step and how long the
/// disposal took to return. The CLN and LND classes run in parallel; their rows one after another.
/// <c>scripts/run-cluster.sh -n 3 --class '*StartupTiming*'</c> runs six topologies at once.
/// </summary>
public static class StartupTiming
{
    public const long CapacitySat = 1_000_000;
    public const long PaymentMsat = 20_000_000;

    public static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    public static async Task MeasureAsync(string suite, Func<TopologyBuilder, TopologyBuilder> declare,
                                          NodeStorage storage, bool withChain)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var options = TestRunOptions.FromEnvironment(suite) with { Quota = NamespaceQuota.Spike, Log = Log };
        var run = await TestRun.StartAsync(options, ct);
        var ns = run.Namespace;
        var startedAfter = watch.Elapsed;
        var variant = $"{storage.ToString().ToLowerInvariant()}, nodes {(withChain ? "with" : "after")} the chain";
        try
        {
            // Act: build
            var builder = declare(new TopologyBuilder
            {
                Log = Log,
                Storage = storage,
                DeployNodesWithChain = withChain,
                ReadyTimeout = TimeSpan.FromMinutes(4)
            });
            using var topology = await builder.BuildAsync(run, ct);
            var builtAfter = watch.Elapsed;

            // Act: pay over the channel (LND may list it before its router has the edge, NL-319)
            var (_, paid, attempts) = await LndPairTopology.PayAsync(topology.Node("alice"), topology.Node("bob"),
                                                                     PaymentMsat, TimeSpan.FromMinutes(1), ct);

            // Assert
            Assert.True(paid.Succeeded, paid.FailureReason);
            Assert.All(topology.Spec.Nodes, n => Assert.Equal(storage, n.Storage));
            var steps = string.Join(", ", topology.Timings.Select(t => $"{t.Key} {t.Value.TotalSeconds:F1} s"));
            Log($"[timing] {ns} {suite} ({variant}): namespace {startedAfter.TotalSeconds:F1} s, built in "
              + $"{builtAfter.TotalSeconds:F1} s ({steps}), paid after {attempts} attempt(s)");
        }
        finally
        {
            watch.Restart();
            await run.DisposeAsync();
            Log($"[timing] {ns} {suite} ({variant}): disposal returned in {watch.Elapsed.TotalSeconds:F1} s");
        }

        await RunAssertions.AssertDeletedOrTerminatingAsync(ns, ct);
    }
}

[Trait("Category", "Cluster")]
public class StartupTimingClnTests
{
    [Theory(Explicit = true)]
    [InlineData(NodeStorage.Persistent, false)]
    [InlineData(NodeStorage.Persistent, true)]
    [InlineData(NodeStorage.Ephemeral, true)]
    public Task Given_TheClnPair_When_BuiltWithTheStartupCuts_Then_ItPaysAndLogsItsTimings(NodeStorage storage,
                                                                                          bool withChain) =>
        StartupTiming.MeasureAsync("timing-cln", b => b.AddBitcoinCore("miner")
                                                      .AddCln("alice")
                                                      .AddCln("bob")
                                                      .FundWallet("alice", 2_000_000)
                                                      .AddChannel("alice", "bob", StartupTiming.CapacitySat),
                                   storage, withChain);
}

[Trait("Category", "Cluster")]
public class StartupTimingLndTests
{
    [Theory(Explicit = true)]
    [InlineData(NodeStorage.Persistent, false)]
    [InlineData(NodeStorage.Persistent, true)]
    [InlineData(NodeStorage.Ephemeral, true)]
    public Task Given_TheLndPair_When_BuiltWithTheStartupCuts_Then_ItPaysAndLogsItsTimings(NodeStorage storage,
                                                                                          bool withChain) =>
        StartupTiming.MeasureAsync("timing-lnd", b => b.AddBitcoinCore("miner")
                                                      .AddLnd("alice")
                                                      .AddLnd("bob")
                                                      .FundWallet("alice", 2_000_000)
                                                      .AddChannel("alice", "bob", StartupTiming.CapacitySat),
                                   storage, withChain);
}