using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Abcd;
using Daemon.Extensions;
using Daemon.Interfaces;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Client.Requests;
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
/// Proof SP1 of the splicing plan (<c>docs/agents/SPLICING_PLAN.md</c> §5 "Proof SP1", wave SP1 lane SP1-E, NL-021):
/// BOLT 2 "Channel Splicing" against Core Lightning v26.06.8, with our node on
/// <c>Features:AllowExperimentalFeatures</c>, <c>OptionQuiesce = Optional</c> and <c>OptionSplice = Optional</c> (both
/// stay experimental until Proof SP2, plan D13). (a) CLN splices in, (b) we splice in, (c) we splice out to an address,
/// (d) CLN splices out, (e) payments while the splice is pending (<c>start_batch</c> + one <c>commitment_signed</c> per
/// active funding, one <c>revoke_and_ack</c> per batch), (f) every splice transaction is a valid 2-of-2 spend of the
/// previous funding output, and the fee of ours matches the agreed feerate (IT-S-03) within one vbyte.
/// </summary>
/// <remarks>
/// <para><b>What CLN v26.06.8 offers (checked in wave sp1 lane SP1-E, 2026-09-27, on the pinned image with
/// <c>--developer</c>, two CLN nodes with an anchors channel between them; plan §10 questions Q2, Q4 and Q10):</b></para>
/// <list type="bullet">
/// <item><c>lightning-cli help</c> lists <c>splicein channel amount</c>, <c>spliceout channel amount [destination]
/// [force_feerate]</c>, <c>splice_init channel_id relative_amount [initialpsbt] [feerate_per_kw] [force_feerate]
/// [skip_stfu]</c>, <c>splice_update channel_id psbt</c>, <c>splice_signed psbt [channel_id] [sign_first]</c>,
/// <c>stfu_channels</c>, <c>abort_channels</c> and the developer commands <c>dev-splice [script_or_json] [dryrun]
/// [force_feerate] [debug_log] [dev-wetrun]</c> and <c>dev-quiesce id</c>; the image has no man pages. No dev flag
/// forces a disconnection at a splice step or delays <c>splice_locked</c> (Q10).</item>
/// <item><c>splicein &lt;channel_id&gt; &lt;sat&gt;</c> runs the whole splice in one call (<c>stfu</c>,
/// <c>splice_init</c>, CLN's wallet inputs, both signatures) and returns <c>{psbt, txid}</c> once the transaction is
/// signed and broadcast; CLN initiates at its <c>splice</c> feerate (253 sat/kw on the idle regtest; it logs
/// <c>Splice fee is 258sat at 258 perkw</c>). The low-level <c>splice_init</c> (with a <c>fundpsbt</c> PSBT) →
/// <c>splice_update</c> until <c>commitments_secured</c> → <c>signpsbt</c> → <c>splice_signed</c> works too; its
/// <c>feerate_per_kw</c> came out as a quarter on the wire (3000 → 750 perkw in <c>inflight</c>), and CLN's own
/// initiator refuses a feerate it finds high (code 360 "Feerate too high") without <c>force_feerate</c>. This proof
/// uses <c>splicein</c>/<c>spliceout</c> (Q10).</item>
/// <item><c>spliceout &lt;channel_id&gt; &lt;sat&gt;</c> without a destination pays CLN's own wallet, and CLN's
/// contribution includes its fee: <c>splice_amount</c> -50199 for 50000 sat (Q2: <c>funding_contribution_satoshis</c>
/// = -(amount + fee), CLN pays the whole fee as initiator). With a bitcoin address as destination CLN refuses
/// ("Dynamic bitcoin address amounts not supported for now"; <c>dev-splice</c>: "Paying out to bitcoin addresses not
/// supported for now"), so (d) pays CLN's wallet.</item>
/// <item>While the splice is pending CLN lists the channel <c>CHANNELD_AWAITING_SPLICE</c> with an <c>inflight</c>
/// entry (<c>funding_txid</c>, <c>funding_outnum</c>, <c>feerate</c>, <c>total_funding_msat</c>,
/// <c>our_funding_msat</c>, <c>splice_amount</c>) and keeps the old <c>short_channel_id</c>; after both
/// <c>splice_locked</c> it is <c>CHANNELD_NORMAL</c> on the new <c>funding_txid</c>, <c>short_channel_id</c> and
/// <c>total_msat</c>. CLN sent its <c>splice_locked</c> at the first confirmation on regtest (Q4; ours waits for the
/// channel's <c>minimum_depth</c>, D8, so the proofs mine one block at a time until both are locked).</item>
/// <item>Payments while pending work both ways; CLN sends <c>start_batch</c>
/// (<c>007f ‖ channel_id ‖ 0002 ‖ TLV 1 = 0084</c>) followed by its two <c>commitment_signed</c> as separate wire
/// messages (its connectd splits the batch element), and one <c>revoke_and_ack</c> per batch.</item>
/// <item>CLN log lines of a splice: <c>Splice initiator: we recv commit</c>, <c>Splice: we signed second</c>,
/// <c>Left STFU mode.</c>, <c>Broadcasting splice tx</c>, <c>mutual splice_locked</c>.</item>
/// </list>
/// <para>The proof is written against the SP1 contracts (5ad7acb4); the node side (wire, engine, signer and schema,
/// negotiation) lands in lanes SP1-A..D and the integrator runs it after the merge. The lock steps (our
/// <c>splice_locked</c>, the new funding in <c>listchannels</c>) need the lock of lane SP2-B if SP1 ships without it.
/// Each test builds its own node and a channel we fund to CLN (1M sat, 400k pushed), records our channel traffic both
/// ways with <see cref="SpliceWireRecorder"/> and reaches the splice through the daemon's <c>splicein</c>/<c>spliceout</c>
/// client handlers. Run from the host process: <c>NLightning.Integration.Tests -class
/// NLightning.Integration.Tests.Docker.Interop.Cln.ClnSpliceTests</c>.</para>
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnSpliceTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 12 * 60 * 1_000;

    /// <summary>The most blocks mined one at a time until both ends locked the splice.</summary>
    private const int MaxLockBlocks = 12;

    /// <summary>Payments each way while the splice is pending, proof (e).</summary>
    private const int PendingPayments = 5;

    /// <summary>The feerate of our splices: the test node's 10 sat/vB estimate, inside CLN's acceptable range.</summary>
    private const uint OurFeeRatePerKw = 2_500;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(400_000);
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_settleTimeout = TimeSpan.FromSeconds(60);

    private readonly ClnFixture _fixture;
    private ClnChannelSession? _session;
    private SpliceWireRecorder? _wire;

    public ClnSpliceTests(ClnFixture fixture, ITestOutputHelper output)
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
            Console.WriteLine("[cln] CLN splice log lines for our node:\n"
                            + await GetClnLogForUsAsync(_session, CancellationToken.None, "plice", "STFU", "BATCH",
                                                        "batch", "TX_", "tx_", "inflight"));
            Console.WriteLine("[cln] CLN unusual/broken log lines so far:\n"
                            + await _fixture.Cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 60,
                                                                  "unusual"));
            if (DockerDiagnostics.CurrentTestFailed)
                Console.WriteLine($"[cln] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");

            await _session.DisposeAsync();
        }
    }

    /// <summary>
    /// Proof SP1 (a): CLN splices in 100,000 sat (<c>splicein</c>). CLN is the quiescence initiator and sends
    /// <c>splice_init</c> with +100,000; we accept as non-initiator with contribution 0 (D10) and no RAA for the
    /// splice <c>commitment_signed</c> (SP-CS-02); both <c>tx_signatures</c> carry the shared-input signature
    /// (SP-SIG-01); the splice transaction spends the old funding output 2-of-2 (f); a payment each way while it is
    /// pending; it confirms, both send <c>splice_locked</c> for it, CLN and we list the new funding, capacity and short
    /// channel id; payments both ways after.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ClnSplicesIn_When_TheSpliceLocks_Then_BothListTheNewFundingAndPaymentsFlow()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-splice-a", ct);
        await _fixture.FundClnWalletAsync(LightningMoney.Satoshis(300_000), [session.Node], ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;

        // Act
        var spliced = await session.Cln.CallAsync("splicein", ct, ("channel", session.ChannelIdHex),
                                                  ("amount", 100_000));
        Console.WriteLine($"[cln] splicein: txid {spliced["txid"]}");
        var spliceTxId = uint256.Parse(spliced["txid"]!.GetValue<string>());

        // Assert: CLN started it (stfu(1) and splice_init from CLN), we acked with 0 (D10)
        var init = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.SpliceInit, from),
                                       s_stepTimeout, "CLN's splice_init", ct);
        var ack = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.SpliceAck, from),
                                      s_stepTimeout, "our splice_ack", ct);
        Assert.Equal(100_000, init.SpliceContributionSatoshis);
        Assert.Equal(0, ack.SpliceContributionSatoshis);
        Assert.True(init.Sequence < ack.Sequence);
        var theirStfu = wire.First(inbound: true, MessageTypes.Stfu, from);
        var ourStfu = wire.First(inbound: false, MessageTypes.Stfu, from);
        Assert.True(theirStfu.StfuInitiator && !ourStfu.StfuInitiator, "CLN was not the quiescence initiator");
        Assert.True(theirStfu.Sequence < ourStfu.Sequence && ourStfu.Sequence < init.Sequence);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, from, spliceTxId, ct);

        // ...the transaction spends the old funding 2-of-2 into the new funding (f)
        var expectedCapacity = before.CapacitySat + 100_000;
        await AssertSpliceTransactionAsync(before, spliceTxId, expectedCapacity, null, ct);
        await AssertClnPendingAsync(session, spliceTxId, expectedCapacity, ct);

        // ...payments both ways while pending, then the lock and payments after
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(20_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, before.FundingTxId, spliceTxId);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Proof SP1 (b) and (f): we splice in 100,000 sat through <c>splicein</c> at 2,500 sat/kw. We are the quiescence
    /// initiator and send <c>splice_init</c> (+100,000, our feerate); CLN acks with 0; the response carries the signed
    /// splice and the new capacity; the transaction spends the old funding 2-of-2 and pays exactly our feerate for its
    /// weight within one vbyte (IT-S-03: CLN adds nothing, so we pay the whole fee); lock and payments as in (a).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_WeSpliceIn_When_TheSpliceLocks_Then_ClnAgreesAndTheFeeMatchesOurFeerate()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-splice-b", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;

        // Act
        var response = await SpliceInAsync(session, 100_000, ct);

        // Assert: we started it and CLN acked with 0
        Assert.Equal(SpliceNegotiationState.Signed, response.State);
        Assert.Null(response.FailureReason);
        Assert.NotNull(response.SpliceTxId);
        var spliceTxId = new uint256((byte[])response.SpliceTxId.Value);
        var expectedCapacity = before.CapacitySat + 100_000;
        Assert.Equal(expectedCapacity, response.NewCapacitySat);
        var init = wire.First(inbound: false, MessageTypes.SpliceInit, from);
        var ack = wire.First(inbound: true, MessageTypes.SpliceAck, from);
        Assert.Equal(100_000, init.SpliceContributionSatoshis);
        Assert.Equal(OurFeeRatePerKw, init.SpliceFeeratePerKw);
        Assert.Equal(0, ack.SpliceContributionSatoshis);
        var ourStfu = wire.First(inbound: false, MessageTypes.Stfu, from);
        var theirStfu = wire.First(inbound: true, MessageTypes.Stfu, from);
        Assert.True(ourStfu.StfuInitiator && !theirStfu.StfuInitiator, "we were not the quiescence initiator");
        Assert.True(theirStfu.Sequence < init.Sequence && init.Sequence < ack.Sequence);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, from, spliceTxId, ct);

        // ...(f) valid 2-of-2 spend, our feerate paid for the whole transaction
        await AssertSpliceTransactionAsync(before, spliceTxId, expectedCapacity, OurFeeRatePerKw, ct);
        await AssertClnPendingAsync(session, spliceTxId, expectedCapacity, ct);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(20_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(10_000), ct);

        // ...lock, then payments; our balance grew by the splice-in
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat + 100_000_000 - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, before.FundingTxId, spliceTxId);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Proof SP1 (c): we splice 50,000 sat out to an address of bitcoind's wallet (<c>spliceout --address</c>). The
    /// transaction pays exactly 50,000 sat to that address; our <c>splice_init</c> carries -(50,000 + fee) (BOLT 2: the
    /// contribution is what leaves our balance, D16), so the capacity drops by the amount plus the whole fee (we are
    /// the initiator and CLN adds nothing); the address has received it once the splice confirms; our balance drops by
    /// the same.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_WeSpliceOutToAnAddress_When_TheSpliceLocks_Then_TheAddressIsPaidAndCapacityDrops()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-splice-c", ct);
        var before = await SnapshotAsync(session, ct);
        var address = await _fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
        var from = wire.CurrentSequence;

        // Act
        var response = await SpliceOutAsync(session, 50_000, address.ToString(), ct);

        // Assert: the splice was signed
        Assert.Equal(SpliceNegotiationState.Signed, response.State);
        Assert.Null(response.FailureReason);
        Assert.NotNull(response.SpliceTxId);
        var spliceTxId = new uint256((byte[])response.SpliceTxId.Value);
        var init = wire.First(inbound: false, MessageTypes.SpliceInit, from);
        Assert.Equal(0, wire.First(inbound: true, MessageTypes.SpliceAck, from).SpliceContributionSatoshis);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, from, spliceTxId, ct);

        // ...our splice_init carries -(50,000 + fee): BOLT 2 sets funding_contribution_satoshis to what leaves our
        // balance, and the new funding output is the previous capacity plus the contributions (D16, as CLN does in (d))
        var fee = await GetFeeAsync(spliceTxId, ct);
        Assert.Equal(-(long)(50_000 + fee), init.SpliceContributionSatoshis);
        var expectedCapacity = before.CapacitySat - 50_000 - fee;
        Assert.Equal(expectedCapacity, (ulong)((long)before.CapacitySat + init.SpliceContributionSatoshis));
        Assert.Equal(expectedCapacity, response.NewCapacitySat);

        // ...the transaction pays the address and funds the channel with the rest, at our feerate (f)
        var tx = await AssertSpliceTransactionAsync(before, spliceTxId, expectedCapacity, OurFeeRatePerKw,
                                                    ct);
        Assert.Single(tx.Outputs, o => o.ScriptPubKey == address.ScriptPubKey
                                    && o.Value == Money.Satoshis(50_000));
        await AssertClnPendingAsync(session, spliceTxId, expectedCapacity, ct);

        // ...lock: the address holds the amount in a confirmed transaction; our balance paid amount + fee
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        var info = await _fixture.Bitcoin.Rpc.GetRawTransactionInfoAsync(spliceTxId, ct);
        Assert.True(info.Confirmations >= 1, "the splice transaction is not confirmed");
        Assert.Equal(before.LocalBalanceMsat - (long)(50_000 + fee) * 1_000, after.LocalBalanceMsat);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, before.FundingTxId, spliceTxId);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Proof SP1 (d): CLN splices 50,000 sat out to its own wallet (<c>spliceout</c>; CLN v26.06.8 refuses an external
    /// address). CLN's <c>splice_init</c> carries -(50,000 + its fee) (Q2); we ack with 0; the capacity drops by that
    /// much, our balance does not move, and CLN's wallet has the 50,000 sat output of the splice transaction.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ClnSplicesOut_When_TheSpliceLocks_Then_OurBalanceStaysAndClnsWalletIsPaid()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-splice-d", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;

        // Act
        var spliced = await session.Cln.CallAsync("spliceout", ct, ("channel", session.ChannelIdHex),
                                                  ("amount", 50_000));
        Console.WriteLine($"[cln] spliceout: txid {spliced["txid"]}");
        var spliceTxId = uint256.Parse(spliced["txid"]!.GetValue<string>());

        // Assert: CLN's contribution is -(amount + fee); we acked with 0
        var init = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.SpliceInit, from),
                                       s_stepTimeout, "CLN's splice_init", ct);
        var ack = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.SpliceAck, from),
                                      s_stepTimeout, "our splice_ack", ct);
        Assert.True(init.SpliceContributionSatoshis < -50_000,
                    $"CLN's contribution {init.SpliceContributionSatoshis} does not include its fee");
        Assert.Equal(0, ack.SpliceContributionSatoshis);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, from, spliceTxId, ct);
        var expectedCapacity = (ulong)((long)before.CapacitySat + init.SpliceContributionSatoshis);
        var tx = await AssertSpliceTransactionAsync(before, spliceTxId, expectedCapacity, null, ct);
        Assert.Contains(tx.Outputs, o => o.Value == Money.Satoshis(50_000));
        Assert.Equal(-init.SpliceContributionSatoshis - 50_000, (long)await GetFeeAsync(spliceTxId, ct));
        await AssertClnPendingAsync(session, spliceTxId, expectedCapacity, ct);

        // ...lock: our balance unchanged, CLN's wallet lists the 50,000 sat output
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat, after.LocalBalanceMsat);
        await Poll.UntilAsync(async () =>
        {
            var outputs = (await session.Cln.CallAsync("listfunds", ct))["outputs"]!.AsArray();
            return outputs.Any(o => o?["txid"]?.GetValue<string>() == spliceTxId.ToString()
                                 && o["amount_msat"]?.GetValue<long>() == 50_000_000);
        }, s_stepTimeout, "CLN's wallet lists the spliced-out output", ct);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, before.FundingTxId, spliceTxId);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Proof SP1 (e): after both <c>tx_signatures</c> of our splice-in and before it confirms, 5 payments each way. Every
    /// commitment update is a <c>start_batch(batch_size = 2, message_type = 132)</c> followed at once by two
    /// <c>commitment_signed</c>, one per active funding (<c>funding_txid</c> = the current funding and the splice), both
    /// ways (SP-OP-03/05), and answered by one <c>revoke_and_ack</c> per batch (SP-OP-07). After the lock a single
    /// <c>commitment_signed</c> on the new funding again, no <c>start_batch</c>.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_APendingSplice_When_PayingBothWays_Then_EveryUpdateIsABatchOfTwoAndSingleAfterTheLock()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildAsync("nltg-splice-e", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        var response = await SpliceInAsync(session, 100_000, ct);
        Assert.Equal(SpliceNegotiationState.Signed, response.State);
        Assert.NotNull(response.SpliceTxId);
        var spliceTxId = new uint256((byte[])response.SpliceTxId.Value);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, from, spliceTxId, ct);
        var expectedCapacity = before.CapacitySat + 100_000;
        await AssertSpliceTransactionAsync(before, spliceTxId, expectedCapacity, OurFeeRatePerKw, ct);

        // Act: 5 payments each way while pending (nothing mined)
        var tip = await _fixture.Bitcoin.Rpc.GetBlockCountAsync(ct);
        for (var i = 0; i < PendingPayments; i++)
        {
            await AssertWePayClnAsync(session, LightningMoney.Satoshis(10_000 + i * 1_000), ct);
            await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(5_000 + i * 1_000), ct);
        }

        var pendingEnd = wire.CurrentSequence;
        Assert.Equal(tip, await _fixture.Bitcoin.Rpc.GetBlockCountAsync(ct));
        Assert.Contains(spliceTxId, await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct));

        // Assert: batches of two both ways, one revoke_and_ack each
        var (ourBatches, theirBatches) =
            AssertBatchesWhilePending(wire, signaturesAt, pendingEnd, before.FundingTxId, spliceTxId);
        Assert.True(ourBatches >= 2 * PendingPayments,
                    $"only {ourBatches} batches of ours for {2 * PendingPayments} payments");
        Assert.True(theirBatches >= 2 * PendingPayments,
                    $"only {theirBatches} batches of CLN's for {2 * PendingPayments} payments");

        // ...after the lock: single commitment_signed on the new funding, no start_batch
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(21_000), ct);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(11_000), ct);
        var afterLock = wire.Snapshot().Where(m => m.Sequence > after.LockedAtSequence).ToList();
        Assert.DoesNotContain(afterLock, m => m.Type == (ushort)MessageTypes.StartBatch);
        var signed = afterLock.Where(m => m.Type == (ushort)MessageTypes.CommitmentSigned).ToList();
        Assert.Contains(signed, m => !m.Inbound);
        Assert.Contains(signed, m => m.Inbound);
        Assert.All(signed, m => Assert.Equal(spliceTxId, m.CommitmentFundingTxId));
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// Our node with the splice features and the splice IPC commands, a channel we fund to CLN, the wire recorder.
    /// Both ends must see 35 and 63 (D14).
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
                           });
                           // Idempotent: the integrator also registers it in AddNltgNodeServices
                           services.AddSpliceIpcServices();
                           wire.Install(services);
                       });

        var peer = await _session.Cln.GetPeerAsync(_session.Node.NodeIdHex, ct);
        var ourFeatures = peer?["features"]?.GetValue<string>() ?? string.Empty;
        Console.WriteLine($"[cln] CLN lists our features {ourFeatures}");
        foreach (var feature in new[] { Feature.OptionQuiesce, Feature.OptionSplice })
        {
            Assert.True(ClnOnionMessageTests.IsBitSet(ourFeatures, (int)feature)
                     || ClnOnionMessageTests.IsBitSet(ourFeatures, (int)feature - 1),
                        $"CLN does not see {feature} on us");
            Assert.True(_session.Node.PeerManager.GetPeer(_session.ClnPubKey)!.Features.IsFeatureSet(feature),
                        $"CLN does not advertise {feature}");
        }

        return (_session, wire);
    }

    private static async Task<SpliceClientResponse> SpliceInAsync(ClnChannelSession session, ulong amountSat,
                                                                  CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<SpliceInClientRequest, SpliceClientResponse>>();
        var response = await handler.HandleAsync(new SpliceInClientRequest(session.ChannelId, amountSat)
        {
            FeeRatePerKw = OurFeeRatePerKw
        }, ct);
        Console.WriteLine($"[nltg] splicein: {response.State}, txid {response.SpliceTxId}, capacity "
                        + $"{response.NewCapacitySat}, reason {response.FailureReason}");
        return response;
    }

    private static async Task<SpliceClientResponse> SpliceOutAsync(ClnChannelSession session, ulong amountSat,
                                                                   string address, CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<SpliceOutClientRequest, SpliceClientResponse>>();
        var response = await handler.HandleAsync(new SpliceOutClientRequest(session.ChannelId, amountSat)
        {
            Address = address,
            FeeRatePerKw = OurFeeRatePerKw
        }, ct);
        Console.WriteLine($"[nltg] spliceout: {response.State}, txid {response.SpliceTxId}, capacity "
                        + $"{response.NewCapacitySat}, reason {response.FailureReason}");
        return response;
    }

    /// <summary>
    /// The interactive part of a splice on the wire from <paramref name="from"/> (SP-CS-01/02, SP-SIG-01): a
    /// <c>commitment_signed</c> each way for the new funding before any <c>tx_signatures</c>, no
    /// <c>revoke_and_ack</c> in between, both <c>tx_signatures</c> for the splice txid with a
    /// <c>shared_input_signature</c>; the transaction is in bitcoind's mempool and our splice service reports it
    /// signed. Returns the sequence after the last <c>tx_signatures</c> (the splice is pending from there).
    /// </summary>
    private async Task<long> AssertSignedSpliceAsync(ClnChannelSession session, SpliceWireRecorder wire, long from,
                                                     uint256 spliceTxId, CancellationToken ct)
    {
        var ourSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxSignatures, from),
                                          s_stepTimeout, "our tx_signatures", ct);
        var theirSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxSignatures, from),
                                            s_stepTimeout, "CLN's tx_signatures", ct);
        foreach (var sigs in new[] { ourSigs, theirSigs })
        {
            Assert.Equal(spliceTxId, sigs.TxSignaturesTxId);
            Assert.True(sigs.HasSharedInputSignature,
                        $"{(sigs.Inbound ? "CLN's" : "our")} tx_signatures has no shared_input_signature");
        }

        var first = Math.Min(ourSigs.Sequence, theirSigs.Sequence);
        var ourCs = wire.First(inbound: false, MessageTypes.CommitmentSigned, from);
        var theirCs = wire.First(inbound: true, MessageTypes.CommitmentSigned, from);
        Assert.True(ourCs.Sequence < first && theirCs.Sequence < first,
                    "a tx_signatures went out before both splice commitment_signed");
        Assert.Equal(spliceTxId, ourCs.CommitmentFundingTxId);
        Assert.Equal(spliceTxId, theirCs.CommitmentFundingTxId);
        Assert.DoesNotContain(wire.Snapshot(), m => m.Type == (ushort)MessageTypes.RevokeAndAck
                                                 && m.Sequence > from && m.Sequence < first);
        Assert.DoesNotContain(wire.Snapshot(), m => m.Type == (ushort)MessageTypes.TxAbort && m.Sequence > from);

        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct)).Contains(spliceTxId),
                              s_stepTimeout, "the splice transaction in bitcoind's mempool", ct);
        var negotiation = session.Node.Services.GetRequiredService<ISpliceService>().GetNegotiation(session.ChannelId);
        Assert.NotNull(negotiation);
        Assert.Equal(SpliceNegotiationState.Signed, negotiation.State);
        Assert.NotNull(negotiation.SpliceTxId);
        Assert.Equal(spliceTxId, new uint256((byte[])negotiation.SpliceTxId.Value));
        return Math.Max(ourSigs.Sequence, theirSigs.Sequence);
    }

    /// <summary>
    /// Proof SP1 (f): the splice transaction spends the previous funding output with a 2-of-2 witness (empty item,
    /// two SIGHASH_ALL signatures, the BOLT 3 <c>2 &lt;key1&gt; &lt;key2&gt; 2 CHECKMULTISIG</c> script with the keys
    /// in lexicographic order, hashing to the spent P2WSH), has exactly one P2WSH output of
    /// <paramref name="expectedCapacitySat"/> (the new funding) and, when <paramref name="feeRatePerKw"/> is ours, pays
    /// that feerate for the transaction's weight within one vbyte (IT-S-03; plus the 2 WU per ECDSA signature by which
    /// the maximum-witness budget may exceed a low-R signature). CLN's own splices are only checked not to underpay.
    /// </summary>
    private async Task<Transaction> AssertSpliceTransactionAsync(ChannelSnapshot before, uint256 spliceTxId,
                                                                 ulong expectedCapacitySat, uint? feeRatePerKw,
                                                                 CancellationToken ct)
    {
        var rpc = _fixture.Bitcoin.Rpc;
        var tx = await rpc.GetRawTransactionAsync(spliceTxId, true, ct);
        var oldFunding = new OutPoint(before.FundingTxId, before.FundingOutputIndex);
        var shared = Assert.Single(tx.Inputs, i => i.PrevOut == oldFunding);
        var spent = (await rpc.GetRawTransactionAsync(oldFunding.Hash, true, ct)).Outputs[oldFunding.N];
        var witness = shared.WitScript.Pushes.ToArray();
        Assert.Equal(4, witness.Length);
        Assert.Empty(witness[0]);
        var script = new Script(witness[3]);
        Assert.Equal(spent.ScriptPubKey, script.WitHash.ScriptPubKey);
        var ops = script.ToOps().ToArray();
        Assert.Equal(5, ops.Length);
        Assert.Equal(OpcodeType.OP_2, ops[0].Code);
        Assert.Equal(OpcodeType.OP_2, ops[3].Code);
        Assert.Equal(OpcodeType.OP_CHECKMULTISIG, ops[4].Code);
        Assert.True(ops[1].PushData.AsSpan().SequenceCompareTo(ops[2].PushData) < 0,
                    "the 2-of-2 keys are not in lexicographic order (BOLT 3)");
        foreach (var signature in witness[1..3])
        {
            Assert.Equal(0x30, signature[0]);
            Assert.Equal((byte)SigHash.All, signature[^1]);
        }

        var funding = Assert.Single(tx.Outputs, o => o.ScriptPubKey.IsScriptType(ScriptType.P2WSH)
                                                  && o.Value == Money.Satoshis((long)expectedCapacitySat));
        Console.WriteLine($"[proof] splice {spliceTxId}: funding output {tx.Outputs.IndexOf(funding)} of "
                        + $"{expectedCapacitySat} sat, {tx.Inputs.Count} inputs, {tx.Outputs.Count} outputs");

        var fee = await GetFeeAsync(spliceTxId, ct);
        var weight = await GetWeightAsync(spliceTxId, ct);
        var ecdsaSignatures = tx.Inputs.SelectMany(i => i.WitScript.Pushes)
                                .Count(p => p.Length is >= 9 and <= 73 && p[0] == 0x30);
        var effectiveFeeRate = feeRatePerKw ?? 253;
        var floor = effectiveFeeRate * weight / 1_000;
        Console.WriteLine($"[proof] splice fee {fee} sat for {weight} WU ({fee * 1_000.0 / weight:F1} sat/kw), "
                        + $"{ecdsaSignatures} ECDSA signatures");
        Assert.True(fee + 1 >= floor, $"the splice pays {fee} sat, less than {floor} sat at {effectiveFeeRate} sat/kw");
        if (feeRatePerKw is { } ours)
        {
            var ceiling = (ours * (weight + 4 + 2 * (ulong)ecdsaSignatures) + 999) / 1_000;
            Assert.True(fee <= ceiling + 1,
                        $"our splice pays {fee} sat, more than one vbyte above {floor} sat at {ours} sat/kw");
        }

        return tx;
    }

    private async Task<ulong> GetFeeAsync(uint256 txId, CancellationToken ct)
    {
        var rpc = _fixture.Bitcoin.Rpc;
        var tx = await rpc.GetRawTransactionAsync(txId, true, ct);
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

    /// <summary>
    /// CLN lists the splice pending: <c>CHANNELD_AWAITING_SPLICE</c> with one <c>inflight</c> entry for the splice
    /// txid and the new capacity.
    /// </summary>
    private static async Task AssertClnPendingAsync(ClnChannelSession session, uint256 spliceTxId,
                                                    ulong expectedCapacitySat, CancellationToken ct)
    {
        var channel = await Poll.ForAsync(async () =>
        {
            var c = await session.GetClnChannelAsync(ct);
            return c["inflight"]?.AsArray().Count > 0 ? c : null;
        }, s_stepTimeout, "CLN's inflight splice", ct);
        Console.WriteLine($"[cln] pending: {ClnChannelSession.DescribeCln(channel)} inflight "
                        + channel["inflight"]!.ToJsonString());
        Assert.Equal("CHANNELD_AWAITING_SPLICE", channel["state"]!.GetValue<string>());
        var inflight = Assert.Single(channel["inflight"]!.AsArray());
        Assert.Equal(spliceTxId.ToString(), inflight!["funding_txid"]!.GetValue<string>());
        Assert.Equal((long)expectedCapacitySat * 1_000, inflight["total_funding_msat"]!.GetValue<long>());
    }

    /// <summary>
    /// Mines one block at a time (no HTLC in flight) until both ends locked the splice: CLN is
    /// <c>CHANNELD_NORMAL</c> on the splice's funding with the new capacity, both sent <c>splice_locked</c> for it
    /// (SP-LK-01), our <c>listchannels</c> shows the same funding, capacity and short channel id, and the channel is
    /// usable again.
    /// </summary>
    private async Task<LockedChannel> MineUntilLockedAsync(ClnChannelSession session, SpliceWireRecorder wire,
                                                           ChannelSnapshot before, uint256 spliceTxId,
                                                           ulong expectedCapacitySat, CancellationToken ct)
    {
        for (var block = 1; ; block++)
        {
            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            var cln = await session.GetClnChannelAsync(ct);
            var ours = await session.GetOurChannelAsync(ct);
            Console.WriteLine($"[proof] +{block} block(s): ours {ours.Describe()} funding "
                            + $"{(ours.FundingTxId is { } f ? new uint256((byte[])f) : null)}; cln "
                            + ClnChannelSession.DescribeCln(cln));
            if (cln["state"]?.GetValue<string>() == "CHANNELD_NORMAL"
             && cln["funding_txid"]?.GetValue<string>() == spliceTxId.ToString()
             && ours.FundingTxId is { } funding && new uint256((byte[])funding) == spliceTxId)
                break;

            Assert.True(block < MaxLockBlocks, $"the splice was not locked by both ends in {MaxLockBlocks} blocks");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        var ourLocked = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.SpliceLocked),
                                            s_stepTimeout, "our splice_locked", ct);
        var theirLocked = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.SpliceLocked),
                                              s_stepTimeout, "CLN's splice_locked", ct);
        Assert.Equal(spliceTxId, ourLocked.SpliceLockedTxId);
        Assert.Equal(spliceTxId, theirLocked.SpliceLockedTxId);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);

        var channel = await session.GetOurChannelAsync(ct);
        var clnChannel = await session.GetClnChannelAsync(ct);
        Assert.Equal((long)expectedCapacitySat, channel.Capacity.Satoshi);
        Assert.Equal((long)expectedCapacitySat * 1_000, clnChannel["total_msat"]!.GetValue<long>());
        Assert.NotNull(channel.ShortChannelId);
        var clnScid = clnChannel["short_channel_id"]!.GetValue<string>();
        Assert.NotEqual(before.ShortChannelId, clnScid);
        Assert.Equal(ParseScid(clnScid), channel.ShortChannelId.Value.ToUInt64());
        Assert.Equal(clnChannel["to_us_msat"]!.GetValue<long>(), (long)channel.RemoteBalance.MilliSatoshi);
        return new LockedChannel((long)channel.LocalBalance.MilliSatoshi,
                                 Math.Max(ourLocked.Sequence, theirLocked.Sequence));
    }

    /// <summary>
    /// SP-OP-03/05/07 on the wire between <paramref name="from"/> and <paramref name="to"/>: every
    /// <c>commitment_signed</c> of each side comes in a batch (<c>start_batch</c> with <c>batch_size</c> 2 and
    /// <c>message_type</c> 132, then two <c>commitment_signed</c> with nothing of that side in between, whose
    /// <c>funding_txid</c>s are the current funding and the splice), and each batch is answered by exactly one
    /// <c>revoke_and_ack</c>.
    /// </summary>
    /// <returns>How many batches each side sent.</returns>
    private static (int Ours, int Theirs) AssertBatchesWhilePending(SpliceWireRecorder wire, long from, long to,
                                                                    uint256 currentFunding, uint256 spliceTxId)
    {
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
                    Assert.Fail($"{who} sent a commitment_signed outside a batch while the splice was pending "
                              + $"(#{side[i].Sequence})");
                if (side[i].Type != (ushort)MessageTypes.StartBatch)
                    continue;

                Assert.Equal(2, side[i].StartBatchSize);
                Assert.Equal((ushort)MessageTypes.CommitmentSigned, side[i].StartBatchMessageType);
                Assert.True(i + 2 < side.Count,
                            $"{who}: start_batch #{side[i].Sequence} is not followed by two messages");
                var first = side[i + 1];
                var second = side[i + 2];
                Assert.True(first.Type == (ushort)MessageTypes.CommitmentSigned
                         && second.Type == (ushort)MessageTypes.CommitmentSigned,
                            $"{who}: start_batch #{side[i].Sequence} is not followed by two commitment_signed");
                Assert.Equal(new HashSet<uint256> { currentFunding, spliceTxId },
                             new HashSet<uint256> { first.CommitmentFundingTxId!, second.CommitmentFundingTxId! });
                batches++;
                i += 2;
            }

            var revokes = window.Count(m => m.Inbound != inbound && m.Type == (ushort)MessageTypes.RevokeAndAck);
            Assert.True(revokes == batches,
                        $"{batches} batches from {who} answered by {revokes} revoke_and_ack (one per batch)");
            counts[inbound ? 1 : 0] = batches;
        }

        Console.WriteLine($"[proof] while pending: {counts[0]} batches of ours, {counts[1]} of CLN's");
        return (counts[0], counts[1]);
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
        return new ChannelSnapshot(funding, ours.FundingOutputIndex.Value, (ulong)ours.Capacity.Satoshi,
                                   (long)ours.LocalBalance.MilliSatoshi,
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

    /// <summary>
    /// We pay a CLN invoice over the channel (pending or not) and wait until neither end has an HTLC.
    /// </summary>
    private static async Task AssertWePayClnAsync(ClnChannelSession session, LightningMoney amount,
                                                  CancellationToken ct)
    {
        var label = $"nltg-splice-pays-{Guid.NewGuid():N}";
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

    /// <summary>
    /// CLN pays our invoice (pending or not); the invoice settles and neither end keeps an HTLC.
    /// </summary>
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
                                      .TakeLast(150);
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception e)
        {
            return $"(getlog failed: {e.Message})";
        }
    }

    /// <summary>The channel before the splice.</summary>
    private sealed record ChannelSnapshot(uint256 FundingTxId, uint FundingOutputIndex, ulong CapacitySat,
                                          long LocalBalanceMsat, string ShortChannelId);

    /// <summary>The channel once both ends locked the splice.</summary>
    /// <param name="LocalBalanceMsat">Our balance then.</param>
    /// <param name="LockedAtSequence">The later of the two <c>splice_locked</c> in the recording.</param>
    private sealed record LockedChannel(long LocalBalanceMsat, long LockedAtSequence);

    /// <summary>
    /// One recorded message.
    /// </summary>
    /// <param name="Sequence">Its position in the recording (both directions share one order).</param>
    /// <param name="Type">The message type.</param>
    /// <param name="Wire">The whole message (u16 type prefix included).</param>
    /// <param name="Inbound">Received (true) or sent (false) by our node.</param>
    internal sealed record SpliceWireMessage(long Sequence, ushort Type, byte[] Wire, bool Inbound)
    {
        // Every channel message starts with type (2) and channel_id (32)
        private const int BodyOffset = 2 + 32;

        public string Hex => Convert.ToHexStringLower(Wire);

        public bool IsPingOrPong => Type is (ushort)MessageTypes.Ping or (ushort)MessageTypes.Pong;

        /// <summary><c>stfu</c>: the <c>initiator</c> byte.</summary>
        public bool StfuInitiator => Type == (ushort)MessageTypes.Stfu && Wire.Length > BodyOffset
                                                                     && Wire[BodyOffset] != 0;

        /// <summary><c>splice_init</c>/<c>splice_ack</c>: the s64 <c>funding_contribution_satoshis</c>.</summary>
        public long SpliceContributionSatoshis => BinaryPrimitives.ReadInt64BigEndian(Wire.AsSpan(BodyOffset, 8));

        /// <summary><c>splice_init</c>: the u32 <c>funding_feerate_perkw</c> after the contribution.</summary>
        public uint SpliceFeeratePerKw => BinaryPrimitives.ReadUInt32BigEndian(Wire.AsSpan(BodyOffset + 8, 4));

        /// <summary><c>splice_locked</c>: <c>splice_txid</c>.</summary>
        public uint256 SpliceLockedTxId => new(Wire.AsSpan(BodyOffset, 32).ToArray());

        /// <summary><c>tx_signatures</c>: <c>txid</c>.</summary>
        public uint256 TxSignaturesTxId => new(Wire.AsSpan(BodyOffset, 32).ToArray());

        /// <summary>
        /// <c>tx_signatures</c> carries TLV 0 <c>shared_input_signature</c> (64 bytes) after its witnesses.
        /// </summary>
        public bool HasSharedInputSignature
        {
            get
            {
                var offset = BodyOffset + 32;
                var count = BinaryPrimitives.ReadUInt16BigEndian(Wire.AsSpan(offset, 2));
                offset += 2;
                for (var i = 0; i < count; i++)
                    offset += 2 + BinaryPrimitives.ReadUInt16BigEndian(Wire.AsSpan(offset, 2));

                return FindTlv(offset, 0) is { Length: 64 };
            }
        }

        /// <summary><c>start_batch</c>: <c>batch_size</c>.</summary>
        public ushort StartBatchSize => BinaryPrimitives.ReadUInt16BigEndian(Wire.AsSpan(BodyOffset, 2));

        /// <summary><c>start_batch</c>: TLV 1 <c>message_type</c>, or 0 without it.</summary>
        public ushort StartBatchMessageType =>
            FindTlv(BodyOffset + 2, 1) is { Length: 2 } value ? BinaryPrimitives.ReadUInt16BigEndian(value) : (ushort)0;

        /// <summary><c>commitment_signed</c>: TLV 1 <c>funding_txid</c>, or null without it.</summary>
        public uint256? CommitmentFundingTxId
        {
            get
            {
                // signature (64), num_htlcs (u16), htlc_signatures (64 each), then the TLV stream
                var offset = BodyOffset + 64;
                var htlcs = BinaryPrimitives.ReadUInt16BigEndian(Wire.AsSpan(offset, 2));
                return FindTlv(offset + 2 + 64 * htlcs, 1) is { Length: 32 } value ? new uint256(value) : null;
            }
        }

        /// <summary>The value of TLV <paramref name="type"/> in the stream at <paramref name="offset"/>.</summary>
        private byte[]? FindTlv(int offset, ulong type)
        {
            while (offset < Wire.Length)
            {
                var recordType = ReadBigSize(ref offset);
                var length = (int)ReadBigSize(ref offset);
                if (recordType == type)
                    return Wire.AsSpan(offset, length).ToArray();

                offset += length;
            }

            return null;
        }

        private ulong ReadBigSize(ref int offset)
        {
            var first = Wire[offset++];
            ulong value;
            switch (first)
            {
                case 0xfd:
                    value = BinaryPrimitives.ReadUInt16BigEndian(Wire.AsSpan(offset, 2));
                    offset += 2;
                    break;
                case 0xfe:
                    value = BinaryPrimitives.ReadUInt32BigEndian(Wire.AsSpan(offset, 4));
                    offset += 4;
                    break;
                case 0xff:
                    value = BinaryPrimitives.ReadUInt64BigEndian(Wire.AsSpan(offset, 8));
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
    /// Records, in one order for both directions, every message on our node's transport path (the pattern of
    /// <see cref="ClnQuiescenceTests.QuiescenceWireRecorder"/>: the node's <see cref="ITransportServiceFactory"/> and
    /// <see cref="IMessageServiceFactory"/> are replaced by the production factories over a recording decorator of its
    /// <see cref="IMessageSerializer"/>, so what the channel layer serializes for storage is never recorded). Every type
    /// is kept: the batch proof needs to see that nothing came between a <c>start_batch</c> and its messages.
    /// </summary>
    internal sealed class SpliceWireRecorder
    {
        public const ushort WarningType = 1;
        public const ushort ErrorType = 17;

        private static readonly HashSet<ushort> s_described =
        [
            (ushort)MessageTypes.Stfu, (ushort)MessageTypes.SpliceInit, (ushort)MessageTypes.SpliceAck,
            (ushort)MessageTypes.SpliceLocked, (ushort)MessageTypes.StartBatch, (ushort)MessageTypes.TxAddInput,
            (ushort)MessageTypes.TxAddOutput, (ushort)MessageTypes.TxComplete, (ushort)MessageTypes.TxSignatures,
            (ushort)MessageTypes.TxAbort, (ushort)MessageTypes.CommitmentSigned, (ushort)MessageTypes.RevokeAndAck,
            (ushort)MessageTypes.UpdateAddHtlc, (ushort)MessageTypes.UpdateFulfillHtlc,
            (ushort)MessageTypes.UpdateFailHtlc, (ushort)MessageTypes.ChannelReestablish, WarningType, ErrorType
        ];

        private readonly ConcurrentQueue<SpliceWireMessage> _traffic = new();
        private readonly Lock _sequenceLock = new();
        private long _sequence;

        public IReadOnlyList<SpliceWireMessage> Snapshot() => _traffic.ToArray();

        /// <summary>The sequence the next recorded message gets.</summary>
        public long CurrentSequence
        {
            get
            {
                lock (_sequenceLock)
                    return _sequence;
            }
        }

        public SpliceWireMessage? FirstOrDefault(bool inbound, MessageTypes type, long fromSequence = 0) =>
            _traffic.FirstOrDefault(m => m.Inbound == inbound && m.Type == (ushort)type
                                      && m.Sequence >= fromSequence);

        public SpliceWireMessage First(bool inbound, MessageTypes type, long fromSequence) =>
            FirstOrDefault(inbound, type, fromSequence)
         ?? throw new Xunit.Sdk.XunitException($"no {(inbound ? "inbound" : "outbound")} {type} recorded from "
                                             + $"#{fromSequence}");

        public string Describe() =>
            string.Join(" ", Snapshot().Where(m => s_described.Contains(m.Type))
                                       .Select(m => $"{(m.Inbound ? "<" : ">")}{(MessageTypes)m.Type}"));

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
            lock (_sequenceLock)
                _traffic.Enqueue(new SpliceWireMessage(_sequence++, type, wire, inbound));
        }

        private sealed class RecordingMessageSerializer(IMessageSerializer inner, SpliceWireRecorder recorder)
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