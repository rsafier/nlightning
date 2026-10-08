using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Node.Options;
using NLightning.Domain.Payments.Enums;
using NLightning.Domain.Payments.Models;
using NLightning.Domain.Persistence.Interfaces;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.RemoteSigning.Tests;
using NLightning.Testing.Cluster.Nodes;
using NLightning.Testing.Cluster.Nodes.Lnd;
using NLightning.Testing.Cluster.Run;
using NLightning.Testing.Cluster.Topology;
using NLightning.Testing.Lnd.Lnrpc;
using ClusterPoll = NLightning.Testing.Cluster.Poll;

namespace NLightning.Integration.Tests.Cluster.Live;

/// <summary>Concurrent hosted native nodes retain separate signer authority with shared Bitcoin infrastructure.</summary>
[Trait("Category", "Cluster")]
public sealed class HostedNativeNodesForwardingClusterTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);
    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_TwoHostedNativeNodes_When_LndRoutesFractionalPaymentThroughBoth_Then_SeparateCircuitsAndBalancesSettle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("hosted-native-forwarding")
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
            .FundWallet("first", 2_000_000).FundWallet("first", 2_000_000)
            .FundWallet("second", 2_000_000)
            .AddChannel("first", "alice", 1_000_000, 300_000_000)
            .AddChannel("first", "second", 1_000_000, 300_000_000)
            .AddChannel("second", "bob", 1_000_000, 300_000_000).BuildAsync(run, ct);
        var nodeA = topology.InProcessNode("first");
        var nodeB = topology.InProcessNode("second");
        var alice = topology.Node<LndNode>("alice");
        var bob = topology.Node<LndNode>("bob");
        Assert.NotEqual(await nodeA.GetNodeIdAsync(ct), await nodeB.GetNodeIdAsync(ct));
        Assert.NotEqual(nodeA.TestNode.DatabaseFilePath, nodeB.TestNode.DatabaseFilePath);
        var beforeA = await nodeA.ListChannelsAsync(ct);
        var beforeB = await nodeB.ListChannelsAsync(ct);
        var aToB = Assert.Single(beforeA, channel => channel.RemoteNodeId == nodeB.TestNode.NodeIdHex);
        var bToA = Assert.Single(beforeB, channel => channel.FundingTxId == aToB.FundingTxId);
        var aToAlice = Assert.Single(beforeA, channel => channel.FundingTxId != aToB.FundingTxId);
        var bToBob = Assert.Single(beforeB, channel => channel.FundingTxId != aToB.FundingTxId);
        var policyA = nodeA.TestNode.Services.GetRequiredService<IOptions<NodeOptions>>().Value.Routing;
        var policyB = nodeB.TestNode.Services.GetRequiredService<IOptions<NodeOptions>>().Value.Routing;
        const ulong delivered = 10_000_123;
        var forwardedByA = delivered + Fee(policyB, delivered);
        var paidToA = forwardedByA + Fee(policyA, forwardedByA);
        var hint = new RouteHint();
        hint.HopHints.Add(Hint(await nodeA.GetNodeIdAsync(ct), aToB.ShortChannelId!, policyA));
        hint.HopHints.Add(Hint(await nodeB.GetNodeIdAsync(ct), bToBob.ShortChannelId!, policyB));
        var request = new Invoice { ValueMsat = (long)delivered, Memo = "two isolated native forwards", Private = true };
        request.RouteHints.Add(hint);
        var created = await bob.Lightning.AddInvoiceAsync(request, cancellationToken: ct);
        var decoded = await alice.Lightning.DecodePayReqAsync(new PayReqString { PayReq = created.PaymentRequest }, cancellationToken: ct);
        Assert.Contains(decoded.RouteHints, route => route.HopHints.Count == 2
            && route.HopHints[0].NodeId == nodeA.TestNode.NodeIdHex && route.HopHints[1].NodeId == nodeB.TestNode.NodeIdHex);
        TestPaymentResult? payment = null;
        var deadline = DateTime.UtcNow + s_timeout;
        while (DateTime.UtcNow < deadline)
        {
            payment = await alice.PayInvoiceAsync(created.PaymentRequest, ct);
            if (payment.Succeeded) break;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        Assert.NotNull(payment);
        Assert.True(payment.Succeeded, payment.FailureReason);
        Assert.NotNull(payment.PreimageHex);
        Assert.Equal(created.RHash.ToByteArray(), System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(payment.PreimageHex)));
        var hash = Convert.ToHexString(created.RHash.ToByteArray()).ToLowerInvariant();
        await AssertCircuitAsync(nodeA, hash, paidToA, forwardedByA, ct);
        await AssertCircuitAsync(nodeB, hash, forwardedByA, delivered, ct);
        await ClusterPoll.UntilDoneAsync(async cancellation =>
        {
            var currentA = await nodeA.ListChannelsAsync(cancellation);
            var currentB = await nodeB.ListChannelsAsync(cancellation);
            var aIncoming = Assert.Single(currentA, channel => channel.FundingTxId == aToAlice.FundingTxId);
            var aOutgoing = Assert.Single(currentA, channel => channel.FundingTxId == aToB.FundingTxId);
            var bIncoming = Assert.Single(currentB, channel => channel.FundingTxId == bToA.FundingTxId);
            var bOutgoing = Assert.Single(currentB, channel => channel.FundingTxId == bToBob.FundingTxId);
            return aIncoming.LocalBalanceMsat == aToAlice.LocalBalanceMsat + (long)paidToA
                && aOutgoing.LocalBalanceMsat == aToB.LocalBalanceMsat - (long)forwardedByA
                && bIncoming.LocalBalanceMsat == bToA.LocalBalanceMsat + (long)forwardedByA
                && bOutgoing.LocalBalanceMsat == bToBob.LocalBalanceMsat - (long)delivered
                ? null : "four exact millisatoshi balances are still converging";
        }, s_timeout, "both native forward balances", ct);
        var settled = await bob.Lightning.LookupInvoiceAsync(new PaymentHash { RHash = created.RHash }, cancellationToken: ct);
        Assert.Equal(Invoice.Types.InvoiceState.Settled, settled.State);
        Assert.Equal((long)delivered, settled.AmtPaidMsat);
        Log($"{run.Namespace}: LND paid {delivered} msat through both native hosted identities; exact independent forwarding receipts and balances settled");

    }

    private static ulong Fee(RoutingOptions policy, ulong amount) => policy.FeeBaseMsat
        + amount * policy.FeeProportionalMillionths / 1_000_000;

    private static HopHint Hint(string identity, string scid, RoutingOptions policy)
    {
        var id = ShortChannelId.Parse(scid);
        return new HopHint
        {
            NodeId = identity,
            ChanId = ((ulong)id.BlockHeight << 40) | ((ulong)id.TransactionIndex << 16) | id.OutputIndex,
            FeeBaseMsat = policy.FeeBaseMsat,
            FeeProportionalMillionths = policy.FeeProportionalMillionths,
            CltvExpiryDelta = policy.CltvExpiryDelta
        };
    }

    private static Task AssertCircuitAsync(InProcessNode node, string hash, ulong incoming, ulong outgoing, CancellationToken ct) =>
        ClusterPoll.UntilDoneAsync(async cancellation =>
        {
            using var scope = node.TestNode.Services.CreateScope();
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var circuits = await unit.ForwardCircuitDbRepository.ListAsync(new ForwardCircuitListQuery(0, 100), cancellation);
            var settled = circuits.Where(circuit => circuit.PaymentHash.ToString() == hash
                && circuit.Status == ForwardCircuitStatus.Fulfilled).ToList();
            if (settled.Count == 0) return "fulfilled forwarding circuit not persisted";
            var circuit = Assert.Single(settled);
            Assert.NotNull(circuit.OutgoingChannelId);
            Assert.NotEqual(circuit.IncomingChannelId, circuit.OutgoingChannelId.Value);
            Assert.Equal(incoming, circuit.IncomingAmount.MilliSatoshi);
            Assert.Equal(outgoing, circuit.OutgoingAmount.MilliSatoshi);
            Assert.NotNull(circuit.ResolvedAt);
            return null;
        }, s_timeout, "context-local exact native forward", ct);
}