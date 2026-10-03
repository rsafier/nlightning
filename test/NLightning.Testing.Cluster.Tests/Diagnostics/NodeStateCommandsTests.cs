using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Diagnostics;

using Cluster.Diagnostics;
using Cluster.Nodes;
using Cluster.Nodes.BitcoinCore;
using Cluster.Run;

public class NodeStateCommandsTests
{
    private static V1Pod Pod(string node, NodeKind kind, params string[] args) =>
        new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"{node}-0",
                Labels = new Dictionary<string, string>
                {
                    [RunLabels.Node] = node,
                    [RunLabels.Kind] = RunLabels.KindValue(kind)
                }
            },
            Spec = new V1PodSpec
            {
                Containers = [new V1Container { Name = "sidecar" }, new V1Container { Name = node, Args = args }]
            }
        };

    [Fact]
    public void Given_ABitcoindPod_When_CommandsAreBuilt_Then_TheyUseItsOwnRpcSettings()
    {
        // Arrange
        var options = new BitcoinCoreOptions { RpcUser = "alice", RpcPassword = "s3cret" };
        var pod = Pod("miner", NodeKind.BitcoinCore, [.. BitcoinCoreWorkload.BuildArgs(options)]);

        // Act
        var commands = NodeStateCommands.For(pod);

        // Assert
        Assert.Equal(["getblockchaininfo.json", "getpeerinfo.json", "getmempoolinfo.json"],
                     commands.Select(c => c.FileName));
        var first = commands[0].Command;
        Assert.Equal("bitcoin-cli", first[0]);
        Assert.Contains("-chain=regtest", first);
        Assert.Contains($"-rpcport={BitcoinCorePorts.Rpc}", first);
        Assert.Contains("-rpcuser=alice", first);
        Assert.Contains("-rpcpassword=s3cret", first);
        Assert.Equal("getblockchaininfo", first[^1]);
        Assert.Equal("miner", NodeStateCommands.ContainerOf(pod));
    }

    [Fact]
    public void Given_AClnPod_When_CommandsAreBuilt_Then_TheyAreLightningCliReads()
    {
        // Act
        var commands = NodeStateCommands.For(Pod("alice", NodeKind.Cln));

        // Assert
        Assert.Equal(["getinfo.json", "listpeerchannels.json", "listfunds.json"], commands.Select(c => c.FileName));
        Assert.All(commands, c => Assert.Equal("lightning-cli", c.Command[0]));
        Assert.All(commands, c => Assert.Contains("--network=regtest", c.Command));
    }

    [Fact]
    public void Given_AnLndPod_When_CommandsAreBuilt_Then_TheyAreLncliReadsThatNeverPrintAMacaroon()
    {
        // Act
        var commands = NodeStateCommands.For(Pod("bob", NodeKind.Lnd));

        // Assert
        Assert.Equal(["getinfo.json", "listchannels.json", "pendingchannels.json", "listpeers.json"],
                     commands.Select(c => c.FileName));
        Assert.All(commands, c => Assert.Equal("lncli", c.Command[0]));
        Assert.All(commands, c => Assert.DoesNotContain(c.Command, a => a.Contains("macaroon", StringComparison.Ordinal)
                                                                    || a is "cat" or "bakemacaroon"
                                                                             or "printmacaroon"));
    }

    [Fact]
    public void Given_AnEclairPod_When_CommandsAreBuilt_Then_TheyAreApiReadsWithThePasswordFromTheEnvironment()
    {
        // Act
        var commands = NodeStateCommands.For(Pod("carol", NodeKind.Eclair));

        // Assert
        Assert.Equal(["getinfo.json", "channels.json", "peers.json", "onchainbalance.json"],
                     commands.Select(c => c.FileName));
        Assert.All(commands, c => Assert.Equal(["sh", "-c"], c.Command.Take(2)));
        Assert.All(commands, c => Assert.Contains("-u \":$NLTG_API_PASSWORD\"", c.Command[2], StringComparison.Ordinal));
        Assert.All(commands, c => Assert.Contains("http://127.0.0.1:8080/", c.Command[2], StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(NodeKind.Other)]
    [InlineData(NodeKind.Postgres)]
    public void Given_APodOfAKindWithoutState_When_CommandsAreBuilt_Then_ThereAreNone(NodeKind kind)
    {
        // Act & Assert
        Assert.Empty(NodeStateCommands.For(Pod("probe", kind)));
    }

    [Fact]
    public void Given_APodWithoutLabels_When_Read_Then_ItIsOtherInItsFirstContainer()
    {
        // Arrange
        var pod = new V1Pod { Spec = new V1PodSpec { Containers = [new V1Container { Name = "echo" }] } };

        // Act & Assert
        Assert.Equal(NodeKind.Other, NodeStateCommands.KindOf(pod));
        Assert.Equal("echo", NodeStateCommands.ContainerOf(pod));
        Assert.Empty(NodeStateCommands.For(pod));
    }
}