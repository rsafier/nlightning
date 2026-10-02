using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Kube;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Run;

public class NodeWorkloadTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "lnd" }, DateTimeOffset.UnixEpoch);

    private static NodeWorkload Alice()
    {
        var workload = new NodeWorkload("alice", NodeKind.Lnd, ImageVersions.Lnd)
        {
            Data = new DataVolume("/root/.lnd", "2Gi"),
            ReadinessProbe = Probes.Exec(["lncli", "getinfo"])
        };
        workload.Args.Add("--bitcoin.regtest");
        workload.Env["ZED"] = "z";
        workload.Env["ALPHA"] = "a";
        workload.Ports.Add(new WorkloadPort("p2p", 9735));
        workload.Ports.Add(new WorkloadPort("grpc", 10009));
        workload.ScratchVolumes["tmp"] = "/tmp/scratch";
        workload.Labels["extra"] = "yes";
        return workload;
    }

    [Fact]
    public void Given_ANode_When_Built_Then_ItIsAOneReplicaStatefulSetWithAStableSelector()
    {
        // Act
        var set = Alice().Build(s_run).StatefulSet;

        // Assert
        Assert.Equal("alice", set.Metadata.Name);
        Assert.Equal("nltg-spike-r1", set.Metadata.NamespaceProperty);
        Assert.Equal(1, set.Spec.Replicas);
        Assert.Equal("alice", set.Spec.ServiceName);
        Assert.Equal(new Dictionary<string, string> { [RunLabels.Run] = "r1", [RunLabels.Node] = "alice" },
                     set.Spec.Selector.MatchLabels);
        var templateLabels = set.Spec.Template.Metadata.Labels;
        Assert.All(set.Spec.Selector.MatchLabels, l => Assert.Equal(l.Value, templateLabels[l.Key]));
        Assert.Equal("lnd", templateLabels[RunLabels.Kind]);
        Assert.Equal("yes", templateLabels["extra"]);
        Assert.Equal("true", templateLabels[RunLabels.Spike]);
    }

    [Fact]
    public void Given_ANodeWithData_When_Built_Then_ItsDataComesFromAClaimTemplate()
    {
        // Act
        var set = Alice().Build(s_run).StatefulSet;

        // Assert
        var claim = Assert.Single(set.Spec.VolumeClaimTemplates);
        Assert.Equal(DataVolume.VolumeName, claim.Metadata.Name);
        Assert.Equal(["ReadWriteOnce"], claim.Spec.AccessModes);
        Assert.Equal("2Gi", claim.Spec.Resources.Requests["storage"].ToString());
        Assert.Null(claim.Spec.StorageClassName);
        Assert.Equal("Delete", set.Spec.PersistentVolumeClaimRetentionPolicy.WhenDeleted);
        var container = Assert.Single(set.Spec.Template.Spec.Containers);
        Assert.Contains(container.VolumeMounts, m => m.Name == DataVolume.VolumeName && m.MountPath == "/root/.lnd");
        Assert.Contains(container.VolumeMounts, m => m.Name == "tmp" && m.MountPath == "/tmp/scratch");
        var scratch = Assert.Single(set.Spec.Template.Spec.Volumes);
        Assert.NotNull(scratch.EmptyDir);
    }

    [Fact]
    public void Given_ANode_When_Built_Then_TheContainerHasImageResourcesProbeEnvAndPorts()
    {
        // Act
        var spec = Alice().Build(s_run).StatefulSet.Spec.Template.Spec;

        // Assert
        var container = Assert.Single(spec.Containers);
        Assert.Equal("alice", container.Name);
        Assert.Equal("custom_lnd:latest", container.Image);
        Assert.Equal("Never", container.ImagePullPolicy);
        Assert.Null(container.Command);
        Assert.Equal(["--bitcoin.regtest"], container.Args);
        Assert.Equal(["ALPHA", "ZED"], container.Env.Select(e => e.Name));
        Assert.Equal([9735, 10009], container.Ports.Select(p => p.ContainerPort));
        Assert.Equal("250m", container.Resources.Requests["cpu"].ToString());
        Assert.Equal("256Mi", container.Resources.Requests["memory"].ToString());
        Assert.Equal("1", container.Resources.Limits["cpu"].ToString());
        Assert.Equal("1Gi", container.Resources.Limits["memory"].ToString());
        Assert.Equal(["lncli", "getinfo"], container.ReadinessProbe.Exec.Command);
        Assert.False(spec.EnableServiceLinks);
        Assert.False(spec.AutomountServiceAccountToken);
        Assert.Equal(10, spec.TerminationGracePeriodSeconds);
        Assert.Equal("Always", spec.RestartPolicy);
    }

    [Fact]
    public void Given_ANode_When_Built_Then_ItsServiceIsHeadlessAndPublishesNotReadyAddresses()
    {
        // Act
        var service = Alice().Build(s_run).Service;

        // Assert
        Assert.Equal("alice", service.Metadata.Name);
        Assert.Equal("None", service.Spec.ClusterIP);
        Assert.True(service.Spec.PublishNotReadyAddresses);
        Assert.Equal(new Dictionary<string, string> { [RunLabels.Run] = "r1", [RunLabels.Node] = "alice" },
                     service.Spec.Selector);
        Assert.Equal(["p2p", "grpc"], service.Spec.Ports.Select(p => p.Name));
        Assert.Equal("r1", service.Metadata.Labels[RunLabels.Run]);
    }

    [Fact]
    public void Given_ABareNode_When_Built_Then_ItHasNoClaimsVolumesOrPorts()
    {
        // Arrange
        var workload = new NodeWorkload("probe", NodeKind.Other, ImageVersions.Busybox);

        // Act
        var manifests = workload.Build(s_run);

        // Assert
        Assert.Null(manifests.StatefulSet.Spec.VolumeClaimTemplates);
        Assert.Null(manifests.StatefulSet.Spec.PersistentVolumeClaimRetentionPolicy);
        Assert.Null(manifests.StatefulSet.Spec.Template.Spec.Volumes);
        Assert.Null(manifests.StatefulSet.Spec.Template.Spec.Containers[0].VolumeMounts);
        Assert.Null(manifests.Service.Spec.Ports);
        Assert.Equal("IfNotPresent", manifests.StatefulSet.Spec.Template.Spec.Containers[0].ImagePullPolicy);
        Assert.Equal("probe-0", workload.PodName);
    }

    [Fact]
    public void Given_ACustomizeHook_When_Built_Then_ItRunsLast()
    {
        // Arrange
        var workload = new NodeWorkload("cln", NodeKind.Cln, ImageVersions.Cln)
        {
            CustomizePod = spec => spec.InitContainers = [new V1Container { Name = "init", Image = "busybox:1.37" }]
        };

        // Act
        var spec = workload.Build(s_run).StatefulSet.Spec.Template.Spec;

        // Assert
        Assert.Equal("init", Assert.Single(spec.InitContainers).Name);
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("alice_1")]
    [InlineData("-alice")]
    [InlineData("")]
    public void Given_AnInvalidName_When_AWorkloadIsCreated_Then_ItThrows(string name)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new NodeWorkload(name, NodeKind.Other, ImageVersions.Busybox));
    }

    [Fact]
    public void Given_ANameLongerThanAStatefulSetAllows_When_AWorkloadIsCreated_Then_ItThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new NodeWorkload(new string('a', KubeNames.MaxWorkloadNameLength + 1),
                                                                NodeKind.Other, ImageVersions.Busybox));
    }
}