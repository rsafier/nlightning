namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Run;

public class KubeClientFactoryTests
{
    [Fact]
    public void Given_TheServiceHostVariable_When_TheSourceIsDetected_Then_ItIsInCluster()
    {
        // Act
        var source = KubeClientFactory.DetectSource(
            k => k == KubeClientFactory.ServiceHostVariable ? "10.96.0.1" : null);

        // Assert
        Assert.Equal(KubeConfigSource.InCluster, source);
    }

    [Fact]
    public void Given_NoServiceHostVariable_When_TheSourceIsDetected_Then_ItIsTheKubeConfig()
    {
        // Act
        var source = KubeClientFactory.DetectSource(_ => null);

        // Assert
        Assert.Equal(KubeConfigSource.KubeConfig, source);
    }
}