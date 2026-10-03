namespace NLightning.Testing.Cluster.Tests.Kube;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Run;

public class StoppedNodeMaintenanceTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "onchain" }, DateTimeOffset.UnixEpoch);

    private static NodeWorkload David(NodeStorage storage = NodeStorage.Persistent)
    {
        var workload = new NodeWorkload("david", NodeKind.Lnd, ImageVersions.Lnd)
        {
            Data = new DataVolume("/home/lnd/.lnd", "2Gi", Storage: storage),
            ReadinessProbe = Probes.Exec(["lncli", "getinfo"])
        };
        workload.Args.Add("lnd");
        workload.Ports.Add(new WorkloadPort("p2p", 9735));
        return workload;
    }

    [Fact]
    public void Given_ANodeOnAPvc_When_ItsMaintenancePodIsBuilt_Then_ItMountsTheNodesClaimAtTheSamePath()
    {
        // Arrange
        var set = David().Build(s_run).StatefulSet;

        // Act
        var pod = StoppedNodeMaintenance.BuildMaintenancePod(set, "nltg-spike-r1", "david");

        // Assert
        Assert.Equal("david" + StoppedNodeMaintenance.PodSuffix, pod.Metadata.Name);
        Assert.Equal("nltg-spike-r1", pod.Metadata.NamespaceProperty);
        var container = Assert.Single(pod.Spec.Containers);
        var main = set.Spec.Template.Spec.Containers[0];
        Assert.Equal(main.Image, container.Image);
        Assert.Equal(main.ImagePullPolicy, container.ImagePullPolicy);
        Assert.Equal(main.Resources, container.Resources);
        Assert.Equal(["sleep", "3600"], container.Command);
        var mount = Assert.Single(container.VolumeMounts);
        Assert.Equal("/home/lnd/.lnd", mount.MountPath);
        var volume = Assert.Single(pod.Spec.Volumes, v => v.Name == mount.Name);
        Assert.Equal($"{DataVolume.VolumeName}-david-0", volume.PersistentVolumeClaim.ClaimName);
        Assert.Equal("Never", pod.Spec.RestartPolicy);
    }

    [Fact]
    public void Given_ANodeOnAPvc_When_ItsMaintenancePodIsBuilt_Then_TheStatefulSetsSelectorDoesNotMatchIt()
    {
        // Arrange
        var set = David().Build(s_run).StatefulSet;

        // Act
        var pod = StoppedNodeMaintenance.BuildMaintenancePod(set, "nltg-spike-r1", "david");

        // Assert: never adopted by the StatefulSet nor routed by the node's Service; still a pod of the run
        var labels = pod.Metadata.Labels;
        Assert.False(set.Spec.Selector.MatchLabels.All(l => labels.TryGetValue(l.Key, out var v) && v == l.Value));
        Assert.Equal("david", labels[StoppedNodeMaintenance.MaintenanceLabel]);
        Assert.Equal("true", labels[RunLabels.Spike]);
    }

    [Fact]
    public void Given_ANodeOnAnEmptyDir_When_ItsMaintenancePodIsBuilt_Then_ItIsRefused()
    {
        // Arrange
        var set = David(NodeStorage.Ephemeral).Build(s_run).StatefulSet;

        // Act / Assert
        var e = Assert.Throws<InvalidOperationException>(() =>
            StoppedNodeMaintenance.BuildMaintenancePod(set, "nltg-spike-r1", "david"));
        Assert.Contains("no data on a PVC", e.Message);
    }
}