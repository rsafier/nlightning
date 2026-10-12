using k8s;
using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Faults;

using Cluster.Faults;
using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Run;

public class FaultInjectorTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "faults" }, DateTimeOffset.UnixEpoch);

    // Never contacted: every call below is refused before it reaches the cluster
    private static Kubernetes UnreachableClient() =>
        new(new KubernetesClientConfiguration { Host = "http://127.0.0.1:9" });

    [Fact]
    public void Given_AWorkload_When_ProcessFaultsAreEnabled_Then_ThePodSharesItsProcessNamespace()
    {
        // Arrange
        var hookRan = false;
        var workload = new NodeWorkload("alice", NodeKind.Lnd, ImageVersions.Lnd)
        {
            CustomizePod = spec =>
            {
                hookRan = true;
                spec.ShareProcessNamespace = false;
            }
        };

        // Act
        var spec = workload.WithProcessFaults().Build(s_run).StatefulSet.Spec.Template.Spec;

        // Assert
        Assert.True(hookRan);
        Assert.True(spec.ShareProcessNamespace);
        Assert.True(ProcessFaultSupport.IsEnabled(new V1Pod { Spec = spec }));
        Assert.False(ProcessFaultSupport.IsEnabled(new V1Pod { Spec = new V1PodSpec() }));
    }

    [Fact]
    public async Task Given_ANodeOfAnotherNamespace_When_AFaultIsInjected_Then_ItIsRefused()
    {
        // Arrange
        using var client = UnreachableClient();
        await using var faults = new FaultInjector(client, s_run);
        var stranger = new KubeNodeHandle(client, "default", "alice", NodeKind.Lnd);
        var ct = TestContext.Current.CancellationToken;

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(() => faults.PauseAsync(stranger, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => faults.ResumeAsync(stranger, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => faults.CrashAsync(stranger, TimeSpan.FromSeconds(1), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => faults.RestartAsync(stranger, TimeSpan.FromSeconds(1), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => faults.KillAsync(stranger, TimeSpan.FromSeconds(1), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => faults.IsolateAsync(stranger, null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => faults.PauseProcessAsync(stranger, "lightningd", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => faults.PartitionFromHostAsync([stranger], null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => faults.LimitIngressPortsAsync(stranger, [18443], ct));
        await Assert.ThrowsAsync<ArgumentException>(
            () => faults.RestartInPlaceAsync(stranger, ["true"], TimeSpan.FromSeconds(1), ct));
        Assert.Empty(faults.Events);
    }

    [Fact]
    public async Task Given_AnEmptyStopCommand_When_RestartedInPlace_Then_ItIsRefusedBeforeTheCluster()
    {
        // Arrange
        using var client = UnreachableClient();
        await using var faults = new FaultInjector(client, s_run);
        var node = new KubeNodeHandle(client, s_run.Namespace, "miner", NodeKind.BitcoinCore);

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => faults.RestartInPlaceAsync(node, [], TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Empty(faults.Events);
    }

    [Fact]
    public async Task Given_ADisposedInjector_When_AFaultIsInjected_Then_ItThrows()
    {
        // Arrange
        using var client = UnreachableClient();
        var faults = new FaultInjector(client, s_run);
        var node = new KubeNodeHandle(client, s_run.Namespace, "alice", NodeKind.Lnd);
        await faults.DisposeAsync();

        // Act / Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => faults.PauseAsync(node, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Given_Replacements_When_Compared_Then_NewPodTellsAReplacementFromAnInPlaceRestart()
    {
        // Act
        var restart = new NodeReplacement("a", FaultKind.Restart, "u1", "u2", "ip1", "ip2", 0, 0, TimeSpan.Zero);
        var crash = new NodeReplacement("a", FaultKind.Crash, "u1", "u1", "ip1", "ip1", 0, 1, TimeSpan.Zero);

        // Assert
        Assert.True(restart.NewPod);
        Assert.False(crash.NewPod);
    }

    [Fact]
    public void Given_FaultRecords_When_Printed_Then_TheyNameKindTargetAndSides()
    {
        // Act
        var fault = new FaultEvent(DateTimeOffset.UnixEpoch, FaultKind.Pause, "alice", TimeSpan.FromSeconds(1.5),
                                   "SIGSTOP pids 7");
        var isolate = new NetworkPartition("nltg-partition-1", ["alice"], null);
        var split = new NetworkPartition("nltg-partition-2", ["alice", "bob"], ["carol"], PartitionShape.Split);
        var outside = new NetworkPartition("nltg-partition-3", ["cln"], null, PartitionShape.Outside);
        var ports = new NetworkPartition("nltg-partition-4", ["miner"], null, PartitionShape.Ports, [18443, 18444]);

        // Assert
        Assert.Equal("00:00:00.000 Pause alice (1.5 s): SIGSTOP pids 7", fault.ToString());
        Assert.Equal("nltg-partition-1: [alice] | [*]", isolate.ToString());
        Assert.Equal("nltg-partition-2: [alice,bob] | [carol]", split.ToString());
        Assert.Equal("nltg-partition-3: [cln] | outside the run (host)", outside.ToString());
        Assert.Equal("nltg-partition-4: [miner] open only on ports 18443,18444", ports.ToString());
    }
}