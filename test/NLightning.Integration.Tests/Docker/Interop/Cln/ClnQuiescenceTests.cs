using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Abcd;
using Application.Channels.Interfaces;
using Application.InteractiveTx.Interfaces;
using Application.Payments.Switch;
using Domain.Channels.Interfaces;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Serialization.Interfaces;
using Fixtures;
using Infrastructure.Crypto.Interfaces;
using Infrastructure.Protocol.Factories;
using Infrastructure.Transport.Factories;
using Utils;

/// <summary>
/// Proof Q of the splicing plan (<c>docs/agents/SPLICING_PLAN.md</c> §5 "Wave Q", NL-042/NL-019): BOLT 2 "Channel
/// Quiescence" (<c>stfu</c>, <c>option_quiesce</c> 34/35) against Core Lightning v26.06.8, with our node on
/// <c>Features:AllowExperimentalFeatures</c> and <c>OptionQuiesce = Optional</c> (the feature stays experimental until
/// splicing ships, plan D2/D13).
/// </summary>
/// <remarks>
/// <para><b>What CLN v26.06.8 offers (checked in wave qit lane Q-C, 2026-09-27, on the pinned image and on two CLN
/// nodes with a channel between them; plan §10 questions Q1 and Q7):</b></para>
/// <list type="bullet">
/// <item><c>lightningd --list-features-only</c> lists <c>option_quiesce/odd</c> and <c>option_splice/odd</c> by default
/// (no <c>--experimental-splicing</c> needed).</item>
/// <item><c>stfu_channels channel_ids</c> and <c>abort_channels channel_ids</c> (the <c>spenderp</c> plugin) exist
/// <b>without</b> <c>--developer</c>; <c>dev-quiesce id</c> is developer-only (our fixture runs <c>--developer</c>,
/// but the proof does not need it). <c>stfu_channels</c> sends <c>stfu(initiator=1)</c>, waits for the peer's
/// <c>stfu</c> and returns (<c>{"channels":[{"channel_id","available_msat"}]}</c>) once the channel is quiescent; CLN
/// logs <c>STFU initiator local.</c>, <c>STFU complete: we are quiescent</c>.</item>
/// <item><c>abort_channels</c> ends a quiescence CLN started: CLN sends <c>tx_abort</c> ("requested by user"), waits
/// for the peer's <c>tx_abort</c> ack (<c>We got TX_ABORT ack</c>) and restarts its channeld <b>in place</b>
/// (<c>Restarting channeld after tx_abort on CHANNELD_NORMAL channel</c>): no disconnection and no
/// <c>channel_reestablish</c> on the wire; payments both ways work right after (Q7).</item>
/// <item>As the non-initiator, CLN answers a peer's <c>stfu(1)</c> with <c>stfu(0)</c> (<c>STFU initiator was
/// remote.</c>), and a <c>tx_abort</c> received while quiescent without any splice is acked with <c>tx_abort</c>
/// (<c>Send ack of tx_abort</c>) and CLN resumes normal operation the same way (Q1: yes, CLN resumes on
/// <c>tx_abort</c> without a splice).</item>
/// <item>A disconnection ends CLN's quiescence (Q-R-04): after <c>disconnect force=true</c> the channel reestablishes
/// normally. CLN applies no idle timeout of its own: an idle quiescent channel stayed connected for 120 s.</item>
/// <item>A CLN payment attempted while the channel is quiescent is held inside channeld and dies with
/// <c>temporary_channel_failure (Outgoing subdaemon died)</c> when <c>abort_channels</c> restarts channeld; xpay then
/// remembers the channel as unable to carry that amount. These tests therefore never let CLN pay while quiescent.</item>
/// <item>CLN has no hold invoices in the image (no plugin, no Python), so (c) is proven twice: our HTLC in flight
/// (<see cref="IQuiescenceService.RequestAsync"/> started the moment our <c>update_add_htlc</c> is written; only an
/// attempt whose request was seen queued before CLN revoked for the add counts, so the Q-S-02 wait is really
/// exercised), and CLN's HTLC held by us (one part of a <c>basic_mpp</c> payment sent with <c>sendpay</c>, which our
/// switch holds until the set is complete: committed but unresolved during the quiescence). Not covered on the wire:
/// a CLN add not yet revoked for at our request (Q1-T2: a received but unrevoked peer add does not block our
/// <c>stfu</c>), the Q-R-03 60 s timeout with HTLCs pending, and a CLN fulfill queued while quiescent; lanes Q-A and
/// Q-B cover those in unit and harness tests.</item>
/// </list>
/// <para>Each test funds its own private channel to CLN (<see cref="ClnChannelSession.BuildOurFundedAsync"/>) and
/// records our channel traffic with <see cref="QuiescenceWireRecorder"/> (both directions, in wire order). They need
/// lanes Q-A (the <c>stfu</c> route and rules) and Q-B (<c>QuiescenceService</c> in the node's DI, the update gate and
/// the <c>tx_abort</c> exit of a <see cref="QuiescencePurpose.Probe"/> or a CLN-initiated quiescence); the integrator
/// runs them after those lanes merge.</para>
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnQuiescenceTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 8 * 60 * 1_000;

    /// <summary>
    /// How many payments Proof Q (c), our side, may make before it gives up on queuing the request in time.
    /// </summary>
    private const int InFlightAttempts = 3;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);
    private static readonly TimeSpan s_quiescenceTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_settleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_heldSetTimeout = TimeSpan.FromMinutes(5);

    private readonly ClnFixture _fixture;
    private ClnChannelSession? _session;
    private QuiescenceWireRecorder? _wire;

    public ClnQuiescenceTests(ClnFixture fixture, ITestOutputHelper output)
    {
        fixture.SkipIfUnavailable(); // the fixture runs on the cluster only (NL-866)
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_wire is not null)
            Console.WriteLine($"[wire] {_wire.Describe()}");

        if (_session is not null)
        {
            Console.WriteLine("[cln] CLN quiescence log lines for our node:\n"
                            + await GetClnLogForUsAsync(_session, CancellationToken.None,
                                                        "STFU", "TX_ABORT", "tx_abort", "Restarting channeld",
                                                        "FULFILL", "fulfill", "HTLC", "htlc", "quiesc"));
            Console.WriteLine("[cln] CLN unusual/broken log lines so far:\n"
                            + await _fixture.Cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 60,
                                                                  "unusual"));
            if (TestDiagnostics.CurrentTestFailed)
                Console.WriteLine($"[cln] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");

            await _session.DisposeAsync();
        }
    }

    /// <summary>
    /// Proof Q (a): CLN initiates (<c>dev-quiesce</c>, see <see cref="ClnQuiesceAsync"/>); we reply <c>stfu(0)</c> and
    /// the channel is quiescent on both ends with CLN the initiator. CLN v26.06.8 cannot end it without splicing
    /// (<c>abort_channels</c> refuses a peer without <c>option_splice</c>), so we end it with <c>tx_abort</c> (plan D2:
    /// with no dependent protocol a quiescence ends with <c>tx_abort</c> or a disconnection); CLN acks with
    /// <c>tx_abort</c>, restarts its channeld in place, and the channel works again on the same connection (a payment
    /// both ways). Our echo of a peer's <c>tx_abort</c> is proven in-process (<c>InteractiveTxDriverTests</c>).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ClnInitiatesQuiescence_When_WeEndItWithTxAbort_Then_ClnAcksAndPaymentsFlowBothWays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-quiesce-a", ct);
        var quiescence = GetQuiescenceService(session);
        var reestablishesBefore = wire.Count(inbound: true, MessageTypes.ChannelReestablish);

        // Act: CLN asks for quiescence
        await ClnQuiesceAsync(session, ct);

        // Assert: quiescent on our side with CLN the initiator; CLN's stfu(1) came first, our stfu(0) replied
        var state = await Poll.ForAsync(() =>
                                        {
                                            var current = quiescence.GetState(session.ChannelId);
                                            return current.IsQuiescent ? current : null;
                                        }, s_quiescenceTimeout, "our channel quiescent", ct);
        Assert.Equal(QuiescenceInitiator.Remote, state.Initiator);
        Assert.True(state.SentStfuInitiator == false, "our stfu was not a reply (initiator = 0)");
        Assert.True(state.ReceivedStfuInitiator == true, "CLN's stfu did not carry initiator = 1");
        var theirStfu = wire.Single(inbound: true, MessageTypes.Stfu);
        var ourStfu = wire.Single(inbound: false, MessageTypes.Stfu);
        Assert.True(theirStfu.Sequence < ourStfu.Sequence, "we sent stfu before CLN's");
        Assert.True(theirStfu.StfuInitiator);
        Assert.False(ourStfu.StfuInitiator);
        Assert.True(theirStfu.HasChannelId(session.ChannelId) && ourStfu.HasChannelId(session.ChannelId));
        await AssertClnLogsAsync(session, ct, "STFU initiator local.", "STFU complete: we are quiescent");

        // Act: CLN v26.06.8 refuses abort_channels for a peer without option_splice (354), so the quiescence CLN
        // started is ended from our side the way a refused dependent protocol would be: our tx_abort (plan D2)
        await EndWithOurTxAbortAsync(session, ct);

        // Assert: CLN acked our tx_abort and restarted its channeld in place; quiescence ended on both sides
        var ourAbort = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxAbort),
                                           s_quiescenceTimeout, "our tx_abort", ct);
        var theirAbort = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxAbort),
                                             s_quiescenceTimeout, "CLN's tx_abort ack", ct);
        await AssertClnLogsAsync(session, ct, "Send ack of tx_abort", "Restarting channeld after tx_abort");
        Assert.True(ourStfu.Sequence < ourAbort.Sequence && ourAbort.Sequence < theirAbort.Sequence,
                    "tx_abort order: ours after quiescence, then CLN's ack");
        Assert.Equal(1, wire.Count(inbound: false, MessageTypes.TxAbort));
        Assert.True(ourAbort.HasChannelId(session.ChannelId));
        await Poll.UntilAsync(() => !quiescence.GetState(session.ChannelId).BlocksNewLocalUpdates,
                              s_quiescenceTimeout, "our quiescence ended by tx_abort", ct);
        AssertNoUpdateFromUsBetween(wire, ourStfu.Sequence, ourAbort.Sequence);

        // ...and the channel works again on the same connection
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        Assert.Equal(reestablishesBefore, wire.Count(inbound: true, MessageTypes.ChannelReestablish));
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(12_000), ct);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Proof Q (a), disconnection exit (Q-R-04): CLN initiates, then drops the connection while quiescent; after the
    /// reconnection and <c>channel_reestablish</c> the channel is no longer quiescent on our side and payments flow.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ClnInitiatedQuiescence_When_ClnDisconnects_Then_QuiescenceEndsAndPaymentsFlow()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-quiesce-a2", ct);
        var quiescence = GetQuiescenceService(session);
        await ClnQuiesceAsync(session, ct);
        await Poll.UntilAsync(() => quiescence.GetState(session.ChannelId).IsQuiescent, s_quiescenceTimeout,
                              "our channel quiescent", ct);
        var reestablishesBefore = wire.Count(inbound: true, MessageTypes.ChannelReestablish);

        // Act
        await session.Cln.CallAsync("disconnect", ct, ("id", session.Node.NodeIdHex), ("force", true));

        // Assert: a fresh connection and reestablish, no quiescence left, no tx_abort needed
        await Poll.UntilAsync(() => wire.Count(inbound: true, MessageTypes.ChannelReestablish) > reestablishesBefore,
                              ClnChannelSession.UsableTimeout, "CLN's channel_reestablish after the reconnection",
                              ct);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        Assert.False(quiescence.GetState(session.ChannelId).BlocksNewLocalUpdates);
        Assert.Equal(0, wire.Count(inbound: false, MessageTypes.TxAbort));
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(22_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(11_000), ct);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Proof Q (b): we initiate through <see cref="IQuiescenceService.RequestAsync"/> (purpose
    /// <see cref="QuiescencePurpose.Probe"/>); CLN replies <c>stfu(0)</c> and we are the initiator; we end it with
    /// <c>tx_abort</c> (plan D2), CLN acks with <c>tx_abort</c> and resumes; payments work both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_WeRequestQuiescence_When_WeEndItWithTxAbort_Then_ClnResumesAndPaymentsFlowBothWays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-quiesce-b", ct);
        var quiescence = GetQuiescenceService(session);
        var reestablishesBefore = wire.Count(inbound: true, MessageTypes.ChannelReestablish);

        // Act
        var initiator = await quiescence.RequestAsync(session.ChannelId, QuiescencePurpose.Probe, ct)
                                        .WaitAsync(s_quiescenceTimeout, ct);

        // Assert: our stfu(1), CLN's stfu(0), we are the initiator
        Assert.Equal(QuiescenceInitiator.Local, initiator);
        var ourStfu = wire.Single(inbound: false, MessageTypes.Stfu);
        var theirStfu = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.Stfu),
                                            s_quiescenceTimeout, "CLN's stfu", ct);
        Assert.True(ourStfu.Sequence < theirStfu.Sequence, "CLN sent stfu before ours");
        Assert.True(ourStfu.StfuInitiator);
        Assert.False(theirStfu.StfuInitiator);
        Assert.True(ourStfu.HasChannelId(session.ChannelId) && theirStfu.HasChannelId(session.ChannelId));
        await AssertClnLogsAsync(session, ct, "STFU initiator was remote.", "STFU complete: we are quiescent");

        // ...then our tx_abort ends it; CLN acks and restarts its channeld in place (Q1)
        var ourAbort = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxAbort),
                                           s_quiescenceTimeout, "our tx_abort ending the probe", ct);
        var theirAbort = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxAbort),
                                             s_quiescenceTimeout, "CLN's tx_abort ack", ct);
        Assert.True(theirStfu.Sequence < ourAbort.Sequence && ourAbort.Sequence < theirAbort.Sequence,
                    "tx_abort order: ours after quiescence, then CLN's ack");
        Assert.True(ourAbort.HasChannelId(session.ChannelId));
        Assert.Equal(1, wire.Count(inbound: false, MessageTypes.TxAbort));
        await AssertClnLogsAsync(session, ct, "Send ack of tx_abort", "Restarting channeld after tx_abort");
        await Poll.UntilAsync(() => !quiescence.GetState(session.ChannelId).BlocksNewLocalUpdates,
                              s_quiescenceTimeout, "our quiescence ended by tx_abort", ct);
        AssertNoUpdateFromUsBetween(wire, ourStfu.Sequence, ourAbort.Sequence);

        // ...and the channel works again on the same connection
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        Assert.Equal(reestablishesBefore, wire.Count(inbound: true, MessageTypes.ChannelReestablish));
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(23_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(13_000), ct);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Proof Q (c), our HTLC: our add is in flight when we ask for quiescence. The request is started the moment our
    /// <c>update_add_htlc</c> is written, and an attempt counts only when the request was seen queued
    /// (<see cref="QuiescenceState.PendingRequest"/> set, our <c>stfu</c> not sent) before CLN's
    /// <c>revoke_and_ack</c> for our <c>commitment_signed</c> reached us: our <c>stfu</c> then had to wait until the
    /// add was committed and revoked both ways (Q-S-02). An attempt whose request was queued later proves no wait and
    /// is retried (up to <see cref="InFlightAttempts"/>), never counted. Every attempt checks that we send no update
    /// after our <c>stfu</c> (Q-S-04) and that the payment completes once the probe ends with <c>tx_abort</c>.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurHtlcInFlight_When_WeRequestQuiescence_Then_OurStfuWaitsUntilItIsCommitted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-quiesce-c", ct);
        var quiescence = GetQuiescenceService(session);

        // Act: pay CLN and request quiescence while our add is pending, until the request was queued in time
        InFlightAttempt? exercised = null;
        for (var attempt = 1; attempt <= InFlightAttempts && exercised is null; attempt++)
        {
            var result = await RunOurHtlcInFlightAttemptAsync(session, wire, quiescence, attempt, ct);
            Console.WriteLine($"[proof] {result.Describe()}");
            if (result.WaitExercised)
                exercised = result;
        }

        // Assert: one attempt queued the request before CLN revoked for our add, so our stfu waited for it
        Assert.True(exercised is not null,
                    $"in {InFlightAttempts} attempts our request was never queued before CLN's revoke_and_ack for our "
                  + "add; the Q-S-02 wait was not exercised");
        Assert.True(exercised.OurAddSequence < exercised.QueuedAt, "the request was queued before our add");
        Assert.True(exercised.QueuedAt <= exercised.TheirRevokeSequence
                 && exercised.TheirRevokeSequence < exercised.OurStfuSequence,
                    "our stfu did not wait for CLN's revoke_and_ack of our add");
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(14_000), ct);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Proof Q (c), CLN's HTLC held by us (plan §5 "a held invoice on CLN"; the CLN image has no hold invoices, so our
    /// node holds instead): CLN sends one part of a two-part <c>basic_mpp</c> payment to our invoice with
    /// <c>sendpay</c>; our switch holds the incomplete set (<c>MppTimeout</c> 5 min here), so the HTLC is irrevocably
    /// committed on both commitments and unresolved. We request quiescence: our <c>stfu</c> goes out with it pending
    /// (Q-S-02 counts only uncommitted updates), we neither fulfill nor fail it while quiescing, the probe ends with
    /// <c>tx_abort</c>, and then CLN's second part completes the set: both parts are fulfilled and our invoice is
    /// settled for the total.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ClnHtlcHeldByUs_When_WeRequestQuiescence_Then_OurStfuGoesOutAndItSettlesAfterTxAbort()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-quiesce-c2", ct,
                                               services => services.PostConfigure<HtlcSwitchOptions>(
                                                   o => o.MppTimeout = s_heldSetTimeout));
        var quiescence = GetQuiescenceService(session);
        var total = LightningMoney.Satoshis(30_000);
        var firstPart = LightningMoney.Satoshis(18_000);
        var secondPart = LightningMoney.Satoshis(12_000);
        var invoice = await session.Node.CreateInvoiceAsync(total, $"quiesce held {Guid.NewGuid():N}", ct);
        var payment = await HeldPayment.CreateAsync(session, invoice, ct);
        var from = wire.CurrentSequence;

        // Act: CLN's first part; we hold the incomplete set once the add is irrevocably committed
        await payment.SendPartAsync(firstPart, partId: 1, ct);
        var theirAdd = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.UpdateAddHtlc, from),
                                           s_quiescenceTimeout, "CLN's update_add_htlc", ct);
        var committedAt = await Poll.ForAsync(() => IrrevocablyCommittedAt(wire.Snapshot(), theirAdd.Sequence),
                                              s_quiescenceTimeout, "CLN's add committed and revoked both ways", ct);
        await Poll.UntilAsync(async () => (await session.GetOurChannelAsync(ct)).ReceivedHtlcCount == 1,
                              s_quiescenceTimeout, "our channel lists CLN's HTLC", ct);
        var initiator = await quiescence.RequestAsync(session.ChannelId, QuiescencePurpose.Probe, ct)
                                        .WaitAsync(s_quiescenceTimeout, ct);

        // Assert: we are the initiator and our stfu went out with CLN's HTLC still pending, after its commitment
        Assert.Equal(QuiescenceInitiator.Local, initiator);
        var ourStfu = wire.Single(inbound: false, MessageTypes.Stfu);
        Assert.True(ourStfu.StfuInitiator);
        Assert.True(committedAt.Value < ourStfu.Sequence, "our stfu went out before CLN's add was committed");
        AssertNoUpdateFromUsBetween(wire, theirAdd.Sequence, ourStfu.Sequence);
        Assert.Equal(1, (await session.GetOurChannelAsync(ct)).ReceivedHtlcCount);
        Assert.Single((await session.GetClnChannelAsync(ct))["htlcs"]!.AsArray());

        // ...the probe ends with tx_abort (no update of ours in between) and CLN resumes with the HTLC in place
        var ourAbort = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxAbort),
                                           s_quiescenceTimeout, "our tx_abort ending the probe", ct);
        await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxAbort, ourAbort.Sequence),
                            s_quiescenceTimeout, "CLN's tx_abort ack", ct);
        AssertNoUpdateFromUsBetween(wire, ourStfu.Sequence, ourAbort.Sequence);
        await AssertClnLogsAsync(session, ct, "Send ack of tx_abort", "Restarting channeld after tx_abort");
        await Poll.UntilAsync(() => !quiescence.GetState(session.ChannelId).BlocksNewLocalUpdates,
                              s_quiescenceTimeout, "our quiescence ended by tx_abort", ct);
        await session.WaitUsableAsync(ct);
        Assert.Equal(1, (await session.GetOurChannelAsync(ct)).ReceivedHtlcCount);

        // ...and CLN's second part completes the set: both parts fulfilled, our invoice settled for the total
        await payment.SendPartAsync(secondPart, partId: 2, ct);
        await payment.AssertPartCompleteAsync(partId: 1, ct);
        await payment.AssertPartCompleteAsync(partId: 2, ct);
        var settled = await Poll.ForAsync(async () => await session.Node.GetInvoiceAsync(invoice.PaymentHash, ct)
                                                          is { Status: InvoiceStatus.Settled } i
                                                          ? i
                                                          : null, s_settleTimeout, "our invoice settled", ct);
        Assert.Equal(total, settled.AmountReceived);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(24_000), ct);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// CLN starts a quiescence (<c>stfu(1)</c>). CLN v26.06.8's <c>stfu_channels</c> (the <c>spenderp</c> plugin)
    /// refuses a peer without <c>option_splice</c> ("Peer does not support splicing", code 354), and we advertise no
    /// splice bit before SP1 (plan D2), so the proof uses the developer command <c>dev-quiesce</c> (the fixture runs
    /// <c>--developer</c>), which sends the same <c>stfu</c> from channeld after its <c>option_quiesce</c> check.
    /// </summary>
    private static async Task ClnQuiesceAsync(ClnChannelSession session, CancellationToken ct)
    {
        var result = await session.Cln.CallAsync("dev-quiesce", ct, ("id", session.Node.NodeIdHex));
        Console.WriteLine($"[cln] dev-quiesce: {result.ToJsonString()}");
    }

    private async Task<(ClnChannelSession Session, QuiescenceWireRecorder Wire)> BuildAsync(
        string nodeName, CancellationToken ct, Action<IServiceCollection>? configureServices = null)
    {
        var wire = new QuiescenceWireRecorder();
        _wire = wire;
        _session = await ClnChannelSession.BuildOurFundedAsync(
                       _fixture, nodeName, s_capacity, s_push, ct,
                       node => node.ConfigureServices = services =>
                       {
                           services.PostConfigure<NodeOptions>(o =>
                           {
                               o.Features.AllowExperimentalFeatures = true;
                               o.Features.OptionQuiesce = FeatureSupport.Optional;
                           });
                           configureServices?.Invoke(services);
                           wire.Install(services);
                       });

        // Both ends see option_quiesce (Q-S-01)
        var peer = await _session.Cln.GetPeerAsync(_session.Node.NodeIdHex, ct);
        var ourFeatures = peer?["features"]?.GetValue<string>() ?? string.Empty;
        Console.WriteLine($"[cln] CLN lists our features {ourFeatures}");
        Assert.True(ClnOnionMessageTests.IsBitSet(ourFeatures, (int)Feature.OptionQuiesce)
                 || ClnOnionMessageTests.IsBitSet(ourFeatures, (int)Feature.OptionQuiesce - 1),
                    "CLN does not see option_quiesce on us");
        Assert.True(_session.Node.PeerManager.GetPeer(_session.ClnPubKey)!.Features
                            .IsFeatureSet(Feature.OptionQuiesce), "CLN does not advertise option_quiesce");
        return (_session, wire);
    }

    /// <summary>
    /// Ends the channel's quiescence with our <c>tx_abort</c> through the node's interactive-tx driver, under the
    /// channel's lock, as the node's own probe exit does.
    /// </summary>
    private static async Task EndWithOurTxAbortAsync(ClnChannelSession session, CancellationToken ct)
    {
        var services = session.Node.Services;
        var driver = services.GetRequiredService<IInteractiveTxDriver>();
        var lockProvider = services.GetRequiredService<IChannelLockProvider>();
        var publisher = services.GetRequiredService<IChannelMessagePublisher>();
        using (await lockProvider.AcquireAsync(session.ChannelId, ct))
        {
            var messages = driver.AbortQuiescence(session.ChannelId, session.ClnPubKey, "no dependent protocol");
            Assert.Single(messages);
            publisher.Publish(session.ClnPubKey, messages);
        }
    }

    private static IQuiescenceService GetQuiescenceService(ClnChannelSession session) =>
        session.Node.Services.GetService<IQuiescenceService>()
     ?? throw new InvalidOperationException("IQuiescenceService is not registered in the node's composition "
                                          + "(lane Q-B's AddQuiescenceServices)");

    /// <summary>
    /// One attempt of Proof Q (c), our side: pays a CLN invoice and starts <see cref="IQuiescenceService.RequestAsync"/>
    /// the moment our <c>update_add_htlc</c> is written, while a watcher records the wire position at which the request
    /// is first seen queued without our <c>stfu</c>. Checks what every attempt must show (our <c>stfu(1)</c> only after
    /// our add was committed and revoked both ways, no update of ours until our <c>tx_abort</c>, the payment paid) and
    /// leaves the channel idle for the next attempt.
    /// </summary>
    private static async Task<InFlightAttempt> RunOurHtlcInFlightAttemptAsync(
        ClnChannelSession session, QuiescenceWireRecorder wire, IQuiescenceService quiescence, int attempt,
        CancellationToken ct)
    {
        var from = wire.CurrentSequence;
        var amount = LightningMoney.Satoshis(40_000 + attempt * 1_000);
        var label = $"nltg-quiesce-c-{attempt}-{Guid.NewGuid():N}";
        var clnInvoice = await session.Cln.CallAsync("invoice", ct, ("amount_msat", (long)amount.MilliSatoshi),
                                                     ("label", label), ("description", "htlc in flight"));
        var started = new TaskCompletionSource<(Task<QuiescenceInitiator> Request, Task<long?> QueuedAt)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        wire.OnFirstOutbound(MessageTypes.UpdateAddHtlc, () =>
        {
            var request = Task.Run(() => quiescence.RequestAsync(session.ChannelId, QuiescencePurpose.Probe, ct), ct);
            var queuedAt = Task.Factory.StartNew(() => WatchQueuedRequest(quiescence, session.ChannelId, wire, request),
                                                 ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            started.TrySetResult((request, queuedAt));
        });

        var payment = session.Node.PayInvoiceAsync(clnInvoice["bolt11"]!.GetValue<string>(), ct, 120);
        var (requestTask, queuedAtTask) = await started.Task.WaitAsync(s_quiescenceTimeout, ct);
        var initiator = await requestTask.WaitAsync(s_quiescenceTimeout, ct);
        var queuedAt = await queuedAtTask.WaitAsync(s_quiescenceTimeout, ct);
        Assert.Equal(QuiescenceInitiator.Local, initiator);

        var ourStfu = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.Stfu, from),
                                          s_quiescenceTimeout, "our stfu", ct);
        Assert.True(ourStfu.StfuInitiator);
        var ourAbort = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxAbort, from),
                                           s_quiescenceTimeout, "our tx_abort ending the probe", ct);
        await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxAbort, ourAbort.Sequence),
                            s_quiescenceTimeout, "CLN's tx_abort ack", ct);
        // CLN v26.06.8 interop gap (NL-467): when CLN's lightningd hands channeld the fulfill of our HTLC while
        // channeld is quiescent (our stfu right after our revoke_and_ack), the in-place channeld restart after tx_abort
        // restores the HTLC as already SENT_REMOVE_HTLC and never sends the fulfill; CLN retransmits it only at the next
        // channel_reestablish. Our side is correct (the fulfill never reached us), so a reconnection recovers it.
        if (await Task.WhenAny(payment, Task.Delay(TimeSpan.FromSeconds(20), ct)) != payment)
        {
            Console.WriteLine($"[proof] attempt {attempt}: CLN did not send its fulfill after the in-place channeld "
                            + "restart (NL-467); reconnecting so channel_reestablish retransmits it");
            await session.Cln.CallAsync("disconnect", ct, ("id", session.Node.NodeIdHex), ("force", true));
        }

        var paid = await payment.WaitAsync(TimeSpan.FromSeconds(150), ct);
        Console.WriteLine($"[cln] our payment: {paid.Status}, failure {paid.FailureCode}: {paid.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, paid.Status);
        var listed = (await session.Cln.CallAsync("listinvoices", ct, ("label", label)))["invoices"]!.AsArray()
                                                                                             .Single()!;
        Assert.Equal("paid", listed["status"]!.GetValue<string>());
        await Poll.UntilAsync(() => !quiescence.GetState(session.ChannelId).BlocksNewLocalUpdates,
                              s_quiescenceTimeout, "our quiescence ended", ct);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);

        var traffic = wire.Snapshot().Where(m => m.Sequence >= from).ToList();
        Assert.Single(traffic, m => !m.Inbound && m.Type == (ushort)MessageTypes.Stfu);
        var ourAdd = traffic.First(m => !m.Inbound && m.Type == (ushort)MessageTypes.UpdateAddHtlc);
        var ourCommit = traffic.First(m => !m.Inbound && m.Type == (ushort)MessageTypes.CommitmentSigned
                                        && m.Sequence > ourAdd.Sequence);
        var theirRevoke = traffic.First(m => m.Inbound && m.Type == (ushort)MessageTypes.RevokeAndAck
                                          && m.Sequence > ourCommit.Sequence);
        AssertOurUpdatesSettledBefore(traffic, ourStfu.Sequence);
        Assert.True(theirRevoke.Sequence < ourStfu.Sequence, "our stfu went out before CLN revoked for our add");
        AssertNoUpdateFromUsBetween(wire, ourStfu.Sequence, ourAbort.Sequence);
        return new InFlightAttempt(attempt, ourAdd.Sequence, queuedAt, theirRevoke.Sequence, ourStfu.Sequence);
    }

    /// <summary>
    /// Spins until <paramref name="request"/> is seen queued (<see cref="QuiescenceState.PendingRequest"/> set, our
    /// <c>stfu</c> not sent) and returns <see cref="QuiescenceWireRecorder.CurrentSequence"/> read after that
    /// observation: every message recorded at or after it was recorded after the request was queued. Null when our
    /// <c>stfu</c> was seen first or the request finished without being seen queued (the attempt proves no wait).
    /// </summary>
    private static long? WatchQueuedRequest(IQuiescenceService quiescence, ChannelId channelId,
                                            QuiescenceWireRecorder wire, Task request)
    {
        var deadline = DateTime.UtcNow + s_quiescenceTimeout;
        var spinner = new SpinWait();
        while (DateTime.UtcNow < deadline)
        {
            var finished = request.IsCompleted;
            var state = quiescence.GetState(channelId);
            if (state.StfuSent)
                return null;

            if (state.PendingRequest.HasValue)
                return wire.CurrentSequence;

            if (finished)
                return null;

            spinner.SpinOnce();
        }

        return null;
    }

    /// <summary>
    /// The sequence of the last message that made the inbound <c>update_add_htlc</c> at
    /// <paramref name="addSequence"/> irrevocably committed on both sides (CLN's <c>commitment_signed</c> and our
    /// <c>revoke_and_ack</c>, our <c>commitment_signed</c> and CLN's <c>revoke_and_ack</c>), or null before that.
    /// </summary>
    private static StrongBox<long>? IrrevocablyCommittedAt(IReadOnlyList<WireMessage> traffic, long addSequence)
    {
        WireMessage? First(bool inbound, MessageTypes type, long after) =>
            traffic.FirstOrDefault(m => m.Inbound == inbound && m.Type == (ushort)type && m.Sequence > after);

        var theirCommit = First(true, MessageTypes.CommitmentSigned, addSequence);
        var ourRevoke = theirCommit is null ? null : First(false, MessageTypes.RevokeAndAck, theirCommit.Sequence);
        var ourCommit = First(false, MessageTypes.CommitmentSigned, addSequence);
        var theirRevoke = ourCommit is null ? null : First(true, MessageTypes.RevokeAndAck, ourCommit.Sequence);
        return ourRevoke is null || theirRevoke is null
                   ? null
                   : new StrongBox<long>(Math.Max(ourRevoke.Sequence, theirRevoke.Sequence));
    }

    /// <summary>
    /// One attempt of Proof Q (c), our side, as positions in the recording.
    /// </summary>
    /// <param name="Attempt">The attempt number.</param>
    /// <param name="OurAddSequence">Our <c>update_add_htlc</c>.</param>
    /// <param name="QueuedAt">The wire position at which the request was first seen queued, or null if never.</param>
    /// <param name="TheirRevokeSequence">CLN's <c>revoke_and_ack</c> for our <c>commitment_signed</c> of the add.</param>
    /// <param name="OurStfuSequence">Our <c>stfu</c>.</param>
    private sealed record InFlightAttempt(int Attempt, long OurAddSequence, long? QueuedAt, long TheirRevokeSequence,
                                          long OurStfuSequence)
    {
        /// <summary>
        /// The request was queued before CLN's <c>revoke_and_ack</c> for our add was even read, so our <c>stfu</c> had
        /// to wait for it (Q-S-02).
        /// </summary>
        public bool WaitExercised => QueuedAt is { } queued && queued <= TheirRevokeSequence;

        public string Describe() =>
            $"attempt {Attempt}: our add #{OurAddSequence}, request queued at "
          + $"{(QueuedAt is { } q ? $"#{q}" : "(not seen queued)")}, CLN's revoke_and_ack #{TheirRevokeSequence}, "
          + $"our stfu #{OurStfuSequence}: wait {(WaitExercised ? "exercised" : "not exercised")}";
    }

    /// <summary>
    /// A multi-part payment CLN sends to our invoice part by part with <c>sendpay</c> over the direct channel (all
    /// parts share one <c>groupid</c> and carry the invoice total as <c>amount_msat</c>, BOLT 4 <c>basic_mpp</c>).
    /// </summary>
    private sealed class HeldPayment
    {
        private const int GroupId = 1;

        private readonly ClnChannelSession _session;
        private readonly string _bolt11;
        private readonly string _paymentHashHex;
        private readonly string _paymentSecretHex;
        private readonly string _scid;
        private readonly long _totalMsat;
        private readonly int _finalCltvDelta;

        private HeldPayment(ClnChannelSession session, string bolt11, string paymentHashHex, string paymentSecretHex,
                            string scid, long totalMsat, int finalCltvDelta)
        {
            _session = session;
            _bolt11 = bolt11;
            _paymentHashHex = paymentHashHex;
            _paymentSecretHex = paymentSecretHex;
            _scid = scid;
            _totalMsat = totalMsat;
            _finalCltvDelta = finalCltvDelta;
        }

        public static async Task<HeldPayment> CreateAsync(ClnChannelSession session,
                                                          InvoiceInfoClientResponse invoice, CancellationToken ct)
        {
            var bolt11 = invoice.Bolt11!;
            var decoded = await session.Cln.CallAsync("decode", ct, ("string", bolt11));
            Console.WriteLine($"[cln] decode of our invoice: {decoded.ToJsonString()}");
            Assert.NotNull(decoded["payment_secret"]);
            var scid = (await session.GetClnChannelAsync(ct))["short_channel_id"]!.GetValue<string>();
            return new HeldPayment(session, bolt11, Convert.ToHexStringLower((byte[])invoice.PaymentHash),
                                   decoded["payment_secret"]!.GetValue<string>(), scid,
                                   (long)invoice.Amount!.MilliSatoshi,
                                   decoded["min_final_cltv_expiry"]!.GetValue<int>());
        }

        /// <summary>
        /// <c>sendpay</c> of one part over the direct channel (CLN adds the route's <c>delay</c> to its next block).
        /// </summary>
        public async Task SendPartAsync(LightningMoney part, int partId, CancellationToken ct)
        {
            var route = new JsonArray(new JsonObject
            {
                ["id"] = _session.Node.NodeIdHex,
                ["channel"] = _scid,
                ["amount_msat"] = (long)part.MilliSatoshi,
                ["delay"] = _finalCltvDelta + 6
            });
            var sent = await _session.Cln.CallAsync("sendpay", ct, ("route", route),
                                                    ("payment_hash", _paymentHashHex), ("amount_msat", _totalMsat),
                                                    ("bolt11", _bolt11), ("payment_secret", _paymentSecretHex),
                                                    ("partid", partId), ("groupid", GroupId));
            Console.WriteLine($"[cln] sendpay part {partId}: {sent.ToJsonString()}");
        }

        /// <summary>
        /// <c>waitsendpay</c> of one part: it completed (we fulfilled it).
        /// </summary>
        public async Task AssertPartCompleteAsync(int partId, CancellationToken ct)
        {
            JsonNode result;
            try
            {
                result = await _session.Cln.CallAsync("waitsendpay", ct, ("payment_hash", _paymentHashHex),
                                                      ("timeout", 60), ("partid", partId), ("groupid", GroupId));
            }
            catch (ClnRpcException e)
            {
                Assert.Fail($"CLN's part {partId} did not complete: {e.Message}");
                throw;
            }

            Assert.Equal("complete", result["status"]!.GetValue<string>());
        }
    }

    /// <summary>
    /// Waits until CLN's log for our node's peer holds every <paramref name="fragments"/>.
    /// </summary>
    private static async Task AssertClnLogsAsync(ClnChannelSession session, CancellationToken ct,
                                                 params string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            await Poll.UntilAsync(async () => (await GetClnLogForUsAsync(session, ct, fragment)).Length > 0,
                                  s_quiescenceTimeout, $"CLN log '{fragment}' for our node", ct,
                                  TimeSpan.FromMilliseconds(500));
        }
    }

    /// <summary>
    /// CLN's debug log lines whose <c>node_id</c> is our node and that contain one of <paramref name="fragments"/>.
    /// </summary>
    private static async Task<string> GetClnLogForUsAsync(ClnChannelSession session, CancellationToken ct,
                                                          params string[] fragments)
    {
        try
        {
            var result = await session.Cln.CallAsync("getlog", ct, ("level", "debug"));
            var lines = result["log"]!.AsArray()
                                      .Where(e => e?["node_id"]?.GetValue<string>() == session.Node.NodeIdHex
                                               || e?["source"]?.ToString().Contains(session.Node.NodeIdHex[..8],
                                                      StringComparison.Ordinal) == true)
                                      .Select(e => $"{e?["time"]} {e?["source"]}: {e?["log"]}")
                                      .Where(l => fragments.Any(f => l.Contains(f, StringComparison.Ordinal)))
                                      .TakeLast(120);
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception e)
        {
            return $"(getlog failed: {e.Message})";
        }
    }

    /// <summary>
    /// Q-S-02 on the wire: every update we sent before <paramref name="stfuSequence"/> was followed, still before it,
    /// by our <c>commitment_signed</c> and CLN's <c>revoke_and_ack</c> (locked in CLN's commitment) and by CLN's
    /// <c>commitment_signed</c> and our <c>revoke_and_ack</c> (locked in ours).
    /// </summary>
    private static void AssertOurUpdatesSettledBefore(IReadOnlyList<WireMessage> traffic, long stfuSequence)
    {
        var lastUpdate = traffic.LastOrDefault(m => !m.Inbound && m.IsUpdate && m.Sequence < stfuSequence);
        if (lastUpdate is null)
            return;

        bool Exists(bool inbound, MessageTypes type, long after, out long sequence)
        {
            var found = traffic.FirstOrDefault(m => m.Inbound == inbound && m.Type == (ushort)type
                                                 && m.Sequence > after && m.Sequence < stfuSequence);
            sequence = found?.Sequence ?? -1;
            return found is not null;
        }

        Assert.True(Exists(false, MessageTypes.CommitmentSigned, lastUpdate.Sequence, out var ourCs),
                    "no commitment_signed of ours after our last update before our stfu");
        Assert.True(Exists(true, MessageTypes.RevokeAndAck, ourCs, out _),
                    "CLN did not revoke for our last update before our stfu");
        Assert.True(Exists(true, MessageTypes.CommitmentSigned, ourCs, out var theirCs),
                    "CLN did not sign our last update into our commitment before our stfu");
        Assert.True(Exists(false, MessageTypes.RevokeAndAck, theirCs, out _),
                    "we did not revoke for CLN's commitment_signed before our stfu");
    }

    /// <summary>
    /// Q-S-04 on the wire: no <c>update_*</c> of ours between our <c>stfu</c> and our <c>tx_abort</c>.
    /// </summary>
    private static void AssertNoUpdateFromUsBetween(QuiescenceWireRecorder wire, long from, long to)
    {
        var updates = wire.Snapshot().Where(m => !m.Inbound && m.IsUpdate && m.Sequence > from && m.Sequence < to)
                          .ToList();
        Assert.True(updates.Count == 0,
                    $"we sent {string.Join(", ", updates.Select(u => (MessageTypes)u.Type))} while quiescing");
    }

    private static void AssertNoWarningOrError(QuiescenceWireRecorder wire)
    {
        var sent = wire.Snapshot().Where(m => m.Type is QuiescenceWireRecorder.WarningType
                                                     or QuiescenceWireRecorder.ErrorType).ToList();
        Assert.True(sent.Count == 0,
                    "warnings/errors on the wire: "
                  + string.Join("; ", sent.Select(m => $"{(m.Inbound ? "received" : "sent")} {m.Type} {m.Hex}")));
    }

    private static async Task AssertWePayClnAsync(ClnChannelSession session, LightningMoney amount,
                                                  CancellationToken ct)
    {
        var label = $"nltg-quiesce-pays-{Guid.NewGuid():N}";
        var invoice = await session.Cln.CallAsync("invoice", ct, ("amount_msat", (long)amount.MilliSatoshi),
                                                  ("label", label), ("description", "nltg pays cln"));
        var payment = await session.Node.PayInvoiceAsync(invoice["bolt11"]!.GetValue<string>(), ct);
        Console.WriteLine($"[cln] our payment: {payment.Status}, failure {payment.FailureCode}: "
                        + payment.FailureReason);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        var listed = (await session.Cln.CallAsync("listinvoices", ct, ("label", label)))["invoices"]!.AsArray()
                                                                                             .Single()!;
        Assert.Equal("paid", listed["status"]!.GetValue<string>());
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
    }

    private static async Task AssertClnPaysUsAsync(ClnChannelSession session, LightningMoney amount,
                                                   CancellationToken ct)
    {
        var invoice = await session.Node.CreateInvoiceAsync(amount, $"cln pays nltg {Guid.NewGuid():N}", ct);
        JsonNode result;
        try
        {
            result = await session.Cln.CallAsync("pay", ct, ("bolt11", invoice.Bolt11!), ("retry_for", 30));
        }
        catch (ClnRpcException e)
        {
            Assert.Fail($"CLN could not pay our invoice: {e.Message}");
            throw;
        }

        Assert.Equal("complete", result["status"]!.GetValue<string>());
        var preimage = Convert.FromHexString(result["payment_preimage"]!.GetValue<string>());
        Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(preimage));
        await Poll.UntilAsync(async () => (await session.Node.GetInvoiceAsync(invoice.PaymentHash, ct))?.Status
                                       == InvoiceStatus.Settled, s_settleTimeout, "our invoice settled", ct);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
    }

    /// <summary>
    /// One recorded channel message.
    /// </summary>
    /// <param name="Sequence">Its position in the recording (both directions share one order).</param>
    /// <param name="Type">The message type.</param>
    /// <param name="Wire">The whole message (u16 type prefix included).</param>
    /// <param name="Inbound">Received (true) or sent (false) by our node.</param>
    /// <param name="At">When it was serialized or read.</param>
    internal sealed record WireMessage(long Sequence, ushort Type, byte[] Wire, bool Inbound, DateTimeOffset At)
    {
        // channel_id follows the type; stfu: channel_id || u8 initiator
        private const int ChannelIdOffset = 2;
        private const int ChannelIdLength = 32;
        private const int StfuInitiatorOffset = ChannelIdOffset + ChannelIdLength;

        public string Hex => Convert.ToHexStringLower(Wire);

        /// <summary>
        /// Whether this is an update message (BOLT 2: <c>update_add_htlc</c>, <c>update_fulfill_htlc</c>,
        /// <c>update_fail_htlc</c>, <c>update_fail_malformed_htlc</c>, <c>update_fee</c>).
        /// </summary>
        public bool IsUpdate => (MessageTypes)Type is MessageTypes.UpdateAddHtlc or MessageTypes.UpdateFulfillHtlc
                                                    or MessageTypes.UpdateFailHtlc
                                                    or MessageTypes.UpdateFailMalformedHtlc
                                                    or MessageTypes.UpdateFee;

        /// <summary>
        /// The <c>initiator</c> byte of a <c>stfu</c> (nonzero = 1).
        /// </summary>
        public bool StfuInitiator =>
            Type == (ushort)MessageTypes.Stfu && Wire.Length > StfuInitiatorOffset && Wire[StfuInitiatorOffset] != 0;

        public bool HasChannelId(byte[] channelId) =>
            Wire.Length >= ChannelIdOffset + ChannelIdLength
         && Wire.AsSpan(ChannelIdOffset, ChannelIdLength).SequenceEqual(channelId);
    }

    /// <summary>
    /// Records, in one order for both directions, the channel messages of the quiescence proof (<c>stfu</c>,
    /// <c>tx_abort</c>, the update/commitment/revocation messages, <c>channel_reestablish</c>) and every
    /// <c>warning</c>/<c>error</c>, with the transport-path pattern of <see cref="RawOnionMessageRecorder"/>: the node's
    /// <see cref="ITransportServiceFactory"/> and <see cref="IMessageServiceFactory"/> are replaced by the production
    /// factories over a recording decorator of its <see cref="IMessageSerializer"/>, so commit diffs and stored errors
    /// the channel layer serializes are never recorded.
    /// </summary>
    internal sealed class QuiescenceWireRecorder
    {
        public const ushort WarningType = 1;
        public const ushort ErrorType = 17;

        private static readonly HashSet<ushort> s_recorded =
        [
            WarningType, ErrorType, (ushort)MessageTypes.Stfu, (ushort)MessageTypes.TxAbort,
            (ushort)MessageTypes.UpdateAddHtlc, (ushort)MessageTypes.UpdateFulfillHtlc,
            (ushort)MessageTypes.UpdateFailHtlc, (ushort)MessageTypes.CommitmentSigned,
            (ushort)MessageTypes.RevokeAndAck, (ushort)MessageTypes.UpdateFee,
            (ushort)MessageTypes.UpdateFailMalformedHtlc, (ushort)MessageTypes.ChannelReestablish
        ];

        private readonly ConcurrentQueue<WireMessage> _traffic = new();
        private readonly ConcurrentDictionary<ushort, Action> _onFirstOutbound = new();
        private readonly Lock _sequenceLock = new();
        private long _sequence;

        public IReadOnlyList<WireMessage> Snapshot() => _traffic.ToArray();

        public int Count(bool inbound, MessageTypes type) =>
            _traffic.Count(m => m.Inbound == inbound && m.Type == (ushort)type);

        public WireMessage? FirstOrDefault(bool inbound, MessageTypes type) =>
            _traffic.FirstOrDefault(m => m.Inbound == inbound && m.Type == (ushort)type);

        /// <summary>
        /// The first recorded message of <paramref name="type"/> at or after <paramref name="fromSequence"/>.
        /// </summary>
        public WireMessage? FirstOrDefault(bool inbound, MessageTypes type, long fromSequence) =>
            _traffic.FirstOrDefault(m => m.Inbound == inbound && m.Type == (ushort)type
                                      && m.Sequence >= fromSequence);

        /// <summary>
        /// The sequence the next recorded message gets: every message recorded so far is below it.
        /// </summary>
        public long CurrentSequence
        {
            get
            {
                lock (_sequenceLock)
                    return _sequence;
            }
        }

        public WireMessage Single(bool inbound, MessageTypes type) =>
            Assert.Single(_traffic, m => m.Inbound == inbound && m.Type == (ushort)type);

        /// <summary>
        /// Runs <paramref name="callback"/> once, when the next outbound message of <paramref name="type"/> is written
        /// (after it is recorded, before it reaches the socket). It must not block.
        /// </summary>
        public void OnFirstOutbound(MessageTypes type, Action callback) =>
            _onFirstOutbound[(ushort)type] = callback;

        public string Describe() =>
            string.Join(" ", Snapshot().Select(m => $"{(m.Inbound ? "<" : ">")}{(MessageTypes)m.Type}"
                                                  + (m.Type == (ushort)MessageTypes.Stfu
                                                         ? $"({(m.StfuInitiator ? 1 : 0)})"
                                                         : string.Empty)));

        public void Install(IServiceCollection services)
        {
            services.AddKeyedSingleton<IMessageSerializer>(this, (sp, _) =>
                                                               new RecordingMessageSerializer(
                                                                   sp.GetRequiredService<IMessageSerializer>(),
                                                                   this));
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
        }

        private void Record(byte[] wire, bool inbound)
        {
            if (wire.Length < 2)
                return;

            var type = BinaryPrimitives.ReadUInt16BigEndian(wire);
            if (!s_recorded.Contains(type))
                return;

            lock (_sequenceLock)
                _traffic.Enqueue(new WireMessage(_sequence++, type, wire, inbound, DateTimeOffset.UtcNow));

            if (!inbound && _onFirstOutbound.TryRemove(type, out var callback))
                callback();
        }

        private sealed class RecordingMessageSerializer(IMessageSerializer inner, QuiescenceWireRecorder recorder)
            : IMessageSerializer
        {
            public async Task SerializeAsync(IMessage message, Stream stream)
            {
                using var buffer = new MemoryStream();
                await inner.SerializeAsync(message, buffer);
                var wire = buffer.ToArray();
                recorder.Record(wire, inbound: false);
                await stream.WriteAsync(wire);
            }

            public Task<TMessage?> DeserializeMessageAsync<TMessage>(Stream stream) where TMessage : class, IMessage =>
                inner.DeserializeMessageAsync<TMessage>(stream);

            public async Task<IMessage?> DeserializeMessageAsync(Stream stream)
            {
                var wire = new byte[stream.Length - stream.Position];
                await stream.ReadExactlyAsync(wire);
                recorder.Record(wire, inbound: true);

                using var copy = new MemoryStream(wire, false);
                return await inner.DeserializeMessageAsync(copy);
            }
        }
    }
}