using System.Security.Cryptography;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain;

using Abcd;
using Anchors;
using Cheater;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Fixtures;
using Infrastructure.Transport.Interfaces;
using Infrastructure.VlsSigning;
using RemoteSigning.Tests;
using Utils;

/// <summary>
/// BOLT 5 on-chain resolution of a VLS-signed node (Rust gateway at the pinned VLS revision) against LND david, on
/// static_remotekey channels with an HTLC in flight each way: every output of ours is signed through VLS's semantic
/// API (<c>sign_holder_htlc_tx</c>, <c>sign_delayed_sweep</c>, <c>sign_counterparty_htlc_sweep</c>,
/// <c>sign_justice_sweep</c>, the unilateral-close key for <c>to_remote</c>) and confirmed by Bitcoin Core:
/// <list type="bullet">
///   <item>(1) our <c>forceclosechannel</c>: HTLC-success with the preimage, HTLC-timeout at <c>cltv_expiry</c>, both
///   second-level outputs and our <c>to_local</c> swept after the CSV.</item>
///   <item>(2) david's force close: our preimage claim, our timeout claim at <c>cltv_expiry</c> and our
///   <c>to_remote</c> sweep.</item>
///   <item>(3) david restarts on an old <c>channel.db</c> holding an HTLC and force-closes with the revoked commitment
///   while we are down: the penalty takes its <c>to_local</c>, the HTLC and our <c>to_remote</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <c>Explicit</c>: it needs the pinned gateway binary (<c>tools/vls-gateway/run.sh build</c>, then
/// <c>NLTG_VLS_GATEWAY_BINARY</c>). Run it with <c>NLTG_VLS_GATEWAY_BINARY=... scripts/run-cluster.sh -n 1 --suite
/// onchain --class NLightning.Integration.Tests.Docker.Onchain.VlsOnchainResolutionTests --explicit only</c>. The HTLC
/// david offers us stays on the commitments with its preimage known to us as in the taproot proof: our fulfill is saved
/// and every connection is cut before it goes out.
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
public class VlsOnchainResolutionTests : IAsyncLifetime
{
    private const ulong HoldInvoiceCltvExpiry = 24;
    private const long HtlcSat = 50_000;
    private const long LiquiditySat = 300_000;
    private const string David = "david";

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly AnchorsHarness _harness;
    private readonly List<VlsNode> _nodes = [];

    public VlsOnchainResolutionTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    private LndNodeConnection Lnd => _fixture.GetLndNode(David);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_HtlcsBothWays_When_WeForceClose_Then_VlsSignsEveryResolutionOfOurCommitment(bool anchors)
    {
        // Arrange: our VLS channel to david, liquidity paid to david, our HTLC held by his hold invoice and his HTLC
        // fulfilled by us but cut off before the fulfill reaches him
        var ct = TestContext.Current.CancellationToken;
        var vls = await CreateVlsNodeAsync(anchors ? "vls-fc-ours-anchors" : "vls-fc-ours", anchors ? (byte)0x71 : (byte)0x61,
                                           anchors, ct);
        var node = vls.Node;
        var lnd = Lnd;
        var channel = await OpenChannelAsync(vls, lnd, ct);
        var csv = AnchorsHarness.GetModel(node, channel.ChannelId).ChannelParams.Remote.ToSelfDelay;
        var (_, holdHash) = LndTestHelpers.NewPreimage();
        var hold = await LndTestHelpers.AddHoldInvoiceAsync(lnd, holdHash, HtlcSat * 1_000, [], ct, "vls fc ours",
                                                            HoldInvoiceCltvExpiry);
        try
        {
            var outgoing = await SendHeldPaymentAsync(vls, lnd, channel.ChannelId, hold.PaymentRequest, holdHash, ct);
            var (incoming, invoice, lndPayment) = await ReceiveFulfillCutOffAsync(node, lnd, channel, ct);

            // Act 1: forceclosechannel (VLS signs the commitment for broadcast); it confirms
            var forceClose = await HandleAsync<ForceCloseChannelClientRequest, ForceCloseChannelClientResponse>(
                                 node, new ForceCloseChannelClientRequest(channel.ChannelId), ct);
            Assert.Equal("Broadcast", forceClose.Status);
            Assert.NotNull(forceClose.CommitmentTxId);
            var commitmentTxId = forceClose.CommitmentTxId.Value;
            var commitmentInfo = await _harness.MineUntilConfirmedAsync(node, [lnd], commitmentTxId, ct);
            var commitment = commitmentInfo.Transaction;
            var commitmentHeight = await ConfirmationHeightAsync(commitmentInfo, ct);
            var close = await AnchorsHarness.WaitForCloseAsync(node, channel.ChannelId, ct);
            Assert.Equal(ChannelCloseKind.LocalCommitment, close.Kind);
            var offeredRow = await WaitForRowAsync(node, channel.ChannelId, commitmentTxId,
                                                   OutputDescriptorKind.LocalOfferedHtlc, ct);
            var receivedRow = await WaitForRowAsync(node, channel.ChannelId, commitmentTxId,
                                                    OutputDescriptorKind.LocalReceivedHtlc, ct);
            var toLocalRow = await WaitForRowAsync(node, channel.ChannelId, commitmentTxId,
                                                   OutputDescriptorKind.DelayedToLocal, ct);
            Assert.Equal(outgoing.Id, offeredRow.HtlcId);
            Assert.Equal(incoming.Id, receivedRow.HtlcId);
            await ChainSync.MineUntilLndSweptAsync(_fixture, lnd, [node], commitment.GetHash(), ct);
            await LndTestHelpers.CancelInvoiceAsync(lnd, holdHash, ct);

            // Assert 1: our HTLC-success (VLS sign_holder_htlc_tx) with the preimage, before the expiry; LND learns it
            var successTxId = await _harness.MineUntilResolvingTxAsync(node, [lnd], channel.ChannelId, commitmentTxId,
                                                                       receivedRow.OutputIndex, ct);
            var successInfo = await _harness.MineUntilConfirmedAsync(node, [lnd], successTxId, ct);
            var success = successInfo.Transaction;
            var successInput = AssertHtlcTransaction(success, new OutPoint(commitment, receivedRow.OutputIndex),
                                                     anchors);
            Assert.Equal(0u, (uint)success.LockTime);
            Assert.Equal((byte[])incoming.Removal!.PaymentPreimage!.Value, successInput.WitScript[3]);
            var successHeight = await ConfirmationHeightAsync(successInfo, ct);
            Assert.True(successHeight < incoming.CltvExpiry, $"HTLC-success at {successHeight}");
            var lndResult = await _harness.MineUntilAsync(node, [lnd], async () =>
            {
                await Task.WhenAny(lndPayment, Task.Delay(TimeSpan.FromSeconds(2), ct));
                return lndPayment.IsCompleted ? await lndPayment : null;
            }, "LND's payment completed", ct);
            Assert.Equal(Payment.Types.PaymentStatus.Succeeded, lndResult.Status);
            Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(Convert.FromHexString(lndResult.PaymentPreimage)));

            // Assert 2: our HTLC-timeout (VLS sign_holder_htlc_tx) at cltv_expiry, not before; our payment fails
            await _harness.MineToAsync(node, [lnd], outgoing.CltvExpiry - 1, ct);
            Assert.Null((await AnchorsHarness.GetRowsAsync(node, channel.ChannelId))
                       .Single(r => r.TransactionId == commitmentTxId && r.OutputIndex == offeredRow.OutputIndex)
                       .ResolvingTransactionId);
            await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [node], ct);
            var timeoutTxId = await AnchorsHarness.WaitForResolvingTxAsync(node, channel.ChannelId, commitmentTxId,
                                                                           offeredRow.OutputIndex, ct);
            var timeoutInfo = await _harness.MineUntilConfirmedAsync(node, [lnd], timeoutTxId, ct);
            var timeout = timeoutInfo.Transaction;
            var timeoutInput = AssertHtlcTransaction(timeout, new OutPoint(commitment, offeredRow.OutputIndex),
                                                     anchors);
            Assert.Empty(timeoutInput.WitScript[3]);
            Assert.Equal(outgoing.CltvExpiry, (uint)timeout.LockTime);
            var timeoutHeight = await ConfirmationHeightAsync(timeoutInfo, ct);
            await _harness.MineToAsync(node, [lnd], timeoutHeight + AnchorsHarness.ReasonableDepth - 1, ct);
            var failed = await Poll.ForAsync(async () =>
            {
                var payment = await node.GetPaymentAsync(new Hash(holdHash), ct);
                return payment is { Status: PaymentStatus.Failed } ? payment : null;
            }, s_timeout, "our payment failed", ct);
            Assert.Equal(FailureCode.PermanentChannelFailure, failed.FailureCode);

            // Assert 3: after the CSV both second-level outputs and our to_local (VLS sign_delayed_sweep)
            var successSweep = await SweepDelayedAsync(node, lnd, channel.ChannelId, success, 0, successHeight, csv, ct);
            var timeoutSweep = await SweepDelayedAsync(node, lnd, channel.ChannelId, timeout, 0, timeoutHeight, csv, ct);
            var toLocalSweep = await SweepDelayedAsync(node, lnd, channel.ChannelId, commitment, toLocalRow.OutputIndex,
                                                       commitmentHeight, csv, ct);
            Console.WriteLine($"VLS-signed: HTLC-success {success.GetHash()}, HTLC-timeout {timeout.GetHash()}, "
                            + $"sweeps {successSweep.GetHash()}, {timeoutSweep.GetHash()}, to_local "
                            + $"{toLocalSweep.GetHash()}");

            // Assert 4: every output of ours irrevocably resolved, the channel Closed, LND lists our force close
            await AssertAllResolvedAndClosedAsync(node, lnd, channel.ChannelId, ct);
            await AssertLndClosedAsync(node, lnd, channel.ChannelPoint, commitment.GetHash(),
                                       ChannelCloseSummary.Types.ClosureType.RemoteForceClose, ct);
            AssertVlsSigned(vls);
        }
        finally
        {
            await AnchorsHarness.CancelHoldInvoiceQuietlyAsync(lnd, holdHash);
        }
    }

    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_HtlcsBothWays_When_LndForceCloses_Then_VlsSignsOurClaimsAndToRemoteSweep(bool anchors)
    {
        // Arrange: as in the first proof, both HTLCs on the commitments, the connection cut
        var ct = TestContext.Current.CancellationToken;
        var vls = await CreateVlsNodeAsync(anchors ? "vls-fc-lnd-anchors" : "vls-fc-lnd", anchors ? (byte)0x72 : (byte)0x62,
                                           anchors, ct);
        var node = vls.Node;
        var lnd = Lnd;
        var channel = await OpenChannelAsync(vls, lnd, ct);
        var (_, holdHash) = LndTestHelpers.NewPreimage();
        var hold = await LndTestHelpers.AddHoldInvoiceAsync(lnd, holdHash, HtlcSat * 1_000, [], ct, "vls fc lnd",
                                                            HoldInvoiceCltvExpiry);
        try
        {
            var outgoing = await SendHeldPaymentAsync(vls, lnd, channel.ChannelId, hold.PaymentRequest, holdHash, ct);
            var (incoming, invoice, lndPayment) = await ReceiveFulfillCutOffAsync(node, lnd, channel, ct);

            // Act: david force-closes; his commitment confirms
            var commitmentTxId = await ForceCloseWhenStartedAsync(lnd, channel.ChannelPoint, ct);
            TxId lndCommitment = commitmentTxId.ToBytes();
            await _harness.MineUntilConfirmedAsync(node, [lnd], lndCommitment, ct);
            var close = await AnchorsHarness.WaitForCloseAsync(node, channel.ChannelId, ct);
            Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);
            var offeredByLnd = await WaitForRowAsync(node, channel.ChannelId, lndCommitment,
                                                     OutputDescriptorKind.RemoteOfferedHtlc, ct);
            var receivedByLnd = await WaitForRowAsync(node, channel.ChannelId, lndCommitment,
                                                      OutputDescriptorKind.RemoteReceivedHtlc, ct);
            Assert.Equal(incoming.Id, offeredByLnd.HtlcId);
            Assert.Equal(outgoing.Id, receivedByLnd.HtlcId);
            await LndTestHelpers.CancelInvoiceAsync(lnd, holdHash, ct);

            // Assert 1: our static to_remote swept to our wallet (VLS's unilateral-close key): <sig> <pubkey>
            var toRemote = await AnchorsHarness.WaitForRowAsync(node, channel.ChannelId,
                                                                o => o is
                                                                {
                                                                    Descriptor: OutputDescriptorKind.PaymentToRemote,
                                                                    ResolvingTransactionId: not null
                                                                }, "our to_remote sweep saved", ct);
            var toRemoteSweep = (await _harness.MineUntilConfirmedAsync(node, [lnd],
                                                                        toRemote.ResolvingTransactionId!.Value, ct))
               .Transaction;
            var toRemoteInput = Assert.Single(toRemoteSweep.Inputs,
                                              i => i.PrevOut == new OutPoint(commitmentTxId, toRemote.OutputIndex));
            // <sig> <pubkey>, or with anchors <sig> <to_remote script> at nSequence 1
            Assert.Equal(2, toRemoteInput.WitScript.PushCount);
            Assert.Equal(AnchorsHarness.SigHashAll, toRemoteInput.WitScript[0][^1]);
            if (anchors)
            {
                Assert.Equal(1u, toRemoteInput.Sequence.Value);
                Assert.True(AnchorsHarness.IsAnchorsToRemoteSpend(toRemoteInput.WitScript));
            }

            // Assert 2: the preimage claim (VLS sign_counterparty_htlc_sweep) before the expiry; LND's payment succeeds
            var claimTxId = await _harness.MineUntilResolvingTxAsync(node, [lnd], channel.ChannelId, lndCommitment,
                                                                     offeredByLnd.OutputIndex, ct);
            var claimInfo = await _harness.MineUntilConfirmedAsync(node, [lnd], claimTxId, ct);
            var claim = claimInfo.Transaction;
            var claimInput = Assert.Single(claim.Inputs,
                                           i => i.PrevOut == new OutPoint(commitmentTxId, offeredByLnd.OutputIndex));
            Assert.Equal(0u, claim.LockTime.Value);
            Assert.Equal(anchors ? 1u : SweepFeePolicy.RbfSequence, claimInput.Sequence.Value);
            Assert.Equal(3, claimInput.WitScript.PushCount);
            Assert.Equal((byte[])incoming.Removal!.PaymentPreimage!.Value, claimInput.WitScript[1]);
            Assert.True(await ConfirmationHeightAsync(claimInfo, ct) < incoming.CltvExpiry);
            var lndResult = await _harness.MineUntilAsync(node, [lnd], async () =>
            {
                await Task.WhenAny(lndPayment, Task.Delay(TimeSpan.FromSeconds(2), ct));
                return lndPayment.IsCompleted ? await lndPayment : null;
            }, "LND's payment completed", ct);
            Assert.Equal(Payment.Types.PaymentStatus.Succeeded, lndResult.Status);
            Assert.Equal(InvoiceStatus.Settled, (await node.GetInvoiceAsync(invoice.PaymentHash, ct))?.Status);

            // Assert 3: the timeout claim (VLS sign_counterparty_htlc_sweep) at cltv_expiry: <sig> <> <script>
            await _harness.MineToAsync(node, [lnd], outgoing.CltvExpiry - 1, ct);
            Assert.Null((await AnchorsHarness.GetRowsAsync(node, channel.ChannelId))
                       .Single(r => r.TransactionId == lndCommitment && r.OutputIndex == receivedByLnd.OutputIndex)
                       .ResolvingTransactionId);
            await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [node], ct);
            var timeoutTxId = await AnchorsHarness.WaitForResolvingTxAsync(node, channel.ChannelId, lndCommitment,
                                                                           receivedByLnd.OutputIndex, ct);
            var timeoutInfo = await _harness.MineUntilConfirmedAsync(node, [lnd], timeoutTxId, ct);
            var timeoutClaim = timeoutInfo.Transaction;
            var timeoutInput = Assert.Single(timeoutClaim.Inputs,
                                             i => i.PrevOut == new OutPoint(commitmentTxId, receivedByLnd.OutputIndex));
            Assert.Equal(outgoing.CltvExpiry, timeoutClaim.LockTime.Value);
            Assert.Equal(3, timeoutInput.WitScript.PushCount);
            Assert.Equal(anchors ? 1u : SweepFeePolicy.RbfSequence, timeoutInput.Sequence.Value);
            Assert.Empty(timeoutInput.WitScript[1]);
            var timeoutHeight = await ConfirmationHeightAsync(timeoutInfo, ct);
            await _harness.MineToAsync(node, [lnd], timeoutHeight + AnchorsHarness.ReasonableDepth - 1, ct);
            var failed = await Poll.ForAsync(async () =>
            {
                var payment = await node.GetPaymentAsync(new Hash(holdHash), ct);
                return payment is { Status: PaymentStatus.Failed } ? payment : null;
            }, s_timeout, "our payment failed", ct);
            Assert.Equal(FailureCode.PermanentChannelFailure, failed.FailureCode);

            // Assert 4: every output of ours irrevocably resolved, the channel Closed, LND lists its force close
            await AssertAllResolvedAndClosedAsync(node, lnd, channel.ChannelId, ct);
            await AssertLndClosedAsync(node, lnd, channel.ChannelPoint, commitmentTxId,
                                       ChannelCloseSummary.Types.ClosureType.LocalForceClose, ct);
            AssertVlsSigned(vls);
        }
        finally
        {
            await AnchorsHarness.CancelHoldInvoiceQuietlyAsync(lnd, holdHash);
        }
    }

    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_LndRestartsOnAnOldChannelDbWithAnHtlc_When_ItForceCloses_Then_VlsSignsThePenalty(bool anchors)
    {
        // Arrange: payments both ways, our HTLC held by david while his channel.db is copied, then settled and more
        // payments both ways, so the copied state is revoked
        var ct = TestContext.Current.CancellationToken;
        var vls = await CreateVlsNodeAsync(anchors ? "vls-penalty-anchors" : "vls-penalty", anchors ? (byte)0x73 : (byte)0x63,
                                           anchors, ct);
        var node = vls.Node;
        var lnd = Lnd;
        var channel = await OpenChannelAsync(vls, lnd, ct);
        var channelId = channel.ChannelId;
        await AnchorsHarness.LndPaysUsAsync(node, lnd, channel.LndChanId, channelId, channel.ChannelPoint, 30_000, ct);

        var (holdPreimage, holdHash) = LndTestHelpers.NewPreimage();
        var hold = await LndTestHelpers.AddHoldInvoiceAsync(lnd, holdHash, HtlcSat * 1_000, [], ct,
                                                            "vls revoked htlc");
        await SendHeldPaymentAsync(vls, lnd, channelId, hold.PaymentRequest, holdHash, ct);
        var rollback = new LndChannelDbRollback(_fixture, David);
        await rollback.TakeSnapshotAsync(ct);
        lnd = Lnd;
        await WaitUsableAsync(node, lnd, channelId, ct);
        var snapshotState = await node.GetChannelAsync(channelId, ct);
        Assert.Equal(1, snapshotState.OfferedHtlcCount);
        await LndTestHelpers.SettleInvoiceAsync(lnd, holdPreimage, ct);
        await Poll.ForAsync(async () =>
        {
            var payment = await node.GetPaymentAsync(new Hash(holdHash), ct);
            return payment?.Status == PaymentStatus.Succeeded ? payment : null;
        }, s_timeout, "our held payment succeeded after david's restart", ct);
        // VLS refuses HTLCs trimmed on either commitment: at 10,000 sat/kw every amount stays above 20,000 sat
        for (var i = 0; i < 2; i++)
        {
            await PayLndApprovedAsync(vls, lnd, channel, 20_000, ct);
            await AnchorsHarness.LndPaysUsAsync(node, lnd, channel.LndChanId, channelId, channel.ChannelPoint, 20_000,
                                                ct);
        }

        var now = await node.GetChannelAsync(channelId, ct);
        Assert.True(now.RemoteCommitmentNumber > snapshotState.RemoteCommitmentNumber + 1, now.Describe());
        var walletBefore = AnchorsHarness.WalletBalance(node);
        await node.StopAsync();

        // Act: david restarts on the old database and force-closes while we are down; one block; we start again
        await rollback.RestoreSnapshotAsync(ct);
        lnd = Lnd;
        var revokedTxId = await ForceCloseWhenStartedAsync(lnd, channel.ChannelPoint, ct);
        var revoked = await _fixture.Bitcoin.GetRawTransactionAsync(revokedTxId, true, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [], ct);
        await node.StartAsync(ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [lnd], [node], ct);

        // Assert: classified as revoked, holding the HTLC
        var close = await AnchorsHarness.WaitForCloseAsync(node, channelId, ct);
        Assert.Equal(ChannelCloseKind.RevokedCommitment, close.Kind);
        Assert.Equal((TxId)revokedTxId.ToBytes(), close.CommitmentTransactionId);
        var rows = await Poll.ForAsync(async () =>
        {
            var all = await AnchorsHarness.GetRowsAsync(node, channelId);
            return all.Any(r => r.Descriptor == OutputDescriptorKind.RevokedHtlc) ? all : null;
        }, s_timeout, "the revoked HTLC output recorded", ct);
        foreach (var row in rows)
            Console.WriteLine($"Row {row.TransactionId}:{row.OutputIndex} {row.Descriptor} {row.State}");
        var takenVouts = Enumerable.Range(0, revoked.Outputs.Count).Select(v => (uint)v)
                                   .Where(v => revoked.Outputs[(int)v].Value.Satoshi != AnchorsHarness.AnchorAmountSat)
                                   .ToList();
        Assert.Equal(3, takenVouts.Count); // david's to_local, our to_remote, the HTLC (anchors left out)

        // Every output is spent by a penalty of ours (VLS sign_justice_sweep, and the to_remote key), all confirmed
        var ours = await MineUntilAllTakenAsync(node, lnd, channelId, revokedTxId, takenVouts, ct);
        var used = ours.Where(t => t.Inputs.Any(i => i.PrevOut.Hash == revokedTxId && takenVouts.Contains(i.PrevOut.N)))
                       .ToList();
        var inputs = used.SelectMany(t => t.Inputs).Where(i => i.PrevOut.Hash == revokedTxId).ToList();
        var htlcVout = rows.Single(r => r.Descriptor == OutputDescriptorKind.RevokedHtlc).OutputIndex;
        var toLocalVout = rows.Single(r => r.Descriptor == OutputDescriptorKind.RevokedToLocal).OutputIndex;
        var toRemoteVout = rows.Single(r => r.Descriptor == OutputDescriptorKind.PaymentToRemote).OutputIndex;
        // The HTLC by the revocation key: <revocation_sig> <revocationpubkey> <script>
        var htlcInput = Assert.Single(inputs, i => i.PrevOut.N == htlcVout);
        Assert.Equal(3, htlcInput.WitScript.PushCount);
        Assert.Equal(33, htlcInput.WitScript[1].Length);
        // david's to_local: <revocation_sig> 1 <script>
        var toLocalInput = Assert.Single(inputs, i => i.PrevOut.N == toLocalVout);
        Assert.Equal(3, toLocalInput.WitScript.PushCount);
        Assert.Equal([1], toLocalInput.WitScript[1]);
        // Our to_remote: <sig> <payment pubkey>, or with anchors <sig> <to_remote script> after its 1-block CSV
        var toRemoteInput = Assert.Single(inputs, i => i.PrevOut.N == toRemoteVout);
        Assert.Equal(2, toRemoteInput.WitScript.PushCount);
        Assert.Equal(anchors, AnchorsHarness.IsAnchorsToRemoteSpend(toRemoteInput.WitScript));
        Assert.All(inputs, i => Assert.Equal(AnchorsHarness.SigHashAll, i.WitScript[0][^1]));

        // The wallet gained the whole channel less the revoked commitment's fee and our penalty fees
        var gained = 0L;
        foreach (var tx in used)
            gained += tx.TotalOut.Satoshi;
        var taken = takenVouts.Sum(v => revoked.Outputs[(int)v].Value.Satoshi);
        var commitmentFee = (await _harness.FeeAsync(revoked, ct)).Satoshi;
        Console.WriteLine($"Taken outputs {taken} sat, gained {gained} sat, commitment fee {commitmentFee} sat");
        Assert.Equal((long)s_capacity.Satoshi,
                     taken + commitmentFee + (anchors ? 2 * AnchorsHarness.AnchorAmountSat : 0));
        Assert.InRange(taken - gained, 1, (long)s_capacity.Satoshi / 100);
        await Poll.UntilAsync(() => (AnchorsHarness.WalletBalance(node) - walletBefore).Satoshi == gained,
                              s_timeout, $"our wallet gained {gained} sat", ct);
        await AssertAllResolvedAndClosedAsync(node, lnd, channelId, ct);
        AssertVlsSigned(vls);
    }

    public async ValueTask DisposeAsync()
    {
        if (TestDiagnostics.CurrentTestFailed && _fixture.UnavailableReason is null)
        {
            foreach (var vls in _nodes)
                foreach (var line in vls.Node.NodeLog.TakeLast(300))
                    Console.WriteLine(line);
            await _fixture.DumpLndLogsAsync([David]);
        }

        foreach (var vls in _nodes)
        {
            await vls.Node.DisposeAsync();
            await vls.Gateway.DisposeAsync();
        }

        _nodes.Clear();
        GC.SuppressFinalize(this);
    }

    #region Setup

    private sealed record VlsNode(NLightningTestNode Node, VlsGatewayFixture Gateway, VlsSecureKeyManager Keys,
                                  VlsPaymentApprovalClient Approval, bool Anchors);

    private sealed record VlsChannel(ChannelId ChannelId, string ChannelPoint, ulong LndChanId);

    /// <summary>
    /// A fresh VLS identity (its own gateway process and seed) and a node signing through it, with the profile the
    /// VLS adapter supports (static_remotekey or zero-fee-HTLC anchors, no dust HTLCs, a 1,000 sat HTLC minimum).
    /// </summary>
    private async Task<VlsNode> CreateVlsNodeAsync(string name, byte seed, bool anchors, CancellationToken ct)
    {
        _fixture.SkipIfUnavailable();
        var gateway = new VlsGatewayFixture(seed);
        await gateway.InitializeAsync(ct);
        var connection = new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = gateway.SocketPath, TokenFile = gateway.TokenFile, Network = "regtest" });
        var keys = new VlsSecureKeyManager(connection);
        var node = await NLightningTestNode.CreateAsync(_fixture, name, configureNodeOptions: o => VlsProfile(o, anchors),
                                                        secureKeyManager: keys);
        node.VlsSignerConnection = connection;
        node.ExtraConfiguration["Signing:Mode"] = "Vls";
        node.ExtraConfiguration["Signing:SocketPath"] = gateway.SocketPath;
        node.ExtraConfiguration["Signing:AuthTokenFile"] = gateway.TokenFile;
        var vls = new VlsNode(node, gateway, keys,
                              new VlsPaymentApprovalClient(gateway.ApprovalSocketPath, gateway.ApprovalTokenFile),
                              anchors);
        _nodes.Add(vls);
        await node.StartAsync(ct);
        return vls;
    }

    private static void VlsProfile(NodeOptions options, bool anchors)
    {
        var features = options.Features;
        features.OptionAnchors = anchors ? FeatureSupport.Optional : FeatureSupport.No;
        features.DualFund = features.OptionQuiesce = features.OptionSplice =
            features.OptionSimpleTaproot = features.OptionGossipV2 = features.OptionSimpleClose =
            features.OptionRouteBlinding = features.OptionOnionMessages = features.OptionTrampolineRouting =
            features.OptionProvideStorage = features.BeyondSegwitShutdown = features.ZeroConf = FeatureSupport.No;
        options.MaxDustHtlcExposureMsat = 0;
        options.HtlcMinimumAmount = LightningMoney.Satoshis(1_000);
    }

    /// <summary>
    /// Funds our VLS wallet, opens a private static_remotekey channel of <see cref="s_capacity"/> to
    /// <paramref name="lnd"/> (VLS refuses an outbound push) and gives the peer <see cref="LiquiditySat"/> through an
    /// approved payment.
    /// </summary>
    private async Task<VlsChannel> OpenChannelAsync(VlsNode vls, LndNodeConnection lnd, CancellationToken ct)
    {
        var node = vls.Node;
        // LND keeps an on-chain reserve for anchors channels; our wallet funds the channel, the anchors reserve and
        // the fee inputs of our anchors HTLC transactions
        if (vls.Anchors)
            await _harness.EnsureLndWalletFundedAsync(lnd, ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [lnd], [node], ct);
        var lndAddress = await node.ConnectToAsync(lnd, ct);
        var opened = await node.OpenChannelAsync(new OpenChannelClientRequest(lndAddress, s_capacity)
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        var channelPoint = opened.ChannelPoint();
        Console.WriteLine($"We opened the VLS channel {opened.ChannelId} ({channelPoint}) to {lnd.LocalAlias}");

        Channel? lndChannel = null;
        await Poll.UntilAsync(async () =>
        {
            var ours = (await node.ListChannelsAsync(ct)).Channels.FirstOrDefault(c => c.ChannelId == opened.ChannelId);
            lndChannel = await LndTestHelpers.GetChannelByPointAsync(lnd, channelPoint, ct);
            if (ours is not null && ours.IsUsable() && lndChannel is { Active: true }
             && await LndTestHelpers.HasOwnChannelEdgeAsync(lnd, lndChannel.ChanId, ct))
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [node], ct);
            return false;
        }, s_timeout, $"channel {channelPoint} usable on both ends", ct, TimeSpan.FromSeconds(1));
        await ChainSync.WaitAllAtTipAsync(_fixture, [lnd], [node], ct);
        Assert.NotNull(lndChannel);
        Assert.Equal(vls.Anchors ? CommitmentType.Anchors : CommitmentType.StaticRemoteKey, lndChannel.CommitmentType);
        Assert.Equal(vls.Anchors, AnchorsHarness.GetModel(node, opened.ChannelId).ChannelParams.OptionAnchorOutputs);
        var channel = new VlsChannel(opened.ChannelId, channelPoint, lndChannel.ChanId);
        await PayLndApprovedAsync(vls, lnd, channel, LiquiditySat, ct);
        return channel;
    }

    /// <summary>We pay an approved invoice of <paramref name="lnd"/> and wait until both sides settled.</summary>
    private static async Task PayLndApprovedAsync(VlsNode vls, LndNodeConnection lnd, VlsChannel channel,
                                                  long amountSat, CancellationToken ct)
    {
        var invoice = await LndTestHelpers.AddInvoiceAsync(lnd, amountSat * 1_000, [], ct, "vls pays lnd");
        vls.Approval.AuthorizeInvoice(Guid.NewGuid(), invoice.PaymentRequest);
        var node = vls.Node;
        var payment = await node.PayInvoiceAsync(invoice.PaymentRequest, ct);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channel.ChannelId, ct);
            var theirs = await LndTestHelpers.GetChannelByPointAsync(lnd, channel.ChannelPoint, ct);
            return ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 }
                && theirs is not null && theirs.PendingHtlcs.Count == 0
                && theirs.LocalBalance == (long)ours.RemoteBalance.Satoshi;
        }, s_timeout, "the payment settled with LND", ct);
    }

    /// <summary>
    /// We pay <paramref name="lnd"/>'s hold invoice (approved by the VLS operator); LND holds the HTLC (Accepted).
    /// Returns our HTLC once it is on both commitments.
    /// </summary>
    private static async Task<HtlcRecord> SendHeldPaymentAsync(VlsNode vls, LndNodeConnection lnd,
                                                               ChannelId channelId, string paymentRequest,
                                                               byte[] paymentHash, CancellationToken ct)
    {
        vls.Approval.AuthorizeInvoice(Guid.NewGuid(), paymentRequest);
        var inFlight = await vls.Node.PayInvoiceAsync(paymentRequest, ct, timeoutSeconds: 2);
        Assert.Equal(PaymentStatus.InFlight, inFlight.Status);
        await LndTestHelpers.WaitForInvoiceStateAsync(lnd, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                      s_timeout, ct);
        var htlc = await AnchorsHarness.WaitForHtlcInBothCommitmentsAsync(vls.Node, channelId, HtlcDirection.Outgoing,
                                                                          ct);
        Console.WriteLine($"Our HTLC {htlc.Id} held by {lnd.LocalAlias}: cltv_expiry {htlc.CltvExpiry}");
        return htlc;
    }

    /// <summary>
    /// <paramref name="lnd"/> pays an invoice of ours; we fulfill the HTLC (the invoice settles in that save) and every
    /// connection is cut before the fulfill goes out, so the HTLC stays on both commitments with its preimage known to
    /// us only. Returns the HTLC, the invoice and LND's payment, still in flight.
    /// </summary>
    private static async Task<(HtlcRecord Htlc, InvoiceInfoClientResponse Invoice, Task<Payment> LndPayment)>
        ReceiveFulfillCutOffAsync(NLightningTestNode node, LndNodeConnection lnd, VlsChannel channel,
                                  CancellationToken ct)
    {
        var tcpService = Assert.IsType<CrashableTcpService>(node.Services.GetRequiredService<ITcpService>());
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.ChannelManager.OnResponseMessageReady += (_, args) =>
        {
            // Raised under the channel lock right after the fulfill's save: cut every connection before it goes out
            if (args.ResponseMessage is not UpdateFulfillHtlcMessage || crashed.Task.IsCompleted)
                return;

            tcpService.CrashAsync().GetAwaiter().GetResult();
            crashed.TrySetResult();
        };
        var invoice = await node.CreateInvoiceAsync(LightningMoney.Satoshis(HtlcSat), "vls fc preimage", ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_timeout);
        Task<Payment> payment;
        while (true)
        {
            await LndTestHelpers.ResetMissionControlAsync(lnd, ct);
            payment = LndTestHelpers.SendPaymentV2Async(lnd, LndTestHelpers.PinnedPayment(invoice.Bolt11!,
                                                            [channel.LndChanId], timeoutSeconds: 600), ct,
                                                        TimeSpan.FromMinutes(20));
            if (await Task.WhenAny(crashed.Task, payment).WaitAsync(deadline.Token) == crashed.Task)
                break;

            var result = await payment;
            if (result.FailureReason is not (PaymentFailureReason.FailureReasonInsufficientBalance
                                             or PaymentFailureReason.FailureReasonNoRoute))
                Assert.Fail($"{lnd.LocalAlias}'s payment ended before it reached us: {result.Status} "
                          + $"{result.FailureReason}");

            await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token);
        }

        var htlc = Assert.Single(AnchorsHarness.GetHtlcs(node, channel.ChannelId),
                                 h => h.Direction == HtlcDirection.Incoming);
        Assert.NotNull(htlc.Removal);
        Assert.True(htlc.Removal.IsFulfill);
        Console.WriteLine($"Fulfill of {lnd.LocalAlias}'s HTLC {htlc.Id} saved and cut off; cltv_expiry "
                        + $"{htlc.CltvExpiry}");
        Assert.Equal(InvoiceStatus.Settled, (await node.GetInvoiceAsync(invoice.PaymentHash, ct))?.Status);
        return (htlc, invoice, payment);
    }

    /// <summary>The node signs through VLS only: no local key, the VLS adapter as its signer.</summary>
    private static void AssertVlsSigned(VlsNode vls)
    {
        Assert.IsType<VlsLightningSigner>(vls.Node.Services.GetRequiredService<ILightningSigner>());
        Assert.Throws<NotSupportedException>(() => vls.Keys.GetNodeKeyPair());
        Assert.False(File.Exists(Path.Combine(vls.Gateway.DirectoryPath, "node.key")));
    }

    #endregion

    #region Chain

    private async Task<uint> ConfirmationHeightAsync(NBitcoin.RPC.RawTransactionInfo info, CancellationToken ct) =>
        (uint)(await _fixture.Bitcoin.GetBlockCountAsync(ct) - info.Confirmations + 1);

    private static Task<OutputResolutionModel> WaitForRowAsync(NLightningTestNode node, ChannelId channelId,
                                                               TxId txId, OutputDescriptorKind descriptor,
                                                               CancellationToken ct) =>
        AnchorsHarness.WaitForRowAsync(node, channelId, r => r.TransactionId == txId && r.Descriptor == descriptor,
                                       $"the {descriptor} row of {txId}", ct);

    /// <summary>
    /// Our HTLC transaction on our commitment, <c>&lt;&gt; &lt;remotehtlcsig&gt; &lt;localhtlcsig&gt; &lt;preimage or
    /// empty&gt; &lt;script&gt;</c>: static_remotekey, one input and output and both signatures <c>SIGHASH_ALL</c>;
    /// anchors, the HTLC input 0 (nSequence 1) and its output 0 with wallet fee inputs after them, both signatures
    /// <c>SIGHASH_SINGLE|ANYONECANPAY</c> (VLS signs ours that way).
    /// </summary>
    private static TxIn AssertHtlcTransaction(Transaction tx, OutPoint htlcOutPoint, bool anchors)
    {
        var input = tx.Inputs[0];
        Assert.Equal(htlcOutPoint, input.PrevOut);
        Assert.Equal(5, input.WitScript.PushCount);
        Assert.Empty(input.WitScript[0]);
        var sigHash = anchors ? AnchorsHarness.SigHashSingleAnyoneCanPay : AnchorsHarness.SigHashAll;
        Assert.Equal(sigHash, input.WitScript[1][^1]);
        Assert.Equal(sigHash, input.WitScript[2][^1]);
        if (anchors)
        {
            Assert.Equal(1u, input.Sequence.Value);
            Assert.True(AnchorsHarness.HasForeignInput(tx, htlcOutPoint.Hash), "no wallet fee input");
            Console.WriteLine($"Anchors HTLC transaction {tx.GetHash()}: {tx.Inputs.Count} inputs, {tx.Outputs.Count} "
                            + "outputs");
        }
        else
        {
            Assert.Single(tx.Inputs);
            Assert.Single(tx.Outputs);
        }

        return input;
    }

    /// <summary>
    /// Mines to the CSV of output <paramref name="vout"/> of <paramref name="parent"/> (confirmed at
    /// <paramref name="confirmedAt"/>) and checks its delayed sweep: nSequence = CSV, <c>&lt;sig&gt; &lt;&gt;
    /// &lt;script&gt;</c>.
    /// </summary>
    private async Task<Transaction> SweepDelayedAsync(NLightningTestNode node, LndNodeConnection lnd,
                                                      ChannelId channelId, Transaction parent, uint vout,
                                                      uint confirmedAt, ushort csv, CancellationToken ct)
    {
        TxId parentTxId = parent.GetHash().ToBytes();
        var row = await AnchorsHarness.WaitForRowAsync(node, channelId,
                                                       r => r.TransactionId == parentTxId && r.OutputIndex == vout
                                                         && r.Descriptor == OutputDescriptorKind.DelayedToLocal,
                                                       $"the delayed output {parentTxId}:{vout}", ct);
        await _harness.MineToAsync(node, [lnd], confirmedAt + csv - 1, ct);
        var sweepTxId = await _harness.MineUntilResolvingTxAsync(node, [lnd], channelId, parentTxId, row.OutputIndex,
                                                                 ct);
        var sweep = (await _harness.MineUntilConfirmedAsync(node, [lnd], sweepTxId, ct)).Transaction;
        var input = Assert.Single(sweep.Inputs, i => i.PrevOut == new OutPoint(parent, row.OutputIndex));
        Assert.Equal((uint)csv, input.Sequence.Value);
        Assert.Equal(3, input.WitScript.PushCount);
        Assert.Empty(input.WitScript[1]);
        Assert.Equal(AnchorsHarness.SigHashAll, input.WitScript[0][^1]);
        return sweep;
    }

    /// <summary>
    /// Mines until every output of ours is resolved irrevocably (100 blocks deep) and the channel is <c>Closed</c>.
    /// </summary>
    private async Task AssertAllResolvedAndClosedAsync(NLightningTestNode node, LndNodeConnection lnd,
                                                       ChannelId channelId, CancellationToken ct)
    {
        await ChainSync.MineAndWaitAsync(_fixture, 101, [lnd], [node], ct);
        await Poll.UntilAsync(async () =>
        {
            var pending = await HandleAsync<PendingSweepsClientRequest, PendingSweepsClientResponse>(
                              node, new PendingSweepsClientRequest { ChannelId = channelId }, ct);
            if (pending.Channels.Count == 0)
                return true;

            foreach (var output in pending.Channels.SelectMany(c => c.Outputs))
                Console.WriteLine($"pendingsweeps: {output.TransactionId}:{output.OutputIndex} {output.Descriptor} "
                                + $"{output.State} wait {output.WaitUntilHeight} resolving {output.ResolvingTxId}");
            await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [node], ct);
            return false;
        }, s_timeout, $"channel {channelId} Closed (pendingsweeps empty)", ct, TimeSpan.FromSeconds(2));

        var closed = await HandleAsync<PendingSweepsClientRequest, PendingSweepsClientResponse>(
                         node, new PendingSweepsClientRequest { ChannelId = channelId, IncludeClosed = true }, ct);
        var entry = Assert.Single(closed.Channels);
        Assert.Equal(ChannelState.Closed, entry.State);
        foreach (var output in entry.Outputs)
        {
            Console.WriteLine($"Resolved: {output.TransactionId}:{output.OutputIndex} {output.Descriptor} "
                            + $"{output.State} {output.AmountSat} sat by {output.ResolvingTxId}");
            if (output.Descriptor is OutputDescriptorKind.OurAnchor or OutputDescriptorKind.PeerAnchor
                                     or OutputDescriptorKind.PeerOutput)
                Assert.True(output.State is OutputResolutionState.Irrevocable or OutputResolutionState.Ignored,
                            $"{output.Descriptor} is {output.State}");
            else
                Assert.Equal(OutputResolutionState.Irrevocable, output.State);
        }

        foreach (var abandoned in closed.AbandonedBroadcasts)
            Console.WriteLine($"Abandoned broadcast: {abandoned.TransactionId} {abandoned.Purpose}");
    }

    /// <summary><paramref name="lnd"/> lists the channel closed with <paramref name="closeType"/> and the commitment.</summary>
    private async Task AssertLndClosedAsync(NLightningTestNode node, LndNodeConnection lnd, string channelPoint,
                                            uint256 commitmentTxId, ChannelCloseSummary.Types.ClosureType closeType,
                                            CancellationToken ct)
    {
        var summary = await Poll.ForAsync(async () =>
        {
            var closed = await lnd.LightningClient.ClosedChannelsAsync(new ClosedChannelsRequest(),
                                                                      cancellationToken: ct);
            var found = closed.Channels.FirstOrDefault(c => c.ChannelPoint == channelPoint);
            if (found is null)
                await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [node], ct);
            return found;
        }, s_timeout, $"{lnd.LocalAlias} lists the channel closed", ct, TimeSpan.FromSeconds(2));
        Assert.Equal(closeType, summary.CloseType);
        Assert.Equal(commitmentTxId.ToString(), summary.ClosingTxHash);
    }

    /// <summary>Mines until every output in <paramref name="vouts"/> is spent by a confirmed transaction of ours.</summary>
    private async Task<IReadOnlyList<Transaction>> MineUntilAllTakenAsync(NLightningTestNode node,
                                                                          LndNodeConnection lnd, ChannelId channelId,
                                                                          uint256 revokedTxId,
                                                                          IReadOnlyList<uint> vouts,
                                                                          CancellationToken ct)
    {
        var ours = new Dictionary<uint256, Transaction>();
        await Poll.UntilAsync(async () =>
        {
            foreach (var row in await AnchorsHarness.GetRowsAsync(node, channelId))
            {
                if (row.ResolvingTransactionId is not { } txId)
                    continue;
                var hash = new uint256((byte[])txId);
                if (ours.ContainsKey(hash))
                    continue;
                try
                {
                    ours[hash] = await _fixture.Bitcoin.GetRawTransactionAsync(hash, true, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Not in the mempool or a block (yet, or replaced)
                }
            }

            var spent = new HashSet<OutPoint>(ours.Values.SelectMany(t => t.Inputs.Select(i => i.PrevOut)));
            var confirmed = vouts.All(v => spent.Contains(new OutPoint(revokedTxId, v)))
                         && await AllConfirmedAsync(ours.Values, revokedTxId, ct);
            if (!confirmed)
                await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [node], ct);
            return confirmed;
        }, s_timeout, "every output of the revoked commitment taken and confirmed", ct, TimeSpan.FromSeconds(1));
        return ours.Values.ToList();
    }

    private async Task<bool> AllConfirmedAsync(IEnumerable<Transaction> txs, uint256 revokedTxId, CancellationToken ct)
    {
        foreach (var tx in txs.Where(t => t.Inputs.Any(i => i.PrevOut.Hash == revokedTxId)))
        {
            try
            {
                if ((await _fixture.Bitcoin.GetRawTransactionInfoAsync(tx.GetHash(), ct)).Confirmations == 0)
                    return false;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return false; // replaced
            }
        }

        return true;
    }

    /// <summary>
    /// LND answers GetInfo (synced) before its server is started and refuses CloseChannel until then: retries the force
    /// close; returns the commitment's txid.
    /// </summary>
    private static async Task<uint256> ForceCloseWhenStartedAsync(LndNodeConnection lnd, string channelPoint,
                                                                  CancellationToken ct)
    {
        var parts = channelPoint.Split(':');
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(s_timeout);
        PendingUpdate? pending = null;
        while (pending is null)
        {
            try
            {
                using var closeCall = lnd.LightningClient.CloseChannel(new CloseChannelRequest
                {
                    ChannelPoint = new ChannelPoint { FundingTxidStr = parts[0], OutputIndex = uint.Parse(parts[1]) },
                    Force = true
                }, cancellationToken: closeTimeout.Token);
                while (pending is null && await closeCall.ResponseStream.MoveNext(closeTimeout.Token))
                    pending = closeCall.ResponseStream.Current.ClosePending;
                Assert.NotNull(pending);
            }
            catch (RpcException e) when (e.Status.Detail.Contains("still in the process of starting"))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), closeTimeout.Token);
            }
        }

        var txId = new uint256(pending.Txid.ToByteArray());
        Console.WriteLine($"{lnd.LocalAlias} force-closed {channelPoint} with {txId}");
        return txId;
    }

    private async Task WaitUsableAsync(NLightningTestNode node, LndNodeConnection lnd, ChannelId channelId,
                                       CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var usable = (await node.GetChannelAsync(channelId, ct)).IsUsable();
            try
            {
                var channels = await lnd.LightningClient.ListChannelsAsync(new ListChannelsRequest(),
                                                                          cancellationToken: ct);
                usable &= channels.Channels.Any(c => c.Active && c.RemotePubkey == node.NodeIdHex);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                usable = false; // a restarting LND refuses RPCs for a moment
            }

            return usable;
        }, s_timeout, $"channel {channelId} usable again", ct, TimeSpan.FromSeconds(1));
        await ChainSync.WaitAllAtTipAsync(_fixture, [lnd], [node], ct);
    }

    #endregion

    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(NLightningTestNode node, TRequest request,
                                                                          CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, ct);
    }
}