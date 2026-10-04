namespace NLightning.Application.Tests.Channels.Splicing;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.ValueObjects;
using Harness;
using NLightning.Tests.Utils;
using NLightning.Tests.Utils.Mocks;

/// <summary>
/// Splicing plan SP2-A-T3: the reestablish flows of <c>bolt02/splicing-test.md</c> (SP-T-03..SP-T-11) on the real
/// engine (<see cref="SpliceHarness"/> with <c>realEngine</c>): Alice splices in 100,000 sat, the link drops at the
/// scenario's point (every message in flight lost both ways), and on reconnection each <c>channel_reestablish</c>
/// carries <c>next_funding</c> / <c>my_current_funding_locked</c> as BOLT 2 says, the retransmissions follow, and the
/// splice completes with the same active commitments on both sides.
/// </summary>
/// <remarks>
/// <para>The protocol's roles: Alice (the initiator) sends the final <c>tx_complete</c> and her <c>commitment_signed</c>
/// right after it; Bob (contribution 0, so the lower <c>tx_add_input</c> total) sends <c>tx_signatures</c> first. Where
/// the test file names the other side for a step, the scenario is played with the roles that step has here.</para>
/// <para>BOLT 2 asks for a missing splice <c>commitment_signed</c> with bit 0 of <c>next_funding</c> and keeps
/// <c>next_commitment_number</c> at the next number; <c>splicing-test.md</c> still draws the older convention
/// (<c>next_commitment_number</c> = the current number). Ours follows BOLT 2 and the planner accepts both.</para>
/// </remarks>
public class SpliceConformanceTests
{
    private const uint CltvExpiry = 700;
    private const long SpliceIn = 100_000;
    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    #region SP-T-03 one side sent commit_sig

    /// <summary>
    /// SP-T-03: Alice's final <c>tx_complete</c> is lost, so only she constructed the transaction and sent
    /// <c>commitment_signed</c>. On reconnection she asks for Bob's with <c>next_funding</c> (bit 0); Bob knows no such
    /// transaction and answers <c>tx_abort</c>, Alice echoes it and forgets the splice: both stay on FundingTx1.
    /// </summary>
    [Fact]
    public async Task Given_OnlyOneSideSentCommitSig_When_Reconnected_Then_TheSpliceIsAbortedOnBothSides()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(SpliceIn + 200_000);
        var start = StartSplice(harness);
        // Alice's commitment_signed is raised by a continuation that follows her tx_complete (a save in between),
        // so the outbox it belongs behind is waited for, not raced (NL-513)
        await PumpUntilAsync(harness, static (from, m) => from == "Alice" && m is TxCompleteMessage,
                             () => SecondQueued(harness.Alice) is CommitmentSignedMessage,
                             "Alice's commitment_signed to be queued behind her tx_complete");
        Assert.IsType<CommitmentSignedMessage>(SecondQueued(harness.Alice));

        // Act
        await harness.Harness.DisconnectAsync();
        var result = await start.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        // The tx_abort exchange is published by the splice negotiation's continuations after the reestablishes were
        // handled, possibly once the exchange already went quiet: the transcript is waited for (NL-513)
        await PumpUntilAsync(harness, static (_, _) => false,
                             () => Sequence(harness, mark) is
                             ["Alice:ChannelReestablish", "Bob:ChannelReestablish", "Bob:TxAbort", "Alice:TxAbort"],
                             "the splice to be aborted on both sides (both tx_abort on the transcript)");

        // Assert: Alice asked for Bob's commitment_signed, Bob never had the transaction
        var aliceReestablish = Reestablish(harness, mark, "Alice");
        var bobReestablish = Reestablish(harness, mark, "Bob");
        Assert.NotNull(aliceReestablish.NextFundingTlv);
        Assert.Equal(1, aliceReestablish.NextFundingTlv.RetransmitFlags);
        Assert.Null(bobReestablish.NextFundingTlv);
        Assert.Equal(
        [
            "Alice:ChannelReestablish", "Bob:ChannelReestablish", "Bob:TxAbort", "Alice:TxAbort"
        ], Sequence(harness, mark));
        Assert.Equal(SpliceNegotiationState.CommitmentSigned, result.State);
        var spliceTxId = result.SpliceTxId!.Value;
        Assert.Equal(new TxId(aliceReestablish.NextFundingTlv.NextFundingTxId), spliceTxId);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Node.State.PendingFundings);
            Assert.Empty(node.Broadcasts);
        }

        // Alice's ChannelFundings row of FundingTx2, written with her commitment_signed, is discarded with the splice:
        // a restart would otherwise load it as a pending splice Bob forgot
        Assert.Equal(ChannelFundingStatus.Discarded, harness.Alice.FundingRows.Committed[spliceTxId].Status);
        Assert.False(harness.Alice.Service.GetNegotiation(TwoNodeHarness.ChannelId) is
        { State: SpliceNegotiationState.CommitmentSigned });
        await AssertUsableAsync(harness, null);

        // And after Alice restarts neither side names the splice again
        await harness.RestartAsync(harness.Alice);
        mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await PumpUntilAsync(harness, static (_, _) => false,
                             () => Reestablished(harness, mark) && harness.Alice.Node.OutboxIsEmpty
                                && harness.Bob.Node.OutboxIsEmpty,
                             "both channel_reestablish after Alice's restart");
        Assert.Null(Reestablish(harness, mark, "Alice").NextFundingTlv);
        Assert.Null(Reestablish(harness, mark, "Bob").NextFundingTlv);
        Assert.DoesNotContain(harness.Transcript.Skip(mark), t => t.Message is TxAbortMessage);
        Assert.DoesNotContain(harness.Alice.FundingRows.Committed.Values,
                              f => f.Status == ChannelFundingStatus.Pending);
    }

    #endregion

    #region SP-T-04 both sides sent commit_sig

    /// <summary>
    /// SP-T-04: both <c>commitment_signed</c> are lost. Each <c>channel_reestablish</c> names FundingTx2 with bit 0, each
    /// side retransmits its <c>commitment_signed</c> byte for byte, then Bob sends <c>tx_signatures</c> first and Alice
    /// answers: FundingTx2 is pending on both.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_BothCommitSigsLost_When_Reconnected_Then_BothAreRetransmittedAndTheSpliceCompletes(bool taproot)
    {
        // Arrange (also on a simple taproot channel, NL-965: PR #1324 nonces through channel_reestablish)
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: taproot);
        harness.Alice.Fund(SpliceIn + 200_000);
        var start = StartSplice(harness);
        await PumpUntilAsync(harness, (_, m) => m is CommitmentSignedMessage);
        var lostAlice = (CommitmentSignedMessage)harness.Alice.Node.PeekNext()!;
        var lostBob = (CommitmentSignedMessage)harness.Bob.Node.PeekNext()!;

        // Act
        await harness.Harness.DisconnectAsync();
        await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        var spliceTxId = lostAlice.FundingTxIdTlv!.FundingTxId;
        foreach (var name in new[] { "Alice", "Bob" })
        {
            var reestablish = Reestablish(harness, mark, name);
            Assert.NotNull(reestablish.NextFundingTlv);
            Assert.Equal(spliceTxId, new TxId(reestablish.NextFundingTlv.NextFundingTxId));
            Assert.Equal(1, reestablish.NextFundingTlv.RetransmitFlags);
        }

        // (Alice's channel_reestablish reaches Bob first in the harness, so his commitment_signed leads)
        Assert.Equal(
        [
            "Alice:ChannelReestablish", "Bob:ChannelReestablish", "Bob:CommitmentSigned", "Alice:CommitmentSigned",
            "Bob:TxSignatures", "Alice:TxSignatures"
        ], Sequence(harness, mark, IsSigningStep));
        AssertRetransmittedCommitment(harness, lostAlice,
                                      Retransmitted<CommitmentSignedMessage>(harness, mark, "Alice"), taproot);
        AssertRetransmittedCommitment(harness, lostBob,
                                      Retransmitted<CommitmentSignedMessage>(harness, mark, "Bob"), taproot);
        if (taproot)
            AssertTaprootReestablishNonces(harness, mark, spliceTxId);
        AssertPendingOnBoth(harness, spliceTxId);
        await AssertUsableAsync(harness, spliceTxId);
    }

    /// <summary>
    /// SP-T-04 across a restart (wave sp2 integration, Proof SP2 (b)): both <c>commitment_signed</c> are lost and Alice
    /// restarts, so her splice service and interactive-tx driver no longer hold the negotiation. On
    /// <c>channel_reestablish</c> she resumes it from her stored rows: Bob's retransmitted <c>commitment_signed</c> is
    /// taken as the splice's (not as a commitment update), and both <c>tx_signatures</c> follow.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_BothCommitSigsLostAndTheInitiatorRestarted_When_Reconnected_Then_TheSpliceCompletes(bool taproot)
    {
        // Arrange (also on a simple taproot channel, NL-965: PR #1324 nonces through channel_reestablish)
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: taproot);
        harness.Alice.Fund(SpliceIn + 200_000);
        var start = StartSplice(harness);
        await PumpUntilAsync(harness, (_, m) => m is CommitmentSignedMessage);
        var lostAlice = (CommitmentSignedMessage)harness.Alice.Node.PeekNext()!;
        var spliceTxId = lostAlice.FundingTxIdTlv!.FundingTxId;

        // Act
        await harness.RestartAsync(harness.Alice);
        await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(harness.Alice.Driver.GetInfo(TwoNodeHarness.ChannelId));
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        Assert.Equal(
        [
            "Alice:ChannelReestablish", "Bob:ChannelReestablish", "Bob:CommitmentSigned", "Alice:CommitmentSigned",
            "Bob:TxSignatures", "Alice:TxSignatures"
        ], Sequence(harness, mark, IsSigningStep));
        AssertRetransmittedCommitment(harness, lostAlice,
                                      Retransmitted<CommitmentSignedMessage>(harness, mark, "Alice"), taproot);
        AssertPendingOnBoth(harness, spliceTxId);
        await AssertUsableAsync(harness, spliceTxId);
    }

    /// <summary>
    /// SP-T-04 across the accepter's restart (NL-496): both <c>commitment_signed</c> are lost and Bob, who did not start
    /// the splice, restarts. His negotiation is resumed from his stored rows on <c>channel_reestablish</c>: his
    /// <c>commitment_signed</c> is retransmitted byte for byte, Alice's is taken as the splice's, and he sends
    /// <c>tx_signatures</c> first.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_BothCommitSigsLostAndTheAccepterRestarted_When_Reconnected_Then_TheSpliceCompletes(bool taproot)
    {
        // Arrange (also on a simple taproot channel, NL-965: PR #1324 nonces through channel_reestablish)
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: taproot);
        harness.Alice.Fund(SpliceIn + 200_000);
        var start = StartSplice(harness);
        await PumpUntilAsync(harness, (_, m) => m is CommitmentSignedMessage);
        var lostAlice = (CommitmentSignedMessage)harness.Alice.Node.PeekNext()!;
        var lostBob = (CommitmentSignedMessage)harness.Bob.Node.PeekNext()!;
        var spliceTxId = lostBob.FundingTxIdTlv!.FundingTxId;

        // Act
        await harness.RestartAsync(harness.Bob);
        await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(harness.Bob.Driver.GetInfo(TwoNodeHarness.ChannelId));
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        foreach (var name in new[] { "Alice", "Bob" })
        {
            var reestablish = Reestablish(harness, mark, name);
            Assert.NotNull(reestablish.NextFundingTlv);
            Assert.Equal(spliceTxId, new TxId(reestablish.NextFundingTlv.NextFundingTxId));
            Assert.Equal(1, reestablish.NextFundingTlv.RetransmitFlags);
        }

        Assert.Equal(
        [
            "Alice:ChannelReestablish", "Bob:ChannelReestablish", "Bob:CommitmentSigned", "Alice:CommitmentSigned",
            "Bob:TxSignatures", "Alice:TxSignatures"
        ], Sequence(harness, mark, IsSigningStep));
        AssertRetransmittedCommitment(harness, lostAlice,
                                      Retransmitted<CommitmentSignedMessage>(harness, mark, "Alice"), taproot);
        AssertRetransmittedCommitment(harness, lostBob,
                                      Retransmitted<CommitmentSignedMessage>(harness, mark, "Bob"), taproot);
        if (taproot)
            AssertTaprootReestablishNonces(harness, mark, spliceTxId);
        AssertPendingOnBoth(harness, spliceTxId);
        await AssertUsableAsync(harness, spliceTxId);
    }

    #endregion

    #region SP-T-05 one side sent tx_signatures

    /// <summary>
    /// SP-T-05: Bob's <c>tx_signatures</c> (he signs first) is lost. Both ask with <c>next_funding</c> without bit 0;
    /// Bob retransmits his <c>tx_signatures</c> (identical), Alice then sends hers.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_TheFirstTxSignaturesLost_When_Reconnected_Then_ItIsRetransmittedAndTheSpliceCompletes(bool taproot)
    {
        // Arrange (also on a simple taproot channel, NL-965: PR #1324 nonces through channel_reestablish)
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: taproot);
        harness.Alice.Fund(SpliceIn + 200_000);
        var start = StartSplice(harness);
        await PumpUntilAsync(harness, (from, m) => from == "Bob" && m is TxSignaturesMessage);
        var lost = (TxSignaturesMessage)harness.Bob.Node.PeekNext()!;

        // Act
        await harness.Harness.DisconnectAsync();
        await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        foreach (var name in new[] { "Alice", "Bob" })
        {
            var reestablish = Reestablish(harness, mark, name);
            Assert.NotNull(reestablish.NextFundingTlv);
            Assert.Equal(lost.Payload.TxId, new TxId(reestablish.NextFundingTlv.NextFundingTxId));
            Assert.Equal(0, reestablish.NextFundingTlv.RetransmitFlags);
        }

        Assert.Equal(
        [
            "Alice:ChannelReestablish", "Bob:ChannelReestablish", "Bob:TxSignatures", "Alice:TxSignatures"
        ], Sequence(harness, mark, IsSigningStep));
        AssertSameBytes(harness, lost, Retransmitted<TxSignaturesMessage>(harness, mark, "Bob"));
        AssertPendingOnBoth(harness, lost.Payload.TxId);
        await AssertUsableAsync(harness, lost.Payload.TxId);
    }

    /// <summary>
    /// SP-T-05 across the sender's restart (NL-496): Bob's <c>tx_signatures</c> (the first) is lost and Bob restarts.
    /// Both still name FundingTx2 without bit 0; Bob's <c>tx_signatures</c> are rebuilt byte for byte from his stored
    /// row, Alice then signs.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_TheFirstTxSignaturesLostAndTheSenderRestarted_When_Reconnected_Then_ItIsRebuiltFromItsRow(bool taproot)
    {
        // Arrange (also on a simple taproot channel, NL-965: PR #1324 nonces through channel_reestablish)
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: taproot);
        harness.Alice.Fund(SpliceIn + 200_000);
        var start = StartSplice(harness);
        await PumpUntilAsync(harness, (from, m) => from == "Bob" && m is TxSignaturesMessage);
        var lost = (TxSignaturesMessage)harness.Bob.Node.PeekNext()!;

        // Act
        await harness.RestartAsync(harness.Bob);
        await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(harness.Bob.Driver.GetInfo(TwoNodeHarness.ChannelId));
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        foreach (var name in new[] { "Alice", "Bob" })
        {
            var reestablish = Reestablish(harness, mark, name);
            Assert.NotNull(reestablish.NextFundingTlv);
            Assert.Equal(lost.Payload.TxId, new TxId(reestablish.NextFundingTlv.NextFundingTxId));
            Assert.Equal(0, reestablish.NextFundingTlv.RetransmitFlags);
        }

        Assert.Equal(
        [
            "Alice:ChannelReestablish", "Bob:ChannelReestablish", "Bob:TxSignatures", "Alice:TxSignatures"
        ], Sequence(harness, mark, IsSigningStep));
        AssertSameBytes(harness, lost, Retransmitted<TxSignaturesMessage>(harness, mark, "Bob"));
        AssertPendingOnBoth(harness, lost.Payload.TxId);
        await AssertUsableAsync(harness, lost.Payload.TxId);
    }

    /// <summary>
    /// SP-T-05 across the waiting side's restart (NL-496; the shape of NL-600 against CLN): Bob's first
    /// <c>tx_signatures</c> is lost and Alice, who has sent her <c>commitment_signed</c> and waits for them, restarts.
    /// She resumes the negotiation from her stored row on <c>channel_reestablish</c>, takes Bob's retransmitted
    /// <c>tx_signatures</c> and signs her wallet inputs.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_TheFirstTxSignaturesLostAndTheReceiverRestarted_When_Reconnected_Then_SheSignsAfterResuming(bool taproot)
    {
        // Arrange (also on a simple taproot channel, NL-965: PR #1324 nonces through channel_reestablish)
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: taproot);
        harness.Alice.Fund(SpliceIn + 200_000);
        var start = StartSplice(harness);
        await PumpUntilAsync(harness, (from, m) => from == "Bob" && m is TxSignaturesMessage);
        var lost = (TxSignaturesMessage)harness.Bob.Node.PeekNext()!;
        var signCalls = harness.Alice.Contributor.SignCalls;

        // Act
        await harness.RestartAsync(harness.Alice);
        await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(harness.Alice.Driver.GetInfo(TwoNodeHarness.ChannelId));
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        Assert.Equal(
        [
            "Alice:ChannelReestablish", "Bob:ChannelReestablish", "Bob:TxSignatures", "Alice:TxSignatures"
        ], Sequence(harness, mark, IsSigningStep));
        AssertSameBytes(harness, lost, Retransmitted<TxSignaturesMessage>(harness, mark, "Bob"));
        Assert.Equal(signCalls + 1, harness.Alice.Contributor.SignCalls);
        AssertPendingOnBoth(harness, lost.Payload.TxId);
        await AssertUsableAsync(harness, lost.Payload.TxId);
    }

    #endregion

    #region SP-T-06 both sides sent tx_signatures

    /// <summary>
    /// SP-T-06: Alice's <c>tx_signatures</c> (the second) is lost: she is done, Bob is not. Only Bob sends
    /// <c>next_funding</c>; Alice retransmits her <c>tx_signatures</c> and Bob completes.
    /// </summary>
    [Fact]
    public async Task Given_TheSecondTxSignaturesLost_When_Reconnected_Then_OnlyItIsRetransmitted()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(SpliceIn + 200_000);
        var start = StartSplice(harness);
        await PumpUntilAsync(harness, (from, m) => from == "Alice" && m is TxSignaturesMessage, start);
        var lost = (TxSignaturesMessage)harness.Alice.Node.PeekNext()!;
        Assert.Equal(SpliceNegotiationState.Signed, (await start).State);

        // Act
        await harness.Harness.DisconnectAsync();
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        Assert.Null(Reestablish(harness, mark, "Alice").NextFundingTlv);
        var bobNextFunding = Reestablish(harness, mark, "Bob").NextFundingTlv;
        Assert.NotNull(bobNextFunding);
        Assert.Equal(0, bobNextFunding.RetransmitFlags);
        Assert.Equal(["Alice:ChannelReestablish", "Bob:ChannelReestablish", "Alice:TxSignatures"],
                     Sequence(harness, mark, IsSigningStep));
        AssertSameBytes(harness, lost, Retransmitted<TxSignaturesMessage>(harness, mark, "Alice"));
        AssertPendingOnBoth(harness, lost.Payload.TxId);
        await AssertUsableAsync(harness, lost.Payload.TxId);
    }

    /// <summary>
    /// SP-T-06 across a restart (SP-I7): Alice's second <c>tx_signatures</c> is lost and she restarts, so her
    /// interactive-tx driver no longer holds the negotiation. BOLT 2: having received Bob's <c>tx_signatures</c> she
    /// MUST send hers again; they are rebuilt byte for byte from her stored <c>InteractiveTxSessions</c> row and Bob
    /// completes the splice.
    /// </summary>
    [Fact]
    public async Task Given_TheSecondTxSignaturesLostAndTheSenderRestarted_When_Reconnected_Then_ItIsRebuiltFromItsRow()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(SpliceIn + 200_000);
        var start = StartSplice(harness);
        await PumpUntilAsync(harness, (from, m) => from == "Alice" && m is TxSignaturesMessage, start);
        var lost = (TxSignaturesMessage)harness.Alice.Node.PeekNext()!;
        Assert.Equal(SpliceNegotiationState.Signed, (await start).State);

        // Act
        await harness.RestartAsync(harness.Alice);
        Assert.Null(harness.Alice.Driver.GetInfo(TwoNodeHarness.ChannelId));
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: only Bob asks, Alice's tx_signatures come from her row, Bob completes
        Assert.Null(Reestablish(harness, mark, "Alice").NextFundingTlv);
        Assert.NotNull(Reestablish(harness, mark, "Bob").NextFundingTlv);
        Assert.Equal(["Alice:ChannelReestablish", "Bob:ChannelReestablish", "Alice:TxSignatures"],
                     Sequence(harness, mark, IsSigningStep));
        AssertSameBytes(harness, lost, Retransmitted<TxSignaturesMessage>(harness, mark, "Alice"));
        Assert.Empty(harness.Failures);
        Assert.True(Assert.Single(harness.Bob.Node.State.PendingFundings).FundingTxId == lost.Payload.TxId);
        Assert.Equal(lost.Payload.TxId, Assert.Single(harness.Bob.Broadcasts).TransactionId);
        Assert.Equal(harness.Alice.Broadcasts.Single().RawTransaction, harness.Bob.Broadcasts.Single().RawTransaction);
    }

    #endregion

    #region SP-T-07 both sent tx_signatures, then channel updates

    /// <summary>
    /// SP-T-07: after her <c>tx_signatures</c> Alice adds an HTLC and signs it (<c>start_batch</c> + one
    /// <c>commitment_signed</c> per funding); all of it is lost. On reconnection Alice retransmits her
    /// <c>tx_signatures</c>, then the update and the batch, and the exchange ends at the next commitment number on both
    /// fundings.
    /// </summary>
    [Fact]
    public async Task Given_TxSignaturesAndABatchLost_When_Reconnected_Then_TheyAreRetransmittedInOrder()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(SpliceIn + 200_000);
        var start = StartSplice(harness);
        await PumpUntilAsync(harness, (from, m) => from == "Alice" && m is TxSignaturesMessage, start);
        var spliceTxId = (await start).SpliceTxId!.Value;
        var number = harness.Alice.Node.State.LocalCommit.Number;
        await OfferWithoutDeliveryAsync(harness, harness.Alice, 20_000_000, 1);
        Assert.Equal(["TxSignatures", "UpdateAddHtlc", "StartBatch", "CommitmentSigned", "CommitmentSigned"],
                     Queued(harness.Alice));

        // Act
        await harness.Harness.DisconnectAsync();
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        Assert.Equal(
        [
            "Alice:ChannelReestablish", "Bob:ChannelReestablish", "Alice:TxSignatures", "Alice:UpdateAddHtlc",
            "Alice:StartBatch", "Alice:CommitmentSigned", "Alice:CommitmentSigned", "Bob:RevokeAndAck",
            "Bob:StartBatch", "Bob:CommitmentSigned", "Bob:CommitmentSigned", "Alice:RevokeAndAck"
        ], Sequence(harness, mark, IsFlowMessage));
        AssertPendingOnBoth(harness, spliceTxId);
        AssertCommitmentNumber(harness, number + 1);
        Assert.Single(harness.Bob.Node.State.Htlcs);
    }

    #endregion

    #region SP-T-08 concurrent splice_locked

    /// <summary>
    /// SP-T-08: Alice's <c>splice_locked</c> is lost; on reconnection her <c>my_current_funding_locked</c> names
    /// FundingTx2 and Bob takes it as her <c>splice_locked</c>. Bob reaches the depth while disconnected, locks, and his
    /// <c>splice_locked</c> is lost too; on the next reconnection his <c>my_current_funding_locked</c> lets Alice lock
    /// at once. An update afterwards is signed on FundingTx2 alone.
    /// </summary>
    [Fact]
    public async Task Given_SpliceLockedLostBothWays_When_Reconnected_Then_MyCurrentFundingLockedLocksBothSides()
    {
        // Arrange: a signed splice
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(SpliceIn + 200_000);
        var result = await harness.SpliceAsync(harness.Alice, SpliceIn);
        var fundingTx1 = harness.Alice.Node.State.Params.Funding!.FundingTxId;
        var fundingTx2 = result.SpliceTxId!.Value;

        // Act 1: Alice reaches the depth, her splice_locked is lost
        harness.Alice.Confirm(fundingTx2, TwoNodeHarness.BlockHeight + 3);
        await harness.Alice.DepthWatcher.WhenIdleAsync();
        await harness.WhenIdleAsync();
        Assert.IsType<SpliceLockedMessage>(harness.Alice.Node.PeekNext());
        await harness.Harness.DisconnectAsync();
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert 1: Alice's funding locked names FundingTx2, Bob's the funding he sent channel_ready for
        var aliceLocked = Reestablish(harness, mark, "Alice").MyCurrentFundingLockedTlv;
        var bobLocked = Reestablish(harness, mark, "Bob").MyCurrentFundingLockedTlv;
        Assert.Equal(fundingTx2, aliceLocked!.FundingTxId);
        Assert.Equal(fundingTx1, bobLocked!.FundingTxId);
        Assert.True(Assert.Single(harness.Bob.Node.State.PendingFundings).FundingTxId == fundingTx2);
        Assert.True(harness.Bob.FundingRows.Committed[fundingTx2].SpliceLockedReceived);
        Assert.DoesNotContain(harness.Transcript.Skip(mark), t => t.Message is SpliceLockedMessage);

        // Act 2: Bob reaches the depth while disconnected; his splice_locked goes nowhere, he locks
        await harness.Harness.DisconnectAsync();
        harness.Bob.Confirm(fundingTx2, TwoNodeHarness.BlockHeight + 3);
        await harness.Bob.DepthWatcher.WhenIdleAsync();
        await harness.WhenIdleAsync();
        Assert.Empty(harness.Bob.Node.State.PendingFundings);
        Assert.Single(harness.Alice.Node.State.PendingFundings);
        mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert 2: Bob's my_current_funding_locked locks Alice without a splice_locked
        Assert.Equal(fundingTx2, Reestablish(harness, mark, "Bob").MyCurrentFundingLockedTlv!.FundingTxId);
        Assert.DoesNotContain(harness.Transcript.Skip(mark), t => t.Message is SpliceLockedMessage);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Node.State.PendingFundings);
            Assert.Equal(fundingTx2, node.Node.State.Params.Funding!.FundingTxId);
        }

        // And an update is signed on FundingTx2 alone
        mark = harness.Transcript.Count;
        await OfferAsync(harness, harness.Alice, 10_000_000, 2);
        Assert.Equal(
        [
            "Alice:UpdateAddHtlc", $"Alice:CommitmentSigned:{fundingTx2}", "Bob:RevokeAndAck",
            $"Bob:CommitmentSigned:{fundingTx2}", "Alice:RevokeAndAck"
        ], Describe(harness, mark));
    }

    /// <summary>
    /// SP-T-08 across restarts (NL-496): Alice's <c>splice_locked</c> is lost and she restarts; her stored lock still
    /// names FundingTx2 in <c>my_current_funding_locked</c> and Bob takes it as her <c>splice_locked</c>. Bob then
    /// reaches the depth while disconnected, locks, and restarts before his <c>splice_locked</c> leaves; his
    /// <c>my_current_funding_locked</c> after the restart locks Alice. An update is signed on FundingTx2 alone.
    /// </summary>
    [Fact]
    public async Task Given_SpliceLockedLostBothWaysAndBothRestarted_When_Reconnected_Then_MyCurrentFundingLockedLocksBothSides()
    {
        // Arrange: a signed splice
        using var harness = new SpliceHarness(realEngine: true);
        harness.Alice.Fund(SpliceIn + 200_000);
        var result = await harness.SpliceAsync(harness.Alice, SpliceIn);
        var fundingTx1 = harness.Alice.Node.State.Params.Funding!.FundingTxId;
        var fundingTx2 = result.SpliceTxId!.Value;

        // Act 1: Alice reaches the depth, her splice_locked is lost with her restart
        harness.Alice.Confirm(fundingTx2, TwoNodeHarness.BlockHeight + 3);
        await harness.Alice.DepthWatcher.WhenIdleAsync();
        await harness.WhenIdleAsync();
        Assert.IsType<SpliceLockedMessage>(harness.Alice.Node.PeekNext());
        await harness.RestartAsync(harness.Alice);
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert 1
        Assert.Equal(fundingTx2, Reestablish(harness, mark, "Alice").MyCurrentFundingLockedTlv!.FundingTxId);
        Assert.Equal(fundingTx1, Reestablish(harness, mark, "Bob").MyCurrentFundingLockedTlv!.FundingTxId);
        Assert.True(harness.Bob.FundingRows.Committed[fundingTx2].SpliceLockedReceived);
        Assert.True(Assert.Single(harness.Alice.Node.State.PendingFundings).FundingTxId == fundingTx2);
        Assert.Empty(harness.Failures);

        // Act 2: Bob reaches the depth while disconnected, locks, and restarts
        await harness.Harness.DisconnectAsync();
        harness.Bob.Confirm(fundingTx2, TwoNodeHarness.BlockHeight + 3);
        await harness.Bob.DepthWatcher.WhenIdleAsync();
        await harness.WhenIdleAsync();
        Assert.Empty(harness.Bob.Node.State.PendingFundings);
        await harness.RestartAsync(harness.Bob);
        Assert.Empty(harness.Bob.Node.State.PendingFundings);
        Assert.Equal(fundingTx2, harness.Bob.Node.State.Params.Funding!.FundingTxId);
        mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert 2
        Assert.Equal(fundingTx2, Reestablish(harness, mark, "Bob").MyCurrentFundingLockedTlv!.FundingTxId);
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Node.State.PendingFundings);
            Assert.Equal(fundingTx2, node.Node.State.Params.Funding!.FundingTxId);
        }

        mark = harness.Transcript.Count;
        await OfferAsync(harness, harness.Alice, 10_000_000, 2);
        Assert.Equal(
        [
            "Alice:UpdateAddHtlc", $"Alice:CommitmentSigned:{fundingTx2}", "Bob:RevokeAndAck",
            $"Bob:CommitmentSigned:{fundingTx2}", "Alice:RevokeAndAck"
        ], Describe(harness, mark));
    }

    #endregion

    #region SP-T-09..11 after tx_signatures, channel updates

    /// <summary>
    /// SP-T-09: the splice is signed; Alice's <c>update_add_htlc</c> reaches Bob but her batch does not. Neither
    /// <c>channel_reestablish</c> carries <c>next_funding</c>, both <c>my_current_funding_locked</c> name FundingTx1
    /// (nothing locked yet); Alice retransmits the update and the batch, and the exchange ends at the next number.
    /// </summary>
    [Fact]
    public async Task Given_AfterTheSpliceOneSidesBatchLost_When_Reconnected_Then_TheUpdateAndBatchAreRetransmitted()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        var (fundingTx1, fundingTx2) = await SignedSpliceAsync(harness);
        var number = harness.Alice.Node.State.LocalCommit.Number;
        await OfferWithoutDeliveryAsync(harness, harness.Alice, 20_000_000, 1);
        await PumpUntilAsync(harness, (from, m) => from == "Alice" && m is StartBatchMessage);

        // Act
        await harness.Harness.DisconnectAsync();
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        AssertNoNextFunding(harness, mark, fundingTx1);
        Assert.Equal(
        [
            "Alice:ChannelReestablish", "Bob:ChannelReestablish", "Alice:UpdateAddHtlc", "Alice:StartBatch",
            "Alice:CommitmentSigned", "Alice:CommitmentSigned", "Bob:RevokeAndAck", "Bob:StartBatch",
            "Bob:CommitmentSigned", "Bob:CommitmentSigned", "Alice:RevokeAndAck"
        ], Sequence(harness, mark, IsFlowMessage));
        AssertPendingOnBoth(harness, fundingTx2);
        AssertCommitmentNumber(harness, number + 1);
    }

    /// <summary>
    /// SP-T-10: both sides signed the update; Bob's <c>revoke_and_ack</c> and his batch are lost. Bob retransmits both
    /// in their original order and Alice revokes.
    /// </summary>
    [Fact]
    public async Task Given_TheRevokeAndAckAndBatchLost_When_Reconnected_Then_BothAreRetransmittedInOrder()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        var (fundingTx1, fundingTx2) = await SignedSpliceAsync(harness);
        var number = harness.Alice.Node.State.LocalCommit.Number;
        await OfferWithoutDeliveryAsync(harness, harness.Alice, 20_000_000, 1);
        await PumpUntilAsync(harness, (from, m) => from == "Bob" && m is RevokeAndAckMessage);
        Assert.Equal(["RevokeAndAck", "StartBatch", "CommitmentSigned", "CommitmentSigned"], Queued(harness.Bob));

        // Act
        await harness.Harness.DisconnectAsync();
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        AssertNoNextFunding(harness, mark, fundingTx1);
        Assert.Equal(
        [
            "Alice:ChannelReestablish", "Bob:ChannelReestablish", "Bob:RevokeAndAck", "Bob:StartBatch",
            "Bob:CommitmentSigned", "Bob:CommitmentSigned", "Alice:RevokeAndAck"
        ], Sequence(harness, mark, IsFlowMessage));
        AssertPendingOnBoth(harness, fundingTx2);
        AssertCommitmentNumber(harness, number + 1);
    }

    /// <summary>
    /// SP-T-11: Alice received Bob's <c>revoke_and_ack</c> but his batch is lost. Bob retransmits the batch alone and
    /// Alice revokes.
    /// </summary>
    [Fact]
    public async Task Given_OnlyTheBatchLost_When_Reconnected_Then_ItIsRetransmitted()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true);
        var (fundingTx1, fundingTx2) = await SignedSpliceAsync(harness);
        var number = harness.Alice.Node.State.LocalCommit.Number;
        await OfferWithoutDeliveryAsync(harness, harness.Alice, 20_000_000, 1);
        await PumpUntilAsync(harness, (from, m) => from == "Bob" && m is StartBatchMessage);

        // Act
        await harness.Harness.DisconnectAsync();
        var mark = harness.Transcript.Count;
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert
        AssertNoNextFunding(harness, mark, fundingTx1);
        Assert.Equal(
        [
            "Alice:ChannelReestablish", "Bob:ChannelReestablish", "Bob:StartBatch", "Bob:CommitmentSigned",
            "Bob:CommitmentSigned", "Alice:RevokeAndAck"
        ], Sequence(harness, mark, IsFlowMessage));
        AssertPendingOnBoth(harness, fundingTx2);
        AssertCommitmentNumber(harness, number + 1);
    }

    #endregion

    #region SP-I7 a crash at every save

    /// <summary>
    /// SP2-A-T3's crash variant (SP-I7, NL-496): the node dies instead of performing its n-th save of a splice-in, for
    /// every save it makes from Alice's request until the splice is signed on both sides. Nothing of that save persists
    /// and nothing it would have sent leaves (the restart drops the outboxes both ways); the node restarts on what it
    /// saved and reconnects. Whatever the crash point, both sides end up agreeing: the same pending splice (the same
    /// transaction broadcast by both) or none, no failure after the restart, and a payment over the channel works on
    /// every active funding.
    /// </summary>
    [Theory]
    [InlineData("Alice", false)]
    [InlineData("Bob", false)]
    [InlineData("Alice", true)]
    [InlineData("Bob", true)]
    public async Task Given_ACrashAtEverySpliceSave_When_TheNodeRestarts_Then_BothSidesAgreeAndTheChannelWorks(
        string crashing, bool taproot)
    {
        // A simple taproot channel too (NL-965, D-T4): the shared input's signing nonce dies with the process, the
        // stored partial signature does not, and no MuSig2 nonce signs twice (the signer would refuse)
        var saves = await MeasureSpliceSavesAsync(crashing, taproot);
        Assert.InRange(saves, 2, 200);
        for (var crashAt = 1; crashAt <= saves; crashAt++)
        {
            // Arrange
            var context = $"{crashing} crashed at save {crashAt} of {saves} (taproot {taproot})";
            using var harness = new SpliceHarness(realEngine: true, simpleTaproot: taproot);
            harness.Alice.Fund(SpliceIn + 200_000);
            var node = crashing == "Alice" ? harness.Alice : harness.Bob;
            node.Node.Store.CrashAtSave = node.Node.Store.Saves + crashAt;

            // Act
            var start = StartSplice(harness);
            var failuresAtRestart = await PumpThroughCrashAsync(harness, node, context);
            // The request dies with Alice's process when she crashes (its task is abandoned); otherwise it ends
            if (crashing == "Bob")
            {
                try
                {
                    await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }
                catch (Exception e) when (e is not TimeoutException)
                {
                    // A failed request: the reconnection decides the splice
                }
            }
            else
            {
                _ = start.ContinueWith(t => t.Exception, TaskScheduler.Default);
            }

            // Assert
            Assert.True(failuresAtRestart is not null, $"{context}: the node never crashed");
            Assert.True(harness.Failures.Count == failuresAtRestart,
                        $"{context}: {string.Join(" | ", harness.Failures.Skip(failuresAtRestart.Value)
                                                                 .Select(f => $"{f.Node}: {f.Exception.Message}"))}");
            var alicePending = harness.Alice.Node.State.PendingFundings.Select(f => f.FundingTxId).ToList();
            var bobPending = harness.Bob.Node.State.PendingFundings.Select(f => f.FundingTxId).ToList();
            Assert.True(alicePending.SequenceEqual(bobPending),
                        $"{context}: Alice pends [{string.Join(", ", alicePending)}], Bob [{string.Join(", ", bobPending)}]");
            foreach (var spliceNode in new[] { harness.Alice, harness.Bob })
                Assert.True(spliceNode.Node.Channel.State == ChannelState.Open, $"{context}: {spliceNode.Name} is "
                                                                              + spliceNode.Node.Channel.State);

            var pending = alicePending.Count == 0 ? (TxId?)null : Assert.Single(alicePending);
            if (pending is { } spliceTxId)
            {
                var aliceTx = harness.Alice.Broadcasts.Where(b => b.TransactionId == spliceTxId).ToList();
                var bobTx = harness.Bob.Broadcasts.Where(b => b.TransactionId == spliceTxId).ToList();
                Assert.True(aliceTx.Count > 0 && bobTx.Count > 0, $"{context}: the splice is not broadcast by both");
                Assert.Equal(aliceTx[0].RawTransaction, bobTx[0].RawTransaction);
            }

            await AssertUsableAsync(harness, pending);
        }
    }

    /// <summary>How many saves <paramref name="crashing"/> makes from Alice's splice-in request until both signed.</summary>
    private static async Task<int> MeasureSpliceSavesAsync(string crashing, bool taproot = false)
    {
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: taproot);
        harness.Alice.Fund(SpliceIn + 200_000);
        var node = crashing == "Alice" ? harness.Alice : harness.Bob;
        var before = node.Node.Store.Saves;
        var result = await harness.SpliceAsync(harness.Alice, SpliceIn);
        await harness.PumpAsync();
        Assert.Equal(SpliceNegotiationState.Signed, result.State);
        return node.Node.Store.Saves - before;
    }

    /// <summary>
    /// Pumps the exchange; once <paramref name="crashing"/>'s store crashed, nothing more of the dead process is
    /// delivered: it restarts on what it saved (both outboxes are lost with the link) and reconnects, and the pump goes
    /// on until both sides are quiet. Returns the failure count at the restart (null when the node never crashed).
    /// </summary>
    private static async Task<int?> PumpThroughCrashAsync(SpliceHarness harness, SpliceNode crashing, string context)
    {
        int? failuresAtRestart = null;
        var quietRounds = 0;
        for (var round = 0; round < 4_000; round++)
        {
            await IdleThroughCrashAsync(harness);
            if (crashing.Node.Store.Crashed)
            {
                await harness.RestartAsync(crashing);
                failuresAtRestart = harness.Failures.Count;
                await harness.Harness.ReconnectAsync();
                quietRounds = 0;
                continue;
            }

            var moved = false;
            foreach (var node in new[] { harness.Alice, harness.Bob })
            {
                if (crashing.Node.Store.Crashed)
                    break;

                try
                {
                    moved |= await TryDeliverAsync(harness, node, static (_, _) => false);
                }
                catch (SimulatedCrashException)
                {
                    moved = true;
                }
            }

            if (moved || crashing.Node.Store.Crashed)
            {
                quietRounds = 0;
                continue;
            }

            if (!harness.Alice.Node.OutboxIsEmpty || !harness.Bob.Node.OutboxIsEmpty)
                continue;

            // A background continuation (after a save, a quiescence end) may still publish
            if (++quietRounds >= 5)
                return failuresAtRestart;

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"{context}: the exchange did not converge");
    }

    private static async Task IdleThroughCrashAsync(SpliceHarness harness)
    {
        try
        {
            await harness.WhenIdleAsync();
        }
        catch (SimulatedCrashException)
        {
            // A background continuation died with its process
        }
    }

    #endregion

    #region Helpers

    private static Task<SpliceResult> StartSplice(SpliceHarness harness) =>
        harness.Alice.Service.StartAsync(new SpliceRequest(TwoNodeHarness.ChannelId, SpliceIn,
                                                           SpliceHarness.FeeratePerKw),
                                         TestContext.Current.CancellationToken);

    /// <summary>A splice-in by Alice signed on both sides (pending), and the two fundings.</summary>
    private static async Task<(TxId FundingTx1, TxId FundingTx2)> SignedSpliceAsync(SpliceHarness harness)
    {
        harness.Alice.Fund(SpliceIn + 200_000);
        var fundingTx1 = harness.Alice.Node.State.Params.Funding!.FundingTxId;
        var result = await harness.SpliceAsync(harness.Alice, SpliceIn);
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        return (fundingTx1, result.SpliceTxId!.Value);
    }

    /// <summary>
    /// Pumps the exchange (a held message is never delivered) inside a bounded wait until
    /// <paramref name="condition"/> holds: the assertions that follow wait for the messages a background
    /// continuation still owes (published once the exchange already went quiet) instead of racing them (NL-513).
    /// </summary>
    /// <exception cref="TimeoutException">Still false after half a minute.</exception>
    private static async Task PumpUntilAsync(SpliceHarness harness, Func<string, IChannelMessage, bool> hold,
                                             Func<bool> condition, string description)
    {
        await WaitFor.TrueAsync(async () =>
        {
            await harness.WhenIdleAsync();
            await TryDeliverAsync(harness, harness.Alice, hold);
            await TryDeliverAsync(harness, harness.Bob, hold);
            return condition();
        }, TimeSpan.FromSeconds(30), description, TestContext.Current.CancellationToken);
    }

    /// <summary>Both sides sent their channel_reestablish since <paramref name="mark"/>.</summary>
    private static bool Reestablished(SpliceHarness harness, int mark) =>
        harness.Transcript.Skip(mark).Any(t => t.From == "Alice" && t.Message is ChannelReestablishMessage)
     && harness.Transcript.Skip(mark).Any(t => t.From == "Bob" && t.Message is ChannelReestablishMessage);

    /// <summary>
    /// Delivers messages both ways, as <see cref="SpliceHarness.PumpAsync"/> does, except that a node whose next message
    /// matches <paramref name="hold"/> delivers nothing more; returns when nothing else can move and
    /// <paramref name="until"/> (if any) completed.
    /// </summary>
    private static async Task PumpUntilAsync(SpliceHarness harness, Func<string, IChannelMessage, bool> hold,
                                             Task? until = null)
    {
        var quietRounds = 0;
        for (var round = 0; round < 2_000; round++)
        {
            await harness.WhenIdleAsync();
            var aliceSent = await TryDeliverAsync(harness, harness.Alice, hold);
            var bobSent = await TryDeliverAsync(harness, harness.Bob, hold);
            if (aliceSent || bobSent)
            {
                quietRounds = 0;
                continue;
            }

            await harness.WhenIdleAsync();
            if (CanDeliver(harness.Alice, hold) || CanDeliver(harness.Bob, hold))
                continue;

            // A message may still be raised by a background continuation (after a save, a quiescence end)
            if (++quietRounds >= 5 && (until is null || until.IsCompleted))
                return;

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("The exchange did not reach the held message");
    }

    private static bool CanDeliver(SpliceNode node, Func<string, IChannelMessage, bool> hold) =>
        node.Node.PeekNext() is { } next && !hold(node.Name, next);

    /// <summary>As the peer's inbound loop: a <c>start_batch</c> and its members go to the channel manager as one.</summary>
    private static async Task<bool> TryDeliverAsync(SpliceHarness harness, SpliceNode node,
                                                    Func<string, IChannelMessage, bool> hold)
    {
        var from = node.Node;
        if (from.PeekNext() is not { } next || hold(node.Name, next))
            return false;

        try
        {
            if (next is not StartBatchMessage startBatch)
                return await from.DeliverNextAsync();

            from.TryTakeNext(out _);
            var members = new List<CommitmentSignedMessage>();
            for (var i = 0; i < startBatch.Payload.BatchSize; i++)
            {
                Assert.True(from.TryTakeNext(out var member));
                members.Add((CommitmentSignedMessage)member);
            }

            await from.Peer.ChannelManager.HandleCommitmentSignedBatchAsync(
                new CommitmentSignedBatch(startBatch.Payload.ChannelId, members), from.NegotiatedFeatures, from.NodeId);
            return true;
        }
        catch (Exception e) when (e is WarningException or ChannelErrorException)
        {
            harness.Failures.Enqueue((from.Peer.Name, e));
            return true;
        }
    }

    private static IChannelMessage? SecondQueued(SpliceNode node) =>
        QueuedMessages(node).Skip(1).FirstOrDefault();

    private static List<string> Queued(SpliceNode node) =>
        QueuedMessages(node).Select(m => Enum.GetName(m.Type)!).ToList();

    /// <summary>The node's outbox, oldest first (read through the harness's private queue).</summary>
    private static IEnumerable<IChannelMessage> QueuedMessages(SpliceNode node)
    {
        var field = typeof(HarnessNode).GetField("_outbox",
                                                 System.Reflection.BindingFlags.NonPublic
                                               | System.Reflection.BindingFlags.Instance)!;
        return (IEnumerable<IChannelMessage>)field.GetValue(node.Node)!;
    }

    private static async Task OfferWithoutDeliveryAsync(SpliceHarness harness, SpliceNode from, ulong amountMsat,
                                                        int tag)
    {
        var preimage = TwoNodeHarness.Preimage(tag);
        var hash = TwoNodeHarness.Hash(preimage);
        await from.Node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId, LightningMoney.MilliSatoshis(amountMsat),
                                                  hash, CltvExpiry, s_onion, null, HtlcOrigin.Local(hash),
                                                  TestContext.Current.CancellationToken);
        await harness.WhenIdleAsync();
    }

    private static async Task OfferAsync(SpliceHarness harness, SpliceNode from, ulong amountMsat, int tag)
    {
        await OfferWithoutDeliveryAsync(harness, from, amountMsat, tag);
        await harness.PumpAsync();
    }

    private static ChannelReestablishMessage Reestablish(SpliceHarness harness, int mark, string from) =>
        (ChannelReestablishMessage)harness.Transcript.Skip(mark)
                                          .Single(t => t.From == from && t.Message is ChannelReestablishMessage)
                                          .Message;

    private static T Retransmitted<T>(SpliceHarness harness, int mark, string from) where T : IChannelMessage =>
        harness.Transcript.Skip(mark).Where(t => t.From == from).Select(t => t.Message).OfType<T>().First();

    private static bool IsSigningStep(IChannelMessage message) =>
        message.Type is MessageTypes.ChannelReestablish or MessageTypes.CommitmentSigned or MessageTypes.TxSignatures
                     or MessageTypes.TxAbort;

    private static bool IsFlowMessage(IChannelMessage message) =>
        message.Type is MessageTypes.ChannelReestablish or MessageTypes.TxSignatures or MessageTypes.UpdateAddHtlc
                     or MessageTypes.StartBatch or MessageTypes.CommitmentSigned or MessageTypes.RevokeAndAck
                     or MessageTypes.TxAbort or MessageTypes.SpliceLocked;

    private static List<string> Sequence(SpliceHarness harness, int mark, Func<IChannelMessage, bool>? filter = null) =>
        harness.Transcript.Skip(mark)
               .Where(t => filter?.Invoke(t.Message) ?? true)
               .Select(t => $"{t.From}:{Enum.GetName(t.Message.Type)}")
               .ToList();

    /// <summary>The commitment-flow messages raised since <paramref name="mark"/>, with each CS's funding txid.</summary>
    private static List<string> Describe(SpliceHarness harness, int mark) =>
        harness.Transcript.Skip(mark)
               .Where(t => t.Message.Type is MessageTypes.UpdateAddHtlc or MessageTypes.StartBatch
                                          or MessageTypes.CommitmentSigned or MessageTypes.RevokeAndAck)
               .Select(t => t.Message is CommitmentSignedMessage cs
                                ? $"{t.From}:CommitmentSigned:{cs.FundingTxIdTlv?.FundingTxId}"
                                : $"{t.From}:{Enum.GetName(t.Message.Type)}")
               .ToList();

    /// <summary>
    /// A retransmitted splice <c>commitment_signed</c>: byte-identical to the lost original (SP2-A-T2), or for a simple
    /// taproot channel signed again with a fresh signing nonce, never replayed (BOLTs PR #1324, NL-965).
    /// </summary>
    private static void AssertRetransmittedCommitment(SpliceHarness harness, CommitmentSignedMessage original,
                                                      CommitmentSignedMessage again, bool taproot)
    {
        if (!taproot)
        {
            AssertSameBytes(harness, original, again);
            return;
        }

        Assert.Equal(original.FundingTxIdTlv!.FundingTxId, again.FundingTxIdTlv!.FundingTxId);
        Assert.True(again.Payload.Signature.IsZero);
        Assert.NotEqual(original.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce.PublicNonce,
                        again.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce.PublicNonce);
    }

    /// <summary>
    /// Both <c>channel_reestablish</c> of a taproot channel name the splice in negotiation in <c>next_local_nonces</c>
    /// and carry <c>current_commit_nonce</c> while the peer's splice <c>commitment_signed</c> is missing (PR #1324).
    /// </summary>
    private static void AssertTaprootReestablishNonces(SpliceHarness harness, int mark, TxId spliceTxId)
    {
        foreach (var name in new[] { "Alice", "Bob" })
        {
            var reestablish = Reestablish(harness, mark, name);
            Assert.Contains(reestablish.NextLocalNoncesTlv!.Nonces.Entries, e => e.FundingTxId == spliceTxId);
            Assert.NotNull(reestablish.CurrentCommitNonceTlv);
        }
    }

    /// <summary>The retransmission is byte-identical to the lost original (SP2-A-T2).</summary>
    private static void AssertSameBytes(SpliceHarness harness, IChannelMessage original, IChannelMessage again)
    {
        var serializer = harness.Alice.Node.Services
                                .GetService(typeof(Domain.Serialization.Interfaces.IMessageSerializer))
                             as Domain.Serialization.Interfaces.IMessageSerializer;
        Assert.NotNull(serializer);
        Assert.Equal(Serialize(serializer, original), Serialize(serializer, again));
    }

    private static byte[] Serialize(Domain.Serialization.Interfaces.IMessageSerializer serializer,
                                    IChannelMessage message)
    {
        using var stream = new MemoryStream();
        serializer.SerializeAsync(message, stream).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    /// <summary>Neither side has an interactive transaction to finish, and both name FundingTx1 (not locked yet).</summary>
    private static void AssertNoNextFunding(SpliceHarness harness, int mark, TxId fundingTx1)
    {
        foreach (var name in new[] { "Alice", "Bob" })
        {
            var reestablish = Reestablish(harness, mark, name);
            Assert.Null(reestablish.NextFundingTlv);
            Assert.Equal(fundingTx1, reestablish.MyCurrentFundingLockedTlv!.FundingTxId);
        }
    }

    private static void AssertPendingOnBoth(SpliceHarness harness, TxId spliceTxId)
    {
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Equal(ChannelState.Open, node.Node.Channel.State);
            Assert.True(Assert.Single(node.Node.State.PendingFundings).FundingTxId == spliceTxId, node.Name);
            Assert.True(node.FundingRows.CommittedLocal.ContainsKey(spliceTxId), node.Name);
            Assert.Equal(spliceTxId, Assert.Single(node.Broadcasts).TransactionId);
        }

        Assert.Equal(harness.Alice.Broadcasts.Single().RawTransaction, harness.Bob.Broadcasts.Single().RawTransaction);
    }

    private static void AssertCommitmentNumber(SpliceHarness harness, ulong number)
    {
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Equal(number, node.Node.State.LocalCommit.Number);
            Assert.Equal(number, node.Node.State.RemoteCommit.Number);
            Assert.Null(node.Node.State.RemoteNextCommit);
        }
    }

    /// <summary>An HTLC after the reconnection is signed on every active funding (a batch when one is pending).</summary>
    private static async Task AssertUsableAsync(SpliceHarness harness, TxId? pendingSplice)
    {
        var mark = harness.Transcript.Count;
        await OfferAsync(harness, harness.Alice, 10_000_000, 9);
        Assert.Empty(harness.Failures);
        var expected = pendingSplice is null
                           ? new List<string> { "Alice:UpdateAddHtlc", "Alice:CommitmentSigned", "Bob:RevokeAndAck" }
                           : ["Alice:UpdateAddHtlc", "Alice:StartBatch", "Alice:CommitmentSigned", "Alice:CommitmentSigned",
                              "Bob:RevokeAndAck"];
        Assert.Equal(expected, Sequence(harness, mark).Take(expected.Count).ToList());
    }

    #endregion
}