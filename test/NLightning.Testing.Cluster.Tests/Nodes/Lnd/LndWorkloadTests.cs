namespace NLightning.Testing.Cluster.Tests.Nodes.Lnd;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Nodes.Lnd;
using Cluster.Run;

public class LndWorkloadTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "lnd" }, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Given_DefaultOptions_When_Built_Then_TheNodeRunsTheLocalCustomLndImageWithItsDataOnAPvc()
    {
        // Act
        var workload = LndWorkload.Build(new LndNodeOptions("alice"));

        // Assert
        Assert.Equal("alice", workload.Name);
        Assert.Equal(NodeKind.Lnd, workload.Kind);
        Assert.Equal("custom_lnd:latest", workload.Image.Reference);
        Assert.Equal(ImagePullPolicy.Never, workload.Image.PullPolicy);
        Assert.NotNull(workload.Data);
        Assert.Equal("/home/lnd/.lnd", workload.Data.MountPath);
        Assert.Null(workload.Command);
        Assert.Equal(["p2p", "grpc", "rest"], workload.Ports.Select(p => p.Name));
        Assert.Equal([9735, 10009, 8080], workload.Ports.Select(p => p.Port));
    }

    [Fact]
    public void Given_ALndNode_When_Built_Then_ItIsReadyOnlyWhenGetInfoReportsSyncedToChain()
    {
        // Act
        var probe = LndWorkload.Build(new LndNodeOptions("alice")).ReadinessProbe;

        // Assert
        Assert.NotNull(probe?.Exec);
        var script = probe.Exec.Command[^1];
        Assert.Equal(["sh", "-c"], probe.Exec.Command.Take(2));
        Assert.Contains("lncli --lnddir=/home/lnd/.lnd --network=regtest", script);
        Assert.Contains("getinfo", script);
        Assert.Contains("synced_to_chain", script);
    }

    [Fact]
    public void Given_Options_When_TheArgsAreBuilt_Then_LndFollowsTheTopologysBitcoindByItsServiceName()
    {
        // Arrange
        var options = new LndNodeOptions("bob") { BitcoindHost = "chain", BitcoindRpcPort = 18000 };

        // Act
        var args = LndWorkload.BuildArgs(options);

        // Assert
        Assert.Equal("lnd", args[0]);
        Assert.Contains("--alias=bob", args);
        Assert.Contains("--noseedbackup", args);
        Assert.Contains("--bitcoin.regtest", args);
        Assert.Contains("--bitcoin.node=bitcoind", args);
        Assert.Contains("--bitcoind.rpchost=chain:18000", args);
        Assert.Contains("--bitcoind.zmqpubrawblock=tcp://chain:28334", args);
        Assert.Contains("--bitcoind.zmqpubrawtx=tcp://chain:28335", args);
        Assert.Contains("--tlsextradomain=bob", args);
        Assert.Contains("--tlsextradomain=bob-0.bob", args);
        Assert.Contains("--rpclisten=0.0.0.0:10009", args);
        Assert.Contains("--accept-keysend", args);
        Assert.DoesNotContain(args, a => a.Contains("nltg-spike", StringComparison.Ordinal));
    }

    [Fact]
    public void Given_ExtraArgsAndNoKeysend_When_TheArgsAreBuilt_Then_TheExtrasComeLastAndKeysendIsOff()
    {
        // Arrange
        var options = new LndNodeOptions("alice")
        {
            AcceptKeysend = false,
            ExtraArgs = ["--protocol.rbf-coop-close", "--protocol.simple-taproot-chans"]
        };

        // Act
        var args = LndWorkload.BuildArgs(options);

        // Assert
        Assert.DoesNotContain("--accept-keysend", args);
        Assert.Equal(["--protocol.rbf-coop-close", "--protocol.simple-taproot-chans"], args.TakeLast(2));
    }

    [Fact]
    public void Given_ALndNode_When_ItsManifestsAreBuilt_Then_TheContainerKeepsTheImageEntrypointAndGetsTheArgs()
    {
        // Act
        var manifests = LndWorkload.Build(new LndNodeOptions("alice")).Build(s_run);

        // Assert
        var container = Assert.Single(manifests.StatefulSet.Spec.Template.Spec.Containers);
        Assert.Null(container.Command);
        Assert.Equal("lnd", container.Args[0]);
        Assert.Equal("Never", container.ImagePullPolicy);
        Assert.Equal("/home/lnd/.lnd", Assert.Single(container.VolumeMounts).MountPath);
        Assert.Single(manifests.StatefulSet.Spec.VolumeClaimTemplates);
        Assert.Equal(3, manifests.Service.Spec.Ports.Count);
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("al_ice")]
    [InlineData("")]
    public void Given_AnInvalidAlias_When_OptionsAreCreated_Then_TheyThrow(string alias)
    {
        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => new LndNodeOptions(alias));
    }

    [Fact]
    public void Given_TheFileConstants_When_Read_Then_TheyAreLndsRegtestPaths()
    {
        // Assert
        Assert.Equal("/home/lnd/.lnd/tls.cert", LndWorkload.TlsCertPath);
        Assert.Equal("/home/lnd/.lnd/data/chain/bitcoin/regtest/admin.macaroon", LndWorkload.AdminMacaroonPath);
        Assert.Equal(WorkloadResources.Default, new LndNodeOptions("alice").Resources);
    }
}