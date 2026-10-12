using System.Diagnostics;

namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Kube;
using Cluster.Nodes.Cln;
using Cluster.Run;
using Cluster.Topology;

/// <summary>
/// The CLN topology against a real cluster: bitcoind + two CLN nodes with a pre-opened channel, a payment between
/// them, and the run's namespace deleted (in the background). Explicit (see <see cref="ClusterSmokeTests"/> for how to run them).
/// </summary>
[Trait("Category", "Cluster")]
public class ClnTopologyTests
{
    private const long ChannelCapacitySat = 1_000_000;
    private const long PushMsat = 100_000_000;
    private const long PaymentMsat = 50_000_000;

    private static TestRunOptions Options(string suite) =>
        TestRunOptions.FromEnvironment(suite) with
        {
            Quota = NamespaceQuota.Spike,
            Log = Log
        };

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static TopologyBuilder ClnPair(NodeStorage? storage = null) =>
        new TopologyBuilder { Log = Log, Storage = storage }
           .AddBitcoinCore("miner")
           .AddCln("alice")
           .AddCln("bob")
           .FundWallet("alice", 2_000_000)
           .AddChannel("alice", "bob", ChannelCapacitySat, PushMsat);

    [Fact(Explicit = true)]
    public async Task Given_BitcoindAndTwoClnNodes_When_TheTopologyIsBuilt_Then_AliceOpensAChannelAndPaysBob()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var run = await TestRun.StartAsync(Options("cln-pair"), ct);
        var ns = run.Namespace;
        try
        {
            // Act: build (deploy, fund, open, confirm)
            var topology = await ClnPair().BuildAsync(run, ct);
            var builtAfter = watch.Elapsed;
            var alice = topology.Node<ClnTestPeer>("alice");
            var bob = topology.Node<ClnTestPeer>("bob");

            // Assert: the channel
            var channel = Assert.Single(topology.Channels);
            Assert.NotNull(channel.ShortChannelId);
            var bobId = await bob.GetNodeIdAsync(ct);
            var aliceView = Assert.Single(await alice.ListChannelsAsync(ct), c => c.RemoteNodeId == bobId);
            Assert.True(aliceView.Active);
            Assert.Equal(ChannelCapacitySat, aliceView.CapacitySat);
            Assert.Equal(channel.Open.FundingTxId, aliceView.FundingTxId);

            // Act: pay
            watch.Restart();
            var invoice = await bob.CreateInvoiceAsync(PaymentMsat, "cluster spike", ct);
            var paid = await alice.PayInvoiceAsync(invoice.Bolt11, ct);
            var payTime = watch.Elapsed;

            // Assert: paid, and the balances moved
            Assert.True(paid.Succeeded, paid.FailureReason);
            Assert.NotNull(paid.PreimageHex);
            var aliceId = await alice.GetNodeIdAsync(ct);
            var bobView = Assert.Single(await bob.ListChannelsAsync(ct), c => c.RemoteNodeId == aliceId);
            Assert.Equal(PushMsat + PaymentMsat, bobView.LocalBalanceMsat);
            var invoices = await bob.Rpc.CallAsync("listinvoices", ct, ("payment_hash", invoice.PaymentHashHex));
            Assert.Equal("paid", invoices["invoices"]![0]!["status"]!.GetValue<string>());
            Log($"{ns}: topology built in {builtAfter.TotalSeconds:F1} s (scid {channel.ShortChannelId}), "
              + $"paid {PaymentMsat} msat in {payTime.TotalSeconds:F1} s");
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

    /// <summary>
    /// Lifecycle on the CLN pair: a crash of bob's <c>lightningd</c> (container restart, same pod and IP), a graceful
    /// restart of bob (a new pod, on another IP) and one of alice. Data survives on the PVCs (same node ids, same
    /// channel), and after each step CLN brings the channel back by itself: alice dials bob's
    /// <see cref="StableNodeAddress"/>, which keeps its IP when bob's pod gets another one.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Given_AChannelBetweenTwoClnNodes_When_TheNodesCrashAndRestart_Then_TheChannelComesBackAndPays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var readyTimeout = TimeSpan.FromMinutes(3);
        await using var run = await TestRun.StartAsync(Options("cln-restart"), ct);
        var topology = await ClnPair(NodeStorage.Persistent).BuildAsync(run, ct);
        var alice = topology.Node<ClnTestPeer>("alice");
        var bob = topology.Node<ClnTestPeer>("bob");
        var aliceId = await alice.GetNodeIdAsync(ct);
        var bobId = await bob.GetNodeIdAsync(ct);
        var fundingTxId = topology.Channels[0].Open.FundingTxId;
        await PayAsync(alice, bob, "before", ct);

        // Act + Assert: crash of bob's process
        var watch = Stopwatch.StartNew();
        var bobIp = bob.Node.PodIp;
        Log($"{run.Namespace}: crash of bob's lightningd");
        await bob.CrashAsync(readyTimeout, ct);
        var crashTime = watch.Elapsed;
        var crashReconnect = await WaitReconnectedAsync(topology, ct);
        Assert.Equal(bobIp, bob.Node.PodIp);
        await PayAsync(alice, bob, "after crash", ct);

        // Act + Assert: graceful restart of bob (new pod)
        Log($"{run.Namespace}: {crashReconnect}; graceful restart of bob");
        watch.Restart();
        await bob.Node.RestartAsync(readyTimeout, ct);
        var bobRestartTime = watch.Elapsed;
        var bobRestartReconnect = await WaitReconnectedAsync(topology, ct);
        await PayAsync(alice, bob, "after bob restart", ct);

        // Act + Assert: graceful restart of alice (the funder, who dials)
        Log($"{run.Namespace}: {bobRestartReconnect}; graceful restart of alice");
        watch.Restart();
        await alice.Node.RestartAsync(readyTimeout, ct);
        var aliceRestartTime = watch.Elapsed;
        var aliceRestartReconnect = await WaitReconnectedAsync(topology, ct);
        await PayAsync(alice, bob, "after alice restart", ct);

        // Assert: same keys (hsm_secret on the PVC), same channel, every payment in bob's balance
        Assert.Equal(aliceId, (await alice.Rpc.GetInfoAsync(ct))["id"]!.GetValue<string>());
        Assert.Equal(bobId, (await bob.Rpc.GetInfoAsync(ct))["id"]!.GetValue<string>());
        var bobView = Assert.Single(await bob.ListChannelsAsync(ct), c => c.RemoteNodeId == aliceId);
        Assert.Equal(fundingTxId, bobView.FundingTxId);
        Assert.Equal(PushMsat + 4 * PaymentMsat, bobView.LocalBalanceMsat);
        Log($"{run.Namespace}: bob crash {crashTime.TotalSeconds:F1} s ({crashReconnect}); bob restart "
          + $"{bobRestartTime.TotalSeconds:F1} s, IP {bobIp} -> {bob.Node.PodIp} ({bobRestartReconnect}); alice "
          + $"restart {aliceRestartTime.TotalSeconds:F1} s ({aliceRestartReconnect})");
    }

    private static async Task PayAsync(ClnTestPeer payer, ClnTestPeer payee, string what, CancellationToken ct)
    {
        var invoice = await payee.CreateInvoiceAsync(PaymentMsat, what, ct);
        var paid = await payer.PayInvoiceAsync(invoice.Bolt11, ct);
        Assert.True(paid.Succeeded, $"{what}: {paid.FailureReason}");
    }

    /// <summary>Waits until CLN has brought every channel back by itself (no reconnect from the test).</summary>
    private static async Task<string> WaitReconnectedAsync(TestTopology topology, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await topology.WaitChannelsActiveAsync(TimeSpan.FromSeconds(60), ct);
        return $"channel active again after {watch.Elapsed.TotalSeconds:F1} s";
    }

    /// <summary>
    /// The spike's isolation proof for CLN: <c>NLTG_CLN_CONCURRENCY</c> (3 by default) CLN pairs built and paying at
    /// the same time, each in its own namespace with the same aliases.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Given_SeveralRuns_When_EachBuildsTheSameClnTopologyConcurrently_Then_EveryOnePays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var concurrency = int.TryParse(Environment.GetEnvironmentVariable("NLTG_CLN_CONCURRENCY"), out var n)
                              ? Math.Clamp(n, 1, 6)
                              : 3;
        var watch = Stopwatch.StartNew();

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async i =>
        {
            // Each run its own id: "<NLTG_TEST_RUN_ID>-c<i>", or a generated one
            var options = Options("cln-concurrent");
            options = options with { RunId = options.RunId is null ? null : $"{options.RunId}-c{i}" };
            var run = await TestRun.StartAsync(options, ct);
            try
            {
                var topology = await ClnPair().BuildAsync(run, ct);
                var built = watch.Elapsed;
                var invoice = await topology.Node("bob").CreateInvoiceAsync(PaymentMsat, $"run {i}", ct);
                var paid = await topology.Node("alice").PayInvoiceAsync(invoice.Bolt11, ct);
                await run.DisposeAsync();
                return (run.Namespace, Built: built, Paid: paid, Done: watch.Elapsed);
            }
            finally
            {
                await run.DisposeAsync();
            }
        }));

        // Assert
        Assert.Equal(concurrency, results.Select(r => r.Namespace).Distinct().Count());
        foreach (var (ns, built, paid, done) in results)
        {
            Assert.True(paid.Succeeded, $"{ns}: {paid.FailureReason}");
            Log($"{ns}: built at {built.TotalSeconds:F1} s, paid, deleted at {done.TotalSeconds:F1} s");
        }

        Log($"{concurrency} concurrent CLN runs in {watch.Elapsed.TotalSeconds:F1} s");
    }
}