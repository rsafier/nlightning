using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Diagnostics;

using Cluster.Diagnostics;

public class ResourceDescriberTests
{
    private static V1Pod CrashingPod() =>
        new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = "alice-0",
                NamespaceProperty = "nltg-spike-r1",
                Labels = new Dictionary<string, string> { ["nltg.node"] = "alice", ["nltg.kind"] = "cln" }
            },
            Spec = new V1PodSpec
            {
                Containers =
                [
                    new V1Container
                    {
                        Name = "alice",
                        Image = "elementsproject/lightningd@sha256:abc",
                        Args = ["--bitcoin-rpcpassword=hunter2", "--no-such-option"],
                        Env =
                        [
                            new V1EnvVar { Name = "LIGHTNINGD_NETWORK", Value = "regtest" },
                            new V1EnvVar { Name = "RPC_PASSWORD", Value = "hunter2" }
                        ],
                        ReadinessProbe = new V1Probe
                        {
                            Exec = new V1ExecAction { Command = ["lightning-cli", "getinfo"] }, PeriodSeconds = 1
                        }
                    }
                ],
                Volumes = [new V1Volume { Name = "data", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = "data-alice-0" } }]
            },
            Status = new V1PodStatus
            {
                Phase = "Running",
                PodIP = "10.42.0.7",
                Conditions = [new V1PodCondition { Type = "Ready", Status = "False", Reason = "ContainersNotReady" }],
                ContainerStatuses =
                [
                    new V1ContainerStatus
                    {
                        Name = "alice",
                        Ready = false,
                        RestartCount = 2,
                        State = new V1ContainerState
                        {
                            Waiting = new V1ContainerStateWaiting { Reason = "CrashLoopBackOff", Message = "back-off 20s" }
                        },
                        LastState = new V1ContainerState
                        {
                            Terminated = new V1ContainerStateTerminated { Reason = "Error", ExitCode = 1 }
                        }
                    }
                ]
            }
        };

    [Fact]
    public void Given_ACrashingPod_When_Described_Then_StateRestartsAndLastExitAreShownAndSecretsMasked()
    {
        // Act
        var text = ResourceDescriber.DescribePod(CrashingPod());

        // Assert
        Assert.Contains("Name:         alice-0", text);
        Assert.Contains("Ready                      False", text);
        Assert.Contains("waiting (CrashLoopBackOff) back-off 20s", text);
        Assert.Contains("Last state: terminated (Error, exit 1)", text);
        Assert.Contains("Restarts: 2", text);
        Assert.Contains("--no-such-option", text);
        Assert.Contains("LIGHTNINGD_NETWORK=regtest", text);
        Assert.Contains("data: pvc data-alice-0", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public void Given_EventsOutOfOrder_When_Described_Then_TheyAreSortedByTime()
    {
        // Arrange
        var t0 = new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc);
        Corev1Event Event(string name, DateTime? last, DateTime? first, string reason) =>
            new()
            {
                Metadata = new V1ObjectMeta { Name = name },
                LastTimestamp = last,
                FirstTimestamp = first,
                Type = "Normal",
                Reason = reason,
                Message = $"{reason} message",
                InvolvedObject = new V1ObjectReference { Kind = "Pod", Name = "alice-0" }
            };

        var events = new[]
        {
            Event("c", t0.AddSeconds(30), null, "BackOff"),
            Event("a", null, t0, "Scheduled"),
            Event("b", t0.AddSeconds(5), t0.AddSeconds(1), "Pulled")
        };

        // Act
        var lines = ResourceDescriber.DescribeEvents(events).TrimEnd().Split('\n');

        // Assert
        Assert.Equal(3, lines.Length);
        Assert.Contains("Scheduled", lines[0]);
        Assert.Contains("Pulled", lines[1]);
        Assert.Contains("BackOff", lines[2]);
        Assert.StartsWith("2026-10-02T10:00:00.000Z", lines[0]);
        Assert.Contains("Pod/alice-0: BackOff message", lines[2]);
    }

    [Fact]
    public void Given_AClaimAndItsVolume_When_Described_Then_BothAreShown()
    {
        // Arrange
        var claim = new V1PersistentVolumeClaim
        {
            Metadata = new V1ObjectMeta { Name = "data-alice-0" },
            Spec = new V1PersistentVolumeClaimSpec
            {
                VolumeName = "pvc-123",
                StorageClassName = "local-path",
                AccessModes = ["ReadWriteOnce"]
            },
            Status = new V1PersistentVolumeClaimStatus { Phase = "Bound" }
        };
        var pending = new V1PersistentVolumeClaim
        {
            Metadata = new V1ObjectMeta { Name = "data-bob-0" },
            Spec = new V1PersistentVolumeClaimSpec { StorageClassName = "local-path" },
            Status = new V1PersistentVolumeClaimStatus { Phase = "Pending" }
        };
        var volume = new V1PersistentVolume
        {
            Spec = new V1PersistentVolumeSpec
            {
                HostPath = new V1HostPathVolumeSource { Path = "/var/lib/rancher/pvc-123" },
                PersistentVolumeReclaimPolicy = "Delete"
            },
            Status = new V1PersistentVolumeStatus { Phase = "Bound" }
        };

        // Act
        var text = ResourceDescriber.DescribeStorage([pending, claim],
                                                     new Dictionary<string, V1PersistentVolume> { ["pvc-123"] = volume });

        // Assert
        Assert.Contains("PVC data-alice-0: phase=Bound volume=pvc-123 class=local-path", text);
        Assert.Contains("PV pvc-123: phase=Bound reclaim=Delete source=hostPath /var/lib/rancher/pvc-123", text);
        Assert.Contains("PVC data-bob-0: phase=Pending volume=-", text);
        Assert.True(text.IndexOf("data-alice-0", StringComparison.Ordinal)
                  < text.IndexOf("data-bob-0", StringComparison.Ordinal));
    }

    [Fact]
    public void Given_NoClaims_When_Described_Then_ItSaysSo()
    {
        // Act & Assert
        Assert.Equal("no PVCs\n", ResourceDescriber.DescribeStorage([], new Dictionary<string, V1PersistentVolume>()));
    }
}