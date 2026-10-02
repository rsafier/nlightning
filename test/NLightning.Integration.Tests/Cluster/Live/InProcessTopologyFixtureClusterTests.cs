using System.Collections.Concurrent;
using System.Diagnostics;

namespace NLightning.Integration.Tests.Cluster.Live;

using Testing.Cluster.Kube;
using Testing.Cluster.Topology;
using Testing.Cluster.Topology.Lnd;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>
/// bitcoind + our in-process node + CLN on <c>emptyDir</c>s, with our v1 channel to CLN (a push, so CLN can pay at
/// once), kept warm for its collection (<see cref="InProcessTopologyFixture"/>).
/// </summary>
public sealed class WarmNltgClnFixture : InProcessTopologyFixture
{
    public const long CapacitySat = 1_000_000;
    public const long PushMsat = 300_000_000;

    protected override string Suite => "warm-nltg-cln";

    protected override void ConfigureTopology(TopologyBuilder builder)
    {
        builder.Storage = NodeStorage.Ephemeral;
        builder.AddBitcoinCore("miner")
               .AddNLightning("nltg")
               .AddCln("cln")
               .FundWallet("nltg", 2_000_000)
               .AddChannel("nltg", "cln", CapacitySat, PushMsat);
    }
}

[CollectionDefinition(Name)]
public sealed class WarmNltgClnCollection : ICollectionFixture<WarmNltgClnFixture>
{
    public const string Name = "warm-nltg-cln";
}

/// <summary>
/// The integrated phase 2 seam the CLN port builds on: a warm topology with our in-process node (lanes A and C), whose
/// tests share one namespace and channel and assert their own deltas; a failure dumps the fixture's run (lane D).
/// </summary>
/// <remarks>
/// Explicit and <c>Category=Cluster</c>: <c>scripts/run-cluster.sh -n 3 -p integration --class
/// NLightning.Integration.Tests.Cluster.Live.InProcessTopologyFixtureClusterTests</c>.
/// </remarks>
[Trait("Category", "Cluster")]
[Collection(WarmNltgClnCollection.Name)]
public class InProcessTopologyFixtureClusterTests(WarmNltgClnFixture fixture)
{
    private const long PaymentMsat = 10_000_000;

    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, byte> s_namespaces = new(StringComparer.Ordinal);

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public Task Given_TheWarmTopology_When_WePayCln_Then_ClnsBalanceGrowsByThePayment() =>
        PayAndCheckAsync("nltg", "cln");

    [Fact(Explicit = true)]
    public Task Given_TheSameWarmTopology_When_ClnPaysUs_Then_OurBalanceGrowsByThePayment() =>
        PayAndCheckAsync("cln", "nltg");

    private async Task PayAndCheckAsync(string payerName, string payeeName)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var payer = fixture.Node(payerName);
        var payee = fixture.Node(payeeName);
        var payerId = await payer.GetNodeIdAsync(ct);
        var payeeId = await payee.GetNodeIdAsync(ct);
        var fundingTxId = fixture.Topology.Channels.Single().Open.FundingTxId;
        if (s_namespaces.IsEmpty)
            foreach (var line in fixture.StartLog)
                Log(line);
        var payeeBefore = await BalanceAsync(payee, payerId, fundingTxId, ct);
        var payerBefore = await BalanceAsync(payer, payeeId, fundingTxId, ct);

        // Act
        var (_, paid, attempts) = await LndPairTopology.PayAsync(payer, payee, PaymentMsat, s_stepTimeout, ct);

        // Assert: paid, both ends moved by the payment (a direct channel: no fee), which also leaves the channel
        // settled for the next test (a payer's balance is gross of its offered HTLC until the HTLC is removed), and one
        // namespace for the whole collection
        Assert.True(paid.Succeeded, paid.FailureReason);
        await WaitBalanceAsync(payee, payeeName, payerId, fundingTxId, payeeBefore + PaymentMsat, ct);
        await WaitBalanceAsync(payer, payerName, payeeId, fundingTxId, payerBefore - PaymentMsat, ct);
        s_namespaces.TryAdd(fixture.Run.Namespace, 0);
        Assert.Single(s_namespaces);
        Assert.NotNull(fixture.InProcessNode("nltg").TestNode);
        Log($"{fixture.Run.Namespace} ({payerName} pays {payeeName}): topology started once in "
          + $"{fixture.StartTime.TotalSeconds:F1} s; paid in {attempts} attempt(s), test ran in "
          + $"{watch.Elapsed.TotalSeconds:F1} s");
    }

    private static Task WaitBalanceAsync(ITopologyLightningNode node, string name, string peerId, string fundingTxId,
                                         long expectedMsat, CancellationToken ct) =>
        ClusterPoll.UntilDoneAsync(async c =>
        {
            var balance = await BalanceAsync(node, peerId, fundingTxId, c);
            return balance == expectedMsat ? null : $"{name} holds {balance} msat";
        }, s_stepTimeout, $"{name} holds {expectedMsat} msat", ct);

    private static async Task<long> BalanceAsync(ITopologyLightningNode node, string peerId, string fundingTxId,
                                                 CancellationToken ct) =>
        (await node.ListChannelsAsync(ct)).Single(c => c.RemoteNodeId == peerId && c.FundingTxId == fundingTxId)
                                          .LocalBalanceMsat;
}