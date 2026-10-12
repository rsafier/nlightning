using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Run;

public class QuotaSizingTests
{
    private static NodeWorkload Node(string name, WorkloadResources resources, bool data = true) =>
        new(name, NodeKind.Other, ImageVersions.Busybox)
        {
            Resources = resources,
            Data = data ? new DataVolume("/data", "64Mi") : null
        };

    [Fact]
    public void Given_ATopology_When_ItsQuotaIsSized_Then_ItIsTheSumOfItsPods()
    {
        // Arrange
        var workloads = new[]
        {
            Node("miner", WorkloadResources.Default),
            Node("alice", WorkloadResources.Default),
            Node("probe", WorkloadResources.Tiny, data: false)
        };

        // Act
        var quota = QuotaSizing.ForWorkloads(workloads);

        // Assert
        Assert.Equal("510m", quota.RequestsCpu);
        Assert.Equal("528Mi", quota.RequestsMemory);
        Assert.Equal("2100m", quota.LimitsCpu);
        Assert.Equal("2112Mi", quota.LimitsMemory);
        Assert.Equal(3, quota.Pods);
        Assert.Equal(2, quota.PersistentVolumeClaims);
    }

    [Fact]
    public void Given_ExtraPods_When_TheQuotaIsSized_Then_TheyAreAdded()
    {
        // Act
        var quota = QuotaSizing.ForWorkloads([Node("miner", WorkloadResources.Default)], extraPods: 2,
                                             extraPodResources: new WorkloadResources("500m", "512Mi", "1", "1Gi"));

        // Assert
        Assert.Equal("1250m", quota.RequestsCpu);
        Assert.Equal("1280Mi", quota.RequestsMemory);
        Assert.Equal("3000m", quota.LimitsCpu);
        Assert.Equal("3072Mi", quota.LimitsMemory);
        Assert.Equal(3, quota.Pods);
        Assert.Equal(1, quota.PersistentVolumeClaims);
    }

    [Fact]
    public void Given_ASidecarAndAnInitContainer_When_ThePodIsSized_Then_SidecarsAddAndTheInitContainerCapsBelow()
    {
        // Arrange
        var node = Node("lnd", WorkloadResources.Default);
        node.CustomizePod = spec =>
        {
            spec.Containers.Add(new V1Container
            {
                Name = "sidecar",
                Resources = new WorkloadResources("100m", "64Mi", "200m", "128Mi").ToKubernetes()
            });
            spec.InitContainers =
            [
                new V1Container
                {
                    Name = "init",
                    Resources = new WorkloadResources("2", "64Mi", "2", "64Mi").ToKubernetes()
                }
            ];
        };

        // Act
        var quota = QuotaSizing.ForWorkloads([node]);

        // Assert: CPU from the init container (2 > 350m), memory from the containers' sum (320Mi > 64Mi)
        Assert.Equal("2000m", quota.RequestsCpu);
        Assert.Equal("320Mi", quota.RequestsMemory);
        Assert.Equal("2000m", quota.LimitsCpu);
        Assert.Equal("1152Mi", quota.LimitsMemory);
    }

    [Fact]
    public void Given_AContainerWithoutResources_When_TheQuotaIsSized_Then_ItIsRefused()
    {
        // Arrange
        var node = Node("lnd", WorkloadResources.Default);
        node.CustomizePod = spec => spec.Containers.Add(new V1Container { Name = "bare" });

        // Act
        var exception = Assert.Throws<ArgumentException>(() => QuotaSizing.ForWorkloads([node]));

        // Assert
        Assert.Contains("bare", exception.Message);
    }

    [Theory]
    [InlineData("0.0001", "1m")]
    [InlineData("1.5", "1500m")]
    public void Given_Cpus_When_Formatted_Then_TheyRoundUpToMillicores(string cpus, string expected)
    {
        // Act & Assert
        Assert.Equal(expected,
                     QuotaSizing.FormatCpu(decimal.Parse(cpus, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Given_Bytes_When_Formatted_Then_TheyRoundUpToMebibytes()
    {
        // Act & Assert
        Assert.Equal("2Mi", QuotaSizing.FormatMemory(1024 * 1024 + 1));
    }
}