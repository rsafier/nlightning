using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain;

using Abcd;
using Application.Channels.Safety.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Enums;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 5 plan Proof O3 (our commitment on chain, resolved by <c>LocalCommitResolver</c> through the on-chain
/// resolution executor), against LND david (and alice for the forwarding variant):
/// <list type="bullet">
///   <item>(a) an idle channel with push: our <c>to_local</c> is swept once the CSV LND imposed on us has passed; the
///   wallet gains the <c>to_local</c> output minus the sweep fee, and LND lists a remote force close.</item>
///   <item>(b) our HTLC to david's hold invoice, which david settles after our force close: david claims it on chain
///   with the preimage, we extract it and the payment succeeds with that preimage.</item>
///   <item>(c) the same, but david cancels: at <c>cltv_expiry</c> our HTLC-timeout confirms, the payment fails with
///   <c>permanent_channel_failure</c> once it is reasonably deep, and after the CSV its output is swept.</item>
///   <item>(d) alice → us → david: the downstream channel is force closed while david holds the forwarded HTLC and then
///   cancels; the upstream HTLC is failed back to alice only once our HTLC-timeout is 6 deep, with our
///   <c>permanent_channel_failure</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>Authored against the wave 5 watcher/executor (W5-A: classification, rows, <c>IOutputResolver</c> rounds per
/// block); they cannot pass before it is wired. The force close goes through <see cref="IChannelFailureService"/>, the
/// only broadcaster of our commitment (the <c>forceclosechannel</c> IPC calls the same service).</para>
/// <para>Run with <c>scripts/run-onchain.sh</c> (own process, own fixture, one framework).</para>
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
public class OnchainO3Tests : IAsyncLifetime
{
    private const ulong HoldInvoiceCltvExpiry = 24;
    private const ushort OurCltvExpiryDelta = 40;
    private const uint OurFeeBaseMsat = 1_000;
    private const uint OurFeeProportionalMillionths = 100;
    private const uint ReasonableDepth = OutputResolutionFacts.DefaultReasonableDepth;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public OnchainO3Tests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "o3", configureNodeOptions: o =>
        {
            o.Routing.CltvExpiryDelta = OurCltvExpiryDelta;
            o.Routing.FeeBaseMsat = OurFeeBaseMsat;
            o.Routing.FeeProportionalMillionths = OurFeeProportionalMillionths;
        });
        await _node.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_IdleChannelWithPush_When_WeForceClose_Then_ToLocalSweptAfterLndsCsv()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _fixture.GetLndNode("david");
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var channel = await OpenUsableChannelAsync(node, david, LightningMoney.Satoshis(200_000), ct);
        var csv = await GetOurCsvAsync(node, david, channel, ct);

        // Act: force close, the commitment confirms
        var commitment = await ForceCloseAndConfirmAsync(node, david, channel, ct);
        var toLocalVout = await PollValueAsync(async () => (await GetRowsAsync(node, channel.ChannelId))
                                                        .FirstOrDefault(r => r.TransactionId == commitment.TxId
                                                                          && r.Descriptor
                                                                          == OutputDescriptorKind.DelayedToLocal)
                                                       ?.OutputIndex, "to_local row", ct);
        var toLocal = commitment.Transaction.Outputs[(int)toLocalVout];
        var confirmedAt = commitment.Height;
        var balanceBefore = WalletBalance(node);

        // Mine to one block before the sweep may enter a block: nothing is swept
        await MineToAsync(node, [david], confirmedAt + csv - 2, ct);
        Assert.Null((await GetRowsAsync(node, channel.ChannelId))
                   .Single(r => r.OutputIndex == toLocalVout && r.TransactionId == commitment.TxId)
                   .ResolvingTransactionId);

        // Act: the tip from which nSequence = csv is final in the next block, then that block
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);
        var sweepTxId = await WaitForResolvingTxAsync(node, channel.ChannelId, commitment.TxId, toLocalVout, ct);
        var sweep = await WaitInMempoolAsync(sweepTxId, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);

        // Assert: the sweep spends to_local with nSequence = csv, confirmed, and the wallet gained its output
        Assert.Equal(new OutPoint(commitment.Transaction, toLocalVout), Assert.Single(sweep.Inputs).PrevOut);
        Assert.Equal((uint)csv, sweep.Inputs[0].Sequence.Value);
        var fee = toLocal.Value - sweep.Outputs.Sum(o => o.Value);
        Console.WriteLine($"to_local {toLocal.Value}, sweep {sweep.GetHash()} fee {fee}, csv {csv}");
        Assert.True(fee > Money.Zero && fee < toLocal.Value / 2, $"sweep fee {fee}");
        await Poll.UntilAsync(() => Task.FromResult(WalletBalance(node) - balanceBefore
                                                 == LightningMoney.Satoshis((ulong)(toLocal.Value - fee).Satoshi)),
                              s_timeout, "wallet credited with the sweep", ct);
        await AssertLndClosedAsync(david, channel, commitment, ct);
    }

    [Fact]
    public async Task Given_OurHtlcToAHoldInvoice_When_DavidSettlesAfterOurForceClose_Then_PreimageExtractedAndPaymentSucceeded()
    {
        // Arrange: our payment to david's hold invoice, held
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _fixture.GetLndNode("david");
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var channel = await OpenUsableChannelAsync(node, david, null, ct);
        var (preimage, paymentHash) = LndTestHelpers.NewPreimage();
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(david, paymentHash, 50_000_000, [], ct,
                                                                   "o3 b settled on chain", HoldInvoiceCltvExpiry);
        var inFlight = await node.PayInvoiceAsync(holdInvoice.PaymentRequest, ct, timeoutSeconds: 2);
        Assert.Equal(PaymentStatus.InFlight, inFlight.Status);
        await LndTestHelpers.WaitForInvoiceStateAsync(david, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                      s_timeout, ct);
        await WaitForHtlcInBothCommitmentsAsync(node, channel.ChannelId, HtlcDirection.Outgoing, ct);

        // Act: we force close, then david settles (it claims our offered HTLC on chain with the preimage)
        var commitment = await ForceCloseAndConfirmAsync(node, david, channel, ct);
        await LndTestHelpers.SettleInvoiceAsync(david, preimage, ct);
        var payment = await MineUntilAsync(node, [david], async () =>
        {
            var p = await node.GetPaymentAsync(new Hash(paymentHash), ct);
            return p is { Status: PaymentStatus.Succeeded } ? p : null;
        }, "the payment succeeded from the preimage on chain", ct);

        // Assert: the preimage we learnt on chain is the invoice's; the HTLC output was spent by david
        Assert.Equal(preimage, (byte[])payment.Preimage!.Value);
        var htlcRow = (await GetRowsAsync(node, channel.ChannelId))
                     .Single(r => r.TransactionId == commitment.TxId
                               && r.Descriptor == OutputDescriptorKind.LocalOfferedHtlc);
        Assert.NotEqual(OutputResolutionState.Pending, htlcRow.State);
        Console.WriteLine($"HTLC output {htlcRow.OutputIndex}: {htlcRow.State}, resolved at {htlcRow.ResolvedHeight}");
    }

    [Fact]
    public async Task Given_OurHtlcToAHoldInvoice_When_DavidCancels_Then_HtlcTimeoutAtExpiryPaymentFailedAtDepthAndSecondLevelSwept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _fixture.GetLndNode("david");
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var channel = await OpenUsableChannelAsync(node, david, null, ct);
        var csv = await GetOurCsvAsync(node, david, channel, ct);
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(david, paymentHash, 50_000_000, [], ct,
                                                                   "o3 c timed out on chain", HoldInvoiceCltvExpiry);
        await node.PayInvoiceAsync(holdInvoice.PaymentRequest, ct, timeoutSeconds: 2);
        await LndTestHelpers.WaitForInvoiceStateAsync(david, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                      s_timeout, ct);
        var htlc = await WaitForHtlcInBothCommitmentsAsync(node, channel.ChannelId, HtlcDirection.Outgoing, ct);

        // Act: force close, david gives the HTLC up
        var commitment = await ForceCloseAndConfirmAsync(node, david, channel, ct);
        await LndTestHelpers.CancelInvoiceAsync(david, paymentHash, ct);
        var htlcVout = await PollValueAsync(async () => (await GetRowsAsync(node, channel.ChannelId))
                                                     .FirstOrDefault(r => r.TransactionId == commitment.TxId
                                                                       && r.Descriptor
                                                                       == OutputDescriptorKind.LocalOfferedHtlc)
                                                    ?.OutputIndex, "HTLC row", ct);

        // Mine to one block before cltv_expiry: no HTLC-timeout yet
        await MineToAsync(node, [david], htlc.CltvExpiry - 1, ct);
        Assert.Null((await GetRowsAsync(node, channel.ChannelId))
                   .Single(r => r.TransactionId == commitment.TxId && r.OutputIndex == htlcVout).ResolvingTransactionId);

        // At cltv_expiry: our HTLC-timeout (nLockTime = cltv_expiry) goes out and confirms in the next block
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);
        var timeoutTxId = await WaitForResolvingTxAsync(node, channel.ChannelId, commitment.TxId, htlcVout, ct);
        var timeout = await WaitInMempoolAsync(timeoutTxId, ct);
        Assert.Equal(htlc.CltvExpiry, (uint)timeout.LockTime);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);
        var timeoutHeight = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);

        // Assert: the payment stays in flight until the HTLC-timeout is reasonably deep
        await MineToAsync(node, [david], timeoutHeight + ReasonableDepth - 2, ct);
        Assert.Equal(PaymentStatus.InFlight, (await node.GetPaymentAsync(new Hash(paymentHash), ct))!.Status);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);
        var failed = await Poll.ForAsync(async () => await node.GetPaymentAsync(new Hash(paymentHash), ct) is
        { Status: PaymentStatus.Failed } p
                                                         ? p
                                                         : null, s_timeout, "the payment failed", ct);
        Assert.Equal(FailureCode.PermanentChannelFailure, failed.FailureCode);

        // Act: past the CSV of the HTLC-timeout's output
        await MineToAsync(node, [david], timeoutHeight + csv - 1, ct);
        var secondLevelSweepTxId = await WaitForResolvingTxAsync(node, channel.ChannelId, timeoutTxId, 0, ct);
        var sweep = await WaitInMempoolAsync(secondLevelSweepTxId, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);

        // Assert: the second-level output is swept with nSequence = csv and confirmed
        Assert.Equal(new OutPoint(timeout, 0), Assert.Single(sweep.Inputs).PrevOut);
        Assert.Equal((uint)csv, sweep.Inputs[0].Sequence.Value);
        Assert.True((await _fixture.Bitcoin.GetRawTransactionInfoAsync(sweep.GetHash(), ct)).Confirmations >= 1);
    }

    [Fact]
    public async Task Given_ForwardedHtlc_When_DownstreamForceClosedAndTimedOut_Then_FailedUpstreamOnlyAtReasonableDepth()
    {
        // Arrange: alice -> us -> david (david's hold invoice, reachable only through our private channel)
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var alice = _fixture.GetLndNode("alice");
        var david = _fixture.GetLndNode("david");
        await node.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        var upstream = await OpenUsableChannelAsync(node, alice, LightningMoney.Satoshis(500_000), ct);
        var downstream = await OpenUsableChannelAsync(node, david, null, ct);
        var upstreamLnd = await LndTestHelpers.GetChannelByPointAsync(alice, upstream.ChannelPoint(), ct);
        Assert.NotNull(upstreamLnd);
        var downstreamScid = (await node.GetChannelAsync(downstream.ChannelId, ct)).ShortChannelId;
        Assert.NotNull(downstreamScid);
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        var hint = LndTestHelpers.RouteHint(LndTestHelpers.HopHint(node.NodeIdHex, downstreamScid.Value.ToUInt64(),
                                                                   OurFeeBaseMsat, OurFeeProportionalMillionths,
                                                                   OurCltvExpiryDelta));
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(david, paymentHash, 50_000_000, [hint], ct,
                                                                   "o3 d forwarded, timed out on chain",
                                                                   HoldInvoiceCltvExpiry);
        await LndTestHelpers.ResetMissionControlAsync(alice, ct);
        var payment = LndTestHelpers.SendPaymentV2Async(
            alice, LndTestHelpers.PinnedPayment(holdInvoice.PaymentRequest, [upstreamLnd.ChanId]), ct,
            TimeSpan.FromMinutes(10));
        await LndTestHelpers.WaitForInvoiceStateAsync(david, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                      s_timeout, ct);
        await WaitForHtlcInBothCommitmentsAsync(node, upstream.ChannelId, HtlcDirection.Incoming, ct);
        var outgoing = await WaitForHtlcInBothCommitmentsAsync(node, downstream.ChannelId, HtlcDirection.Outgoing, ct);

        // Act: we force close the downstream channel; david gives the HTLC up; our HTLC-timeout confirms after its
        // cltv_expiry
        var commitment = await ForceCloseAndConfirmAsync(node, david, downstream, ct);
        await LndTestHelpers.CancelInvoiceAsync(david, paymentHash, ct);
        await MineToAsync(node, [alice, david], outgoing.CltvExpiry, ct);
        var htlcVout = (await GetRowsAsync(node, downstream.ChannelId))
                      .Single(r => r.TransactionId == commitment.TxId
                                && r.Descriptor == OutputDescriptorKind.LocalOfferedHtlc).OutputIndex;
        var timeoutTxId = await WaitForResolvingTxAsync(node, downstream.ChannelId, commitment.TxId, htlcVout, ct);
        await WaitInMempoolAsync(timeoutTxId, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [alice, david], [node], ct);
        var timeoutHeight = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);

        // Assert: alice's payment is still in flight one block short of the reasonable depth
        await MineToAsync(node, [alice, david], timeoutHeight + ReasonableDepth - 2, ct);
        Assert.False(await WaitUntilCompletedAsync(payment, TimeSpan.FromSeconds(3), ct),
                     "alice's payment failed before our HTLC-timeout was reasonably deep");

        // Act: depth 6
        await ChainSync.MineAndWaitAsync(_fixture, 1, [alice, david], [node], ct);

        // Assert: failed back to alice by us (index 1) with permanent_channel_failure, and the upstream channel lives
        Assert.True(await WaitUntilCompletedAsync(payment, s_timeout, ct), "alice's payment never failed");
        var result = await payment;
        Console.WriteLine($"Alice's payment {result.Status} ({result.FailureReason})");
        Assert.Equal(Payment.Types.PaymentStatus.Failed, result.Status);
        var attempt = Assert.Single(result.Htlcs);
        Assert.Equal(Failure.Types.FailureCode.PermanentChannelFailure, attempt.Failure.Code);
        Assert.Equal(1u, attempt.Failure.FailureSourceIndex);
        Assert.True((await node.GetChannelAsync(upstream.ChannelId, ct)).IsUsable());
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var line in _node?.NodeLog.TakeLast(300) ?? [])
                Console.WriteLine(line);
            await DockerDiagnostics.DumpContainerLogsAsync(["alice", "david"]);
        }

        if (_node is not null)
            await _node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private sealed record ConfirmedCommitment(TxId TxId, Transaction Transaction, uint Height);

    /// <summary>
    /// Fails the channel through <see cref="IChannelFailureService"/> (our latest commitment, broadcast), mines it and
    /// waits until the node recorded the funding spend as our local commitment.
    /// </summary>
    private async Task<ConfirmedCommitment> ForceCloseAndConfirmAsync(NLightningTestNode node, LNDNodeConnection peer,
                                                                      OpenChannelClientSubscriptionResponse channel,
                                                                      CancellationToken ct)
    {
        var outcome = await node.Services.GetRequiredService<IChannelFailureService>()
                                .FailChannelAsync(channel.ChannelId,
                                                  new ChannelFailureRequest("O3 proof force close",
                                                                            "force closing the channel"), ct);
        Assert.NotNull(outcome.CommitmentTxId);
        var displayTxId = new uint256((byte[])outcome.CommitmentTxId.Value);
        Console.WriteLine($"Force closed {channel.ChannelId}: {outcome.Status}, commitment {displayTxId}");
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.GetRawMempoolAsync(ct)).Contains(displayTxId),
                              s_timeout, "our commitment in the mempool", ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [node], ct);
        var height = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
        var transaction = (await _fixture.Bitcoin.GetRawTransactionInfoAsync(displayTxId, ct)).Transaction;

        await Poll.UntilAsync(async () =>
        {
            using var scope = node.Services.CreateScope();
            var close = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                                   .GetCloseAsync(channel.ChannelId);
            return close is { Kind: ChannelCloseKind.LocalCommitment } && close.CommitmentTransactionId
                == outcome.CommitmentTxId.Value;
        }, s_timeout, "the funding spend classified as our local commitment", ct);
        return new ConfirmedCommitment(outcome.CommitmentTxId.Value, transaction, height);
    }

    /// <summary>The CSV LND imposes on our <c>to_local</c> (our channel's <c>Remote.ToSelfDelay</c>).</summary>
    private static async Task<ushort> GetOurCsvAsync(NLightningTestNode node, LNDNodeConnection peer,
                                                     OpenChannelClientSubscriptionResponse channel,
                                                     CancellationToken ct)
    {
        Assert.True(node.ChannelMemoryRepository.TryGetChannel(channel.ChannelId, out var model));
        var csv = model.ChannelParams.Remote.ToSelfDelay;
        var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
        Console.WriteLine($"CSV on our to_local: {csv} (LND remote constraint {lnd?.RemoteConstraints?.CsvDelay}, "
                        + $"LND version {await LndTestHelpers.GetVersionAsync(peer, ct)})");
        if (lnd?.RemoteConstraints is { } constraints)
            Assert.Equal(constraints.CsvDelay, csv);
        return csv;
    }

    private static async Task<IReadOnlyList<OutputResolutionModel>> GetRowsAsync(NLightningTestNode node,
                                                                                 ChannelId channelId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                          .GetOutputsByChannelIdAsync(channelId);
    }

    /// <summary>The resolving transaction our node recorded for an output.</summary>
    private static Task<TxId> WaitForResolvingTxAsync(NLightningTestNode node, ChannelId channelId, TxId txId,
                                                      uint vout, CancellationToken ct) =>
        PollValueAsync(async () => (await GetRowsAsync(node, channelId))
                                  .FirstOrDefault(r => r.TransactionId == txId && r.OutputIndex == vout)
                                  ?.ResolvingTransactionId, $"a resolving transaction for {vout}", ct);

    /// <summary><see cref="Poll.ForAsync{T}(Func{Task{T}}, TimeSpan, string, CancellationToken, TimeSpan?)"/> for a
    /// value type.</summary>
    private static async Task<T> PollValueAsync<T>(Func<Task<T?>> probe, string description, CancellationToken ct)
        where T : struct
    {
        T? found = null;
        await Poll.UntilAsync(async () => (found = await probe()) is not null, s_timeout, description, ct);
        return found!.Value;
    }

    private async Task<Transaction> WaitInMempoolAsync(TxId txId, CancellationToken ct)
    {
        var displayTxId = new uint256((byte[])txId);
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.GetRawMempoolAsync(ct)).Contains(displayTxId),
                              s_timeout, $"{displayTxId} in the mempool", ct);
        return (await _fixture.Bitcoin.GetRawTransactionInfoAsync(displayTxId, ct)).Transaction;
    }

    private async Task MineToAsync(NLightningTestNode node, LNDNodeConnection[] peers, uint height,
                                   CancellationToken ct)
    {
        var tip = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
        if (height > tip)
            await ChainSync.MineAndWaitAsync(_fixture, (int)(height - tip), peers, [node], ct);
    }

    private async Task<T> MineUntilAsync<T>(NLightningTestNode node, LNDNodeConnection[] peers,
                                            Func<Task<T?>> probe, string what, CancellationToken ct) where T : class
    {
        for (var i = 0; i < 40; i++)
        {
            if (await probe() is { } done)
                return done;

            await ChainSync.MineAndWaitAsync(_fixture, 1, peers, [node], ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return await Poll.ForAsync(probe, s_timeout, what, ct);
    }

    private static LightningMoney WalletBalance(NLightningTestNode node)
    {
        var utxos = node.Services.GetRequiredService<IUtxoMemoryRepository>();
        var height = node.BlockchainMonitor.LastProcessedBlockHeight;
        return utxos.GetConfirmedBalance(height) + utxos.GetUnconfirmedBalance(height);
    }

    private async Task AssertLndClosedAsync(LNDNodeConnection peer, OpenChannelClientSubscriptionResponse channel,
                                            ConfirmedCommitment commitment, CancellationToken ct)
    {
        var displayTxId = new uint256((byte[])commitment.TxId).ToString();
        await Poll.UntilAsync(async () =>
        {
            var closed = await peer.LightningClient.ClosedChannelsAsync(new ClosedChannelsRequest(),
                                                                       cancellationToken: ct);
            var ours = closed.Channels.FirstOrDefault(c => c.ChannelPoint == channel.ChannelPoint());
            if (ours is not null)
            {
                Console.WriteLine($"LND closed the channel: {ours.CloseType}, closing tx {ours.ClosingTxHash}");
                Assert.Equal(ChannelCloseSummary.Types.ClosureType.RemoteForceClose, ours.CloseType);
                Assert.Equal(displayTxId, ours.ClosingTxHash);
                return true;
            }

            await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [_node!], ct);
            return false;
        }, s_timeout, "LND lists the channel as remote force closed", ct);
    }

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(NLightningTestNode node,
        LNDNodeConnection peer, LightningMoney? push, CancellationToken ct)
    {
        var peerAddress = await node.ConnectToAsync(peer, ct);
        var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress,
                                                                               LightningMoney.Satoshis(1_000_000))
        {
            PushAmount = push,
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) to {peer.LocalAlias}");

        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && lnd is { Active: true })
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [node], ct);
            return false;
        }, s_timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [peer], [node], ct);
        return channel;
    }

    private static Task<HtlcRecord> WaitForHtlcInBothCommitmentsAsync(NLightningTestNode node, ChannelId channelId,
                                                                      HtlcDirection direction, CancellationToken ct) =>
        Poll.ForAsync(() =>
        {
            var model = node.ChannelMemoryRepository.TryGetChannel(channelId, out var c) ? c : null;
            return model?.Commitments?.Htlcs.Values.FirstOrDefault(h => h.Direction == direction
                                                                    && h.IsInCommit(CommitmentSide.Local)
                                                                    && h.IsInCommit(CommitmentSide.Remote));
        }, s_timeout, $"{direction} HTLC in both commitments of {channelId}", ct);

    private static async Task<bool> WaitUntilCompletedAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        await Task.WhenAny(task, Task.Delay(timeout, ct));
        return task.IsCompleted;
    }
}