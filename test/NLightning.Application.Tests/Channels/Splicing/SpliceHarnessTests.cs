namespace NLightning.Application.Tests.Channels.Splicing;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Quiescence;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.ValueObjects;
using Harness;
using NBitcoin;

/// <summary>
/// Splicing plan SP1-D-T3: two nodes splice over the production splice, quiescence and interactive-tx services
/// (<see cref="SpliceHarness"/>, every message through the receiver's <c>ChannelManager</c>, the real Appendix G
/// transaction builder). The fundings and the splice commitment signatures are the stand-ins of lanes SP1-B and SP1-C
/// (<see cref="FakeSpliceStatePort"/>, <see cref="SpliceSigningProxy"/>): what needs their real code is listed on each
/// test.
/// </summary>
public class SpliceHarnessTests
{
    private const uint CltvExpiry = 700;
    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    private static bool IsSpliceFlowMessage(IChannelMessage message) =>
        message.Type is MessageTypes.Stfu or MessageTypes.SpliceInit or MessageTypes.SpliceAck
                     or MessageTypes.CommitmentSigned or MessageTypes.RevokeAndAck or MessageTypes.TxSignatures
                     or MessageTypes.SpliceLocked or MessageTypes.TxComplete or MessageTypes.StartBatch;

    #region SP-T-01 successful single splice (bolt02/splicing-test.md)

    /// <summary>
    /// SP-T-01, "Successful single splice": stfu, stfu, splice_init, splice_ack, the interactive-tx construction ending
    /// with two consecutive tx_complete, commit_sig both ways, tx_signatures both ways (the channel no longer quiescent),
    /// updates while the splice is pending, splice_locked both ways at depth, and the channel on the new funding.
    /// </summary>
    /// <remarks>
    /// The test file omits the interactive-tx messages and draws one order of the last two tx_complete and
    /// tx_signatures; the order is the protocol's: Alice adds the shared input, her wallet input, the funding output and
    /// her change (Bob answers tx_complete to each), so her tx_complete is the second consecutive one; Bob sends
    /// tx_signatures first because the shared input counts for the initiator (IT-SIG-01, SP-CS-02). Batched
    /// commitment_signed while pending (start_batch, SP-OP-03) needs lanes SP1-A/SP1-B: until then the updates go over
    /// the single current funding.
    /// </remarks>
    [Fact]
    public async Task Given_AliceSplicesIn_When_TheSpliceConfirms_Then_TheMessagesFollowSpT01AndTheFundingIsLocked()
    {
        // Arrange
        using var harness = new SpliceHarness();
        harness.Alice.Fund(500_000);
        var localNumber = harness.Alice.Node.State.LocalCommit.Number;
        var remoteNumber = harness.Alice.Node.State.RemoteCommit.Number;

        // Act: the splice
        var result = await harness.SpliceAsync(harness.Alice, 100_000);

        // Assert: signed, pending on both sides, the channel no longer quiescent (SP-Q-01)
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        Assert.Empty(harness.Failures);
        Assert.Equal(TwoNodeHarness.FundingSatoshis + 100_000, result.NewCapacitySatoshis);
        var spliceTxId = result.SpliceTxId!.Value;
        Assert.Equal(
        [
            "Alice:Stfu", "Bob:Stfu", "Alice:SpliceInit", "Bob:SpliceAck", "Bob:TxComplete", "Bob:TxComplete",
            "Bob:TxComplete", "Bob:TxComplete", "Alice:TxComplete", "Alice:CommitmentSigned", "Bob:CommitmentSigned",
            "Bob:TxSignatures", "Alice:TxSignatures"
        ], harness.Sequence(IsSpliceFlowMessage));
        AssertPending(harness, spliceTxId, 100_000_000, 0);

        // SP-CS-01/02: the splice commitments at the current numbers, no revoke_and_ack, the numbers unchanged
        Assert.Equal([(spliceTxId, remoteNumber)], harness.Alice.Port.Signed);
        Assert.Equal([(spliceTxId, localNumber)], harness.Alice.Port.Verified);
        Assert.Equal(localNumber, harness.Alice.Node.State.LocalCommit.Number);
        Assert.Equal(remoteNumber, harness.Alice.Node.State.RemoteCommit.Number);

        // Act: updates while the splice is pending, both ways
        await PayAsync(harness, harness.Alice, harness.Bob, 50_000_000, 1);
        await PayAsync(harness, harness.Bob, harness.Alice, 20_000_000, 2);

        // Assert: still pending (the channel works on the current funding)
        Assert.Single(harness.Alice.Port.GetFundings(harness.Alice.Node.Channel).Pending);

        // Act: the splice reaches acceptable depth on both sides
        await harness.ConfirmAsync(spliceTxId, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);

        // Assert: splice_locked both ways (SP-LK-01), then the new funding is the current one on both (SP-LK-03)
        Assert.Equal(["Alice:SpliceLocked", "Bob:SpliceLocked"],
                     harness.Sequence(m => m.Type == MessageTypes.SpliceLocked));
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var saved = node.Port.Saved!;
            Assert.Equal(spliceTxId, saved.Current.FundingTxId);
            Assert.Equal(ChannelFundingStatus.Current, saved.Current.Status);
            Assert.Empty(saved.Pending);
            var replaced = Assert.Single(node.Port.Retired);
            Assert.Equal(ChannelFundingStatus.Replaced, replaced.Status);
            Assert.Equal(new TxId(Enumerable.Repeat((byte)0x77, 32).ToArray()), replaced.FundingTxId);
        }

        // Act / Assert: the channel keeps working after the lock
        await PayAsync(harness, harness.Alice, harness.Bob, 10_000_000, 3);
        Assert.Empty(harness.Failures);
    }

    #endregion

    #region Splice-in and splice-out, each side initiating

    [Fact]
    public async Task Given_BobTheNonFunderSplicesIn_When_Negotiated_Then_AliceAcceptsWithContributionZero()
    {
        // Arrange
        using var harness = new SpliceHarness();
        harness.Bob.Fund(300_000);

        // Act
        var result = await harness.SpliceAsync(harness.Bob, 50_000);

        // Assert
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        Assert.Equal(TwoNodeHarness.FundingSatoshis + 50_000, result.NewCapacitySatoshis);
        Assert.Equal(["Bob:Stfu", "Alice:Stfu", "Bob:SpliceInit", "Alice:SpliceAck"],
                     harness.Sequence(IsSpliceFlowMessage).Take(4));
        var ack = Assert.IsType<SpliceAckMessage>(
            harness.Transcript.Single(t => t.Message is SpliceAckMessage).Message);
        Assert.Equal(0, ack.Payload.FundingContributionSatoshis);
        AssertPending(harness, result.SpliceTxId!.Value, 0, 50_000_000);
        Assert.Empty(harness.Alice.Contributor.ActiveReservations);
    }

    [Fact]
    public async Task Given_AliceSplicesOut_When_Negotiated_Then_TheAmountGoesToHerDestinationAndHerBalancePaysTheFee()
    {
        // Arrange
        using var harness = new SpliceHarness();

        // Act
        var result = await harness.SpliceAsync(harness.Alice, -200_000);

        // Assert: D16: the contribution is the amount plus the initiator's fee, paid from her channel balance
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        var init = Assert.IsType<SpliceInitMessage>(harness.Transcript.Single(t => t.Message is SpliceInitMessage)
                                                           .Message);
        var contribution = init.Payload.FundingContributionSatoshis;
        Assert.True(contribution < -200_000, $"contribution {contribution}");
        var fee = -contribution - 200_000;
        Assert.InRange(fee, 1, 2_000);
        Assert.Equal((ulong)((long)TwoNodeHarness.FundingSatoshis + contribution), result.NewCapacitySatoshis);

        var transaction = Transaction.Parse(Convert.ToHexString(harness.Alice.Broadcasts.Single().RawTransaction),
                                            Network.RegTest);
        Assert.Single(transaction.Inputs);
        Assert.Contains(transaction.Outputs, o => o.Value.Satoshi == 200_000
                                               && o.ScriptPubKey.ToBytes().SequenceEqual(
                                                      (byte[])harness.Alice.Destination.Script));
        Assert.Equal(fee, (long)TwoNodeHarness.FundingSatoshis - transaction.Outputs.Sum(o => o.Value.Satoshi));
        AssertPending(harness, result.SpliceTxId!.Value, contribution * 1_000, 0);
    }

    [Fact]
    public async Task Given_BobSplicesOut_When_Negotiated_Then_TheSpliceIsSignedWithHisContribution()
    {
        // Arrange
        using var harness = new SpliceHarness();

        // Act
        var result = await harness.SpliceAsync(harness.Bob, -100_000);

        // Assert
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        var init = Assert.IsType<SpliceInitMessage>(harness.Transcript.Single(t => t.Message is SpliceInitMessage)
                                                           .Message);
        AssertPending(harness, result.SpliceTxId!.Value, 0, init.Payload.FundingContributionSatoshis * 1_000);
        var transaction = Transaction.Parse(Convert.ToHexString(harness.Bob.Broadcasts.Single().RawTransaction),
                                            Network.RegTest);
        Assert.Contains(transaction.Outputs, o => o.Value.Satoshi == 100_000);
    }

    #endregion

    #region SP-T-02 concurrent splice_locked

    /// <summary>
    /// SP-T-02's lock part, "nodes may send splice_locked at slightly different times": both sides reach the depth
    /// before either splice_locked is delivered; the two cross and each side locks once. The RBF attempt of SP-T-02
    /// (tx_init_rbf on the pending splice, three active commitments, the obsolete commit_sig ignored in a batch) needs
    /// waves SPR and lanes SP1-A/SP1-B.
    /// </summary>
    [Fact]
    public async Task Given_BothSidesReachTheDepthAtOnce_When_SpliceLockedCross_Then_EachSideLocksOnce()
    {
        // Arrange
        using var harness = new SpliceHarness();
        harness.Alice.Fund(500_000);
        var result = await harness.SpliceAsync(harness.Alice, 100_000);
        var spliceTxId = result.SpliceTxId!.Value;

        // Act: both monitors report the depth before anything is delivered
        harness.Alice.Confirm(spliceTxId, TwoNodeHarness.BlockHeight + 3);
        harness.Bob.Confirm(spliceTxId, TwoNodeHarness.BlockHeight + 4);
        await harness.Alice.DepthWatcher.WhenIdleAsync();
        await harness.Bob.DepthWatcher.WhenIdleAsync();
        await harness.PumpAsync();

        // Assert
        Assert.Equal(2, harness.Transcript.Count(t => t.Message is SpliceLockedMessage));
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Equal(spliceTxId, node.Port.Saved!.Current.FundingTxId);
            Assert.Single(node.Port.Retired);
        }

        Assert.Empty(harness.Failures);
    }

    #endregion

    #region Rejections

    [Fact]
    public async Task Given_AFeerateBobDoesNotAccept_When_AliceSplices_Then_BobAnswersTxAbortAndBothAreNoLongerQuiescent()
    {
        // Arrange (SP-R-01: "If the funding_feerate_perkw is unacceptable: MUST respond with tx_abort")
        using var harness = new SpliceHarness((name, options) =>
        {
            if (name == "Bob")
                options.MinFeeratePerKw = SpliceHarness.FeeratePerKw + 1;
        });
        harness.Alice.Fund(500_000);

        // Act
        var result = await harness.SpliceAsync(harness.Alice, 100_000);

        // Assert
        Assert.Equal(SpliceNegotiationState.Aborted, result.State);
        Assert.Contains(harness.Transcript, t => t is { From: "Bob", Message: TxAbortMessage });
        Assert.DoesNotContain(harness.Transcript, t => t.Message is SpliceAckMessage);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.False(node.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
            Assert.Null(node.Service.GetNegotiation(TwoNodeHarness.ChannelId));
        }

        // Alice's wallet reservation is released
        Assert.Single(harness.Alice.Contributor.Released);
        Assert.Empty(harness.Alice.Contributor.ActiveReservations);

        // The channel carries updates again
        await PayAsync(harness, harness.Alice, harness.Bob, 1_000_000, 4);
    }

    [Fact]
    public async Task Given_AnInvalidSpliceCommitmentSigned_When_Received_Then_TheNegotiationEndsWithTxAbort()
    {
        // Arrange (SP-CS-02: a commitment_signed for the new funding that does not verify, before our tx_signatures;
        // Bob signs first, so Alice's bad commitment_signed reaches him before he sends anything irrevocable)
        using var harness = new SpliceHarness();
        harness.Alice.Fund(500_000);
        harness.Alice.Port.CorruptNextSignature = true;

        // Act
        var result = await harness.SpliceAsync(harness.Alice, 100_000);

        // Assert
        Assert.Equal(SpliceNegotiationState.Aborted, result.State);
        Assert.Contains(harness.Transcript, t => t is { From: "Bob", Message: TxAbortMessage });
        Assert.Empty(harness.Bob.Port.Verified);
        Assert.DoesNotContain(harness.Transcript, t => t.Message is TxSignaturesMessage);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Broadcasts);
            Assert.Empty(node.SharedInputSignatures);
            Assert.False(node.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
        }

        Assert.Empty(harness.Alice.Contributor.ActiveReservations);
    }

    /// <summary>
    /// A <c>tx_abort</c> after the commitment step and before our <c>tx_signatures</c> (BOLT 2 interactive-tx: the
    /// negotiation is forgotten): the peer's splice commitment had already made the new funding pending (SP-CS-02, lane
    /// SP1-B's engine), so it is discarded again and no later <c>commitment_signed</c> batch covers it.
    /// </summary>
    [Fact]
    public async Task Given_ATxAbortAfterTheSpliceCommitments_When_Received_Then_ThePendingFundingIsDiscarded()
    {
        // Arrange: Alice splices in; stop when Bob's tx_signatures is next and Alice verified Bob's commitment_signed
        using var harness = new SpliceHarness();
        harness.Alice.Fund(500_000);
        var start = harness.Alice.Service.StartAsync(
            new SpliceRequest(TwoNodeHarness.ChannelId, 100_000, SpliceHarness.FeeratePerKw),
            TestContext.Current.CancellationToken);
        for (var round = 0; round < 2_000 && harness.Bob.Node.PeekNext() is not TxSignaturesMessage; round++)
        {
            await harness.WhenIdleAsync();
            if (!await harness.Alice.Node.DeliverNextAsync() && !await harness.Bob.Node.DeliverNextAsync())
                await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        var spliceTxId = Assert.Single(harness.Alice.Port.GetFundings(harness.Alice.Node.Channel).Pending).FundingTxId;
        Assert.True(harness.Bob.Node.TryTakeNext(out _));

        // Act: Bob's tx_abort reaches Alice instead of his tx_signatures
        var abort = new TxAbortMessage(new Domain.Protocol.Payloads.TxAbortPayload(TwoNodeHarness.ChannelId, [0x01]));
        await harness.Alice.Node.ChannelManager.HandleChannelMessageAsync(abort, SpliceHarness.CreateFeatures(),
                                                                          SpliceHarness.NodeIdOf("Bob"));
        var result = await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await harness.WhenIdleAsync();

        // Assert
        Assert.Equal(SpliceNegotiationState.Aborted, result.State);
        Assert.Empty(harness.Alice.Port.GetFundings(harness.Alice.Node.Channel).Pending);
        var discarded = Assert.Single(harness.Alice.Port.Retired);
        Assert.Equal(spliceTxId, discarded.FundingTxId);
        Assert.Equal(ChannelFundingStatus.Discarded, discarded.Status);
        Assert.Empty(harness.Alice.Port.Saved!.Pending);
        Assert.DoesNotContain(harness.Transcript, t => t is { From: "Alice", Message: TxSignaturesMessage });
        Assert.Empty(harness.Alice.Broadcasts);
        Assert.False(harness.Alice.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
    }

    [Fact]
    public async Task Given_AnInvalidSharedInputSignature_When_Received_Then_TheChannelFails()
    {
        // Arrange (SP-SIG-01: "If shared_input_signature is not valid ...: MUST send an error and fail the channel")
        using var harness = new SpliceHarness();
        harness.Alice.Fund(500_000);
        harness.Alice.RejectSharedInputSignature = true;
        var start = harness.Alice.Service.StartAsync(
            new SpliceRequest(TwoNodeHarness.ChannelId, 100_000, SpliceHarness.FeeratePerKw),
            TestContext.Current.CancellationToken);

        // Act
        for (var round = 0; round < 1_000 && !harness.Failures.Any(f => f.Exception is ChannelFailedException); round++)
        {
            await harness.PumpAsync();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        // Assert: Bob signs first; his signature fails at Alice, whose tx_signatures never goes out
        var failure = Assert.IsType<ChannelFailedException>(
            Assert.Single(harness.Failures, f => f.Exception is ChannelFailedException).Exception);
        Assert.Equal("SP-SIG-01", failure.RequirementId);
        Assert.True(failure.MustBroadcast);
        Assert.DoesNotContain(harness.Transcript, t => t is { From: "Alice", Message: TxSignaturesMessage });
        Assert.Empty(harness.Alice.Broadcasts);
        Assert.False(start.IsCompletedSuccessfully && (await start).State == SpliceNegotiationState.Signed);
    }

    [Fact]
    public async Task Given_ASpliceLockedForAnUnknownTransaction_When_Received_Then_WarningAndClose()
    {
        // Arrange (SP-LK-02)
        using var harness = new SpliceHarness();
        var message = new SpliceLockedMessage(
            new Domain.Protocol.Payloads.SpliceLockedPayload(TwoNodeHarness.ChannelId,
                                                             new TxId(Enumerable.Repeat((byte)0x99, 32).ToArray())));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
            () => harness.Bob.Node.ChannelManager.HandleChannelMessageAsync(message, SpliceHarness.CreateFeatures(),
                                                                             harness.Alice.Node.NodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("SP-LK-02", exception.Message);
    }

    [Fact]
    public async Task Given_ASpliceAckWithoutOurSpliceInit_When_Received_Then_WarningAndClose()
    {
        // Arrange (SP-R-02: "Otherwise (it has not sent splice_init): MUST send a warning and close the connection")
        using var harness = new SpliceHarness();
        var message = new SpliceAckMessage(
            new Domain.Protocol.Payloads.SpliceAckPayload(TwoNodeHarness.ChannelId, 0, harness.Alice.Node.NodeId));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
            () => harness.Bob.Node.ChannelManager.HandleChannelMessageAsync(message, SpliceHarness.CreateFeatures(),
                                                                             harness.Alice.Node.NodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("SP-R-02", exception.Message);
    }

    [Fact]
    public async Task Given_ASpliceInitOnAChannelThatIsNotQuiescent_When_Received_Then_WarningAndClose()
    {
        // Arrange (SP-R-01: "If the channel is not quiescent: MUST send a warning and close the connection")
        using var harness = new SpliceHarness();
        var message = new SpliceInitMessage(
            new Domain.Protocol.Payloads.SpliceInitPayload(TwoNodeHarness.ChannelId, 100_000,
                                                           SpliceHarness.FeeratePerKw, 0, harness.Alice.Node.NodeId));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
            () => harness.Bob.Node.ChannelManager.HandleChannelMessageAsync(message, SpliceHarness.CreateFeatures(),
                                                                             harness.Alice.Node.NodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("SP-R-01", exception.Message);
        Assert.Null(harness.Bob.Service.GetNegotiation(TwoNodeHarness.ChannelId));
    }

    [Fact]
    public async Task Given_ASpliceWhileAnotherIsUnlocked_When_Started_Then_RefusedBySpS01()
    {
        // Arrange
        using var harness = new SpliceHarness();
        harness.Alice.Fund(500_000);
        await harness.SpliceAsync(harness.Alice, 100_000);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.Service.StartAsync(
                new SpliceRequest(TwoNodeHarness.ChannelId, -10_000, SpliceHarness.FeeratePerKw),
                TestContext.Current.CancellationToken));

        // Assert: "MUST NOT send splice_init if another splice has been negotiated but splice_locked has not been
        // sent and received"
        Assert.Contains("SP-S-01", exception.Message);
        Assert.False(harness.Alice.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
    }

    #endregion

    #region NL-470 quiescence seams

    /// <summary>
    /// NL-470 (1): the disconnection reaches the quiescence service through <c>ChannelManager.OnPeerDisconnectedAsync</c>
    /// (Q-R-04), so a splice waiting for <c>splice_ack</c> ends and the next connection can splice again. Without a
    /// peer manager (this harness) nothing else would end the quiescence.
    /// </summary>
    [Fact]
    public async Task Given_ADisconnectionAfterSpliceInit_When_Reconnected_Then_TheSpliceEndedAndANewOneWorks()
    {
        // Arrange
        using var harness = new SpliceHarness();
        harness.Alice.Fund(500_000);
        harness.Alice.Fund(500_000);
        var start = harness.Alice.Service.StartAsync(
            new SpliceRequest(TwoNodeHarness.ChannelId, 100_000, SpliceHarness.FeeratePerKw),
            TestContext.Current.CancellationToken);
        for (var round = 0; round < 500 && !harness.Transcript.Any(t => t.Message is SpliceInitMessage); round++)
        {
            await harness.Alice.Node.DeliverNextAsync();
            await harness.Bob.Node.DeliverNextAsync();
            await harness.WhenIdleAsync();
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        // Act: the link drops with splice_init in flight
        await harness.Harness.DisconnectAsync();
        var result = await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(SpliceNegotiationState.Aborted, result.State);
        Assert.Equal(QuiescenceState.None, harness.Alice.Quiescence.GetState(TwoNodeHarness.ChannelId));
        Assert.Equal(QuiescenceState.None, harness.Bob.Quiescence.GetState(TwoNodeHarness.ChannelId));
        Assert.Empty(harness.Alice.Contributor.ActiveReservations);

        // Act: a new connection, then a new splice
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();
        var second = await harness.SpliceAsync(harness.Alice, 100_000);

        // Assert
        Assert.True(second.State == SpliceNegotiationState.Signed, $"{second.State}: {second.FailureReason}");
    }

    /// <summary>
    /// NL-470 (4): a quiescence requested before the channel is reestablished on the new connection owes its
    /// <c>stfu</c>; the reestablish now releases it (no commitment transition needed).
    /// </summary>
    [Fact]
    public async Task Given_AQuiescenceRequestedBeforeTheReestablish_When_Reestablished_Then_TheOwedStfuGoesOut()
    {
        // Arrange
        using var harness = new SpliceHarness();
        await harness.Harness.DisconnectAsync();
        await harness.Harness.ReconnectAsync();

        // Act: requested while both channel_reestablish are still queued
        var request = harness.Alice.Quiescence.RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe,
                                                            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(harness.Transcript, t => t.Message is StfuMessage);
        await harness.PumpAsync(request);

        // Assert
        Assert.Equal(QuiescenceInitiator.Local, await request);
        Assert.Contains(harness.Transcript, t => t is { From: "Alice", Message: StfuMessage });
        var reestablish = harness.Sequence().FindIndex(s => s == "Bob:ChannelReestablish");
        var stfu = harness.Sequence().FindIndex(s => s == "Alice:Stfu");
        Assert.True(reestablish >= 0 && reestablish < stfu);
    }

    #endregion

    private static void AssertPending(SpliceHarness harness, TxId spliceTxId, long aliceDeltaMsat, long bobDeltaMsat)
    {
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var pending = Assert.Single(node.Port.Saved!.Pending);
            Assert.Equal(spliceTxId, pending.FundingTxId);
            Assert.Equal(ChannelFundingKind.Splice, pending.Kind);
            Assert.Equal(ChannelFundingStatus.Pending, pending.Status);
            var isAlice = node == harness.Alice;
            Assert.Equal(isAlice ? aliceDeltaMsat : bobDeltaMsat, pending.LocalBalanceDeltaMsat);
            Assert.Equal(isAlice ? bobDeltaMsat : aliceDeltaMsat, pending.RemoteBalanceDeltaMsat);
            Assert.Single(node.Broadcasts, b => b.TransactionId == spliceTxId);
            Assert.Single(node.Watches, w => w.TransactionId == spliceTxId);
            var session = Assert.Single(node.Sessions.Committed.Values);
            Assert.Equal(InteractiveTxSessionState.Signed, session.State);
            Assert.Equal(InteractiveTxPurpose.Splice, session.Purpose);
            Assert.False(node.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);

            // SP-I1: our shared_input_signature was made only after the peer's splice commitment was saved
            Assert.Contains(node.PersistedSpliceCommitments, m => m.Item2 == spliceTxId);
            Assert.Contains(spliceTxId, node.SharedInputSignatures);
        }

        // Both sides broadcast the same fully signed transaction
        Assert.Equal(harness.Alice.Broadcasts.Single().RawTransaction, harness.Bob.Broadcasts.Single().RawTransaction);
        Assert.Equal(TwoNodeHarness.FundingSatoshis * 1_000,
                     harness.Alice.Node.State.LocalBalanceMsat + harness.Alice.Node.State.RemoteBalanceMsat);
    }

    /// <summary>An HTLC from <paramref name="from"/> fulfilled by <paramref name="to"/>, both commitment dances.</summary>
    private static async Task PayAsync(SpliceHarness harness, SpliceNode from, SpliceNode to, ulong amountMsat, int tag)
    {
        var preimage = TwoNodeHarness.Preimage(tag);
        var hash = TwoNodeHarness.Hash(preimage);
        var fromBalance = from.Node.State.LocalBalanceMsat;
        var id = await from.Node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId,
                                                           LightningMoney.MilliSatoshis(amountMsat), hash, CltvExpiry,
                                                           s_onion, null, HtlcOrigin.Local(hash),
                                                           TestContext.Current.CancellationToken);
        await harness.PumpAsync();
        await to.Node.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, id, preimage,
                                                  TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        Assert.Equal(fromBalance - amountMsat, from.Node.State.LocalBalanceMsat);
        Assert.Equal(ChannelState.Open, from.Node.Channel.State);
    }
}