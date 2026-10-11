using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.InteractiveTx;

using Application.InteractiveTx;
using Application.InteractiveTx.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Messages;
using TestDoubles;

/// <summary>
/// Persist, then update memory, then send; restarts and reconnections (<c>next_funding</c>) of a stored negotiation;
/// the driver's own IT-RBF-01 double-spend check.
/// </summary>
public class InteractiveTxRecoveryTests
{
    private const uint FeeratePerKw = 1_000;
    private static readonly ChannelId s_channelId = InteractiveTxHarness.ChannelId;

    public static TheoryData<string> Engines => InteractiveTxEngines.All;

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_TheSaveOfTheLastSignaturesFails_When_Redelivered_Then_CompletedOnceWithTheDatabase(
        string engine)
    {
        // Arrange: Bob signs first (smaller inputs); Alice's save of the completed negotiation fails
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        var quiescence = new Mock<IQuiescenceService>();
        harness.Alice.Configure(quiescence.Object);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        var undelivered = await harness.PumpAsync(harness.Alice, await harness.StartAsync(FeeratePerKw, ct), ct,
                                                  (from, m) => m is TxSignaturesMessage && from == harness.Bob);
        var bobSignatures = undelivered.Single(u => u.Message is TxSignaturesMessage).Message;
        harness.Alice.FailNextSave = true;

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.Driver.ReceiveAsync(bobSignatures, harness.Bob.NodeId, harness.Alice.UnitOfWork, ct));

        // Assert: memory still matches the database (nothing sent, the negotiation not over, quiescence kept)
        Assert.True(harness.Alice.Driver.IsNegotiating(s_channelId));
        Assert.Empty(harness.Alice.Driver.GetInfo(s_channelId)!.CompletedAttempts);
        Assert.Equal(InteractiveTxSessionState.AwaitingTxSignatures,
                     harness.Alice.StoredSession(s_channelId)!.State);
        Assert.False(harness.Alice.StoredSession(s_channelId)!.TxSignaturesSent);
        quiescence.Verify(q => q.Terminate(s_channelId, It.IsAny<QuiescenceEndReason>()), Times.Never);

        // Act: the same tx_signatures handled again (e.g. after the reestablish) completes the exchange
        var replies = await harness.Alice.Driver.ReceiveAsync(bobSignatures, harness.Bob.NodeId,
                                                              harness.Alice.UnitOfWork, ct);
        await harness.PumpAsync(harness.Alice, replies, ct);

        // Assert
        Assert.IsType<TxSignaturesMessage>(Assert.Single(replies));
        Assert.False(harness.Alice.Driver.IsNegotiating(s_channelId));
        Assert.Single(harness.Alice.Driver.GetInfo(s_channelId)!.CompletedAttempts);
        Assert.Equal(InteractiveTxSessionState.Signed, harness.Alice.StoredSession(s_channelId)!.State);
        Assert.Single(harness.Bob.Host.Completions);
        quiescence.Verify(q => q.Terminate(s_channelId, QuiescenceEndReason.TxSignaturesExchanged), Times.Once);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_TheSaveAfterCommitmentSignedFails_When_Retried_Then_OurSignaturesGoOutOnce(string engine)
    {
        // Arrange: Bob signs first, so the peer's commitment_signed makes him send tx_signatures in the same save
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(FeeratePerKw, ct), ct,
                                (from, m) => m is TestCommitmentSignedMessage && from == harness.Alice);
        harness.Bob.FailNextSave = true;

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Bob.Driver.OnCommitmentSignedReceivedAsync(s_channelId, harness.Bob.UnitOfWork, ct));
        var replies = await harness.Bob.Driver.OnCommitmentSignedReceivedAsync(s_channelId, harness.Bob.UnitOfWork,
                                                                               ct);

        // Assert
        Assert.IsType<TxSignaturesMessage>(Assert.Single(replies));
        var stored = harness.Bob.StoredSession(s_channelId)!;
        Assert.True(stored is { CommitmentSignedReceived: true, TxSignaturesSent: true });
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_ARestartAfterCompletion_When_ResumedWithTheStoredRows_Then_TheRbfRulesStillApply(
        string engine)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(FeeratePerKw, ct), ct);
        var rows = harness.Alice.Repository.Committed.Values.ToList();
        var signed = Assert.Single(rows);
        var sentSignatures = (TxSignaturesMessage)harness.Transcript
                                                         .First(t => t is { From: "alice", Message: TxSignaturesMessage })
                                                         .Message;

        // Act
        harness.Alice.Restart();
        await harness.Alice.Driver.ResumeAsync(signed, harness.Alice.Terms(s_channelId, harness.Bob, true,
                                                                           FeeratePerKw),
                                               harness.Alice.Host, ct, rows);

        // Assert: remembered as a completed attempt, not a negotiation in progress
        Assert.False(harness.Alice.Driver.IsNegotiating(s_channelId));
        Assert.Single(harness.Alice.Driver.GetInfo(s_channelId)!.CompletedAttempts);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.Driver.RequestRbfAsync(harness.Alice.Terms(s_channelId, harness.Bob, true, 1_040),
                                                       harness.Alice.Host.LocalOutputShare, ct));
        var retransmission = harness.Alice.Driver.CreateTxSignaturesRetransmission(s_channelId,
                                                                                    signed.ConstructedTx!.TxId);
        Assert.NotNull(retransmission);
        Assert.Equal(sentSignatures.Payload.TxId, retransmission.Payload.TxId);
        Assert.Equal(sentSignatures.Payload.Witnesses, retransmission.Payload.Witnesses);
        Assert.Equal(new[] { s_channelId }, harness.Alice.Driver.GetChannels(harness.Bob.NodeId));
        Assert.Empty(harness.Alice.Driver.GetChannels(harness.Alice.NodeId));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_OurTxSignaturesLostInADisconnection_When_ResumedAndRetransmitted_Then_Completed(
        string engine)
    {
        // Arrange: Bob (signs first) sent tx_signatures, which the disconnection lost; Bob restarts
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        var undelivered = await harness.PumpAsync(harness.Alice, await harness.StartAsync(FeeratePerKw, ct), ct,
                                                  (from, m) => m is TxSignaturesMessage && from == harness.Bob);
        var lost = (TxSignaturesMessage)undelivered.Single(u => u.Message is TxSignaturesMessage).Message;
        var stored = harness.Bob.StoredSession(s_channelId)!;
        Assert.True(stored.TxSignaturesSent);
        harness.Bob.Restart();
        await harness.Bob.Driver.ResumeAsync(stored, harness.Bob.Terms(s_channelId, harness.Alice, false,
                                                                       FeeratePerKw),
                                             harness.Bob.Host, ct);
        Assert.Null(harness.Bob.Driver.CreateTxSignaturesRetransmission(s_channelId,
                                                                        new TxId(new byte[32])));

        // Act (BOLT 2 channel_reestablish next_funding: MUST send its tx_signatures)
        var retransmission = harness.Bob.Driver.CreateTxSignaturesRetransmission(s_channelId,
                                                                                  stored.ConstructedTx!.TxId);
        await harness.PumpAsync(harness.Bob, [retransmission!], ct);

        // Assert
        Assert.Equal(lost.Payload.Witnesses, retransmission!.Payload.Witnesses);
        Assert.Single(harness.Alice.Host.Completions);
        Assert.Single(harness.Bob.Host.Completions);
        Assert.Equal(InteractiveTxSessionState.Signed, harness.Bob.StoredSession(s_channelId)!.State);
    }

    [Fact]
    public async Task Given_AnEngineWithoutTheRbfRule_When_TheRbfDoesNotDoubleSpend_Then_TheDriverAborts()
    {
        // Arrange (IT-RBF-01: the sender MUST ensure that the new transaction double-spends all other attempts)
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(new NoRbfRuleInteractiveTxEngine(), 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Alice.Fund(310_000);
        harness.Bob.Fund(200_000);
        harness.Bob.Fund(210_000);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(FeeratePerKw, ct), ct);
        harness.Bob.Host.RbfHandler = (message, _) =>
            InteractiveTxRbfDecision.Accept(harness.Bob.Terms(s_channelId, harness.Alice, false,
                                                              message.Payload.Feerate),
                                            harness.Bob.Host.LocalOutputShare);

        // Act: both sides pick new wallet outputs
        var initRbf = await harness.Alice.Driver.RequestRbfAsync(
                          harness.Alice.Terms(s_channelId, harness.Bob, true, 1_100),
                          harness.Alice.Host.LocalOutputShare, ct);
        await harness.PumpAsync(harness.Alice, initRbf, ct, maxMessages: 100);

        // Assert: nothing signed for the second attempt, the first is kept
        Assert.Contains(harness.Transcript, t => t.Message is TxAbortMessage abort
                                              && System.Text.Encoding.ASCII.GetString(abort.Payload.Data)
                                                    .Contains("IT-RBF-01"));
        Assert.Single(harness.Alice.Host.Completions);
        Assert.Single(harness.Bob.Host.Completions);
        Assert.Equal(1, harness.Transcript.Count(t => t is { From: "alice", Message: TxSignaturesMessage }));
        Assert.Equal(1, harness.Transcript.Count(t => t is { From: "bob", Message: TxSignaturesMessage }));
        foreach (var node in new[] { harness.Alice, harness.Bob })
            Assert.Single(node.Driver.GetInfo(s_channelId)!.CompletedAttempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_TheEngineRefusesOurRbfTerms_When_TheAttemptWouldStart_Then_TxAbortNotAnException(
        bool initiatorRefuses)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(InteractiveTxEngines.Reference, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Alice.Fund(310_000);
        harness.Bob.Fund(200_000);
        harness.Bob.Fund(210_000);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(FeeratePerKw, ct), ct);
        var refusing = initiatorRefuses ? harness.Alice : harness.Bob;
        refusing.ReplaceDriver(new InteractiveTxDriver(new RefusingRbfInteractiveTxEngine(), refusing.Builder,
                                                       refusing.Contributor, refusing.Inspector,
                                                       NullLogger<InteractiveTxDriver>.Instance));
        await refusing.Driver.ResumeAsync(refusing.StoredSession(s_channelId)!,
                                          refusing.Terms(s_channelId, harness.Other(refusing), initiatorRefuses,
                                                         FeeratePerKw), refusing.Host, ct);
        harness.Bob.Host.RbfHandler = (message, _) =>
            InteractiveTxRbfDecision.Accept(harness.Bob.Terms(s_channelId, harness.Alice, false,
                                                              message.Payload.Feerate),
                                            harness.Bob.Host.LocalOutputShare);

        // Act
        var initRbf = await harness.Alice.Driver.RequestRbfAsync(
                          harness.Alice.Terms(s_channelId, harness.Bob, true, 1_100),
                          harness.Alice.Host.LocalOutputShare, ct);
        await harness.PumpAsync(harness.Alice, initRbf, ct, maxMessages: 100);

        // Assert: the refusing side answered tx_abort, its fresh reservation released, nobody negotiates
        Assert.Contains(harness.Transcript, t => t.From == refusing.Name && t.Message is TxAbortMessage);
        Assert.Single(refusing.Contributor.Released);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.False(node.Driver.IsNegotiating(s_channelId));
            Assert.False(node.Driver.GetInfo(s_channelId)!.AwaitingAbortEcho);
            Assert.Single(node.Host.Completions);
        }
    }
}