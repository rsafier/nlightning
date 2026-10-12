namespace NLightning.Testing.Cluster.Tests.Nodes.BitcoinCore;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Nodes.BitcoinCore;
using Cluster.Run;

public class BitcoinCoreWorkloadTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "chain" }, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Given_TheDefaults_When_Built_Then_ItIsARegtestMinerWithDataRpcAndZmq()
    {
        // Act
        var workload = BitcoinCoreWorkload.Build(new BitcoinCoreOptions());

        // Assert
        Assert.Equal("miner", workload.Name);
        Assert.Equal(NodeKind.BitcoinCore, workload.Kind);
        Assert.Equal(ImageVersions.BitcoinCore, workload.Image);
        Assert.Null(workload.Command);
        Assert.Equal(new DataVolume("/home/bitcoin/.bitcoin", "2Gi"), workload.Data);
        Assert.Equal(WorkloadResources.Default, workload.Resources);
        Assert.Equal(30, workload.TerminationGracePeriodSeconds);
        Assert.Equal(
        [
            ("rpc", 18443), ("p2p", 18444), ("zmq-block", 28332), ("zmq-tx", 28333), ("zmq-hashblock", 28334)
        ], workload.Ports.Select(p => (p.Name, p.Port)));
        Assert.Equal("-regtest", workload.Args[0]);
        Assert.Contains("-rpcuser=nltg", workload.Args);
        Assert.Contains("-rpcpassword=nltg", workload.Args);
        Assert.Contains("-rpcbind=0.0.0.0", workload.Args);
        Assert.Contains("-rpcallowip=0.0.0.0/0", workload.Args);
        Assert.Contains("-datadir=/home/bitcoin/.bitcoin", workload.Args);
        Assert.Contains("-zmqpubrawblock=tcp://0.0.0.0:28332", workload.Args);
        Assert.Contains("-zmqpubrawtx=tcp://0.0.0.0:28333", workload.Args);
        Assert.Contains("-zmqpubhashblock=tcp://0.0.0.0:28334", workload.Args);
        Assert.Contains("-txindex=1", workload.Args);
        Assert.Contains("-fallbackfee=0.0002", workload.Args);
        Assert.Contains("-listen=1", workload.Args);
        Assert.DoesNotContain(workload.Args, a => a.StartsWith("-addnode", StringComparison.Ordinal));
    }

    [Fact]
    public void Given_TheDefaults_When_Built_Then_ReadinessMeansRpcAnswers()
    {
        // Act
        var probe = BitcoinCoreWorkload.Build(new BitcoinCoreOptions()).ReadinessProbe;

        // Assert
        Assert.NotNull(probe);
        Assert.Equal(
        [
            "bitcoin-cli", "-regtest", "-rpcport=18443", "-rpcuser=nltg", "-rpcpassword=nltg", "-rpcclienttimeout=4",
            "getblockchaininfo"
        ], probe.Exec.Command);
        Assert.Equal(1, probe.PeriodSeconds);
    }

    [Fact]
    public void Given_AFollowerWithOptions_When_Built_Then_TheyBecomeArguments()
    {
        // Arrange
        var options = new BitcoinCoreOptions
        {
            Name = "follower",
            Image = ImageVersions.BitcoinCore31,
            Wallet = null,
            TxIndex = false,
            FallbackFeeBtcPerKvB = null,
            AddNodes = ["miner", "other:19000"],
            ExtraArgs = ["-minrelaytxfee=0.00000100"],
            RpcUser = "u",
            RpcPassword = "p",
            DataSize = "512Mi",
            StorageClassName = "local-path",
            ReadinessPeriodSeconds = 3
        };

        // Act
        var workload = BitcoinCoreWorkload.Build(options);

        // Assert
        Assert.Equal("follower", workload.Name);
        Assert.Equal(ImageVersions.BitcoinCore31, workload.Image);
        Assert.DoesNotContain("-txindex=1", workload.Args);
        Assert.DoesNotContain(workload.Args, a => a.StartsWith("-fallbackfee", StringComparison.Ordinal));
        Assert.Contains("-addnode=miner:18444", workload.Args);
        Assert.Contains("-addnode=other:19000", workload.Args);
        Assert.Equal("-minrelaytxfee=0.00000100", workload.Args[^1]);
        Assert.Contains("-rpcuser=u", workload.Args);
        Assert.Equal(new DataVolume("/home/bitcoin/.bitcoin", "512Mi", "local-path"), workload.Data);
        Assert.Contains("-rpcuser=u", workload.ReadinessProbe!.Exec.Command);
        Assert.Equal(3, workload.ReadinessProbe.PeriodSeconds);
    }

    [Theory]
    [InlineData("", "p")]
    [InlineData("u", " ")]
    public void Given_NoCredentials_When_Built_Then_ItIsRefused(string user, string password)
    {
        // Act / Assert
        Assert.ThrowsAny<ArgumentException>(
            () => BitcoinCoreWorkload.Build(new BitcoinCoreOptions { RpcUser = user, RpcPassword = password }));
    }

    [Fact]
    public void Given_TheWorkload_When_BuiltForARun_Then_TheStatefulSetRunsBitcoindWithAPvc()
    {
        // Act
        var manifests = BitcoinCoreWorkload.Build(new BitcoinCoreOptions()).Build(s_run);

        // Assert
        var container = Assert.Single(manifests.StatefulSet.Spec.Template.Spec.Containers);
        Assert.Equal("miner", container.Name);
        Assert.Equal(ImageVersions.BitcoinCore.Reference, container.Image);
        Assert.Null(container.Command);
        Assert.Equal("-regtest", container.Args[0]);
        Assert.Equal("data", Assert.Single(manifests.StatefulSet.Spec.VolumeClaimTemplates).Metadata.Name);
        Assert.Equal("/home/bitcoin/.bitcoin", Assert.Single(container.VolumeMounts).MountPath);
        Assert.Equal("bitcoincore", manifests.StatefulSet.Metadata.Labels[RunLabels.Kind]);
        Assert.Equal(5, manifests.Service.Spec.Ports.Count);
        Assert.Equal("None", manifests.Service.Spec.ClusterIP);
    }

    [Fact]
    public void Given_ACall_When_BuildingTheCliCommand_Then_ItUsesTheNodesCredentials()
    {
        // Act
        var command = BitcoinCoreWorkload.CliCommand(new BitcoinCoreOptions(), "miner", "getbalances");

        // Assert
        Assert.Equal(
        [
            "bitcoin-cli", "-regtest", "-rpcport=18443", "-rpcuser=nltg", "-rpcpassword=nltg", "-rpcwallet=miner",
            "-named", "getbalances"
        ], command);
    }
}