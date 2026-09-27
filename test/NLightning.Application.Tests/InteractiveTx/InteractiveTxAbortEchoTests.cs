namespace NLightning.Application.Tests.InteractiveTx;

using Application.InteractiveTx;
using Application.InteractiveTx.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using TestDoubles;

/// <summary>
/// The <c>tx_abort</c> echo rules (BOLT 2: "Echoing back tx_abort allows the peer to ack that they've seen the abort
/// message, permitting the originating peer to terminate the in-flight process without worrying about stale
/// messages"): an echo is never taken for the next attempt's, and two drivers never bounce tx_abort forever.
/// </summary>
public class InteractiveTxAbortEchoTests
{
    private const uint FeeratePerKw = 1_000;
    private static readonly ChannelId s_channelId = InteractiveTxHarness.ChannelId;

    public static TheoryData<string> Engines => InteractiveTxEngines.All;

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_AWithdrawnRbfRequest_When_AnotherFollowsAtOnce_Then_RefusedUntilTheEchoAndNoAbortLoop(
        string engine)
    {
        // Arrange: a completed negotiation, then Alice asks for an RBF and withdraws it before Bob answers
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(FeeratePerKw, ct), ct);
        AcceptRbfReusingInputs(harness.Bob, harness.Alice);
        var initRbf = await harness.Alice.Driver.RequestRbfAsync(ReusingTerms(harness.Alice, harness.Bob, 1_100),
                                                                 harness.Alice.Host.LocalOutputShare, ct);
        var abort = await harness.Alice.Driver.AbortAsync(s_channelId, "changed my mind", harness.Alice.UnitOfWork,
                                                          ct);

        // Act / Assert: a new request before the echo would take the echo as the new attempt's answer
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.Driver.RequestRbfAsync(ReusingTerms(harness.Alice, harness.Bob, 1_200),
                                                       harness.Alice.Host.LocalOutputShare, ct));
        await harness.PumpAsync(harness.Alice, [.. initRbf, .. abort], ct, maxMessages: 60);

        // Assert: our tx_abort and its one echo, nothing else bounced
        Assert.Equal(2, harness.Transcript.Count(t => t.Message is TxAbortMessage));
        Assert.False(harness.Alice.Driver.GetInfo(s_channelId)!.AwaitingAbortEcho);
        Assert.False(harness.Bob.Driver.IsNegotiating(s_channelId));

        // Act: once the echo is in, the next RBF goes through
        var second = await harness.Alice.Driver.RequestRbfAsync(ReusingTerms(harness.Alice, harness.Bob, 1_200),
                                                                harness.Alice.Host.LocalOutputShare, ct);
        await harness.PumpAsync(harness.Alice, second, ct, maxMessages: 60);

        // Assert
        Assert.Equal(2, harness.Alice.Host.Completions.Count);
        Assert.Equal(2, harness.Bob.Host.Completions.Count);
        Assert.Equal(1_200u, harness.Alice.Host.Completions[1].FeeratePerKw);
        Assert.Equal(2, harness.Transcript.Count(t => t.Message is TxAbortMessage));
    }

    [Fact]
    public async Task Given_AnEchoedTxAbort_When_AnotherArrivesWithNothingStarted_Then_NotEchoedAgain()
    {
        // Arrange: the echo of our echo must not be echoed, or two nodes bounce tx_abort forever
        var ct = TestContext.Current.CancellationToken;
        var node = CreateNode();
        var peer = CreateNode(0x22);
        var first = await node.Driver.ReceiveAsync(InteractiveTxMessages.Create(MessageTypes.TxAbort, s_channelId),
                                                   peer.NodeId, node.UnitOfWork, ct);

        // Act
        var second = await node.Driver.ReceiveAsync(InteractiveTxMessages.Create(MessageTypes.TxAbort, s_channelId),
                                                    peer.NodeId, node.UnitOfWork, ct);

        // Assert
        Assert.IsType<TxAbortMessage>(Assert.Single(first));
        Assert.Empty(second);
    }

    [Fact]
    public async Task Given_AnEchoedTxAbort_When_ANewQuiescenceEndsWithTxAbort_Then_EchoedAgain()
    {
        // Arrange: a quiescence the peer gives up with tx_abort still gets its echo (BOLT 2 MUST)
        var ct = TestContext.Current.CancellationToken;
        var quiescence = new Mock<IQuiescenceService>();
        quiescence.Setup(q => q.GetState(s_channelId)).Returns(QuiescenceState.None);
        var node = CreateNode();
        node.Configure(quiescence.Object);
        var peer = CreateNode(0x22);
        await node.Driver.ReceiveAsync(InteractiveTxMessages.Create(MessageTypes.TxAbort, s_channelId), peer.NodeId,
                                       node.UnitOfWork, ct);
        quiescence.Setup(q => q.GetState(s_channelId))
                  .Returns(new QuiescenceState { SentStfuInitiator = false, ReceivedStfuInitiator = true });

        // Act
        var replies = await node.Driver.ReceiveAsync(InteractiveTxMessages.Create(MessageTypes.TxAbort, s_channelId),
                                                     peer.NodeId, node.UnitOfWork, ct);

        // Assert
        Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        quiescence.Verify(q => q.Terminate(s_channelId, QuiescenceEndReason.TxAbort), Times.Exactly(2));
    }

    [Fact]
    public async Task Given_OurTxAbortNeverEchoed_When_TheTimeoutPasses_Then_NoLongerWaiting()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var clock = new InteractiveTxTestClock();
        var node = CreateNode();
        node.Configure(clock: clock);
        var peer = CreateNode(0x22);
        var stale = InteractiveTxMessages.Create(MessageTypes.TxAddOutput, s_channelId);
        var abort = await node.Driver.ReceiveAsync(stale, peer.NodeId, node.UnitOfWork, ct);

        // Act / Assert: just before the timeout the tx_abort still waits, stale messages are ignored and nothing new
        // may start
        clock.Advance(InteractiveTxDriver.AbortEchoTimeout - TimeSpan.FromSeconds(1));
        Assert.Empty(await node.Driver.ReceiveAsync(stale, peer.NodeId, node.UnitOfWork, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.Driver.StartAsync(node.Terms(s_channelId, peer, false, FeeratePerKw, false), node.Host, ct));

        // Act / Assert: at the timeout the driver stops waiting
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(node.Driver.GetInfo(s_channelId)?.AwaitingAbortEcho ?? false);
        Assert.IsType<TxAbortMessage>(Assert.Single(await node.Driver.ReceiveAsync(stale, peer.NodeId,
                                                                                   node.UnitOfWork, ct)));
        clock.Advance(InteractiveTxDriver.AbortEchoTimeout);
        await node.Driver.StartAsync(node.Terms(s_channelId, peer, false, FeeratePerKw, false), node.Host, ct);
        Assert.IsType<TxAbortMessage>(Assert.Single(abort));
        Assert.True(node.Driver.IsNegotiating(s_channelId));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_ThePeersTxSignaturesRejected_When_WeAbort_Then_NoEchoIsAwaited(string engine)
    {
        // Arrange: a peer that sent tx_signatures keeps the negotiation and never echoes (IT-ABT-01), so our
        // tx_abort must not block its later messages
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        var undelivered = await harness.PumpAsync(harness.Alice, await harness.StartAsync(FeeratePerKw, ct), ct,
                                                  (_, m) => m is TxSignaturesMessage);
        var (signer, message) = undelivered[0];
        var signatures = Assert.IsType<TxSignaturesMessage>(message);
        var receiver = harness.Other(signer);
        var tampered = new TxSignaturesMessage(new TxSignaturesPayload(s_channelId, signatures.Payload.TxId, []));

        // Act
        var replies = await receiver.Driver.ReceiveAsync(tampered, signer.NodeId, receiver.UnitOfWork, ct);

        // Assert
        Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        Assert.False(receiver.Driver.IsNegotiating(s_channelId));
        Assert.False(receiver.Driver.GetInfo(s_channelId)?.AwaitingAbortEcho ?? false);
        var later = await receiver.Driver.ReceiveAsync(InteractiveTxMessages.Create(MessageTypes.TxComplete,
                                                                                    s_channelId),
                                                       signer.NodeId, receiver.UnitOfWork, ct);
        Assert.IsType<TxAbortMessage>(Assert.Single(later));
    }

    private static InteractiveTxTestNode CreateNode(byte key = 0x11) =>
        InteractiveTxTestNode.Create($"node{key}", key, new ReferenceInteractiveTxEngine(), 100_000, 50_000);

    private static InteractiveTxTerms ReusingTerms(InteractiveTxTestNode node, InteractiveTxTestNode peer,
                                                   uint feeratePerKw, bool isInitiator = true)
    {
        var terms = node.Terms(s_channelId, peer, isInitiator, feeratePerKw);
        var previous = node.Repository.Committed.Values
                           .Where(s => s.State == InteractiveTxSessionState.Signed)
                           .MaxBy(s => s.CreatedAt)!.LocalContribution;
        return terms with
        {
            ContributionRequest = null,
            Contribution = node.Contributor.ContributeReusing(previous, terms.ContributionRequest!)
        };
    }

    private static void AcceptRbfReusingInputs(InteractiveTxTestNode node, InteractiveTxTestNode peer)
    {
        node.Host.RbfHandler = (message, _) =>
            InteractiveTxRbfDecision.Accept(ReusingTerms(node, peer, message.Payload.Feerate, false),
                                            node.Host.LocalOutputShare);
    }
}