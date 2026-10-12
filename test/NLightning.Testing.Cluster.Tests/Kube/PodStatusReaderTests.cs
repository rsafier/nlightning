using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Kube;

using Cluster.Kube;

public class PodStatusReaderTests
{
    private static V1Pod Pod(string phase, string? ip, bool ready, string? waitingReason = null) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = "alice-0" },
            Status = new V1PodStatus
            {
                Phase = phase,
                PodIP = ip,
                Conditions = [new V1PodCondition { Type = "Ready", Status = ready ? "True" : "False" }],
                ContainerStatuses =
                [
                    new V1ContainerStatus
                    {
                        Name = "alice",
                        Ready = ready,
                        State = waitingReason is null
                                    ? new V1ContainerState { Running = new V1ContainerStateRunning() }
                                    : new V1ContainerState
                                    {
                                        Waiting = new V1ContainerStateWaiting { Reason = waitingReason }
                                    }
                    }
                ]
            }
        };

    [Fact]
    public void Given_ARunningReadyPod_When_Read_Then_ItIsReady()
    {
        // Arrange
        var pod = Pod("Running", "10.0.0.5", ready: true);

        // Act & Assert
        Assert.True(PodStatusReader.IsRunning(pod));
        Assert.True(PodStatusReader.IsReady(pod));
        Assert.Null(PodStatusReader.GetFatalReason(pod));
    }

    [Fact]
    public void Given_ARunningPodWhoseProbeFails_When_Read_Then_ItIsRunningButNotReady()
    {
        // Arrange
        var pod = Pod("Running", "10.0.0.5", ready: false);

        // Act & Assert
        Assert.True(PodStatusReader.IsRunning(pod));
        Assert.False(PodStatusReader.IsReady(pod));
    }

    [Fact]
    public void Given_ATerminatingPod_When_Read_Then_ItIsNotReady()
    {
        // Arrange
        var pod = Pod("Running", "10.0.0.5", ready: true);
        pod.Metadata.DeletionTimestamp = DateTime.UtcNow;

        // Act & Assert
        Assert.False(PodStatusReader.IsReady(pod));
    }

    [Fact]
    public void Given_APendingPodWithoutIp_When_Read_Then_ItIsNotRunning()
    {
        // Act & Assert
        Assert.False(PodStatusReader.IsRunning(Pod("Pending", null, ready: false)));
    }

    [Theory]
    [InlineData("ErrImageNeverPull")]
    [InlineData("CrashLoopBackOff")]
    [InlineData("ImagePullBackOff")]
    public void Given_AHopelessContainerState_When_Read_Then_ThereIsAFatalReason(string reason)
    {
        // Act
        var fatal = PodStatusReader.GetFatalReason(Pod("Pending", null, ready: false, reason));

        // Assert
        Assert.NotNull(fatal);
        Assert.Contains(reason, fatal);
    }

    [Fact]
    public void Given_AContainerStillCreating_When_Read_Then_ThereIsNoFatalReason()
    {
        // Act & Assert
        Assert.Null(PodStatusReader.GetFatalReason(Pod("Pending", null, ready: false, "ContainerCreating")));
    }

    [Fact]
    public void Given_AFailedPod_When_Read_Then_ThereIsAFatalReason()
    {
        // Act & Assert
        Assert.NotNull(PodStatusReader.GetFatalReason(Pod("Failed", null, ready: false)));
    }

    [Fact]
    public void Given_APod_When_Described_Then_TheSummaryNamesPhaseIpAndContainers()
    {
        // Act
        var text = PodStatusReader.Describe(Pod("Pending", null, ready: false, "ContainerCreating"));

        // Assert
        Assert.Contains("alice-0 phase=Pending ip=-", text);
        Assert.Contains("[alice waiting(ContainerCreating) ready=False restarts=0]", text);
        Assert.Equal("pod not found", PodStatusReader.Describe(null));
    }
}