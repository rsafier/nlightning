using k8s;
using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Kube;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Run;

public class NodeStorageTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "storage" }, DateTimeOffset.UnixEpoch);

    private static NodeWorkload Node(NodeStorage storage)
    {
        var workload = new NodeWorkload("alice", NodeKind.Cln, ImageVersions.Cln)
        {
            Data = new DataVolume("/root/.lightning", "1Gi", Storage: storage)
        };
        workload.ScratchVolumes["tmp"] = "/tmp/scratch";
        return workload;
    }

    [Fact]
    public void Given_EphemeralData_When_Built_Then_TheDataIsAnEmptyDirAndThereIsNoClaim()
    {
        // Act
        var set = Node(NodeStorage.Ephemeral).Build(s_run).StatefulSet;

        // Assert
        Assert.Null(set.Spec.VolumeClaimTemplates);
        Assert.Null(set.Spec.PersistentVolumeClaimRetentionPolicy);
        var pod = set.Spec.Template.Spec;
        var data = Assert.Single(pod.Volumes, v => v.Name == DataVolume.VolumeName);
        Assert.NotNull(data.EmptyDir);
        Assert.Contains(pod.Volumes, v => v.Name == "tmp" && v.EmptyDir is not null);
        Assert.Contains(pod.Containers[0].VolumeMounts,
                        m => m.Name == DataVolume.VolumeName && m.MountPath == "/root/.lightning");
    }

    [Fact]
    public void Given_PersistentData_When_Built_Then_TheDataIsAClaimAndNotAPodVolume()
    {
        // Act
        var set = Node(NodeStorage.Persistent).Build(s_run).StatefulSet;

        // Assert
        Assert.Single(set.Spec.VolumeClaimTemplates);
        Assert.DoesNotContain(set.Spec.Template.Spec.Volumes, v => v.Name == DataVolume.VolumeName);
    }

    [Fact]
    public void Given_InitContainers_When_Built_Then_TheyRunBeforeTheNodeInOrder()
    {
        // Arrange
        var workload = Node(NodeStorage.Persistent);
        workload.InitContainers.Add(new V1Container { Name = "first" });
        workload.InitContainers.Add(new V1Container { Name = "second" });

        // Act
        var pod = workload.Build(s_run).StatefulSet.Spec.Template.Spec;

        // Assert
        Assert.Equal(["first", "second"], pod.InitContainers.Select(c => c.Name));
        Assert.Null(Node(NodeStorage.Persistent).Build(s_run).StatefulSet.Spec.Template.Spec.InitContainers);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("persistent", NodeStorage.Persistent)]
    [InlineData(" PVC ", NodeStorage.Persistent)]
    [InlineData("ephemeral", NodeStorage.Ephemeral)]
    [InlineData("emptyDir", NodeStorage.Ephemeral)]
    public void Given_TheStorageVariable_When_Read_Then_ItGivesTheDefault(string? value, NodeStorage? expected)
    {
        // Act & Assert
        Assert.Equal(expected, NodeStorageEnvironment.Read(name => name == NodeStorageEnvironment.Variable ? value : null));
    }

    [Fact]
    public void Given_AnUnknownStorage_When_Read_Then_ItIsRefused()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => NodeStorageEnvironment.Read(_ => "tmpfs"));
    }

    [Fact]
    public async Task Given_AnEphemeralNode_When_RestartedOrKilled_Then_ItRefusesBeforeTouchingThePod()
    {
        // Arrange: a client whose server does not exist, so any call would fail differently
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://127.0.0.1:9" });
        var handle = new KubeNodeHandle(client, "nltg-spike-r1", "alice", NodeKind.Cln, storage: NodeStorage.Ephemeral);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var restart = await Assert.ThrowsAsync<InvalidOperationException>(
                          () => handle.RestartAsync(TimeSpan.FromSeconds(1), ct));
        var kill = await Assert.ThrowsAsync<InvalidOperationException>(
                       () => handle.KillAsync(TimeSpan.FromSeconds(1), ct));

        // Assert
        Assert.Contains("emptyDir", restart.Message);
        Assert.Contains(nameof(NodeStorage.Persistent), kill.Message);
    }
}