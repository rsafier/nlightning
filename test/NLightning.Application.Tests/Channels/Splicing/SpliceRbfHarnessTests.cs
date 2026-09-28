using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Splicing;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Quiescence;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Onchain.Interfaces;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Protocol.Payloads;
using Harness;

/// <summary>
/// Wave SPR, SPR-T1/T2 on the real engine and signer (<see cref="SpliceHarness"/> with <c>realEngine</c>): an RBF of a
/// pending splice by either side (the <c>tx_init_rbf</c> sender becomes the interactive-tx initiator), the bumped
/// transaction double-spending the first, payments while several attempts are pending (one <c>commitment_signed</c>
/// per active funding), the lock of any attempt discarding the others, a splice-out RBF with a negative
/// <c>funding_output_contribution</c> (NL-481), a restart with attempts pending, and the receiver's rules.
/// </summary>
public class SpliceRbfHarnessTests
{
    private const uint CltvExpiry = 700;
    private const long SpliceIn = 100_000;
    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    private static bool IsRbfFlowMessage(IChannelMessage message) =>
        message.Type is MessageTypes.Stfu or MessageTypes.TxInitRbf or MessageTypes.TxAckRbf
                     or MessageTypes.CommitmentSigned or MessageTypes.TxSignatures or MessageTypes.TxAbort;

    #region RBF by either side

    /// <summary>
    /// Alice splices in, then bumps her splice: stfu both ways (Alice the quiescence initiator), tx_init_rbf at the new
    /// feerate carrying her contribution, tx_ack_rbf, a new construction with Alice as the interactive-tx initiator,
    /// commit_sig and tx_signatures both ways. Both nodes hold two pending attempts, the second a
    /// <see cref="ChannelFundingKind.SpliceRbf"/> of the first; the bumped transaction spends the same funding output.
    /// </summary>
    [Fact]
    public async Task Given_APendingSplice_When_TheSpliceInitiatorBumpsIt_Then_ASecondAttemptDoubleSpendsTheFirst()
    {
        // Arrange
        using var harness = CreateHarness();
        harness.Alice.Fund(SpliceIn + 200_000);
        var first = (await harness.SpliceAsync(harness.Alice, SpliceIn)).SpliceTxId!.Value;
        var mark = harness.Transcript.Count;

        // Act
        var result = await BumpAsync(harness, harness.Alice, 2_000);

        // Assert
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        Assert.Empty(harness.Failures);
        var second = result.SpliceTxId!.Value;
        Assert.NotEqual(first, second);
        var sequence = Sequence(harness, mark);
        Assert.Equal(["Alice:Stfu", "Bob:Stfu", "Alice:TxInitRbf", "Bob:TxAckRbf"], sequence.Take(4));
        Assert.Contains("Alice:CommitmentSigned", sequence);
        Assert.Contains("Bob:CommitmentSigned", sequence);
        Assert.Equal(2, sequence.Count(s => s.EndsWith(":TxSignatures")));
        Assert.Equal(SpliceIn, RbfContribution(harness, mark, "Alice"));
        // NL-503: a zero contribution is still sent as the TLV (Core Lightning requires it)
        Assert.Equal(0, RbfContribution(harness, mark, "Bob"));

        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var pending = node.Node.State.PendingFundings;
            Assert.Equal([first, second], pending.Select(f => f.FundingTxId));
            Assert.Equal(ChannelFundingKind.SpliceRbf, pending[1].Kind);
            Assert.Equal(first, pending[1].RbfOf);
            Assert.Equal(2_000u, pending[1].FeeratePerKw);
            Assert.Equal(pending[0].CapacitySatoshis, pending[1].CapacitySatoshis);
            Assert.Equal(pending[0].LocalFundingPubKey, pending[1].LocalFundingPubKey);
            Assert.Equal(ChannelFundingStatus.Pending, node.FundingRows.Committed[second].Status);
            Assert.False(node.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
        }

        AssertDoubleSpend(harness.Alice, first, second);
        var bumped = harness.Alice.Broadcasts.Single(b => b.TransactionId == second);
        Assert.Equal(first, bumped.ReplacesTransactionId);
        Assert.Equal(bumped.RawTransaction, harness.Bob.Broadcasts.Single(b => b.TransactionId == second).RawTransaction);
        // The same inputs pay out less: the bump's fee is higher
        Assert.True(OutputTotal(bumped.RawTransaction)
                  < OutputTotal(harness.Alice.Broadcasts.Single(b => b.TransactionId == first).RawTransaction));
    }

    /// <summary>
    /// Bob, who did not initiate the splice, bumps it (BOLT 2: any quiescence initiator "MAY send tx_init_rbf even if it
    /// is not the splice initiator"): Bob becomes the interactive-tx initiator (he adds the shared input and the funding
    /// output and pays the common fields from his channel balance: a negative contribution), Alice keeps her splice-in
    /// with the same wallet input and pays only for her own input and change.
    /// </summary>
    [Fact]
    public async Task Given_ASpliceOfAlice_When_BobBumpsIt_Then_BobIsTheInitiatorAndAliceKeepsHerSpliceIn()
    {
        // Arrange
        using var harness = CreateHarness();
        var utxo = harness.Alice.Fund(SpliceIn + 200_000);
        var first = (await harness.SpliceAsync(harness.Alice, SpliceIn)).SpliceTxId!.Value;
        var mark = harness.Transcript.Count;

        // Act
        var result = await BumpAsync(harness, harness.Bob, 2_000);

        // Assert
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        Assert.Empty(harness.Failures);
        var second = result.SpliceTxId!.Value;
        Assert.Equal(["Bob:Stfu", "Alice:Stfu", "Bob:TxInitRbf", "Alice:TxAckRbf"], Sequence(harness, mark).Take(4));
        var bobContribution = RbfContribution(harness, mark, "Bob");
        Assert.True(bobContribution < 0, $"Bob's contribution {bobContribution}");
        Assert.Equal(SpliceIn, RbfContribution(harness, mark, "Alice"));

        // Bob's first tx_add_input is the shared input (the interactive-tx initiator adds it)
        var (sender, firstAdd) = harness.Transcript.Skip(mark).First(t => t.Message is TxAddInputMessage);
        Assert.Equal("Bob", sender);
        Assert.NotNull(((TxAddInputMessage)firstAdd).SharedInputTxIdTlv);

        var aliceSide = harness.Alice.Node.State.PendingFundings[1];
        Assert.Equal(SpliceIn * 1_000, aliceSide.LocalBalanceDeltaMsat);
        Assert.Equal(bobContribution!.Value * 1_000, aliceSide.RemoteBalanceDeltaMsat);
        Assert.Equal(TwoNodeHarness.FundingSatoshis + (ulong)(SpliceIn + bobContribution.Value),
                     aliceSide.CapacitySatoshis);
        AssertDoubleSpend(harness.Bob, first, second);

        // Alice's wallet input is in both attempts (the same reservation)
        foreach (var txId in new[] { first, second })
        {
            var tx = Parse(harness.Alice.Broadcasts.Single(b => b.TransactionId == txId).RawTransaction);
            Assert.Contains(tx.Inputs, i => i.PrevOut.Hash.ToBytes().SequenceEqual((byte[])utxo.TxId)
                                           && i.PrevOut.N == utxo.Vout);
        }
    }

    #endregion

    #region Several attempts pending: batches, lock of any sibling

    /// <summary>
    /// Three attempts pending (the splice and two RBFs): an HTLC each way goes as <c>start_batch</c>(4) with one
    /// <c>commitment_signed</c> per active funding, current first, all at the same commitment number (SP-OP-03).
    /// </summary>
    [Fact]
    public async Task Given_ThreeAttemptsPending_When_PaymentsFlow_Then_EachSignatureIsABatchOfFour()
    {
        // Arrange
        using var harness = CreateHarness();
        harness.Alice.Fund(SpliceIn + 200_000);
        var first = (await harness.SpliceAsync(harness.Alice, SpliceIn)).SpliceTxId!.Value;
        var second = (await BumpAsync(harness, harness.Alice, 2_000)).SpliceTxId!.Value;
        var third = (await BumpAsync(harness, harness.Bob, 3_000)).SpliceTxId!.Value;
        var current = harness.Alice.Node.State.Params.Funding!.FundingTxId;
        var mark = harness.Transcript.Count;

        // Act
        var (id, preimage) = await OfferAsync(harness, harness.Alice, 20_000_000, 1);
        await FulfillAsync(harness, harness.Bob, id, preimage);

        // Assert
        Assert.Empty(harness.Failures);
        var batches = harness.Transcript.Skip(mark).Select(t => t.Message).OfType<StartBatchMessage>().ToList();
        Assert.Equal(4, batches.Count);
        Assert.All(batches, b => Assert.Equal(4, b.Payload.BatchSize));
        var signed = harness.Transcript.Skip(mark).Where(t => t.Message is CommitmentSignedMessage).ToList();
        Assert.Equal(16, signed.Count);
        Assert.Equal([current, first, second, third],
                     signed.Take(4).Select(t => ((CommitmentSignedMessage)t.Message).FundingTxIdTlv!.FundingTxId));
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Equal(3, node.Node.State.LocalCommit.PendingFundingSignatures.Count);
            Assert.Empty(node.Node.State.Htlcs);
        }
    }

    /// <summary>
    /// With two attempts pending, whichever confirms is locked by both sides and the other one is discarded in the
    /// lock's save (SP-LK-03): one funding left, the other attempt's row <see cref="ChannelFundingStatus.Discarded"/>,
    /// the replaced funding <see cref="ChannelFundingStatus.Replaced"/>, and payments go over the locked one alone.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_TwoAttemptsPending_When_EitherLocks_Then_TheOtherIsDiscarded(bool lockTheBump)
    {
        // Arrange
        using var harness = CreateHarness();
        harness.Alice.Fund(SpliceIn + 200_000);
        var first = (await harness.SpliceAsync(harness.Alice, SpliceIn)).SpliceTxId!.Value;
        var second = (await BumpAsync(harness, harness.Alice, 2_000)).SpliceTxId!.Value;
        var original = harness.Alice.Node.State.Params.Funding!.FundingTxId;
        var (locked, discarded) = lockTheBump ? (second, first) : (first, second);

        // Act
        await harness.ConfirmAsync(locked, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);

        // Assert
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Node.State.PendingFundings);
            Assert.Equal(locked, node.Node.State.Params.Funding!.FundingTxId);
            Assert.Equal(locked, node.FundingRows.LockedCurrent!.FundingTxId);
            Assert.Equal(ChannelFundingStatus.Discarded, node.FundingRows.Committed[discarded].Status);
            Assert.Equal(ChannelFundingStatus.Replaced, node.FundingRows.Committed[original].Status);
        }

        var mark = harness.Transcript.Count;
        await OfferAsync(harness, harness.Alice, 1_000_000, 3);
        Assert.DoesNotContain(harness.Transcript.Skip(mark), t => t.Message is StartBatchMessage);
        Assert.All(harness.Transcript.Skip(mark).Select(t => t.Message).OfType<CommitmentSignedMessage>(),
                   cs => Assert.Equal(locked, cs.FundingTxIdTlv?.FundingTxId));
        Assert.Empty(harness.Failures);
    }

    #endregion

    #region Splice-out RBF (NL-481)

    /// <summary>
    /// Alice splices out, then bumps: her <c>tx_init_rbf</c> carries a negative <c>funding_output_contribution</c>
    /// (NL-481; it could only be positive before), the new attempt pays the same splice-out amount to the same
    /// destination with a larger fee taken from her balance, and the bumped attempt locks with that balance.
    /// </summary>
    [Fact]
    public async Task Given_ASpliceOut_When_Bumped_Then_TheContributionIsNegativeAndTheOutputIsKept()
    {
        // Arrange
        using var harness = CreateHarness();
        var first = (await harness.SpliceAsync(harness.Alice, -80_000)).SpliceTxId!.Value;
        var firstDelta = harness.Alice.Node.State.PendingFundings[0].LocalBalanceDeltaMsat;
        var before = harness.Alice.Node.State.LocalBalanceMsat;
        var mark = harness.Transcript.Count;

        // Act
        var result = await BumpAsync(harness, harness.Alice, 2_000);

        // Assert
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        var second = result.SpliceTxId!.Value;
        var contribution = RbfContribution(harness, mark, "Alice");
        Assert.NotNull(contribution);
        Assert.True(contribution.Value * 1_000 < firstDelta, $"{contribution} vs {firstDelta} msat");
        var attempt = harness.Alice.Node.State.PendingFundings[1];
        Assert.Equal(contribution.Value * 1_000, attempt.LocalBalanceDeltaMsat);
        Assert.Equal(contribution.Value * 1_000, harness.Bob.Node.State.PendingFundings[1].RemoteBalanceDeltaMsat);
        var tx = Parse(harness.Alice.Broadcasts.Single(b => b.TransactionId == second).RawTransaction);
        Assert.Contains(tx.Outputs, o => o.ScriptPubKey.ToBytes().SequenceEqual((byte[])harness.Alice.Destination.Script)
                                      && o.Value.Satoshi == 80_000);
        AssertDoubleSpend(harness.Alice, first, second);

        // Act: the bump confirms
        await harness.ConfirmAsync(second, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);

        // Assert
        Assert.Empty(harness.Failures);
        Assert.Equal(second, harness.Alice.Node.State.Params.Funding!.FundingTxId);
        Assert.Equal((long)before + attempt.LocalBalanceDeltaMsat, (long)harness.Alice.Node.State.LocalBalanceMsat);
    }

    #endregion

    #region Restart with attempts pending

    /// <summary>
    /// Alice restarts with two attempts pending: after the reestablish both are still active on both sides, a payment
    /// goes as a batch of three, a third attempt can be started (the driver learns the signed attempts from their rows
    /// again) and the latest attempt locks.
    /// </summary>
    [Fact]
    public async Task Given_TwoAttemptsPending_When_AliceRestarts_Then_TheyStayActiveAndCanBeBumpedAndLocked()
    {
        // Arrange
        using var harness = CreateHarness();
        harness.Alice.Fund(SpliceIn + 200_000);
        var first = (await harness.SpliceAsync(harness.Alice, SpliceIn)).SpliceTxId!.Value;
        var second = (await BumpAsync(harness, harness.Alice, 2_000)).SpliceTxId!.Value;

        // Act: restart and reconnect
        await harness.RestartAsync(harness.Alice);
        RegisterPendingFundings(harness.Alice);
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
            Assert.Equal([first, second], node.Node.State.PendingFundings.Select(f => f.FundingTxId));

        var mark = harness.Transcript.Count;
        var (id, preimage) = await OfferAsync(harness, harness.Alice, 5_000_000, 1);
        await FulfillAsync(harness, harness.Bob, id, preimage);
        var batches = harness.Transcript.Skip(mark).Select(t => t.Message).OfType<StartBatchMessage>().ToList();
        Assert.Equal(4, batches.Count);
        Assert.All(batches, b => Assert.Equal(3, b.Payload.BatchSize));
        Assert.Empty(harness.Failures);
        Assert.Empty(harness.Alice.Node.State.Htlcs);

        // Act: a third attempt after the restart
        var third = await BumpAsync(harness, harness.Alice, 3_000);

        // Assert
        Assert.True(third.State == SpliceNegotiationState.Signed, $"{third.State}: {third.FailureReason}");
        foreach (var node in new[] { harness.Alice, harness.Bob })
            Assert.Equal([first, second, third.SpliceTxId!.Value],
                         node.Node.State.PendingFundings.Select(f => f.FundingTxId));

        // Act: the latest attempt locks
        await harness.ConfirmAsync(third.SpliceTxId!.Value, TwoNodeHarness.BlockHeight + 3, harness.Alice,
                                   harness.Bob);

        // Assert
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Node.State.PendingFundings);
            Assert.Equal(third.SpliceTxId.Value, node.Node.State.Params.Funding!.FundingTxId);
            Assert.Equal(ChannelFundingStatus.Discarded, node.FundingRows.Committed[first].Status);
            Assert.Equal(ChannelFundingStatus.Discarded, node.FundingRows.Committed[second].Status);
        }
    }

    #endregion

    #region Refusals

    /// <summary>
    /// SP-LK-04: we do not bump a splice after sending <c>splice_locked</c> for it, and the bump starts no quiescence.
    /// </summary>
    [Fact]
    public async Task Given_OurSpliceLockedWasSent_When_Bumping_Then_Refused()
    {
        // Arrange
        using var harness = CreateHarness();
        harness.Alice.Fund(SpliceIn + 200_000);
        var first = (await harness.SpliceAsync(harness.Alice, SpliceIn)).SpliceTxId!.Value;
        await harness.ConfirmAsync(first, TwoNodeHarness.BlockHeight + 3, harness.Alice);
        var mark = harness.Transcript.Count;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.Service.BumpAsync(new SpliceBumpRequest(TwoNodeHarness.ChannelId, 2_000),
                                                  TestContext.Current.CancellationToken));
        Assert.Contains("SP-LK-04", exception.Message);
        Assert.DoesNotContain(harness.Transcript.Skip(mark), t => t.Message is StfuMessage);
    }

    /// <summary>A feerate below the IT-RBF-01 floor is refused before anything is sent.</summary>
    [Fact]
    public async Task Given_AFeerateBelowTheFloor_When_Bumping_Then_Refused()
    {
        // Arrange
        using var harness = CreateHarness();
        harness.Alice.Fund(SpliceIn + 200_000);
        await harness.SpliceAsync(harness.Alice, SpliceIn);
        var mark = harness.Transcript.Count;

        // Act & Assert (the splice ran at 1,000 sat/kw: the floor is 1,041)
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((ISpliceService)harness.Alice.Service).BumpAsync(TwoNodeHarness.ChannelId, 1_040,
                                                  TestContext.Current.CancellationToken));
        Assert.Contains("IT-RBF-01", exception.Message);
        Assert.Equal(mark, harness.Transcript.Count);
    }

    /// <summary>Our cap on the fee of the bump (<c>bumpsplice --max-fee-sat</c>) refuses it before anything is
    /// sent.</summary>
    [Fact]
    public async Task Given_AMaxFeeBelowOurShare_When_Bumping_Then_Refused()
    {
        // Arrange
        using var harness = CreateHarness();
        harness.Alice.Fund(SpliceIn + 200_000);
        await harness.SpliceAsync(harness.Alice, SpliceIn);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.Service.BumpAsync(new SpliceBumpRequest(TwoNodeHarness.ChannelId, 2_000, 1),
                                                  TestContext.Current.CancellationToken));
        Assert.Contains("above the limit", exception.Message);
    }

    /// <summary>
    /// SP-LK-04 receive side (NL-489): the peer's <c>tx_init_rbf</c> after its own <c>splice_locked</c>, while it is the
    /// quiescence initiator, is a warning and close; nothing is negotiated.
    /// </summary>
    [Fact]
    public async Task Given_ThePeerSentSpliceLocked_When_ItsTxInitRbfArrives_Then_WarningAndClose()
    {
        // Arrange: Bob reaches the depth first (Alice records his splice_locked), then Bob quiesces the channel
        using var harness = CreateHarness();
        harness.Alice.Fund(SpliceIn + 200_000);
        var first = (await harness.SpliceAsync(harness.Alice, SpliceIn)).SpliceTxId!.Value;
        await harness.ConfirmAsync(first, TwoNodeHarness.BlockHeight + 3, harness.Bob);
        var quiescence = harness.Bob.Quiescence.RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.SpliceRbf,
                                                             TestContext.Current.CancellationToken);
        await harness.PumpAsync(quiescence);
        Assert.Equal(QuiescenceInitiator.Remote,
                     harness.Alice.Quiescence.GetState(TwoNodeHarness.ChannelId).Initiator);
        using var scope = harness.Alice.Node.Services.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var message = new TxInitRbfMessage(new TxInitRbfPayload(TwoNodeHarness.ChannelId, 2_000, 0));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
            () => harness.Alice.Service.HandleTxInitRbfAsync(message, SpliceHarness.CreateFeatures(),
                                                             SpliceHarness.NodeIdOf("Bob"), unitOfWork,
                                                             TestContext.Current.CancellationToken));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("SP-LK-04", exception.Message);
        Assert.Single(harness.Alice.Node.State.PendingFundings);
        Assert.Null(harness.Alice.Service.GetNegotiation(TwoNodeHarness.ChannelId)?.RbfOf);
    }

    /// <summary>
    /// BOLT 2: "If another RBF attempt has been created recently: SHOULD send tx_abort" (our
    /// <c>Splice:MinRbfInterval</c>): Bob's bump right after the splice is rejected with <c>tx_abort</c>, which ends
    /// the quiescence; the splice stays the only attempt.
    /// </summary>
    [Fact]
    public async Task Given_ARecentAttempt_When_ThePeerBumps_Then_TxAbortAndTheSpliceIsKept()
    {
        // Arrange
        using var harness = new SpliceHarness((name, o) => o.MinRbfInterval = name == "Alice"
                                                                                  ? TimeSpan.FromHours(1)
                                                                                  : TimeSpan.Zero,
                                              realEngine: true);
        harness.Alice.Fund(SpliceIn + 200_000);
        var first = (await harness.SpliceAsync(harness.Alice, SpliceIn)).SpliceTxId!.Value;
        var mark = harness.Transcript.Count;

        // Act
        var result = await BumpAsync(harness, harness.Bob, 2_000);

        // Assert
        Assert.Equal(SpliceNegotiationState.Aborted, result.State);
        Assert.Contains("Alice:TxAbort", Sequence(harness, mark));
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Equal([first], node.Node.State.PendingFundings.Select(f => f.FundingTxId));
            Assert.False(node.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
        }

        // And a later bump (Bob no longer blocked) still works once Alice's interval is over: Alice bumps herself
        var own = await BumpAsync(harness, harness.Alice, 2_000);
        Assert.True(own.State == SpliceNegotiationState.Signed, $"{own.State}: {own.FailureReason}");
        Assert.Equal(ChannelState.Open, harness.Alice.Node.Channel.State);
    }

    #endregion

    #region Review fixes (wave SPR review)

    /// <summary>
    /// A peer's <c>tx_init_rbf</c> of our splice-out at 4,000,000 sat/kw (above <c>Splice:MaxFeeratePerKw</c>) gets
    /// <c>tx_abort</c> before anything is planned: in a peer's RBF we pay our output's fee from our balance at the peer's
    /// feerate, so without the cap the peer could burn up to our balance above the reserve.
    /// </summary>
    [Fact]
    public async Task Given_OurSpliceOut_When_ThePeerBumpsAtAHugeFeerate_Then_TxAbortAndOurBalanceIsUnchanged()
    {
        // Arrange: Bob is the quiescence initiator
        using var harness = CreateHarness();
        var first = (await harness.SpliceAsync(harness.Alice, -80_000)).SpliceTxId!.Value;
        var balance = harness.Alice.Node.State.LocalBalanceMsat;
        var quiescence = harness.Bob.Quiescence.RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.SpliceRbf,
                                                             TestContext.Current.CancellationToken);
        await harness.PumpAsync(quiescence);
        using var scope = harness.Alice.Node.Services.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var message = new TxInitRbfMessage(new TxInitRbfPayload(TwoNodeHarness.ChannelId, 4_000_000, 0));

        // Act
        var replies = await harness.Alice.Service.HandleTxInitRbfAsync(message, SpliceHarness.CreateFeatures(),
                                                                       SpliceHarness.NodeIdOf("Bob"), unitOfWork,
                                                                       TestContext.Current.CancellationToken);

        // Assert
        var abort = Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        Assert.Contains("above our limit", System.Text.Encoding.ASCII.GetString(abort.Payload.Data));
        Assert.Equal([first], harness.Alice.Node.State.PendingFundings.Select(f => f.FundingTxId));
        Assert.Equal(balance, harness.Alice.Node.State.LocalBalanceMsat);
        Assert.Null(harness.Alice.Service.GetNegotiation(TwoNodeHarness.ChannelId)?.RbfOf);
        Assert.False(harness.Alice.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
    }

    /// <summary>
    /// The same cap over the wire: Alice accepts RBFs up to 1,500 sat/kw only, Bob's bump at 2,000 gets her
    /// <c>tx_abort</c>, which ends the quiescence on both sides; the splice-out stays the only attempt, Alice's
    /// balance is unchanged and Bob's host serves his earlier negotiation again (not the aborted attempt).
    /// </summary>
    [Fact]
    public async Task Given_APeerFeerateAboveOurMax_When_ThePeerBumps_Then_TxAbortAndNothingChanges()
    {
        // Arrange
        using var harness = new SpliceHarness((name, o) =>
                                              {
                                                  o.MinRbfInterval = TimeSpan.Zero;
                                                  if (name == "Alice")
                                                      o.MaxFeeratePerKw = 1_500;
                                              }, realEngine: true);
        var first = (await harness.SpliceAsync(harness.Alice, -80_000)).SpliceTxId!.Value;
        var balance = harness.Alice.Node.State.LocalBalanceMsat;
        var mark = harness.Transcript.Count;

        // Act
        var result = await BumpAsync(harness, harness.Bob, 2_000);

        // Assert
        Assert.Equal(SpliceNegotiationState.Aborted, result.State);
        Assert.Contains("Alice:TxAbort", Sequence(harness, mark));
        Assert.DoesNotContain("Alice:TxAckRbf", Sequence(harness, mark));
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Equal([first], node.Node.State.PendingFundings.Select(f => f.FundingTxId));
            Assert.False(node.Quiescence.GetState(TwoNodeHarness.ChannelId).BlocksNewLocalUpdates);
        }

        Assert.Equal(balance, harness.Alice.Node.State.LocalBalanceMsat);
        var host = GetHost(harness.Bob);
        Assert.NotNull(host);
        Assert.NotEqual(SpliceNegotiationState.Aborted, host.Negotiation.State);
    }

    /// <summary>
    /// Our share of the fee of a peer's RBF is capped (<c>Splice:MaxRbfFeeShareSatoshis</c>): above it we contribute
    /// nothing to the attempt (BOLT 2: "sets their sats to zero") instead of paying: Alice's <c>tx_ack_rbf</c> carries
    /// no contribution, the new attempt drops her splice-out output and takes nothing from her balance.
    /// </summary>
    [Fact]
    public async Task Given_OurFeeShareAboveTheCap_When_ThePeerBumps_Then_WeContributeNothing()
    {
        // Arrange
        using var harness = new SpliceHarness((name, o) =>
                                              {
                                                  o.MinRbfInterval = TimeSpan.Zero;
                                                  if (name == "Alice")
                                                      o.MaxRbfFeeShareSatoshis = 1;
                                              }, realEngine: true);
        await harness.SpliceAsync(harness.Alice, -80_000);
        var mark = harness.Transcript.Count;

        // Act
        var result = await BumpAsync(harness, harness.Bob, 2_000);

        // Assert
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        Assert.Empty(harness.Failures);
        Assert.Equal(0, RbfContribution(harness, mark, "Alice"));
        Assert.Equal(0, harness.Alice.Node.State.PendingFundings[1].LocalBalanceDeltaMsat);
        Assert.Equal(0, harness.Bob.Node.State.PendingFundings[1].RemoteBalanceDeltaMsat);
        var tx = Parse(harness.Alice.Broadcasts.Single(b => b.TransactionId == result.SpliceTxId!.Value)
                                  .RawTransaction);
        Assert.DoesNotContain(tx.Outputs,
                              o => o.ScriptPubKey.ToBytes().SequenceEqual((byte[])harness.Alice.Destination.Script));
    }

    /// <summary>
    /// Our fee share of a peer's RBF above half of what our contribution moves is refused too, whatever the cap: a
    /// 2,000 sat splice-out whose output would pay about 2,500 sat at the peer's 20,000 sat/kw is not rebuilt.
    /// </summary>
    [Fact]
    public async Task Given_AFeeShareAboveHalfOfOurSpliceOut_When_ThePeerBumps_Then_WeContributeNothing()
    {
        // Arrange
        using var harness = new SpliceHarness((_, o) =>
                                              {
                                                  o.MinRbfInterval = TimeSpan.Zero;
                                                  o.MaxRbfFeeShareSatoshis = null;
                                              }, realEngine: true);
        await harness.SpliceAsync(harness.Alice, -2_000);
        var mark = harness.Transcript.Count;

        // Act
        var result = await BumpAsync(harness, harness.Bob, 20_000);

        // Assert
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        Assert.Equal(0, RbfContribution(harness, mark, "Alice"));
        Assert.Equal(0, harness.Alice.Node.State.PendingFundings[1].LocalBalanceDeltaMsat);
    }

    /// <summary>
    /// A bumped attempt does not take the one it bumps out of the rebroadcast set (the replacement may never be accepted
    /// by the mempool): both stay pending, and only the lock abandons the attempt that did not confirm. The lock also
    /// drops the channel's host (no attempt is left to RBF).
    /// </summary>
    [Fact]
    public async Task Given_ABumpedSplice_When_TheBumpCompletesAndLocks_Then_TheOldAttemptIsRebroadcastUntilTheLock()
    {
        // Arrange
        using var harness = CreateHarness();
        harness.Alice.Fund(SpliceIn + 200_000);
        var first = (await harness.SpliceAsync(harness.Alice, SpliceIn)).SpliceTxId!.Value;

        // Act
        var second = (await BumpAsync(harness, harness.Alice, 2_000)).SpliceTxId!.Value;

        // Assert
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var broadcasts = GetBroadcastRepository(node);
            broadcasts.Verify(b => b.MarkReplacedAsync(It.IsAny<TxId>()), Times.Never);
            Assert.Equal(first, node.Broadcasts.Single(b => b.TransactionId == second).ReplacesTransactionId);
        }

        // Act: the bump locks
        await harness.ConfirmAsync(second, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);

        // Assert
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var broadcasts = GetBroadcastRepository(node);
            broadcasts.Verify(b => b.MarkAbandonedAsync(first), Times.Once);
            broadcasts.Verify(b => b.MarkAbandonedAsync(second), Times.Never);
            Assert.Null(GetHost(node));
        }
    }

    #endregion

    #region Helpers

    private static Mock<IBroadcastTransactionDbRepository> GetBroadcastRepository(SpliceNode node)
    {
        using var scope = node.Node.Services.CreateScope();
        return Mock.Get(scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BroadcastTransactionDbRepository);
    }

    /// <summary>The channel's interactive-tx host of the node's splice service, or null.</summary>
    private static SpliceNegotiationHost? GetHost(SpliceNode node)
    {
        var field = typeof(SpliceService).GetField("_hosts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var hosts = (ConcurrentDictionary<ChannelId, SpliceNegotiationHost>)field.GetValue(node.Service)!;
        return hosts.GetValueOrDefault(TwoNodeHarness.ChannelId);
    }

    private static SpliceHarness CreateHarness() =>
        new((_, o) => o.MinRbfInterval = TimeSpan.Zero, realEngine: true);

    private static async Task<SpliceResult> BumpAsync(SpliceHarness harness, SpliceNode node, uint feerate)
    {
        var bump = ((ISpliceService)node.Service).BumpAsync(TwoNodeHarness.ChannelId, feerate,
                                                            TestContext.Current.CancellationToken);
        await harness.PumpAsync(bump);
        return await bump;
    }

    private static List<string> Sequence(SpliceHarness harness, int mark) =>
        harness.Transcript.Skip(mark)
               .Where(t => IsRbfFlowMessage(t.Message))
               .Select(t => $"{t.From}:{Enum.GetName(t.Message.Type)}")
               .ToList();

    /// <summary>The <c>funding_output_contribution</c> of the tx_init_rbf or tx_ack_rbf <paramref name="from"/> sent
    /// since <paramref name="mark"/> (null: no TLV).</summary>
    private static long? RbfContribution(SpliceHarness harness, int mark, string from) =>
        harness.Transcript.Skip(mark)
               .Where(t => t.From == from)
               .Select(t => t.Message switch
                {
                    TxInitRbfMessage init => (IsRbf: true, Satoshis: init.FundingOutputContributionTlv?.Satoshis),
                    TxAckRbfMessage ack => (IsRbf: true, Satoshis: ack.FundingOutputContributionTlv?.Satoshis),
                    _ => (IsRbf: false, Satoshis: null)
                })
               .First(r => r.IsRbf).Satoshis;

    /// <summary>
    /// What the node's signer loads from the database after a restart in production (<c>ChannelSigningInfo.Fundings</c>
    /// from <c>ChannelSigningInfoDbRepository</c>): the harness registers channels by hand without their pending
    /// fundings, so they are registered here.
    /// </summary>
    private static void RegisterPendingFundings(SpliceNode node)
    {
        var signer = node.Node.Services.GetRequiredService<Domain.Bitcoin.Interfaces.ILightningSigner>();
        foreach (var funding in node.Node.State.PendingFundings)
            signer.RegisterFunding(TwoNodeHarness.ChannelId, funding);
    }

    private static NBitcoin.Transaction Parse(byte[] raw) =>
        NBitcoin.Transaction.Parse(Convert.ToHexString(raw), NBitcoin.Network.RegTest);

    private static long OutputTotal(byte[] raw) => Parse(raw).Outputs.Sum(o => o.Value.Satoshi);

    /// <summary>Both attempts spend the channel's funding output (the shared input): they double-spend each other.</summary>
    private static void AssertDoubleSpend(SpliceNode node, TxId first, TxId second)
    {
        var a = Parse(node.Broadcasts.Single(b => b.TransactionId == first).RawTransaction);
        var b = Parse(node.Broadcasts.Single(b => b.TransactionId == second).RawTransaction);
        var funding = node.Node.State.Params.Funding!;
        Assert.Contains(a.Inputs, i => i.PrevOut.Hash.ToBytes().SequenceEqual((byte[])funding.FundingTxId)
                                    && i.PrevOut.N == funding.OutputIndex);
        Assert.Contains(b.Inputs, i => i.PrevOut.Hash.ToBytes().SequenceEqual((byte[])funding.FundingTxId)
                                    && i.PrevOut.N == funding.OutputIndex);
    }

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