using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBitcoin.RPC;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Abcd;
using Application.Channels.Splicing;
using Daemon.Extensions;
using Daemon.Interfaces;
using Domain.Channels.Splicing.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Protocol.Constants;
using Domain.Protocol.InteractiveTx;
using Fixtures;
using Utils;
using SpliceWireMessage = ClnSpliceTests.SpliceWireMessage;
using SpliceWireRecorder = ClnSpliceTests.SpliceWireRecorder;

/// <summary>
/// Proof SPR of the splicing plan (<c>docs/agents/SPLICING_PLAN.md</c> §5 "Wave SPR", SPR-T4, lane SPR-C, NL-489):
/// splice RBF against Core Lightning v26.06.8, with our node on <c>Features:AllowExperimentalFeatures</c> and
/// <c>OptionQuiesce</c>/<c>OptionSplice</c>/<c>DualFund</c> Optional. (a) We RBF our pending splice with
/// <c>bumpsplice</c>: <c>tx_init_rbf</c> at the new feerate, CLN's <c>tx_ack_rbf</c>, both attempts pending on both ends,
/// bitcoind replaces the first attempt, the bumped attempt locks and the first never confirms. (b) CLN RBFs its own
/// pending splice at a higher feerate and we follow it as acceptor. (c) CLN's RBF at the feerate of the attempt it
/// replaces is refused with <c>tx_abort</c> (IT-RBF-01) and the first attempt locks. (d) Payments both ways while three
/// attempts are pending: every update is <c>start_batch(4)</c> + one <c>commitment_signed</c> per active funding (the
/// current one and the three attempts) with one <c>revoke_and_ack</c> per batch; the last attempt locks, the other two
/// never confirm. (e) A bumped splice survives a disconnection and a restart of our node (SP-RE with RBF: no
/// <c>next_funding</c> once both attempts are signed, both attempts still pending after <c>channel_reestablish</c>) and
/// the bumped attempt locks.
/// </summary>
/// <remarks>
/// <para><b>What CLN v26.06.8 offers for splice RBF (checked in wave spr lane SPR-C, 2026-09-28, on the pinned image
/// with <c>--developer --dev-bitcoind-poll=1 --ignore-fee-limits=false</c>, two CLN nodes with an anchors channel
/// between them, on a scratch bitcoind; the RPC calls below are the ones this proof uses):</b></para>
/// <list type="bullet">
/// <item><c>lightning-cli help</c> has no splice RBF command: the splice RPCs are the ones listed in
/// <see cref="ClnSpliceTests"/> (<c>splicein</c>, <c>spliceout</c>, <c>splice_init</c>, <c>splice_update</c>,
/// <c>splice_signed</c>, <c>dev-splice</c>, <c>stfu_channels</c>, <c>abort_channels</c>); <c>openchannel_bump</c> is
/// for v2 opens only. A splice on a channel whose splice is pending <b>is</b> the RBF (CLN #8021): CLN logs
/// <c>Getting handle_splice_init psbt version 2 (RBF?: y)</c> and sends <c>tx_init_rbf</c> instead of
/// <c>splice_init</c>; its peer answers <c>tx_ack_rbf</c>.</item>
/// <item><c>splicein</c> has no feerate: its RBF goes out at CLN's splice estimate, on the idle regtest the same
/// 253 sat/kw as the first attempt. The CLN acceptor acked that (CLN does not apply BOLT 2's 25/24 rule to a received
/// <c>tx_init_rbf</c>), both signed, and bitcoind refused the replacement (<c>insufficient fee, rejecting
/// replacement</c>, -26); CLN still listed the unbroadcast attempt as a second <c>inflight</c>. Proof (c) uses exactly
/// that call against our node, which must refuse it.</item>
/// <item>An RBF at a chosen feerate needs the low-level flow (<see cref="ClnBumpAsync"/>): <c>utxopsbt</c> with one
/// confirmed wallet output of CLN's that no pending attempt spends (the first attempt's change output makes bitcoind
/// refuse the RBF with <c>bad-txns-spends-conflicting-tx</c>) and a <c>startweight</c> that covers the shared input and
/// the new funding output (with <c>startweight=0</c> CLN aborts its own RBF: "Our fee (402000msat) was too low, must be
/// at least 1003sat weight: 1003"), then <c>splice_init channel_id relative_amount initialpsbt feerate_per_kw
/// force_feerate=true</c>, <c>splice_update</c> until <c>commitments_secured</c>, <c>signpsbt</c> and
/// <c>splice_signed</c>. <c>splice_init</c>'s <c>feerate_per_kw</c> is read per kilo-<b>vbyte</b>: 1000 gave 253perkw
/// (250, floored), 4000 gave <c>1000perkw</c> in <c>inflight</c> and in CLN's log (<c>splicing-&gt;feerate_per_kw:
/// 1000</c>); SP1 saw 3000 become 750. The proof passes 4 x the sat/kw it wants and asserts the feerate of the
/// <c>tx_init_rbf</c> on the wire.</item>
/// <item>Every attempt stays an <c>inflight</c> entry (<c>funding_txid</c>, <c>feerate</c> "253perkw" then
/// "1000perkw", <c>splice_amount</c> = that attempt's own contribution) with the old <c>short_channel_id</c>; bitcoind
/// replaced the 253 sat/kw attempt with the 1000 sat/kw one; at the first confirmation both CLN nodes sent
/// <c>splice_locked</c> for the bumped attempt (<c>mutual splice_locked, scid ... updated</c>) and cleared every
/// inflight.</item>
/// <item>With two attempts pending CLN's commitment updates were <c>start_batch</c> with <c>batch_size</c> 3 (the
/// current funding and both attempts; connectd log <c>Processing batch extracted item WIRE_START_BATCH ... 0003</c>),
/// three <c>commitment_signed</c> with their <c>funding_txid</c> TLV and one <c>revoke_and_ack</c>.</item>
/// <item>CLN as the acceptor of an RBF contributes 0 even when it initiated the splice: the second CLN node's RBF (its
/// own +20,000) of the first node's +50,000 splice-in produced a funding output of the old capacity + 20,000, and the
/// first node's inflight showed <c>splice_amount</c> 0. An RBF by the other side drops the splice initiator's
/// contribution, so this proof only bumps a splice from the side that started it (BOLT 2 allows either quiescence
/// initiator; the in-process harness of lane SPR-A covers the other case).</item>
/// </list>
/// <para>Our <c>Splice:MinRbfInterval</c> ("an attempt created recently": a peer's <c>tx_init_rbf</c> gets
/// <c>tx_abort</c>) is set to <see cref="s_minRbfInterval"/> on the test node so CLN's RBF, seconds after its first
/// attempt, is judged on its feerate; every RBF waits past it first. Our bumps go through the daemon's
/// <c>bumpsplice</c> client handler (<see cref="BumpSpliceClientRequest"/>, lane SPR-B). Written against the SPR
/// contracts (<c>b72a42ea</c>); the RBF protocol (lane SPR-A) and <c>bumpsplice</c> (lane SPR-B) land in parallel and
/// the integrator runs the proof after the merge, from the host process: <c>NLightning.Integration.Tests -class
/// NLightning.Integration.Tests.Docker.Interop.Cln.ClnSpliceRbfTests</c>.</para>
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnSpliceRbfTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 15 * 60 * 1_000;

    /// <summary>The most blocks mined one at a time until both ends locked the splice.</summary>
    private const int MaxLockBlocks = 12;

    /// <summary>The first attempt: BOLT 3's floor, the lowest feerate CLN accepts (and its own idle estimate).</summary>
    private const uint LowFeeRatePerKw = 253;

    /// <summary>A middle attempt of (d): above the IT-RBF-01 minimum after 253 (278) and at least 1 sat/vB more for
    /// bitcoind's replacement rule.</summary>
    private const uint MidFeeRatePerKw = 600;

    /// <summary>The bumped attempt: above the IT-RBF-01 minimum after 600 (625) and 1 sat/vB more for bitcoind.</summary>
    private const uint BumpFeeRatePerKw = 1_000;

    /// <summary>CLN's <c>splice_init</c> reads <c>feerate_per_kw</c> per kilo-vbyte (see the class remarks).</summary>
    private const uint ClnSpliceInitFeerateFactor = 4;

    /// <summary>The weight CLN's RBF PSBT pays for besides its own input and change (shared input, funding output).</summary>
    private const int ClnRbfStartWeight = 800;

    private const ulong SpliceInSat = 100_000;

    /// <summary>Payments each way while three attempts are pending, proof (d).</summary>
    private const int PendingPayments = 3;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(400_000);
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_settleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Our <c>Splice:MinRbfInterval</c> in this proof (the default is a minute).</summary>
    private static readonly TimeSpan s_minRbfInterval = TimeSpan.FromSeconds(2);

    private readonly ClnFixture _fixture;
    private ClnChannelSession? _session;
    private SpliceWireRecorder? _wire;

    public ClnSpliceRbfTests(ClnFixture fixture, ITestOutputHelper output)
    {
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
            Console.WriteLine("[cln] CLN splice/RBF log lines so far:\n"
                            + await _fixture.Cln.GetLogLinesAsync("plice", CancellationToken.None, 80));
            Console.WriteLine("[cln] CLN RBF log lines so far:\n"
                            + await _fixture.Cln.GetLogLinesAsync("RBF", CancellationToken.None, 40));
            Console.WriteLine("[cln] CLN unusual/broken log lines so far:\n"
                            + await _fixture.Cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 60,
                                                                  "unusual"));
            if (DockerDiagnostics.CurrentTestFailed)
            {
                Console.WriteLine($"[cln] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
                if (_session.Node.IsRunning)
                {
                    Console.WriteLine("===== our node: last log lines =====");
                    foreach (var line in _session.Node.NodeLog.TakeLast(200))
                        Console.WriteLine(line);
                }
            }

            await _session.DisposeAsync();
        }
    }

    /// <summary>
    /// Proof SPR (a): we splice in 100,000 sat at 253 sat/kw, then <c>bumpsplice</c> at 1,000 sat/kw. We are the
    /// quiescence initiator of the RBF and send <c>tx_init_rbf</c> (feerate 1,000, our contribution kept at +100,000);
    /// CLN answers <c>tx_ack_rbf</c> with 0; both sign the new attempt (<c>commitment_signed</c> for its funding both ways,
    /// then <c>tx_signatures</c> with the shared-input signature); both attempts spend the current funding output 2-of-2
    /// and bitcoind replaced the first; the bumped attempt pays our new feerate and more than the first in total
    /// (SP-TX-05); both ends list both attempts pending (CLN: two <c>inflight</c> entries); a payment each way while
    /// pending; the bumped attempt locks, the first never confirms; payments after the lock.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurPendingSplice_When_WeBumpIt_Then_ClnFollowsTheRbfAndTheBumpedAttemptLocks()
    {
        // Arrange: our splice-in at the floor feerate, pending
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-rbf-a", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        var first = await SpliceInAsync(session, SpliceInSat, LowFeeRatePerKw, ct);
        var firstTxId = AssertSignedResponse(first);
        await AssertSignedAttemptAsync(wire, from, firstTxId, ct);
        var firstTx = await AssertSpliceTransactionAsync(before, firstTxId, before.CapacitySat + SpliceInSat, ct);
        var firstFee = await GetFeeAsync(firstTxId, ct);
        await AssertPendingAttemptsAsync(session, [firstTxId], ct);
        await Task.Delay(s_minRbfInterval + TimeSpan.FromSeconds(1), ct);
        var rbfFrom = wire.CurrentSequence;

        // Act
        var bumped = await BumpAsync(session, BumpFeeRatePerKw, ct);

        // Assert: our tx_init_rbf at the new feerate with our contribution kept, CLN acked with 0
        var bumpTxId = AssertSignedResponse(bumped);
        Assert.NotEqual(firstTxId, bumpTxId);
        var expectedCapacity = before.CapacitySat + SpliceInSat;
        Assert.Equal(expectedCapacity, bumped.NewCapacitySat);
        var initRbf = wire.First(inbound: false, MessageTypes.TxInitRbf, rbfFrom);
        var ackRbf = wire.First(inbound: true, MessageTypes.TxAckRbf, rbfFrom);
        Assert.Equal(BumpFeeRatePerKw, RbfFeerate(initRbf));
        Assert.Equal((long)SpliceInSat, InitRbfContribution(initRbf));
        Assert.Equal(0, AckRbfContribution(ackRbf) ?? 0);
        AssertWeWereQuiescenceInitiator(wire, rbfFrom, initRbf);
        Assert.True(initRbf.Sequence < ackRbf.Sequence);
        Assert.Null(wire.FirstOrDefault(inbound: false, MessageTypes.SpliceInit, rbfFrom));
        var signaturesAt = await AssertSignedAttemptAsync(wire, rbfFrom, bumpTxId, ct);

        // ...both attempts spend the current funding (they double-spend each other), bitcoind kept the bump, which pays
        // our feerate and more than the first in total
        var bumpTx = await AssertSpliceTransactionAsync(before, bumpTxId, expectedCapacity, ct);
        await AssertReplacedAsync(firstTxId, bumpTxId, ct);
        await AssertOurFeerateAsync(bumpTxId, BumpFeeRatePerKw, ct);
        var bumpFee = await GetFeeAsync(bumpTxId, ct);
        Assert.True(bumpFee > firstFee, $"the bump pays {bumpFee} sat, not more than the first attempt's {firstFee}");
        await AssertPendingAttemptsAsync(session, [firstTxId, bumpTxId], ct);

        // ...payments while both are pending (batches of three), then the lock of the bump
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(20_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(10_000), ct);
        var locked = await MineUntilLockedAsync(session, wire, before, bumpTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat + (long)SpliceInSat * 1_000 - 20_000_000 + 10_000_000,
                     locked.LocalBalanceMsat);
        await AssertNeverConfirmsAsync(before, firstTx, bumpTx, ct);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, locked.LockedAtSequence,
                                  [before.FundingTxId, firstTxId, bumpTxId]);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Proof SPR (b): CLN splices in 100,000 sat (<c>splicein</c>, its 253 sat/kw estimate), then RBFs it at 1,000
    /// sat/kw through <c>splice_init</c> on the pending channel. We get <c>tx_init_rbf</c> (feerate 1,000, CLN's
    /// +100,000) and answer <c>tx_ack_rbf</c> with 0 (D10); the new attempt is signed both ways and replaces the first
    /// in bitcoind's mempool; both ends list both attempts; a payment each way while pending; the bumped attempt locks
    /// with our balance unchanged by the splice, the first never confirms.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ClnsPendingSplice_When_ClnBumpsIt_Then_WeAckTheRbfAndTheBumpedAttemptLocks()
    {
        // Arrange: CLN's splice-in pending (two wallet outputs: one for the splice, one for its RBF)
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-rbf-b", ct);
        await _fixture.FundClnWalletAsync(LightningMoney.Satoshis(300_000), [session.Node], ct);
        await _fixture.FundClnWalletAsync(LightningMoney.Satoshis(300_000), [session.Node], ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        var spliced = await session.Cln.CallAsync("splicein", ct, ("channel", session.ChannelIdHex),
                                                  ("amount", SpliceInSat));
        var firstTxId = uint256.Parse(spliced["txid"]!.GetValue<string>());
        Console.WriteLine($"[cln] splicein: txid {firstTxId}");
        await AssertSignedAttemptAsync(wire, from, firstTxId, ct);
        var expectedCapacity = before.CapacitySat + SpliceInSat;
        var firstTx = await AssertSpliceTransactionAsync(before, firstTxId, expectedCapacity, ct);
        var firstInit = wire.First(inbound: true, MessageTypes.SpliceInit, from);
        await AssertPendingAttemptsAsync(session, [firstTxId], ct);
        await Task.Delay(s_minRbfInterval + TimeSpan.FromSeconds(1), ct);
        var rbfFrom = wire.CurrentSequence;

        // Act
        var bumpTxId = await ClnBumpAsync(session, (long)SpliceInSat, BumpFeeRatePerKw, [firstTxId], ct);

        // Assert: CLN's tx_init_rbf at 1,000 sat/kw with its +100,000, our tx_ack_rbf with 0
        var initRbf = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxInitRbf, rbfFrom),
                                          s_stepTimeout, "CLN's tx_init_rbf", ct);
        var ackRbf = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxAckRbf, rbfFrom),
                                         s_stepTimeout, "our tx_ack_rbf", ct);
        Console.WriteLine($"[proof] CLN's first attempt at {firstInit.SpliceFeeratePerKw} sat/kw, its RBF at "
                        + $"{RbfFeerate(initRbf)} sat/kw");
        Assert.Equal(BumpFeeRatePerKw, RbfFeerate(initRbf));
        Assert.Equal((long)SpliceInSat, InitRbfContribution(initRbf));
        Assert.Equal(0, AckRbfContribution(ackRbf) ?? 0);
        Assert.True(initRbf.Sequence < ackRbf.Sequence);
        var theirStfu = wire.First(inbound: true, MessageTypes.Stfu, rbfFrom);
        Assert.True(theirStfu.StfuInitiator && theirStfu.Sequence < initRbf.Sequence,
                    "CLN was not the quiescence initiator of its RBF");
        var signaturesAt = await AssertSignedAttemptAsync(wire, rbfFrom, bumpTxId, ct);
        var bumpTx = await AssertSpliceTransactionAsync(before, bumpTxId, expectedCapacity, ct);
        await AssertReplacedAsync(firstTxId, bumpTxId, ct);
        Assert.True(await GetFeeAsync(bumpTxId, ct) > await GetFeeAsync(firstTx, ct),
                    "CLN's RBF pays no more than its first attempt");
        await AssertPendingAttemptsAsync(session, [firstTxId, bumpTxId], ct);

        // ...payments while pending, the lock of the bump; our balance only moved by the payments
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(20_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(10_000), ct);
        var locked = await MineUntilLockedAsync(session, wire, before, bumpTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat - 20_000_000 + 10_000_000, locked.LocalBalanceMsat);
        await AssertNeverConfirmsAsync(before, firstTx, bumpTx, ct);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, locked.LockedAtSequence,
                                  [before.FundingTxId, firstTxId, bumpTxId]);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Proof SPR (c): CLN splices in, then calls <c>splicein</c> again while the splice is pending. CLN sends
    /// <c>tx_init_rbf</c> at its estimate, the feerate of the attempt it would replace; BOLT 2 (IT-RBF-01) makes us
    /// answer <c>tx_abort</c> (never <c>tx_ack_rbf</c>); CLN's call fails, the first attempt stays the only pending one on
    /// both ends and in bitcoind's mempool, and it locks.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ClnsPendingSplice_When_ClnRbfsAtTheSameFeerate_Then_WeAbortAndTheFirstAttemptLocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-rbf-c", ct);
        await _fixture.FundClnWalletAsync(LightningMoney.Satoshis(300_000), [session.Node], ct);
        await _fixture.FundClnWalletAsync(LightningMoney.Satoshis(300_000), [session.Node], ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        var spliced = await session.Cln.CallAsync("splicein", ct, ("channel", session.ChannelIdHex),
                                                  ("amount", SpliceInSat));
        var firstTxId = uint256.Parse(spliced["txid"]!.GetValue<string>());
        await AssertSignedAttemptAsync(wire, from, firstTxId, ct);
        var firstInit = wire.First(inbound: true, MessageTypes.SpliceInit, from);
        var expectedCapacity = before.CapacitySat + SpliceInSat;
        await AssertPendingAttemptsAsync(session, [firstTxId], ct);
        await Task.Delay(s_minRbfInterval + TimeSpan.FromSeconds(1), ct);
        var rbfFrom = wire.CurrentSequence;

        // Act
        ClnRpcException? refused = null;
        try
        {
            var second = await session.Cln.CallAsync("splicein", ct, ("channel", session.ChannelIdHex),
                                                     ("amount", 50_000));
            Console.WriteLine($"[cln] second splicein returned {second.ToJsonString()}");
        }
        catch (ClnRpcException e)
        {
            refused = e;
            Console.WriteLine($"[cln] second splicein refused: {e.Message}");
        }

        // Assert: CLN's tx_init_rbf did not raise the feerate; we answered tx_abort, never tx_ack_rbf
        var initRbf = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxInitRbf, rbfFrom),
                                          s_stepTimeout, "CLN's tx_init_rbf", ct);
        Console.WriteLine($"[proof] CLN's RBF at {RbfFeerate(initRbf)} sat/kw after {firstInit.SpliceFeeratePerKw}");
        Assert.True(RbfFeerate(initRbf) < GetMinimumNextFeerate(firstInit.SpliceFeeratePerKw),
                    $"CLN's RBF at {RbfFeerate(initRbf)} sat/kw is a valid bump of {firstInit.SpliceFeeratePerKw}; "
                  + "this case needs CLN's same-feerate RBF");
        var abort = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxAbort, rbfFrom),
                                        s_stepTimeout, "our tx_abort", ct);
        Assert.True(initRbf.Sequence < abort.Sequence);
        // ...and for the feerate rule, not for another reason (MinRbfInterval, attempt count, a refused initiator)
        var abortReason = TxAbortData(abort.Wire);
        Console.WriteLine($"[proof] our tx_abort: {abortReason}");
        Assert.Contains(FeerateRefusal(RbfFeerate(initRbf), firstInit.SpliceFeeratePerKw), abortReason,
                        StringComparison.Ordinal);
        Assert.Null(wire.FirstOrDefault(inbound: false, MessageTypes.TxAckRbf, rbfFrom));
        Assert.DoesNotContain(wire.Snapshot(), m => m.Sequence > rbfFrom
                                                 && m.Type == (ushort)MessageTypes.CommitmentSigned);
        Assert.NotNull(refused);

        // ...the first attempt is the only pending one and still in the mempool; it locks
        await WaitNoHtlcsAsync(session, ct);
        await AssertPendingAttemptsAsync(session, [firstTxId], ct);
        Assert.Contains(firstTxId, await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct));
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(20_000), ct);
        var locked = await MineUntilLockedAsync(session, wire, before, firstTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat - 20_000_000, locked.LocalBalanceMsat);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(11_000), ct);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Proof SPR (d): our splice-in at 253 sat/kw bumped twice (600, then 1,000 sat/kw): three attempts pending on both
    /// ends. Three payments each way: every commitment update of each side is <c>start_batch(batch_size = 4,
    /// message_type = 132)</c> followed by four <c>commitment_signed</c>, one per active funding (the current funding
    /// and the three attempts, SP-OP-03/05), each batch answered by one <c>revoke_and_ack</c> (SP-OP-07). The last
    /// attempt locks; the other two never confirm; after the lock single <c>commitment_signed</c> again.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ThreePendingAttempts_When_PayingBothWays_Then_EveryUpdateIsABatchOfFour()
    {
        // Arrange: three attempts of our splice-in
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-rbf-d", ct);
        var before = await SnapshotAsync(session, ct);
        var expectedCapacity = before.CapacitySat + SpliceInSat;
        var from = wire.CurrentSequence;
        var firstTxId = AssertSignedResponse(await SpliceInAsync(session, SpliceInSat, LowFeeRatePerKw, ct));
        await AssertSignedAttemptAsync(wire, from, firstTxId, ct);
        var firstTx = await AssertSpliceTransactionAsync(before, firstTxId, expectedCapacity, ct);
        List<uint256> attempts = [firstTxId];
        List<Transaction> replaced = [firstTx];
        var lastAt = 0L;
        foreach (var feerate in new[] { MidFeeRatePerKw, BumpFeeRatePerKw })
        {
            await Task.Delay(s_minRbfInterval + TimeSpan.FromSeconds(1), ct);
            var rbfFrom = wire.CurrentSequence;
            var bumpTxId = AssertSignedResponse(await BumpAsync(session, feerate, ct));
            Assert.Equal(feerate, RbfFeerate(wire.First(inbound: false, MessageTypes.TxInitRbf, rbfFrom)));
            lastAt = await AssertSignedAttemptAsync(wire, rbfFrom, bumpTxId, ct);
            var bumpTx = await AssertSpliceTransactionAsync(before, bumpTxId, expectedCapacity, ct);
            await AssertReplacedAsync(attempts[^1], bumpTxId, ct);
            attempts.Add(bumpTxId);
            replaced.Add(bumpTx);
        }

        await AssertPendingAttemptsAsync(session, attempts, ct);
        var tip = await _fixture.Bitcoin.Rpc.GetBlockCountAsync(ct);

        // Act: payments each way while the three attempts are pending (nothing mined)
        for (var i = 0; i < PendingPayments; i++)
        {
            await AssertWePayClnAsync(session, LightningMoney.Satoshis(10_000 + i * 1_000), ct);
            await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(5_000 + i * 1_000), ct);
        }

        var pendingEnd = wire.CurrentSequence;
        Assert.Equal(tip, await _fixture.Bitcoin.Rpc.GetBlockCountAsync(ct));

        // Assert: batches of four both ways, one revoke_and_ack each
        var (ourBatches, theirBatches) =
            AssertBatchesWhilePending(wire, lastAt, pendingEnd, [before.FundingTxId, .. attempts]);
        Assert.True(ourBatches >= 2 * PendingPayments,
                    $"only {ourBatches} batches of ours for {2 * PendingPayments} payments");
        Assert.True(theirBatches >= 2 * PendingPayments,
                    $"only {theirBatches} batches of CLN's for {2 * PendingPayments} payments");

        // ...the last attempt locks, the others never confirm, single commitment_signed after the lock
        var lastTxId = attempts[^1];
        var locked = await MineUntilLockedAsync(session, wire, before, lastTxId, expectedCapacity, ct);
        foreach (var loser in replaced.Take(replaced.Count - 1))
            await AssertNeverConfirmsAsync(before, loser, replaced[^1], ct);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(11_000), ct);
        var afterLock = wire.Snapshot().Where(m => m.Sequence > locked.LockedAtSequence).ToList();
        Assert.DoesNotContain(afterLock, m => m.Type == (ushort)MessageTypes.StartBatch);
        var signed = afterLock.Where(m => m.Type == (ushort)MessageTypes.CommitmentSigned).ToList();
        Assert.Contains(signed, m => !m.Inbound);
        Assert.Contains(signed, m => m.Inbound);
        Assert.All(signed, m => Assert.Equal(lastTxId, m.CommitmentFundingTxId));
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Proof SPR (e), SP-RE with RBF: our splice-in bumped once, both attempts signed and pending. CLN drops the
    /// connection (<c>disconnect force=true</c>): our reconnection's <c>channel_reestablish</c> has no
    /// <c>next_funding</c> (nothing is left to sign) and <c>my_current_funding_locked</c> = the current funding; both
    /// attempts are still pending on both ends. Then our node restarts on its database: the same, both attempts are
    /// reloaded (the batch after the restart still carries three <c>commitment_signed</c>), and the bumped attempt locks.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ABumpedSplicePending_When_TheLinkDropsAndOurNodeRestarts_Then_BothAttemptsSurviveAndTheBumpLocks()
    {
        // Arrange: our splice-in and one bump, both signed
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-rbf-e", ct);
        var before = await SnapshotAsync(session, ct);
        var expectedCapacity = before.CapacitySat + SpliceInSat;
        var from = wire.CurrentSequence;
        var firstTxId = AssertSignedResponse(await SpliceInAsync(session, SpliceInSat, LowFeeRatePerKw, ct));
        await AssertSignedAttemptAsync(wire, from, firstTxId, ct);
        var firstTx = await AssertSpliceTransactionAsync(before, firstTxId, expectedCapacity, ct);
        await Task.Delay(s_minRbfInterval + TimeSpan.FromSeconds(1), ct);
        var rbfFrom = wire.CurrentSequence;
        var bumpTxId = AssertSignedResponse(await BumpAsync(session, BumpFeeRatePerKw, ct));
        await AssertSignedAttemptAsync(wire, rbfFrom, bumpTxId, ct);
        var bumpTx = await AssertSpliceTransactionAsync(before, bumpTxId, expectedCapacity, ct);
        await AssertReplacedAsync(firstTxId, bumpTxId, ct);
        await AssertPendingAttemptsAsync(session, [firstTxId, bumpTxId], ct);

        // Act (1): CLN drops the link; our node reconnects
        var cutAt = wire.CurrentSequence;
        await session.Cln.CallAsync("disconnect", ct, ("id", session.Node.NodeIdHex), ("force", true));
        await WaitReestablishedAsync(session, wire, cutAt, ct);

        // Assert (1): nothing to sign again, both attempts still pending
        AssertReestablishWithoutNextFunding(wire, cutAt, before.FundingTxId);
        await AssertPendingAttemptsAsync(session, [firstTxId, bumpTxId], ct);
        Assert.Contains(bumpTxId, await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct));

        // Act (2): our node restarts on its database
        var stoppedAt = wire.CurrentSequence;
        await session.StopNodeAsync();
        await session.StartNodeAsync(ct);
        await WaitReestablishedAsync(session, wire, stoppedAt, ct);

        // Assert (2): the same after the restart, and the reloaded engine signs for all three fundings
        AssertReestablishWithoutNextFunding(wire, stoppedAt, before.FundingTxId);
        await AssertPendingAttemptsAsync(session, [firstTxId, bumpTxId], ct);
        var payFrom = wire.CurrentSequence;
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(20_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(10_000), ct);
        var (ours, theirs) = AssertBatchesWhilePending(wire, payFrom, wire.CurrentSequence,
                                                       [before.FundingTxId, firstTxId, bumpTxId]);
        Assert.True(ours >= 2 && theirs >= 2, $"{ours} batches of ours and {theirs} of CLN's after the restart");

        // ...the bumped attempt locks, the first never confirms
        var locked = await MineUntilLockedAsync(session, wire, before, bumpTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat + (long)SpliceInSat * 1_000 - 20_000_000 + 10_000_000,
                     locked.LocalBalanceMsat);
        await AssertNeverConfirmsAsync(before, firstTx, bumpTx, ct);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(21_000), ct);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Our node with the splice features, the splice IPC commands (<c>splicein</c>/<c>spliceout</c>/<c>bumpsplice</c>)
    /// and <see cref="s_minRbfInterval"/>, a channel we fund to CLN, the wire recorder.
    /// </summary>
    private async Task<(ClnChannelSession Session, SpliceWireRecorder Wire)> BuildAsync(string nodeName,
                                                                                         CancellationToken ct)
    {
        var wire = new SpliceWireRecorder();
        _wire = wire;
        _session = await ClnChannelSession.BuildOurFundedAsync(
                       _fixture, nodeName, s_capacity, s_push, ct,
                       node => node.ConfigureServices = services =>
                       {
                           services.PostConfigure<NodeOptions>(o =>
                           {
                               o.Features.AllowExperimentalFeatures = true;
                               o.Features.OptionQuiesce = FeatureSupport.Optional;
                               o.Features.OptionSplice = FeatureSupport.Optional;
                               o.Features.DualFund = FeatureSupport.Optional;
                           });
                           services.PostConfigure<SpliceOptions>(o => o.MinRbfInterval = s_minRbfInterval);
                           // Idempotent: the integrator also registers it in AddNltgNodeServices
                           services.AddSpliceIpcServices();
                           wire.Install(services);
                       });

        foreach (var feature in new[] { Feature.OptionQuiesce, Feature.OptionSplice })
            Assert.True(_session.Node.PeerManager.GetPeer(_session.ClnPubKey)!.Features.IsFeatureSet(feature),
                        $"CLN does not advertise {feature}");

        return (_session, wire);
    }

    private static async Task<SpliceClientResponse> SpliceInAsync(ClnChannelSession session, ulong amountSat,
                                                                  uint feeRatePerKw, CancellationToken ct)
    {
        var response = await HandleAsync<SpliceInClientRequest, SpliceClientResponse>(
                           session, new SpliceInClientRequest(session.ChannelId, amountSat)
                           {
                               FeeRatePerKw = feeRatePerKw
                           }, ct);
        Console.WriteLine($"[nltg] splicein {amountSat} at {feeRatePerKw} sat/kw: {response.State}, txid "
                        + $"{response.SpliceTxId}, capacity {response.NewCapacitySat}, reason {response.FailureReason}");
        return response;
    }

    /// <summary>Our <c>bumpsplice</c> through the daemon's client handler (lane SPR-B).</summary>
    private static async Task<SpliceClientResponse> BumpAsync(ClnChannelSession session, uint feeRatePerKw,
                                                              CancellationToken ct)
    {
        var response = await HandleAsync<BumpSpliceClientRequest, SpliceClientResponse>(
                           session, new BumpSpliceClientRequest(session.ChannelId, feeRatePerKw), ct);
        Console.WriteLine($"[nltg] bumpsplice at {feeRatePerKw} sat/kw: {response.State}, txid {response.SpliceTxId}, "
                        + $"capacity {response.NewCapacitySat}, reason {response.FailureReason}");
        return response;
    }

    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(ClnChannelSession session, TRequest request,
                                                                          CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, ct);
    }

    private static uint256 AssertSignedResponse(SpliceClientResponse response)
    {
        Assert.True(response.State == SpliceNegotiationState.Signed,
                    $"the splice is {response.State}: {response.FailureReason}");
        Assert.Null(response.FailureReason);
        Assert.NotNull(response.SpliceTxId);
        return new uint256((byte[])response.SpliceTxId.Value);
    }

    /// <summary>
    /// CLN RBFs its pending splice at <paramref name="feeRatePerKw"/> with the low-level flow (see the class remarks):
    /// <c>utxopsbt</c> on one confirmed, unreserved wallet output that no attempt in <paramref name="attempts"/>
    /// created, <c>splice_init</c> with 4 x the feerate and <c>force_feerate</c>, <c>splice_update</c> until
    /// <c>commitments_secured</c>, <c>signpsbt</c>, <c>splice_signed</c>.
    /// </summary>
    /// <returns>The new attempt's txid.</returns>
    private static async Task<uint256> ClnBumpAsync(ClnChannelSession session, long relativeAmountSat,
                                                    uint feeRatePerKw, IReadOnlyCollection<uint256> attempts,
                                                    CancellationToken ct)
    {
        var cln = session.Cln;
        var attemptIds = attempts.Select(a => a.ToString()).ToHashSet();
        var outputs = (await cln.CallAsync("listfunds", ct))["outputs"]!.AsArray();
        var utxo = outputs.FirstOrDefault(o => o?["status"]?.GetValue<string>() == "confirmed"
                                            && o["reserved"]?.GetValue<bool>() != true
                                            && !attemptIds.Contains(o["txid"]!.GetValue<string>())
                                            && o["amount_msat"]!.GetValue<long>()
                                             > (relativeAmountSat + 50_000) * 1_000)
                ?? throw new InvalidOperationException("CLN has no free confirmed wallet output for its RBF: "
                                                     + outputs.ToJsonString());
        var outpoint = $"{utxo["txid"]!.GetValue<string>()}:{utxo["output"]!.GetValue<long>()}";
        var psbt = (await cln.CallAsync("utxopsbt", ct, ("satoshi", relativeAmountSat),
                                        ("feerate", $"{feeRatePerKw}perkw"), ("startweight", ClnRbfStartWeight),
                                        ("utxos", new JsonArray(outpoint)), ("excess_as_change", true)))["psbt"]!
           .GetValue<string>();
        var init = await cln.CallAsync("splice_init", ct, ("channel_id", session.ChannelIdHex),
                                       ("relative_amount", relativeAmountSat), ("initialpsbt", psbt),
                                       ("feerate_per_kw", feeRatePerKw * ClnSpliceInitFeerateFactor),
                                       ("force_feerate", true));
        psbt = init["psbt"]!.GetValue<string>();
        for (var i = 0; ; i++)
        {
            var update = await cln.CallAsync("splice_update", ct, ("channel_id", session.ChannelIdHex),
                                             ("psbt", psbt));
            psbt = update["psbt"]!.GetValue<string>();
            if (update["commitments_secured"]?.GetValue<bool>() == true)
                break;

            Assert.True(i < 10, "CLN's splice_update never reported commitments_secured");
        }

        var signedPsbt = (await cln.CallAsync("signpsbt", ct, ("psbt", psbt)))["signed_psbt"]!.GetValue<string>();
        var signed = await cln.CallAsync("splice_signed", ct, ("channel_id", session.ChannelIdHex),
                                         ("psbt", signedPsbt));
        var txId = uint256.Parse(signed["txid"]!.GetValue<string>());
        Console.WriteLine($"[cln] RBF of its splice at {feeRatePerKw} sat/kw (splice_init feerate_per_kw "
                        + $"{feeRatePerKw * ClnSpliceInitFeerateFactor}, input {outpoint}): txid {txId}");
        return txId;
    }

    /// <summary>
    /// The interactive part of one attempt from <paramref name="from"/> (SP-CS-01/02, SP-SIG-01): the first
    /// <c>commitment_signed</c> each way is for the attempt's funding and comes before any <c>tx_signatures</c>, no
    /// <c>revoke_and_ack</c> in between, both <c>tx_signatures</c> for the attempt's txid with a
    /// <c>shared_input_signature</c>, no <c>tx_abort</c>; the attempt is in bitcoind's mempool. Returns the sequence of
    /// the later <c>tx_signatures</c>.
    /// </summary>
    private async Task<long> AssertSignedAttemptAsync(SpliceWireRecorder wire, long from, uint256 txId,
                                                      CancellationToken ct)
    {
        var ourSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxSignatures, from),
                                          s_stepTimeout, "our tx_signatures", ct);
        var theirSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxSignatures, from),
                                            s_stepTimeout, "CLN's tx_signatures", ct);
        foreach (var sigs in new[] { ourSigs, theirSigs })
        {
            Assert.Equal(txId, sigs.TxSignaturesTxId);
            Assert.True(sigs.HasSharedInputSignature,
                        $"{(sigs.Inbound ? "CLN's" : "our")} tx_signatures has no shared_input_signature");
        }

        var first = Math.Min(ourSigs.Sequence, theirSigs.Sequence);
        var ourCs = wire.First(inbound: false, MessageTypes.CommitmentSigned, from);
        var theirCs = wire.First(inbound: true, MessageTypes.CommitmentSigned, from);
        Assert.True(ourCs.Sequence < first && theirCs.Sequence < first,
                    "a tx_signatures went out before both commitment_signed of the attempt");
        Assert.Equal(txId, ourCs.CommitmentFundingTxId);
        Assert.Equal(txId, theirCs.CommitmentFundingTxId);
        Assert.DoesNotContain(wire.Snapshot(), m => m.Type == (ushort)MessageTypes.RevokeAndAck
                                                 && m.Sequence > from && m.Sequence < first);
        Assert.DoesNotContain(wire.Snapshot(), m => m.Type == (ushort)MessageTypes.TxAbort && m.Sequence > from);
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct)).Contains(txId),
                              s_stepTimeout, $"the attempt {txId} in bitcoind's mempool", ct);
        return Math.Max(ourSigs.Sequence, theirSigs.Sequence);
    }

    /// <summary>
    /// The attempt spends the channel's current funding output with a 2-of-2 witness and has exactly one P2WSH output
    /// of <paramref name="expectedCapacitySat"/>: every attempt spends the same output, so they double-spend each
    /// other (BOLT 2 splicing rationale).
    /// </summary>
    private async Task<Transaction> AssertSpliceTransactionAsync(ChannelSnapshot before, uint256 txId,
                                                                 ulong expectedCapacitySat, CancellationToken ct)
    {
        var tx = await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(txId, true, ct);
        var shared = Assert.Single(tx.Inputs, i => i.PrevOut == before.FundingOutPoint);
        Assert.Equal(4, shared.WitScript.PushCount);
        Assert.Single(tx.Outputs, o => o.ScriptPubKey.IsScriptType(ScriptType.P2WSH)
                                    && o.Value == Money.Satoshis((long)expectedCapacitySat));
        Console.WriteLine($"[proof] attempt {txId}: {tx.Inputs.Count} inputs, {tx.Outputs.Count} outputs, fee "
                        + $"{await GetFeeAsync(tx, ct)} sat, weight {await GetWeightAsync(txId, ct)}");
        return tx;
    }

    /// <summary>bitcoind's mempool holds <paramref name="bumpTxId"/> and no longer <paramref name="replacedTxId"/>.</summary>
    private async Task AssertReplacedAsync(uint256 replacedTxId, uint256 bumpTxId, CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var mempool = await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct);
            return mempool.Contains(bumpTxId) && !mempool.Contains(replacedTxId);
        }, s_stepTimeout, $"{bumpTxId} replaced {replacedTxId} in bitcoind's mempool", ct);
    }

    /// <summary>
    /// Our attempt pays <paramref name="feeRatePerKw"/> for its weight within one vbyte (IT-S-03, as proof SP1 (f): we
    /// are the initiator and CLN adds nothing; plus the 2 WU per ECDSA signature by which the maximum-witness budget
    /// may exceed a low-R signature).
    /// </summary>
    private async Task AssertOurFeerateAsync(uint256 txId, uint feeRatePerKw, CancellationToken ct)
    {
        var tx = await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(txId, true, ct);
        var fee = await GetFeeAsync(tx, ct);
        var weight = await GetWeightAsync(txId, ct);
        var ecdsaSignatures = tx.Inputs.SelectMany(i => i.WitScript.Pushes)
                                .Count(p => p.Length is >= 9 and <= 73 && p[0] == 0x30);
        var floor = feeRatePerKw * weight / 1_000;
        var ceiling = (feeRatePerKw * (weight + 4 + 2 * (ulong)ecdsaSignatures) + 999) / 1_000;
        Console.WriteLine($"[proof] {txId}: fee {fee} sat for {weight} WU ({fee * 1_000.0 / weight:F1} sat/kw)");
        Assert.True(fee + 1 >= floor, $"the attempt pays {fee} sat, less than {floor} sat at {feeRatePerKw} sat/kw");
        Assert.True(fee <= ceiling + 1, $"the attempt pays {fee} sat, more than one vbyte above {floor} sat");
    }

    /// <summary>
    /// <paramref name="loser"/> can never confirm: it spends the same funding output as the confirmed
    /// <paramref name="winner"/>, which spent it, and bitcoind does not know it (not in a block nor the mempool).
    /// </summary>
    private async Task AssertNeverConfirmsAsync(ChannelSnapshot before, Transaction loser, Transaction winner,
                                                CancellationToken ct)
    {
        var rpc = _fixture.Bitcoin.Rpc;
        Assert.Contains(loser.Inputs, i => i.PrevOut == before.FundingOutPoint);
        Assert.Contains(winner.Inputs, i => i.PrevOut == before.FundingOutPoint);
        var info = await rpc.GetRawTransactionInfoAsync(winner.GetHash(), ct);
        Assert.True(info.Confirmations >= 1, $"the locked attempt {winner.GetHash()} is not confirmed");
        Assert.Null(await rpc.GetTxOutAsync(before.FundingOutPoint.Hash, (int)before.FundingOutPoint.N,
                                            cancellationToken: ct));
        Assert.DoesNotContain(loser.GetHash(), await rpc.GetRawMempoolAsync(ct));
        try
        {
            var loserInfo = await rpc.GetRawTransactionInfoAsync(loser.GetHash(), ct);
            Assert.Fail($"bitcoind knows the replaced attempt {loser.GetHash()} ({loserInfo.Confirmations} "
                      + "confirmations)");
        }
        catch (RPCException)
        {
            Console.WriteLine($"[proof] the replaced attempt {loser.GetHash()} never confirmed");
        }
    }

    /// <summary>
    /// Both ends list exactly <paramref name="attempts"/> pending: CLN one <c>inflight</c> entry per attempt, our
    /// <c>listchannels</c> one <c>Pending</c> funding per attempt (the first a <c>Splice</c>, the others
    /// <c>SpliceRbf</c>).
    /// </summary>
    private static async Task AssertPendingAttemptsAsync(ClnChannelSession session, IReadOnlyList<uint256> attempts,
                                                         CancellationToken ct)
    {
        var expected = attempts.Select(a => a.ToString()).ToHashSet();
        var cln = await Poll.ForAsync(async () =>
        {
            var c = await session.GetClnChannelAsync(ct);
            var inflight = c["inflight"]?.AsArray().Select(i => i!["funding_txid"]!.GetValue<string>()).ToHashSet()
                        ?? [];
            return inflight.SetEquals(expected) ? c : null;
        }, s_stepTimeout, $"CLN's inflight = {string.Join(", ", expected)}", ct);
        Console.WriteLine($"[cln] pending: {ClnChannelSession.DescribeCln(cln)} inflight "
                        + cln["inflight"]!.ToJsonString());
        Assert.Equal("CHANNELD_AWAITING_SPLICE", cln["state"]!.GetValue<string>());

        var ours = await Poll.ForAsync(async () =>
        {
            var channel = await session.GetOurChannelAsync(ct);
            var pending = channel.Fundings.Where(f => f.Status == ChannelFundingStatus.Pending)
                                 .Select(f => new uint256((byte[])f.FundingTxId).ToString()).ToHashSet();
            return pending.SetEquals(expected) ? channel : null;
        }, s_stepTimeout, $"our pending fundings = {string.Join(", ", expected)}", ct);
        foreach (var funding in ours.Fundings.Where(f => f.Status == ChannelFundingStatus.Pending))
        {
            var txId = new uint256((byte[])funding.FundingTxId);
            Assert.Equal(txId == attempts[0] ? ChannelFundingKind.Splice : ChannelFundingKind.SpliceRbf, funding.Kind);
        }
    }

    /// <summary>
    /// Mines one block at a time (no HTLC in flight) until both ends locked <paramref name="lockTxId"/>: CLN is
    /// <c>CHANNELD_NORMAL</c> on it with the new capacity and no <c>inflight</c>, both sent <c>splice_locked</c> for it,
    /// our <c>listchannels</c> has it as the current funding with no pending one left, and the short channel ids agree.
    /// </summary>
    private async Task<LockedChannel> MineUntilLockedAsync(ClnChannelSession session, SpliceWireRecorder wire,
                                                           ChannelSnapshot before, uint256 lockTxId,
                                                           ulong expectedCapacitySat, CancellationToken ct)
    {
        var from = wire.CurrentSequence;
        for (var block = 1; ; block++)
        {
            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            var cln = await session.GetClnChannelAsync(ct);
            var ours = await session.GetOurChannelAsync(ct);
            Console.WriteLine($"[proof] +{block} block(s): ours {ours.Describe()}; cln "
                            + ClnChannelSession.DescribeCln(cln));
            if (cln["state"]?.GetValue<string>() == "CHANNELD_NORMAL"
             && cln["funding_txid"]?.GetValue<string>() == lockTxId.ToString()
             && ours.FundingTxId is { } funding && new uint256((byte[])funding) == lockTxId)
                break;

            Assert.True(block < MaxLockBlocks, $"{lockTxId} was not locked by both ends in {MaxLockBlocks} blocks");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        var ourLocked = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.SpliceLocked, from),
                                            s_stepTimeout, "our splice_locked", ct);
        var theirLocked = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.SpliceLocked, from),
                                              s_stepTimeout, "CLN's splice_locked", ct);
        Assert.Equal(lockTxId, ourLocked.SpliceLockedTxId);
        Assert.Equal(lockTxId, theirLocked.SpliceLockedTxId);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);

        var channel = await session.GetOurChannelAsync(ct);
        var clnChannel = await session.GetClnChannelAsync(ct);
        Assert.Equal((long)expectedCapacitySat, channel.Capacity.Satoshi);
        Assert.Equal((long)expectedCapacitySat * 1_000, clnChannel["total_msat"]!.GetValue<long>());
        Assert.True(clnChannel["inflight"] is null || clnChannel["inflight"]!.AsArray().Count == 0,
                    $"CLN still lists inflight attempts: {clnChannel["inflight"]?.ToJsonString()}");
        Assert.DoesNotContain(channel.Fundings, f => f.Status == ChannelFundingStatus.Pending);
        Assert.Contains(channel.Fundings, f => f.Status == ChannelFundingStatus.Current
                                            && new uint256((byte[])f.FundingTxId) == lockTxId);
        Assert.NotNull(channel.ShortChannelId);
        var clnScid = clnChannel["short_channel_id"]!.GetValue<string>();
        Assert.NotEqual(before.ShortChannelId, clnScid);
        Assert.Equal(ParseScid(clnScid), channel.ShortChannelId.Value.ToUInt64());
        return new LockedChannel((long)channel.LocalBalance.MilliSatoshi,
                                 Math.Max(ourLocked.Sequence, theirLocked.Sequence));
    }

    /// <summary>
    /// SP-OP-03/05/07 between <paramref name="from"/> and <paramref name="to"/> with several attempts pending: every
    /// <c>commitment_signed</c> of each side is in a batch (<c>start_batch</c> with <c>batch_size</c> = the number of
    /// <paramref name="fundings"/> and <c>message_type</c> 132, then that many <c>commitment_signed</c> of that side,
    /// nothing of that side in between, whose <c>funding_txid</c>s are exactly <paramref name="fundings"/>), and each
    /// batch is answered by one <c>revoke_and_ack</c>.
    /// </summary>
    /// <returns>How many batches each side sent.</returns>
    private static (int Ours, int Theirs) AssertBatchesWhilePending(SpliceWireRecorder wire, long from, long to,
                                                                    IReadOnlyList<uint256> fundings)
    {
        var expected = fundings.ToHashSet();
        var window = wire.Snapshot().Where(m => m.Sequence > from && m.Sequence < to && !m.IsPingOrPong).ToList();
        var counts = new int[2];
        foreach (var inbound in new[] { false, true })
        {
            var side = window.Where(m => m.Inbound == inbound).ToList();
            var who = inbound ? "CLN" : "we";
            var batches = 0;
            for (var i = 0; i < side.Count; i++)
            {
                if (side[i].Type == (ushort)MessageTypes.CommitmentSigned)
                    Assert.Fail($"{who} sent a commitment_signed outside a batch while {fundings.Count - 1} attempts "
                              + $"were pending (#{side[i].Sequence})");
                if (side[i].Type != (ushort)MessageTypes.StartBatch)
                    continue;

                Assert.Equal(fundings.Count, side[i].StartBatchSize);
                Assert.Equal((ushort)MessageTypes.CommitmentSigned, side[i].StartBatchMessageType);
                Assert.True(i + fundings.Count < side.Count,
                            $"{who}: start_batch #{side[i].Sequence} is not followed by {fundings.Count} messages");
                var batch = side.Skip(i + 1).Take(fundings.Count).ToList();
                Assert.True(batch.All(m => m.Type == (ushort)MessageTypes.CommitmentSigned),
                            $"{who}: start_batch #{side[i].Sequence} is not followed by {fundings.Count} "
                          + "commitment_signed");
                Assert.True(expected.SetEquals(batch.Select(m => m.CommitmentFundingTxId!)),
                            $"{who}: batch #{side[i].Sequence} signs for "
                          + $"{string.Join(", ", batch.Select(m => m.CommitmentFundingTxId))}");
                batches++;
                i += fundings.Count;
            }

            var revokes = window.Count(m => m.Inbound != inbound && m.Type == (ushort)MessageTypes.RevokeAndAck);
            Assert.True(revokes == batches,
                        $"{batches} batches from {who} answered by {revokes} revoke_and_ack (one per batch)");
            counts[inbound ? 1 : 0] = batches;
        }

        Console.WriteLine($"[proof] {fundings.Count} active fundings: {counts[0]} batches of ours, {counts[1]} of CLN's");
        return (counts[0], counts[1]);
    }

    /// <summary>
    /// After <paramref name="from"/>: our node sent <c>channel_reestablish</c> and got CLN's, and the channel is usable
    /// on our side (reestablished) with CLN connected.
    /// </summary>
    private static async Task WaitReestablishedAsync(ClnChannelSession session, SpliceWireRecorder wire, long from,
                                                     CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            if (!session.Node.IsRunning
             || wire.FirstOrDefault(inbound: false, MessageTypes.ChannelReestablish, from) is null
             || wire.FirstOrDefault(inbound: true, MessageTypes.ChannelReestablish, from) is null)
                return false;

            var ours = await session.GetOurChannelAsync(ct);
            var cln = await session.GetClnChannelAsync(ct);
            return ours.IsUsable() && cln["peer_connected"]?.GetValue<bool>() == true;
        }, s_stepTimeout, "the channel reestablished on both ends", ct, TimeSpan.FromMilliseconds(500));
    }

    /// <summary>
    /// Our <c>channel_reestablish</c> after <paramref name="from"/> has no <c>next_funding</c> (TLV 1: both attempts
    /// are signed both ways, SP-RE-01) and <c>my_current_funding_locked</c> (TLV 5) names the current funding (neither
    /// attempt is locked, SP-RE-02).
    /// </summary>
    private static void AssertReestablishWithoutNextFunding(SpliceWireRecorder wire, long from, uint256 currentFunding)
    {
        var ours = wire.First(inbound: false, MessageTypes.ChannelReestablish, from);
        var theirs = wire.First(inbound: true, MessageTypes.ChannelReestablish, from);
        // type (2), channel_id (32), next_commitment_number (8), next_revocation_number (8),
        // your_last_per_commitment_secret (32), my_current_per_commitment_point (33)
        const int tlvOffset = 2 + 32 + 8 + 8 + 32 + 33;
        Console.WriteLine($"[proof] our channel_reestablish: {ours.Hex[(tlvOffset * 2)..]}; CLN's: "
                        + theirs.Hex[(tlvOffset * 2)..]);
        Assert.Null(FindTlv(ours.Wire, tlvOffset, 1));
        var locked = FindTlv(ours.Wire, tlvOffset, 5);
        Assert.NotNull(locked);
        Assert.Equal(currentFunding, new uint256(locked.AsSpan(0, 32).ToArray()));
        Assert.Null(FindTlv(theirs.Wire, tlvOffset, 1));
    }

    private static void AssertWeWereQuiescenceInitiator(SpliceWireRecorder wire, long from, SpliceWireMessage initRbf)
    {
        var ourStfu = wire.First(inbound: false, MessageTypes.Stfu, from);
        var theirStfu = wire.First(inbound: true, MessageTypes.Stfu, from);
        Assert.True(ourStfu.StfuInitiator && !theirStfu.StfuInitiator, "we were not the quiescence initiator");
        Assert.True(theirStfu.Sequence < initRbf.Sequence);
    }

    /// <summary>
    /// IT-RBF-01: the lowest feerate an RBF of an attempt at <paramref name="previous"/> may have, max(floor(25/24 x
    /// previous), previous + 25) (BOLT 2 <c>tx_init_rbf</c>; the product rule).
    /// </summary>
    internal static uint GetMinimumNextFeerate(uint previous) => InteractiveTxRbfRules.GetMinimumNextFeerate(previous);

    /// <summary>
    /// The reason our node writes into the <c>tx_abort</c> that refuses a <c>tx_init_rbf</c> at
    /// <paramref name="feerate"/> after an attempt at <paramref name="previous"/> (IT-RBF-01; the splice rules and the
    /// interactive-tx driver both write it).
    /// </summary>
    internal static string FeerateRefusal(uint feerate, uint previous) =>
        $"feerate {feerate} sat/kw is below {GetMinimumNextFeerate(previous)} sat/kw";

    /// <summary><c>tx_abort</c>: <c>data</c> (u16 length after <c>channel_id</c>) as ASCII text.</summary>
    internal static string TxAbortData(byte[] wire)
    {
        const int lengthOffset = 2 + 32;
        var length = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(lengthOffset, 2));
        return System.Text.Encoding.ASCII.GetString(wire, lengthOffset + 2, length);
    }

    /// <summary><c>tx_init_rbf</c>: <c>feerate</c> (u32 after <c>channel_id</c> and <c>locktime</c>).</summary>
    private static uint RbfFeerate(SpliceWireMessage message) =>
        BinaryPrimitives.ReadUInt32BigEndian(message.Wire.AsSpan(2 + 32 + 4, 4));

    /// <summary><c>tx_init_rbf</c>: TLV 0 <c>funding_output_contribution</c> (s64), or null without it.</summary>
    private static long? InitRbfContribution(SpliceWireMessage message) =>
        ReadS64(FindTlv(message.Wire, 2 + 32 + 4 + 4, 0));

    /// <summary><c>tx_ack_rbf</c>: TLV 0 <c>funding_output_contribution</c> (s64), or null without it.</summary>
    private static long? AckRbfContribution(SpliceWireMessage message) => ReadS64(FindTlv(message.Wire, 2 + 32, 0));

    private static long? ReadS64(byte[]? value)
    {
        if (value is null)
            return null;

        Span<byte> padded = stackalloc byte[8];
        padded.Clear();
        if (value.Length > 0 && (value[0] & 0x80) != 0 && value.Length < 8)
            padded.Fill(0xff);
        value.CopyTo(padded[(8 - value.Length)..]);
        return BinaryPrimitives.ReadInt64BigEndian(padded);
    }

    /// <summary>The value of TLV <paramref name="type"/> in the stream at <paramref name="offset"/> of a message.</summary>
    private static byte[]? FindTlv(byte[] wire, int offset, ulong type)
    {
        while (offset < wire.Length)
        {
            var recordType = ReadBigSize(wire, ref offset);
            var length = (int)ReadBigSize(wire, ref offset);
            if (recordType == type)
                return wire.AsSpan(offset, length).ToArray();

            offset += length;
        }

        return null;
    }

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

    private async Task<ulong> GetFeeAsync(uint256 txId, CancellationToken ct) =>
        await GetFeeAsync(await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(txId, true, ct), ct);

    private async Task<ulong> GetFeeAsync(Transaction tx, CancellationToken ct)
    {
        var rpc = _fixture.Bitcoin.Rpc;
        var spent = Money.Zero;
        foreach (var input in tx.Inputs)
            spent += (await rpc.GetRawTransactionAsync(input.PrevOut.Hash, true, ct)).Outputs[input.PrevOut.N].Value;

        return (ulong)(spent - tx.TotalOut).Satoshi;
    }

    private async Task<ulong> GetWeightAsync(uint256 txId, CancellationToken ct)
    {
        var verbose = await _fixture.Bitcoin.Rpc.SendCommandAsync("getrawtransaction", ct, txId.ToString(), true);
        return (ulong)verbose.Result["weight"]!;
    }

    private static async Task<ChannelSnapshot> SnapshotAsync(ClnChannelSession session, CancellationToken ct)
    {
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var ours = await session.GetOurChannelAsync(ct);
        var cln = await session.GetClnChannelAsync(ct);
        Assert.NotNull(ours.FundingTxId);
        Assert.NotNull(ours.FundingOutputIndex);
        var funding = new uint256((byte[])ours.FundingTxId.Value);
        Assert.Equal(funding.ToString(), cln["funding_txid"]!.GetValue<string>());
        return new ChannelSnapshot(new OutPoint(funding, ours.FundingOutputIndex.Value),
                                   (ulong)ours.Capacity.Satoshi, (long)ours.LocalBalance.MilliSatoshi,
                                   cln["short_channel_id"]!.GetValue<string>());
    }

    private static ulong ParseScid(string scid)
    {
        var parts = scid.Split('x').Select(ulong.Parse).ToArray();
        return (parts[0] << 40) | (parts[1] << 16) | parts[2];
    }

    private static void AssertNoWarningOrError(SpliceWireRecorder wire)
    {
        var sent = wire.Snapshot().Where(m => m.Type is SpliceWireRecorder.WarningType
                                                     or SpliceWireRecorder.ErrorType).ToList();
        Assert.True(sent.Count == 0,
                    "warnings/errors on the wire: "
                  + string.Join("; ", sent.Select(m => $"{(m.Inbound ? "received" : "sent")} {m.Type} {m.Hex}")));
    }

    /// <summary>We pay a CLN invoice over the channel (pending attempts or not); neither end keeps an HTLC.</summary>
    private static async Task AssertWePayClnAsync(ClnChannelSession session, LightningMoney amount,
                                                  CancellationToken ct)
    {
        var label = $"nltg-rbf-pays-{Guid.NewGuid():N}";
        var invoice = await session.Cln.CallAsync("invoice", ct, ("amount_msat", (long)amount.MilliSatoshi),
                                                  ("label", label), ("description", "nltg pays cln"));
        var payment = await session.Node.PayInvoiceAsync(invoice["bolt11"]!.GetValue<string>(), ct);
        Console.WriteLine($"[cln] our payment of {amount.Satoshi} sat: {payment.Status}, failure "
                        + $"{payment.FailureCode}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        var listed = (await session.Cln.CallAsync("listinvoices", ct, ("label", label)))["invoices"]!.AsArray()
                                                                                             .Single()!;
        Assert.Equal("paid", listed["status"]!.GetValue<string>());
        await WaitNoHtlcsAsync(session, ct);
    }

    /// <summary>CLN pays our invoice (pending attempts or not); the invoice settles and neither end keeps an HTLC.</summary>
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
            Assert.Fail($"CLN could not pay our invoice of {amount.Satoshi} sat: {e.Message}");
            throw;
        }

        Assert.Equal("complete", result["status"]!.GetValue<string>());
        var preimage = Convert.FromHexString(result["payment_preimage"]!.GetValue<string>());
        Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(preimage));
        await Poll.UntilAsync(async () => (await session.Node.GetInvoiceAsync(invoice.PaymentHash, ct))?.Status
                                       == InvoiceStatus.Settled, s_settleTimeout, "our invoice settled", ct);
        await WaitNoHtlcsAsync(session, ct);
    }

    /// <summary>The channel before the splice: its funding output, capacity, our balance, CLN's short channel id.</summary>
    private sealed record ChannelSnapshot(OutPoint FundingOutPoint, ulong CapacitySat, long LocalBalanceMsat,
                                          string ShortChannelId)
    {
        public uint256 FundingTxId => FundingOutPoint.Hash;
    }

    /// <summary>The channel once both ends locked the splice.</summary>
    /// <param name="LocalBalanceMsat">Our balance then.</param>
    /// <param name="LockedAtSequence">The later of the two <c>splice_locked</c> in the recording.</param>
    private sealed record LockedChannel(long LocalBalanceMsat, long LockedAtSequence);

    /// <summary>
    /// Neither end has an HTLC (CLN may be <c>CHANNELD_AWAITING_SPLICE</c>, which <c>WaitUsableAsync</c> refuses).
    /// </summary>
    private static Task WaitNoHtlcsAsync(ClnChannelSession session, CancellationToken ct) =>
        Poll.UntilAsync(async () =>
        {
            var ours = await session.GetOurChannelAsync(ct);
            var cln = await session.GetClnChannelAsync(ct);
            return ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 } && cln["htlcs"]?.AsArray().Count == 0;
        }, s_settleTimeout, "no HTLC left on either end", ct, TimeSpan.FromMilliseconds(250));
}