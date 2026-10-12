using System.Security.Cryptography;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Taproot;

using Abcd;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Infrastructure.Transport.Interfaces;
using Onchain.Anchors;
using Onchain.Cheater;
using Utils;

/// <summary>
/// Taproot plan T6 (wave t03, NL-978): force closes of a simple taproot channel against LND 0.21.4 run with
/// <c>--protocol.simple-taproot-chans</c> (<see cref="LndTaprootNetworkFixture"/>), with HTLCs in flight both ways, and
/// every output of ours resolved on chain:
/// <list type="bullet">
///   <item>(1) our <c>forceclosechannel</c>: our MuSig2 key-path commitment, the HTLC-success (preimage) and the
///   HTLC-timeout (at <c>cltv_expiry</c>) by script path with LND's <c>SIGHASH_SINGLE|ANYONECANPAY</c> signature and
///   wallet fee inputs, their P2TR second-level outputs and our <c>to_local</c> swept by the delay leaf after the CSV;
///   LND learns the preimage from our HTLC-success, our payment fails.</item>
///   <item>(2) LND's force close: our preimage claim and our timeout claim by script path (nSequence 1) and our
///   <c>to_remote</c> by its 1-CSV leaf.</item>
///   <item>(3) LND restarts on an old <c>channel.db</c> (<see cref="LndChannelDbRollback"/>; its snapshot taken with an
///   HTLC in flight, so that is also an LND restart with an HTLC in flight) and broadcasts a revoked commitment that
///   holds an HTLC: its <c>to_local</c> taken by the revocation leaf, the HTLC output by the revocation key path, our
///   <c>to_remote</c> by its leaf; the wallet gets the whole channel less the commitment's and our fees.</item>
/// </list>
/// </summary>
/// <remarks>
/// The HTLC LND offers us stays on the commitments with its preimage known to us the way the anchors proof O4 (c) does
/// it: our fulfill is saved and the connection is cut before it goes out (<see cref="CrashableTcpService"/>, which also
/// keeps the node from reconnecting until it restarts). Run with <c>scripts/run-cluster.sh -n 1 --suite taproot</c>.
/// </remarks>
[Collection(LndTaprootRegtestCollection.Name)]
public class LndTaprootForceCloseTests : IAsyncLifetime
{
    private const ulong HoldInvoiceCltvExpiry = 24;
    private const long HtlcSat = 50_000;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);

    private readonly LndTaprootNetworkFixture _fixture;
    private readonly AnchorsHarness _harness;

    public LndTaprootForceCloseTests(LndTaprootNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    private LndNodeConnection Tara => _fixture.GetLndNode(LndTaprootNetworkFixture.Alias);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Given_HtlcsBothWays_When_WeForceClose_Then_EveryOutputOfOursIsResolvedByTaprootSpends()
    {
        // Arrange: our taproot channel to tara with a push; our HTLC held by tara's hold invoice; tara's HTLC to us
        // fulfilled by us but cut off before the fulfill reaches tara
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("taproot-fc-ours", ct);
        var tara = Tara;
        var channel = await OpenTaprootChannelAsync(node, tara, ct);
        var csv = AnchorsHarness.GetModel(node, channel.ChannelId).ChannelParams.Remote.ToSelfDelay;
        Console.WriteLine($"CSV on our to_local: {csv}");

        var (_, holdHash) = LndTestHelpers.NewPreimage();
        var hold = await LndTestHelpers.AddHoldInvoiceAsync(tara, holdHash, HtlcSat * 1_000, [], ct,
                                                            "taproot fc ours timeout", HoldInvoiceCltvExpiry);
        try
        {
            var outgoing = await SendHeldPaymentAsync(node, tara, channel.ChannelId, hold.PaymentRequest, holdHash,
                                                      ct);
            var (incoming, invoice, lndPayment) = await ReceiveFulfillCutOffAsync(node, tara, channel, ct);

            // Act 1: forceclosechannel; the commitment confirms
            var forceClose = await HandleAsync<ForceCloseChannelClientRequest, ForceCloseChannelClientResponse>(
                                 node, new ForceCloseChannelClientRequest(channel.ChannelId), ct);
            Console.WriteLine($"forceclosechannel: {forceClose.Status} {forceClose.State} {forceClose.CommitmentTxId}");
            Assert.Equal("Broadcast", forceClose.Status);
            Assert.NotNull(forceClose.CommitmentTxId);
            var commitmentTxId = forceClose.CommitmentTxId.Value;
            var commitmentInfo = await _harness.MineUntilConfirmedAsync(node, [tara], commitmentTxId, ct);
            var commitment = commitmentInfo.Transaction;
            var commitmentHeight = await ConfirmationHeightAsync(commitmentInfo, ct);
            var close = await AnchorsHarness.WaitForCloseAsync(node, channel.ChannelId, ct);

            // Assert 1: our commitment, spent by the MuSig2 key path, holds both HTLCs
            Assert.Equal(ChannelCloseKind.LocalCommitment, close.Kind);
            AssertKeyPathFundingSpend(commitment, channel.ChannelPoint);
            var offeredRow = await WaitForRowAsync(node, channel.ChannelId, commitmentTxId,
                                                   OutputDescriptorKind.LocalOfferedHtlc, ct);
            var receivedRow = await WaitForRowAsync(node, channel.ChannelId, commitmentTxId,
                                                    OutputDescriptorKind.LocalReceivedHtlc, ct);
            var toLocalRow = await WaitForRowAsync(node, channel.ChannelId, commitmentTxId,
                                                   OutputDescriptorKind.DelayedToLocal, ct);
            Assert.Equal(outgoing.Id, offeredRow.HtlcId);
            Assert.Equal(incoming.Id, receivedRow.HtlcId);
            AssertAllP2Tr(commitment);
            // LND sweeps its to_remote at its own pace first (a block burst makes LND 0.21.4 give it up, NL-770)
            await ChainSync.MineUntilLndSweptAsync(_fixture, tara, [node], commitment.GetHash(), ct);

            // Act 2: tara gives our HTLC up
            await LndTestHelpers.CancelInvoiceAsync(tara, holdHash, ct);

            // Assert 2: our HTLC-success (preimage, fee inputs) confirms and LND learns the preimage from it
            var successTxId = await _harness.MineUntilResolvingTxAsync(node, [tara], channel.ChannelId, commitmentTxId,
                                                                       receivedRow.OutputIndex, ct);
            var success = await _harness.WaitInMempoolAsync(successTxId, ct);
            var successInput = AssertTaprootHtlcTransaction(success, new OutPoint(commitment, receivedRow.OutputIndex),
                                                            isSuccess: true);
            Assert.Equal(0u, (uint)success.LockTime);
            Assert.Equal((byte[])incoming.Removal!.PaymentPreimage!.Value, successInput.WitScript[2]);
            var successInfo = await _harness.MineUntilConfirmedAsync(node, [tara], successTxId, ct);
            var successHeight = await ConfirmationHeightAsync(successInfo, ct);
            Assert.True(successHeight < incoming.CltvExpiry,
                        $"HTLC-success at {successHeight}, expiry {incoming.CltvExpiry}");
            var lndResult = await _harness.MineUntilAsync(node, [tara], async () =>
            {
                await Task.WhenAny(lndPayment, Task.Delay(TimeSpan.FromSeconds(2), ct));
                return lndPayment.IsCompleted ? await lndPayment : null;
            }, "LND's payment completed", ct);
            Console.WriteLine($"LND's payment: {lndResult.Status} {lndResult.FailureReason}");
            Assert.Equal(Payment.Types.PaymentStatus.Succeeded, lndResult.Status);
            Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(Convert.FromHexString(lndResult.PaymentPreimage)));
            Assert.Equal(InvoiceStatus.Settled, (await node.GetInvoiceAsync(invoice.PaymentHash, ct))?.Status);

            // Assert 3: our HTLC-timeout (fee inputs) at cltv_expiry, not before; our payment fails once it is deep
            await _harness.MineToAsync(node, [tara], outgoing.CltvExpiry - 1, ct);
            Assert.Null((await AnchorsHarness.GetRowsAsync(node, channel.ChannelId))
                       .Single(r => r.TransactionId == commitmentTxId && r.OutputIndex == offeredRow.OutputIndex)
                       .ResolvingTransactionId);
            await ChainSync.MineAndWaitAsync(_fixture, 1, [tara], [node], ct);
            var timeoutTxId = await AnchorsHarness.WaitForResolvingTxAsync(node, channel.ChannelId, commitmentTxId,
                                                                           offeredRow.OutputIndex, ct);
            var timeout = await _harness.WaitInMempoolAsync(timeoutTxId, ct);
            AssertTaprootHtlcTransaction(timeout, new OutPoint(commitment, offeredRow.OutputIndex), isSuccess: false);
            Assert.Equal(outgoing.CltvExpiry, (uint)timeout.LockTime);
            var timeoutInfo = await _harness.MineUntilConfirmedAsync(node, [tara], timeoutTxId, ct);
            var timeoutHeight = await ConfirmationHeightAsync(timeoutInfo, ct);
            await _harness.MineToAsync(node, [tara], timeoutHeight + AnchorsHarness.ReasonableDepth - 1, ct);
            var failed = await Poll.ForAsync(async () =>
            {
                var payment = await node.GetPaymentAsync(new Hash(holdHash), ct);
                return payment is { Status: PaymentStatus.Failed } ? payment : null;
            }, s_timeout, "our payment failed", ct);
            Assert.Equal(FailureCode.PermanentChannelFailure, failed.FailureCode);

            // Assert 4: after the CSV, both second-level outputs and our to_local are swept by the delay leaf
            var successSweep = await SweepSecondLevelAsync(node, tara, channel.ChannelId, success, successHeight, csv,
                                                           ct);
            var timeoutSweep = await SweepSecondLevelAsync(node, tara, channel.ChannelId, timeout, timeoutHeight, csv,
                                                           ct);
            await _harness.MineToAsync(node, [tara], commitmentHeight + csv - 1, ct);
            var toLocalSweepTxId = await _harness.MineUntilResolvingTxAsync(node, [tara], channel.ChannelId,
                                                                            commitmentTxId, toLocalRow.OutputIndex,
                                                                            ct);
            var toLocalSweep = (await _harness.MineUntilConfirmedAsync(node, [tara], toLocalSweepTxId, ct))
               .Transaction;
            var toLocalInput = Assert.Single(toLocalSweep.Inputs,
                                             i => i.PrevOut == new OutPoint(commitment, toLocalRow.OutputIndex));
            Assert.Equal((uint)csv, toLocalInput.Sequence.Value);
            AssertScriptPathSpend(toLocalInput, 3);
            Console.WriteLine($"Sweeps: second level {successSweep.GetHash()}, {timeoutSweep.GetHash()}; to_local "
                            + $"{toLocalSweep.GetHash()}");

            // Assert 5: every output of ours resolved, the channel Closed, LND lists our force close
            await AssertAllResolvedAndClosedAsync(node, tara, channel.ChannelId, ct);
            await AssertLndClosedAsync(node, tara, channel.ChannelPoint, commitment.GetHash(),
                                       ChannelCloseSummary.Types.ClosureType.RemoteForceClose, ct);
        }
        finally
        {
            await AnchorsHarness.CancelHoldInvoiceQuietlyAsync(tara, holdHash);
        }
    }

    [Fact]
    public async Task Given_HtlcsBothWays_When_LndForceCloses_Then_WeClaimByScriptPathAndSweepToRemote()
    {
        // Arrange: as in the first proof, both HTLCs on the commitments, the connection cut
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("taproot-fc-lnd", ct);
        var tara = Tara;
        var channel = await OpenTaprootChannelAsync(node, tara, ct);
        var (_, holdHash) = LndTestHelpers.NewPreimage();
        var hold = await LndTestHelpers.AddHoldInvoiceAsync(tara, holdHash, HtlcSat * 1_000, [], ct,
                                                            "taproot fc lnd timeout", HoldInvoiceCltvExpiry);
        try
        {
            var outgoing = await SendHeldPaymentAsync(node, tara, channel.ChannelId, hold.PaymentRequest, holdHash,
                                                      ct);
            var (incoming, invoice, lndPayment) = await ReceiveFulfillCutOffAsync(node, tara, channel, ct);

            // Act 1: tara force-closes; its commitment confirms
            var commitmentTxId = await ForceCloseWhenStartedAsync(tara, channel.ChannelPoint, ct);
            var commitmentInfo = await _harness.MineUntilConfirmedAsync(node, [tara], commitmentTxId, ct);
            var commitment = commitmentInfo.Transaction;
            var close = await AnchorsHarness.WaitForCloseAsync(node, channel.ChannelId, ct);

            // Assert 1: LND's commitment (MuSig2 key path) holds both HTLCs
            Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);
            Assert.Equal(commitmentTxId, new uint256((byte[])close.CommitmentTransactionId));
            AssertKeyPathFundingSpend(commitment, channel.ChannelPoint);
            AssertAllP2Tr(commitment);
            TxId lndCommitment = commitmentTxId.ToBytes();
            var offeredByLnd = await WaitForRowAsync(node, channel.ChannelId, lndCommitment,
                                                     OutputDescriptorKind.RemoteOfferedHtlc, ct);
            var receivedByLnd = await WaitForRowAsync(node, channel.ChannelId, lndCommitment,
                                                      OutputDescriptorKind.RemoteReceivedHtlc, ct);
            Assert.Equal(incoming.Id, offeredByLnd.HtlcId);
            Assert.Equal(outgoing.Id, receivedByLnd.HtlcId);
            await LndTestHelpers.CancelInvoiceAsync(tara, holdHash, ct);

            // Assert 2: our to_remote swept by its 1-CSV leaf (nSequence 1)
            var toRemote = await AnchorsHarness.WaitForRowAsync(node, channel.ChannelId,
                                                                o => o is
                                                                {
                                                                    Descriptor: OutputDescriptorKind.PaymentToRemote,
                                                                    ResolvingTransactionId: not null
                                                                }, "our to_remote sweep saved", ct);
            var toRemoteSweep = (await _harness.MineUntilConfirmedAsync(node, [tara],
                                                                        toRemote.ResolvingTransactionId!.Value, ct))
               .Transaction;
            var toRemoteInput = Assert.Single(toRemoteSweep.Inputs,
                                              i => i.PrevOut == new OutPoint(commitmentTxId, toRemote.OutputIndex));
            Assert.Equal(1u, toRemoteInput.Sequence.Value);
            AssertScriptPathSpend(toRemoteInput, 3);
            AssertToRemoteScript(toRemoteInput.WitScript[1]);

            // Assert 3: the preimage claim (<sig> <preimage> <leaf> <control block>, nSequence 1) before the expiry
            var claimTxId = await _harness.MineUntilResolvingTxAsync(node, [tara], channel.ChannelId, lndCommitment,
                                                                     offeredByLnd.OutputIndex, ct);
            var claimInfo = await _harness.MineUntilConfirmedAsync(node, [tara], claimTxId, ct);
            var claim = claimInfo.Transaction;
            var claimInput = Assert.Single(claim.Inputs,
                                           i => i.PrevOut == new OutPoint(commitmentTxId, offeredByLnd.OutputIndex));
            Assert.Equal(1u, claimInput.Sequence.Value);
            Assert.Equal(0u, claim.LockTime.Value);
            AssertScriptPathSpend(claimInput, 4);
            Assert.Equal((byte[])incoming.Removal!.PaymentPreimage!.Value, claimInput.WitScript[1]);
            Assert.True(await ConfirmationHeightAsync(claimInfo, ct) < incoming.CltvExpiry);
            var lndResult = await _harness.MineUntilAsync(node, [tara], async () =>
            {
                await Task.WhenAny(lndPayment, Task.Delay(TimeSpan.FromSeconds(2), ct));
                return lndPayment.IsCompleted ? await lndPayment : null;
            }, "LND's payment completed", ct);
            Console.WriteLine($"LND's payment: {lndResult.Status} {lndResult.FailureReason}");
            Assert.Equal(Payment.Types.PaymentStatus.Succeeded, lndResult.Status);
            Assert.Equal(InvoiceStatus.Settled, (await node.GetInvoiceAsync(invoice.PaymentHash, ct))?.Status);

            // Assert 4: the timeout claim (<sig> <leaf> <control block>, nLockTime = cltv_expiry, nSequence 1)
            await _harness.MineToAsync(node, [tara], outgoing.CltvExpiry - 1, ct);
            Assert.Null((await AnchorsHarness.GetRowsAsync(node, channel.ChannelId))
                       .Single(r => r.TransactionId == lndCommitment && r.OutputIndex == receivedByLnd.OutputIndex)
                       .ResolvingTransactionId);
            await ChainSync.MineAndWaitAsync(_fixture, 1, [tara], [node], ct);
            var timeoutTxId = await AnchorsHarness.WaitForResolvingTxAsync(node, channel.ChannelId, lndCommitment,
                                                                           receivedByLnd.OutputIndex, ct);
            var timeoutInfo = await _harness.MineUntilConfirmedAsync(node, [tara], timeoutTxId, ct);
            var timeoutClaim = timeoutInfo.Transaction;
            var timeoutInput = Assert.Single(timeoutClaim.Inputs,
                                             i => i.PrevOut == new OutPoint(commitmentTxId, receivedByLnd.OutputIndex));
            Assert.Equal(outgoing.CltvExpiry, timeoutClaim.LockTime.Value);
            Assert.Equal(1u, timeoutInput.Sequence.Value);
            AssertScriptPathSpend(timeoutInput, 3);
            var timeoutHeight = await ConfirmationHeightAsync(timeoutInfo, ct);
            await _harness.MineToAsync(node, [tara], timeoutHeight + AnchorsHarness.ReasonableDepth - 1, ct);
            var failed = await Poll.ForAsync(async () =>
            {
                var payment = await node.GetPaymentAsync(new Hash(holdHash), ct);
                return payment is { Status: PaymentStatus.Failed } ? payment : null;
            }, s_timeout, "our payment failed", ct);
            Assert.Equal(FailureCode.PermanentChannelFailure, failed.FailureCode);

            // Assert 5: every output of ours resolved, the channel Closed, LND lists its force close
            await AssertAllResolvedAndClosedAsync(node, tara, channel.ChannelId, ct);
            await AssertLndClosedAsync(node, tara, channel.ChannelPoint, commitmentTxId,
                                       ChannelCloseSummary.Types.ClosureType.LocalForceClose, ct);
        }
        finally
        {
            await AnchorsHarness.CancelHoldInvoiceQuietlyAsync(tara, holdHash);
        }
    }

    [Fact]
    public async Task Given_LndRestartsOnAnOldChannelDbWithAnHtlc_When_ItForceCloses_Then_WeTakeTheWholeChannel()
    {
        // Arrange: our taproot channel to tara, payments both ways, our HTLC held by tara while its channel.db is
        // copied (tara restarts with the HTLC in flight), then tara settles it and more payments go both ways
        var ct = TestContext.Current.CancellationToken;
        var node = await CreateNodeAsync("taproot-fc-penalty", ct);
        var tara = Tara;
        var channel = await OpenTaprootChannelAsync(node, tara, ct);
        var channelId = channel.ChannelId;
        await AnchorsHarness.PayLndAsync(node, tara, channelId, channel.ChannelPoint, 20_000, ct);
        await AnchorsHarness.LndPaysUsAsync(node, tara, channel.LndChanId, channelId, channel.ChannelPoint, 30_000, ct);

        var (holdPreimage, holdHash) = LndTestHelpers.NewPreimage();
        var hold = await LndTestHelpers.AddHoldInvoiceAsync(tara, holdHash, HtlcSat * 1_000, [], ct,
                                                            "taproot fc revoked htlc");
        await SendHeldPaymentAsync(node, tara, channelId, hold.PaymentRequest, holdHash, ct);
        var rollback = new LndChannelDbRollback(_fixture, LndTaprootNetworkFixture.Alias);
        await rollback.TakeSnapshotAsync(ct);
        tara = Tara;
        await WaitUsableAsync(node, tara, channelId, ct);
        var snapshotState = await node.GetChannelAsync(channelId, ct);
        Console.WriteLine($"Snapshot state (one HTLC of ours in flight): {snapshotState.Describe()}");
        Assert.Equal(1, snapshotState.OfferedHtlcCount);

        // The HTLC survived tara's restart: tara settles it, our payment succeeds
        await LndTestHelpers.SettleInvoiceAsync(tara, holdPreimage, ct);
        var settled = await Poll.ForAsync(async () =>
        {
            var payment = await node.GetPaymentAsync(new Hash(holdHash), ct);
            return payment?.Status == PaymentStatus.Succeeded ? payment : null;
        }, s_timeout, "our held payment succeeded after tara's restart", ct);
        Assert.Equal(holdPreimage, (byte[])settled.Preimage!.Value);
        for (var i = 0; i < 2; i++)
        {
            await AnchorsHarness.PayLndAsync(node, tara, channelId, channel.ChannelPoint, 10_000, ct);
            await AnchorsHarness.LndPaysUsAsync(node, tara, channel.LndChanId, channelId, channel.ChannelPoint, 5_000,
                                                ct);
        }

        var now = await node.GetChannelAsync(channelId, ct);
        Assert.True(now.RemoteCommitmentNumber > snapshotState.RemoteCommitmentNumber + 1, now.Describe());
        var walletBefore = AnchorsHarness.WalletBalance(node);
        await node.StopAsync();

        // Act: tara restarts on the old database and force-closes while we are down; one block; we start again
        await rollback.RestoreSnapshotAsync(ct);
        tara = Tara;
        var revokedTxId = await ForceCloseWhenStartedAsync(tara, channel.ChannelPoint, ct);
        var revoked = await _fixture.Bitcoin.GetRawTransactionAsync(revokedTxId, true, ct);
        Console.WriteLine($"tara force-closed with the revoked {revokedTxId}");
        await ChainSync.MineAndWaitAsync(_fixture, 1, [tara], [], ct);
        await node.StartAsync(ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [tara], [node], ct);

        // Assert: classified as revoked; it holds the HTLC; both anchors are left
        var close = await AnchorsHarness.WaitForCloseAsync(node, channelId, ct);
        Assert.Equal(ChannelCloseKind.RevokedCommitment, close.Kind);
        Assert.Equal((TxId)revokedTxId.ToBytes(), close.CommitmentTransactionId);
        AssertKeyPathFundingSpend(revoked, channel.ChannelPoint);
        AssertAllP2Tr(revoked);
        var rows = await Poll.ForAsync(async () =>
        {
            var all = await AnchorsHarness.GetRowsAsync(node, channelId);
            return all.Any(r => r.Descriptor == OutputDescriptorKind.RevokedHtlc) ? all : null;
        }, s_timeout, "the revoked HTLC output recorded", ct);
        foreach (var row in rows)
            Console.WriteLine($"Row {row.TransactionId}:{row.OutputIndex} {row.Descriptor} {row.State}");
        var anchors = rows.Where(r => r.TransactionId == close.CommitmentTransactionId
                                   && r.Descriptor is OutputDescriptorKind.OurAnchor
                                                      or OutputDescriptorKind.PeerAnchor)
                          .Select(r => r.OutputIndex).ToHashSet();
        var takenVouts = Enumerable.Range(0, revoked.Outputs.Count).Select(v => (uint)v)
                                   .Where(v => revoked.Outputs[(int)v].Value.Satoshi != AnchorsHarness.AnchorAmountSat
                                            && !anchors.Contains(v))
                                   .ToList();
        Assert.Equal(3, takenVouts.Count); // tara's to_local, our to_remote, the HTLC

        // Every non-anchor output is spent by a transaction of ours, all confirmed
        var ours = await MineUntilAllTakenAsync(node, tara, channelId, revokedTxId, takenVouts, ct);
        var used = ours.Where(t => t.Inputs.Any(i => i.PrevOut.Hash == revokedTxId)).ToList();
        var inputs = used.SelectMany(t => t.Inputs).Where(i => i.PrevOut.Hash == revokedTxId).ToList();
        foreach (var input in inputs)
            Console.WriteLine($"Spend of {input.PrevOut.N}: {input.WitScript.PushCount} witness items, sequence "
                            + $"{input.Sequence.Value}");
        var htlcVout = rows.Single(r => r.Descriptor == OutputDescriptorKind.RevokedHtlc
                                     && r.TransactionId == close.CommitmentTransactionId).OutputIndex;
        var toLocalVout = rows.Single(r => r.Descriptor == OutputDescriptorKind.RevokedToLocal).OutputIndex;
        var toRemoteVout = rows.Single(r => r.Descriptor == OutputDescriptorKind.PaymentToRemote).OutputIndex;
        // The HTLC by the revocation key path (one 64/65-byte signature)
        AssertKeyPathSpend(Assert.Single(inputs, i => i.PrevOut.N == htlcVout));
        // tara's to_local by the revocation leaf: <revoke_sig> <revoke_script> <control block>
        AssertScriptPathSpend(Assert.Single(inputs, i => i.PrevOut.N == toLocalVout), 3);
        // Our to_remote by its 1-CSV leaf
        var toRemoteInput = Assert.Single(inputs, i => i.PrevOut.N == toRemoteVout);
        Assert.Equal(1u, toRemoteInput.Sequence.Value);
        AssertScriptPathSpend(toRemoteInput, 3);
        AssertToRemoteScript(toRemoteInput.WitScript[1]);

        // The wallet gained the whole channel less the revoked commitment's fee, its anchors and our fees
        var gained = 0L;
        foreach (var tx in used)
            gained += tx.TotalOut.Satoshi - (await ForeignInputValueAsync(tx, revokedTxId, ct)).Satoshi;
        var taken = takenVouts.Sum(v => revoked.Outputs[(int)v].Value.Satoshi);
        var commitmentFee = (await _harness.FeeAsync(revoked, ct)).Satoshi;
        Console.WriteLine($"Taken outputs {taken} sat, gained {gained} sat, commitment fee {commitmentFee} sat");
        Assert.Equal((long)s_capacity.Satoshi, taken + commitmentFee + 2 * AnchorsHarness.AnchorAmountSat);
        Assert.InRange(taken - gained, 1, (long)s_capacity.Satoshi / 100);
        await Poll.UntilAsync(() => (AnchorsHarness.WalletBalance(node) - walletBefore).Satoshi == gained,
                              s_timeout, $"our wallet gained {gained} sat", ct);

        await AssertAllResolvedAndClosedAsync(node, tara, channelId, ct);
        await Poll.ForAsync(async () =>
        {
            var closed = await tara.LightningClient.ClosedChannelsAsync(new ClosedChannelsRequest(),
                                                                        cancellationToken: ct);
            var found = closed.Channels.FirstOrDefault(c => c.ChannelPoint == channel.ChannelPoint);
            if (found is null)
                await ChainSync.MineAndWaitAsync(_fixture, 1, [tara], [node], ct);
            return found;
        }, s_timeout, "tara lists the channel closed", ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync([LndTaprootNetworkFixture.Alias]);
        GC.SuppressFinalize(this);
    }

    #region Setup

    private sealed record TaprootChannel(ChannelId ChannelId, string ChannelPoint, ulong LndChanId);

    private async Task<NLightningTestNode> CreateNodeAsync(string name, CancellationToken ct) =>
        await _harness.CreateNodeAsync(name, ct, o =>
        {
            o.Features.AllowExperimentalFeatures = true;
            o.Features.OptionSimpleTaproot = FeatureSupport.Optional;
        });

    /// <summary>
    /// Funds our wallet (the channel, the anchors reserve and the HTLC transactions' fee inputs) and tara's, opens a
    /// private taproot channel of <see cref="s_capacity"/> with <see cref="s_push"/> to tara and waits until both ends
    /// use it.
    /// </summary>
    private async Task<TaprootChannel> OpenTaprootChannelAsync(NLightningTestNode node, LndNodeConnection lnd,
                                                               CancellationToken ct)
    {
        await _harness.EnsureLndWalletFundedAsync(lnd, ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [lnd], [node], ct);
        var lndAddress = await node.ConnectToAsync(lnd, ct);
        var opened = await node.OpenChannelAsync(new OpenChannelClientRequest(lndAddress, s_capacity)
        {
            PushAmount = s_push,
            FeeRatePerKw = AnchorsHarness.EstimateFeeRatePerKw,
            IsSimpleTaproot = true
        }, ct);
        var channelPoint = opened.ChannelPoint();
        Console.WriteLine($"We opened the taproot channel {opened.ChannelId} ({channelPoint}) to {lnd.LocalAlias}");

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
        Assert.Equal(CommitmentType.SimpleTaprootFinal, lndChannel.CommitmentType);
        Assert.True(AnchorsHarness.GetModel(node, opened.ChannelId).ChannelParams.OptionSimpleTaproot);
        return new TaprootChannel(opened.ChannelId, channelPoint, lndChannel.ChanId);
    }

    /// <summary>
    /// We pay tara's hold invoice; tara holds the HTLC (Accepted). Returns our HTLC once it is on both commitments.
    /// </summary>
    private static async Task<Domain.Channels.Commitments.HtlcRecord> SendHeldPaymentAsync(
        NLightningTestNode node, LndNodeConnection lnd, ChannelId channelId, string paymentRequest, byte[] paymentHash,
        CancellationToken ct)
    {
        var inFlight = await node.PayInvoiceAsync(paymentRequest, ct, timeoutSeconds: 2);
        Assert.Equal(PaymentStatus.InFlight, inFlight.Status);
        await LndTestHelpers.WaitForInvoiceStateAsync(lnd, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                      s_timeout, ct);
        var htlc = await AnchorsHarness.WaitForHtlcInBothCommitmentsAsync(node, channelId, HtlcDirection.Outgoing, ct);
        Console.WriteLine($"Our HTLC {htlc.Id} held by {lnd.LocalAlias}: cltv_expiry {htlc.CltvExpiry}");
        return htlc;
    }

    /// <summary>
    /// tara pays an invoice of ours; we fulfill the HTLC (the invoice settles in that save) and every connection is cut
    /// before the fulfill goes out, so the HTLC stays on both commitments with its preimage known to us only. Returns
    /// the HTLC, the invoice and LND's payment, still in flight.
    /// </summary>
    private async Task<(Domain.Channels.Commitments.HtlcRecord Htlc, InvoiceInfoClientResponse Invoice,
            Task<Payment> LndPayment)>
        ReceiveFulfillCutOffAsync(NLightningTestNode node, LndNodeConnection lnd, TaprootChannel channel,
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
        var invoice = await node.CreateInvoiceAsync(LightningMoney.Satoshis(HtlcSat), "taproot fc preimage", ct);

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

            Console.WriteLine($"{lnd.LocalAlias}'s payment failed with {result.FailureReason}; retrying");
            await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token);
        }

        var htlc = Assert.Single(AnchorsHarness.GetHtlcs(node, channel.ChannelId),
                                 h => h.Direction == HtlcDirection.Incoming);
        Assert.NotNull(htlc.Removal);
        Assert.True(htlc.Removal.IsFulfill);
        Assert.True(htlc.IsInCommit(Domain.Bitcoin.Transactions.Enums.CommitmentSide.Local));
        Console.WriteLine($"Fulfill of {lnd.LocalAlias}'s HTLC {htlc.Id} saved and cut off; cltv_expiry "
                        + $"{htlc.CltvExpiry}");
        Assert.Equal(InvoiceStatus.Settled, (await node.GetInvoiceAsync(invoice.PaymentHash, ct))?.Status);
        return (htlc, invoice, payment);
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
    /// Mines to the CSV of the second-level output of <paramref name="htlcTx"/> (confirmed at
    /// <paramref name="confirmedAt"/>) and checks its sweep: nSequence = CSV, the delay leaf
    /// (<c>&lt;sig&gt; &lt;leaf&gt; &lt;control block&gt;</c>).
    /// </summary>
    private async Task<Transaction> SweepSecondLevelAsync(NLightningTestNode node, LndNodeConnection lnd,
                                                          ChannelId channelId, Transaction htlcTx, uint confirmedAt,
                                                          ushort csv, CancellationToken ct)
    {
        TxId htlcTxId = htlcTx.GetHash().ToBytes();
        var row = await WaitForRowAsync(node, channelId, htlcTxId, OutputDescriptorKind.DelayedToLocal, ct);
        await _harness.MineToAsync(node, [lnd], confirmedAt + csv - 1, ct);
        var sweepTxId = await _harness.MineUntilResolvingTxAsync(node, [lnd], channelId, htlcTxId, row.OutputIndex,
                                                                 ct);
        var sweep = (await _harness.MineUntilConfirmedAsync(node, [lnd], sweepTxId, ct)).Transaction;
        var input = Assert.Single(sweep.Inputs, i => i.PrevOut == new OutPoint(htlcTx, row.OutputIndex));
        Assert.Equal((uint)csv, input.Sequence.Value);
        AssertScriptPathSpend(input, 3);
        Assert.Equal(33, input.WitScript[2].Length); // a single-leaf tree: no inclusion proof
        return sweep;
    }

    /// <summary>
    /// Mines until every output of ours is resolved irrevocably (100 blocks deep) and the channel is <c>Closed</c>:
    /// <c>pendingsweeps</c> no longer lists it, and with closed channels it lists only resolved or ignored outputs.
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
            Assert.True(output.State is OutputResolutionState.Irrevocable or OutputResolutionState.Ignored,
                        $"{output.Descriptor} {output.TransactionId}:{output.OutputIndex} is {output.State}");
            if (output.Descriptor is not (OutputDescriptorKind.OurAnchor or OutputDescriptorKind.PeerAnchor
                                          or OutputDescriptorKind.PeerOutput))
                Assert.Equal(OutputResolutionState.Irrevocable, output.State);
        }

        foreach (var abandoned in closed.AbandonedBroadcasts)
            Console.WriteLine($"Abandoned broadcast: {abandoned.TransactionId} {abandoned.Purpose}");
    }

    /// <summary>
    /// <paramref name="lnd"/> lists the channel closed with <paramref name="closeType"/> and the commitment; its
    /// resolutions are logged.
    /// </summary>
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
        Console.WriteLine($"{lnd.LocalAlias} closed the channel: {summary.CloseType}, closing tx "
                        + $"{summary.ClosingTxHash}, settled {summary.SettledBalance}, time-locked "
                        + $"{summary.TimeLockedBalance}");
        foreach (var resolution in summary.Resolutions)
            Console.WriteLine($"  {resolution.ResolutionType} {resolution.Outcome} {resolution.AmountSat} sat at "
                            + $"{resolution.Outpoint?.TxidStr}:{resolution.Outpoint?.OutputIndex} by "
                            + $"{resolution.SweepTxid}");
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
        }, s_timeout, "every non-anchor output of the revoked commitment taken and confirmed", ct,
                              TimeSpan.FromSeconds(1));
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

    /// <summary>The value of the inputs of <paramref name="tx"/> that do not spend <paramref name="parentTxId"/>.</summary>
    private async Task<Money> ForeignInputValueAsync(Transaction tx, uint256 parentTxId, CancellationToken ct)
    {
        var total = Money.Zero;
        foreach (var input in tx.Inputs.Where(i => i.PrevOut.Hash != parentTxId))
        {
            var parent = await _fixture.Bitcoin.GetRawTransactionAsync(input.PrevOut.Hash, true, ct);
            total += parent.Outputs[(int)input.PrevOut.N].Value;
        }

        return total;
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

    #region Taproot transaction facts

    /// <summary>The commitment spends the funding output alone by the MuSig2 key path (one 64-byte signature).</summary>
    private static void AssertKeyPathFundingSpend(Transaction commitment, string channelPoint)
    {
        var parts = channelPoint.Split(':');
        var input = Assert.Single(commitment.Inputs);
        Assert.Equal(new OutPoint(uint256.Parse(parts[0]), uint.Parse(parts[1])), input.PrevOut);
        Assert.Equal(64, Assert.Single(input.WitScript.Pushes).Length);
    }

    /// <summary>Every output of a taproot commitment is a segwit v1 (P2TR) output.</summary>
    private static void AssertAllP2Tr(Transaction commitment)
    {
        foreach (var output in commitment.Outputs)
            Assert.True(output.ScriptPubKey.IsScriptType(ScriptType.Taproot),
                        $"commitment output {output.ScriptPubKey} is not P2TR");
        Console.WriteLine($"Commitment {commitment.GetHash()}: "
                        + string.Join(", ", commitment.Outputs.Select((o, i) => $"{i}: {o.Value.Satoshi} sat")));
    }

    private static void AssertKeyPathSpend(TxIn input)
    {
        var signature = Assert.Single(input.WitScript.Pushes);
        Assert.True(signature.Length is 64 or 65, $"key-path signature of {signature.Length} bytes");
    }

    /// <summary>
    /// A script-path spend: <paramref name="items"/> witness items, the last a BIP 341 control block (leaf version
    /// 0xc0, a 32-byte internal key and 32-byte inclusion proofs).
    /// </summary>
    private static void AssertScriptPathSpend(TxIn input, int items)
    {
        Assert.Equal(items, input.WitScript.PushCount);
        var controlBlock = input.WitScript[items - 1];
        Assert.True(controlBlock.Length >= 33 && (controlBlock.Length - 33) % 32 == 0,
                    $"control block of {controlBlock.Length} bytes");
        Assert.Equal(0xc0, controlBlock[0] & 0xfe);
    }

    /// <summary>
    /// The taproot <c>to_remote</c> leaf: <c>&lt;remotepubkey&gt; OP_CHECKSIGVERIFY 1 OP_CHECKSEQUENCEVERIFY</c>.
    /// </summary>
    private static void AssertToRemoteScript(byte[] script)
    {
        Assert.Equal(36, script.Length);
        Assert.Equal(0x20, script[0]);
        Assert.Equal([0xad, 0x51, 0xb2], script[33..]);
    }

    /// <summary>
    /// Our taproot second-level HTLC transaction: the HTLC input (nSequence 1) spent by script path with the witness
    /// <c>&lt;remotehtlcsig&gt; &lt;localhtlcsig&gt; [&lt;preimage&gt;] &lt;leaf&gt; &lt;control block&gt;</c>, LND's
    /// signature <c>SIGHASH_SINGLE|ANYONECANPAY</c> (65 bytes), the P2TR second-level output at the input's index, and
    /// at least one wallet input for the fee (zero-fee HTLC transactions).
    /// </summary>
    private static TxIn AssertTaprootHtlcTransaction(Transaction tx, OutPoint htlcOutPoint, bool isSuccess)
    {
        var htlcInput = Assert.Single(tx.Inputs, i => i.PrevOut == htlcOutPoint);
        Assert.Equal(1u, htlcInput.Sequence.Value);
        AssertScriptPathSpend(htlcInput, isSuccess ? 5 : 4);
        var remoteSignature = htlcInput.WitScript[0];
        Assert.Equal(65, remoteSignature.Length);
        Assert.Equal(AnchorsHarness.SigHashSingleAnyoneCanPay, remoteSignature[^1]);
        var localSignature = htlcInput.WitScript[1];
        Assert.True(localSignature.Length is 64 or 65, $"our signature of {localSignature.Length} bytes");
        Assert.True(AnchorsHarness.HasForeignInput(tx, htlcOutPoint.Hash), "the HTLC transaction has no fee input");

        var index = tx.Inputs.IndexOf(htlcInput);
        Assert.True(index < tx.Outputs.Count, "no output at the HTLC input's index");
        Assert.True(tx.Outputs[index].ScriptPubKey.IsScriptType(ScriptType.Taproot),
                    "the second-level output is not P2TR");
        Console.WriteLine($"HTLC-{(isSuccess ? "success" : "timeout")} {tx.GetHash()}: {tx.Inputs.Count} inputs, "
                        + $"{tx.Outputs.Count} outputs, HTLC input {index}, our signature {localSignature.Length} "
                        + $"bytes, vsize {tx.GetVirtualSize()}");
        return htlcInput;
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