namespace NLightning.Testing.Cluster.Tests.Reach;

using Cluster.Reach;
using Cluster.Run;

public class ReachabilityTypesTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "reach" }, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Given_AHost_When_ThePodProbeCommandIsBuilt_Then_TheHostAndPortArePassedAsArguments()
    {
        // Act
        var command = PodProbe.Command("host.orb.internal", 38417, 3);

        // Assert
        Assert.Equal(["sh", "-c", "echo probe | nc -w \"$2\" \"$0\" \"$1\"", "host.orb.internal", "38417", "3"],
                     command);
    }

    [Fact]
    public void Given_AnEchoNode_When_Built_Then_ItListensOnItsPortAndPublishesIt()
    {
        // Act
        var manifests = EchoNode.Build("echo").Build(s_run);

        // Assert
        var container = Assert.Single(manifests.StatefulSet.Spec.Template.Spec.Containers);
        Assert.Contains($"tcpsvd 0.0.0.0 {EchoNode.Port}", container.Command[2]);
        Assert.Contains(EchoNode.Banner, container.Command[2]);
        Assert.Equal(EchoNode.Port, Assert.Single(manifests.Service.Spec.Ports).Port);
        Assert.Equal("10m", container.Resources.Requests["cpu"].ToString());
    }

    [Fact]
    public void Given_ANode_When_ItsClusterIpServiceIsBuilt_Then_ItSelectsTheNodeAndHasAnIp()
    {
        // Act
        var service = ReachabilityCheck.BuildClusterIpService(s_run, "echo", EchoNode.Port);

        // Assert
        Assert.Equal("echo-cip", service.Metadata.Name);
        Assert.Equal("nltg-spike-r1", service.Metadata.NamespaceProperty);
        Assert.Equal("ClusterIP", service.Spec.Type);
        Assert.Null(service.Spec.ClusterIP);
        Assert.Equal(RunLabels.NodeSelector("r1", "echo"), service.Spec.Selector);
        Assert.Equal("r1", service.Metadata.Labels[RunLabels.Run]);
    }

    [Fact]
    public void Given_Probes_When_Reported_Then_TheyAreFoundByLabelAndPrinted()
    {
        // Arrange
        var report = new ReachabilityReport();
        report.Add("a", new ProbeResult(ProbeDirection.HostToPod, "10.0.0.1:8080", true,
                                        TimeSpan.FromMilliseconds(3), null, "10.0.0.1", "nltg-echo"));
        report.Add("b", new ProbeResult(ProbeDirection.PodToHost, "host.orb.internal:1", false,
                                        TimeSpan.FromSeconds(3), "nc exit 1"));

        // Act
        var text = report.ToString();

        // Assert
        Assert.True(report.Succeeded("a"));
        Assert.False(report.Succeeded("b"));
        Assert.False(report.Succeeded("c"));
        Assert.Equal("host.orb.internal:1", report["b"].Target);
        Assert.Throws<KeyNotFoundException>(() => report["c"]);
        Assert.Contains("host->pod 10.0.0.1:8080 OK 3 ms [10.0.0.1] (nltg-echo)", text);
        Assert.Contains("pod->host host.orb.internal:1 FAIL 3000 ms: nc exit 1", text);
    }

    [Theory]
    [InlineData(true, "host.orb.internal", RunnerPlacement.Host)]
    [InlineData(false, "host.orb.internal", RunnerPlacement.InCluster)]
    [InlineData(true, null, RunnerPlacement.InCluster)]
    public void Given_WhatWasReached_When_ThePlacementIsRead_Then_ItNeedsBothDirections(
        bool hostReachesPods, string? podHostAddress, RunnerPlacement expected)
    {
        // Act
        var result = new ReachabilityResult(new ReachabilityReport(), hostReachesPods, podHostAddress, false);

        // Assert
        Assert.Equal(expected, result.Placement);
    }

    [Fact]
    public void Given_ACandidate_When_ItsLabelIsBuilt_Then_ItNamesTheListener()
    {
        Assert.Equal("pod->host.orb.internal (lo)",
                     ReachabilityCheck.Labels.PodToHost(HostEndpoints.OrbStackHost, true));
        Assert.Equal("pod->10.0.0.1 (any)", ReachabilityCheck.Labels.PodToHost("10.0.0.1", false));
    }
}