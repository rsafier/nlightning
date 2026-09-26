using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Revoked;

using Application.Onchain.Resolvers.Revoked;
using Channels.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;

/// <summary>
/// BOLT 5 plan O5-T2/O5-T3 on a fake chain with real crypto: the cheater (Alice) broadcasts her revoked commitment
/// <c>k</c>, holding an HTLC the victim (Bob) offered (<c>b1</c>) and one she offered (<c>a1</c>); every penalty the
/// resolver builds is signed by Bob's real signer and executed against the outputs it spends.
/// </summary>
public class RevokedResolutionTests
{
    private const ulong B1Msat = 50_000_000;
    private const ulong A1Msat = 40_000_000;
    private const uint B1Expiry = 600;
    private const uint A1Expiry = 650;

    private static readonly Secret s_b1Preimage = RealSigningCommitmentPair.Preimage(0xB1);
    private static readonly Secret s_a1Preimage = RealSigningCommitmentPair.Preimage(0xA1);

    /// <summary>
    /// State k: b1 (Bob → Alice) and a1 (Alice → Bob) committed on both sides. Then a fee update makes Alice revoke k;
    /// both HTLCs stay pending (so their upstream sides are still open). Optionally Bob offers b2 after k.
    /// </summary>
    private static RevokedBreachKit CreateBreach(bool addB2AfterRevokedState = false,
                                                 RevokedCommitResolverOptions? options = null)
    {
        var kit = new RevokedBreachKit(options);
        var pair = kit.Pair;
        pair.Add(pair.Bob, B1Msat, s_b1Preimage, B1Expiry);
        pair.Add(pair.Alice, A1Msat, s_a1Preimage, A1Expiry);
        pair.Settle(pair.Bob);
        kit.CaptureRevokedState();

        if (addB2AfterRevokedState)
            pair.Add(pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(0xB2), 700);
        pair.UpdateFee(3_000);
        pair.Settle(pair.Alice);

        kit.Breach();
        return kit;
    }

    [Fact]
    public async Task Given_RevokedCommitmentWithHtlcs_When_Resolved_Then_OneBatchedPenaltySpendsEveryOutput()
    {
        // Arrange
        using var kit = CreateBreach();

        // Act
        var actions = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);

        // Assert: to_local, both HTLC outputs and our to_remote in one penalty, every input valid
        var broadcast = Assert.Single(actions.OfType<BroadcastAction>()).Transaction;
        Assert.Equal(BroadcastPurpose.Penalty, broadcast.Purpose);
        var penalty = kit.LoadBroadcast(broadcast.TransactionId);
        Assert.Equal(kit.RevokedCommitment.Outputs.Count, penalty.Inputs.Count);
        Assert.Equal(RevokedBreachKit.Destination, Assert.Single(penalty.Outputs).ScriptPubKey.ToBytes());
        kit.AssertVerifies(broadcast.TransactionId);

        var total = kit.RevokedCommitment.Outputs.Sum(o => o.Value.Satoshi);
        var fee = total - penalty.Outputs[0].Value.Satoshi;
        Assert.InRange(fee, 1, total / 100);

        // Every output has a row pointing at the penalty, and is watched
        Assert.Equal(kit.RevokedCommitment.Outputs.Count, kit.Rows.Count);
        Assert.All(kit.Rows, r =>
        {
            Assert.Equal(OutputResolutionState.Broadcast, r.State);
            Assert.Equal(broadcast.TransactionId, r.ResolvingTransactionId);
            Assert.Contains((r.TransactionId, r.OutputIndex), kit.Watches);
        });
        Assert.Contains(kit.Rows, r => r.Descriptor == OutputDescriptorKind.RevokedToLocal);
        Assert.Equal(2, kit.Rows.Count(r => r.Descriptor == OutputDescriptorKind.RevokedHtlc));
        Assert.Contains(kit.Rows, r => r.Descriptor == OutputDescriptorKind.PaymentToRemote);
        Assert.Empty(kit.Events);
        Assert.Empty(kit.Alerts);
    }

    [Fact]
    public async Task Given_PenaltyBroadcast_When_NextBlocks_Then_NothingIsRebuilt()
    {
        // Arrange
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);

        // Act
        var second = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 2);
        var third = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 3);

        // Assert
        Assert.Empty(second);
        Assert.Empty(third);
        Assert.Single(kit.Broadcasts);
    }

    [Fact]
    public async Task Given_PenaltyConfirmed_When_Resolved_Then_OutputsResolvedByUsAndNoAlert()
    {
        // Arrange
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var penalty = RevokedBreachKit.ToChainTx(kit.LoadBroadcast(kit.Broadcasts.Keys.Single()));

        // Act
        var spent = await kit.ConfirmAsync(penalty, RevokedBreachKit.SpentAtHeight + 2);
        var next = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 3);

        // Assert
        Assert.Empty(spent);
        Assert.DoesNotContain(next, a => a is BroadcastAction or AlertAction);
        Assert.All(kit.Rows, r => Assert.Equal(OutputResolutionState.Resolved, r.State));
    }

    [Fact]
    public async Task Given_TheirHtlcTimeoutConfirmsFirst_When_Resolved_Then_SecondLevelPenalized()
    {
        // Arrange: our batched penalty is in the mempool when the cheater's HTLC-timeout for a1 confirms
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var batchTxId = kit.Broadcasts.Keys.Single();
        var htlcTimeout = kit.CheaterSecondLevel(HtlcDirection.Incoming, 0);
        var height = RevokedBreachKit.SpentAtHeight + 2;

        // Act
        var onSpent = await kit.ConfirmAsync(htlcTimeout, height);
        var round = await kit.RunAsync(height);

        // Assert: its output is a new row, watched, and penalized with <revsig> 1 before the cheater's CSV
        Assert.Contains(onSpent, a => a is UpsertOutputAction { Output.Descriptor: OutputDescriptorKind.RevokedSecondLevel });
        var secondLevel = Assert.Single(kit.Rows, r => r.Descriptor == OutputDescriptorKind.RevokedSecondLevel);
        Assert.Equal(htlcTimeout.TxId, secondLevel.TransactionId);
        Assert.Contains((secondLevel.TransactionId, 0u), kit.Watches);
        var toSelfDelay = kit.Victim.Channel.ChannelParams.Local.ToSelfDelay;
        Assert.Equal(height + toSelfDelay, secondLevel.DeadlineHeight);
        Assert.Equal(OutputResolutionState.Broadcast, secondLevel.State);

        // B5-REV-09: the batch lost an input, so its other outputs are penalized again, together with the new
        // second-level output (one batch: nothing is within security_delay of its deadline)
        var rebuilt = Assert.Single(round.OfType<BroadcastAction>()).Transaction;
        Assert.Equal(rebuilt.TransactionId, secondLevel.ResolvingTransactionId);
        Assert.NotEqual(batchTxId, rebuilt.TransactionId);
        var rebuiltTx = kit.LoadBroadcast(rebuilt.TransactionId);
        Assert.Equal(kit.RevokedCommitment.Outputs.Count, rebuiltTx.Inputs.Count);
        Assert.Contains(rebuiltTx.Inputs, i => i.PrevOut == new OutPoint(new uint256((byte[])htlcTimeout.TxId), 0));
        kit.AssertVerifies(rebuilt.TransactionId, htlcTimeout);
        Assert.All(kit.Rows.Where(r => r.State != OutputResolutionState.Resolved),
                   r => Assert.Equal(rebuilt.TransactionId, r.ResolvingTransactionId));

        // a1 is the cheater's HTLC: nothing goes upstream
        Assert.Empty(kit.Events);
    }

    [Fact]
    public async Task Given_TheirHtlcSuccessRevealsPreimage_When_Spent_Then_UpstreamFulfilledAtOnce()
    {
        // Arrange: the cheater claims b1 (our offered HTLC) with its HTLC-success
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var htlcSuccess = kit.CheaterSecondLevel(HtlcDirection.Outgoing, 0, s_b1Preimage);

        // Act
        var onSpent = await kit.ConfirmAsync(htlcSuccess, RevokedBreachKit.SpentAtHeight + 2);

        // Assert: B5-REV-07 / B5-REV-RES-01, before any depth
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(
            Assert.Single(onSpent.OfType<RaiseChannelEventAction>()).Event);
        Assert.Equal(0ul, fulfilled.HtlcId);
        Assert.Equal(s_b1Preimage, fulfilled.PaymentPreimage);

        // And the second-level output is penalized; the fulfill is asked again (the switch is idempotent), never a fail
        var round = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 2);
        var secondLevel = Assert.Single(kit.Rows, r => r.Descriptor == OutputDescriptorKind.RevokedSecondLevel);
        kit.AssertVerifies(secondLevel.ResolvingTransactionId!.Value, htlcSuccess);
        Assert.All(round.OfType<RaiseChannelEventAction>(), a => AssertFulfilled(a.Event, 0, s_b1Preimage));

        // Our second-level penalty confirms: still no failure upstream, ever
        var secondPenalty = RevokedBreachKit.ToChainTx(kit.LoadBroadcast(secondLevel.ResolvingTransactionId!.Value));
        await kit.ConfirmAsync(secondPenalty, RevokedBreachKit.SpentAtHeight + 3);
        for (var h = RevokedBreachKit.SpentAtHeight + 3; h < RevokedBreachKit.SpentAtHeight + 20; h++)
            await kit.RunAsync(h);
        Assert.All(kit.Events, e => AssertFulfilled(e, 0, s_b1Preimage));
    }

    [Fact]
    public async Task Given_FulfillLostWithAFailedSave_When_NextRound_Then_FulfillAskedAgain()
    {
        // Arrange: the cheater's HTLC-success reveals b1's preimage, and the executor's save of that block fails (so
        // the switch never got the event)
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var htlcSuccess = kit.CheaterSecondLevel(HtlcDirection.Outgoing, 0, s_b1Preimage);
        await kit.ConfirmAsync(htlcSuccess, RevokedBreachKit.SpentAtHeight + 2);
        kit.Events.Clear();

        // Act
        var round = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 3);

        // Assert: asked again from the chain facts, not suppressed by memory
        AssertFulfilled(Assert.Single(round.OfType<RaiseChannelEventAction>()).Event, 0, s_b1Preimage);
    }

    [Fact]
    public async Task Given_OurOfferedHtlcPenalizedAtDepth_When_Resolved_Then_UpstreamFailedOnce()
    {
        // Arrange: our batched penalty (with b1's output) confirms at h
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var penalty = RevokedBreachKit.ToChainTx(kit.LoadBroadcast(kit.Broadcasts.Keys.Single()));
        var h = RevokedBreachKit.SpentAtHeight + 2;
        await kit.ConfirmAsync(penalty, h);

        // Act / Assert: nothing before reasonable depth (6)
        for (var tip = h; tip < h + OutputResolutionFacts.DefaultReasonableDepth - 1; tip++)
        {
            await kit.RunAsync(tip);
            Assert.Empty(kit.Events);
        }

        // From depth 6 b1 fails upstream (asked every round until the output is irrevocable; the switch is idempotent)
        for (var tip = h + OutputResolutionFacts.DefaultReasonableDepth - 1; tip < h + 30; tip++)
        {
            var round = await kit.RunAsync(tip);
            var failed = Assert.IsType<OutgoingHtlcFailed>(Assert.Single(round.OfType<RaiseChannelEventAction>()).Event);
            Assert.Equal(0ul, failed.HtlcId);
            Assert.Equal(OnchainHtlcRemovals.OnchainTimeoutKind, (byte)failed.Removal.Kind);
        }

        Assert.All(kit.Events, e => Assert.Equal(0ul, Assert.IsType<OutgoingHtlcFailed>(e).HtlcId));
    }

    [Fact]
    public async Task Given_CommittedHtlcWithoutOutputInRevoked_When_Resolved_Then_UpstreamFailedAtReasonableDepth()
    {
        // Arrange: b2 was committed after state k, so the revoked commitment has no output for it
        using var kit = CreateBreach(addB2AfterRevokedState: true);
        var commitmentDepth6 = RevokedBreachKit.SpentAtHeight + OutputResolutionFacts.DefaultReasonableDepth - 1;

        // Act / Assert: B5-REV-RES-03, only once the revoked commitment is 6 deep
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        await kit.RunAsync(commitmentDepth6 - 1);
        Assert.DoesNotContain(kit.Events, e => e is OutgoingHtlcFailed { HtlcId: 1 });

        await kit.RunAsync(commitmentDepth6);
        await kit.RunAsync(commitmentDepth6 + 1);
        Assert.NotEmpty(kit.Events);
        Assert.All(kit.Events, e => Assert.Equal(1ul, Assert.IsType<OutgoingHtlcFailed>(e).HtlcId));
        Assert.DoesNotContain(kit.Rows, r => r.HtlcId == 1 && r.HtlcDirection == HtlcDirection.Outgoing);
    }

    [Fact]
    public async Task Given_BatchedPenaltyUnconfirmedAtDeadlineMinus18_When_Resolved_Then_SplitIntoPerOutputPenalties()
    {
        // Arrange: the batch goes out at once and stays unconfirmed. b1's output expires at 600, to_local at 500+100
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var batch = kit.Broadcasts.Values.Single();
        var batchTx = kit.LoadBroadcast(batch.TransactionId);
        var batchFee = kit.RevokedCommitment.Outputs.Sum(o => o.Value.Satoshi) - batchTx.Outputs[0].Value.Satoshi;
        var securityDelay = new SweepFeePolicyOptions().SecurityDelay;

        // Act / Assert: 19 blocks before the deadline nothing moves
        Assert.Empty(await kit.RunAsync(B1Expiry - securityDelay - 1));

        // 18 blocks before it the batch is split into one penalty per output (to_remote into a plain sweep)
        var split = await kit.RunAsync(B1Expiry - securityDelay);
        var singles = split.OfType<BroadcastAction>().Select(b => b.Transaction).ToList();
        Assert.Equal(batchTx.Inputs.Count, singles.Count);
        Assert.All(singles, s =>
        {
            Assert.Equal(batch.TransactionId, s.ReplacesTransactionId);
            Assert.Single(kit.LoadBroadcast(s.TransactionId).Inputs);
            kit.AssertVerifies(s.TransactionId);
        });
        Assert.Equal(1, singles.Count(s => s.Purpose == BroadcastPurpose.Sweep));

        // The most urgent penalty outbids the batch (BIP 125: more than its fee plus the relay fee of its own size)
        var urgentFee = singles.Select(s => kit.LoadBroadcast(s.TransactionId))
                               .Max(t => kit.SpentOutput(t.Inputs[0].PrevOut).Value.Satoshi - t.Outputs[0].Value.Satoshi);
        Assert.True(urgentFee > batchFee, $"{urgentFee} <= {batchFee}");

        // Every row points at its own transaction now, and the split is not repeated
        Assert.Equal(singles.Count, kit.Rows.Select(r => r.ResolvingTransactionId).Distinct().Count());
        Assert.Empty(await kit.RunAsync(B1Expiry - securityDelay + 1));
    }

    [Fact]
    public async Task Given_UrgentOutputWithoutTransaction_When_Resolved_Then_ItGetsItsOwnPenalty()
    {
        // Arrange: we see the breach late: b1's output and to_local are within security_delay of their deadlines
        using var kit = CreateBreach();

        // Act
        var actions = await kit.RunAsync(B1Expiry - 10);

        // Assert: b1 (expires at 600) and to_local (CSV ends at 500 + 100) alone, a1 and to_remote batched
        var broadcasts = actions.OfType<BroadcastAction>().Select(b => kit.LoadBroadcast(b.Transaction.TransactionId))
                                .ToList();
        Assert.Equal(3, broadcasts.Count);
        Assert.Equal(2, broadcasts.Count(t => t.Inputs.Count == 1));
        Assert.Contains(broadcasts, t => t.Inputs.Count == kit.RevokedCommitment.Outputs.Count - 2);
        foreach (var tx in broadcasts)
            kit.AssertVerifies(tx.GetHash().ToBytes());
    }

    [Fact]
    public async Task Given_RevokedCommitmentWithoutHtlcs_When_NoLogEntry_Then_ToLocalAndToRemoteFoundByScript()
    {
        // Arrange: commitment 0 has no HTLC, so the log has no entry for it
        using var kit = new RevokedBreachKit();
        kit.CaptureRevokedState();
        kit.Pair.UpdateFee(3_000);
        kit.Pair.Settle(kit.Pair.Alice);
        kit.Breach(useLog: false);

        // Act
        var actions = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);

        // Assert
        var broadcast = Assert.Single(actions.OfType<BroadcastAction>()).Transaction;
        Assert.Equal(2, kit.LoadBroadcast(broadcast.TransactionId).Inputs.Count);
        kit.AssertVerifies(broadcast.TransactionId);
        Assert.Empty(kit.Alerts);
    }

    [Fact]
    public async Task Given_CommitmentRevokedBeforeTheLog_When_Resolved_Then_HtlcOutputsReportedAndToLocalPenalized()
    {
        // Arrange: a breach of a state with HTLCs whose log entry was never written (pre-O1, plan §8 risk 5)
        using var kit = new RevokedBreachKit();
        var pair = kit.Pair;
        pair.Add(pair.Bob, B1Msat, s_b1Preimage, B1Expiry);
        pair.Settle(pair.Bob);
        kit.CaptureRevokedState();
        pair.UpdateFee(3_000);
        pair.Settle(pair.Alice);
        kit.Breach(useLog: false, logStart: kit.RevokedNumber + 1);

        // Act
        var actions = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);

        // Assert
        var alert = Assert.Single(actions.OfType<AlertAction>());
        Assert.Contains("pre-O1", alert.Message);
        var broadcast = Assert.Single(actions.OfType<BroadcastAction>()).Transaction;
        Assert.Equal(2, kit.LoadBroadcast(broadcast.TransactionId).Inputs.Count);
        kit.AssertVerifies(broadcast.TransactionId);
    }

    /// <summary>
    /// A breach whose HTLC outputs cannot be rebuilt, with b1 (our offered HTLC) still pending: before the log
    /// (<paramref name="predatesLog"/>), or inside it with the entry missing. b1's output is unmapped.
    /// </summary>
    private static RevokedBreachKit CreateUnmappedHtlcBreach(bool predatesLog)
    {
        var kit = new RevokedBreachKit();
        var pair = kit.Pair;
        pair.Add(pair.Bob, B1Msat, s_b1Preimage, B1Expiry);
        pair.Settle(pair.Bob);
        kit.CaptureRevokedState();
        pair.UpdateFee(3_000);
        pair.Settle(pair.Alice);
        kit.Breach(useLog: false, logStart: predatesLog ? kit.RevokedNumber + 1 : 0);
        return kit;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_UnmappedOutputOfPendingOfferedHtlc_When_ReasonablyDeep_Then_NoUpstreamFailure(
        bool predatesLog)
    {
        // Arrange
        using var kit = CreateUnmappedHtlcBreach(predatesLog);
        var outputCount = kit.RevokedChainTx.Outputs.Count;

        // Act: well past reasonable depth, nothing spent the unmapped output
        for (var h = RevokedBreachKit.SpentAtHeight + 1; h < RevokedBreachKit.SpentAtHeight + 20; h++)
            await kit.RunAsync(h);

        // Assert: b1 may still be claimed by the cheater with its preimage: never failed upstream (BOLT 5 limits the
        // failure to an HTLC without an output); the unmapped output is watched and reported
        Assert.DoesNotContain(kit.Events, e => e is OutgoingHtlcFailed);
        var unmapped = Enumerable.Range(0, outputCount).Select(v => (uint)v)
                                 .Where(v => kit.Rows.All(r => r.OutputIndex != v)).ToList();
        var vout = Assert.Single(unmapped);
        Assert.Contains((kit.RevokedChainTx.TxId, vout), kit.Watches);
        Assert.Contains(kit.Alerts, a => a.RequirementId == "B5-REV-04" && a.Message.Contains($"outputs {vout}"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_UnmappedOfferedHtlcClaimedWithPreimage_When_Resolved_Then_UpstreamFulfilledNotFailed(
        bool predatesLog)
    {
        // Arrange: the cheater sweeps b1's (unmapped) output with its HTLC-success
        using var kit = CreateUnmappedHtlcBreach(predatesLog);
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var htlcSuccess = kit.CheaterSecondLevel(HtlcDirection.Outgoing, 0, s_b1Preimage);
        var h = RevokedBreachKit.SpentAtHeight + 3;
        await kit.ConfirmAsync(htlcSuccess, h);

        // Act
        var round = await kit.RunAsync(h);
        for (var tip = h + 1; tip < h + 20; tip++)
            await kit.RunAsync(tip);

        // Assert: fulfilled at once (B5-REV-RES-01), and never failed
        AssertFulfilled(Assert.Single(round.OfType<RaiseChannelEventAction>()).Event, 0, s_b1Preimage);
        Assert.All(kit.Events, e => AssertFulfilled(e, 0, s_b1Preimage));
    }

    [Fact]
    public async Task Given_UnmappedOutputSpentWithoutPreimage_When_ReasonablyDeep_Then_UpstreamFailed()
    {
        // Arrange: b1's unmapped output is spent by a transaction that reveals no preimage
        using var kit = CreateUnmappedHtlcBreach(predatesLog: true);
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var spend = kit.CheaterSecondLevel(HtlcDirection.Outgoing, 0);
        var h = RevokedBreachKit.SpentAtHeight + 3;
        await kit.ConfirmAsync(spend, h);

        // Act / Assert: nothing before that spend is 6 deep, then b1 fails upstream
        for (var tip = h; tip < h + OutputResolutionFacts.DefaultReasonableDepth - 1; tip++)
        {
            await kit.RunAsync(tip);
            Assert.Empty(kit.Events);
        }

        await kit.RunAsync(h + OutputResolutionFacts.DefaultReasonableDepth - 1);
        Assert.Equal(0ul, Assert.IsType<OutgoingHtlcFailed>(Assert.Single(kit.Events)).HtlcId);
    }

    [Fact]
    public async Task Given_SpenderUnreadableAfterRestart_When_Resolved_Then_NoGuessAndSecondLevelStillPenalized()
    {
        // Arrange: the cheater's HTLC-success took b1 (our offered HTLC) with the preimage; the node restarts and the
        // spending transaction can no longer be fetched (pruned block, RPC error)
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var batchTxId = kit.Broadcasts.Keys.Single();
        var htlcSuccess = kit.CheaterSecondLevel(HtlcDirection.Outgoing, 0, s_b1Preimage);
        await kit.ConfirmAsync(htlcSuccess, RevokedBreachKit.SpentAtHeight + 2);
        kit.RestartResolver();
        kit.DataSource.FetchFails = true;
        kit.Events.Clear();

        // Act
        var round = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 2);
        for (var h = RevokedBreachKit.SpentAtHeight + 3; h < RevokedBreachKit.SpentAtHeight + 30; h++)
            await kit.RunAsync(h);

        // Assert: b1 is not taken for our own penalty, so nothing fails upstream; the operator is told
        Assert.DoesNotContain(kit.Events, e => e is OutgoingHtlcFailed);
        Assert.Single(kit.Alerts, a => a.RequirementId == "B5-GEN-06");

        // The batch lost b1 to another transaction (its txid is known): the rest and the recorded second-level output
        // are penalized again, and every input is valid
        var rebuilt = Assert.Single(round.OfType<BroadcastAction>()).Transaction;
        Assert.NotEqual(batchTxId, rebuilt.TransactionId);
        var rebuiltTx = kit.LoadBroadcast(rebuilt.TransactionId);
        Assert.Contains(rebuiltTx.Inputs, i => i.PrevOut == new OutPoint(new uint256((byte[])htlcSuccess.TxId), 0));
        kit.AssertVerifies(rebuilt.TransactionId, htlcSuccess);
    }

    [Fact]
    public async Task Given_TheirSpendReorgedOut_When_Resolved_Then_CachedSpendIgnored()
    {
        // Arrange: the cheater's HTLC-timeout for a1 is seen in a block that is then disconnected: the monitor drops
        // the spend and the executor puts the row back
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var batchTxId = kit.Broadcasts.Keys.Single();
        var htlcTimeout = kit.CheaterSecondLevel(HtlcDirection.Incoming, 0);
        await kit.ConfirmAsync(htlcTimeout, RevokedBreachKit.SpentAtHeight + 2);
        var a1 = kit.Rows.FindIndex(r => r is { Descriptor: OutputDescriptorKind.RevokedHtlc, HtlcDirection: HtlcDirection.Incoming });
        kit.Rows[a1] = kit.Rows[a1] with { State = OutputResolutionState.Broadcast, ResolvedHeight = null };
        foreach (var input in htlcTimeout.Inputs)
            kit.Spends.Remove((input.PreviousTxId, input.PreviousVout));

        // Act
        var round = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 2);

        // Assert: the batch is valid again, so it is neither rebuilt nor replaced
        Assert.DoesNotContain(round, a => a is BroadcastAction);
        Assert.All(kit.Rows.Where(r => r.Descriptor != OutputDescriptorKind.RevokedSecondLevel),
                   r => Assert.Equal(batchTxId, r.ResolvingTransactionId));
    }

    [Fact]
    public async Task Given_BatchFeeUnknown_When_DeadlineMinus18_Then_BatchKeptWithAlert()
    {
        // Arrange: the batch cannot be read back, so no replacement can be priced against it
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var batchTxId = kit.Broadcasts.Keys.Single();
        kit.DataSource.BroadcastsHidden = true;

        // Act
        var round = await kit.RunAsync(B1Expiry - new SweepFeePolicyOptions().SecurityDelay);

        // Assert: nothing split (every single would be refused and the rows would point at them)
        Assert.DoesNotContain(round, a => a is BroadcastAction or UpsertOutputAction);
        Assert.Contains(round, a => a is AlertAction { RequirementId: "B5-REV-08" });
        Assert.All(kit.Rows, r => Assert.Equal(batchTxId, r.ResolvingTransactionId));
    }

    private static void AssertFulfilled(IChannelDomainEvent channelEvent, ulong htlcId, Secret preimage)
    {
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(channelEvent);
        Assert.Equal(htlcId, fulfilled.HtlcId);
        Assert.Equal(preimage, fulfilled.PaymentPreimage);
    }

    [Fact]
    public async Task Given_NoContext_When_Resolved_Then_AlertAndNothingElse()
    {
        // Arrange
        using var kit = CreateBreach();
        kit.DataSource.Context = null;

        // Act
        var actions = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);

        // Assert
        Assert.IsType<AlertAction>(Assert.Single(actions));
    }

    [Fact]
    public async Task Given_FeeSpikeNearTheDeadline_When_Resolved_Then_EveryOutputIsBroadcastOrAbandonedWithAlert()
    {
        // Arrange: a small revoked HTLC (5,000 sat) and an estimate of 100,000 sat/kw close to the deadlines
        using var kit = new RevokedBreachKit();
        var pair = kit.Pair;
        pair.Add(pair.Bob, 5_000_000, s_b1Preimage, B1Expiry);
        pair.Settle(pair.Bob);
        kit.CaptureRevokedState();
        pair.UpdateFee(3_000);
        pair.Settle(pair.Alice);
        kit.Breach();
        kit.DataSource.Feerate = 100_000;

        // Act
        await kit.RunAsync(B1Expiry - 5);

        // Assert: every output still gets a transaction or is abandoned with an alert, never silently dropped
        Assert.All(kit.Rows, r => Assert.True(r.State is OutputResolutionState.Broadcast or OutputResolutionState.Ignored,
                                              $"{r.Descriptor} {r.State}"));
        Assert.Equal(kit.Rows.Count(r => r.State == OutputResolutionState.Ignored), kit.Alerts.Count);

        // The small HTLC output is not given up: its penalty pays everything above dust rather than leave it to the
        // cheater (the policy may spend the whole value this close to the deadline)
        var b1 = Assert.Single(kit.Rows, r => r is { Descriptor: OutputDescriptorKind.RevokedHtlc, HtlcId: 0 });
        Assert.Equal(OutputResolutionState.Broadcast, b1.State);
        var penalty = kit.LoadBroadcast(b1.ResolvingTransactionId!.Value);
        Assert.Single(penalty.Inputs);
        kit.AssertVerifies(b1.ResolvingTransactionId!.Value);
    }

    [Fact]
    public async Task Given_CheaterSweptItsToLocal_When_ManyBlocksFollow_Then_LossAlertedOnce()
    {
        // Arrange: we were too late: the cheater spent its to_local after the CSV
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var toLocal = Assert.Single(kit.Rows, r => r.Descriptor == OutputDescriptorKind.RevokedToLocal);
        var theirSweep = new ChainTx(new TxId(Enumerable.Repeat((byte)0xCC, 32).ToArray()), 2, 0,
                                     [new ChainTxInput(toLocal.TransactionId, toLocal.OutputIndex, 100, [[1], [2]])],
                                     [new ChainTxOutput(1_000, RevokedBreachKit.Destination)]);
        await kit.ConfirmAsync(theirSweep, RevokedBreachKit.SpentAtHeight + 101);

        // Act
        for (var h = RevokedBreachKit.SpentAtHeight + 101; h < RevokedBreachKit.SpentAtHeight + 111; h++)
            await kit.RunAsync(h);

        // Assert
        var alert = Assert.Single(kit.Alerts, a => a.RequirementId == "B5-REV-03");
        Assert.Contains($"Output {toLocal.OutputIndex}", alert.Message);
    }

    [Fact]
    public void Given_Kinds_When_CanResolve_Then_OnlyRevokedCommitment()
    {
        // Arrange
        using var kit = new RevokedBreachKit();

        // Act / Assert
        foreach (var kind in Enum.GetValues<ChannelCloseKind>())
            Assert.Equal(kind == ChannelCloseKind.RevokedCommitment, kit.Resolver.CanResolve(kind));
    }

    [Fact]
    public async Task Given_OurOwnPenaltySpendsAnOutput_When_OnOutputSpent_Then_NoAction()
    {
        // Arrange
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var penalty = RevokedBreachKit.ToChainTx(kit.LoadBroadcast(kit.Broadcasts.Keys.Single()));

        // Act
        var actions = await kit.ConfirmAsync(penalty, RevokedBreachKit.SpentAtHeight + 2);

        // Assert
        Assert.Empty(actions);
        Assert.DoesNotContain(kit.Rows, r => r.Descriptor == OutputDescriptorKind.RevokedSecondLevel);
    }

    [Fact]
    public async Task Given_RevokedHtlcOutputRow_When_TxIdIsKnown_Then_TxIdsAreConsistent()
    {
        // Arrange / Act
        using var kit = CreateBreach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);

        // Assert: the rows are keyed by the commitment on chain
        Assert.All(kit.Rows, r => Assert.Equal(kit.RevokedChainTx.TxId, r.TransactionId));
        Assert.Equal((TxId)kit.RevokedCommitment.GetHash().ToBytes(), kit.RevokedChainTx.TxId);
    }
}