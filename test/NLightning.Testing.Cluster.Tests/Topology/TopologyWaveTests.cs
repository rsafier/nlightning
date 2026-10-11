using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Topology;

using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Run;
using Cluster.Topology;
using Run;

/// <summary>
/// The deployment waves of <see cref="TopologyDeployer"/> without a cluster: an adopted run on a fake API, a chain
/// whose readiness the test controls, and deployers that record what they saw when they started.
/// </summary>
public class TopologyWaveTests
{
    private static async Task<(FakeKubeApi Api, TestRun Run)> AdoptedRunAsync(CancellationToken ct)
    {
        var options = new TestRunOptions { RunId = "wave1", Suite = "wave", AdoptNamespace = true };
        var api = new FakeKubeApi();
        api.AddNamespace(RunIdentity.Create(options, DateTimeOffset.UtcNow));
        return (api, await TestRun.StartAsync(api.CreateClient(), options, ct));
    }

    /// <summary>A chain that turns ready when <see cref="Release"/> is called (or fails with <see cref="Error"/>).</summary>
    private sealed class GatedChain
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Error { get; init; }

        public void Release() => _gate.TrySetResult();

        /// <summary>The <see cref="TopologyBuilder.ChainFactory"/> (it needs neither the run nor the timeout).</summary>
        public TopologyBuilder.ChainFactory DeployAsync => (_, chainNode, _, cancellationToken) =>
            DeployChainAsync(chainNode, cancellationToken);

        private async Task<ITopologyChain> DeployChainAsync(TopologyNodeSpec chainNode,
                                                            CancellationToken cancellationToken)
        {
            if (Error is not null)
            {
                await Task.Delay(50, cancellationToken);
                throw Error;
            }

            await _gate.Task.WaitAsync(cancellationToken);
            return new FakeChain(new FakeNodeHandle(chainNode.Name, NodeKind.BitcoinCore)) { Tip = 101 };
        }

        public static ITopologyChainEndpoint Endpoint(TopologyNodeSpec chainNode) => new FakeEndpoint(chainNode.Name);
    }

    private sealed class FakeEndpoint(string host) : ITopologyChainEndpoint
    {
        public string RpcHost { get; } = host;

        public int RpcPort => 18443;

        public string RpcUser => "user";

        public string RpcPassword => "secret";

        public int ZmqRawBlockPort => 28332;

        public int ZmqRawTxPort => 28333;

        public V1Container CreateStartupWait() => new() { Name = "wait-for-chain" };
    }

    /// <summary>Records whether the chain was ready when it started; with <see cref="OnStart"/> it can act first.</summary>
    private sealed class RecordingDeployer(NodeKind kind, bool withChain) : ILightningNodeDeployer
    {
        public NodeKind Kind { get; } = kind;

        public bool DeploysWithChain { get; } = withChain;

        public bool? ChainReadyAtStart { get; private set; }

        public string? EndpointHost { get; private set; }

        public bool Cancelled { get; private set; }

        public Func<TopologyDeployContext, CancellationToken, Task>? OnStart { get; init; }

        public async Task<ITopologyLightningNode> DeployAsync(TopologyDeployContext context, TopologyNodeSpec node,
                                                              CancellationToken cancellationToken)
        {
            ChainReadyAtStart = context.IsChainReady;
            EndpointHost = context.ChainEndpoint.RpcHost;
            try
            {
                if (OnStart is not null)
                    await OnStart(context, cancellationToken);
                await context.WaitForChainAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            return new FakeLightningNode(node.Name) { Height = 101 };
        }
    }

    [Fact]
    public async Task Given_ADeployerThatDeploysWithTheChain_When_Built_Then_ItStartsBeforeTheChainIsReadyAndTheOtherAfter()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (api, run) = await AdoptedRunAsync(ct);
        using var _ = api;
        await using var __ = run;
        var chain = new GatedChain();
        var early = new RecordingDeployer(NodeKind.Cln, withChain: true)
        {
            // The chain turns ready only once this node has started: proves the wave is shared
            OnStart = (_, _) =>
            {
                chain.Release();
                return Task.CompletedTask;
            }
        };
        var late = new RecordingDeployer(NodeKind.Lnd, withChain: false);

        // Act
        using var topology = await new TopologyBuilder()
                                   .UseChain(chain.DeployAsync, GatedChain.Endpoint)
                                   .UseDeployer(early)
                                   .UseDeployer(late)
                                   .AddBitcoinCore("miner")
                                   .AddCln("alice")
                                   .AddLnd("bob")
                                   .BuildAsync(run, ct);

        // Assert
        Assert.False(early.ChainReadyAtStart);
        Assert.Equal("miner", early.EndpointHost);
        Assert.True(late.ChainReadyAtStart);
        Assert.Equal(["alice", "bob"], topology.Nodes.Keys.Order());
        Assert.Contains("chain", topology.Timings.Keys);
        Assert.Contains("nodes", topology.Timings.Keys);
        Assert.True(topology.Timings["nodes"] >= topology.Timings["chain"]);
    }

    [Fact]
    public async Task Given_DeployNodesWithChainOff_When_Built_Then_EveryNodeStartsOnceTheChainIsReady()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (api, run) = await AdoptedRunAsync(ct);
        using var _ = api;
        await using var __ = run;
        var chain = new GatedChain();
        chain.Release();
        var early = new RecordingDeployer(NodeKind.Cln, withChain: true);

        // Act
        using var topology = await new TopologyBuilder { DeployNodesWithChain = false }
                                   .UseChain(chain.DeployAsync, GatedChain.Endpoint)
                                   .UseDeployer(early)
                                   .AddBitcoinCore("miner")
                                   .AddCln("alice")
                                   .BuildAsync(run, ct);

        // Assert
        Assert.True(early.ChainReadyAtStart);
    }

    [Fact]
    public async Task Given_AChainWithoutAnEndpoint_When_Built_Then_EveryNodeStartsOnceTheChainIsReady()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (api, run) = await AdoptedRunAsync(ct);
        using var _ = api;
        await using var __ = run;
        var chain = new GatedChain();
        chain.Release();
        var early = new RecordingDeployer(NodeKind.Cln, withChain: true);

        // Act
        using var topology = await new TopologyBuilder()
                                   .UseChain(chain.DeployAsync)
                                   .UseDeployer(early)
                                   .AddBitcoinCore("miner")
                                   .AddCln("alice")
                                   .BuildAsync(run, ct);

        // Assert
        Assert.True(early.ChainReadyAtStart);
    }

    [Fact]
    public async Task Given_TheChainFails_When_ANodeStartsWithIt_Then_TheChainErrorIsThrownAndTheNodeIsCancelled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (api, run) = await AdoptedRunAsync(ct);
        using var _ = api;
        await using var __ = run;
        var chain = new GatedChain { Error = new InvalidOperationException("bitcoind broke") };
        var early = new RecordingDeployer(NodeKind.Cln, withChain: true)
        {
            // A node that would wait for its whole ready timeout without the fail-fast
            OnStart = (_, token) => Task.Delay(Timeout.Infinite, token)
        };

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => new TopologyBuilder()
                             .UseChain(chain.DeployAsync, GatedChain.Endpoint)
                             .UseDeployer(early)
                             .AddBitcoinCore("miner")
                             .AddCln("alice")
                             .BuildAsync(run, ct)
                             .WaitAsync(TimeSpan.FromSeconds(10), ct));

        // Assert
        Assert.Equal("bitcoind broke", error.Message);
        Assert.True(early.Cancelled);
    }

    [Fact]
    public async Task Given_AContextWhileTheChainStarts_When_ChainIsRead_Then_ItSaysToUseTheEndpoint()
    {
        // Arrange
        var (api, run) = await AdoptedRunAsync(TestContext.Current.CancellationToken);
        using var _ = api;
        await using var __ = run;
        var pending = new TaskCompletionSource<ITopologyChain>();
        var context = new TopologyDeployContext(run, new FakeEndpoint("miner"), pending.Task, TimeSpan.FromMinutes(1),
                                                null);

        // Act
        var error = Assert.Throws<InvalidOperationException>(() => context.Chain);

        // Assert
        Assert.False(context.IsChainReady);
        Assert.Contains(nameof(TopologyDeployContext.ChainEndpoint), error.Message);
    }

    [Theory]
    [InlineData(null, null, NodeStorage.Persistent)]
    [InlineData(NodeStorage.Ephemeral, null, NodeStorage.Ephemeral)]
    [InlineData(NodeStorage.Ephemeral, NodeStorage.Persistent, NodeStorage.Persistent)]
    public void Given_StorageOnTheBuilderAndTheNode_When_Built_Then_TheNodeWinsOverTheBuilder(
        NodeStorage? builderStorage, NodeStorage? nodeStorage, NodeStorage expected)
    {
        // Arrange
        var builder = new TopologyBuilder { Storage = builderStorage ?? NodeStorage.Persistent }
                     .AddBitcoinCore("miner")
                     .AddCln("alice", storage: nodeStorage);

        // Act
        var spec = builder.Build();

        // Assert
        Assert.Equal(expected, spec.GetNode("alice").Storage);
        Assert.Equal(builderStorage ?? NodeStorage.Persistent, spec.GetNode("miner").Storage);
    }
}