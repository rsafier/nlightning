using Microsoft.Extensions.DependencyInjection;
using NLightning.Domain.Channels.Enums;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.RemoteSigning.Tests;
using NLightning.Testing.Cluster.Nodes;
using NLightning.Testing.Cluster.Nodes.Lnd;
using NLightning.Testing.Cluster.Run;
using NLightning.Testing.Cluster.Topology;
using NLightning.Testing.Cluster.Topology.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using ClusterPoll = NLightning.Testing.Cluster.Poll;

namespace NLightning.Integration.Tests.Cluster.Live;

/// <summary>Concurrent hosted native nodes retain separate signer authority with shared Bitcoin infrastructure.</summary>
[Trait("Category", "Cluster")]
public sealed class HostedNativeNodesLndClusterTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);
    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_TwoHostedNativeNodes_When_OneSignerStopsAndBothRestart_Then_OtherNodeOperatesAndBothClose()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("hosted-native-lnd")
            with
        { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        await using var supervisor = new HostedNativeSignerSupervisor();
        var first = await supervisor.AddAsync("first", new string('a', 64));
        var second = await supervisor.AddAsync("second", new string('b', 64));
        using var a = new RemoteSignerConnection(first.Options());
        using var b = new RemoteSignerConnection(second.Options());
        var processes = new Dictionary<string, HostedNativeSignerSupervisor.HostedSigner>
        { ["first"] = first, ["second"] = second };
        var connections = new Dictionary<string, RemoteSignerConnection> { ["first"] = a, ["second"] = b };
        await using var deployer = new InProcessNodeDeployer
        {
            KeyManager = name => new RemoteSecureKeyManager(connections[name]),
            PodFacingHost = Environment.GetEnvironmentVariable("NLTG_ADOPT_NAMESPACE") == "1"
                ? System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                        .First(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString()
                : null,
            ConfigureNode = node =>
            {
                var process = processes[node.Name];
                var connection = connections[node.Name];
                node.RemoteSignerConnection = connection;
                node.ExtraConfiguration["Signing:Mode"] = "RemoteNative";
                node.ExtraConfiguration["Signing:SocketPath"] = process.SocketPath;
                node.ExtraConfiguration["Signing:AuthTokenFile"] = Path.Combine(process.DirectoryPath, "token");
                node.ExtraConfiguration["Signing:NodeId"] = connection.Context.NodeId;
                node.ExtraConfiguration["Signing:OwnerId"] = connection.Context.OwnerId;
                node.ExtraConfiguration["Signing:SignerId"] = connection.Context.SignerId;
            }
        };
        using var topology = await new TopologyBuilder
        { Log = Log, ReadyTimeout = TimeSpan.FromMinutes(4), StepTimeout = s_timeout }
            .AddBitcoinCore("miner").AddNLightning("first").AddNLightning("second")
            .AddLnd("alice").AddLnd("bob").UseInProcessNodes(deployer)
            .FundWallet("first", 2_000_000).FundWallet("second", 2_000_000)
            .AddChannel("first", "alice", 1_000_000, 300_000_000)
            .AddChannel("second", "bob", 1_000_000, 300_000_000).BuildAsync(run, ct);
        var nodeA = topology.InProcessNode("first");
        var nodeB = topology.InProcessNode("second");
        var alice = topology.Node<LndNode>("alice");
        var bob = topology.Node<LndNode>("bob");
        var identityA = await nodeA.GetNodeIdAsync(ct);
        var identityB = await nodeB.GetNodeIdAsync(ct);
        Assert.NotEqual(identityA, identityB);
        Assert.NotEqual(nodeA.TestNode.DatabaseFilePath, nodeB.TestNode.DatabaseFilePath);
        Assert.NotEqual(new RemoteSecureKeyManager(a).GetWalletPublicKey(0, false, NLightning.Domain.Bitcoin.Enums.AddressType.P2Wpkh),
                        new RemoteSecureKeyManager(b).GetWalletPublicKey(0, false, NLightning.Domain.Bitcoin.Enums.AddressType.P2Wpkh));
        Assert.NotEqual((await nodeA.GetAddressAsync(ct)).Port, (await nodeB.GetAddressAsync(ct)).Port);
        Assert.Equal(a.Context, nodeA.TestNode.Services.GetRequiredService<NLightning.Domain.Signing.NodeSigningContext>());
        Assert.Equal(b.Context, nodeB.TestNode.Services.GetRequiredService<NLightning.Domain.Signing.NodeSigningContext>());
        var fundingA = Assert.Single(await nodeA.ListChannelsAsync(ct)).FundingTxId;
        var fundingB = Assert.Single(await nodeB.ListChannelsAsync(ct)).FundingTxId;
        Assert.NotEqual(fundingA, fundingB);
        await PayBothWaysAsync(nodeA, alice, ct);
        await PayBothWaysAsync(nodeB, bob, ct);

        await first.StopAsync();
        Assert.Throws<RemoteSignerTransportException>(() => new RemoteSecureKeyManager(a).ReserveChannelKeyIndex());
        await PayBothWaysAsync(nodeB, bob, ct);
        await nodeA.TestNode.StopAsync();
        await first.RestartAsync();
        await nodeA.TestNode.StartAsync(ct);
        await nodeB.TestNode.StopAsync();
        await second.RestartAsync();
        await nodeB.TestNode.StartAsync(ct);
        await topology.WaitChannelsActiveAsync(s_timeout, ct);
        Assert.Equal(identityA, await nodeA.GetNodeIdAsync(ct));
        Assert.Equal(identityB, await nodeB.GetNodeIdAsync(ct));
        await PayBothWaysAsync(nodeA, alice, ct);
        await PayBothWaysAsync(nodeB, bob, ct);
        await CloseAndConfirmAsync(topology, nodeA, alice, fundingA, ct);
        await CloseAndConfirmAsync(topology, nodeB, bob, fundingB, ct);
        Log($"{run.Namespace}: distinct native hosted identities paid both ways, signer A outage preserved B, both restarted and closed");
    }

    private static async Task PayBothWaysAsync(ILightningTestPeer node, ILightningTestPeer peer, CancellationToken ct)
    {
        var (_, outgoing, _) = await LndPairTopology.PayAsync(node, peer, 10_000_123, s_timeout, ct);
        Assert.True(outgoing.Succeeded, outgoing.FailureReason);
        var (_, incoming, _) = await LndPairTopology.PayAsync(peer, node, 5_000_456, s_timeout, ct);
        Assert.True(incoming.Succeeded, incoming.FailureReason);
    }

    private static async Task CloseAndConfirmAsync(TestTopology topology, InProcessNode node, LndNode peer,
                                                   string funding, CancellationToken ct)
    {
        var channel = await node.FindChannelAsync(funding, ct);
        Assert.NotNull(channel);
        var transactionId = await node.CloseChannelAsync(channel.ChannelId, ct);
        await ClusterPoll.UntilDoneAsync(async cancellation =>
        {
            var pending = await peer.Lightning.PendingChannelsAsync(new PendingChannelsRequest(), cancellationToken: cancellation);
            return pending.WaitingCloseChannels.Any(item => item.ClosingTxid == transactionId)
                ? null : "peer has not registered the closing transaction";
        }, s_timeout, "peer closing watch", ct);
        var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
        await chain.Chain.WaitForMempoolAsync(transactionId, ct, s_timeout);
        await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
        var confirmed = await chain.Chain.WaitForConfirmationAsync(transactionId,
            TopologyDeployer.ConfirmationBlocks, ct, s_timeout);
        Assert.True(confirmed.Confirmations >= TopologyDeployer.ConfirmationBlocks);
        var transaction = await chain.Chain.Rpc.CallAsync("getrawtransaction", new Dictionary<string, object?>
        { ["txid"] = transactionId, ["verbose"] = true }, ct);
        Assert.Equal(transactionId, (string?)transaction["txid"]);
        Assert.Equal(funding, (string?)Assert.Single(transaction["vin"]!)["txid"]);
        await ClusterPoll.UntilDoneAsync(async cancellation =>
        {
            var ours = await node.FindChannelAsync(funding, cancellation);
            return ours is null || ours.State == ChannelState.Closed ? null : $"node remains {ours.State}";
        }, s_timeout, "node closed", ct);
        var closed = await ClusterPoll.ForAsync(async cancellation =>
        {
            var response = await peer.Lightning.ClosedChannelsAsync(new ClosedChannelsRequest(), cancellationToken: cancellation);
            return response.Channels.FirstOrDefault(item => item.ChannelPoint.StartsWith(funding, StringComparison.Ordinal));
        }, s_timeout, TimeSpan.FromMilliseconds(500), "peer closed", ct);
        Assert.Equal(transactionId, closed.ClosingTxHash);
        Assert.Equal(ChannelCloseSummary.Types.ClosureType.CooperativeClose, closed.CloseType);
    }
}