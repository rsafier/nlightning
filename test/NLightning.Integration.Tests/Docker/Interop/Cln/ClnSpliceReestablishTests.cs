using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Abcd;
using Daemon.Extensions;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Payments.Enums;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Serialization.Interfaces;
using Fixtures;
using Infrastructure.Crypto.Interfaces;
using Infrastructure.Node.ValueObjects;
using Infrastructure.Protocol.Factories;
using Infrastructure.Protocol.Models;
using Infrastructure.Transport.Events;
using Infrastructure.Transport.Factories;
using Infrastructure.Transport.Interfaces;
using Utils;
using SpliceWireMessage = ClnSpliceTests.SpliceWireMessage;

/// <summary>
/// Proof SP2 of the splicing plan (<c>docs/agents/SPLICING_PLAN.md</c> §5 "Proof SP2", wave sp2 lane SP2-D, NL-021)
/// against Core Lightning v26.06.8, with our node on <c>Features:AllowExperimentalFeatures</c>,
/// <c>OptionQuiesce = Optional</c> and <c>OptionSplice = Optional</c>: (a) the connection cut in four places of a
/// splice (after our <c>commitment_signed</c>, after CLN's, after our <c>tx_signatures</c>, after both), each once with
/// a reconnection and once with a restart of our node, and CLN restarted once mid-splice: the splice completes after
/// <c>channel_reestablish</c> (SP-RE-01..03, SP-T-03..06) and locks; (b) both ends reach the lock depth while
/// disconnected and <c>my_current_funding_locked</c> completes the lock (SP-RE-04, SP-T-08); (c) a public channel is
/// spliced: the short channel id switches, both ends exchange <c>announcement_signatures</c> for it and CLN lists the
/// new <c>channel_announcement</c> (SP-G-01), and forwards through our node to the new short channel id and, within the
/// 72-block retention, to the old one succeed while the old one fails with <c>unknown_next_peer</c> afterwards (D12).
/// Every lock is checked in our <c>listchannels</c> too: the current funding, the replaced one and the retired short
/// channel id (SP2-D-T1).
/// </summary>
/// <remarks>
/// <para><b>What CLN v26.06.8 does (checked in wave sp2 lane SP2-D, 2026-09-27, on the pinned image with
/// <c>--developer --dev-bitcoind-poll=1</c>, two CLN nodes and a public anchors channel between them):</b></para>
/// <list type="bullet">
/// <item><c>listpeerchannels</c> while a splice is pending: <c>CHANNELD_AWAITING_SPLICE</c> on both ends (with the
/// low-level <c>splice_init</c>/<c>splice_update</c> flow the initiator stays <c>CHANNELD_NORMAL</c> until
/// <c>splice_signed</c>), the old <c>short_channel_id</c>, <c>funding_txid</c> and <c>total_msat</c>, and one
/// <c>inflight</c> entry: <c>funding_txid</c>, <c>funding_outnum</c>, <c>feerate</c> (<c>"253perkw"</c>),
/// <c>total_funding_msat</c>, <c>our_funding_msat</c>, <c>splice_amount</c> (the node's own contribution, 0 on the
/// accepter) and <c>scratch_txid</c>. At the first confirmation both ends sent <c>splice_locked</c> and list
/// <c>CHANNELD_NORMAL</c> on the new funding, total and short channel id with no <c>inflight</c>.</item>
/// <item>Re-announcement: a public channel spliced by a transaction confirmed at height H (108x1x0 spliced by
/// 115x1x1) is in CLN's <c>listchannels</c> under the new short channel id, both directions, from H + 5 (6
/// confirmations); the old short channel id stays listed and active until the splice is 72 blocks deep (listed at
/// H + 71, gone at H + 73), BOLT 7's forget delay (SP-G-02).</item>
/// <item>Restart mid-splice (the accepter restarted once commitments were secured): CLN keeps the inflight
/// (<c>Watching splice inflight &lt;txid&gt;</c> at start) and reconnects with <c>channel_reestablish</c> carrying
/// <c>next_funding</c> = the splice and <c>my_current_funding_locked</c> = the current funding (log: <c>Sending
/// channel_reestablish with next_funding_tx_id: &lt;txid&gt;, my_current_funding_locked: &lt;txid&gt;</c>), then
/// <c>Resuming splice negotation.</c> A restarted CLN does not dial a peer that had connected to it (<c>Failed
/// connected out: Unable to connect, no address known for peer</c>): our node reconnects (backoff or an explicit
/// connect). When the channel is idle CLN still sends <c>my_current_funding_locked</c> = its current funding.</item>
/// <item>Lock depth reached while disconnected (seen against our node in this lane's harness run, before the SP2-A/B
/// lanes merged, 2026-09-27): our splice was signed both ways, the link cut, 4 blocks mined; on reconnection CLN sent
/// <c>my_current_funding_locked</c> = the <b>pre-splice</b> funding (not the splice that reached its depth while
/// disconnected, which SP-RE-02 as our plan reads it would name), stayed <c>CHANNELD_AWAITING_SPLICE</c>, sent no
/// <c>splice_locked</c> and retransmitted <c>channel_ready</c> (no splice TLV on either side, SP-RE-05). BOLT 2
/// (channel_reestablish: "if a splice transaction reached acceptable depth while disconnected: MUST include
/// <c>my_current_funding_locked</c> with the txid of the latest such transaction") makes that a CLN deviation. Proof
/// (b) therefore holds our <c>my_current_funding_locked</c> strictly (the splice, bit 0 clear on a private channel)
/// and branches on CLN's: when it names the splice the lock must complete at the same tip (our SP-RE-04); when it
/// does not, the deviation is logged and one more block is allowed for CLN's <c>splice_locked</c>.</item>
/// <item>CLN fails the channel (<c>error</c> "tx_abort is not allowed after I have sent my signature" and a unilateral
/// close) when it gets <c>tx_abort</c> after its own <c>tx_signatures</c>. Between two CLN nodes that happened when the
/// initiator of a low-level splice was restarted before <c>splice_signed</c> (<c>Unable to resume splice as user
/// sig(s) are missing</c>: it omits <c>next_funding</c> and answers the peer's with <c>tx_abort</c> "next_funding_txid
/// not recognized"). The proof drives CLN only through <c>splicein</c>, which signs CLN's inputs inside the call, and
/// our node never sends <c>tx_abort</c> after its <c>tx_signatures</c> (IT-ABT-01), so it does not hit that path.</item>
/// <item><c>sendpay</c> takes a circular route back to CLN over one channel (<c>[{us, scid}, {CLN, scid}]</c> with
/// the invoice's <c>payment_secret</c>), and <c>waitsendpay</c> reports the failure code of a hop by name
/// (<c>WIRE_UNKNOWN_NEXT_PEER</c>): proof (c) names the short channel id our node must forward to.</item>
/// </list>
/// <para>How the cuts work: <see cref="SpliceLinkCutter"/> records our transport traffic (the
/// <see cref="ClnSpliceTests.SpliceWireRecorder"/> pattern) and wraps the node's <see cref="ITcpService"/>. An armed
/// cut drops the first inbound message of a type (our node never processes it) and everything CLN sends after it,
/// resets every socket 2 s later (what our node sends in reaction to what it did receive still goes out) and until
/// <see cref="SpliceLinkCutter.Heal"/> refuses new connections both ways, so what CLN sent after the cut point is lost
/// as on a real link failure. The four points, from our node's side: (1) we splice in and drop CLN's splice
/// <c>commitment_signed</c> (the two cross on the wire; ours goes out in the grace; <c>next_funding</c> with
/// <c>retransmit_flags</c> bit 0 set, and CLN's <c>tx_signatures</c>, sent once it has our CS, is lost too); (2) we splice
/// in, CLN signs first (smaller contribution) and we drop its <c>tx_signatures</c> (both CS received, no
/// <c>tx_signatures</c> either way from us); (3) CLN splices in, we sign first and drop CLN's <c>tx_signatures</c>
/// (ours sent); (4) we splice in and cut once both <c>tx_signatures</c> passed on our side (CLN's received, ours
/// written, the splice broadcast and 2 s of grace for ours to reach CLN; our <c>next_funding</c> absent is what is
/// asserted, CLN's is not, since nothing on the wire confirms that CLN read ours before the reset). The restart
/// variants stop our node while cut and start it again on the same database. CLN is restarted once in a separate test
/// on its own container (<c>nltg-cln-sp2</c>, a fixed host port, so its address survives the restart and the shared
/// fixture's CLN is never restarted).</para>
/// <para>Not covered here: LND 0.20 learning the spliced channel (plan (c) mentions it; the CLN fixture has no LND),
/// and the on-chain part (d) (<c>Docker/Onchain/OnchainSpliceTests</c>). Written against the SP2 contracts
/// (3560f3a9); the node side (reestablish, lock, SCID map, announcements) lands in lanes SP2-A/B and the integrator
/// runs it after the merge, from the host process: <c>NLightning.Integration.Tests -class
/// NLightning.Integration.Tests.Docker.Interop.Cln.ClnSpliceReestablishTests</c>.</para>
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnSpliceReestablishTests : IAsyncLifetime
{
    /// <summary>Where the connection is cut, from our node's side (see the class remarks).</summary>
    public enum CutPoint
    {
        AfterOurCommitmentSigned,
        AfterClnCommitmentSigned,
        AfterOurTxSignatures,
        AfterBothTxSignatures
    }

    private const int TestTimeoutMs = 15 * 60 * 1_000;

    /// <summary>The most blocks mined one at a time until both ends locked a splice.</summary>
    private const int MaxLockBlocks = 12;

    /// <summary>The most blocks mined after the lock until CLN lists the new channel_announcement.</summary>
    private const int MaxAnnounceBlocks = 12;

    /// <summary>The feerate of our splices: the test node's 10 sat/vB estimate, inside CLN's acceptable range.</summary>
    private const uint OurFeeRatePerKw = 2_500;

    private const ulong SpliceInSat = 100_000;

    // The dedicated CLN for the restart case, on the fixture's bitcoind (ClnFixture keeps those settings private)
    private const string DedicatedClnName = "nltg-cln-sp2";
    private const int P2PPort = 9735;
    private const int BitcoinRpcPort = 18443;
    private const string BitcoinRpcUser = "nltg";
    private const string BitcoinRpcPassword = "nltg";

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(400_000);
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_reconnectTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_settleTimeout = TimeSpan.FromSeconds(60);

    private readonly ClnFixture _fixture;
    private readonly DockerClient _docker = new DockerClientConfiguration().CreateClient();
    private readonly List<Task> _background = [];
    private SpliceChannel? _channel;
    private ClnPeer? _dedicatedCln;
    private int _dedicatedClnPort;

    public ClnSpliceReestablishTests(ClnFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    /// <summary>The cut points of proof (a), each with a reconnection and with a restart of our node.</summary>
    public static TheoryData<CutPoint, bool> CutPoints =>
        new()
        {
            { CutPoint.AfterOurCommitmentSigned, false },
            { CutPoint.AfterClnCommitmentSigned, false },
            { CutPoint.AfterOurTxSignatures, false },
            { CutPoint.AfterBothTxSignatures, false },
            { CutPoint.AfterOurCommitmentSigned, true },
            { CutPoint.AfterClnCommitmentSigned, true },
            { CutPoint.AfterOurTxSignatures, true },
            { CutPoint.AfterBothTxSignatures, true }
        };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var task in _background)
        {
            // The IPC splice calls cut short by a disconnection or a restart: observe them, never wait long
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception e)
            {
                Console.WriteLine($"[proof] background call ended with {e.GetType().Name}: {e.Message}");
            }
        }

        if (_channel is not null)
        {
            Console.WriteLine($"[wire] {_channel.Cutter.Describe()}");
            try
            {
                Console.WriteLine("[cln] splice and reestablish log lines for our node:\n"
                                + await GetClnLogForUsAsync(_channel, "plice", "eestablish", "STFU", "inflight",
                                                            "next_funding", "abort"));
                Console.WriteLine("[cln] unusual/broken log lines so far:\n"
                                + await _channel.Cln.Client.GetLogLinesAsync(string.Empty, CancellationToken.None,
                                                                             60, "unusual"));
                if (DockerDiagnostics.CurrentTestFailed)
                    Console.WriteLine($"[cln] channel at failure: {await _channel.DescribeAsync(CancellationToken.None)}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[cln] diagnostics unavailable: {e.Message}");
            }

            await _channel.DisposeAsync();
        }

        if (_dedicatedCln is not null)
        {
            await DockerContainerUtils.RemoveContainerAsync(_docker, DedicatedClnName);
            PortPoolUtil.ReleasePort(_dedicatedClnPort);
        }

        _docker.Dispose();
    }

    /// <summary>
    /// Proof SP2 (a), SP-T-03..06 against CLN: the connection is cut (or our node restarted) at <paramref name="cut"/>
    /// of a splice-in; after the reconnection our <c>channel_reestablish</c> carries <c>next_funding</c> for the splice
    /// with <c>retransmit_flags</c> bit 0 set exactly when CLN's <c>commitment_signed</c> never reached us (SP-RE-01),
    /// CLN's missing messages are retransmitted (SP-RE-03), the splice transaction is broadcast, and it locks on both
    /// ends with the new short channel id (our <c>listchannels</c>: current funding = the splice, the replaced one and
    /// the retired short channel id listed); payments flow both ways after. Nobody sends <c>tx_abort</c> or an
    /// <c>error</c>.
    /// </summary>
    [Theory(Timeout = TestTimeoutMs)]
    [MemberData(nameof(CutPoints))]
    public async Task Given_ASpliceCutMidway_When_ReconnectedOrRestarted_Then_TheSpliceCompletesAndLocks(
        CutPoint cut, bool restartOurNode)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var channel = await BuildAsync(FixturePeer(), $"nltg-sp2-a{(int)cut}{(restartOurNode ? "r" : "d")}",
                                       isPublic: false, ct);
        var before = await SnapshotAsync(channel, ct);
        var wire = channel.Cutter;
        var from = wire.CurrentSequence;
        if (cut == CutPoint.AfterOurTxSignatures)
            await FundClnAsync(channel.Cln, LightningMoney.Satoshis(300_000), [channel.Node], ct);

        // Act: start the splice with the cut armed, wait for the cut
        var armed = cut switch
        {
            CutPoint.AfterOurCommitmentSigned => wire.ArmInboundCut(MessageTypes.CommitmentSigned, from),
            CutPoint.AfterClnCommitmentSigned or CutPoint.AfterOurTxSignatures =>
                wire.ArmInboundCut(MessageTypes.TxSignatures, from),
            _ => null
        };
        if (cut == CutPoint.AfterOurTxSignatures)
            StartClnSpliceIn(channel, SpliceInSat, ct);
        else
            StartOurSpliceIn(channel, SpliceInSat);

        if (armed is not null)
        {
            var dropped = await armed.WaitAsync(s_stepTimeout, ct);
            Console.WriteLine($"[proof] cut at {cut}: dropped CLN's {(MessageTypes)dropped.Type} #{dropped.Sequence}");
        }
        else
        {
            await Poll.UntilAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxSignatures, from) is not null
                                     && wire.FirstOrDefault(inbound: true, MessageTypes.TxSignatures, from) is not null,
                                  s_stepTimeout, "both tx_signatures", ct);
            // Ours is recorded before it is written: give it time to reach CLN (the broadcast follows our send, the
            // grace lets CLN read it) before the reset, so the cut lands after both signatures on both sides as far
            // as the wire allows; only our side (both signatures sent and received) is asserted below
            await WaitBroadcastAsync(FindSpliceTxId(wire, from), ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            wire.CutNow();
            Console.WriteLine($"[proof] cut at {cut}: after both tx_signatures");
        }

        var spliceTxId = FindSpliceTxId(wire, from);
        Console.WriteLine($"[proof] splice {spliceTxId}");
        AssertStateAtCut(wire, from, cut);

        // ...reconnect (or restart our node) and let the splice finish
        var reconnectedFrom = wire.CurrentSequence;
        if (restartOurNode)
        {
            await channel.StopNodeAsync();
            wire.Heal();
            await channel.StartNodeAsync(ct);
        }
        else
        {
            wire.Heal();
        }

        await channel.ReconnectAsync(ct);

        // Assert: our channel_reestablish names the splice exactly when it is not signed both ways (SP-RE-01)
        var ourReestablish = await Poll.ForAsync(
                                 () => wire.FirstOrDefault(inbound: false, MessageTypes.ChannelReestablish,
                                                           reconnectedFrom), s_stepTimeout,
                                 "our channel_reestablish", ct);
        var theirReestablish = await Poll.ForAsync(
                                   () => wire.FirstOrDefault(inbound: true, MessageTypes.ChannelReestablish,
                                                             reconnectedFrom), s_stepTimeout,
                                   "CLN's channel_reestablish", ct);
        var ours = ReestablishTlvs.Read(ourReestablish);
        var theirs = ReestablishTlvs.Read(theirReestablish);
        Console.WriteLine($"[proof] our reestablish {ours}; CLN's {theirs}");
        if (cut == CutPoint.AfterBothTxSignatures)
        {
            Assert.Null(ours.NextFundingTxId);
        }
        else
        {
            Assert.Equal(spliceTxId, ours.NextFundingTxId);
            Assert.Equal(cut == CutPoint.AfterOurCommitmentSigned, ours.NextFundingBit0);
        }

        // ...the missing signatures came after the reconnection, the splice is broadcast and signed on our side
        if (cut != CutPoint.AfterBothTxSignatures)
            await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxSignatures, reconnectedFrom),
                                s_stepTimeout, "CLN's tx_signatures after the reconnection", ct);
        await WaitBroadcastAsync(spliceTxId, ct);
        await AssertClnPendingOrLockedAsync(channel, spliceTxId, ct);

        // ...lock, SCID switch, payments
        var after = await MineUntilLockedAsync(channel, before, spliceTxId, ct);
        // Whoever splices in pays the fee from its wallet inputs: the capacity grows by the amount (Proof SP1 (a), (b))
        Assert.Equal(before.CapacitySat + SpliceInSat, after.CapacitySat);
        await AssertWePayClnAsync(channel, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(channel, LightningMoney.Satoshis(11_000), ct);
        AssertNoAbortOrError(wire, from);
    }

    /// <summary>
    /// Proof SP2 (a), "restart CLN once": on a CLN of its own (<c>nltg-cln-sp2</c>), we splice in and the connection
    /// is cut when CLN's splice <c>commitment_signed</c> arrives (dropped); CLN is restarted (a container restart) while
    /// cut. CLN reloads the inflight and, once our node reconnects, both send <c>next_funding</c> for the splice, the
    /// signatures are exchanged and the splice locks; payments both ways after.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ClnRestartedMidSplice_When_OurNodeReconnects_Then_TheSpliceCompletesAndLocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var cln = await StartDedicatedClnAsync(ct);
        var channel = await BuildAsync(cln, "nltg-sp2-clnrestart", isPublic: false, ct);
        var before = await SnapshotAsync(channel, ct);
        var wire = channel.Cutter;
        var from = wire.CurrentSequence;

        // Act: cut at CLN's splice commitment_signed, restart CLN while cut
        var armed = wire.ArmInboundCut(MessageTypes.CommitmentSigned, from);
        StartOurSpliceIn(channel, SpliceInSat);
        await armed.WaitAsync(s_stepTimeout, ct);
        var spliceTxId = FindSpliceTxId(wire, from);
        Console.WriteLine($"[proof] cut at CLN's commitment_signed; splice {spliceTxId}; restarting {DedicatedClnName}");
        await _docker.Containers.RestartContainerAsync(DedicatedClnName,
                                                       new ContainerRestartParameters { WaitBeforeKillSeconds = 10 },
                                                       ct);
        await DockerContainerUtils.WaitUntilReadyAsync(DedicatedClnName,
                                                       async c => await cln.Client.GetInfoAsync(c),
                                                       TimeSpan.FromMinutes(2));
        var restarted = await cln.Client.GetPeerChannelAsync(channel.Node.NodeIdHex, channel.ChannelIdHex, ct);
        Console.WriteLine($"[cln] after its restart: {(restarted is null ? "not listed" : DescribeCln(restarted))} "
                        + $"inflight {restarted?["inflight"]?.ToJsonString()}");
        var reconnectedFrom = wire.CurrentSequence;
        wire.Heal();
        await channel.ReconnectAsync(ct);

        // Assert: both name the splice in channel_reestablish, the splice finishes and locks
        var ourReestablish = await Poll.ForAsync(
                                 () => wire.FirstOrDefault(inbound: false, MessageTypes.ChannelReestablish,
                                                           reconnectedFrom), s_stepTimeout,
                                 "our channel_reestablish", ct);
        var theirReestablish = await Poll.ForAsync(
                                   () => wire.FirstOrDefault(inbound: true, MessageTypes.ChannelReestablish,
                                                             reconnectedFrom), s_stepTimeout,
                                   "CLN's channel_reestablish", ct);
        var ours = ReestablishTlvs.Read(ourReestablish);
        var theirs = ReestablishTlvs.Read(theirReestablish);
        Console.WriteLine($"[proof] our reestablish {ours}; CLN's {theirs}");
        Assert.Equal(spliceTxId, ours.NextFundingTxId);
        Assert.True(ours.NextFundingBit0, "we never got CLN's commitment_signed, bit 0 must ask for it");
        await WaitBroadcastAsync(spliceTxId, ct);
        var after = await MineUntilLockedAsync(channel, before, spliceTxId, ct);
        Assert.Equal(before.CapacitySat + SpliceInSat, after.CapacitySat);
        await AssertWePayClnAsync(channel, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(channel, LightningMoney.Satoshis(11_000), ct);
        AssertNoAbortOrError(wire, from);
    }

    /// <summary>
    /// Proof SP2 (b), SP-T-08: we splice in, both <c>tx_signatures</c> pass, the connection is cut, and the splice is
    /// mined past both ends' lock depth while nobody can send <c>splice_locked</c>. On reconnection both
    /// <c>channel_reestablish</c> carry <c>my_current_funding_locked</c> = the splice (SP-RE-02), which completes the
    /// lock without another block (SP-RE-04): new funding, capacity and short channel id on both ends; payments after.
    /// Ours is asserted strictly; CLN v26.06.8 names the pre-splice funding instead (a BOLT 2 deviation, class
    /// remarks), in which case one more block is allowed for its <c>splice_locked</c> and the deviation is logged.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_TheLockDepthReachedWhileDisconnected_When_Reconnected_Then_MyCurrentFundingLockedLocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var channel = await BuildAsync(FixturePeer(), "nltg-sp2-b", isPublic: false, ct);
        var before = await SnapshotAsync(channel, ct);
        var wire = channel.Cutter;
        var from = wire.CurrentSequence;
        StartOurSpliceIn(channel, SpliceInSat);
        await Poll.UntilAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxSignatures, from) is not null
                                 && wire.FirstOrDefault(inbound: true, MessageTypes.TxSignatures, from) is not null,
                              s_stepTimeout, "both tx_signatures", ct);
        var spliceTxId = FindSpliceTxId(wire, from);
        await WaitBroadcastAsync(spliceTxId, ct);

        // Act: cut, mine past the lock depth of both ends (ours: the channel's minimum_depth, D8; CLN: 1 on regtest)
        wire.CutNow();
        var depth = Math.Max(3, (int)(channel.Node.ChannelMemoryRepository.TryGetChannel(channel.ChannelId, out var model)
                                          ? model.ChannelParams.MinimumDepth
                                          : 3));
        await channel.MineAsync(depth + 1, ct);
        Assert.DoesNotContain(wire.Snapshot(), m => m.Message.Type == (ushort)MessageTypes.SpliceLocked
                                                  && m.Message.Sequence >= from && !m.Dropped);
        var tip = await _fixture.Bitcoin.Rpc.GetBlockCountAsync(ct);
        var reconnectedFrom = wire.CurrentSequence;
        wire.Heal();
        await channel.ReconnectAsync(ct);

        // Assert: both name the splice as their locked funding and the lock completes at this tip
        var ourReestablish = await Poll.ForAsync(
                                 () => wire.FirstOrDefault(inbound: false, MessageTypes.ChannelReestablish,
                                                           reconnectedFrom), s_stepTimeout,
                                 "our channel_reestablish", ct);
        var theirReestablish = await Poll.ForAsync(
                                   () => wire.FirstOrDefault(inbound: true, MessageTypes.ChannelReestablish,
                                                             reconnectedFrom), s_stepTimeout,
                                   "CLN's channel_reestablish", ct);
        var ours = ReestablishTlvs.Read(ourReestablish);
        var theirs = ReestablishTlvs.Read(theirReestablish);
        Console.WriteLine($"[proof] our reestablish {ours}; CLN's {theirs}");
        Assert.Null(ours.NextFundingTxId);
        Assert.Equal(spliceTxId, ours.CurrentFundingLockedTxId);
        // A private channel: no announcement_signatures to ask for (BOLT 2 retransmit_flags bit 0 needs announce_channel)
        Assert.False(ours.CurrentFundingLockedBit0, "bit 0 of my_current_funding_locked set on a private channel");
        if (theirs.CurrentFundingLockedTxId == spliceTxId)
        {
            // CLN names the splice as BOLT 2 requires: its my_current_funding_locked is its splice_locked, so the lock
            // completes at this tip without another block (SP-RE-04 on our side)
            Console.WriteLine("[proof] CLN's my_current_funding_locked names the splice");
            await Poll.UntilAsync(async () => await IsLockedOnBothEndsAsync(channel, spliceTxId, ct), s_stepTimeout,
                                  "the lock completed by my_current_funding_locked", ct);
            Assert.Equal(tip, await _fixture.Bitcoin.Rpc.GetBlockCountAsync(ct));
        }
        else
        {
            // Known CLN deviation (v26.06.8, class remarks): BOLT 2 channel_reestablish says "if a splice transaction
            // reached acceptable depth while disconnected: MUST include my_current_funding_locked with the txid of the
            // latest such transaction", and CLN names the pre-splice funding instead and sends no splice_locked until
            // a block arrives. Our side cannot lock without CLN's lock, so one more block is allowed here; what this
            // proof holds us to is our own my_current_funding_locked above
            Console.WriteLine($"[proof] CLN deviation: my_current_funding_locked {theirs.CurrentFundingLockedTxId} "
                            + $"instead of the splice {spliceTxId} that reached its depth while disconnected; "
                            + "allowing one block for CLN's splice_locked");
            if (!await PollAsync(() => IsLockedOnBothEndsAsync(channel, spliceTxId, ct), s_settleTimeout, ct))
            {
                await channel.MineAsync(1, ct);
                await Poll.UntilAsync(async () => await IsLockedOnBothEndsAsync(channel, spliceTxId, ct),
                                      s_stepTimeout, "the lock after one block (CLN deviation)", ct);
                Assert.Equal(tip + 1, await _fixture.Bitcoin.Rpc.GetBlockCountAsync(ct));
            }
        }

        var after = await AssertLockedAsync(channel, before, spliceTxId, ct);
        Assert.Equal(before.CapacitySat + SpliceInSat, after.CapacitySat);
        await AssertWePayClnAsync(channel, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(channel, LightningMoney.Satoshis(11_000), ct);
        AssertNoAbortOrError(wire, from);
    }

    /// <summary>
    /// Proof SP2 (c), SP-G-01 and D12: a public channel we fund to CLN, announced; we splice in; after the lock the
    /// short channel id switches on both ends and our <c>listchannels</c> lists the old one as retired for 72 blocks;
    /// by 6 confirmations both ends exchanged <c>announcement_signatures</c> for the new short channel id and CLN lists
    /// its <c>channel_announcement</c> (both directions) while it still lists the old one. CLN pays itself through our
    /// node with the onion naming the new short channel id, then the old one (our retired-SCID map resolves it); once
    /// the retention is over the old one fails with <c>unknown_next_peer</c> and CLN no longer lists it.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_APublicChannelSpliced_When_Locked_Then_ReannouncedAndTheOldScidForwardsForItsRetention()
    {
        // Arrange: an announced public channel
        var ct = TestContext.Current.CancellationToken;
        var channel = await BuildAsync(FixturePeer(), "nltg-sp2-c", isPublic: true, ct);
        var before = await SnapshotAsync(channel, ct);
        var oldScid = ParseScid(before.ShortChannelId);
        await MineUntilAsync(channel, async () => await ClnListsBothDirectionsAsync(channel, oldScid, ct),
                             MaxAnnounceBlocks, "CLN lists the announced channel", ct);
        var wire = channel.Cutter;
        var from = wire.CurrentSequence;

        // Act: splice in and lock
        var response = await SpliceInAsync(channel, SpliceInSat, ct);
        Assert.Equal(SpliceNegotiationState.Signed, response.State);
        Assert.NotNull(response.SpliceTxId);
        var spliceTxId = new uint256((byte[])response.SpliceTxId.Value);
        await WaitBroadcastAsync(spliceTxId, ct);
        var after = await MineUntilLockedAsync(channel, before, spliceTxId, ct);
        var newScid = after.ShortChannelId;
        Assert.NotEqual(oldScid, newScid);

        // Assert: re-announced at 6 confirmations, the old one still listed by CLN (SP-G-02)
        await MineUntilAsync(channel, async () => await ClnListsBothDirectionsAsync(channel, newScid, ct),
                             MaxAnnounceBlocks, "CLN lists the splice's channel_announcement", ct);
        var spliceConfirmations = (await _fixture.Bitcoin.Rpc.GetRawTransactionInfoAsync(spliceTxId, ct)).Confirmations;
        Assert.True(spliceConfirmations >= 6, $"announced at {spliceConfirmations} confirmations");
        Assert.True(await ClnListsBothDirectionsAsync(channel, oldScid, ct),
                    "CLN forgot the old short channel id before the 72 blocks");
        var ourAnnouncementSigs = wire.Snapshot().Where(m => !m.Message.Inbound && m.Message.Sequence > from
                                                          && m.Message.Type == (ushort)MessageTypes.AnnouncementSignatures)
                                      .ToList();
        var theirAnnouncementSigs = wire.Snapshot().Where(m => m.Message.Inbound && m.Message.Sequence > from
                                                            && m.Message.Type
                                                            == (ushort)MessageTypes.AnnouncementSignatures)
                                        .ToList();
        Assert.Contains(ourAnnouncementSigs, m => AnnouncementScid(m.Message) == newScid);
        Assert.Contains(theirAnnouncementSigs, m => AnnouncementScid(m.Message) == newScid);

        // ...forwards through our node to the new and to the old short channel id
        await ClnPaysItselfThroughUsAsync(channel, newScid, newScid, 30_000_000, expectFailure: null, ct);
        await ClnPaysItselfThroughUsAsync(channel, newScid, oldScid, 31_000_000, expectFailure: null, ct);

        // ...after the retention the old one fails with unknown_next_peer and CLN forgot it
        var ourChannel = await channel.GetOurChannelAsync(ct);
        var retired = Assert.Single(ourChannel.RetiredShortChannelIds, r => r.ShortChannelId.ToUInt64() == oldScid);
        var tip = (uint)await _fixture.Bitcoin.Rpc.GetBlockCountAsync(ct);
        if (retired.ExpiresAtHeight > tip)
            await channel.MineAsync((int)(retired.ExpiresAtHeight - tip) + 1, ct);
        await Poll.UntilAsync(async () => (await channel.GetOurChannelAsync(ct)).RetiredShortChannelIds.Count == 0,
                              s_stepTimeout, "our retired short channel id pruned", ct);
        await ClnPaysItselfThroughUsAsync(channel, newScid, oldScid, 32_000_000, "UNKNOWN_NEXT_PEER", ct);
        await Poll.UntilAsync(async () => !await ClnListsAnyDirectionAsync(channel, oldScid, ct), s_stepTimeout,
                              "CLN forgets the old short channel id after 72 blocks", ct);
        await ClnPaysItselfThroughUsAsync(channel, newScid, newScid, 33_000_000, expectFailure: null, ct);
        AssertNoAbortOrError(wire, from);
    }

    #region Building

    private ClnPeer FixturePeer() =>
        new(_fixture.Cln, _fixture.ClnNodeId, () => _fixture.ClnAddress, IsDedicated: false);

    /// <summary>
    /// Our node with the splice features, the splice IPC commands and the link cutter, a channel of
    /// <see cref="s_capacity"/> (<see cref="s_push"/> pushed) we fund to <paramref name="cln"/>, public or private,
    /// usable on both ends. Both ends must see 35 and 63 (D14).
    /// </summary>
    private async Task<SpliceChannel> BuildAsync(ClnPeer cln, string nodeName, bool isPublic, CancellationToken ct)
    {
        var cutter = new SpliceLinkCutter();
        var node = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, nodeName);
        node.ExtraConfiguration["Gossip:Enabled"] = "true";
        node.ExtraConfiguration["Gossip:AcceptPublicChannels"] = "true";
        node.ExtraConfiguration["Node:Alias"] = nodeName;
        node.ConfigureServices = services =>
        {
            services.PostConfigure<NodeOptions>(o =>
            {
                o.Features.AllowExperimentalFeatures = true;
                o.Features.OptionQuiesce = FeatureSupport.Optional;
                o.Features.OptionSplice = FeatureSupport.Optional;
            });
            // Idempotent: the integrator also registers it in AddNltgNodeServices
            services.AddSpliceIpcServices();
            cutter.Install(services);
        };
        var channel = new SpliceChannel(this, node, cln, cutter);
        _channel = channel;

        await channel.StartNodeAsync(ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(s_capacity.Satoshi * 5 / 2), AddressType.P2Wpkh, ct);
        await channel.WaitAtTipAsync(ct);
        await channel.ConnectAsync(ct);
        var opened = await node.OpenChannelAsync(new OpenChannelClientRequest(cln.Address(), s_capacity)
        {
            PushAmount = s_push,
            IsPublic = isPublic
        }, ct);
        channel.ChannelId = opened.ChannelId;
        Console.WriteLine($"[proof] {nodeName} opened {(isPublic ? "public" : "private")} {opened.ChannelId} "
                        + $"({opened.ChannelPoint()}) to {(cln.IsDedicated ? DedicatedClnName : "CLN")}");
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (!await channel.IsUsableAsync(true, ct))
        {
            Assert.True(DateTime.UtcNow < deadline, $"the channel was not usable in time: "
                                                  + await channel.DescribeAsync(ct));
            await channel.MineAsync(1, ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        var peer = await cln.Client.GetPeerAsync(node.NodeIdHex, ct);
        var ourFeatures = peer?["features"]?.GetValue<string>() ?? string.Empty;
        foreach (var feature in new[] { Feature.OptionQuiesce, Feature.OptionSplice })
        {
            Assert.True(ClnOnionMessageTests.IsBitSet(ourFeatures, (int)feature)
                     || ClnOnionMessageTests.IsBitSet(ourFeatures, (int)feature - 1),
                        $"CLN does not see {feature} on us");
            Assert.True(node.PeerManager.GetPeer(cln.PubKey)!.Features.IsFeatureSet(feature),
                        $"CLN does not advertise {feature}");
        }

        return channel;
    }

    /// <summary>
    /// A CLN of this class (<see cref="DedicatedClnName"/>) on the fixture's bitcoind and network, its p2p port
    /// published on a fixed <c>127.0.0.1</c> port so its address survives a container restart; funded.
    /// </summary>
    private async Task<ClnPeer> StartDedicatedClnAsync(CancellationToken ct)
    {
        await DockerContainerUtils.RemoveContainerAsync(_docker, DedicatedClnName);
        _dedicatedClnPort = await PortPoolUtil.GetAvailablePortAsync();
        var portKey = $"{P2PPort}/tcp";
        var container = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = $"{ClnFixture.ClnImage}:{ClnFixture.ClnTag}",
            Name = DedicatedClnName,
            Hostname = DedicatedClnName,
            Env = ["LIGHTNINGD_NETWORK=regtest"],
            Cmd =
            [
                $"--bitcoin-rpcconnect={ClnFixture.BitcoinContainerName}", $"--bitcoin-rpcport={BitcoinRpcPort}",
                $"--bitcoin-rpcuser={BitcoinRpcUser}", $"--bitcoin-rpcpassword={BitcoinRpcPassword}",
                $"--bind-addr=0.0.0.0:{P2PPort}", $"--alias={DedicatedClnName}", "--log-level=debug", "--developer",
                "--dev-bitcoind-poll=1", "--ignore-fee-limits=false"
            ],
            ExposedPorts = new Dictionary<string, EmptyStruct> { [portKey] = default },
            HostConfig = new HostConfig
            {
                NetworkMode = ClnFixture.NetworkName,
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [portKey] =
                    [
                        new PortBinding
                        {
                            HostIP = "127.0.0.1",
                            HostPort = _dedicatedClnPort.ToString(CultureInfo.InvariantCulture)
                        }
                    ]
                },
                ExtraHosts = OperatingSystem.IsLinux() ? [$"{ClnFixture.HostAddressFromContainers}:host-gateway"] : null
            }
        }, ct) ?? throw new InvalidOperationException($"Failed to create {DedicatedClnName}");
        var client = new ClnClient(_docker, DedicatedClnName);
        _dedicatedCln = new ClnPeer(client, string.Empty, () => string.Empty, IsDedicated: true);
        await _docker.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct);
        await DockerContainerUtils.WaitUntilReadyAsync(DedicatedClnName, async c => await client.GetInfoAsync(c),
                                                       TimeSpan.FromMinutes(2));
        var nodeId = (await client.GetInfoAsync(ct))["id"]!.GetValue<string>();
        _dedicatedCln = new ClnPeer(client, nodeId, () => $"{nodeId}@127.0.0.1:{_dedicatedClnPort}",
                                    IsDedicated: true);
        await FundClnAsync(_dedicatedCln, LightningMoney.Satoshis(500_000), [], ct);
        return _dedicatedCln;
    }

    #endregion

    #region Splice drivers

    private void StartOurSpliceIn(SpliceChannel channel, ulong amountSat) =>
        // Not awaited: the IPC call waits for the outcome, which the cut delays or the restart cuts short
        _background.Add(Task.Run(() => SpliceInAsync(channel, amountSat, CancellationToken.None)));

    private void StartClnSpliceIn(SpliceChannel channel, ulong amountSat, CancellationToken ct) =>
        _background.Add(Task.Run(async () =>
        {
            try
            {
                var spliced = await channel.Cln.Client.CallAsync("splicein", ct, ("channel", channel.ChannelIdHex),
                                                                 ("amount", amountSat));
                Console.WriteLine($"[cln] splicein: txid {spliced["txid"]}");
            }
            catch (ClnRpcException e)
            {
                // Expected when the cut comes before CLN's splicein returned; the splice resumes after reestablish
                Console.WriteLine($"[cln] splicein ended with {e.Message}");
            }
        }, ct));

    private static async Task<SpliceClientResponse> SpliceInAsync(SpliceChannel channel, ulong amountSat,
                                                                  CancellationToken ct)
    {
        using var scope = channel.Node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<SpliceInClientRequest, SpliceClientResponse>>();
        var response = await handler.HandleAsync(new SpliceInClientRequest(channel.ChannelId, amountSat)
        {
            FeeRatePerKw = OurFeeRatePerKw
        }, ct);
        Console.WriteLine($"[nltg] splicein: {response.State}, txid {response.SpliceTxId}, capacity "
                        + $"{response.NewCapacitySat}, reason {response.FailureReason}");
        return response;
    }

    /// <summary>
    /// The splice txid: the <c>funding_txid</c> of the first splice <c>commitment_signed</c> recorded (either way,
    /// dropped or not) or the txid of a <c>tx_signatures</c>.
    /// </summary>
    private static uint256 FindSpliceTxId(SpliceLinkCutter wire, long from)
    {
        var recorded = wire.Snapshot().Where(m => m.Message.Sequence >= from).Select(m => m.Message).ToList();
        var fromSignatures = recorded.FirstOrDefault(m => m.Type == (ushort)MessageTypes.TxSignatures)
                                    ?.TxSignaturesTxId;
        var fromCommitment = recorded.FirstOrDefault(m => m.Type == (ushort)MessageTypes.CommitmentSigned
                                                       && m.CommitmentFundingTxId is not null)
                                    ?.CommitmentFundingTxId;
        return fromCommitment ?? fromSignatures
            ?? throw new Xunit.Sdk.XunitException("no splice commitment_signed or tx_signatures recorded");
    }

    /// <summary>What our node had sent and processed when the link was cut.</summary>
    private static void AssertStateAtCut(SpliceLinkCutter wire, long from, CutPoint cut)
    {
        var processed = wire.Snapshot().Where(m => m.Message.Sequence >= from && !m.Dropped).Select(m => m.Message)
                            .ToList();
        bool Has(bool inbound, MessageTypes type) =>
            processed.Any(m => m.Inbound == inbound && m.Type == (ushort)type);

        Assert.DoesNotContain(processed, m => m.Type == (ushort)MessageTypes.TxAbort);
        switch (cut)
        {
            case CutPoint.AfterOurCommitmentSigned:
                Assert.True(Has(false, MessageTypes.CommitmentSigned), "our splice commitment_signed was not sent");
                Assert.False(Has(true, MessageTypes.CommitmentSigned), "CLN's commitment_signed was processed");
                Assert.False(Has(false, MessageTypes.TxSignatures), "we sent tx_signatures");
                break;
            case CutPoint.AfterClnCommitmentSigned:
                Assert.True(Has(true, MessageTypes.CommitmentSigned), "CLN's commitment_signed was not processed");
                Assert.False(Has(true, MessageTypes.TxSignatures), "CLN's tx_signatures was processed");
                Assert.False(Has(false, MessageTypes.TxSignatures), "we sent tx_signatures before CLN (we sign second)");
                break;
            case CutPoint.AfterOurTxSignatures:
                Assert.True(Has(false, MessageTypes.TxSignatures), "we did not send tx_signatures (we sign first)");
                Assert.False(Has(true, MessageTypes.TxSignatures), "CLN's tx_signatures was processed");
                break;
            case CutPoint.AfterBothTxSignatures:
                Assert.True(Has(false, MessageTypes.TxSignatures) && Has(true, MessageTypes.TxSignatures),
                            "both tx_signatures were not exchanged");
                break;
        }
    }

    #endregion

    #region Lock

    /// <summary>
    /// Mines one block at a time (no HTLC in flight) until both ends locked the splice, then checks it with
    /// <see cref="AssertLockedAsync"/>.
    /// </summary>
    private async Task<LockedChannel> MineUntilLockedAsync(SpliceChannel channel, ChannelSnapshot before,
                                                           uint256 spliceTxId, CancellationToken ct)
    {
        for (var block = 1; !await IsLockedOnBothEndsAsync(channel, spliceTxId, ct); block++)
        {
            Assert.True(block <= MaxLockBlocks, $"the splice was not locked by both ends in {MaxLockBlocks} blocks: "
                                              + await channel.DescribeAsync(ct));
            await channel.MineAsync(1, ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return await AssertLockedAsync(channel, before, spliceTxId, ct);
    }

    private static async Task<bool> IsLockedOnBothEndsAsync(SpliceChannel channel, uint256 spliceTxId,
                                                            CancellationToken ct)
    {
        var cln = await channel.GetClnChannelAsync(ct);
        var ours = await channel.GetOurChannelAsync(ct);
        Console.WriteLine($"[proof] ours {ours.Describe()} funding "
                        + $"{(ours.FundingTxId is { } f ? new uint256((byte[])f) : null)}; cln {DescribeCln(cln)}");
        return cln["state"]?.GetValue<string>() == "CHANNELD_NORMAL"
            && cln["funding_txid"]?.GetValue<string>() == spliceTxId.ToString()
            && ours.FundingTxId is { } funding && new uint256((byte[])funding) == spliceTxId;
    }

    /// <summary>
    /// Both ends locked the splice: usable, the same new funding, capacity, balance and short channel id (not the old
    /// one: the SCID switch), and our <c>listchannels</c> (SP2-D-T1) lists the splice as the current funding with its
    /// depth and short channel id, the replaced funding, no pending one, and the old short channel id retired for 72
    /// blocks (D12).
    /// </summary>
    private static async Task<LockedChannel> AssertLockedAsync(SpliceChannel channel, ChannelSnapshot before,
                                                               uint256 spliceTxId, CancellationToken ct)
    {
        await channel.WaitUsableAsync(ct, requireNoHtlcs: true);
        var ours = await channel.GetOurChannelAsync(ct);
        var cln = await channel.GetClnChannelAsync(ct);
        Assert.Equal(cln["total_msat"]!.GetValue<long>(), ours.Capacity.Satoshi * 1_000);
        Assert.Equal(cln["to_us_msat"]!.GetValue<long>(), (long)ours.RemoteBalance.MilliSatoshi);
        Assert.NotNull(ours.ShortChannelId);
        var clnScid = cln["short_channel_id"]!.GetValue<string>();
        Assert.NotEqual(before.ShortChannelId, clnScid);
        var newScid = ParseScid(clnScid);
        Assert.Equal(newScid, ours.ShortChannelId.Value.ToUInt64());

        Console.WriteLine("[proof] listchannels fundings: "
                        + string.Join("; ", ours.Fundings.Select(f => $"{f.Status}/{f.Kind} "
                                                                     + $"{new uint256((byte[])f.FundingTxId)} "
                                                                     + $"depth {f.Depth} scid {f.ShortChannelId}"))
                        + "; retired: "
                        + string.Join("; ", ours.RetiredShortChannelIds.Select(r => $"{r.ShortChannelId} "
                                                                                   + $"{r.RetiredAtHeight}.."
                                                                                   + $"{r.ExpiresAtHeight}")));
        var current = ours.Fundings[0];
        Assert.Equal(ChannelFundingStatus.Current, current.Status);
        Assert.Equal(ChannelFundingKind.Splice, current.Kind);
        Assert.Equal(spliceTxId, new uint256((byte[])current.FundingTxId));
        Assert.Equal(ours.Capacity, current.Capacity);
        Assert.Equal(newScid, current.ShortChannelId?.ToUInt64());
        Assert.True(current.Depth >= 1, $"the current funding's depth is {current.Depth}");
        Assert.True(current.SpliceLockedSent, "our splice_locked is not recorded");
        Assert.True(current.SpliceLockedReceived, "CLN's splice_locked is not recorded");
        Assert.DoesNotContain(ours.Fundings, f => f.Status == ChannelFundingStatus.Pending);
        var retired = Assert.Single(ours.RetiredShortChannelIds);
        Assert.Equal(ParseScid(before.ShortChannelId), retired.ShortChannelId.ToUInt64());
        Assert.Equal(72U, retired.ExpiresAtHeight - retired.RetiredAtHeight);
        var replaced = Assert.Single(ours.Fundings, f => f.Status == ChannelFundingStatus.Replaced);
        Assert.Equal(before.FundingTxId, new uint256((byte[])replaced.FundingTxId));
        Assert.Equal(retired.ShortChannelId, replaced.ShortChannelId);

        return new LockedChannel((ulong)ours.Capacity.Satoshi, newScid);
    }

    /// <summary>Polls <paramref name="condition"/> once a second; false when it still fails after the timeout.</summary>
    private static async Task<bool> PollAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return true;
    }

    /// <summary>The splice transaction reached bitcoind (mempool or a block).</summary>
    private async Task WaitBroadcastAsync(uint256 spliceTxId, CancellationToken ct) =>
        await Poll.UntilAsync(async () =>
        {
            try
            {
                await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(spliceTxId, true, ct);
                return true;
            }
            catch (RPCException)
            {
                return false;
            }
        }, s_stepTimeout, $"the splice {spliceTxId} broadcast", ct);

    /// <summary>
    /// CLN lists the splice as its inflight (<c>CHANNELD_AWAITING_SPLICE</c>), or already locked on it.
    /// </summary>
    private static async Task AssertClnPendingOrLockedAsync(SpliceChannel channel, uint256 spliceTxId,
                                                            CancellationToken ct)
    {
        var cln = await Poll.ForAsync(async () =>
        {
            var c = await channel.GetClnChannelAsync(ct);
            var inflight = c["inflight"]?.AsArray()
                                        .Any(i => i?["funding_txid"]?.GetValue<string>() == spliceTxId.ToString());
            return inflight == true || c["funding_txid"]?.GetValue<string>() == spliceTxId.ToString() ? c : null;
        }, s_stepTimeout, "CLN's inflight splice", ct);
        Console.WriteLine($"[cln] after the reconnection: {DescribeCln(cln)} inflight {cln["inflight"]?.ToJsonString()}");
    }

    #endregion

    #region Gossip and forwards

    private async Task MineUntilAsync(SpliceChannel channel, Func<Task<bool>> condition, int maxBlocks, string what,
                                      CancellationToken ct)
    {
        for (var block = 0; !await condition(); block++)
        {
            Assert.True(block < maxBlocks, $"{what}: not after {maxBlocks} blocks");
            await channel.MineAsync(1, ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    private static async Task<bool> ClnListsBothDirectionsAsync(SpliceChannel channel, ulong scid,
                                                               CancellationToken ct) =>
        await ClnDirectionsAsync(channel, scid, ct) == 2;

    private static async Task<bool> ClnListsAnyDirectionAsync(SpliceChannel channel, ulong scid, CancellationToken ct) =>
        await ClnDirectionsAsync(channel, scid, ct) > 0;

    private static async Task<int> ClnDirectionsAsync(SpliceChannel channel, ulong scid, CancellationToken ct)
    {
        var channels = (await channel.Cln.Client.CallAsync("listchannels", ct,
                                                           ("short_channel_id", ClnGossipTests.ClnScid(scid))))
                      ["channels"]!.AsArray();
        Console.WriteLine($"[cln] listchannels {ClnGossipTests.ClnScid(scid)}: {channels.Count} direction(s)");
        return channels.Count;
    }

    /// <summary>
    /// CLN pays its own invoice over a circular route: to us over <paramref name="clnFirstHopScid"/> (CLN's short
    /// channel id of the channel), then back to CLN with our hop told to forward to <paramref name="ourOutgoingScid"/>,
    /// at our announced fee and CLTV delta. With <paramref name="expectFailure"/> null the payment must complete and our
    /// node must have forwarded it; else it must fail with that failure name from our node.
    /// </summary>
    private static async Task ClnPaysItselfThroughUsAsync(SpliceChannel channel, ulong clnFirstHopScid,
                                                          ulong ourOutgoingScid, long amountMsat,
                                                          string? expectFailure, CancellationToken ct)
    {
        var cln = channel.Cln.Client;
        var label = $"nltg-sp2-circular-{Guid.NewGuid():N}";
        var invoice = await cln.CallAsync("invoice", ct, ("amount_msat", amountMsat), ("label", label),
                                          ("description", "circular through nltg"));
        var decoded = await cln.CallAsync("decode", ct, ("string", invoice["bolt11"]!.GetValue<string>()));
        var finalDelay = decoded["min_final_cltv_expiry"]!.GetValue<long>() + 6;
        var ours = await channel.GetOurChannelAsync(ct);
        var fee = ours.FeeBaseMsat + amountMsat * ours.FeePpm / 1_000_000;
        var route = new JsonArray(
            new JsonObject
            {
                ["id"] = channel.Node.NodeIdHex,
                ["channel"] = ClnGossipTests.ClnScid(clnFirstHopScid),
                ["amount_msat"] = amountMsat + fee,
                ["delay"] = finalDelay + ours.CltvExpiryDelta
            },
            new JsonObject
            {
                ["id"] = channel.Cln.NodeId,
                ["channel"] = ClnGossipTests.ClnScid(ourOutgoingScid),
                ["amount_msat"] = amountMsat,
                ["delay"] = finalDelay
            });
        var paymentHash = invoice["payment_hash"]!.GetValue<string>();
        await cln.CallAsync("sendpay", ct, ("route", route), ("payment_hash", paymentHash),
                            ("payment_secret", invoice["payment_secret"]!.GetValue<string>()),
                            ("amount_msat", amountMsat));
        try
        {
            var result = await cln.CallAsync("waitsendpay", ct, ("payment_hash", paymentHash), ("timeout", 60));
            Console.WriteLine($"[cln] circular payment of {amountMsat} msat to {ClnGossipTests.ClnScid(ourOutgoingScid)}: "
                            + result["status"]);
            Assert.Null(expectFailure);
            Assert.Equal("complete", result["status"]!.GetValue<string>());
            var preimage = Convert.FromHexString(result["payment_preimage"]!.GetValue<string>());
            Assert.Equal(paymentHash, Convert.ToHexStringLower(SHA256.HashData(preimage)));
        }
        catch (ClnRpcException e)
        {
            Console.WriteLine($"[cln] circular payment of {amountMsat} msat to {ClnGossipTests.ClnScid(ourOutgoingScid)} "
                            + $"failed: {e.Message}");
            Assert.NotNull(expectFailure);
            Assert.Contains(expectFailure, e.Message, StringComparison.Ordinal);
        }

        await WaitNoHtlcsAsync(channel, ct);
    }

    private static ulong AnnouncementScid(SpliceWireMessage message) =>
        // announcement_signatures: type (2), channel_id (32), short_channel_id (8)
        BinaryPrimitives.ReadUInt64BigEndian(message.Wire.AsSpan(2 + 32, 8));

    #endregion

    #region Payments and helpers

    private static async Task<ChannelSnapshot> SnapshotAsync(SpliceChannel channel, CancellationToken ct)
    {
        await channel.WaitUsableAsync(ct, requireNoHtlcs: true);
        var ours = await channel.GetOurChannelAsync(ct);
        var cln = await channel.GetClnChannelAsync(ct);
        Assert.NotNull(ours.FundingTxId);
        Assert.NotNull(ours.FundingOutputIndex);
        var funding = new uint256((byte[])ours.FundingTxId.Value);
        Assert.Equal(funding.ToString(), cln["funding_txid"]!.GetValue<string>());
        // Before any splice our listchannels lists the initial funding alone (SP2-D-T1)
        var initial = Assert.Single(ours.Fundings);
        Assert.Equal(ChannelFundingStatus.Current, initial.Status);
        Assert.Equal(ChannelFundingKind.Initial, initial.Kind);
        Assert.Equal(funding, new uint256((byte[])initial.FundingTxId));
        Assert.Empty(ours.RetiredShortChannelIds);
        return new ChannelSnapshot(funding, ours.FundingOutputIndex.Value, (ulong)ours.Capacity.Satoshi,
                                   cln["short_channel_id"]!.GetValue<string>());
    }

    private static ulong ParseScid(string scid)
    {
        var parts = scid.Split('x').Select(ulong.Parse).ToArray();
        return (parts[0] << 40) | (parts[1] << 16) | parts[2];
    }

    private static string DescribeCln(JsonNode channel) => ClnChannelSession.DescribeCln(channel);

    /// <summary>No <c>tx_abort</c>, <c>warning</c> or <c>error</c> either way since <paramref name="from"/>.</summary>
    private static void AssertNoAbortOrError(SpliceLinkCutter wire, long from)
    {
        var bad = wire.Snapshot().Where(m => m.Message.Sequence >= from
                                          && m.Message.Type is (ushort)MessageTypes.TxAbort
                                                            or ClnSpliceTests.SpliceWireRecorder.WarningType
                                                            or ClnSpliceTests.SpliceWireRecorder.ErrorType)
                      .ToList();
        Assert.True(bad.Count == 0,
                    "tx_abort/warning/error on the wire: "
                  + string.Join("; ", bad.Select(m => $"{(m.Message.Inbound ? "received" : "sent")} "
                                                    + $"{m.Message.Type} {m.Message.Hex}")));
    }

    private static async Task AssertWePayClnAsync(SpliceChannel channel, LightningMoney amount, CancellationToken ct)
    {
        var label = $"nltg-sp2-pays-{Guid.NewGuid():N}";
        var invoice = await channel.Cln.Client.CallAsync("invoice", ct, ("amount_msat", (long)amount.MilliSatoshi),
                                                         ("label", label), ("description", "nltg pays cln"));
        var payment = await channel.Node.PayInvoiceAsync(invoice["bolt11"]!.GetValue<string>(), ct);
        Console.WriteLine($"[cln] our payment of {amount.Satoshi} sat: {payment.Status}, failure "
                        + $"{payment.FailureCode}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        await WaitNoHtlcsAsync(channel, ct);
    }

    private static async Task AssertClnPaysUsAsync(SpliceChannel channel, LightningMoney amount, CancellationToken ct)
    {
        var invoice = await channel.Node.CreateInvoiceAsync(amount, $"cln pays nltg {Guid.NewGuid():N}", ct);
        var result = await channel.Cln.Client.CallAsync("pay", ct, ("bolt11", invoice.Bolt11!), ("retry_for", 30));
        Assert.Equal("complete", result["status"]!.GetValue<string>());
        await Poll.UntilAsync(async () => (await channel.Node.GetInvoiceAsync(invoice.PaymentHash, ct))?.Status
                                       == InvoiceStatus.Settled, s_settleTimeout, "our invoice settled", ct);
        await WaitNoHtlcsAsync(channel, ct);
    }

    private static Task WaitNoHtlcsAsync(SpliceChannel channel, CancellationToken ct) =>
        Poll.UntilAsync(async () =>
        {
            var ours = await channel.GetOurChannelAsync(ct);
            var cln = await channel.GetClnChannelAsync(ct);
            return ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 } && cln["htlcs"]?.AsArray().Count == 0;
        }, s_settleTimeout, "no HTLC left on either end", ct, TimeSpan.FromMilliseconds(250));

    /// <summary>Sends <paramref name="amount"/> to a new wallet address of <paramref name="cln"/> and confirms it.</summary>
    private async Task FundClnAsync(ClnPeer cln, LightningMoney amount, IEnumerable<NLightningTestNode> nodes,
                                    CancellationToken ct)
    {
        if (!cln.IsDedicated)
        {
            await _fixture.FundClnWalletAsync(amount, nodes, ct);
            return;
        }

        var address = (await cln.Client.CallAsync("newaddr", ct, ("addresstype", "bech32")))["bech32"]!
           .GetValue<string>();
        var txId = await _fixture.Bitcoin.Rpc.SendToAddressAsync(BitcoinAddress.Create(address, NBitcoin.Network.RegTest),
                                                                 Money.Satoshis((long)amount.Satoshi),
                                                                 cancellationToken: ct);
        await _fixture.MineAsync(6, ct);
        await Poll.UntilAsync(async () =>
        {
            var outputs = (await cln.Client.CallAsync("listfunds", ct))["outputs"]!.AsArray();
            return outputs.Any(o => o?["txid"]?.GetValue<string>() == txId.ToString()
                                 && o["status"]?.GetValue<string>() == "confirmed");
        }, s_settleTimeout, $"{DedicatedClnName} sees its deposit confirmed", ct);
    }

    private static async Task<string> GetClnLogForUsAsync(SpliceChannel channel, params string[] fragments)
    {
        try
        {
            var result = await channel.Cln.Client.CallAsync("getlog", CancellationToken.None, ("level", "debug"));
            var lines = result["log"]!.AsArray()
                                      .Where(e => e?["node_id"]?.GetValue<string>() == channel.Node.NodeIdHex
                                               || e?["source"]?.ToString().Contains(channel.Node.NodeIdHex[..8],
                                                      StringComparison.Ordinal) == true)
                                      .Select(e => $"{e?["time"]} {e?["source"]}: {e?["log"]}")
                                      .Where(l => fragments.Any(f => l.Contains(f, StringComparison.Ordinal)))
                                      .TakeLast(150);
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception e)
        {
            return $"(getlog failed: {e.Message})";
        }
    }

    #endregion

    #region Types

    /// <summary>A CLN our node has a channel with: the fixture's, or the one this class starts.</summary>
    private sealed record ClnPeer(ClnClient Client, string NodeId, Func<string> Address, bool IsDedicated)
    {
        public CompactPubKey PubKey => Convert.FromHexString(NodeId);
    }

    /// <summary>The channel before the splice.</summary>
    private sealed record ChannelSnapshot(uint256 FundingTxId, uint FundingOutputIndex, ulong CapacitySat,
                                          string ShortChannelId);

    /// <summary>The channel once both ends locked the splice.</summary>
    private sealed record LockedChannel(ulong CapacitySat, ulong ShortChannelId);

    /// <summary>The funding TLVs of a recorded <c>channel_reestablish</c> (BOLT 2: types 1 and 5).</summary>
    private sealed record ReestablishTlvs(uint256? NextFundingTxId, bool NextFundingBit0,
                                          uint256? CurrentFundingLockedTxId, bool CurrentFundingLockedBit0)
    {
        // type (2), channel_id (32), next_commitment_number (8), next_revocation_number (8),
        // your_last_per_commitment_secret (32), my_current_per_commitment_point (33)
        private const int TlvOffset = 2 + 32 + 8 + 8 + 32 + 33;

        public static ReestablishTlvs Read(SpliceWireMessage message)
        {
            var wire = message.Wire;
            byte[]? next = null, locked = null;
            var offset = TlvOffset;
            while (offset < wire.Length)
            {
                var type = ReadBigSize(wire, ref offset);
                var length = (int)ReadBigSize(wire, ref offset);
                var value = wire.AsSpan(offset, length).ToArray();
                offset += length;
                if (type == 1)
                    next = value;
                else if (type == 5)
                    locked = value;
            }

            return new ReestablishTlvs(next is { Length: >= 32 } ? new uint256(next.AsSpan(0, 32).ToArray()) : null,
                                       next is { Length: > 32 } && (next[32] & 1) != 0,
                                       locked is { Length: >= 32 } ? new uint256(locked.AsSpan(0, 32).ToArray()) : null,
                                       locked is { Length: > 32 } && (locked[32] & 1) != 0);
        }

        public override string ToString() =>
            $"next_funding {NextFundingTxId?.ToString() ?? "-"} (bit 0 {NextFundingBit0}), "
          + $"my_current_funding_locked {CurrentFundingLockedTxId?.ToString() ?? "-"} (bit 0 {CurrentFundingLockedBit0})";

        private static ulong ReadBigSize(byte[] wire, ref int offset)
        {
            var first = wire[offset++];
            ulong value;
            switch (first)
            {
                case 0xfd:
                    value = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(offset, 2));
                    offset += 2;
                    break;
                case 0xfe:
                    value = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(offset, 4));
                    offset += 4;
                    break;
                case 0xff:
                    value = BinaryPrimitives.ReadUInt64BigEndian(wire.AsSpan(offset, 8));
                    offset += 8;
                    break;
                default:
                    value = first;
                    break;
            }

            return value;
        }
    }

    /// <summary>
    /// Our node, its channel to a CLN, the link cutter; restartable on the same key and database.
    /// </summary>
    private sealed class SpliceChannel(
        ClnSpliceReestablishTests owner,
        NLightningTestNode node,
        ClnPeer cln,
        SpliceLinkCutter cutter) : IAsyncDisposable
    {
        public NLightningTestNode Node { get; } = node;
        public ClnPeer Cln { get; } = cln;
        public SpliceLinkCutter Cutter { get; } = cutter;
        public ChannelId ChannelId { get; set; }
        public string ChannelIdHex => ChannelId.ToString();

        public Task StartNodeAsync(CancellationToken ct) => Node.StartAsync(ct);

        public Task StopNodeAsync() => Node.StopAsync();

        public async Task ConnectAsync(CancellationToken ct)
        {
            await Node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(Cln.Address())).WaitAsync(ct);
            await Poll.UntilAsync(async () => Node.IsConnectedTo(Cln.PubKey)
                                           && await Cln.Client.IsConnectedAsync(Node.NodeIdHex, ct),
                                  TimeSpan.FromSeconds(30), $"{Node.Name} and CLN connected", ct);
        }

        /// <summary>
        /// After a cut: our node dials CLN (a restarted CLN does not dial us; our own backoff may win the race) until
        /// the channel is reestablished on both ends.
        /// </summary>
        public async Task ReconnectAsync(CancellationToken ct)
        {
            await Poll.UntilAsync(async () =>
            {
                if (!Node.IsConnectedTo(Cln.PubKey))
                {
                    try
                    {
                        await Node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(Cln.Address()))
                                  .WaitAsync(TimeSpan.FromSeconds(10), ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        Console.WriteLine($"[proof] reconnect attempt failed: {e.Message}");
                        return false;
                    }
                }

                var ours = await GetOurChannelAsync(ct);
                var theirs = await Cln.Client.GetPeerChannelAsync(Node.NodeIdHex, ChannelIdHex, ct);
                return ours.IsReestablished && ours.IsPeerConnected
                    && theirs?["peer_connected"]?.GetValue<bool>() == true;
            }, s_reconnectTimeout, "the channel reestablished after the cut", ct, TimeSpan.FromSeconds(1));
        }

        public Task<ChannelInfoClientResponse> GetOurChannelAsync(CancellationToken ct) =>
            Node.GetChannelAsync(ChannelId, ct);

        public async Task<JsonNode> GetClnChannelAsync(CancellationToken ct) =>
            await Cln.Client.GetPeerChannelAsync(Node.NodeIdHex, ChannelIdHex, ct)
         ?? throw new InvalidOperationException($"CLN does not list the channel {ChannelIdHex}");

        public async Task<bool> IsUsableAsync(bool requireNoHtlcs, CancellationToken ct)
        {
            if (!Node.IsRunning)
                return false;

            var ours = (await Node.ListChannelsAsync(ct)).Channels.FirstOrDefault(c => c.ChannelId == ChannelId);
            var theirs = await Cln.Client.GetPeerChannelAsync(Node.NodeIdHex, ChannelIdHex, ct);
            var ready = ours is not null && ours.IsUsable() && ours.ShortChannelId is not null
                     && theirs?["state"]?.GetValue<string>() == "CHANNELD_NORMAL"
                     && theirs["peer_connected"]?.GetValue<bool>() == true;
            if (requireNoHtlcs)
                ready &= ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 } && theirs?["htlcs"]?.AsArray().Count == 0;
            return ready;
        }

        public Task WaitUsableAsync(CancellationToken ct, bool requireNoHtlcs) =>
            Poll.UntilAsync(() => IsUsableAsync(requireNoHtlcs, ct), ClnChannelSession.UsableTimeout,
                            "the channel usable on both ends", ct, TimeSpan.FromMilliseconds(500));

        /// <summary>Mines on the fixture's bitcoind and waits until both CLNs and our node are at the tip.</summary>
        public async Task MineAsync(int blocks, CancellationToken ct)
        {
            await owner._fixture.MineAsync(blocks, ct);
            await WaitAtTipAsync(ct);
        }

        public async Task WaitAtTipAsync(CancellationToken ct)
        {
            await owner._fixture.WaitAllAtTipAsync(Node.IsRunning ? [Node] : [], ct);
            if (!Cln.IsDedicated)
                return;

            var tip = await owner._fixture.Bitcoin.Rpc.GetBlockCountAsync(ct);
            await Poll.UntilAsync(async () => (await Cln.Client.GetInfoAsync(ct))["blockheight"]!.GetValue<long>()
                                           >= tip, s_settleTimeout, $"{DedicatedClnName} at the tip", ct);
        }

        public async Task<string> DescribeAsync(CancellationToken ct)
        {
            var ours = Node.IsRunning
                           ? (await Node.ListChannelsAsync(ct)).Channels.FirstOrDefault(c => c.ChannelId == ChannelId)
                                                              ?.Describe() ?? "not listed"
                           : "node stopped";
            string theirs;
            try
            {
                theirs = await Cln.Client.GetPeerChannelAsync(Node.NodeIdHex, ChannelIdHex, ct) is { } c
                             ? DescribeCln(c)
                             : "not listed";
            }
            catch (Exception e)
            {
                theirs = $"unavailable: {e.Message}";
            }

            return $"ours {ours}; cln {theirs}";
        }

        public ValueTask DisposeAsync() => Node.DisposeAsync();
    }

    /// <summary>A recorded message and whether the cutter dropped it before our node processed it.</summary>
    internal sealed record CutWireMessage(SpliceWireMessage Message, bool Dropped);

    /// <summary>
    /// Records our node's transport traffic in one order for both directions (the
    /// <see cref="ClnSpliceTests.SpliceWireRecorder"/> pattern: the node's <see cref="ITransportServiceFactory"/> and
    /// <see cref="IMessageServiceFactory"/> over a recording decorator of its <see cref="IMessageSerializer"/>) and wraps
    /// its <see cref="ITcpService"/>, so a test can cut the link at a message: an armed cut drops the first inbound
    /// message of a type (the node never sees it), resets every socket (TCP RST, no final message) and refuses every new
    /// connection until <see cref="Heal"/>. Everything the node reads while cut is dropped too. The cutter outlives the
    /// node's restarts (its decorators are registered again on every start).
    /// </summary>
    internal sealed class SpliceLinkCutter
    {
        /// <summary>
        /// How long the sockets stay open after an armed cut dropped its message: everything inbound is already
        /// dropped, but what the node sends in that window (e.g. its own <c>commitment_signed</c>, raced by the peer's)
        /// still goes out, so the cut lands after the node's reaction to what it did receive.
        /// </summary>
        private static readonly TimeSpan s_resetGrace = TimeSpan.FromSeconds(2);

        private readonly ConcurrentQueue<CutWireMessage> _traffic = new();
        private readonly Lock _lock = new();
        private long _sequence;
        private (ushort Type, long From, TaskCompletionSource<SpliceWireMessage> Done)? _armed;
        private volatile bool _severed;
        private volatile bool _reset;
        private CuttableTcpService? _tcp;

        public IReadOnlyList<CutWireMessage> Snapshot() => _traffic.ToArray();

        public long CurrentSequence
        {
            get
            {
                lock (_lock)
                    return _sequence;
            }
        }

        public bool IsSevered => _severed;

        /// <summary>The first recorded message (dropped or not) of <paramref name="type"/> from a sequence.</summary>
        public SpliceWireMessage? FirstOrDefault(bool inbound, MessageTypes type, long fromSequence = 0) =>
            _traffic.Select(m => m.Message)
                    .FirstOrDefault(m => m.Inbound == inbound && m.Type == (ushort)type && m.Sequence >= fromSequence);

        /// <summary>
        /// Arms the cut: the first inbound <paramref name="type"/> recorded at or after <paramref name="fromSequence"/>
        /// is dropped, every later inbound message too, and the sockets are reset after a short grace; the task
        /// completes with the dropped message once they are.
        /// </summary>
        public Task<SpliceWireMessage> ArmInboundCut(MessageTypes type, long fromSequence)
        {
            var done = new TaskCompletionSource<SpliceWireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
                _armed = ((ushort)type, fromSequence, done);
            return done.Task;
        }

        /// <summary>Cuts the link now.</summary>
        public void CutNow()
        {
            _severed = true;
            _reset = true;
            _tcp?.ResetAll();
        }

        /// <summary>Lets connections through again.</summary>
        public void Heal()
        {
            _severed = false;
            _reset = false;
        }

        public string Describe() =>
            string.Join(" ", Snapshot().Where(m => m.Message.Type is not ((ushort)MessageTypes.Ping
                                                                           or (ushort)MessageTypes.Pong))
                                       .Select(m => $"{(m.Message.Inbound ? "<" : ">")}"
                                                  + $"{(MessageTypes)m.Message.Type}{(m.Dropped ? "(dropped)" : "")}"));

        public void Install(IServiceCollection services)
        {
            services.AddKeyedSingleton<IMessageSerializer>(this, (sp, _) =>
                                                               new CuttingMessageSerializer(
                                                                   sp.GetRequiredService<IMessageSerializer>(), this));
            services.AddSingleton<ITransportServiceFactory>(sp =>
                                                                new TransportServiceFactory(
                                                                    sp.GetRequiredService<IEcdh>(),
                                                                    sp.GetRequiredService<ILoggerFactory>(),
                                                                    sp.GetRequiredKeyedService<IMessageSerializer>(
                                                                        this),
                                                                    sp.GetRequiredService<IOptions<NodeOptions>>()));
            services.AddSingleton<IMessageServiceFactory>(sp =>
                                                              new MessageServiceFactory(
                                                                  sp.GetRequiredKeyedService<IMessageSerializer>(
                                                                      this),
                                                                  sp.GetRequiredService<ILoggerFactory>()));

            // Wrap the node's own ITcpService (the crashable one of NLightningTestNode)
            var previous = services.Last(d => d.ServiceType == typeof(ITcpService));
            services.AddSingleton<ITcpService>(sp =>
            {
                var inner = previous.ImplementationFactory is { } factory
                                ? (ITcpService)factory(sp)
                                : previous.ImplementationInstance as ITcpService
                               ?? (ITcpService)ActivatorUtilities.CreateInstance(sp, previous.ImplementationType!);
                var tcp = new CuttableTcpService(inner, this);
                _tcp = tcp;
                return tcp;
            });
        }

        /// <summary>Records an inbound message; true when it must be dropped (armed match, or the link is cut).</summary>
        private bool RecordInbound(byte[] wire)
        {
            if (wire.Length < 2)
                return false;

            var type = BinaryPrimitives.ReadUInt16BigEndian(wire);
            TaskCompletionSource<SpliceWireMessage>? fired = null;
            SpliceWireMessage message;
            bool drop;
            lock (_lock)
            {
                message = new SpliceWireMessage(_sequence++, type, wire, true);
                drop = _severed;
                if (_armed is { } armed && armed.Type == type && message.Sequence >= armed.From)
                {
                    drop = true;
                    fired = armed.Done;
                    _armed = null;
                    _severed = true;
                }

                _traffic.Enqueue(new CutWireMessage(message, drop));
            }

            if (fired is not null)
            {
                // Not on the read loop: the node keeps sending during the grace
                _ = Task.Run(async () =>
                {
                    await Task.Delay(s_resetGrace);
                    _reset = true;
                    _tcp?.ResetAll();
                    fired.TrySetResult(message);
                });
            }

            return drop;
        }

        private void RecordOutbound(byte[] wire)
        {
            if (wire.Length < 2)
                return;

            var type = BinaryPrimitives.ReadUInt16BigEndian(wire);
            lock (_lock)
                _traffic.Enqueue(new CutWireMessage(new SpliceWireMessage(_sequence++, type, wire, false), _reset));
        }

        private sealed class CuttingMessageSerializer(IMessageSerializer inner, SpliceLinkCutter cutter)
            : IMessageSerializer
        {
            public async Task SerializeAsync(IMessage message, Stream stream)
            {
                using var buffer = new MemoryStream();
                await inner.SerializeAsync(message, buffer);
                var wire = buffer.ToArray();
                cutter.RecordOutbound(wire);
                await stream.WriteAsync(wire);
            }

            public Task<TMessage?> DeserializeMessageAsync<TMessage>(Stream stream) where TMessage : class, IMessage =>
                inner.DeserializeMessageAsync<TMessage>(stream);

            public async Task<IMessage?> DeserializeMessageAsync(Stream stream)
            {
                var wire = new byte[stream.Length - stream.Position];
                await stream.ReadExactlyAsync(wire);
                // A dropped message is never handed to the node (MessageService ignores a null message)
                if (cutter.RecordInbound(wire))
                    return null;

                using var copy = new MemoryStream(wire, false);
                return await inner.DeserializeMessageAsync(copy);
            }
        }
    }

    /// <summary>
    /// The node's <see cref="ITcpService"/> with a link cut: <see cref="ResetAll"/> resets every socket handed out so
    /// far, and while the cutter is severed new connections are refused both ways (outbound attempts throw, inbound
    /// ones are reset before the node sees them). Listening goes on, unlike <c>CrashableTcpService.CrashAsync</c>.
    /// </summary>
    private sealed class CuttableTcpService : ITcpService
    {
        private readonly ITcpService _inner;
        private readonly SpliceLinkCutter _cutter;
        private readonly ConcurrentBag<TcpClient> _clients = [];

        public CuttableTcpService(ITcpService inner, SpliceLinkCutter cutter)
        {
            _inner = inner;
            _cutter = cutter;
            _inner.OnNewPeerConnected += HandleNewPeerConnected;
        }

        public List<EndPoint> ListeningTo => _inner.ListeningTo;

        public event EventHandler<NewPeerConnectedEventArgs>? OnNewPeerConnected;

        public Task StartListeningAsync(CancellationToken cancellationToken) =>
            _inner.StartListeningAsync(cancellationToken);

        public Task StopListeningAsync() => _inner.StopListeningAsync();

        public async Task<ConnectedPeer> ConnectToPeerAsync(PeerAddress peerAddress)
        {
            if (_cutter.IsSevered)
                throw new ConnectionException("The test cut the link");

            var connected = await _inner.ConnectToPeerAsync(peerAddress);
            _clients.Add(connected.TcpClient);
            if (_cutter.IsSevered)
            {
                Reset(connected.TcpClient);
                throw new ConnectionException("The test cut the link");
            }

            return connected;
        }

        public void ResetAll()
        {
            foreach (var client in _clients)
                Reset(client);
        }

        private void HandleNewPeerConnected(object? sender, NewPeerConnectedEventArgs args)
        {
            _clients.Add(args.TcpClient);
            if (_cutter.IsSevered)
            {
                Reset(args.TcpClient);
                return;
            }

            OnNewPeerConnected?.Invoke(this, args);
        }

        private static void Reset(TcpClient client)
        {
            try
            {
                client.Client.LingerState = new LingerOption(true, 0);
                client.Client.Close();
            }
            catch (Exception)
            {
                // Already closed
            }
        }
    }

    #endregion
}