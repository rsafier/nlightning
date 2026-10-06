namespace NLightning.Testing.Cluster.Tests.Nodes.Rbitcoin;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Nodes.Rbitcoin;

/// <summary>The rbitcoin workload of the contract test (NL-1095).</summary>
public class RbitcoinNodeTests
{
    [Fact]
    public void Given_Options_When_TheWorkloadIsBuilt_Then_ItWritesTheCookieAndFollowsThePeerOverRpcWithBasicAuth()
    {
        // Act
        var workload = RbitcoinNode.Workload(new RbitcoinNodeOptions
        {
            Connect = "miner:18444",
            MinRelayTxFeeBtcPerKvB = 0.00005m
        });

        // Assert
        Assert.Equal("rbitcoin", workload.Name);
        Assert.Equal(NodeKind.Other, workload.Kind);
        Assert.Equal(ImageVersions.Rbitcoin, workload.Image);
        Assert.Equal(ImagePullPolicy.Never, ImageVersions.Rbitcoin.PullPolicy);
        Assert.Equal(new WorkloadPort("rpc", 18443), Assert.Single(workload.Ports));
        var command = Assert.IsType<List<string>>(workload.Command);
        Assert.Equal(["sh", "-c"], command.Take(2));
        // No trailing newline in the cookie: rbitcoin refuses one
        Assert.Contains("printf '%s' 'nltg:nltg' > /data/rpc.cookie", command[2], StringComparison.Ordinal);
        Assert.Contains("exec rbitcoin-node", command[2], StringComparison.Ordinal);
        var args = RbitcoinNode.BuildArgs(new RbitcoinNodeOptions { Connect = "miner:18444" });
        Assert.Equal(["--network", "regtest"], args.Take(2));
        Assert.Contains("--rpc-cookie-file", args);
        Assert.Equal("miner:18444", args[args.ToList().IndexOf("--connect") + 1]);
        Assert.DoesNotContain("--min-relay-tx-fee", args);
        Assert.Contains("0.00005", command[2], StringComparison.Ordinal);
        Assert.Contains("nltg:nltg", RbitcoinNode.ReadinessCommand(new RbitcoinNodeOptions { Connect = "x" }));
    }
}