using k8s;

namespace NLightning.Testing.Cluster.Tests.Runner;

using Cluster.Run;

public class AdoptedRunTests
{
    [Fact]
    public async Task Given_AdoptWithoutARunId_When_TheRunStarts_Then_ItRefusesBeforeCallingTheCluster()
    {
        // Arrange: a client whose server does not exist, so any call would fail differently
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://127.0.0.1:9" });
        var options = new TestRunOptions { AdoptNamespace = true, Suite = "adopt" };

        // Act
        var e = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => TestRun.StartAsync(client, options, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains(TestRunId.EnvironmentVariable, e.Message);
    }
}