using System.Diagnostics;

namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Run;

/// <summary>
/// The harness against a real cluster (OrbStack's locally; the context is <c>NLTG_KUBE_CONTEXT</c> or the current
/// one). Explicit: the normal test run never needs a cluster. Run them with
/// <c>dotnet run --project test/NLightning.Testing.Cluster.Tests -f net10.0 -- -explicit only -trait Category=Cluster</c>.
/// Each test owns a <c>nltg-spike-&lt;id&gt;</c> namespace and deletes it.
/// </summary>
[Trait("Category", "Cluster")]
public class ClusterSmokeTests
{
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// A busybox node that appends a line to <c>/data/starts</c> and writes <c>/data/ready</c> at start (the readiness
    /// probe waits for it), and stops at once on SIGTERM (a shell as PID 1 ignores it otherwise).
    /// </summary>
    private static NodeWorkload BusyboxNode(string name) =>
        new(name, NodeKind.Other, ImageVersions.Busybox)
        {
            Command =
            [
                "sh", "-c",
                "trap 'exit 0' TERM; date >> /data/starts; touch /data/ready; while true; do sleep 1; done"
            ],
            Resources = WorkloadResources.Tiny,
            Data = new DataVolume("/data", "64Mi"),
            ReadinessProbe = Probes.Exec(["test", "-f", "/data/ready"], periodSeconds: 1),
            TerminationGracePeriodSeconds = 5
        };

    private static TestRunOptions Options(string suite) =>
        TestRunOptions.FromEnvironment(suite) with
        {
            Quota = NamespaceQuota.Spike,
            Log = Log
        };

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_ACluster_When_ARunDeploysABusyboxStatefulSet_Then_ItIsReadyAndItsNamespaceIsDeleted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var run = await TestRun.StartAsync(Options("cluster-smoke"), ct);
        var ns = run.Namespace;
        try
        {
            // Act
            var node = await run.DeployAsync(BusyboxNode("probe"), s_readyTimeout, ct);
            var readyAfter = watch.Elapsed;

            // Assert
            var namespaceObject = await RunNamespace.TryReadAsync(run.Client, ns, ct);
            Assert.NotNull(namespaceObject);
            Assert.Equal(run.Id, namespaceObject.Metadata.Labels[RunLabels.Run]);
            Assert.Equal("true", namespaceObject.Metadata.Labels[RunLabels.Spike]);
            Assert.StartsWith("nltg-spike-", ns);
            Assert.NotNull(node.PodIp);
            var hostname = await node.ExecAsync(["hostname"], ct);
            Assert.Equal("probe-0", hostname.EnsureSuccess("hostname").StdOutText.Trim());
            var lookup = await node.ExecAsync(["nslookup", node.ServiceDnsName], ct);
            Assert.Contains(node.PodIp, lookup.StdOutText);
            Log($"{ns}: probe ready after {readyAfter.TotalSeconds:F1} s, pod IP {node.PodIp}");
        }
        finally
        {
            await run.DisposeAsync();
        }

        // Assert: the namespace is gone
        using var client = KubeClientFactory.Create();
        Assert.Null(await RunNamespace.TryReadAsync(client, ns, ct));
        Log($"{ns}: created, ready and deleted in {watch.Elapsed.TotalSeconds:F1} s");
    }

    [Fact(Explicit = true)]
    public async Task Given_ANodeWithData_When_ItIsRestartedAndKilled_Then_ItKeepsItsNameAndData()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(Options("cluster-restart"), ct);
        var node = await run.DeployAsync(BusyboxNode("keeper"), s_readyTimeout, ct);
        byte[] marker = [0x00, 0x01, 0x7f, 0x80, 0xfe, 0xff, (byte)'\n', 0x00];
        await node.WriteFileAsync("/data/sub/marker.bin", marker, ct);
        var firstUid = node.PodUid;

        // Act
        var watch = Stopwatch.StartNew();
        await node.RestartAsync(s_readyTimeout, ct);
        var restartTime = watch.Elapsed;
        var afterRestart = await node.ReadFileAsync("/data/sub/marker.bin", ct);
        var restartUid = node.PodUid;
        watch.Restart();
        await node.KillAsync(s_readyTimeout, ct);
        var killTime = watch.Elapsed;
        var afterKill = await node.ReadFileAsync("/data/sub/marker.bin", ct);

        // Assert
        Assert.Equal(marker, afterRestart);
        Assert.Equal(marker, afterKill);
        Assert.NotEqual(firstUid, restartUid);
        Assert.NotEqual(restartUid, node.PodUid);
        Assert.Equal("keeper-0", (await node.ExecAsync(["hostname"], ct)).StdOutText.Trim());
        var starts = (await node.ExecAsync(["sh", "-c", "wc -l < /data/starts"], ct)).StdOutText.Trim();
        Assert.Equal("3", starts);
        await Assert.ThrowsAsync<KubeExecException>(() => node.ReadFileAsync("/data/missing", ct));
        Log($"{run.Namespace}: restart {restartTime.TotalSeconds:F1} s, kill {killTime.TotalSeconds:F1} s, "
          + $"pod IP now {node.PodIp}");
    }
}