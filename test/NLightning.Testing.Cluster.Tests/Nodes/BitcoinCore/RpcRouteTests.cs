using System.Net;
using System.Text;
using k8s;
using Newtonsoft.Json.Linq;

namespace NLightning.Testing.Cluster.Tests.Nodes.BitcoinCore;

using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Nodes.BitcoinCore;
using Cluster.Nodes.BitcoinCore.Rpc;

public class RpcRouteTests
{
    [Theory]
    [InlineData(RpcRoute.Auto, true, true, RpcRoute.ServiceDns)]
    [InlineData(RpcRoute.Auto, true, false, RpcRoute.ServiceDns)]
    [InlineData(RpcRoute.Auto, false, true, RpcRoute.PodIp)]
    [InlineData(RpcRoute.Auto, false, false, RpcRoute.Exec)]
    [InlineData(RpcRoute.PodIp, true, false, RpcRoute.PodIp)]
    [InlineData(RpcRoute.Exec, false, true, RpcRoute.Exec)]
    [InlineData(RpcRoute.ServiceDns, false, true, RpcRoute.ServiceDns)]
    public void Given_ARequestedRoute_When_Selected_Then_AutoFollowsWhereTheProcessRuns(
        RpcRoute requested, bool inCluster, bool podIpReachable, RpcRoute expected)
    {
        // Act / Assert
        Assert.Equal(expected, RpcRouteSelector.Select(requested, inCluster, podIpReachable));
    }

    [Theory]
    [InlineData("192.168.194.43", "http://192.168.194.43:18443")]
    [InlineData("fd07:b51a:cc66::5", "http://[fd07:b51a:cc66::5]:18443")]
    [InlineData("miner.ns.svc.cluster.local", "http://miner.ns.svc.cluster.local:18443")]
    public void Given_AHost_When_BuildingTheUri_Then_Ipv6IsBracketed(string host, string expected)
    {
        // Act / Assert
        Assert.Equal(expected, HttpRpcTransport.BuildUri(host, 18443));
    }

    [Fact]
    public void Given_AHostThatChanges_When_TheClientIsAskedFor_Then_ItFollowsTheNewHost()
    {
        // Arrange
        var host = "10.0.0.1";
        var transport = new HttpRpcTransport(() => host, 18443, new NetworkCredential("u", "p"), "miner");

        // Act
        var first = transport.GetClient();
        var same = transport.GetClient();
        host = "10.0.0.2";
        var moved = transport.GetClient();

        // Assert
        Assert.Same(first, same);
        Assert.NotSame(first, moved);
        Assert.Equal("10.0.0.2", moved.Address.Host);
        Assert.Equal("http://10.0.0.2:18443/wallet/miner", transport.Description);
    }

    [Fact]
    public async Task Given_NothingListening_When_Called_Then_TheErrorHasNoRpcCode()
    {
        // Arrange: a closed local port
        var ct = TestContext.Current.CancellationToken;
        var transport = new HttpRpcTransport(() => "127.0.0.1", 1, new NetworkCredential("u", "p"), null);

        // Act
        var e = await Assert.ThrowsAsync<BitcoinRpcException>(() => transport.CallAsync("getblockcount", null, ct));

        // Assert
        Assert.Null(e.Code);
        Assert.Equal("getblockcount", e.Method);
        Assert.Contains("not reachable", e.RpcMessage);
    }

    [Fact]
    public async Task Given_AnExecTransport_When_Called_Then_BitcoinCliRunsInTheNodesContainer()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = new FakeNodeHandle(new ExecResult(0, Encoding.UTF8.GetBytes("101\n"), []));
        var transport = new ExecCliRpcTransport(node, 18443, "u", "p", "miner");

        // Act
        var result = await transport.CallAsync("getblockcount", null, ct);

        // Assert
        Assert.Equal(101, result.Value<long>());
        Assert.Equal(["bitcoin-cli", "-regtest", "-rpcport=18443", "-rpcuser=u", "-rpcpassword=p", "-rpcwallet=miner", "-named", "getblockcount"],
                     Assert.Single(node.Commands));
        Assert.Equal("exec ns/miner-0 -rpcwallet=miner", transport.Description);
    }

    [Fact]
    public void Given_ADeployedNode_When_AddressesAreRead_Then_PodsUseTheAliasAndTheHostThePodIpOrDns()
    {
        // Arrange
        using var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://127.0.0.1:1" });
        var handle = new KubeNodeHandle(client, "nltg-spike-r1", "miner", NodeKind.BitcoinCore);
        var node = new BitcoinCoreNode(handle, new BitcoinCoreOptions());

        // Act / Assert
        Assert.Equal("http://miner:18443", node.ClusterRpcUrl);
        Assert.Equal("miner:18444", node.ClusterP2pAddress);
        Assert.Equal("tcp://miner:28332", node.ClusterZmqRawBlock);
        Assert.Equal("tcp://miner:28333", node.ClusterZmqRawTx);
        Assert.Equal("tcp://miner:28334", node.ClusterZmqHashBlock);
        Assert.Equal("miner.nltg-spike-r1.svc.cluster.local", node.GetHost(RpcRoute.ServiceDns));
        Assert.Throws<InvalidOperationException>(() => node.GetHost(RpcRoute.PodIp));
        Assert.Throws<ArgumentOutOfRangeException>(() => node.GetHost(RpcRoute.Exec));
        Assert.Throws<ArgumentOutOfRangeException>(() => node.CreateRpc(RpcRoute.Auto));
        Assert.Equal("exec nltg-spike-r1/miner-0 -rpcwallet=miner", node.CreateRpc(RpcRoute.Exec).Description);
        Assert.Equal("exec nltg-spike-r1/miner-0", node.CreateRpc(RpcRoute.Exec, null).Description);
        Assert.Equal("http://miner.nltg-spike-r1.svc.cluster.local:18443/wallet/miner",
                     node.CreateRpc(RpcRoute.ServiceDns).Description);
        Assert.Equal("nltg", node.Credentials.UserName);
    }

    [Fact]
    public void Given_AProbe_When_Printed_Then_ItSaysWhatWasReachable()
    {
        // Arrange
        var probe = new HostRouteProbe("10.0.0.5", true, TimeSpan.FromMilliseconds(2), "miner.ns.svc.cluster.local",
                                       false, "Name does not resolve");

        // Act / Assert
        Assert.Equal("pod IP 10.0.0.5: reachable (2 ms); miner.ns.svc.cluster.local: not reachable (Name does not resolve)",
                     probe.ToString());
    }

    private sealed class FakeNodeHandle(ExecResult result) : INodeHandle
    {
        public List<IReadOnlyList<string>> Commands { get; } = [];

        public string Name => "miner";

        public NodeKind Kind => NodeKind.BitcoinCore;

        public string Namespace => "ns";

        public string PodName => "miner-0";

        public string ContainerName => "miner";

        public string ServiceDnsName => "miner.ns.svc.cluster.local";

        public string PodDnsName => "miner-0.miner.ns.svc.cluster.local";

        public string? PodIp => null;

        public Task<ExecResult> ExecAsync(IReadOnlyList<string> command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(result);
        }

        public Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte[]> WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> ReadLogAsync(int? tailLines, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RestartAsync(TimeSpan readyTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task KillAsync(TimeSpan readyTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}