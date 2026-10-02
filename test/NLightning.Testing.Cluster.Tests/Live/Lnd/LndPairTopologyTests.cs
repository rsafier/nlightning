using System.Diagnostics;

namespace NLightning.Testing.Cluster.Tests.Live.Lnd;

using Cluster.Run;
using Cluster.Topology.Lnd;

/// <summary>
/// The spike's LND topology (bitcoind + alice and bob with a channel) against a real cluster: build it, pay, restart a
/// node on its PVC, pay again, tear it down. Explicit, like every <c>Category=Cluster</c> test:
/// <c>NLTG_KUBE_CONTEXT=orbstack dotnet run --project test/NLightning.Testing.Cluster.Tests -c Release -f net10.0 --
/// -explicit only -trait Category=Cluster -class NLightning.Testing.Cluster.Tests.Live.Lnd.LndPairTopologyTests</c>.
/// Several processes with different <c>NLTG_TEST_RUN_ID</c>s run side by side, each in its own namespace.
/// </summary>
[Trait("Category", "Cluster")]
public class LndPairTopologyTests
{
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(4);

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_TheLndPair_When_AlicePaysBobAndBobRestarts_Then_BothPaymentsSucceedAndBobKeepsItsChannel()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var total = Stopwatch.StartNew();
        var options = TestRunOptions.FromEnvironment("lnd-pair") with { Quota = NamespaceQuota.Spike, Log = Log };
        var run = await TestRun.StartAsync(options, ct);
        var ns = run.Namespace;
        try
        {
            using var topology = await LndPairTopology.BuildAsync(run, new LndPairTopology.Settings
            {
                ReadyTimeout = s_readyTimeout,
                Log = Log
            }, ct);
            var buildTime = total.Elapsed;
            var aliceId = await topology.Alice.GetNodeIdAsync(ct);
            var bobId = await topology.Bob.GetNodeIdAsync(ct);
            var bobPodUid = topology.Bob.Handle.PodUid;
            var alicePodUid = topology.Alice.Handle.PodUid;

            // Act
            var (_, first, firstAttempts) =
                await LndPairTopology.PayAsync(topology.Alice, topology.Bob, 25_000_000, TimeSpan.FromMinutes(1), ct);
            var watch = Stopwatch.StartNew();
            await topology.RestartAsync(topology.Bob, kill: false, s_readyTimeout, ct);
            var restartTime = watch.Elapsed;
            var (_, second, secondAttempts) =
                await LndPairTopology.PayAsync(topology.Alice, topology.Bob, 5_000_000, TimeSpan.FromMinutes(1), ct);
            watch.Restart();
            await topology.RestartAsync(topology.Alice, kill: true, s_readyTimeout, ct);
            var killTime = watch.Elapsed;
            var (_, third, thirdAttempts) =
                await LndPairTopology.PayAsync(topology.Bob, topology.Alice, 1_000_000, TimeSpan.FromMinutes(1), ct);

            // Assert
            Assert.True(first.Succeeded, first.FailureReason);
            Assert.True(second.Succeeded, second.FailureReason);
            Assert.True(third.Succeeded, third.FailureReason);
            Assert.Equal(64, first.PreimageHex?.Length);
            Assert.Equal(aliceId, await topology.Alice.GetNodeIdAsync(ct));
            Assert.Equal(bobId, await topology.Bob.GetNodeIdAsync(ct));
            Assert.NotEqual(bobPodUid, topology.Bob.Handle.PodUid);
            Assert.NotEqual(alicePodUid, topology.Alice.Handle.PodUid);
            var bobChannel = Assert.Single(await topology.Bob.ListChannelsAsync(ct));
            Assert.Equal(topology.Channel.FundingTxId, bobChannel.FundingTxId);
            Assert.Equal(29_000_000, bobChannel.LocalBalanceMsat);
            Assert.NotNull(bobChannel.ShortChannelId);
            Log($"{ns}: phases {string.Join(", ", topology.Timings.Select(t => $"{t.Key} {t.Value.TotalSeconds:F1} s"))}");
            Log($"{ns}: built in {buildTime.TotalSeconds:F1} s; payment 1 after {firstAttempts} attempt(s); bob "
              + $"restart to channel active {restartTime.TotalSeconds:F1} s (pod IP now {topology.Bob.Handle.PodIp}); "
              + $"payment 2 after {secondAttempts} attempt(s); alice kill to channel active "
              + $"{killTime.TotalSeconds:F1} s; payment 3 (bob to alice) after {thirdAttempts} attempt(s); channel "
              + $"{bobChannel.ShortChannelId}");
        }
        finally
        {
            var teardown = Stopwatch.StartNew();
            await run.DisposeAsync();
            Log($"{ns}: torn down in {teardown.Elapsed.TotalSeconds:F1} s (deletion issued), total "
              + $"{total.Elapsed.TotalSeconds:F1} s");
        }

        // Assert: the namespace is going (in the background)
        await RunAssertions.AssertDeletedOrTerminatingAsync(ns, ct);
    }
}