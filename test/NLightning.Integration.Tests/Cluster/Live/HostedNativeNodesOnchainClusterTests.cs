using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Daemon.Interfaces;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Client.Requests;
using NLightning.Domain.Client.Responses;
using NLightning.Domain.Onchain.Enums;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.RemoteSigning.Tests;
using NLightning.Testing.Cluster.Nodes;
using NLightning.Testing.Cluster.Nodes.Lnd;
using NLightning.Testing.Cluster.Run;
using NLightning.Testing.Cluster.Topology;
using NLightning.Testing.Cluster.Topology.Lnd;
using ClusterPoll = NLightning.Testing.Cluster.Poll;

namespace NLightning.Integration.Tests.Cluster.Live;

/// <summary>Concurrent hosted native nodes retain separate signer authority with shared Bitcoin infrastructure.</summary>
[Trait("Category", "Cluster")]
public sealed class HostedNativeNodesOnchainClusterTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);
    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_TwoHostedNativeNodes_When_TheyForceCloseAndRestart_Then_BothRecoverConfirmedDelayedOutputs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("hosted-native-onchain")
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
            ConfigureNodeOptions = (_, options) => NLightning.Integration.Tests.Docker.Onchain.LegacyChannelOptions.PinStaticRemoteKey(options),
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
        Assert.NotEqual(await nodeA.GetNodeIdAsync(ct), await nodeB.GetNodeIdAsync(ct));
        Assert.NotEqual(nodeA.TestNode.DatabaseFilePath, nodeB.TestNode.DatabaseFilePath);
        var channelA = Assert.Single((await nodeA.TestNode.ListChannelsAsync(ct)).Channels);
        var channelB = Assert.Single((await nodeB.TestNode.ListChannelsAsync(ct)).Channels);
        Assert.True(nodeA.TestNode.ChannelMemoryRepository.TryGetChannel(channelA.ChannelId, out var modelA));
        Assert.True(nodeB.TestNode.ChannelMemoryRepository.TryGetChannel(channelB.ChannelId, out var modelB));
        var csvA = modelA.ChannelParams.Remote.ToSelfDelay;
        var csvB = modelB.ChannelParams.Remote.ToSelfDelay;
        await PayBothWaysAsync(nodeA, alice, ct);
        await PayBothWaysAsync(nodeB, bob, ct);
        var closeA = await ForceCloseAsync(topology, nodeA, channelA, ct);
        await first.StopAsync();
        Assert.Throws<RemoteSignerTransportException>(() => new RemoteSecureKeyManager(a).ReserveChannelKeyIndex());
        await PayBothWaysAsync(nodeB, bob, ct);
        await nodeA.TestNode.StopAsync();
        await first.RestartAsync();
        await nodeA.TestNode.StartAsync(ct);
        var restoredA = await DelayedOutputAsync(nodeA, channelA.ChannelId, closeA, ct);
        Assert.Equal(closeA, restoredA.TransactionId);
        var closeB = await ForceCloseAsync(topology, nodeB, channelB, ct);
        await nodeB.TestNode.StopAsync();
        await second.RestartAsync();
        await nodeB.TestNode.StartAsync(ct);
        var restoredB = await DelayedOutputAsync(nodeB, channelB.ChannelId, closeB, ct);
        Assert.Equal(closeB, restoredB.TransactionId);
        Assert.NotEqual(closeA, closeB);
        Assert.Null(restoredA.ResolvingTxId);
        Assert.Null(restoredB.ResolvingTxId);
        var recoveries = new[] { (Node: nodeA, Channel: channelA, Close: closeA, Output: restoredA, Csv: csvA),
                                 (Node: nodeB, Channel: channelB, Close: closeB, Output: restoredB, Csv: csvB) };
        var maturity = Math.Max(restoredA.WaitUntilHeight!.Value, restoredB.WaitUntilHeight!.Value);
        var currentTip = await topology.Chain.GetBlockCountAsync(ct);
        await topology.MineAndSyncAsync((int)Math.Max(1, maturity + 1 - currentTip), ct);
        foreach (var (node, channel, close, output, csv) in recoveries)
        {
            var sweep = await ClusterPoll.ForAsync(async cancellation =>
            {
                var current = await SweepsAsync(node, channel.ChannelId, cancellation);
                return current.Outputs.FirstOrDefault(item => item.TransactionId == close
                    && item.OutputIndex == output.OutputIndex && item.ResolvingTxId is not null);
            }, s_timeout, TimeSpan.FromMilliseconds(250), "native delayed sweep publication", ct);
            var sweepId = sweep.ResolvingTxId!.Value;
            var display = new uint256((byte[])sweepId).ToString();
            var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
            await ClusterPoll.UntilDoneAsync(async cancellation =>
            {
                var visibility = await chain.Chain.Rpc.GetTransactionStatusAsync(display, cancellation);
                return visibility.State is NLightning.Testing.Cluster.Nodes.BitcoinCore.Rpc.TxState.InMempool or NLightning.Testing.Cluster.Nodes.BitcoinCore.Rpc.TxState.Confirmed
                    ? null : "saved sweep is not yet visible to Bitcoin Core";
            }, s_timeout, "native sweep accepted by Core", ct);
            var raw = await chain.Chain.Rpc.CallAsync("getrawtransaction", new Dictionary<string, object?>
            { ["txid"] = display, ["verbose"] = true }, ct);
            var input = Assert.Single(raw["vin"]!);
            Assert.Equal(new uint256((byte[])close).ToString(), (string?)input["txid"]);
            Assert.Equal((int)output.OutputIndex, (int?)input["vout"]);
            Assert.Equal((uint)csv, (uint?)input["sequence"]);
            var outputSat = raw["vout"]!.Sum(item => (decimal)item["value"]! * 100_000_000m);
            Assert.True(outputSat > 0 && outputSat < output.AmountSat!.Value);
            await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
            var confirmed = await chain.Chain.WaitForConfirmationAsync(display,
                TopologyDeployer.ConfirmationBlocks, ct, s_timeout);
            Assert.True(confirmed.Confirmations >= TopologyDeployer.ConfirmationBlocks);
            await ClusterPoll.UntilDoneAsync(async cancellation =>
            {
                var state = await SweepsAsync(node, channel.ChannelId, cancellation);
                var resolved = state.Outputs.Single(item => item.TransactionId == close && item.OutputIndex == output.OutputIndex);
                if (resolved.ResolvedHeight is null || resolved.State is not (OutputResolutionState.Resolved or OutputResolutionState.Irrevocable))
                    return "resolution is not confirmed in durable state";
                var wallet = node.TestNode.Services.GetRequiredService<IUtxoMemoryRepository>();
                return wallet.TryGetUtxo(sweepId, 0, out var credited)
                    && credited.Amount.Satoshi == outputSat ? null : "wallet has not credited the confirmed sweep";
            }, s_timeout, "confirmed resolution and owned wallet output", ct);
            var other = node == nodeA ? nodeB : nodeA;
            Assert.False(other.TestNode.Services.GetRequiredService<IUtxoMemoryRepository>().TryGetUtxo(sweepId, 0, out _));
            Log($"{node.Alias}: confirmed native delayed recovery {display} spends {close}:{output.OutputIndex}");
        }
    }

    private static async Task PayBothWaysAsync(ILightningTestPeer node, ILightningTestPeer peer, CancellationToken ct)
    {
        var (_, outgoing, _) = await LndPairTopology.PayAsync(node, peer, 10_000_123, s_timeout, ct);
        Assert.True(outgoing.Succeeded, outgoing.FailureReason);
        var (_, incoming, _) = await LndPairTopology.PayAsync(peer, node, 5_000_456, s_timeout, ct);
        Assert.True(incoming.Succeeded, incoming.FailureReason);
    }

    private static async Task<NLightning.Domain.Bitcoin.ValueObjects.TxId> ForceCloseAsync(
        TestTopology topology, InProcessNode node, ChannelInfoClientResponse channel, CancellationToken ct)
    {
        using var scope = node.TestNode.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<ForceCloseChannelClientRequest, ForceCloseChannelClientResponse>>();
        var outcome = await handler.HandleAsync(new ForceCloseChannelClientRequest(channel.ChannelId), ct);
        Assert.Equal("Broadcast", outcome.Status);
        Assert.NotNull(outcome.CommitmentTxId);
        var commitment = outcome.CommitmentTxId.Value;
        var display = new uint256((byte[])commitment).ToString();
        var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
        await chain.Chain.WaitForMempoolAsync(display, ct, s_timeout);
        var transaction = await chain.Chain.Rpc.CallAsync("getrawtransaction", new Dictionary<string, object?>
        { ["txid"] = display, ["verbose"] = true }, ct);
        var input = Assert.Single(transaction["vin"]!);
        Assert.Equal(channel.FundingTxId!.Value.ToString(), (string?)input["txid"]);
        Assert.Equal((int?)channel.FundingOutputIndex, (int?)input["vout"]);
        await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
        var confirmed = await chain.Chain.WaitForConfirmationAsync(display,
            TopologyDeployer.ConfirmationBlocks, ct, s_timeout);
        Assert.True(confirmed.Confirmations >= TopologyDeployer.ConfirmationBlocks);
        await DelayedOutputAsync(node, channel.ChannelId, commitment, ct);
        return commitment;
    }

    private static Task<PendingSweepOutputInfo> DelayedOutputAsync(InProcessNode node, ChannelId channel,
        NLightning.Domain.Bitcoin.ValueObjects.TxId commitment, CancellationToken ct) =>
        ClusterPoll.ForAsync(async cancellation =>
        {
            using var scope = node.TestNode.Services.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<PendingSweepsClientRequest, PendingSweepsClientResponse>>();
            var response = await handler.HandleAsync(new PendingSweepsClientRequest { ChannelId = channel }, cancellation);
            var state = response.Channels.SingleOrDefault();
            if (state is null) return null;
            Assert.Equal(ChannelCloseKind.LocalCommitment, state.CloseKind);
            return state.Outputs.FirstOrDefault(item => item.TransactionId == commitment
                && item.Descriptor == OutputDescriptorKind.DelayedToLocal && item.WaitUntilHeight is not null);
        }, s_timeout, TimeSpan.FromMilliseconds(250), "persisted delayed native output", ct);

    private static async Task<PendingSweepChannelInfo> SweepsAsync(InProcessNode node, ChannelId channel, CancellationToken ct)
    {
        using var scope = node.TestNode.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<PendingSweepsClientRequest, PendingSweepsClientResponse>>();
        var response = await handler.HandleAsync(new PendingSweepsClientRequest { ChannelId = channel }, ct);
        return Assert.Single(response.Channels);
    }
}