using Lnrpc;
using LNUnit.LND;
using NBitcoin;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain.Anchors;

using Abcd;
using Domain.Channels.Enums;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Payments.Enums;
using Domain.Protocol.Onion.Enums;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 5 plan Proof O3 on anchors channels (O7-T4; our commitment on chain), against LND david (and alice for the
/// forward):
/// <list type="bullet">
///   <item>(a) an idle channel with push: our <c>to_local</c> is swept once LND's CSV has passed (nSequence = CSV),
///   the anchors untouched by the sweep, the wallet credited, LND lists a remote force close.</item>
///   <item>(b) our HTLC to david's hold invoice, which david cancels: at <c>cltv_expiry</c> our zero-fee
///   HTLC-timeout goes out combined with wallet fee inputs (B5-HTX-02: david's signature
///   <c>SIGHASH_SINGLE|ANYONECANPAY</c>, ours <c>SIGHASH_ALL</c>, nSequence 1, nLockTime = <c>cltv_expiry</c>), the
///   payment fails with <c>permanent_channel_failure</c> once it is reasonably deep, and its second-level output is
///   swept after the CSV.</item>
///   <item>(c) alice → us → david: we force close the upstream channel while david holds the HTLC, then david settles:
///   the downstream preimage makes us claim alice's HTLC on our commitment with an HTLC-success combined with wallet
///   fee inputs (preimage in the witness, nSequence 1) before its expiry, and alice's payment succeeds.</item>
/// </list>
/// </summary>
/// <remarks>
/// Needs lanes O7-X1 (fee inputs, wallet signing) and O7-X3 (anchors HTLC transactions in the local resolver). Run
/// with <c>ONCHAIN_SUITE=anchors scripts/run-onchain.sh</c>.
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
[Trait("Category", AnchorsChannelTests.AnchorsCategory)]
public class AnchorsO3Tests : IAsyncLifetime
{
    private const ulong HoldInvoiceCltvExpiry = 24;
    private const ushort OurCltvExpiryDelta = 40;
    private const uint OurFeeBaseMsat = 1_000;
    private const uint OurFeeProportionalMillionths = 100;

    private readonly AnchorsHarness _harness;
    private NLightningTestNode? _node;

    public AnchorsO3Tests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await _harness.CreateNodeAsync("anchors-o3", TestContext.Current.CancellationToken, o =>
        {
            o.Routing.CltvExpiryDelta = OurCltvExpiryDelta;
            o.Routing.FeeBaseMsat = OurFeeBaseMsat;
            o.Routing.FeeProportionalMillionths = OurFeeProportionalMillionths;
        });
    }

    [Fact]
    public async Task Given_IdleAnchorsChannelWithPush_When_WeForceClose_Then_ToLocalSweptAfterLndsCsv()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _harness.Fixture.GetLndNode("david");
        var channel = await _harness.OpenAnchorsChannelAsync(node, david, LightningMoney.Satoshis(200_000), ct);
        var csv = await GetOurCsvAsync(node, david, channel, ct);
        var model = AnchorsHarness.GetModel(node, channel.ChannelId);

        // Act: force close, the commitment confirms
        var commitment = await _harness.ForceCloseAndConfirmAsync(node, [david], channel, ct);
        var (ourAnchor, peerAnchor) = AnchorsHarness.FindAnchors(model, commitment.Transaction);
        var toLocalRow = await AnchorsHarness.WaitForRowAsync(node, channel.ChannelId,
                                                              r => r.TransactionId == commitment.TxId
                                                                && r.Descriptor == OutputDescriptorKind.DelayedToLocal,
                                                              "to_local row", ct);
        var toLocal = commitment.Transaction.Outputs[(int)toLocalRow.OutputIndex];
        var balanceBefore = AnchorsHarness.WalletBalance(node);

        // One block before the sweep may enter a block: nothing is swept
        await _harness.MineToAsync(node, [david], commitment.Height + csv - 2, ct);
        Assert.Null((await AnchorsHarness.GetRowsAsync(node, channel.ChannelId))
                   .Single(r => r.TransactionId == commitment.TxId && r.OutputIndex == toLocalRow.OutputIndex)
                   .ResolvingTransactionId);

        // The tip from which nSequence = csv is final in the next block, then that block
        await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [node], ct);
        var sweepTxId = await AnchorsHarness.WaitForResolvingTxAsync(node, channel.ChannelId, commitment.TxId,
                                                                     toLocalRow.OutputIndex, ct);
        var sweep = await _harness.WaitInMempoolAsync(sweepTxId, ct);
        await _harness.MineUntilConfirmedAsync(node, [david], sweepTxId, ct);

        // Assert: the sweep spends to_local alone with nSequence = csv (not the anchors), and the wallet has it
        var input = Assert.Single(sweep.Inputs, i => i.PrevOut.Hash == commitment.Transaction.GetHash());
        Assert.Equal(new OutPoint(commitment.Transaction, toLocalRow.OutputIndex), input.PrevOut);
        Assert.Equal((uint)csv, input.Sequence.Value);
        Assert.DoesNotContain(sweep.Inputs, i => i.PrevOut.N == ourAnchor || i.PrevOut.N == peerAnchor);
        var fee = await _harness.FeeAsync(sweep, ct);
        Console.WriteLine($"to_local {toLocal.Value}, sweep {sweep.GetHash()} fee {fee}, csv {csv}");
        Assert.True(fee > Money.Zero && fee < toLocal.Value / 2, $"sweep fee {fee}");
        await Poll.UntilAsync(() => (AnchorsHarness.WalletBalance(node) - balanceBefore).Satoshi
                                 >= (toLocal.Value - fee).Satoshi,
                              AnchorsHarness.Timeout, "wallet credited with the sweep", ct);
        await _harness.AssertLndClosedAsync(node, david, channel, commitment.Transaction.GetHash(),
                                            ChannelCloseSummary.Types.ClosureType.RemoteForceClose, ct);
    }

    [Fact]
    public async Task Given_OurHtlcToAHoldInvoice_When_DavidCancels_Then_HtlcTimeoutWithFeeInputsAtExpiryAndSecondLevelSwept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _harness.Fixture.GetLndNode("david");
        var channel = await _harness.OpenAnchorsChannelAsync(node, david, null, ct);
        var csv = await GetOurCsvAsync(node, david, channel, ct);
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(david, paymentHash, 50_000_000, [], ct,
                                                                   "o7 o3 b timed out on chain",
                                                                   HoldInvoiceCltvExpiry);
        await node.PayInvoiceAsync(holdInvoice.PaymentRequest, ct, timeoutSeconds: 2);
        await LndTestHelpers.WaitForInvoiceStateAsync(david, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                      AnchorsHarness.Timeout, ct);
        var htlc = await AnchorsHarness.WaitForHtlcInBothCommitmentsAsync(node, channel.ChannelId,
                                                                        HtlcDirection.Outgoing, ct);

        // Act: force close, david gives the HTLC up
        var commitment = await _harness.ForceCloseAndConfirmAsync(node, [david], channel, ct);
        await LndTestHelpers.CancelInvoiceAsync(david, paymentHash, ct);
        var htlcRow = await AnchorsHarness.WaitForRowAsync(node, channel.ChannelId,
                                                           r => r.TransactionId == commitment.TxId
                                                             && r.Descriptor == OutputDescriptorKind.LocalOfferedHtlc,
                                                           "HTLC row", ct);

        // One block before cltv_expiry: no HTLC-timeout yet
        await _harness.MineToAsync(node, [david], htlc.CltvExpiry - 1, ct);
        Assert.Null((await AnchorsHarness.GetRowsAsync(node, channel.ChannelId))
                   .Single(r => r.TransactionId == commitment.TxId && r.OutputIndex == htlcRow.OutputIndex)
                   .ResolvingTransactionId);

        // At cltv_expiry: our HTLC-timeout with fee inputs goes out and confirms
        await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [node], ct);
        var timeoutTxId = await AnchorsHarness.WaitForResolvingTxAsync(node, channel.ChannelId, commitment.TxId,
                                                                       htlcRow.OutputIndex, ct);
        var timeout = await _harness.WaitInMempoolAsync(timeoutTxId, ct);
        var htlcInput = AssertAnchorsHtlcTransaction(timeout, new OutPoint(commitment.Transaction,
                                                                           htlcRow.OutputIndex));
        Assert.Equal(htlc.CltvExpiry, (uint)timeout.LockTime);
        Assert.Empty(htlcInput.WitScript[3]);
        await _harness.MineUntilConfirmedAsync(node, [david], timeoutTxId, ct);
        var timeoutInfo = await _harness.Fixture.Bitcoin.GetRawTransactionInfoAsync(timeout.GetHash(), ct);
        var timeoutHeight = (uint)(await _harness.Fixture.Bitcoin.GetBlockCountAsync(ct)
                                 - timeoutInfo.Confirmations + 1);

        // Assert: the payment fails once the HTLC-timeout is reasonably deep
        await _harness.MineToAsync(node, [david], timeoutHeight + AnchorsHarness.ReasonableDepth - 1, ct);
        var failed = await Poll.ForAsync(async () =>
        {
            var payment = await node.GetPaymentAsync(new Hash(paymentHash), ct);
            return payment is { Status: PaymentStatus.Failed } ? payment : null;
        }, AnchorsHarness.Timeout, "the payment failed", ct);
        Assert.Equal(FailureCode.PermanentChannelFailure, failed.FailureCode);

        // Act: past the CSV of the HTLC-timeout's output (recorded as ours to sweep)
        var secondLevel = await AnchorsHarness.WaitForRowAsync(node, channel.ChannelId,
                                                               r => r.TransactionId == timeoutTxId
                                                                 && r.Descriptor
                                                                 == OutputDescriptorKind.DelayedToLocal,
                                                               "the second-level output row", ct);
        await _harness.MineToAsync(node, [david], timeoutHeight + csv - 1, ct);
        var sweepTxId = await AnchorsHarness.WaitForResolvingTxAsync(node, channel.ChannelId, timeoutTxId,
                                                                     secondLevel.OutputIndex, ct);
        var sweep = await _harness.WaitInMempoolAsync(sweepTxId, ct);
        await _harness.MineUntilConfirmedAsync(node, [david], sweepTxId, ct);

        // Assert: the second-level output swept with nSequence = csv
        var sweepInput = Assert.Single(sweep.Inputs, i => i.PrevOut == new OutPoint(timeout, secondLevel.OutputIndex));
        Assert.Equal((uint)csv, sweepInput.Sequence.Value);
    }

    [Fact]
    public async Task Given_ForwardSettledAfterWeForceClosedUpstream_When_Resolved_Then_HtlcSuccessWithFeeInputsBeforeExpiry()
    {
        // Arrange: alice -> us -> david (david's hold invoice, reachable only through our private channel)
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var alice = _harness.Fixture.GetLndNode("alice");
        var david = _harness.Fixture.GetLndNode("david");
        var upstream = await _harness.OpenAnchorsChannelAsync(node, alice, LightningMoney.Satoshis(500_000), ct);
        var downstream = await _harness.OpenAnchorsChannelAsync(node, david, null, ct);
        var upstreamLnd = await LndTestHelpers.GetChannelByPointAsync(alice, upstream.ChannelPoint(), ct);
        Assert.NotNull(upstreamLnd);
        var downstreamScid = (await node.GetChannelAsync(downstream.ChannelId, ct)).ShortChannelId;
        Assert.NotNull(downstreamScid);
        var (preimage, paymentHash) = LndTestHelpers.NewPreimage();
        var hint = LndTestHelpers.RouteHint(LndTestHelpers.HopHint(node.NodeIdHex, downstreamScid.Value.ToUInt64(),
                                                                   OurFeeBaseMsat, OurFeeProportionalMillionths,
                                                                   OurCltvExpiryDelta));
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(david, paymentHash, 50_000_000, [hint], ct,
                                                                   "o7 o3 c htlc-success upstream",
                                                                   HoldInvoiceCltvExpiry);
        try
        {
            await LndTestHelpers.ResetMissionControlAsync(alice, ct);
            var payment = LndTestHelpers.SendPaymentV2Async(
                alice, LndTestHelpers.PinnedPayment(holdInvoice.PaymentRequest, [upstreamLnd.ChanId]), ct,
                TimeSpan.FromMinutes(10));
            await LndTestHelpers.WaitForInvoiceStateAsync(david, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                          AnchorsHarness.Timeout, ct);
            var incoming = await AnchorsHarness.WaitForHtlcInBothCommitmentsAsync(node, upstream.ChannelId,
                                                                                HtlcDirection.Incoming, ct);
            await AnchorsHarness.WaitForHtlcInBothCommitmentsAsync(node, downstream.ChannelId,
                                                                  HtlcDirection.Outgoing, ct);
            Console.WriteLine($"Incoming HTLC {incoming.Id}: cltv_expiry {incoming.CltvExpiry}");

            // Act: we force close the upstream channel; david settles downstream
            var commitment = await _harness.ForceCloseAndConfirmAsync(node, [alice, david], upstream, ct);
            var htlcRow = await AnchorsHarness.WaitForRowAsync(node, upstream.ChannelId,
                                                               r => r.TransactionId == commitment.TxId
                                                                 && r.Descriptor
                                                                 == OutputDescriptorKind.LocalReceivedHtlc,
                                                               "alice's HTLC row on our commitment", ct);
            await LndTestHelpers.SettleInvoiceAsync(david, preimage, ct);

            // Assert: our HTLC-success, with fee inputs and the preimage, confirms before the incoming expiry
            var successTxId = await _harness.MineUntilResolvingTxAsync(node, [alice, david], upstream.ChannelId,
                                                                       commitment.TxId, htlcRow.OutputIndex, ct);
            var success = await _harness.WaitInMempoolAsync(successTxId, ct);
            var htlcInput = AssertAnchorsHtlcTransaction(success, new OutPoint(commitment.Transaction,
                                                                               htlcRow.OutputIndex));
            Assert.Equal(0u, (uint)success.LockTime);
            Assert.Equal(preimage, htlcInput.WitScript[3]);
            var info = await _harness.MineUntilConfirmedAsync(node, [alice, david], success.GetHash(), ct);
            var confirmedAt = (uint)(await _harness.Fixture.Bitcoin.GetBlockCountAsync(ct) - info.Confirmations + 1);
            Assert.True(confirmedAt < incoming.CltvExpiry,
                        $"HTLC-success at {confirmedAt}, expiry {incoming.CltvExpiry}");

            // alice learns the preimage (from our HTLC-success or our fulfill) and her payment succeeds
            var result = await _harness.MineUntilAsync(node, [alice, david], async () =>
            {
                await Task.WhenAny(payment, Task.Delay(TimeSpan.FromSeconds(2), ct));
                return payment.IsCompleted ? await payment : null;
            }, "alice's payment completed", ct);
            Console.WriteLine($"Alice's payment {result.Status} ({result.FailureReason})");
            Assert.Equal(Payment.Types.PaymentStatus.Succeeded, result.Status);
            Assert.Equal(Convert.ToHexString(preimage).ToLowerInvariant(), result.PaymentPreimage);
        }
        finally
        {
            await AnchorsHarness.CancelHoldInvoiceQuietlyAsync(david, paymentHash);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync(["alice", "david"]);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// An anchors second-level HTLC transaction of ours (BOLT 3, B5-HTX-02): the HTLC input with nSequence 1 and the
    /// witness <c>0 &lt;remotesig&gt; &lt;localsig&gt; &lt;preimage or empty&gt; &lt;script&gt;</c>, david's signature
    /// <c>SIGHASH_SINGLE|ANYONECANPAY</c> and ours <c>SIGHASH_ALL</c>, plus at least one wallet input for the fee.
    /// </summary>
    internal static TxIn AssertAnchorsHtlcTransaction(Transaction tx, OutPoint htlcOutPoint)
    {
        var htlcInput = Assert.Single(tx.Inputs, i => i.PrevOut == htlcOutPoint);
        Assert.Equal(1u, htlcInput.Sequence.Value);
        Assert.Equal(5, htlcInput.WitScript.PushCount);
        Assert.Empty(htlcInput.WitScript[0]);
        Assert.Equal(AnchorsHarness.SigHashSingleAnyoneCanPay, AnchorsHarness.SigHashOf(htlcInput.WitScript[1]));
        Assert.Equal(AnchorsHarness.SigHashAll, AnchorsHarness.SigHashOf(htlcInput.WitScript[2]));
        Assert.True(AnchorsHarness.HasForeignInput(tx, htlcOutPoint.Hash),
                    "the zero-fee HTLC transaction has no fee input");

        // SIGHASH_SINGLE: the second-level output sits at the HTLC input's index
        var index = tx.Inputs.IndexOf(htlcInput);
        Assert.True(index < tx.Outputs.Count, "no output at the HTLC input's index");
        Assert.True(tx.Outputs[index].ScriptPubKey.IsScriptType(ScriptType.P2WSH));
        Console.WriteLine($"HTLC transaction {tx.GetHash()}: {tx.Inputs.Count} inputs, {tx.Outputs.Count} outputs, "
                        + $"HTLC input {index}, vsize {tx.GetVirtualSize()}");
        return htlcInput;
    }

    /// <summary>The CSV LND imposes on our <c>to_local</c> (our channel's <c>Remote.ToSelfDelay</c>).</summary>
    private static async Task<ushort> GetOurCsvAsync(NLightningTestNode node, LNDNodeConnection peer,
                                                     OpenChannelClientSubscriptionResponse channel,
                                                     CancellationToken ct)
    {
        var csv = AnchorsHarness.GetModel(node, channel.ChannelId).ChannelParams.Remote.ToSelfDelay;
        var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
        Console.WriteLine($"CSV on our to_local: {csv} (LND remote constraint {lnd?.RemoteConstraints?.CsvDelay})");
        if (lnd?.RemoteConstraints is { } constraints)
            Assert.Equal(constraints.CsvDelay, csv);
        return csv;
    }
}