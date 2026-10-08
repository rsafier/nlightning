using Google.Protobuf;
using NLightning.Client.Ipc;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Onchain.Enums;
using NLightning.Domain.Payments.Enums;
using NLightning.Testing.Cluster.Nodes.Lnd;
using NLightning.Testing.Cluster.Run;
using NLightning.Testing.Cluster.Topology;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Transport.Ipc.Responses;
using ClusterPoll = NLightning.Testing.Cluster.Poll;

namespace NLightning.Integration.Tests.Cluster.Live;

/// <summary>Final acceptance of the shipped prototype launcher with actual daemon and signer processes.</summary>
[Trait("Category", "Cluster")]
public sealed class NativeRemoteP1DaemonChannelClusterTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(3);
    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_TwoProductDemoNodes_When_SignerAStopsAndNodesRecover_Then_ChannelsPayAndClose(bool forceCloseA)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(25));
        var ct = deadline.Token;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("native-p1-channels")
            with { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        using var topology = await new TopologyBuilder
        { Log = Log, ReadyTimeout = TimeSpan.FromMinutes(4), StepTimeout = s_timeout }
            .AddBitcoinCore("miner").AddLnd("alice").AddLnd("bob").BuildAsync(run, ct);
        await using var demo = await NativeRemoteP1DemoProcess.CreateAsync(topology.Chain, Log, ct);
        await demo.StartAsync("a", ct);
        await demo.StartAsync("b", ct);
        await using var a = demo.Client("a");
        await using var b = demo.Client("b");
        var infoA = await a.GetNodeInfoAsync(ct);
        var infoB = await b.GetNodeInfoAsync(ct);
        Assert.NotEqual(infoA.PubKey, infoB.PubKey);
        Assert.NotEqual(infoA.NodeId, infoB.NodeId);
        Assert.NotEqual(infoA.OwnerId, infoB.OwnerId);
        Assert.NotEqual(infoA.SignerId, infoB.SignerId);
        foreach (var property in new[] { "nodeDatabase", "nodeCookie", "nodeIpc", "signerState", "signerToken", "signerSocket" })
            Assert.NotEqual(demo.PathFor("a", property), demo.PathFor("b", property));
        await using var wrongOwner = new NamedPipeIpcClient(demo.PathFor("b", "nodeIpc"), demo.PathFor("a", "nodeCookie"));
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => wrongOwner.GetNodeInfoAsync(ct));
        Assert.Contains("auth_failed", denied.Message);
        await demo.ClientCommandAsync("a", "info", ct);
        await demo.ClientCommandAsync("b", "info", ct);
        var alice = topology.Node<LndNode>("alice");
        var bob = topology.Node<LndNode>("bob");
        await FundAsync(topology, a, ct);
        await FundAsync(topology, b, ct);
        var channelA = await OpenAsync(topology, a, alice, ct);
        var channelB = await OpenAsync(topology, b, bob, ct);
        Assert.NotEqual(channelA.FundingTxId, channelB.FundingTxId);
        await PayBothWaysAsync(a, alice, infoA.PubKey.ToString(), ct);
        await PayBothWaysAsync(b, bob, infoB.PubKey.ToString(), ct);

        await demo.InterruptSignerAsync("a", ct);
        using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            bounded.CancelAfter(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAnyAsync<Exception>(() => a.CreateInvoiceAsync(
                LightningMoney.MilliSatoshis(10_000_123), "signer unavailable", null, bounded.Token));
        }
        await PayBothWaysAsync(b, bob, infoB.PubKey.ToString(), ct);
        var beforeRestartA = channelA;
        var beforeRestartB = channelB;
        await demo.StopAsync("a");
        await demo.StartAsync("a", ct);
        await demo.StopAsync("b");
        await demo.StartAsync("b", ct);
        Assert.Equal(infoA.PubKey, (await a.GetNodeInfoAsync(ct)).PubKey);
        Assert.Equal(infoB.PubKey, (await b.GetNodeInfoAsync(ct)).PubKey);
        await a.ConnectPeerAsync((await alice.GetAddressAsync(ct)).ToString(), ct);
        await b.ConnectPeerAsync((await bob.GetAddressAsync(ct)).ToString(), ct);
        channelA = await WaitActiveAsync(a, beforeRestartA.ChannelId, requireReestablished: true, ct);
        channelB = await WaitActiveAsync(b, channelB.ChannelId, requireReestablished: true, ct);
        Assert.Equal(beforeRestartA.FundingTxId, channelA.FundingTxId);
        Assert.Equal(beforeRestartB.FundingTxId, channelB.FundingTxId);
        await alice.WaitForActiveChannelAsync(channelA.FundingTxId!.Value.ToString(), s_timeout, ct);
        await bob.WaitForActiveChannelAsync(channelB.FundingTxId!.Value.ToString(), s_timeout, ct);
        Assert.False(channelA.DataLossDetected);
        Assert.False(channelB.DataLossDetected);
        await PayBothWaysAsync(a, alice, infoA.PubKey.ToString(), ct);
        await PayBothWaysAsync(b, bob, infoB.PubKey.ToString(), ct);
        if (forceCloseA)
            await ForceCloseRecoverAsync(topology, demo, a, channelA, ct);
        else
            await CloseAsync(topology, a, alice, channelA, ct);
        await CloseAsync(topology, b, bob, channelB, ct);
        foreach (var name in new[] { "a", "b" })
        {
            var directory = Path.GetDirectoryName(demo.PathFor(name, "nodeDatabase"))!;
            Assert.False(File.Exists(Path.Combine(directory, "nltg.key.json")));
            Assert.True(File.Exists(demo.PathFor(name, "signerState")));
        }
        Log($"{run.Namespace}: product Python launcher, two daemon/signer pairs, fractional payments, independent outage, reestablishment and confirmed {(forceCloseA ? "force recovery" : "cooperative closes")}");
    }

    private static async Task FundAsync(TestTopology topology, NamedPipeIpcClient node, CancellationToken ct)
    {
        var address = (await node.GetAddressAsync("p2wpkh", ct)).AddressP2Wpkh;
        Assert.NotNull(address);
        await topology.Chain.SendToAddressAsync(address, 2_000_000, ct);
        await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
        await ClusterPoll.UntilDoneAsync(async cancellation =>
            (await node.GetWalletBalance(cancellation)).ConfirmedBalance.Satoshi == 2_000_000
                ? null : "daemon has not credited its deposit", s_timeout, "native daemon funding", ct);
    }

    private static async Task<ChannelInfoIpcResponse> OpenAsync(TestTopology topology, NamedPipeIpcClient node,
                                                              LndNode peer, CancellationToken ct)
    {
        var address = (await peer.GetAddressAsync(ct)).ToString();
        await node.ConnectPeerAsync(address, ct);
        await node.OpenChannelAsync(address, "1000000", "300000", ct, forceV1: true);
        var pending = await ClusterPoll.ForAsync(async cancellation =>
            (await node.ListChannelsAsync(null, cancellation)).Channels.SingleOrDefault(channel => channel.FundingTxId is not null),
            s_timeout, TimeSpan.FromMilliseconds(250), "daemon published v1 funding", ct);
        var funding = pending.FundingTxId!.Value.ToString();
        var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
        await chain.Chain.WaitForMempoolAsync(funding, ct, s_timeout);
        await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
        var active = await WaitActiveAsync(node, pending.ChannelId, false, ct);
        var peerChannel = await peer.WaitForActiveChannelAsync(funding, s_timeout, ct);
        Assert.Equal(1_000_000, peerChannel.CapacitySat);
        Assert.Equal("static_remotekey", active.ChannelType);
        return active;
    }

    private static Task<ChannelInfoIpcResponse> WaitActiveAsync(NamedPipeIpcClient node, ChannelId channel,
                                                              bool requireReestablished, CancellationToken ct) =>
        ClusterPoll.ForAsync(async cancellation =>
        {
            var current = (await node.ListChannelsAsync(null, cancellation)).Channels.SingleOrDefault(item => item.ChannelId == channel);
            return current is { State: ChannelState.Open, IsPeerConnected: true }
                   && (!requireReestablished || current.IsReestablished) ? current : null;
        }, s_timeout, TimeSpan.FromMilliseconds(250), "native daemon active and reestablished channel", ct);

    private static async Task PayBothWaysAsync(NamedPipeIpcClient node, LndNode peer, string identity, CancellationToken ct)
    {
        const long outgoingMsat = 10_000_123;
        const long incomingMsat = 5_000_456;
        var before = Assert.Single((await node.ListChannelsAsync(null, ct)).Channels, item => item.State == ChannelState.Open);
        var peerInvoice = await peer.CreateInvoiceAsync(outgoingMsat, "native P1 outgoing", ct);
        var payment = await node.PayInvoiceAsync(peerInvoice.Bolt11, null, 90, ct: ct);
        Assert.Equal(PaymentStatus.Succeeded, payment.Payment.Status);
        Assert.Equal((ulong)outgoingMsat, payment.Payment.Amount.MilliSatoshi);
        Assert.NotNull(payment.Payment.Preimage);
        var peerSettled = await peer.Lightning.LookupInvoiceAsync(new PaymentHash
        { RHash = ByteString.CopyFrom(Convert.FromHexString(peerInvoice.PaymentHashHex)) }, cancellationToken: ct);
        Assert.Equal(Invoice.Types.InvoiceState.Settled, peerSettled.State);
        Assert.Equal(outgoingMsat, peerSettled.AmtPaidMsat);

        var ours = await node.CreateInvoiceAsync(LightningMoney.MilliSatoshis(incomingMsat), "native P1 incoming", null, ct);
        Assert.NotNull(ours.Invoice.Bolt11);
        var decoded = await peer.Lightning.DecodePayReqAsync(new PayReqString { PayReq = ours.Invoice.Bolt11 }, cancellationToken: ct);
        Assert.Equal(identity, decoded.Destination);
        Assert.Equal(incomingMsat, decoded.NumMsat);
        await ClusterPoll.UntilDoneAsync(async cancellation =>
        {
            var result = await peer.PayInvoiceAsync(ours.Invoice.Bolt11, cancellation);
            return result.Succeeded ? null : result.FailureReason ?? "LND route not ready";
        }, s_timeout, "LND pays actual native daemon", ct);
        var settled = await ClusterPoll.ForAsync(async cancellation =>
        {
            var response = await node.WaitInvoiceAsync(ours.Invoice.PaymentHash, 90, cancellation);
            return response.Invoice.Status == InvoiceStatus.Settled ? response : null;
        }, s_timeout, TimeSpan.FromMilliseconds(250), "native daemon irrevocably settles invoice", ct);
        Assert.False(settled.TimedOut);
        Assert.Equal(InvoiceStatus.Settled, settled.Invoice.Status);
        Assert.Equal((ulong)incomingMsat, settled.Invoice.AmountReceived!.MilliSatoshi);
        await ClusterPoll.UntilDoneAsync(async cancellation =>
        {
            var after = (await node.ListChannelsAsync(null, cancellation)).Channels.Single(item => item.ChannelId == before.ChannelId);
            var expected = before.LocalBalance.MilliSatoshi - (ulong)outgoingMsat + (ulong)incomingMsat;
            return after.LocalBalance.MilliSatoshi == expected && after.OfferedHtlcCount == 0 && after.ReceivedHtlcCount == 0
                ? null : "channel has not committed the exact fractional-msat balance delta";
        }, s_timeout, "native daemon exact channel balance", ct);
    }

    private static async Task CloseAsync(TestTopology topology, NamedPipeIpcClient node, LndNode peer,
                                         ChannelInfoIpcResponse channel, CancellationToken ct)
    {
        var close = await node.CloseChannelAsync(channel.ChannelId, null, false, 120, ct);
        Assert.NotNull(close.ClosingTxId);
        await ConfirmSpendsFundingAsync(topology, close.ClosingTxId, channel, ct);
        var closed = await ClusterPoll.ForAsync(async cancellation =>
            (await peer.Lightning.ClosedChannelsAsync(new ClosedChannelsRequest(), cancellationToken: cancellation))
                .Channels.FirstOrDefault(item => item.ChannelPoint.StartsWith(channel.FundingTxId!.Value.ToString(), StringComparison.Ordinal)),
            s_timeout, TimeSpan.FromMilliseconds(250), "LND observes native cooperative close", ct);
        Assert.Equal(close.ClosingTxId, closed.ClosingTxHash);
        Assert.Equal(ChannelCloseSummary.Types.ClosureType.CooperativeClose, closed.CloseType);
        await ClusterPoll.UntilDoneAsync(async cancellation =>
        {
            var current = (await node.ListChannelsAsync(null, cancellation)).Channels.SingleOrDefault(item => item.ChannelId == channel.ChannelId);
            return current is null || current.State == ChannelState.Closed ? null : "daemon has not persisted closed channel";
        }, s_timeout, "native daemon cooperative close persistence", ct);
    }

    private static async Task ConfirmSpendsFundingAsync(TestTopology topology, string transactionId,
                                                       ChannelInfoIpcResponse channel, CancellationToken ct)
    {
        var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
        await chain.Chain.WaitForMempoolAsync(transactionId, ct, s_timeout);
        var transaction = await chain.Chain.Rpc.CallAsync("getrawtransaction", new Dictionary<string, object?>
        { ["txid"] = transactionId, ["verbose"] = true }, ct);
        var input = Assert.Single(transaction["vin"]!);
        Assert.Equal(channel.FundingTxId!.Value.ToString(), (string?)input["txid"]);
        Assert.Equal((int?)channel.FundingOutputIndex, (int?)input["vout"]);
        await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
        var confirmed = await chain.Chain.WaitForConfirmationAsync(transactionId, TopologyDeployer.ConfirmationBlocks, ct, s_timeout);
        Assert.True(confirmed.Confirmations >= TopologyDeployer.ConfirmationBlocks);
    }

    private static async Task ForceCloseRecoverAsync(TestTopology topology, NativeRemoteP1DemoProcess demo,
                                                     NamedPipeIpcClient node, ChannelInfoIpcResponse channel, CancellationToken ct)
    {
        var close = await node.ForceCloseChannelAsync(channel.ChannelId, ct);
        Assert.Equal("Broadcast", close.Status);
        Assert.NotNull(close.CommitmentTxId);
        await ConfirmSpendsFundingAsync(topology, close.CommitmentTxId, channel, ct);
        var output = await DelayedOutputAsync(node, channel.ChannelId, close.CommitmentTxId, ct);
        await demo.StopAsync("a");
        await demo.StartAsync("a", ct);
        var restored = await DelayedOutputAsync(node, channel.ChannelId, close.CommitmentTxId, ct);
        Assert.Equal(output.OutputIndex, restored.OutputIndex);
        Assert.Equal(output.WaitUntilHeight, restored.WaitUntilHeight);
        Assert.Null(restored.ResolvingTxId);
        var walletBefore = (await node.GetWalletBalance(ct)).ConfirmedBalance.Satoshi;
        var height = await topology.Chain.GetBlockCountAsync(ct);
        await topology.MineAndSyncAsync((int)Math.Max(1, restored.WaitUntilHeight!.Value + 1 - height), ct);
        var sweep = await ClusterPoll.ForAsync(async cancellation =>
        {
            var state = await node.PendingSweepsAsync(channel.ChannelId, true, cancellation);
            return state.Channels.Single().Outputs.FirstOrDefault(item => item.TransactionId == close.CommitmentTxId
                && item.OutputIndex == restored.OutputIndex && item.ResolvingTxId is not null);
        }, s_timeout, TimeSpan.FromMilliseconds(250), "native daemon publishes recovered delayed sweep", ct);
        var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
        await chain.Chain.WaitForMempoolAsync(sweep.ResolvingTxId!, ct, s_timeout);
        var transaction = await chain.Chain.Rpc.CallAsync("getrawtransaction", new Dictionary<string, object?>
        { ["txid"] = sweep.ResolvingTxId, ["verbose"] = true }, ct);
        var input = Assert.Single(transaction["vin"]!);
        Assert.Equal(close.CommitmentTxId, (string?)input["txid"]);
        Assert.Equal((int)restored.OutputIndex, (int?)input["vout"]);
        Assert.True((uint?)input["sequence"] > 0);
        var outputSat = transaction["vout"]!.Sum(item => (decimal)item["value"]! * 100_000_000m);
        Assert.True(outputSat > 0 && outputSat < restored.AmountSat!.Value);
        await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
        var confirmed = await chain.Chain.WaitForConfirmationAsync(sweep.ResolvingTxId!, TopologyDeployer.ConfirmationBlocks, ct, s_timeout);
        Assert.True(confirmed.Confirmations >= TopologyDeployer.ConfirmationBlocks);
        await ClusterPoll.UntilDoneAsync(async cancellation =>
        {
            var state = await node.PendingSweepsAsync(channel.ChannelId, true, cancellation);
            var resolved = state.Channels.Single().Outputs.Single(item => item.TransactionId == close.CommitmentTxId
                && item.OutputIndex == restored.OutputIndex);
            return resolved.ResolvedHeight is not null && resolved.State is OutputResolutionState.Resolved or OutputResolutionState.Irrevocable
                ? null : "recovered sweep is not confirmed in daemon state";
        }, s_timeout, "native daemon durable confirmed recovery", ct);
        await ClusterPoll.UntilDoneAsync(async cancellation =>
            (await node.GetWalletBalance(cancellation)).ConfirmedBalance.Satoshi >= walletBefore + outputSat
                ? null : "daemon wallet has not credited the confirmed recovered sweep",
            s_timeout, "native daemon recovered wallet output", ct);
    }

    private static Task<PendingSweepOutputIpcInfo> DelayedOutputAsync(NamedPipeIpcClient node, ChannelId channel,
                                                                    string commitment, CancellationToken ct) =>
        ClusterPoll.ForAsync(async cancellation =>
        {
            var state = (await node.PendingSweepsAsync(channel, true, cancellation)).Channels.SingleOrDefault();
            if (state is null) return null;
            Assert.Equal(ChannelCloseKind.LocalCommitment, state.CloseKind);
            return state.Outputs.FirstOrDefault(item => item.TransactionId == commitment
                && item.Descriptor == OutputDescriptorKind.DelayedToLocal && item.WaitUntilHeight is not null);
        }, s_timeout, TimeSpan.FromMilliseconds(250), "native daemon persisted delayed commitment output", ct);
}