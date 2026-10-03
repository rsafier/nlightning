using System.Diagnostics;
using Google.Protobuf;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Cluster.Live;

using Fixtures.Lnd;
using Testing.Cluster.Nodes;
using Testing.Cluster.Topology.Lnd;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>
/// The Docker suites' LND regtest network on the cluster through the cluster backend of its fixture
/// (<see cref="ClusterLndBackend"/>, test harness phase 3): the members the LND Docker tests use (<c>Bitcoin</c>,
/// <c>BitcoinZmqPorts</c>, <c>LndNodes</c>, <c>GetLndNode</c>, <c>RestartLndAsync</c>) answer, and our in-process node
/// joins the network, opens a channel to alice, pays carol through alice, is paid by alice, and does both again after
/// alice restarts (a new pod IP: our node redials alice's Service name; no address-hold trick, NL-262).
/// </summary>
/// <remarks>
/// Explicit, <c>Category=Cluster</c>, not under <c>Docker</c> (no Docker lock): <c>scripts/run-cluster.sh -n 1 -p
/// integration --class NLightning.Integration.Tests.Cluster.Live.LndRegtestNetworkClusterTests</c>.
/// </remarks>
[Trait("Category", "Cluster")]
public class LndRegtestNetworkClusterTests
{
    private const long WalletSat = 2_000_000;
    private const long CapacitySat = 1_000_000;
    private const long ToCarolMsat = 50_000_000;
    private const long FromAliceMsat = 5_000_000;

    private static readonly TimeSpan s_payTimeout = TimeSpan.FromMinutes(2);

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_TheLndNetworkOnTheCluster_When_OurNodeJoinsAndPaysThroughAlice_Then_ItKeepsPayingAfterAliceRestarts()
    {
        // Arrange: the network through the fixture's members
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        await using var backend = new ClusterLndBackend(new LndRegtestNetworkOptions { Log = Log });
        await backend.StartAsync(ct);
        var built = watch.Elapsed;
        var network = backend.Network;

        Assert.Equal(["alice", "bob", "carol", "david"], backend.LndNodes.Select(n => n.LocalAlias));
        var aliceLnd = backend.GetLndNode("alice");
        Assert.Same(network.Node("alice").Connection, aliceLnd);
        Assert.Equal(await network.Chain.GetBlockCountAsync(ct), await backend.Bitcoin.GetBlockCountAsync(ct));
        Assert.Equal((28332, 28333), backend.BitcoinZmqPorts);
        Assert.Equal(backend.Bitcoin.Address.Host, backend.BitcoinEndpoint.ZmqHost);

        // Act: our node joins (funded), dials alice by her Service name and opens a private v1 channel to her
        var nltg = await backend.JoinInProcessNodeAsync("nltg", WalletSat, ct);
        var channel = await network.OpenChannelAsync(nltg, "alice", CapacitySat, 0, announce: false, ct);
        var joined = watch.Elapsed;

        // Act: we pay carol through alice (our node needs carol <-> alice from alice's gossip first, so retry);
        // alice pays us back over our channel
        var carol = network.Node("carol");
        var (toCarolInvoice, toCarol, toCarolAttempts) = await PayAsync(nltg, carol, ToCarolMsat, ct);
        var (_, fromAlice, fromAliceAttempts) = await PayAsync(network.Node("alice"), nltg, FromAliceMsat, ct);
        var paid = watch.Elapsed;

        // Act: alice restarts (fixture member); our node is redialled to her Service name, and pays and is paid again
        var restart = Stopwatch.StartNew();
        await backend.RestartLndAsync("alice");
        var restartedIn = restart.Elapsed;
        var (_, toCarolAfter, toCarolAfterAttempts) = await PayAsync(nltg, carol, ToCarolMsat, ct);
        var (_, fromAliceAfter, _) = await PayAsync(network.Node("alice"), nltg, FromAliceMsat, ct);

        // Assert: every payment settled, carol's invoice paid, our channel active on both ends after the restart
        Assert.True(toCarol.Succeeded, toCarol.FailureReason);
        Assert.True(fromAlice.Succeeded, fromAlice.FailureReason);
        Assert.True(toCarolAfter.Succeeded, toCarolAfter.FailureReason);
        Assert.True(fromAliceAfter.Succeeded, fromAliceAfter.FailureReason);
        var carolInvoice = await backend.GetLndNode("carol").LightningClient.LookupInvoiceAsync(
            new PaymentHash { RHash = ByteString.CopyFrom(Convert.FromHexString(toCarolInvoice.PaymentHashHex)) },
            cancellationToken: ct);
        Assert.Equal(Invoice.Types.InvoiceState.Settled, carolInvoice.State);
        Assert.Equal(ToCarolMsat, carolInvoice.AmtPaidMsat);
        Assert.Same(aliceLnd, backend.GetLndNode("alice"));
        await ClusterPoll.UntilAsync(async c =>
        {
            var ours = (await nltg.ListChannelsAsync(c)).FirstOrDefault(x => x.FundingTxId == channel.FundingTxId);
            return ours is { Active: true };
        }, s_payTimeout, TimeSpan.FromMilliseconds(250), "our channel active after alice's restart", ct);
        var aliceSide = (await aliceLnd.LightningClient.ListChannelsAsync(new ListChannelsRequest(),
                                                                          cancellationToken: ct))
                       .Channels.Single(c => c.ChanId == channel.ChanId);
        Assert.True(aliceSide.Active);
        Assert.Equal(await nltg.GetNodeIdAsync(ct), aliceSide.RemotePubkey);

        Log($"{backend.Run.Namespace}: network built in {built.TotalSeconds:F1} s, our node joined with channel "
          + $"{channel} at {joined.TotalSeconds:F1} s, paid carol in {toCarolAttempts} and alice paid us in "
          + $"{fromAliceAttempts} attempt(s) by {paid.TotalSeconds:F1} s; alice restarted in "
          + $"{restartedIn.TotalSeconds:F1} s, then carol paid in {toCarolAfterAttempts}; total "
          + $"{watch.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>Pays an invoice of <paramref name="payee"/>, retrying until it succeeds or the timeout (NL-319).</summary>
    private static Task<(TestInvoice Invoice, TestPaymentResult Result, int Attempts)> PayAsync(
        ILightningTestPeer payer, ILightningTestPeer payee, long amountMsat, CancellationToken ct) =>
        Testing.Cluster.Topology.Lnd.LndPairTopology.PayAsync(payer, payee, amountMsat, s_payTimeout, ct);
}