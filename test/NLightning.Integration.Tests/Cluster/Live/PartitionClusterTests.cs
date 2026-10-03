using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Integration.Tests.Cluster.Live;

using Docker.Abcd;
using Domain.Channels.Enums;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Testing.Cluster.Diagnostics;
using Testing.Cluster.Faults;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.Cln;
using Testing.Cluster.Reach;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>
/// Network partitions between our in-process node and a CLN pod, and between CLN and its bitcoind (test harness phase 4,
/// new coverage): an HTLC in flight across a partition, a partition that outlasts our reconnect attempts, a peer stuck
/// before <c>channel_reestablish</c>, a frozen peer only our ping keep-alive notices (NL-806),
/// and a CLN cut off from the chain while our node keeps working.
/// </summary>
/// <remarks>
/// <para>
/// Each test builds its own topology (bitcoind <c>miner</c>, our <c>nltg</c>, CLN <c>cln</c> with a shared process
/// namespace for pauses, everything on <c>emptyDir</c>) with one channel <c>nltg</c> → <c>cln</c>, so a fault never leaks
/// into the next test; the tests of the class run one after another (one namespace at a time).
/// </para>
/// <para>
/// The partitions are NetworkPolicies (<see cref="FaultInjector"/>). OrbStack enforces them for new connections only;
/// an established TCP connection survives, so every partition between the peers is followed by a disconnect from our
/// side (<see cref="InProcessNode.DisconnectAsync"/>). Our node reconnects by itself (the backoff of a peer with
/// channels, here 1 s doubling to <see cref="ReconnectMaxDelay"/>), which the partition refuses until the heal.
/// </para>
/// <para>
/// Explicit and <c>Category=Cluster</c>: <c>scripts/run-cluster.sh -n 1 -p integration --class
/// NLightning.Integration.Tests.Cluster.Live.PartitionClusterTests</c>. No Docker lock.
/// </para>
/// </remarks>
[Trait("Category", "Cluster")]
public class PartitionClusterTests
{
    private const long WalletSat = 2_000_000;
    private const long CapacitySat = 1_000_000;
    private const long PushMsat = 200_000_000;
    private const long SmallMsat = 10_000_000;
    private const long HtlcMsat = 50_000_000;

    /// <summary>The longest wait of our reconnect backoff in these tests (the default is 10 minutes).</summary>
    private static readonly TimeSpan s_reconnectMaxDelay = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_poll = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a partition is held and checked before the heal: several reconnect attempts.</summary>
    private static readonly TimeSpan s_hold = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Our node's deadline for the peer's <c>channel_reestablish</c> in the never-answered case (NL-796; the default
    /// is 60 s); the other tests keep the default.
    /// </summary>
    private static readonly TimeSpan s_reestablishTimeout = TimeSpan.FromSeconds(15);

    public static TimeSpan ReconnectMaxDelay => s_reconnectMaxDelay;

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>
    /// CLN is frozen (SIGSTOP) while our <c>update_add_htlc</c> and <c>commitment_signed</c> are on their way, then
    /// partitioned from our node; our node's liveness ping gets no pong and closes the connection (BOLT 1), as it
    /// would for any silent peer. Our node keeps the HTLC (the payment stays in flight, the channel Open, nothing
    /// failed or broadcast) through the partition; CLN resumes and reads what reached its socket; after the heal our
    /// node reconnects by itself, <c>channel_reestablish</c> resolves the commitment state, CLN settles, the payment
    /// succeeds and both sides agree on the balances.
    /// </summary>
    [Fact(Explicit = true)]
    public Task Given_AnHtlcInFlightToAFrozenCln_When_PartitionedAndHealed_Then_ItSettlesAndBothSidesAgree() =>
        WithClnPairAsync("part-htlc", async (pair, ct) =>
        {
            // Arrange: the channel carries a payment, then CLN freezes with an invoice of ours to pay
            await PayClnAsync(pair, SmallMsat, ct);
            var invoice = await pair.Cln.CreateInvoiceAsync(HtlcMsat, "in flight across a partition", ct);
            await pair.Faults.PauseAsync(pair.ClnHandle, ct);

            // Act: our HTLC goes out to the frozen peer
            var started = await pair.Nltg.TestNode.PayInvoiceAsync(invoice.Bolt11, ct, timeoutSeconds: 3);
            await WaitOurChannelAsync(pair, c => c.OfferedHtlcCount == 1, "our HTLC offered", ct);
            var partition = await pair.Faults.PartitionFromHostAsync([pair.ClnHandle], null, ct);
            var pong = await PingClnAsync(pair, ct);

            // Assert: through the partition the HTLC stays, nothing fails
            var held = await HoldAsync(pair, ct, c => c is
            {
                State: ChannelState.Open,
                IsPeerConnected: false,
                OfferedHtlcCount: 1
            }, invoice.PaymentHashHex, PaymentStatus.InFlight);

            // Act: CLN resumes behind the partition (it reads what reached its socket and finds the connection gone)
            await pair.Faults.ResumeAsync(pair.ClnHandle, ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            var stillPartitioned = await FindOurChannelAsync(pair, ct);
            await pair.Faults.HealAsync(partition, ct);
            var heal = Stopwatch.StartNew();

            // Assert: our node reconnects by itself, the HTLC settles, the balances agree on both sides
            var payment = await WaitPaymentAsync(pair, invoice.PaymentHashHex, PaymentStatus.Succeeded, ct);
            var settledIn = heal.Elapsed;
            await WaitActiveBothEndsAsync(pair, ct);
            Assert.Equal(PaymentStatus.InFlight, started.Status);
            Assert.False(pong);
            Assert.False(stillPartitioned.IsPeerConnected);
            Assert.Equal(1, stillPartitioned.OfferedHtlcCount);
            Assert.NotNull(payment.Preimage);
            await WaitOurChannelAsync(pair, c => c.OfferedHtlcCount == 0 && c.ReceivedHtlcCount == 0, "no HTLC left", ct);
            await WaitBalancesAsync(pair, PushMsat + SmallMsat + HtlcMsat, ct);
            await PayClnAsync(pair, SmallMsat, ct);
            await WaitBalancesAsync(pair, PushMsat + 2 * SmallMsat + HtlcMsat, ct);

            Log($"{pair.Namespace}: HTLC held {held.TotalSeconds:F1} s through the partition, settled "
              + $"{settledIn.TotalSeconds:F1} s after the heal");
            foreach (var fault in pair.Faults.Events)
                Log($"  {fault}");
        });

    /// <summary>
    /// NL-806: CLN is frozen (SIGSTOP) on a quiet channel right after a fresh connection, so its socket stays open and
    /// nothing we send needs an answer: only our keep-alive can notice. Our node pings every <c>Node:PingInterval</c>
    /// (15 s ±10 % on regtest) and closes the connection when a <c>pong</c> is missing after <c>Node:NetworkTimeout</c>
    /// (15 s), so the drop comes within about 31.5 s of the freeze (the old random 30-300 s wait made it at least 45 s
    /// after the connection); CLN resumes and our reconnect backoff brings the channel back by itself.
    /// </summary>
    [Fact(Explicit = true)]
    public Task Given_AFrozenClnOnAQuietChannel_When_NothingIsSent_Then_OurPingDropsItWithinTheIntervalAndTheChannelComesBack() =>
        WithClnPairAsync("part-ping", async (pair, ct) =>
        {
            // Arrange: a fresh connection (CLN drops the first one and our backoff redials), the regtest interval
            var nodeOptions = pair.Nltg.TestNode.Services.GetRequiredService<IOptions<NodeOptions>>().Value;
            var interval = nodeOptions.GetEffectivePingInterval();
            await DropFromClnSideAsync(pair, ct);
            await WaitActiveBothEndsAsync(pair, ct);
            var peerId = new CompactPubKey(Convert.FromHexString(pair.ClnId));

            // Act: freeze CLN and wait for our node to drop it, with no payment or ping of the test's own
            await pair.Faults.PauseAsync(pair.ClnHandle, ct);
            var frozen = Stopwatch.StartNew();
            await ClusterPoll.UntilAsync(_ => Task.FromResult(!pair.Nltg.TestNode.IsConnectedTo(peerId)),
                                         s_stepTimeout, s_poll, "our keep-alive drops the frozen CLN", ct);
            var droppedIn = frozen.Elapsed;
            var whileFrozen = await FindOurChannelAsync(pair, ct);
            await pair.Faults.ResumeAsync(pair.ClnHandle, ct);
            var resume = Stopwatch.StartNew();
            await WaitActiveBothEndsAsync(pair, ct);
            var activeIn = resume.Elapsed;

            // Assert: the interval plus its jitter plus the pong timeout, with a few seconds of polling and scheduling
            Assert.Equal(NodeOptions.RegtestDefaultPingInterval, interval);
            var bound = interval * 1.1 + nodeOptions.NetworkTimeout + TimeSpan.FromSeconds(5);
            Assert.True(droppedIn < bound, $"our node dropped the frozen CLN after {droppedIn} (bound {bound})");
            Assert.Equal(ChannelState.Open, whileFrozen.State);
            Assert.False(whileFrozen.IsPeerConnected);
            await PayClnAsync(pair, SmallMsat, ct);
            await WaitBalancesAsync(pair, PushMsat + SmallMsat, ct);

            Log($"{pair.Namespace}: ping interval {interval.TotalSeconds:F0} s, the frozen CLN dropped "
              + $"{droppedIn.TotalSeconds:F1} s after the freeze, channel active {activeIn.TotalSeconds:F1} s after the "
              + "resume");
            foreach (var fault in pair.Faults.Events)
                Log($"  {fault}");
        });

    /// <summary>
    /// CLN partitioned from our node, then CLN drops the connection: our reconnect attempts fail for several backoff
    /// rounds, the
    /// channel stays Open and unusable, a payment fails at once without adding an HTLC (no HTLC can be stuck behind a
    /// dead link), and after the heal our node reconnects and reestablishes by itself and pays.
    /// </summary>
    [Fact(Explicit = true)]
    public Task Given_AClnPartitionedFromOurNode_When_WePayAndHeal_Then_ThePaymentFailsCleanlyAndTheChannelComesBack() =>
        WithClnPairAsync("part-reconnect", async (pair, ct) =>
        {
            // Arrange: the partition, then CLN drops the established connection (our node sees the peer go)
            var partition = await pair.Faults.PartitionFromHostAsync([pair.ClnHandle], null, ct);
            await DropFromClnSideAsync(pair, ct);

            // Act: hold the partition over several reconnect attempts, then pay
            var held = await HoldAsync(pair, ct, c => c is
            {
                State: ChannelState.Open,
                IsPeerConnected: false,
                IsReestablished: false,
                OfferedHtlcCount: 0
            });
            var invoice = await pair.Cln.CreateInvoiceAsync(HtlcMsat, "paid while partitioned", ct);
            var payWatch = Stopwatch.StartNew();
            var refused = await pair.Nltg.PayInvoiceAsync(invoice.Bolt11, ct);
            var refusedIn = payWatch.Elapsed;
            var afterRefusal = await FindOurChannelAsync(pair, ct);
            var clnSawUs = await IsClnConnectedToUsAsync(pair, ct);

            // Act: heal; no connect call from the test
            await pair.Faults.HealAsync(partition, ct);
            var heal = Stopwatch.StartNew();
            await WaitActiveBothEndsAsync(pair, ct);
            var activeIn = heal.Elapsed;

            // Assert
            Assert.False(refused.Succeeded);
            Assert.True(refusedIn < TimeSpan.FromSeconds(30), $"the payment took {refusedIn} to fail");
            Assert.Equal(0, afterRefusal.OfferedHtlcCount);
            Assert.Equal(ChannelState.Open, afterRefusal.State);
            Assert.False(clnSawUs);
            Assert.Equal("unpaid", await ClnInvoiceStatusAsync(pair, invoice.PaymentHashHex, ct));
            Assert.True(activeIn < s_reconnectMaxDelay + TimeSpan.FromSeconds(20),
                        $"active {activeIn} after the heal");
            await PayClnAsync(pair, HtlcMsat, ct);
            await WaitBalancesAsync(pair, PushMsat + HtlcMsat, ct);

            Log($"{pair.Namespace}: partition held {held.TotalSeconds:F1} s, payment refused in "
              + $"{refusedIn.TotalSeconds:F1} s ({refused.FailureReason}), channel active {activeIn.TotalSeconds:F1} s "
              + "after the heal");
        });

    /// <summary>
    /// CLN's <c>lightningd</c> frozen while its <c>connectd</c> runs: we drop the connection and dial again, and our
    /// <c>channel_reestablish</c> finds nothing behind CLN's transport to answer it. The channel stays gated (a payment
    /// fails without an HTLC), and once <see cref="s_reestablishTimeout"/> passes without CLN's reestablish our node
    /// closes the connection with a warning and its reconnect backoff dials CLN again by itself (NL-796); the channel
    /// stays gated on the new connection too. Then a partition cuts the half-open link, CLN resumes, and after the heal
    /// the reestablish completes and the channel pays again. The test dials itself after its own disconnects: a peer
    /// we disconnected on purpose is not redialled by the backoff.
    /// </summary>
    [Fact(Explicit = true)]
    public Task Given_AClnThatNeverAnswersOurReestablish_When_TheDeadlinePasses_Then_WeDropAndRedialAndTheChannelStaysGatedUntilTheReestablish() =>
        WithClnPairAsync("part-reestablish", async (pair, ct) =>
        {
            // A failure while lightningd is frozen resumes it first: the failure dump reads CLN through lightning-cli
            var resumed = false;
            try
            {
                // Arrange: an invoice first (lightning-cli waits on a frozen lightningd), then freeze lightningd only and
                // drop the connection; our node redials CLN's connectd at the pod IP: the frozen lightningd fails CLN's
                // readiness probe, which takes the pod out of the cln-p2p Service, so our backoff's redial (to the address
                // of this connection) must not go through the Service
                var invoice = await pair.Cln.CreateInvoiceAsync(HtlcMsat, "paid before the reestablish", ct);
                var clnAddress = await pair.Cln.GetAddressAsync(ct);
                var clnPodAddress = clnAddress with
                {
                    Host = pair.ClnHandle.PodIp ?? throw new InvalidOperationException("CLN has no pod IP")
                };
                var clnPeerId = new CompactPubKey(Convert.FromHexString(pair.ClnId));
                await pair.Faults.PauseProcessAsync(pair.ClnHandle, "lightningd", ct);
                await pair.Nltg.DisconnectAsync(pair.ClnId, ct);
                var redial = await Record.ExceptionAsync(() => pair.Nltg.ConnectAsync(clnPodAddress, ct));
                var sinceRedial = Stopwatch.StartNew();
                var firstConnection = pair.Nltg.TestNode.PeerManager.GetPeer(clnPeerId);

                // Act: the transport is up (connectd completed init) and the channel must not become usable without CLN's
                // reestablish; the case this test exists for, so it holds before the deadline or fails
                Assert.Null(redial);
                Assert.NotNull(firstConnection);
                var clnSockets = await TcpConnectionTable.CountEstablishedAsync(pair.ClnHandle, ClnNode.P2PPort, ct);
                var refused = await pair.Nltg.PayInvoiceAsync(invoice.Bolt11, ct);
                var afterRefusal = await FindOurChannelAsync(pair, ct);
                var refusedAt = sinceRedial.Elapsed;

                // Act: past the deadline our node drops the connection and its backoff dials CLN again (a new connection),
                // while the channel stays gated the whole time; the first moment the first connection is gone is recorded,
                // so dropping CLN before the deadline fails the test (NL-891)
                var gateWatch = Stopwatch.StartNew();
                TimeSpan? firstConnectionGoneAt = null;
                while (sinceRedial.Elapsed < s_reestablishTimeout + s_reconnectMaxDelay + s_stepTimeout)
                {
                    if (!ReferenceEquals(pair.Nltg.TestNode.PeerManager.GetPeer(clnPeerId), firstConnection))
                    {
                        firstConnectionGoneAt = sinceRedial.Elapsed;
                        break;
                    }

                    var ours = await FindOurChannelAsync(pair, ct);
                    Assert.True(ours is { State: ChannelState.Open, IsReestablished: false },
                                $"after {sinceRedial.Elapsed.TotalSeconds:F1} s: our channel {ours.State}, reestablished "
                              + $"{ours.IsReestablished}");
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                }

                var gated = gateWatch.Elapsed;
                Assert.True(firstConnectionGoneAt is not null, "our node never dropped the first connection to CLN");
                var secondConnection = await ClusterPoll.ForAsync(
                    _ => Task.FromResult(pair.Nltg.TestNode.PeerManager.GetPeer(clnPeerId) is { } current
                                      && !ReferenceEquals(current, firstConnection)
                                             ? current
                                             : null),
                    s_stepTimeout, s_poll, "our node dropped CLN after the reestablish deadline and dialed it again", ct);
                var redialedIn = sinceRedial.Elapsed;
                var onSecondConnection = await FindOurChannelAsync(pair, ct);

                // Act: partition the half-open link, resume CLN, heal
                var partition = await pair.Faults.PartitionFromHostAsync([pair.ClnHandle], null, ct);
                await pair.Nltg.DisconnectAsync(pair.ClnId, ct);
                await pair.Faults.ResumeAsync(pair.ClnHandle, ct);
                resumed = true;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                await pair.Faults.HealAsync(partition, ct);
                var heal = Stopwatch.StartNew();
                await pair.Nltg.ConnectAsync(clnAddress, ct);
                await WaitActiveBothEndsAsync(pair, ct);
                var activeIn = heal.Elapsed;

                // Assert
                Assert.True(clnSockets >= 1, $"CLN's pod holds {clnSockets} established connections on its p2p port while "
                                           + "lightningd is frozen");
                Assert.False(refused.Succeeded);
                Assert.True(refusedAt < s_reestablishTimeout,
                            $"the payment was refused {refusedAt} after the redial, past the deadline");
                Assert.True(afterRefusal.IsPeerConnected, "the transport dropped before the deadline");
                Assert.False(afterRefusal.IsReestablished);
                Assert.Equal(0, afterRefusal.OfferedHtlcCount);
                Assert.True(firstConnectionGoneAt >= s_reestablishTimeout - TimeSpan.FromSeconds(1),
                            $"our node dropped the first connection {firstConnectionGoneAt} after the redial, before the "
                          + "deadline");
                Assert.True(redialedIn < s_reestablishTimeout + s_reconnectMaxDelay + TimeSpan.FromSeconds(20),
                            $"our node redialed only {redialedIn} after the first connection");
                Assert.NotNull(secondConnection);
                Assert.Equal(ChannelState.Open, onSecondConnection.State);
                Assert.False(onSecondConnection.IsReestablished);
                Assert.Equal(0, onSecondConnection.OfferedHtlcCount);
                Assert.Equal("unpaid", await ClnInvoiceStatusAsync(pair, invoice.PaymentHashHex, ct));
                await PayClnAsync(pair, HtlcMsat, ct);
                await WaitBalancesAsync(pair, PushMsat + HtlcMsat, ct);

                Log($"{pair.Namespace}: redial to the frozen CLN connected ({clnSockets} established on CLN's p2p port), "
                  + $"payment refused ({refused.FailureReason}) {refusedAt.TotalSeconds:F1} s in, gated "
                  + $"{gated.TotalSeconds:F1} s, first connection gone {firstConnectionGoneAt?.TotalSeconds:F1} s, dropped and redialed by our node {redialedIn.TotalSeconds:F1} s after the "
                  + $"redial (deadline {s_reestablishTimeout.TotalSeconds:F0} s), active {activeIn.TotalSeconds:F1} s after "
                  + "the heal");
                foreach (var fault in pair.Faults.Events)
                    Log($"  {fault}");
            }
            catch when (!resumed)
            {
                await pair.Faults.ResumeAsync(pair.ClnHandle, CancellationToken.None);
                throw;
            }
        }, o => o.ReestablishTimeout = s_reestablishTimeout);

    /// <summary>
    /// CLN split from its bitcoind while blocks are mined: CLN's height stalls, our node follows the chain and still
    /// pays CLN over the established connection; after the heal CLN catches up and pays us.
    /// </summary>
    [Fact(Explicit = true)]
    public Task Given_AClnCutOffFromItsBitcoind_When_BlocksAreMined_Then_ClnStallsAndOurNodeKeepsWorking() =>
        WithClnPairAsync("part-chain", async (pair, ct) =>
        {
            // Arrange
            var miner = pair.Topology.Chain.Node;
            var clnBefore = await pair.Cln.GetBlockHeightAsync(ct);

            // Act: split CLN from bitcoind, mine; our node follows the tip
            var partition = await pair.Faults.PartitionAsync([pair.ClnHandle], [miner], null, ct);
            await pair.Topology.Chain.MineAsync(3, ct);
            var tip = await pair.Topology.Chain.GetBlockCountAsync(ct);
            await ClusterPoll.UntilAsync(async c => await pair.Nltg.GetBlockHeightAsync(c) == tip, s_stepTimeout, s_poll,
                                         $"our node at the tip {tip}", ct);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var clnStalled = await pair.Cln.GetBlockHeightAsync(ct);
            var toCln = await PayClnAsync(pair, HtlcMsat, ct);

            // Act: heal; CLN catches up
            await pair.Faults.HealAsync(partition, ct);
            var heal = Stopwatch.StartNew();
            await ClusterPoll.UntilAsync(async c => await pair.Cln.GetBlockHeightAsync(c) == tip, s_stepTimeout, s_poll,
                                         $"CLN at the tip {tip} after the heal", ct);
            var caughtUp = heal.Elapsed;
            var toUs = await pair.Nltg.CreateInvoiceAsync(SmallMsat, "after the heal", ct);
            var paid = await pair.Cln.PayInvoiceAsync(toUs.Bolt11, ct);

            // Assert
            Assert.Equal(clnBefore, clnStalled);
            Assert.True(toCln.Succeeded, toCln.FailureReason);
            Assert.True(paid.Succeeded, paid.FailureReason);
            await WaitBalancesAsync(pair, PushMsat + HtlcMsat - SmallMsat, ct);

            Log($"{pair.Namespace}: CLN held at {clnStalled} while the tip went to {tip}, paid over the split, caught "
              + $"up {caughtUp.TotalSeconds:F1} s after the heal");
        });

    /// <summary>
    /// Builds bitcoind + our node + CLN (process faults on) with an active channel <c>nltg</c> → <c>cln</c> (a push so CLN
    /// can pay back), runs <paramref name="body"/> (diagnostics dumped on failure while the run is alive), and removes
    /// everything.
    /// </summary>
    private static async Task WithClnPairAsync(string suite, Func<ClnPair, CancellationToken, Task> body,
                                               Action<NodeOptions>? configureNode = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment(suite) with
        {
            Quota = NamespaceQuota.Spike,
            Log = Log
        }, ct);
        try
        {
            await using var inProcess = new InProcessNodeDeployer
            {
                ConfigureNodeOptions = (_, o) =>
                {
                    o.ReconnectMaxDelay = s_reconnectMaxDelay;
                    configureNode?.Invoke(o);
                }
            };
            var builder = new TopologyBuilder
            {
                Log = Log,
                Storage = NodeStorage.Ephemeral,
                ReadyTimeout = TimeSpan.FromMinutes(4),
                StepTimeout = s_stepTimeout
            };
            // CLN's bitcoin-retry-timeout: a partition from bitcoind must not end lightningd (bcli gives up after 60 s)
            using var topology = await builder.AddBitcoinCore("miner")
                                              .AddNLightning("nltg")
                                              .AddCln("cln", extraArgs: ["--bitcoin-retry-timeout=600"])
                                              .UseDeployer(new ClnNodeDeployer(o => o with { ProcessFaults = true }))
                                              .UseInProcessNodes(inProcess)
                                              .FundWallet("nltg", WalletSat)
                                              .AddChannel("nltg", "cln", CapacitySat, PushMsat)
                                              .BuildAsync(run, ct);
            await using var faults = run.CreateFaultInjector(Log);
            var cln = topology.Node<ClnTestPeer>("cln");
            var pair = new ClnPair(topology, topology.InProcessNode("nltg"), cln,
                                   (KubeNodeHandle)cln.Node, faults, Assert.Single(topology.Channels).Open.FundingTxId,
                                   await cln.GetNodeIdAsync(ct));
            Log($"{run.Namespace}: topology with an active channel in {watch.Elapsed.TotalSeconds:F1} s");
            await run.CaptureOnFailureAsync(suite, () => body(pair, ct));
        }
        finally
        {
            await run.DisposeAsync();
        }
    }

    /// <summary>
    /// Checks every second for <see cref="s_hold"/> that our channel satisfies <paramref name="channel"/> (and, when
    /// given, that the payment is still <paramref name="paymentStatus"/>); returns how long it held.
    /// </summary>
    private static Task<TimeSpan> HoldAsync(ClnPair pair, CancellationToken ct,
                                            Func<ChannelInfoClientResponse, bool> channel,
                                            string? paymentHashHex = null, PaymentStatus? paymentStatus = null) =>
        HoldAsync(pair, ct, channel, s_hold, paymentHashHex, paymentStatus);

    /// <summary>
    /// Checks every second for <paramref name="hold"/> that our channel satisfies <paramref name="channel"/> (and, when
    /// given, that the payment is still <paramref name="paymentStatus"/>); returns how long it held.
    /// </summary>
    private static async Task<TimeSpan> HoldAsync(ClnPair pair, CancellationToken ct,
                                                  Func<ChannelInfoClientResponse, bool> channel, TimeSpan hold,
                                                  string? paymentHashHex = null, PaymentStatus? paymentStatus = null)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < hold)
        {
            var ours = await FindOurChannelAsync(pair, ct);
            Assert.True(channel(ours),
                        $"after {watch.Elapsed.TotalSeconds:F1} s: our channel {ours.State}, connected "
                      + $"{ours.IsPeerConnected}, reestablished {ours.IsReestablished}, offered {ours.OfferedHtlcCount}");
            if (paymentHashHex is not null)
            {
                var payment = await pair.Nltg.TestNode.GetPaymentAsync(Hash(paymentHashHex), ct);
                Assert.Equal(paymentStatus, payment?.Status);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return watch.Elapsed;
    }

    private static Task<PaymentInfoClientResponse> WaitPaymentAsync(ClnPair pair, string paymentHashHex,
                                                                    PaymentStatus status, CancellationToken ct) =>
        ClusterPoll.ForAsync(async c =>
        {
            var payment = await pair.Nltg.TestNode.GetPaymentAsync(Hash(paymentHashHex), c);
            if (payment is { Status: PaymentStatus.Failed } && status != PaymentStatus.Failed)
                throw new InvalidOperationException($"the payment failed: {payment.FailureReason}");
            return payment?.Status == status ? payment : null;
        }, s_stepTimeout, s_poll, $"our payment {paymentHashHex} {status}", ct);

    private static async Task<ChannelInfoClientResponse> FindOurChannelAsync(ClnPair pair, CancellationToken ct) =>
        await pair.Nltg.FindChannelAsync(pair.FundingTxId, ct)
     ?? throw new InvalidOperationException($"our node has no channel {pair.FundingTxId}");

    private static Task<ChannelInfoClientResponse> WaitOurChannelAsync(ClnPair pair,
                                                                      Func<ChannelInfoClientResponse, bool> condition,
                                                                      string what, CancellationToken ct) =>
        ClusterPoll.ForAsync(async c =>
        {
            var channel = await FindOurChannelAsync(pair, c);
            return condition(channel) ? channel : null;
        }, s_stepTimeout, s_poll, what, ct);

    private static async Task WaitActiveBothEndsAsync(ClnPair pair, CancellationToken ct)
    {
        await TopologyDeployer.WaitChannelActiveAsync(pair.Nltg, pair.ClnId, pair.FundingTxId, s_stepTimeout, ct);
        await TopologyDeployer.WaitChannelActiveAsync(pair.Cln, pair.Nltg.TestNode.NodeIdHex, pair.FundingTxId,
                                                      s_stepTimeout, ct);
    }

    /// <summary>Our node pays a fresh CLN invoice of <paramref name="amountMsat"/>; asserts it succeeded.</summary>
    private static async Task<TestPaymentResult> PayClnAsync(ClnPair pair, long amountMsat,
                                                                                    CancellationToken ct)
    {
        var invoice = await pair.Cln.CreateInvoiceAsync(amountMsat, "partition test", ct);
        var result = await pair.Nltg.PayInvoiceAsync(invoice.Bolt11, ct);
        Assert.True(result.Succeeded, result.FailureReason);
        return result;
    }

    /// <summary>Waits until our side holds the capacity less <paramref name="clnMsat"/> and CLN's holds it.</summary>
    private static async Task WaitBalancesAsync(ClnPair pair, long clnMsat, CancellationToken ct)
    {
        await ClusterPoll.UntilDoneAsync(async c =>
        {
            var ours = await FindOurChannelAsync(pair, c);
            var theirs = (await pair.Cln.ListChannelsAsync(c)).FirstOrDefault(x => x.FundingTxId == pair.FundingTxId);
            var oursMsat = (long)ours.LocalBalance.MilliSatoshi;
            return oursMsat == CapacitySat * 1000 - clnMsat && theirs?.LocalBalanceMsat == clnMsat
                       ? null
                       : $"ours {oursMsat}, CLN's {theirs?.LocalBalanceMsat} (CLN expected {clnMsat})";
        }, s_stepTimeout, "balances agree", ct, s_poll);
    }

    /// <summary>
    /// Our node's own liveness check (the ping before a <c>commitment_signed</c>, NL-251): without a pong in 3 s it
    /// closes the connection like a dead link, which starts its reconnect backoff.
    /// </summary>
    private static async Task<bool> PingClnAsync(ClnPair pair, CancellationToken ct)
    {
        var peerId = new CompactPubKey(Convert.FromHexString(pair.ClnId));
        var peer = pair.Nltg.TestNode.PeerManager.GetPeer(peerId)
                ?? throw new InvalidOperationException("our node is not connected to CLN");
        if (!peer.TryGetPeerService(out var service))
            throw new InvalidOperationException("CLN's peer has no service");

        var pong = await service.PingAsync(TimeSpan.FromSeconds(3), ct);
        await ClusterPoll.UntilAsync(_ => Task.FromResult(!pair.Nltg.TestNode.IsConnectedTo(peerId)), s_stepTimeout,
                                     s_poll, "our node drops the silent CLN", ct);
        return pong;
    }

    /// <summary>CLN closes its connection to our node (a drop our node did not ask for, so it redials).</summary>
    private static async Task DropFromClnSideAsync(ClnPair pair, CancellationToken ct)
    {
        await pair.Cln.Rpc.CallAsync("disconnect", ct, ("id", pair.Nltg.TestNode.NodeIdHex), ("force", true));
        var peerId = new CompactPubKey(Convert.FromHexString(pair.ClnId));
        await ClusterPoll.UntilAsync(_ => Task.FromResult(!pair.Nltg.TestNode.IsConnectedTo(peerId)), s_stepTimeout,
                                     s_poll, "our node sees CLN go", ct);
    }

    private static async Task<bool> IsClnConnectedToUsAsync(ClnPair pair, CancellationToken ct)
    {
        var peers = await pair.Cln.Rpc.CallAsync("listpeers", ct, ("id", pair.Nltg.TestNode.NodeIdHex));
        return peers["peers"]?.AsArray().FirstOrDefault()?["connected"]?.GetValue<bool>() == true;
    }

    private static async Task<string?> ClnInvoiceStatusAsync(ClnPair pair, string paymentHashHex, CancellationToken ct)
    {
        var invoices = await pair.Cln.Rpc.CallAsync("listinvoices", ct, ("payment_hash", paymentHashHex));
        return invoices["invoices"]?.AsArray().FirstOrDefault()?["status"]?.GetValue<string>();
    }

    private static Hash Hash(string hex) => new(Convert.FromHexString(hex));

    private sealed record ClnPair(TestTopology Topology, InProcessNode Nltg, ClnTestPeer Cln, KubeNodeHandle ClnHandle,
                                  FaultInjector Faults, string FundingTxId, string ClnId)
    {
        public string Namespace => Topology.Run.Namespace;
    }
}