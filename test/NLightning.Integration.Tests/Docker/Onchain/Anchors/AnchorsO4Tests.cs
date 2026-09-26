using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using OutPoint = NBitcoin.OutPoint;

namespace NLightning.Integration.Tests.Docker.Onchain.Anchors;

using Abcd;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Payments.Enums;
using Domain.Protocol.Messages;
using Fixtures;
using Infrastructure.Transport.Interfaces;
using Utils;

/// <summary>
/// BOLT 5 plan Proof O4 on anchors channels (O7-T4; the peer's commitment on chain), against LND david, which
/// force-closes. With anchors every output of ours on its commitment is locked by <c>1 OP_CHECKSEQUENCEVERIFY</c>
/// (BOLT 3), so each spend has nSequence 1 and waits for one confirmation:
/// <list type="bullet">
///   <item>(a) an idle channel with a push to david: our P2WSH <c>to_remote</c> is swept to our wallet with
///   <c>&lt;sig&gt; &lt;pubkey OP_CHECKSIGVERIFY 1 OP_CSV&gt;</c> (B5-RMT-02, O7-T3).</item>
///   <item>(b) our HTLC to david's hold invoice, never settled: after <c>cltv_expiry</c> our timeout claim
///   (nLockTime = <c>cltv_expiry</c>, nSequence 1) confirms and the payment fails once it is reasonably deep
///   (B5-RMT-LO-02).</item>
///   <item>(c) david pays our invoice and our fulfill is saved but never reaches david; david force-closes while we are
///   down: we claim the HTLC with the preimage (nSequence 1) before its <c>cltv_expiry</c> and the invoice is settled
///   (B5-RMT-RO-01).</item>
/// </list>
/// </summary>
/// <remarks>
/// Needs lane O7-X3 (anchors CSV-1 spends in the remote resolver). Run with
/// <c>ONCHAIN_SUITE=anchors scripts/run-onchain.sh</c>.
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
[Trait("Category", AnchorsChannelTests.AnchorsCategory)]
public class AnchorsO4Tests : IAsyncLifetime
{
    private const ulong HoldInvoiceCltvExpiry = 24;
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);

    private readonly AnchorsHarness _harness;
    private NLightningTestNode? _node;

    public AnchorsO4Tests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _node = await _harness.CreateNodeAsync("anchors-o4", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_IdleAnchorsChannelWithPush_When_DavidForceCloses_Then_OurCsvOneToRemoteSweptToOurWallet()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var david = _harness.Fixture.GetLndNode("david");
        var channel = await _harness.OpenAnchorsChannelAsync(Node, david, s_push, ct);

        // Act: david force-closes; the commitment confirms
        var commitmentTxId = await AnchorsHarness.LndForceCloseAsync(david, channel, ct);
        await _harness.MineUntilConfirmedAsync(Node, [david], commitmentTxId, ct);

        // Assert: david's current commitment, our to_remote swept with nSequence 1 by the CSV-1 script
        var close = await AnchorsHarness.WaitForCloseAsync(Node, channel.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);
        Assert.Equal(commitmentTxId, new uint256((byte[])close.CommitmentTransactionId));
        var toRemote = await AnchorsHarness.WaitForRowAsync(Node, channel.ChannelId,
                                                            o => o is
                                                            {
                                                                Descriptor: OutputDescriptorKind.PaymentToRemote,
                                                                ResolvingTransactionId: not null
                                                            }, "our to_remote sweep saved", ct);
        var commitment = await _harness.Fixture.Bitcoin.GetRawTransactionAsync(commitmentTxId, true, ct);
        var toRemoteOutput = commitment.Outputs[(int)toRemote.OutputIndex];
        Assert.True(toRemoteOutput.ScriptPubKey.IsScriptType(ScriptType.P2WSH), "to_remote is not P2WSH");
        AnchorsHarness.FindAnchors(AnchorsHarness.GetModel(Node, channel.ChannelId), commitment);

        var sweepInfo = await _harness.MineUntilConfirmedAsync(Node, [david], toRemote.ResolvingTransactionId!.Value,
                                                               ct);
        var sweep = sweepInfo.Transaction;
        var input = Assert.Single(sweep.Inputs, i => i.PrevOut.Hash == commitmentTxId);
        Assert.Equal(toRemote.OutputIndex, input.PrevOut.N);
        Assert.Equal(1u, input.Sequence.Value);
        Assert.True(AnchorsHarness.IsAnchorsToRemoteSpend(input.WitScript), "not the CSV-1 to_remote spend");
        var fee = await _harness.FeeAsync(sweep, ct);
        Console.WriteLine($"to_remote {toRemoteOutput.Value} at {toRemote.OutputIndex}, sweep {sweep.GetHash()} "
                        + $"fee {fee}");
        Assert.True(fee > Money.Zero && fee < toRemoteOutput.Value / 2, $"sweep fee {fee}");
        await Poll.UntilAsync(() => Node.Services.GetRequiredService<IUtxoMemoryRepository>()
                                        .TryGetUtxo(sweep.GetHash().ToBytes(), 0, out _),
                              AnchorsHarness.Timeout, "the sweep output credited to our wallet", ct);
        await AnchorsHarness.WaitForRowAsync(Node, channel.ChannelId,
                                             o => o.OutputIndex == toRemote.OutputIndex
                                               && o.TransactionId == toRemote.TransactionId
                                               && o.State >= OutputResolutionState.Resolved,
                                             "to_remote resolved", ct);
    }

    [Fact]
    public async Task Given_OurHtlcHeldByDavid_When_DavidForceCloses_Then_CsvOneTimeoutClaimAfterExpiryAndPaymentFailed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var david = _harness.Fixture.GetLndNode("david");
        var channel = await _harness.OpenAnchorsChannelAsync(Node, david, null, ct);
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(david, paymentHash, 50_000_000, [], ct,
                                                                   "o7 o4 b timeout claim", HoldInvoiceCltvExpiry);
        try
        {
            var inFlight = await Node.PayInvoiceAsync(holdInvoice.PaymentRequest, ct, timeoutSeconds: 2);
            Assert.Equal(PaymentStatus.InFlight, inFlight.Status);
            await LndTestHelpers.WaitForInvoiceStateAsync(david, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                          AnchorsHarness.Timeout, ct);
            var htlc = await AnchorsHarness.WaitForHtlcInBothCommitmentsAsync(Node, channel.ChannelId,
                                                                            HtlcDirection.Outgoing, ct);
            Console.WriteLine($"Our HTLC {htlc.Id}: cltv_expiry {htlc.CltvExpiry}");

            // Act: david force-closes
            var commitmentTxId = await AnchorsHarness.LndForceCloseAsync(david, channel, ct);
            await _harness.MineUntilConfirmedAsync(Node, [david], commitmentTxId, ct);
            var close = await AnchorsHarness.WaitForCloseAsync(Node, channel.ChannelId, ct);
            Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);

            // Nothing is claimed before cltv_expiry
            var row = await AnchorsHarness.WaitForRowAsync(Node, channel.ChannelId,
                                                           o => o.Descriptor == OutputDescriptorKind.RemoteReceivedHtlc,
                                                           "our HTLC output row", ct);
            Assert.Equal(htlc.Id, row.HtlcId);
            await _harness.MineToAsync(Node, [david], htlc.CltvExpiry - 1, ct);
            Assert.Null((await AnchorsHarness.GetRowsAsync(Node, channel.ChannelId))
                       .Single(o => o.TransactionId == row.TransactionId && o.OutputIndex == row.OutputIndex)
                       .ResolvingTransactionId);

            // At cltv_expiry our claim (nLockTime = cltv_expiry, nSequence 1) goes out and confirms
            await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [Node], ct);
            var claimTxId = await AnchorsHarness.WaitForResolvingTxAsync(Node, channel.ChannelId, row.TransactionId,
                                                                         row.OutputIndex, ct);
            var claim = (await _harness.MineUntilConfirmedAsync(Node, [david], claimTxId, ct)).Transaction;
            var input = Assert.Single(claim.Inputs, i => i.PrevOut == new OutPoint(commitmentTxId, row.OutputIndex));
            Assert.Equal(htlc.CltvExpiry, claim.LockTime.Value);
            Assert.Equal(1u, input.Sequence.Value);
            Assert.Empty(input.WitScript[1]);

            // Assert: the payment fails once the claim is reasonably deep, not before
            var payment = await Node.GetPaymentAsync(new Hash(paymentHash), ct);
            Assert.Equal(PaymentStatus.InFlight, payment?.Status);
            await ChainSync.MineAndWaitAsync(_harness.Fixture, (int)AnchorsHarness.ReasonableDepth - 1, [david],
                                             [Node], ct);
            await Poll.UntilAsync(async () => (await Node.GetPaymentAsync(new Hash(paymentHash), ct))?.Status
                                           == PaymentStatus.Failed,
                                  AnchorsHarness.Timeout, "our payment failed after the on-chain timeout", ct);
        }
        finally
        {
            await AnchorsHarness.CancelHoldInvoiceQuietlyAsync(david, paymentHash);
        }
    }

    [Fact]
    public async Task Given_OurFulfillNeverReachedDavid_When_DavidForceCloses_Then_CsvOnePreimageClaimBeforeExpiry()
    {
        // Arrange: david (with the push) pays our invoice; the wire dies right after our fulfill is saved
        var ct = TestContext.Current.CancellationToken;
        var david = _harness.Fixture.GetLndNode("david");
        var channel = await _harness.OpenAnchorsChannelAsync(Node, david, s_push, ct);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(david, channel.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);
        var tcpService = Assert.IsType<CrashableTcpService>(Node.Services.GetRequiredService<ITcpService>());
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Node.ChannelManager.OnResponseMessageReady += (_, args) =>
        {
            // Raised under the channel lock right after the fulfill's save: cut every connection before it goes out
            if (args.ResponseMessage is not UpdateFulfillHtlcMessage || crashed.Task.IsCompleted)
                return;

            tcpService.CrashAsync().GetAwaiter().GetResult();
            crashed.TrySetResult();
        };
        var invoice = await Node.CreateInvoiceAsync(LightningMoney.Satoshis(50_000), "o7 o4 c claim with preimage", ct);
        await PayUntilSentAsync(david, invoice.Bolt11, lndChannel.ChanId, crashed.Task, ct);
        var htlc = Assert.Single(AnchorsHarness.GetHtlcs(Node, channel.ChannelId),
                                 h => h.Direction == HtlcDirection.Incoming);
        Assert.NotNull(htlc.Removal);
        Assert.True(htlc.Removal.IsFulfill);
        Console.WriteLine($"Fulfill of HTLC {htlc.Id} saved and lost; cltv_expiry {htlc.CltvExpiry}");
        await Node.StopAsync();

        // Act: david force-closes while we are down; we come back after 1 block
        var commitmentTxId = await AnchorsHarness.LndForceCloseAsync(david, channel, ct);
        await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [], ct);
        await Node.StartAsync(ct);
        await ChainSync.WaitAllAtTipAsync(_harness.Fixture, [david], [Node], ct);

        // Assert: the HTLC is claimed with <sig> <preimage>, nSequence 1, before cltv_expiry
        var close = await AnchorsHarness.WaitForCloseAsync(Node, channel.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);
        Assert.Equal(commitmentTxId, new uint256((byte[])close.CommitmentTransactionId));
        var row = await AnchorsHarness.WaitForRowAsync(Node, channel.ChannelId,
                                                       o => o is
                                                       {
                                                           Descriptor: OutputDescriptorKind.RemoteOfferedHtlc,
                                                           ResolvingTransactionId: not null
                                                       }, "our preimage claim saved", ct);
        Assert.Equal(htlc.Id, row.HtlcId);
        var info = await _harness.MineUntilConfirmedAsync(Node, [david], row.ResolvingTransactionId!.Value, ct);
        var claim = info.Transaction;
        var confirmedAt = (uint)(await _harness.Fixture.Bitcoin.GetBlockCountAsync(ct) - info.Confirmations + 1);
        Assert.True(confirmedAt < htlc.CltvExpiry, $"claimed at {confirmedAt}, expiry {htlc.CltvExpiry}");
        var input = Assert.Single(claim.Inputs, i => i.PrevOut == new OutPoint(commitmentTxId, row.OutputIndex));
        Assert.Equal(0U, claim.LockTime.Value);
        Assert.Equal(1u, input.Sequence.Value);
        Assert.Equal((byte[])htlc.Removal.PaymentPreimage!.Value, input.WitScript[1]);

        var ours = await Node.GetInvoiceAsync(invoice.PaymentHash, ct);
        Assert.NotNull(ours);
        Assert.Equal(InvoiceStatus.Settled, ours.Status);
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync(["david"]);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Starts a pinned LND payment and returns once <paramref name="sent"/> completes; a payment LND fails at once for
    /// want of a route (its router adds the private edge a moment after the channel turns active, NL-319) is started
    /// again.
    /// </summary>
    private static async Task PayUntilSentAsync(LNDNodeConnection lnd, string bolt11, ulong chanId, Task sent,
                                                CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(AnchorsHarness.Timeout);
        while (true)
        {
            await LndTestHelpers.ResetMissionControlAsync(lnd, ct);
            var payment = LndTestHelpers.SendPaymentV2Async(lnd, LndTestHelpers.PinnedPayment(bolt11, [chanId]), ct,
                                                            TimeSpan.FromMinutes(5));
            if (await Task.WhenAny(sent, payment).WaitAsync(deadline.Token) == sent)
                return;

            var result = await payment;
            if (result.FailureReason is not (PaymentFailureReason.FailureReasonInsufficientBalance
                                             or PaymentFailureReason.FailureReasonNoRoute))
                Assert.Fail($"{lnd.LocalAlias}'s payment ended before it reached us: {result.Status} "
                          + $"{result.FailureReason}");

            Console.WriteLine($"{lnd.LocalAlias}'s payment failed with {result.FailureReason}; retrying");
            await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token);
        }
    }
}