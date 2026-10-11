using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.InteractiveTx;

using Application.InteractiveTx;
using Application.InteractiveTx.Interfaces;
using Application.InteractiveTx.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using TestDoubles;

public class InteractiveTxDriverTests
{
    private static readonly ChannelId s_channelId = InteractiveTxHarness.ChannelId;

    public static TheoryData<MessageTypes> NegotiationTypes => InteractiveTxMessages.NegotiationTypes;

    [Theory]
    [MemberData(nameof(NegotiationTypes))]
    public async Task Given_NoNegotiation_When_AnInteractiveTxMessageArrives_Then_TxAbortAndStaleOnesIgnored(
        MessageTypes type)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = CreateNode();
        var peer = CreateNode(0x22);
        var message = InteractiveTxMessages.Create(type, s_channelId);

        // Act
        var first = await node.Driver.ReceiveAsync(message, peer.NodeId, node.UnitOfWork, ct);
        var stale = await node.Driver.ReceiveAsync(message, peer.NodeId, node.UnitOfWork, ct);
        var echo = await node.Driver.ReceiveAsync(InteractiveTxMessages.Create(MessageTypes.TxAbort, s_channelId),
                                                  peer.NodeId, node.UnitOfWork, ct);

        // Assert
        var abort = Assert.IsType<TxAbortMessage>(Assert.Single(first));
        Assert.Equal(s_channelId, abort.Payload.ChannelId);
        Assert.Empty(stale);
        Assert.Empty(echo);
        Assert.Null(node.Driver.GetInfo(s_channelId));
    }

    [Fact]
    public async Task Given_NoNegotiation_When_TxAbortArrives_Then_ItIsEchoedAndQuiescenceEnds()
    {
        // Arrange (BOLT 2: if they have not sent tx_abort, MUST echo back tx_abort)
        var ct = TestContext.Current.CancellationToken;
        var quiescence = new Mock<IQuiescenceService>();
        var node = CreateNode(quiescenceService: quiescence.Object);
        var peer = CreateNode(0x22);

        // Act
        var replies = await node.Driver.ReceiveAsync(InteractiveTxMessages.Create(MessageTypes.TxAbort, s_channelId),
                                                     peer.NodeId, node.UnitOfWork, ct);

        // Assert
        Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        quiescence.Verify(q => q.Terminate(s_channelId, QuiescenceEndReason.TxAbort), Times.Once);
    }

    [Fact]
    public async Task Given_AnEngineThatRejectsTheMessage_When_Received_Then_TxAbortAndNoException()
    {
        // Arrange (malformed -> tx_abort, never a channel failure)
        var ct = TestContext.Current.CancellationToken;
        var negotiation = new Mock<IInteractiveTxNegotiation>();
        negotiation.Setup(n => n.Receive(It.IsAny<IChannelMessage>(), It.IsAny<IPrevTxInspector>()))
                   .Throws(new ArgumentException("bad serial_id"));
        negotiation.Setup(n => n.Abort(It.IsAny<string>()))
                   .Returns((string reason) => new InteractiveTxNegotiationStep(
                                negotiation.Object, [InteractiveTxDriver.CreateTxAbort(s_channelId, reason)], false,
                                reason));
        negotiation.SetupGet(n => n.State).Returns(InteractiveTxSessionState.Negotiating);
        var engine = new Mock<IInteractiveTxEngine>();
        engine.Setup(e => e.Create(It.IsAny<InteractiveTxSessionParameters>())).Returns(negotiation.Object);
        var node = CreateNode(engine: engine.Object);
        var peer = CreateNode(0x22);
        node.Fund(200_000);
        await node.Driver.StartAsync(node.Terms(s_channelId, peer, false, 1_000), node.Host, ct);

        // Act
        var replies = await node.Driver.ReceiveAsync(InteractiveTxMessages.Create(MessageTypes.TxAddInput, s_channelId),
                                                     peer.NodeId, node.UnitOfWork, ct);

        // Assert
        var abort = Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        Assert.Contains("bad serial_id", System.Text.Encoding.ASCII.GetString(abort.Payload.Data));
        Assert.False(node.Driver.IsNegotiating(s_channelId));
        Assert.Single(node.Host.Aborts);
        Assert.Single(node.Contributor.Released);
        Assert.True(node.Driver.GetInfo(s_channelId)!.AwaitingAbortEcho);
    }

    [Theory]
    [MemberData(nameof(InteractiveTxHarnessTests.Engines), MemberType = typeof(InteractiveTxHarnessTests))]
    public async Task Given_AnInvalidPrevTx_When_Received_Then_TxAbort(string engine)
    {
        // Arrange (IT-R-01: an invalid prevtx fails the negotiation)
        var ct = TestContext.Current.CancellationToken;
        var node = CreateNode(engine: InteractiveTxEngines.Get(engine));
        var peer = CreateNode(0x22);
        await node.Driver.StartAsync(node.Terms(s_channelId, peer, false, 1_000, false), node.Host, ct);
        var garbage = new TxAddInputMessage(new TxAddInputPayload(s_channelId, 0, [0x01, 0x02, 0x03], 0, 0xFFFFFFFD));

        // Act
        var replies = await node.Driver.ReceiveAsync(garbage, peer.NodeId, node.UnitOfWork, ct);

        // Assert
        Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        Assert.False(node.Driver.IsNegotiating(s_channelId));
    }

    [Fact]
    public async Task Given_RequireConfirmedInputsSent_When_ThePeerAddsAnUnconfirmedInput_Then_TxAbort()
    {
        // Arrange (BOLT 2: every input must be confirmed when we sent require_confirmed_inputs)
        var ct = TestContext.Current.CancellationToken;
        var node = CreateNode();
        var peer = CreateNode(0x22);
        var utxo = WalletUtxo.Create(100_000);
        node.Inspector.Unconfirmed.Add(utxo.TxId);
        var terms = node.Terms(s_channelId, peer, false, 1_000, false) with { LocalRequiresConfirmedInputs = true };
        await node.Driver.StartAsync(terms, node.Host, ct);

        // Act
        var replies = await node.Driver.ReceiveAsync(
                          new TxAddInputMessage(new TxAddInputPayload(s_channelId, 0, utxo.PrevTx, 0, 0xFFFFFFFD)),
                          peer.NodeId, node.UnitOfWork, ct);

        // Assert
        var abort = Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        Assert.Contains("unconfirmed", System.Text.Encoding.ASCII.GetString(abort.Payload.Data));
    }

    [Fact]
    public async Task Given_RequireConfirmedInputsSent_When_ThePeerAddsAnUnconfirmedPrevTxDetailsInput_Then_TxAbort()
    {
        // Arrange (NL-957: a taproot input described by prevtx_details is checked by its outpoint)
        var ct = TestContext.Current.CancellationToken;
        var node = CreateNode();
        var peer = CreateNode(0x22);
        var txId = new Domain.Bitcoin.ValueObjects.TxId(Enumerable.Repeat((byte)0x5a, 32).ToArray());
        node.Inspector.Unconfirmed.Add(txId);
        var terms = node.Terms(s_channelId, peer, false, 1_000, false) with { LocalRequiresConfirmedInputs = true };
        await node.Driver.StartAsync(terms, node.Host, ct);
        var details = new Domain.Protocol.Tlv.PrevTxDetailsTlv(txId, 100_000,
                                                               (byte[])[0x51, 0x20, .. new byte[32]]);

        // Act
        var replies = await node.Driver.ReceiveAsync(
                          new TxAddInputMessage(new TxAddInputPayload(s_channelId, 0, [], 0, 0xFFFFFFFD), null, details),
                          peer.NodeId, node.UnitOfWork, ct);

        // Assert
        var abort = Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        Assert.Contains("unconfirmed", System.Text.Encoding.ASCII.GetString(abort.Payload.Data));
    }

    [Fact]
    public async Task Given_ANegotiationWithAnotherPeer_When_AMessageArrives_Then_TxAbortAndTheNegotiationIsKept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = CreateNode();
        var peer = CreateNode(0x22);
        var stranger = CreateNode(0x33);
        await node.Driver.StartAsync(node.Terms(s_channelId, peer, false, 1_000, false), node.Host, ct);

        // Act
        var replies = await node.Driver.ReceiveAsync(InteractiveTxMessages.Create(MessageTypes.TxComplete, s_channelId),
                                                     stranger.NodeId, node.UnitOfWork, ct);

        // Assert
        Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        Assert.True(node.Driver.IsNegotiating(s_channelId));
        Assert.Empty(node.Host.Aborts);
    }

    [Fact]
    public async Task Given_ANegotiationInProgress_When_StartedAgain_Then_Refused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = CreateNode();
        var peer = CreateNode(0x22);
        await node.Driver.StartAsync(node.Terms(s_channelId, peer, false, 1_000, false), node.Host, ct);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => node.Driver.StartAsync(node.Terms(s_channelId, peer, false, 1_000, false), node.Host, ct));
    }

    [Fact]
    public async Task Given_TheCommitmentStepFails_When_TheNegotiationCompletes_Then_TxAbortAndNothingStored()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(InteractiveTxEngines.Reference, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        harness.Bob.Host.FailCommitmentStep = true;

        // Act
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(1_000, ct), ct);

        // Assert
        Assert.Contains(harness.Transcript, t => t is { From: "bob", Message: TxAbortMessage });
        Assert.Null(harness.Bob.StoredSession(s_channelId));
        Assert.Empty(harness.Bob.Host.Completions);
        Assert.Empty(harness.Alice.Host.Completions);
        Assert.False(harness.Alice.Driver.IsNegotiating(s_channelId));
        Assert.Equal(InteractiveTxSessionState.Aborted, harness.Alice.StoredSession(s_channelId)?.State
                                                      ?? InteractiveTxSessionState.Aborted);
    }

    [Fact]
    public async Task Given_AnUnstoredNegotiation_When_Disconnected_Then_ForgottenAndReleased()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = CreateNode();
        var peer = CreateNode(0x22);
        node.Fund(200_000);
        await node.Driver.StartAsync(node.Terms(s_channelId, peer, true, 1_000), node.Host, ct);

        // Act
        await node.Driver.OnDisconnectedAsync(s_channelId, ct);

        // Assert
        Assert.False(node.Driver.IsNegotiating(s_channelId));
        Assert.Null(node.Driver.GetInfo(s_channelId));
        Assert.Equal(["disconnected"], node.Host.Aborts);
        Assert.Single(node.Contributor.Released);
    }

    [Fact]
    public async Task Given_AStoredNegotiation_When_Disconnected_Then_KeptForTheReconnection()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(InteractiveTxEngines.Reference, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(1_000, ct), ct,
                                (_, m) => m is TestCommitmentSignedMessage);

        // Act
        await harness.Alice.Driver.OnDisconnectedAsync(s_channelId, ct);

        // Assert
        Assert.True(harness.Alice.Driver.IsNegotiating(s_channelId));
        Assert.Empty(harness.Alice.Host.Aborts);
        Assert.Empty(harness.Alice.Contributor.Released);
    }

    [Fact]
    public async Task Given_NoRbfRequested_When_TxAckRbfArrives_Then_TxAbort()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = CreateNode();
        var peer = CreateNode(0x22);

        // Act
        var replies = await node.Driver.ReceiveAsync(InteractiveTxMessages.Create(MessageTypes.TxAckRbf, s_channelId),
                                                     peer.NodeId, node.UnitOfWork, ct);

        // Assert
        Assert.IsType<TxAbortMessage>(Assert.Single(replies));
    }

    [Theory]
    [MemberData(nameof(InteractiveTxHarnessTests.Engines), MemberType = typeof(InteractiveTxHarnessTests))]
    public async Task Given_ANegotiatedDustLimit_When_ThePeerAddsAnOutputBelowIt_Then_TxAbort(string engine)
    {
        // Arrange (NL-473: the terms' negotiated dust limit reaches the session's tx_add_output check)
        var ct = TestContext.Current.CancellationToken;
        var node = CreateNode(engine: InteractiveTxEngines.Get(engine));
        var peer = CreateNode(0x22);
        var terms = node.Terms(s_channelId, peer, false, 1_000, false) with { DustLimitSatoshis = 1_000 };
        await node.Driver.StartAsync(terms, node.Host, ct);
        var output = new TxAddOutputMessage(new TxAddOutputPayload(Domain.Money.LightningMoney.Satoshis(500),
                                                                   s_channelId,
                                                                   InteractiveTxMessages.P2WpkhScript, 0));

        // Act: 500 sat is above Bitcoin Core's 294 sat P2WPKH threshold (and the reference engine's 330 sat floor),
        // so only the negotiated 1,000 refused it
        var replies = await node.Driver.ReceiveAsync(output, peer.NodeId, node.UnitOfWork, ct);

        // Assert
        var abort = Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        Assert.Contains("dust", System.Text.Encoding.ASCII.GetString(abort.Payload.Data),
                        StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Given_ACompletedNegotiation_When_TheHostRejectsTheRbf_Then_TxAbort()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(InteractiveTxEngines.Reference, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(1_000, ct), ct);

        // Act
        var replies = await harness.Bob.Driver.ReceiveAsync(
                          new TxInitRbfMessage(new TxInitRbfPayload(s_channelId, 2_000, 0)), harness.Alice.NodeId,
                          harness.Bob.UnitOfWork, ct);

        // Assert
        Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        Assert.Single(harness.Bob.Host.RbfRequests);
        Assert.False(harness.Bob.Driver.IsNegotiating(s_channelId));
    }

    [Fact]
    public async Task Given_AQuiescentChannel_When_TheHostRejectsTheRbf_Then_TheQuiescenceEndsWithTheTxAbort()
    {
        // Arrange (NL-509: the driver's refusal ends the quiescence even when the host does not, like a non-splice
        // RBF host; SpliceService ends it itself, but it cannot be relied on for every host)
        var ct = TestContext.Current.CancellationToken;
        var quiescence = new Mock<IQuiescenceService>();
        var harness = new InteractiveTxHarness(InteractiveTxEngines.Reference, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        harness.Bob.Configure(quiescence.Object);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(1_000, ct), ct);

        // Act
        var replies = await harness.Bob.Driver.ReceiveAsync(
                          new TxInitRbfMessage(new TxInitRbfPayload(s_channelId, 2_000, 0)), harness.Alice.NodeId,
                          harness.Bob.UnitOfWork, ct);

        // Assert
        Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        quiescence.Verify(q => q.Terminate(s_channelId, QuiescenceEndReason.TxAbort), Times.Once);
    }

    [Fact]
    public async Task Given_OurNegotiationLessTxAbort_When_ThePeerEchoesIt_Then_TheQuiescenceEnds()
    {
        // Arrange (NL-509: a tx_abort we sent without a negotiation, e.g. to a stale message, waits for its echo and
        // the quiescence it ends must release when the echo arrives)
        var ct = TestContext.Current.CancellationToken;
        var quiescence = new Mock<IQuiescenceService>();
        var node = CreateNode(quiescenceService: quiescence.Object);
        var peer = CreateNode(0x22);

        // Act
        var abort = await node.Driver.ReceiveAsync(InteractiveTxMessages.Create(MessageTypes.TxComplete, s_channelId),
                                                   peer.NodeId, node.UnitOfWork, ct);
        Assert.IsType<TxAbortMessage>(Assert.Single(abort));
        quiescence.Verify(q => q.Terminate(s_channelId, QuiescenceEndReason.TxAbort), Times.Never);

        var echo = await node.Driver.ReceiveAsync(InteractiveTxMessages.Create(MessageTypes.TxAbort, s_channelId),
                                                  peer.NodeId, node.UnitOfWork, ct);

        // Assert
        Assert.Empty(echo);
        quiescence.Verify(q => q.Terminate(s_channelId, QuiescenceEndReason.TxAbort), Times.Once);
        Assert.Null(node.Driver.GetInfo(s_channelId));
    }

    [Fact]
    public async Task Given_ACompletedNegotiation_When_ItsTxSignaturesIsRetransmitted_Then_Ignored()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(InteractiveTxEngines.Reference, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(1_000, ct), ct);
        var bobSignatures = harness.Transcript.First(t => t is { From: "bob", Message: TxSignaturesMessage }).Message;

        // Act
        var replies = await harness.Alice.Driver.ReceiveAsync(bobSignatures, harness.Bob.NodeId,
                                                              harness.Alice.UnitOfWork, ct);

        // Assert
        Assert.Empty(replies);
    }

    [Theory]
    [InlineData(253u, 278UL)]
    [InlineData(520u, 545UL)]
    [InlineData(1_000u, 1_041UL)]
    [InlineData(10_000u, 10_416UL)]
    public void Given_APreviousFeerate_When_ComputingTheRbfFloor_Then_TheLargerOfBothRules(uint previous,
                                                                                           ulong expected)
    {
        // Act / Assert (BOLT 2 tx_init_rbf: max(25/24 x previous rounded down, previous + 25))
        Assert.Equal(expected, InteractiveTxDriver.GetMinimumRbfFeeratePerKw(previous));
    }

    [Theory]
    [InlineData(new byte[] { 0x68, 0x69 }, "hi")]
    [InlineData(new byte[] { 0x68, 0x0A }, "0x680a")]
    [InlineData(new byte[0], "(no data)")]
    public void Given_TxAbortData_When_Described_Then_OnlyPrintableAsciiIsShownVerbatim(byte[] data, string expected)
    {
        // Act / Assert (BOLT 2: SHOULD NOT print non-printable data verbatim)
        Assert.Equal(expected, InteractiveTxDriver.DescribeAbortData(data));
    }

    [Fact]
    public void Given_AReasonWithControlCharacters_When_CreatingTxAbort_Then_TheDataIsPrintable()
    {
        // Act
        var abort = InteractiveTxDriver.CreateTxAbort(s_channelId, "bad\nline");

        // Assert
        Assert.All(abort.Payload.Data, b => Assert.InRange(b, (byte)32, (byte)126));
    }

    private static InteractiveTxTestNode CreateNode(byte key = 0x11, IInteractiveTxEngine? engine = null,
                                                    IQuiescenceService? quiescenceService = null)
    {
        var node = InteractiveTxTestNode.Create($"node{key}", key, engine ?? new ReferenceInteractiveTxEngine(),
                                                100_000, 50_000);
        if (quiescenceService is not null)
            node.ReplaceDriver(new InteractiveTxDriver(engine ?? new ReferenceInteractiveTxEngine(), node.Builder,
                                                       node.Contributor, node.Inspector,
                                                       NullLogger<InteractiveTxDriver>.Instance, quiescenceService));
        return node;
    }
}