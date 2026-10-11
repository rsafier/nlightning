namespace NLightning.Testing.Cluster.Tests.Nodes.Cln;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Nodes.Cln;
using Cluster.Run;

public class ClnNodeTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "cln" }, DateTimeOffset.UnixEpoch);

    private static ClnNodeOptions Options() =>
        new() { BitcoindHost = "miner", BitcoindRpcUser = "nltg", BitcoindRpcPassword = "pw" };

    [Fact]
    public void Given_AClnNode_When_ItsWorkloadIsBuilt_Then_ItRunsThePinnedImageOnRegtestWithItsDataOnAPvc()
    {
        // Act
        var workload = ClnNode.Workload("alice", Options());

        // Assert
        Assert.Equal(NodeKind.Cln, workload.Kind);
        Assert.Equal(ImageVersions.Cln, workload.Image);
        Assert.Equal("regtest", workload.Env["LIGHTNINGD_NETWORK"]);
        Assert.Equal(new DataVolume("/root/.lightning", "1Gi"), workload.Data);
        Assert.Equal(new WorkloadPort("p2p", 9735), Assert.Single(workload.Ports));
        Assert.Equal(WorkloadResources.Default, workload.Resources);
    }

    [Fact]
    public void Given_ClnOptions_When_TheArgsAreBuilt_Then_TheyMatchTheDockerFixture()
    {
        // Act
        var args = ClnNode.BuildArgs("alice", Options() with { ExtraArgs = ["--experimental-splicing"] });

        // Assert
        Assert.Equal(
        [
            "--bitcoin-rpcconnect=miner", "--bitcoin-rpcport=18443", "--bitcoin-rpcuser=nltg",
            "--bitcoin-rpcpassword=pw", "--bind-addr=0.0.0.0:9735", "--alias=alice", "--log-level=debug",
            "--developer", "--dev-bitcoind-poll=1", "--ignore-fee-limits=false", "--experimental-splicing"
        ], args);
    }

    [Fact]
    public void Given_FeeLimitsNotEnforcedAndAnAlias_When_TheArgsAreBuilt_Then_TheFlagIsLeftOutAndTheAliasUsed()
    {
        // Act
        var args = ClnNode.BuildArgs("alice", Options() with { EnforceFeeLimits = false, Alias = "Alice CLN" });

        // Assert
        Assert.DoesNotContain("--ignore-fee-limits=false", args);
        Assert.Contains("--alias=Alice CLN", args);
    }

    [Fact]
    public void Given_AClnWorkload_When_Built_Then_ItDrainsBeforeItStopsAndItsProbeHonorsTheDrain()
    {
        // Act
        var container = ClnNode.Workload("alice", Options()).Build(s_run).StatefulSet.Spec.Template.Spec
                               .Containers[0];

        // Assert
        Assert.Equal(ClnNode.StopCommand, container.Lifecycle.PreStop.Exec.Command);
        var stop = ClnNode.StopCommand[2];
        Assert.True(stop.IndexOf($"touch {ClnNode.DrainFile}", StringComparison.Ordinal)
                  < stop.IndexOf("lightning-cli --network=regtest stop", StringComparison.Ordinal));
        Assert.Contains($"sleep {ClnNode.DrainSeconds}", stop);
        Assert.Equal(ClnNode.ReadinessCommand, container.ReadinessProbe.Exec.Command);
        Assert.Contains($"[ ! -f {ClnNode.DrainFile} ]", ClnNode.ReadinessCommand[2]);
        Assert.Contains("getinfo", ClnNode.ReadinessCommand[2]);
        Assert.Equal(1, container.ReadinessProbe.PeriodSeconds);
        Assert.True(ClnNode.DrainSeconds > container.ReadinessProbe.PeriodSeconds
                                         * container.ReadinessProbe.FailureThreshold);
    }

    [Fact]
    public void Given_ProcessFaults_When_TheWorkloadIsBuilt_Then_ThePodSharesItsProcessNamespaceAndKeepsItsPreStop()
    {
        // Act
        var plain = ClnNode.Workload("alice", Options()).Build(s_run).StatefulSet.Spec.Template.Spec;
        var faulty = ClnNode.Workload("alice", Options() with { ProcessFaults = true }).Build(s_run).StatefulSet.Spec
                            .Template.Spec;

        // Assert
        Assert.NotEqual(true, plain.ShareProcessNamespace);
        Assert.True(faulty.ShareProcessNamespace);
        Assert.Equal(ClnNode.StopCommand, faulty.Containers[0].Lifecycle.PreStop.Exec.Command);
    }
}