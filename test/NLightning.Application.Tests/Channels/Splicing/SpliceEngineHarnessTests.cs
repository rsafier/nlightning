namespace NLightning.Application.Tests.Channels.Splicing;

using Domain.Bitcoin.Transactions.Models;
using Domain.Channels.Enums;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.ValueObjects;
using Harness;

/// <summary>
/// Splicing plan SP1-D-T3 on lanes SP1-B/SP1-C's real code (<see cref="SpliceHarness"/> with <c>realEngine</c>): the
/// production <c>EngineSpliceStatePort</c> over the several-funding engine, the real <c>LocalLightningSigner</c> splice
/// members (rotated funding keys, SP-I1, the 2-of-2 shared input), real BOLT 3 commitments on every funding, and
/// <c>start_batch</c> groups handed to <c>ChannelManager.HandleCommitmentSignedBatchAsync</c> as the peer's inbound loop
/// does (lane SP1-A).
/// </summary>
public class SpliceEngineHarnessTests
{
    private const uint CltvExpiry = 700;
    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    private static bool IsCommitmentFlowMessage(IChannelMessage message) =>
        message.Type is MessageTypes.UpdateAddHtlc or MessageTypes.UpdateFulfillHtlc or MessageTypes.StartBatch
                     or MessageTypes.CommitmentSigned or MessageTypes.RevokeAndAck or MessageTypes.SpliceLocked;

    #region SP-T-01 successful single splice (bolt02/splicing-test.md)

    /// <summary>
    /// SP-T-01, "Successful single splice", every step on the real engine: the negotiation (stfu, splice_init,
    /// splice_ack, the construction, commit_sig both ways at the current numbers, tx_signatures both ways), then an
    /// update while the splice is unconfirmed signed as <c>start_batch</c> (batch_size 2) + one <c>commit_sig</c> per
    /// active funding (FundingTx1 first, then FundingTx2, same commitment number) both ways, each answered by one
    /// <c>revoke_and_ack</c>; <c>splice_locked</c> both ways at depth; then a single <c>commit_sig</c> on FundingTx2.
    /// </summary>
    [Fact]
    public async Task Given_AliceSplicesIn_When_UsedWhilePendingAndLocked_Then_TheMessagesFollowSpT01()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var fundingTx1 = harness.Alice.Node.State.Params.Funding!.FundingTxId;
        var localNumber = harness.Alice.Node.State.LocalCommit.Number;

        // Act: the splice
        var result = await harness.SpliceAsync(harness.Alice, 100_000);

        // Assert: signed and pending on both engines at the unchanged numbers (SP-CS-01/02)
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        Assert.Empty(harness.Failures);
        var fundingTx2 = result.SpliceTxId!.Value;
        Assert.Equal(
        [
            "Alice:Stfu", "Bob:Stfu", "Alice:SpliceInit", "Bob:SpliceAck", "Bob:TxComplete", "Bob:TxComplete",
            "Bob:TxComplete", "Bob:TxComplete", "Alice:TxComplete", "Alice:CommitmentSigned", "Bob:CommitmentSigned",
            "Bob:TxSignatures", "Alice:TxSignatures"
        ], harness.Sequence(m => m.Type is MessageTypes.Stfu or MessageTypes.SpliceInit or MessageTypes.SpliceAck
                                        or MessageTypes.TxComplete or MessageTypes.CommitmentSigned
                                        or MessageTypes.TxSignatures));
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var pending = Assert.Single(node.Node.State.PendingFundings);
            Assert.Equal(fundingTx2, pending.FundingTxId);
            Assert.Equal(TwoNodeHarness.FundingSatoshis + 100_000, pending.CapacitySatoshis);
            Assert.Equal(localNumber, node.Node.State.LocalCommit.Number);
            Assert.Equal(ChannelFundingStatus.Pending, node.FundingRows.Committed[fundingTx2].Status);
            Assert.True(node.FundingRows.CommittedLocal.ContainsKey(fundingTx2));
            Assert.True(node.FundingRows.CommittedRemote.ContainsKey(fundingTx2));
        }

        Assert.Equal(harness.Alice.Broadcasts.Single().RawTransaction, harness.Bob.Broadcasts.Single().RawTransaction);
        // NL-626: the splice's broadcast row is labeled Splice on both sides, not Funding
        Assert.All([harness.Alice.Broadcasts.Single(), harness.Bob.Broadcasts.Single()],
                   b => Assert.Equal(BroadcastPurpose.Splice, b.Purpose));

        // Act: an HTLC from Alice to Bob while the splice is unconfirmed (the spec's update_add_htlc, then the batch)
        var mark = harness.Transcript.Count;
        var (id, preimage) = await OfferAsync(harness, harness.Alice, 30_000_000, 1);

        // Assert: start_batch(2) + commit_sig on FundingTx1 then FundingTx2 at number n+1, one revoke_and_ack each way
        var number = localNumber + 1;
        Assert.Equal(
        [
            "Alice:UpdateAddHtlc", "Alice:StartBatch", $"Alice:CommitmentSigned:{fundingTx1}",
            $"Alice:CommitmentSigned:{fundingTx2}", "Bob:RevokeAndAck", "Bob:StartBatch",
            $"Bob:CommitmentSigned:{fundingTx1}", $"Bob:CommitmentSigned:{fundingTx2}", "Alice:RevokeAndAck"
        ], Describe(harness, mark));
        Assert.All(harness.Transcript.Skip(mark).Select(t => t.Message).OfType<StartBatchMessage>(),
                   b => Assert.Equal(2, b.Payload.BatchSize));
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Equal(number, node.Node.State.LocalCommit.Number);
            Assert.Single(node.Node.State.LocalCommit.PendingFundingSignatures);
            Assert.Equal(fundingTx2, node.Node.State.LocalCommit.PendingFundingSignatures[0].FundingTxId);
        }

        // Act: Bob fulfills while still pending (batches the other way first)
        mark = harness.Transcript.Count;
        await FulfillAsync(harness, harness.Bob, id, preimage);

        // Assert
        Assert.Equal(
        [
            "Bob:UpdateFulfillHtlc", "Bob:StartBatch", $"Bob:CommitmentSigned:{fundingTx1}",
            $"Bob:CommitmentSigned:{fundingTx2}", "Alice:RevokeAndAck", "Alice:StartBatch",
            $"Alice:CommitmentSigned:{fundingTx1}", $"Alice:CommitmentSigned:{fundingTx2}", "Bob:RevokeAndAck"
        ], Describe(harness, mark));

        // Act: the splice transaction confirms on both sides
        mark = harness.Transcript.Count;
        await harness.ConfirmAsync(fundingTx2, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);

        // Assert: splice_locked both ways (SP-LK-01), then FundingTx2 is the only active funding on both (SP-LK-03)
        Assert.Equal(["Alice:SpliceLocked", "Bob:SpliceLocked"], Describe(harness, mark));
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var state = node.Node.State;
            Assert.Empty(state.PendingFundings);
            Assert.Equal(fundingTx2, state.Params.Funding!.FundingTxId);
            Assert.Equal(TwoNodeHarness.FundingSatoshis + 100_000, state.Params.FundingSatoshis);
            Assert.Equal((TwoNodeHarness.FundingSatoshis + 100_000) * 1_000,
                         state.LocalBalanceMsat + state.RemoteBalanceMsat);
            Assert.Equal(fundingTx2, node.FundingRows.LockedCurrent!.FundingTxId);
            Assert.Equal(ChannelFundingStatus.Replaced, node.FundingRows.Committed[fundingTx1].Status);

            // The channel and the locked funding row take the splice's short channel id (block, index 1, output)
            var spliceScid = new ShortChannelId(TwoNodeHarness.BlockHeight + 3, 1, state.Params.Funding.OutputIndex);
            Assert.Equal(spliceScid, node.FundingRows.LockedCurrent.ShortChannelId);
            Assert.Equal(spliceScid, node.Node.Channel.ShortChannelId);
        }

        // Act: Alice and Bob use the channel and forget FundingTx1
        mark = harness.Transcript.Count;
        await OfferAsync(harness, harness.Alice, 10_000_000, 2);

        // Assert: a single commit_sig each way, on FundingTx2
        Assert.Equal(
        [
            "Alice:UpdateAddHtlc", $"Alice:CommitmentSigned:{fundingTx2}", "Bob:RevokeAndAck",
            $"Bob:CommitmentSigned:{fundingTx2}", "Alice:RevokeAndAck"
        ], Describe(harness, mark));
        Assert.Empty(harness.Failures);
        Assert.Equal(ChannelState.Open, harness.Alice.Node.Channel.State);
        Assert.Equal(ChannelState.Open, harness.Bob.Node.Channel.State);
    }

    #endregion

    #region Splice-in and splice-out, each side initiating, payments both ways while pending

    public static TheoryData<string, long> Splices => new()
    {
        { "Alice", 100_000 }, // splice-in by the funder
        { "Bob", 60_000 }, // splice-in by the non-funder
        { "Alice", -80_000 }, // splice-out by the funder
        { "Bob", -40_000 } // splice-out by the non-funder (the pushed balance)
    };

    /// <summary>
    /// Each side splices in and out on the real engine; while the splice is pending one payment goes each way (batched
    /// commit_sig both ways, SP-OP-03/07), then the splice locks at depth and a payment goes over the new funding. The
    /// capacity and each side's balance follow the contribution (a splice-out pays its share of the fee, D16).
    /// </summary>
    [Theory]
    [MemberData(nameof(Splices))]
    public async Task Given_ASplice_When_PaymentsGoBothWaysWhilePending_Then_BatchesFlowAndTheLockMovesTheBalances(
        string initiatorName, long contributionSatoshis)
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        var initiator = initiatorName == "Alice" ? harness.Alice : harness.Bob;
        var other = harness.Other(initiator);
        if (contributionSatoshis > 0)
            initiator.Fund(contributionSatoshis + 200_000);
        var before = initiator.Node.State.LocalBalanceMsat;
        var otherBefore = other.Node.State.LocalBalanceMsat;

        // Act: the splice
        var result = await harness.SpliceAsync(initiator, contributionSatoshis);

        // Assert: pending with the initiator's delta (a splice-out also pays the fee: -(amount + fee), D16)
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        var fundingTx2 = result.SpliceTxId!.Value;
        var pending = Assert.Single(initiator.Node.State.PendingFundings);
        Assert.Equal(0, pending.RemoteBalanceDeltaMsat);
        if (contributionSatoshis > 0)
            Assert.Equal(contributionSatoshis * 1_000, pending.LocalBalanceDeltaMsat);
        else
            Assert.True(pending.LocalBalanceDeltaMsat < contributionSatoshis * 1_000);
        Assert.Equal((long)(pending.CapacitySatoshis - TwoNodeHarness.FundingSatoshis) * 1_000,
                     pending.LocalBalanceDeltaMsat);
        Assert.Equal(pending with { LocalBalanceDeltaMsat = 0, RemoteBalanceDeltaMsat = pending.LocalBalanceDeltaMsat },
                     Assert.Single(other.Node.State.PendingFundings) with
                     {
                         LocalFundingPubKey = pending.LocalFundingPubKey,
                         RemoteFundingPubKey = pending.RemoteFundingPubKey,
                         LocalFundingKeyIndex = pending.LocalFundingKeyIndex,
                         LocalBalanceDeltaMsat = 0,
                         RemoteBalanceDeltaMsat = pending.LocalBalanceDeltaMsat
                     });
        if (contributionSatoshis < 0)
        {
            var spliceTx = NBitcoin.Transaction.Parse(Convert.ToHexString(initiator.Broadcasts.Single().RawTransaction),
                                                      NBitcoin.Network.RegTest);
            Assert.Contains(spliceTx.Outputs,
                            o => o.ScriptPubKey.ToBytes().SequenceEqual((byte[])initiator.Destination.Script)
                              && o.Value.Satoshi == -contributionSatoshis);
        }

        // Act: one payment each way while pending
        var mark = harness.Transcript.Count;
        var (aliceId, alicePreimage) = await OfferAsync(harness, harness.Alice, 20_000_000, 1);
        await FulfillAsync(harness, harness.Bob, aliceId, alicePreimage);
        var (bobId, bobPreimage) = await OfferAsync(harness, harness.Bob, 5_000_000, 2);
        await FulfillAsync(harness, harness.Alice, bobId, bobPreimage);

        // Assert: every commitment_signed went in a batch of two (current funding first), no single one
        var signed = harness.Transcript.Skip(mark).Select(t => t.Message).OfType<CommitmentSignedMessage>().ToList();
        Assert.Equal(16, signed.Count);
        Assert.Equal(8, harness.Transcript.Skip(mark).Count(t => t.Message is StartBatchMessage));
        Assert.Empty(harness.Failures);

        // Act: lock at depth
        await harness.ConfirmAsync(fundingTx2, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);

        // Assert: the new capacity, the initiator's balance moved by its delta and the payments (15,000 sat out of
        // Alice), the other side's by the payments only
        var aliceNet = -15_000_000L;
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Node.State.PendingFundings);
            Assert.Equal(fundingTx2, node.Node.State.Params.Funding!.FundingTxId);
            Assert.Equal(pending.CapacitySatoshis, node.Node.State.Params.FundingSatoshis);
        }

        var initiatorNet = initiator == harness.Alice ? aliceNet : -aliceNet;
        Assert.Equal((long)before + pending.LocalBalanceDeltaMsat + initiatorNet,
                     (long)initiator.Node.State.LocalBalanceMsat);
        Assert.Equal((long)otherBefore - initiatorNet, (long)other.Node.State.LocalBalanceMsat);

        // Act / Assert: a payment over the new funding, single commit_sig again
        mark = harness.Transcript.Count;
        await OfferAsync(harness, harness.Alice, 1_000_000, 3);
        Assert.DoesNotContain(harness.Transcript.Skip(mark), t => t.Message is StartBatchMessage);
        Assert.All(harness.Transcript.Skip(mark).Select(t => t.Message).OfType<CommitmentSignedMessage>(),
                   cs => Assert.Equal(fundingTx2, cs.FundingTxIdTlv?.FundingTxId));
        Assert.Empty(harness.Failures);
    }

    #endregion

    #region SP-T-02 concurrent splice_locked (the half without RBF, which is wave SPR)

    /// <summary>
    /// SP-T-02's concurrent <c>splice_locked</c>: Alice sends <c>splice_locked</c>, then an update and its batch
    /// (FundingTx1, FundingTx2); Bob reaches the depth before the batch arrives, so his lock (both sent) makes the
    /// FundingTx1 member obsolete: he ignores it (SP-OP-06), answers one <c>revoke_and_ack</c>, and Alice, who locks on
    /// Bob's <c>splice_locked</c> while her batch is unacknowledged, keeps the FundingTx2 signatures. Nobody fails.
    /// </summary>
    [Fact]
    public async Task Given_SpliceLockedCrossesABatch_When_TheBatchArrivesAfterTheLock_Then_TheObsoleteMemberIsIgnored()
    {
        // Arrange: a pending splice
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var result = await harness.SpliceAsync(harness.Alice, 100_000);
        var fundingTx2 = result.SpliceTxId!.Value;
        var fundingTx1 = harness.Bob.Node.State.Params.Funding!.FundingTxId;

        // Act: Alice reaches the depth and sends splice_locked; then an update and its batch, none delivered yet
        harness.Alice.Confirm(fundingTx2, TwoNodeHarness.BlockHeight + 3);
        await harness.Alice.DepthWatcher.WhenIdleAsync();
        var preimage = TwoNodeHarness.Preimage(7);
        var hash = TwoNodeHarness.Hash(preimage);
        var id = await harness.Alice.Node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId,
                                                                   LightningMoney.MilliSatoshis(25_000_000), hash,
                                                                   CltvExpiry, s_onion, null, HtlcOrigin.Local(hash),
                                                                   TestContext.Current.CancellationToken);
        await harness.WhenIdleAsync();
        Assert.Equal(["Alice:SpliceLocked", "Alice:UpdateAddHtlc", "Alice:StartBatch",
                      $"Alice:CommitmentSigned:{fundingTx1}", $"Alice:CommitmentSigned:{fundingTx2}"],
                     Describe(harness, harness.Transcript.Count - 5));

        // Bob gets splice_locked and the add, then reaches the depth himself: both sent, he locks
        Assert.True(await harness.Alice.Node.DeliverNextAsync());
        Assert.True(await harness.Alice.Node.DeliverNextAsync());
        harness.Bob.Confirm(fundingTx2, TwoNodeHarness.BlockHeight + 3);
        await harness.Bob.DepthWatcher.WhenIdleAsync();
        Assert.Empty(harness.Bob.Node.State.PendingFundings);
        Assert.Equal(fundingTx2, harness.Bob.Node.State.Params.Funding!.FundingTxId);

        // Act: everything else is delivered (Alice's batch to Bob, Bob's splice_locked to Alice, the replies)
        await harness.PumpAsync();

        // Assert: both locked on FundingTx2 at the same commitment numbers, the HTLC committed, nobody failed
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Node.State.PendingFundings);
            Assert.Equal(fundingTx2, node.Node.State.Params.Funding!.FundingTxId);
            Assert.Equal(ChannelState.Open, node.Node.Channel.State);
        }

        Assert.Equal(harness.Alice.Node.State.LocalCommit.Number, harness.Bob.Node.State.RemoteCommit.Number);
        Assert.Equal(harness.Bob.Node.State.LocalCommit.Number, harness.Alice.Node.State.RemoteCommit.Number);
        Assert.Single(harness.Bob.Node.State.Htlcs);

        // Act / Assert: the HTLC settles over FundingTx2 alone
        var mark = harness.Transcript.Count;
        await FulfillAsync(harness, harness.Bob, id, preimage);
        Assert.Empty(harness.Bob.Node.State.Htlcs);
        Assert.All(harness.Transcript.Skip(mark).Select(t => t.Message).OfType<CommitmentSignedMessage>(),
                   cs => Assert.Equal(fundingTx2, cs.FundingTxIdTlv?.FundingTxId));
        Assert.Empty(harness.Failures);
    }

    /// <summary>
    /// One side reaches the depth long before the other: its <c>splice_locked</c> waits, payments keep going in
    /// batches over both fundings (the lock needs both), and the lock happens when the peer's arrives (SP-LK-03).
    /// </summary>
    [Fact]
    public async Task Given_OnlyAliceReachedTheDepth_When_PaymentsFlow_Then_BatchesContinueUntilBobLocksToo()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        harness.Bob.Fund(300_000);
        var result = await harness.SpliceAsync(harness.Bob, 50_000);
        var fundingTx2 = result.SpliceTxId!.Value;

        // Act: only Alice's chain monitor reports the depth
        await harness.ConfirmAsync(fundingTx2, TwoNodeHarness.BlockHeight + 3, harness.Alice);
        var mark = harness.Transcript.Count;
        var (id, preimage) = await OfferAsync(harness, harness.Alice, 3_000_000, 1);
        await FulfillAsync(harness, harness.Bob, id, preimage);

        // Assert: still two active fundings, batches both ways
        Assert.Single(harness.Alice.Node.State.PendingFundings);
        Assert.Single(harness.Bob.Node.State.PendingFundings);
        Assert.Equal(4, harness.Transcript.Skip(mark).Count(t => t.Message is StartBatchMessage));

        // Act: Bob's monitor catches up
        await harness.ConfirmAsync(fundingTx2, TwoNodeHarness.BlockHeight + 4, harness.Bob);

        // Assert
        Assert.Equal(["Alice:SpliceLocked", "Bob:SpliceLocked"],
                     harness.Sequence(m => m.Type == MessageTypes.SpliceLocked));
        Assert.Empty(harness.Alice.Node.State.PendingFundings);
        Assert.Empty(harness.Bob.Node.State.PendingFundings);
        Assert.Empty(harness.Failures);
    }

    #endregion

    #region The splice transaction spending the funding output

    /// <summary>
    /// The chain monitor reports the funding output spent by the splice transaction (it confirms): that is not a close
    /// (splicing plan §3.6), so nothing reaches the on-chain watcher and the channel stays <c>Open</c> and locks.
    /// Without the check the channel was classified as closed by an unknown transaction (Proof SP1 against CLN).
    /// </summary>
    [Fact]
    public async Task Given_TheSpliceTransactionSpendsTheFundingOutput_When_Reported_Then_TheChannelIsNotClosed()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var result = await harness.SpliceAsync(harness.Alice, 100_000);
        var fundingTx2 = result.SpliceTxId!.Value;
        var fundingTx1 = harness.Alice.Node.State.Params.Funding!.FundingTxId;

        // Act: both monitors see the funding output spent by the splice, before and after the lock
        foreach (var node in new[] { harness.Alice, harness.Bob })
            RaiseFundingSpent(node, fundingTx1, TwoNodeHarness.BlockHeight + 1);
        await harness.ConfirmAsync(fundingTx2, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);
        foreach (var node in new[] { harness.Alice, harness.Bob })
            RaiseFundingSpent(node, fundingTx1, TwoNodeHarness.BlockHeight + 1);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.FundingSpends);
            Assert.Equal(ChannelState.Open, node.Node.Channel.State);
            Assert.Equal(fundingTx2, node.Node.State.Params.Funding!.FundingTxId);
        }

        // The channel model's funding output followed the lock, and any other transaction spending it (a commitment)
        // is handed to the on-chain watcher as before (the watch of the new outpoint itself is wave SP2)
        var fundingOutput = harness.Alice.Node.Channel.FundingOutput!;
        Assert.Equal(fundingTx2, fundingOutput.TransactionId);
        Assert.Equal(TwoNodeHarness.FundingSatoshis + 100_000, (ulong)fundingOutput.Amount.Satoshi);
        var other = new Domain.Bitcoin.ValueObjects.SignedTransaction(
            new Domain.Bitcoin.ValueObjects.TxId(Enumerable.Repeat((byte)0x42, 32).ToArray()), [0x02, 0x00]);
        harness.Alice.Node.ChainMonitor.Raise(
            m => m.OnWatchedOutpointSpent += null,
            new Domain.Bitcoin.Events.OutpointSpentEventArgs(TwoNodeHarness.ChannelId, other,
                                                             TwoNodeHarness.BlockHeight + 9, 1, fundingTx2,
                                                             fundingOutput.Index));
        for (var i = 0; i < 100 && harness.Alice.FundingSpends.IsEmpty; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Single(harness.Alice.FundingSpends);
    }

    private static void RaiseFundingSpent(SpliceNode node, Domain.Bitcoin.ValueObjects.TxId fundingTx1, uint height)
    {
        var splice = node.Broadcasts.Single();
        var spend = new Domain.Bitcoin.ValueObjects.SignedTransaction(splice.TransactionId,
                                                                             splice.RawTransaction);
        node.Node.ChainMonitor.Raise(m => m.OnWatchedOutpointSpent += null,
                                     new Domain.Bitcoin.Events.OutpointSpentEventArgs(
                                         TwoNodeHarness.ChannelId, spend, height, 1, fundingTx1,
                                         node.Node.Channel.FundingOutput!.Index));
    }

    #endregion

    #region The locked funding's outpoint, the depth catch-up

    /// <summary>
    /// The splice's funding outpoint is watched for a spend from the save that precedes our splice
    /// <c>commitment_signed</c> on (wave sp2, lane SP2-C: a commitment on the pending funding, revoked or not, must reach
    /// the on-chain watcher before the lock), stored in that save and tracked by the chain monitor after it; the lock
    /// keeps that watch (one row, tracked once). Before wave sp2 it was watched only from the lock on.
    /// </summary>
    [Fact]
    public async Task Given_ASpliceLocks_When_TheLockIsSaved_Then_TheNewFundingOutpointIsWatched()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var result = await harness.SpliceAsync(harness.Alice, 100_000);
        var fundingTx2 = result.SpliceTxId!.Value;
        Assert.All(new[] { harness.Alice, harness.Bob },
                   node => Assert.Equal(fundingTx2, Assert.Single(node.WatchedOutpoints).TransactionId));

        // Act
        await harness.ConfirmAsync(fundingTx2, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);

        // Assert
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            uint index = node.Node.Channel.FundingOutput!.Index!.Value;
            var watch = Assert.Single(node.WatchedOutpoints);
            Assert.Equal(fundingTx2, watch.TransactionId);
            Assert.Equal(index, watch.OutputIndex);
            Assert.Equal(TwoNodeHarness.ChannelId, watch.ChannelId);
            Assert.Equal(WatchedOutpointPurpose.FundingOutput, watch.Purpose);
            node.Node.ChainMonitor.Verify(m => m.TrackWatchedOutpoint(It.Is<WatchedOutpointModel>(
                                                                         w => w.TransactionId == fundingTx2
                                                                           && w.OutputIndex == index)),
                                          Times.Once);
        }
    }

    /// <summary>
    /// A splice whose watch completed while nothing listened (the confirmation came before the depth watcher was
    /// subscribed, e.g. after a restart and before any splice message) is locked by the startup catch-up: our
    /// <c>splice_locked</c> goes out and, with the peer's, the funding is locked. A second catch-up does nothing.
    /// </summary>
    [Fact]
    public async Task Given_ASpliceConfirmedWhileNothingListened_When_TheDepthWatcherCatchesUp_Then_SpliceLockedIsSent()
    {
        // Arrange: Alice's monitor completed the watch without raising the event
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var result = await harness.SpliceAsync(harness.Alice, 100_000);
        var fundingTx2 = result.SpliceTxId!.Value;
        var watch = harness.Alice.Watches.Single(w => w.TransactionId == fundingTx2);
        watch.SetHeightAndIndex(TwoNodeHarness.BlockHeight + 3, 1);
        watch.MarkAsCompleted();
        var mark = harness.Transcript.Count;

        // Act
        var handed = await harness.Alice.DepthWatcher.CatchUpAsync(TestContext.Current.CancellationToken);
        await harness.PumpAsync();
        var again = await harness.Alice.DepthWatcher.CatchUpAsync(TestContext.Current.CancellationToken);
        await harness.ConfirmAsync(fundingTx2, TwoNodeHarness.BlockHeight + 3, harness.Bob);

        // Assert
        Assert.Equal(1, handed);
        Assert.Equal(0, again);
        Assert.Equal(["Alice:SpliceLocked", "Bob:SpliceLocked"], Describe(harness, mark));
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Node.State.PendingFundings);
            Assert.Equal(fundingTx2, node.Node.State.Params.Funding!.FundingTxId);
        }
    }

    #endregion

    #region A splice reorged out before its lock (SP2-C-T4, NL-493)

    /// <summary>
    /// Alice reaches the depth first (her <c>splice_locked</c> is out) and the splice's block is disconnected: she
    /// moves the splice back to waiting (the depth state and the lost block's short channel id are reset, also in the
    /// funding row), and when the splice confirms again on the new branch both sides lock it at the new short channel
    /// id. After the lock she sent her <c>splice_locked</c> twice, the depth watcher's idempotency alone would have
    /// kept the stale one.
    /// </summary>
    [Fact]
    public async Task Given_ASpliceReorgedOutAfterOurSpliceLocked_When_ItsBlockIsDisconnected_Then_ItWaitsAgainAndLocksOnTheNewBranch()
    {
        // Arrange: Alice's monitor reports the depth, Bob's does not yet
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var spliceTxId = (await harness.SpliceAsync(harness.Alice, 100_000)).SpliceTxId!.Value;
        const uint depthHeight = TwoNodeHarness.BlockHeight + 3;
        var mark = harness.Transcript.Count;
        await harness.ConfirmAsync(spliceTxId, depthHeight, harness.Alice);
        var sent = harness.Alice.FundingRows.Committed[spliceTxId];
        Assert.True(sent.SpliceLockedSent);
        Assert.NotNull(sent.ShortChannelId);

        // Act: the block holding the splice is disconnected (the monitor resets the watch row's position)
        var watch = harness.Alice.Watches.Single(w => w.TransactionId == spliceTxId);
        harness.Alice.Watches.Remove(watch);
        harness.Alice.Node.ChainMonitor.Raise(m => m.OnBlockDisconnected += null,
                                              new Domain.Onchain.Events.BlockDisconnectedEventArgs(
                                                  depthHeight,
                                                  new Domain.Crypto.ValueObjects.Hash(new byte[32]), depthHeight - 1));
        await harness.Alice.DepthWatcher.WhenIdleAsync();
        await harness.PumpAsync();

        // Assert: the splice is unconfirmed again, in its row (the in-memory reset is what the re-confirmation below
        // exercises: without it the lock's idempotency would swallow the new depth)
        var waiting = harness.Alice.FundingRows.Committed[spliceTxId];
        Assert.False(waiting.SpliceLockedSent);
        Assert.Null(waiting.ConfirmedHeight);
        Assert.Null(waiting.ShortChannelId);

        // Act: the splice confirms again on the new branch (a different block, the same index) and locks both ways
        harness.Alice.Watches.Add(new WatchedTransactionModel(TwoNodeHarness.ChannelId, spliceTxId,
                                                              watch.RequiredDepth));
        await harness.ConfirmAsync(spliceTxId, depthHeight + 4, harness.Alice, harness.Bob);

        // Assert
        Assert.Empty(harness.Failures);
        Assert.Equal(["Alice:SpliceLocked", "Alice:SpliceLocked", "Bob:SpliceLocked"], Describe(harness, mark));
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Node.State.PendingFundings);
            Assert.Equal(spliceTxId, node.Node.State.Params.Funding!.FundingTxId);
            Assert.Equal(new ShortChannelId(depthHeight + 4, 1, node.Node.Channel.FundingOutput!.Index!.Value),
                         node.FundingRows.Committed[spliceTxId].ShortChannelId);
        }
    }

    /// <summary>
    /// The depth was reached and the reorg rolled the watch row's position back (a restart during the reorg, whose
    /// funding rows keep the depth state): the startup catch-up moves the splice back to waiting instead of leaving it
    /// depth-reached with the lost block's short channel id.
    /// </summary>
    [Fact]
    public async Task Given_ASpliceWhoseDepthWasReachedAndReorgedOut_When_TheWatcherCatchesUp_Then_ItWaitsAgain()
    {
        // Arrange: the depth was reached; the reorg's rollback replaced the watch row with a pending one
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var spliceTxId = (await harness.SpliceAsync(harness.Alice, 100_000)).SpliceTxId!.Value;
        await harness.ConfirmAsync(spliceTxId, TwoNodeHarness.BlockHeight + 3, harness.Alice);
        Assert.True(harness.Alice.FundingRows.Committed[spliceTxId].SpliceLockedSent);
        var watch = harness.Alice.Watches.Single(w => w.TransactionId == spliceTxId);
        harness.Alice.Watches.Remove(watch);
        harness.Alice.Watches.Add(new WatchedTransactionModel(TwoNodeHarness.ChannelId, spliceTxId,
                                                              watch.RequiredDepth));

        // Act
        var handed = await harness.Alice.DepthWatcher.CatchUpAsync(TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(0, handed);
        var waiting = harness.Alice.FundingRows.Committed[spliceTxId];
        Assert.False(waiting.SpliceLockedSent);
        Assert.Null(waiting.ConfirmedHeight);
    }

    #endregion

    #region Abort after the commitment step

    /// <summary>
    /// On the real engine the peer's splice <c>commitment_signed</c> makes the funding pending before
    /// <c>tx_signatures</c>; a <c>tx_abort</c> then discards it from the engine (no batch covers it afterwards), and the
    /// funding row is stored as discarded.
    /// </summary>
    [Fact]
    public async Task Given_ATxAbortAfterTheSpliceCommitments_When_Received_Then_TheEngineDropsThePendingFunding()
    {
        // Arrange: stop when Bob's tx_signatures is next (Alice has verified Bob's commitment_signed)
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(500_000);
        var start = harness.Alice.Service.StartAsync(
            new Domain.Channels.Splicing.Models.SpliceRequest(TwoNodeHarness.ChannelId, 100_000,
                                                              SpliceHarness.FeeratePerKw),
            TestContext.Current.CancellationToken);
        for (var round = 0; round < 2_000 && harness.Bob.Node.PeekNext() is not TxSignaturesMessage; round++)
        {
            await harness.WhenIdleAsync();
            if (!await harness.Alice.Node.DeliverNextAsync() && !await harness.Bob.Node.DeliverNextAsync())
                await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        var spliceTxId = Assert.Single(harness.Alice.Node.State.PendingFundings).FundingTxId;
        Assert.True(harness.Bob.Node.TryTakeNext(out _));

        // Act
        var abort = new TxAbortMessage(new Domain.Protocol.Payloads.TxAbortPayload(TwoNodeHarness.ChannelId, [0x01]));
        await harness.Alice.Node.ChannelManager.HandleChannelMessageAsync(abort, SpliceHarness.CreateFeatures(),
                                                                          SpliceHarness.NodeIdOf("Bob"));
        var result = await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await harness.WhenIdleAsync();

        // Assert
        Assert.Equal(SpliceNegotiationState.Aborted, result.State);
        Assert.Empty(harness.Alice.Node.State.PendingFundings);
        Assert.Empty(harness.Alice.Node.State.LocalCommit.PendingFundingSignatures);
        Assert.Equal(ChannelFundingStatus.Discarded, harness.Alice.FundingRows.Committed[spliceTxId].Status);
        Assert.Empty(harness.Alice.Broadcasts);
        Assert.False(harness.Alice.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
    }

    #endregion

    #region Helpers

    /// <summary>The commitment-flow messages raised since <paramref name="mark"/>, with each CS's funding txid.</summary>
    private static List<string> Describe(SpliceHarness harness, int mark) =>
        harness.Transcript.Skip(mark)
               .Where(t => IsCommitmentFlowMessage(t.Message))
               .Select(t => t.Message is CommitmentSignedMessage cs
                                ? $"{t.From}:CommitmentSigned:{cs.FundingTxIdTlv?.FundingTxId}"
                                : $"{t.From}:{Enum.GetName(t.Message.Type)}")
               .ToList();

    private static async Task<(ulong Id, Domain.Crypto.ValueObjects.Secret Preimage)> OfferAsync(
        SpliceHarness harness, SpliceNode from, ulong amountMsat, int tag)
    {
        var preimage = TwoNodeHarness.Preimage(tag);
        var hash = TwoNodeHarness.Hash(preimage);
        var id = await from.Node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId,
                                                           LightningMoney.MilliSatoshis(amountMsat), hash, CltvExpiry,
                                                           s_onion, null, HtlcOrigin.Local(hash),
                                                           TestContext.Current.CancellationToken);
        await harness.PumpAsync();
        return (id, preimage);
    }

    private static async Task FulfillAsync(SpliceHarness harness, SpliceNode by, ulong id,
                                           Domain.Crypto.ValueObjects.Secret preimage)
    {
        await by.Node.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, id, preimage,
                                                  TestContext.Current.CancellationToken);
        await harness.PumpAsync();
    }

    #endregion
}