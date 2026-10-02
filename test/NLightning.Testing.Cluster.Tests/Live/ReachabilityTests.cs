using System.Diagnostics;
using System.Net;

namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Reach;
using Cluster.Run;

/// <summary>
/// Spike check 1 on a real cluster: what the host test process reaches in the cluster and what a pod reaches on the
/// host. The assertions hold on OrbStack (the expected matrix is in <see cref="HostEndpoints"/>); elsewhere the tests
/// only record the matrix and skip.
/// </summary>
[Trait("Category", "Cluster")]
public class ReachabilityTests
{
    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static TestRunOptions Options(string suite) =>
        TestRunOptions.FromEnvironment(suite) with { Quota = NamespaceQuota.Spike, Log = Log };

    private static bool IsOrbStack()
    {
        var configuration = KubeClientFactory.BuildConfiguration();
        return configuration.CurrentContext == "orbstack";
    }

    [Fact(Explicit = true)]
    public async Task Given_OrbStack_When_TheHostAndAPodDialEachOther_Then_PodAndServiceAddressesAndHostOrbInternalWork()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        await using var run = await TestRun.StartAsync(Options("reach"), ct);

        // Act
        var result = await ReachabilityCheck.RunAsync(run, ct, log: Log);
        Log($"{run.Namespace}: reachability matrix in {watch.Elapsed.TotalSeconds:F1} s, placement {result.Placement}");
        Log(result.Report.ToString());
        if (!IsOrbStack())
            Assert.Skip("The expected matrix is OrbStack's; the matrix above is recorded only");

        // Assert: host to pod
        Assert.True(result.HostReachesPods);
        Assert.True(result.Report.Succeeded(ReachabilityCheck.Labels.PodIp));
        Assert.True(result.Report.Succeeded(ReachabilityCheck.Labels.PodDns));
        Assert.True(result.Report.Succeeded(ReachabilityCheck.Labels.ServiceDns));
        Assert.True(result.Report.Succeeded(ReachabilityCheck.Labels.ClusterIp));
        Assert.True(result.Report.Succeeded(ReachabilityCheck.Labels.ClusterIpDns));
        Assert.False(result.Report.Succeeded(ReachabilityCheck.Labels.OrbK8sDns));

        // Assert: pod to host, through OrbStack's host names to the loopback, which the listener sees as loopback
        Assert.Equal(RunnerPlacement.Host, result.Placement);
        Assert.Equal(HostEndpoints.OrbStackHost, result.PodHostAddress);
        Assert.False(result.PodHostNeedsAllInterfaces);
        foreach (var name in new[] { HostEndpoints.OrbStackHost, HostEndpoints.DockerHost })
        {
            var probe = result.Report[ReachabilityCheck.Labels.PodToHost(name, loopbackListener: true)];
            Assert.True(probe.Succeeded, probe.ToString());
            Assert.Contains($"listener saw {IPAddress.Loopback}", probe.Detail);
        }
    }

    [Fact(Explicit = true)]
    public async Task Given_ThreeRuns_When_TheyProbeAtOnce_Then_EachReachesItsOwnPodsBothWays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async i =>
        {
            await using var run = await TestRun.StartAsync(Options($"reach-{i}"), ct);
            var lines = new List<string>();
            var result = await ReachabilityCheck.RunAsync(run, ct, log: l =>
            {
                lock (lines)
                    lines.Add(l);
            });
            return (run.Namespace, Result: result, Elapsed: watch.Elapsed);
        }));

        // Assert
        foreach (var (ns, result, elapsed) in results)
        {
            Log($"{ns}: done after {elapsed.TotalSeconds:F1} s, placement {result.Placement}");
            Log(result.Report.ToString());
        }

        if (!IsOrbStack())
            Assert.Skip("The expected matrix is OrbStack's; the matrices above are recorded only");
        Assert.All(results, r => Assert.Equal(RunnerPlacement.Host, r.Result.Placement));
        Assert.All(results, r => Assert.Equal(HostEndpoints.OrbStackHost, r.Result.PodHostAddress));
        Log($"3 concurrent runs in {watch.Elapsed.TotalSeconds:F1} s");
    }
}