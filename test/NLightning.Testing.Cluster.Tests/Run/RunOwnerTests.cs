using System.Diagnostics;
using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Run;

public class RunOwnerTests
{
    [Fact]
    public void Given_ThisProcess_When_TheOwnerIsRead_Then_ItIsThisHostAndPid()
    {
        // Act
        var owner = RunOwner.Current();

        // Assert
        Assert.Equal(Environment.MachineName, owner.Host);
        Assert.Equal(Environment.ProcessId, owner.Pid);
        Assert.NotNull(owner.ProcessStartUnixMs);
    }

    [Fact]
    public void Given_AnOwner_When_ItRoundTripsThroughAnnotations_Then_ItIsEqual()
    {
        // Arrange
        var owner = new RunOwner("mac", 4242, 1_700_000_000_123);
        var ns = new V1Namespace { Metadata = new V1ObjectMeta { Annotations = owner.ToAnnotations() } };

        // Act
        var read = RunOwner.FromNamespace(ns);

        // Assert
        Assert.Equal(owner, read);
        Assert.Equal("mac:4242", read!.ToString());
    }

    [Fact]
    public void Given_AnOwnerWithoutAStartTime_When_ItRoundTrips_Then_TheStartStaysUnknown()
    {
        // Arrange
        var annotations = new RunOwner("mac", 7, null).ToAnnotations();

        // Act
        var read = RunOwner.FromNamespace(new V1Namespace { Metadata = new V1ObjectMeta { Annotations = annotations } });

        // Assert
        Assert.False(annotations.ContainsKey(RunOwner.StartAnnotation));
        Assert.Null(read!.ProcessStartUnixMs);
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData("mac", null)]
    [InlineData("mac", "-1")]
    [InlineData("mac", "0")]
    [InlineData("mac", "x")]
    [InlineData(" ", "1")]
    public void Given_BadOwnerAnnotations_When_Read_Then_ThereIsNoOwner(string? host, string? pid)
    {
        // Arrange
        var annotations = new Dictionary<string, string>();
        if (host is not null)
            annotations[RunOwner.HostAnnotation] = host;
        if (pid is not null)
            annotations[RunOwner.PidAnnotation] = pid;

        // Act
        var owner = RunOwner.FromNamespace(new V1Namespace
        {
            Metadata = new V1ObjectMeta { Annotations = annotations }
        });

        // Assert
        Assert.Null(owner);
    }

    [Fact]
    public void Given_ThisProcess_When_Probed_Then_ItIsAlive()
    {
        // Arrange
        var owner = RunOwner.Current();

        // Act
        var state = LocalProcessProbe.Instance.Probe(owner.Pid, owner.ProcessStartUnixMs);

        // Assert
        Assert.Equal(OwnerProcessState.Alive, state);
    }

    [Fact]
    public void Given_ThisPidWithAnotherStartTime_When_Probed_Then_ThePidWasReused()
    {
        // Arrange
        var owner = RunOwner.Current();

        // Act
        var state = LocalProcessProbe.Instance.Probe(owner.Pid, owner.ProcessStartUnixMs!.Value - 60_000);

        // Assert
        Assert.Equal(OwnerProcessState.PidReused, state);
    }

    [Fact]
    public async Task Given_AnExitedProcess_When_Probed_Then_ItIsGone()
    {
        // Arrange
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", "-c true") { UseShellExecute = false })!;
        var pid = process.Id;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        process.Dispose();

        // Act
        var state = LocalProcessProbe.Instance.Probe(pid, null);

        // Assert
        Assert.Equal(OwnerProcessState.Gone, state);
    }
}