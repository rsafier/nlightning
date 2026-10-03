using System.Diagnostics;

namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Diagnostics;
using Cluster.Nodes.Cln;
using Cluster.Run;
using Cluster.Topology;

/// <summary>
/// Failure diagnostics against a real cluster (explicit, <c>Category=Cluster</c>; see <see cref="ClusterSmokeTests"/>
/// for how to run them): a manual dump of bitcoind, CLN and LND, and a topology whose CLN node never becomes ready
/// (an option lightningd does not know), dumped by the build and kept with <c>NLTG_KEEP_NAMESPACE=failure</c>.
/// The two tests that fail on purpose are in <see cref="DiagnosticsFailureProofTests"/>.
/// </summary>
[Trait("Category", "Cluster")]
public class DiagnosticsClusterTests
{
    private static readonly string[] s_secrets = ["rpcpassword=nltg", "rpcpass=nltg", "rpcauth="];

    private static TestRunOptions Options(string suite) =>
        TestRunOptions.FromEnvironment(suite) with
        {
            Quota = NamespaceQuota.Spike,
            Log = Log
        };

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_BitcoindClnAndLnd_When_DumpAsyncIsCalled_Then_EveryNodesStateIsWrittenWithoutSecrets()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(Options("diag-dump"), ct);
        using var topology = await new TopologyBuilder { Log = Log }
                                  .AddBitcoinCore("miner").AddCln("alice").AddLnd("bob")
                                  .BuildAsync(run, ct);

        // Act
        var watch = Stopwatch.StartNew();
        var dump = await run.DumpAsync("manual-dump", "DumpAsync proof", ct);
        var took = watch.Elapsed;

        // Assert: the namespace files and every pod's describe, log and node state
        Log($"{run.Namespace}: dumped in {took.TotalSeconds:F1} s into {dump.Directory}: "
          + string.Join(", ", dump.Files) + (dump.Errors.Count == 0 ? string.Empty : $"; errors: {string.Join("; ", dump.Errors)}"));
        Assert.Empty(dump.Errors);
        Assert.False(run.Diagnostics.Failed);
        foreach (var file in new[] { "pods.txt", "events.txt", "storage.txt", "workloads.txt" })
            Assert.Contains(file, dump.Files);
        foreach (var pod in new[] { "miner-0", "alice-0", "bob-0" })
        {
            var node = pod[..^2];
            Assert.Contains($"pods/{pod}/describe.txt", dump.Files);
            Assert.Contains($"pods/{pod}/{node}.log", dump.Files);
        }

        string Read(string file) => File.ReadAllText(Path.Combine(dump.Directory, file));
        Assert.Contains("\"chain\": \"regtest\"", Read("pods/miner-0/state/getblockchaininfo.json"));
        Assert.StartsWith("[", Read("pods/miner-0/state/getpeerinfo.json"));
        Assert.Contains("\"id\": \"", Read("pods/alice-0/state/getinfo.json"));
        Assert.Contains("\"channels\"", Read("pods/alice-0/state/listpeerchannels.json"));
        Assert.Contains("\"identity_pubkey\"", Read("pods/bob-0/state/getinfo.json"));
        Assert.Contains("\"channels\"", Read("pods/bob-0/state/listchannels.json"));
        Assert.Contains("PVC data-miner-0: phase=Bound", Read("storage.txt"));
        Assert.Contains("Scheduled", Read("events.txt"));
        Assert.Equal("DumpAsync proof", Read("reason.txt"));

        // No secret anywhere, and no secret file read
        foreach (var path in Directory.EnumerateFiles(dump.Directory, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain(s_secrets, s => text.Contains(s, StringComparison.Ordinal));
            Assert.DoesNotContain("macaroon", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hsm_secret", Path.GetFileName(path), StringComparison.Ordinal);
        }
    }

    [Fact(Explicit = true)]
    public async Task Given_AClnNodeWithAnUnknownOption_When_TheTopologyIsBuilt_Then_TheBuildFailsDumpedAndKept()
    {
        // Arrange: keep the namespace on failure (as NLTG_KEEP_NAMESPACE=failure)
        var ct = TestContext.Current.CancellationToken;
        var options = Options("diag-notready") with { KeepNamespaceOnFailure = true };
        options = options with { Diagnostics = options.Diagnostics with { Mode = DiagnosticsMode.Failure } };
        var run = await TestRun.StartAsync(options, ct);
        var identity = run.Identity;
        var watch = Stopwatch.StartNew();
        Exception? failure;
        try
        {
            // Act
            // lightningd exits on the option; the image's entrypoint keeps the container running, so the readiness
            // probe never passes and the wait times out
            var builder = new TopologyBuilder { Log = Log, ReadyTimeout = TimeSpan.FromSeconds(40) }
                         .AddBitcoinCore("miner")
                         .AddCln("alice", extraArgs: ["--nltg-no-such-option"]);
            failure = await Record.ExceptionAsync(() => builder.BuildAsync(run, ct));
        }
        finally
        {
            await run.DisposeAsync();
        }

        var failedAfter = watch.Elapsed;

        // Assert: the build failed, the run is failed and was dumped once, under this test
        using var client = KubeClientFactory.Create();
        try
        {
            Assert.NotNull(failure);
            Log($"{identity.Namespace}: build failed after {failedAfter.TotalSeconds:F1} s: {failure.Message}");
            Assert.True(run.Diagnostics.Failed);
            Assert.Contains("node alice ready failed", run.Diagnostics.FailureReason);
            var directory = Assert.Single(run.Diagnostics.Dumps).Value;
            Log($"diagnostics in {directory}");
            string Read(string file) => File.ReadAllText(Path.Combine(directory, file));
            var reasons = Read(ClusterDiagnostics.FailureFileName);
            Assert.Contains("node alice ready failed", reasons);
            Assert.Contains("topology build failed", reasons);
            var describe = Read("pods/alice-0/describe.txt");
            Assert.Contains("--nltg-no-such-option", describe);
            Assert.Matches(@"Restarts: [1-9]", describe);
            Assert.Contains("--nltg-no-such-option: unknown option", Read("pods/alice-0/alice.previous.log"));
            Assert.Contains("--nltg-no-such-option: unknown option", Read("pods/alice-0/alice.log"));
            Assert.Contains("lightning-rpc", Read("pods/alice-0/state/getinfo.json"));
            Assert.Contains("\"chain\": \"regtest\"", Read("pods/miner-0/state/getblockchaininfo.json"));
            Assert.Contains("Readiness probe failed", Read("events.txt"));
            Assert.Contains("PVC data-alice-0: phase=Bound", Read("storage.txt"));

            // Kept, and annotated so the reaper waits for its TTL
            var kept = await RunNamespace.TryReadAsync(client, identity.Namespace, ct);
            Assert.NotNull(kept);
            Assert.True(RunAnnotations.IsKept(kept));
        }
        finally
        {
            // This test's own kept namespace
            if (await RunNamespace.DeleteAsync(client, identity, CancellationToken.None))
                await RunNamespace.WaitForDeletionAsync(client, identity.Namespace, TimeSpan.FromMinutes(2),
                                                        CancellationToken.None);
        }
    }
}

/// <summary>
/// The test-failure hook's proofs: both tests <b>fail on purpose</b> and are left out of the normal cluster runs
/// (explicit, <c>Category=ClusterFailureProof</c>, not <c>Cluster</c>). Run them with
/// <c>scripts/run-cluster.sh -n 1 --trait Category=ClusterFailureProof</c> and look for their dumps under
/// <c>TestResults/cluster/&lt;batch&gt;/&lt;run&gt;/diag/</c>. The run lives in the class (disposed after the hook, as
/// a fixture's would be), so the hook still sees it.
/// </summary>
[Trait("Category", "ClusterFailureProof")]
public sealed class DiagnosticsFailureProofTests : IAsyncDisposable
{
    private TestRun? _run;

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_APollThatTimesOut_When_TheTestFails_Then_TheRunIsDumped_FailsOnPurpose()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("diag-poll") with { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        using var topology = await new TopologyBuilder { Log = Log }.AddBitcoinCore("miner").AddCln("alice")
                                                                    .BuildAsync(_run, ct);
        var alice = topology.Node<ClnTestPeer>("alice");

        // Act: a wait that cannot succeed (the dump happens at the timeout, the hook appends the test's failure)
        await Poll.UntilAsync(async c => await alice.GetBlockHeightAsync(c) >= 1_000_000, TimeSpan.FromSeconds(10),
                              TimeSpan.FromSeconds(1), "alice at height 1,000,000 (fails on purpose)", ct);
    }

    [Fact(Explicit = true)]
    public async Task Given_AnAssertionFails_When_TheTestEnds_Then_TheHookDumpsTheRun_FailsOnPurpose()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("diag-assert") with { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        using var topology = await new TopologyBuilder { Log = Log }.AddBitcoinCore("miner").AddLnd("bob")
                                                                    .BuildAsync(_run, ct);

        // Act & Assert: fails on purpose
        var height = await topology.Node("bob").GetBlockHeightAsync(ct);
        Assert.Fail($"bob is at {height}; this test fails on purpose to prove the failure hook");
    }

    public async ValueTask DisposeAsync()
    {
        if (_run is not null)
            await _run.DisposeAsync();
    }
}