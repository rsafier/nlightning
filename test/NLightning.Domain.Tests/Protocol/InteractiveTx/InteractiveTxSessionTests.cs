namespace NLightning.Domain.Tests.Protocol.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using static InteractiveTxTestData;

/// <summary>
/// <see cref="InteractiveTxSession"/> (IT1-T1, IT1-T3): turn-taking, consecutive <c>tx_complete</c>, the receiver rules
/// as the session applies them (IT-S-01/02, IT-R-01..04, NL-219), <c>tx_signatures</c> (IT-SIG-01..03) and
/// <c>tx_abort</c> (IT-ABT-01). Migrated from the deleted <c>InteractiveTransactionServiceTests</c>.
/// </summary>
public class InteractiveTxSessionTests
{
    private readonly FakePrevTxInspector _inspector = new();

    #region Helpers

    private sealed record Exchange(InteractiveTxSession Initiator, InteractiveTxSession NonInitiator,
                                   List<(bool FromInitiator, IChannelMessage Message)> Log, bool InitiatorComplete,
                                   bool NonInitiatorComplete);

    /// <summary>Runs two sessions against each other until neither has anything to send.</summary>
    private Exchange Run(InteractiveTxSession initiator, InteractiveTxSession nonInitiator,
                         FakePrevTxInspector? inspector = null)
    {
        inspector ??= _inspector;
        var log = new List<(bool, IChannelMessage)>();
        var initiatorComplete = false;
        var nonInitiatorComplete = false;

        var step = initiator.Start();
        initiator = step.Next;
        var pending = new Queue<(bool FromInitiator, IChannelMessage Message)>(step.Outbound.Select(m => (true, m)));
        initiatorComplete |= step.NegotiationComplete;

        while (pending.Count > 0)
        {
            var (fromInitiator, message) = pending.Dequeue();
            log.Add((fromInitiator, message));
            if (fromInitiator)
            {
                step = nonInitiator.Receive(message, inspector);
                nonInitiator = step.Next;
                nonInitiatorComplete |= step.NegotiationComplete;
            }
            else
            {
                step = initiator.Receive(message, inspector);
                initiator = step.Next;
                initiatorComplete |= step.NegotiationComplete;
            }

            foreach (var outbound in step.Outbound)
                pending.Enqueue((!fromInitiator, outbound));
        }

        return new Exchange(initiator, nonInitiator, log, initiatorComplete, nonInitiatorComplete);
    }

    private static string Names(IEnumerable<(bool FromInitiator, IChannelMessage Message)> log) =>
        string.Join(",", log.Select(e => (e.FromInitiator ? "A:" : "B:") + e.Message.Type));

    private static InteractiveTxSession NonInitiator(InteractiveTxContribution? contribution = null,
                                                     SharedFundingSpec? shared = null) =>
        InteractiveTxSession.Create(Parameters(false, contribution, shared));

    /// <summary>Our non-initiator session after the peer's first message.</summary>
    private InteractiveTxStepResult Receive(InteractiveTxSession session, IChannelMessage message) =>
        session.Receive(message, _inspector);

    private static void AssertAborted(InteractiveTxStepResult result, string requirementId)
    {
        Assert.True(result.Aborted, "expected tx_abort");
        Assert.Equal(requirementId, result.RequirementId);
        Assert.Equal(InteractiveTxSessionState.Aborted, result.Next.State);
        var abort = Assert.IsType<TxAbortMessage>(Assert.Single(result.Outbound));
        Assert.Equal(TestChannelId, abort.Payload.ChannelId);
        Assert.All(abort.Payload.Data, b => Assert.InRange(b, (byte)32, (byte)126));
    }

    /// <summary>A completed, constructed splice-less negotiation where both sides added one input.</summary>
    private (InteractiveTxSession Initiator, InteractiveTxSession NonInitiator) ConstructedPair(
        long initiatorInputSats = 100_000, long nonInitiatorInputSats = 100_000, SharedFundingSpec? shared = null,
        SharedFundingSpec? sharedForNonInitiator = null)
    {
        var a = InteractiveTxSession.Create(Parameters(true, Contribution([Input(1, initiatorInputSats)], [Output(10_000)]),
                                                       shared, LowNodeId, HighNodeId));
        var b = InteractiveTxSession.Create(Parameters(false,
                                                       Contribution([Input(2, nonInitiatorInputSats)], [Output(10_000)]),
                                                       sharedForNonInitiator, HighNodeId, LowNodeId));
        var inspector = new FakePrevTxInspector
        {
            Override = (bytes, vout) =>
            {
                var sats = bytes.SequenceEqual(PrevTx(1)) ? initiatorInputSats : nonInitiatorInputSats;
                return new PrevTxInspection(true, PrevTxId(bytes), 2, LightningMoney.Satoshis(sats), P2Wpkh, true, null);
            }
        };
        var exchange = Run(a, b, inspector);
        Assert.True(exchange.InitiatorComplete && exchange.NonInitiatorComplete, Names(exchange.Log));

        return (exchange.Initiator.WithConstructedTransaction(Construct(exchange.Initiator)),
                exchange.NonInitiator.WithConstructedTransaction(Construct(exchange.NonInitiator)));
    }

    #endregion

    #region Create / Start

    [Fact]
    public void Given_ContributionWithFinalSequence_When_Creating_Then_Throws()
    {
        // Arrange
        // (BOLT 2: "MUST set sequence to be less than or equal to 4294967293 (0xFFFFFFFD)")
        var parameters = Parameters(true, Contribution([Input(1, sequence: 0xFFFFFFFE)]));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => InteractiveTxSession.Create(parameters));
    }

    [Fact]
    public void Given_NonInitiator_When_Starting_Then_Throws()
    {
        // Arrange
        var session = NonInitiator();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => session.Start());
    }

    [Fact]
    public void Given_StartedInitiator_When_StartingAgain_Then_Throws()
    {
        // Arrange
        var started = InteractiveTxSession.Create(Parameters(true, Contribution([Input(1)]))).Start().Next;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => started.Start());
    }

    [Fact]
    public void Given_Initiator_When_Starting_Then_FirstMessageIsTxAddInputWithEvenSerialId()
    {
        // Arrange
        // (BOLT 2: "The initiator initiates the interactive transaction construction protocol with tx_add_input")
        var session = InteractiveTxSession.Create(Parameters(true, Contribution([Input(1)], [Output(50_000)])));

        // Act
        var result = session.Start();

        // Assert
        var add = Assert.IsType<TxAddInputMessage>(Assert.Single(result.Outbound));
        Assert.Equal(0UL, add.Payload.SerialId % 2);
        Assert.Equal(PrevTx(1), add.Payload.PrevTx);
        Assert.Equal(Sequence, add.Payload.Sequence);
        Assert.False(result.Next.IsOurTurn);
        Assert.Single(result.Next.Inputs);
        Assert.Equal(InteractiveTxParty.Local, result.Next.Inputs[0].AddedBy);
    }

    [Fact]
    public void Given_InitiatorWithNothing_When_Starting_Then_SendsTxComplete()
    {
        // Arrange
        var session = InteractiveTxSession.Create(Parameters(true));

        // Act
        var result = session.Start();

        // Assert
        Assert.IsType<TxCompleteMessage>(Assert.Single(result.Outbound));
        Assert.False(result.NegotiationComplete);
    }

    #endregion

    #region IT-S-02 turns and consecutive tx_complete

    [Fact]
    public void Given_InitiatorOnlyExample_When_Running_Then_MatchesTheSpecSequence()
    {
        // Arrange
        // (BOLT 2 "initiator only": A has two inputs and an output, B has nothing to contribute)
        var a = InteractiveTxSession.Create(Parameters(true, Contribution([Input(1), Input(2)], [Output(150_000)])));
        var b = NonInitiator();

        // Act
        var exchange = Run(a, b);

        // Assert
        Assert.Equal("A:TxAddInput,B:TxComplete,A:TxAddInput,B:TxComplete,A:TxAddOutput,B:TxComplete,A:TxComplete",
                     Names(exchange.Log));
        Assert.True(exchange.InitiatorComplete);
        Assert.True(exchange.NonInitiatorComplete);
        Assert.True(exchange.Initiator.IsNegotiationComplete);
        Assert.Equal(2, exchange.NonInitiator.Inputs.Count);
        Assert.All(exchange.NonInitiator.Inputs, i => Assert.Equal(InteractiveTxParty.Remote, i.AddedBy));
        Assert.Equal(exchange.Initiator.Inputs.Select(i => i.SerialId), exchange.NonInitiator.Inputs.Select(i => i.SerialId));
        Assert.Equal(exchange.Initiator.Outputs.Select(o => o.SerialId),
                     exchange.NonInitiator.Outputs.Select(o => o.SerialId));
    }

    [Fact]
    public void Given_BothContribute_When_Running_Then_TurnsAlternateAndBothSeeTheSameTransaction()
    {
        // Arrange
        var a = InteractiveTxSession.Create(Parameters(true, Contribution([Input(1)], [Output(50_000)])));
        var b = NonInitiator(Contribution([Input(2)], [Output(60_000)]));

        // Act
        var exchange = Run(a, b);

        // Assert
        Assert.Equal("A:TxAddInput,B:TxAddInput,A:TxAddOutput,B:TxAddOutput,A:TxComplete,B:TxComplete",
                     Names(exchange.Log));
        Assert.True(exchange.InitiatorComplete && exchange.NonInitiatorComplete);
        Assert.All(exchange.Initiator.Inputs.Where(i => i.AddedBy == InteractiveTxParty.Local),
                   i => Assert.Equal(0UL, i.SerialId % 2));
        Assert.All(exchange.NonInitiator.Inputs.Where(i => i.AddedBy == InteractiveTxParty.Local),
                   i => Assert.Equal(1UL, i.SerialId % 2));
        Assert.Equal(exchange.Initiator.Inputs.Select(i => (i.SerialId, i.PrevTxId)),
                     exchange.NonInitiator.Inputs.Select(i => (i.SerialId, i.PrevTxId)));
        Assert.Equal(2, exchange.Initiator.Outputs.Count);
    }

    [Fact]
    public void Given_NotConsecutiveTxComplete_When_PeerAddsAfterOurComplete_Then_NegotiationContinues()
    {
        // Arrange: we (non-initiator) have nothing; the peer adds, we complete, the peer adds again
        var session = NonInitiator();

        // Act
        var first = Receive(session, AddInput(0, 1));
        var second = Receive(first.Next, AddInput(2, 2));
        var third = Receive(second.Next, Complete());

        // Assert
        Assert.IsType<TxCompleteMessage>(Assert.Single(first.Outbound));
        Assert.False(first.NegotiationComplete);
        Assert.IsType<TxCompleteMessage>(Assert.Single(second.Outbound));
        Assert.False(second.NegotiationComplete);
        Assert.True(third.NegotiationComplete);
        Assert.Empty(third.Outbound);
        Assert.Equal(2, third.Next.Inputs.Count);
    }

    [Fact]
    public void Given_PeersCompleteFirst_When_WeHaveNothing_Then_OurCompleteEndsTheNegotiation()
    {
        // Arrange: the peer (initiator) starts with tx_complete; we have nothing either
        var session = NonInitiator();

        // Act
        var result = Receive(session, Complete());

        // Assert
        Assert.IsType<TxCompleteMessage>(Assert.Single(result.Outbound));
        Assert.True(result.NegotiationComplete);
    }

    [Fact]
    public void Given_PeersCompleteFirst_When_WeStillHaveItems_Then_WeSendThemAndItIsNotComplete()
    {
        // Arrange
        var session = NonInitiator(Contribution([Input(3)]));

        // Act
        var result = Receive(session, Complete());

        // Assert
        Assert.IsType<TxAddInputMessage>(Assert.Single(result.Outbound));
        Assert.False(result.NegotiationComplete);
    }

    [Fact]
    public void Given_InitiatorBeforeStart_When_PeerSendsAMessage_Then_OutOfTurnAbort()
    {
        // Arrange
        var session = InteractiveTxSession.Create(Parameters(true, Contribution([Input(1)])));

        // Act
        var result = session.Receive(AddInput(1, 5), _inspector);

        // Assert
        AssertAborted(result, "IT-S-02");
    }

    [Fact]
    public void Given_CompletedNegotiation_When_PeerAddsAnInput_Then_Abort()
    {
        // Arrange
        var completed = Receive(NonInitiator(), Complete()).Next;

        // Act
        var result = Receive(completed, AddInput(0, 1));

        // Assert
        AssertAborted(result, "IT-S-02");
    }

    #endregion

    #region IT-S-01 / IT-R-01 tx_add_input as received

    [Theory]
    [InlineData(1UL)]
    [InlineData(3UL)]
    public void Given_LocalNonInitiator_When_PeerAddsInputWithOddSerialId_Then_Aborts(ulong serialId)
    {
        // Arrange
        var session = NonInitiator();

        // Act
        var result = Receive(session, AddInput(serialId, 1));

        // Assert
        AssertAborted(result, "IT-S-01");
    }

    [Fact]
    public void Given_LocalInitiator_When_PeerAddsInputWithEvenSerialId_Then_Aborts()
    {
        // Arrange
        var started = InteractiveTxSession.Create(Parameters(true, Contribution([Input(1)]))).Start().Next;

        // Act
        var result = started.Receive(AddInput(2, 5), _inspector);

        // Assert
        AssertAborted(result, "IT-S-01");
    }

    [Fact]
    public void Given_AddedInput_When_PeerAddsTheSameSerialIdAgain_Then_Aborts()
    {
        // Arrange
        var first = Receive(NonInitiator(), AddInput(0, 1)).Next;

        // Act
        var result = Receive(first, AddInput(0, 2));

        // Assert
        AssertAborted(result, "IT-S-01");
    }

    [Fact]
    public void Given_AddedOutput_When_PeerAddsAnInputWithTheSameSerialId_Then_Accepted()
    {
        // Arrange (inputs and outputs have separate serial_id namespaces: "unique serial_id for each input")
        var first = Receive(NonInitiator(), AddOutput(0)).Next;

        // Act
        var result = Receive(first, AddInput(0, 1));

        // Assert
        Assert.False(result.Aborted);
        Assert.Single(result.Next.Inputs);
        Assert.Single(result.Next.Outputs);
    }

    [Fact]
    public void Given_AddedInput_When_PeerAddsTheSameOutpointUnderAnotherSerialId_Then_Aborts()
    {
        // Arrange
        // (BOLT 2: "prevtx and prevtx_vout are identical to a previously added (and not removed) input")
        var first = Receive(NonInitiator(), AddInput(0, 1)).Next;

        // Act
        var result = Receive(first, AddInput(2, 1));

        // Assert
        AssertAborted(result, "IT-R-01");
    }

    [Fact]
    public void Given_OurInput_When_PeerReAddsIt_Then_Aborts()
    {
        // Arrange
        // (BOLT 2 sender: "MUST NOT re-transmit inputs it has received from the peer")
        var started = InteractiveTxSession.Create(Parameters(true, Contribution([Input(1)], [Output(50_000)]))).Start();

        // Act
        var result = started.Next.Receive(AddInput(1, 1), _inspector);

        // Assert
        AssertAborted(result, "IT-R-01");
    }

    [Fact]
    public void Given_PrevTxVoutOutOfRange_When_PeerAddsInput_Then_Aborts()
    {
        // Act
        var result = Receive(NonInitiator(), AddInput(0, 1, vout: 2));

        // Assert
        AssertAborted(result, "IT-R-01");
    }

    [Fact]
    public void Given_InvalidPrevTx_When_PeerAddsInput_Then_Aborts()
    {
        // Arrange
        var inspector = new FakePrevTxInspector
        {
            Override = (_, _) => new PrevTxInspection(false, null, 0, null, null, false, "does not parse")
        };

        // Act
        var result = NonInitiator().Receive(AddInput(0, 1), inspector);

        // Assert
        AssertAborted(result, "IT-R-01");
        Assert.Contains("does not parse", result.AbortReason);
    }

    [Fact]
    public void Given_NonWitnessPrevOut_When_PeerAddsInput_Then_Aborts()
    {
        // Arrange
        var p2Pkh = new BitcoinScript([0x76, 0xa9, 0x14, .. new byte[20], 0x88, 0xac]);
        var inspector = new FakePrevTxInspector
        {
            Override = (bytes, _) => new PrevTxInspection(true, PrevTxId(bytes), 1, LightningMoney.Satoshis(1_000),
                                                          p2Pkh, false, null)
        };

        // Act
        var result = NonInitiator().Receive(AddInput(0, 1), inspector);

        // Assert
        AssertAborted(result, "IT-R-01");
    }

    [Fact]
    public void Given_ValidInput_When_PeerAddsIt_Then_ItIsRecordedFromThePrevTx()
    {
        // Act
        var result = Receive(NonInitiator(), AddInput(0, 9, vout: 1));

        // Assert
        var input = Assert.Single(result.Next.Inputs);
        Assert.Equal(PrevTxId(PrevTx(9)), input.PrevTxId);
        Assert.Equal(1u, input.PrevTxVout);
        Assert.Equal(PrevOutAmount.MilliSatoshi, input.Amount.MilliSatoshi);
        Assert.Equal(P2Wpkh, input.ScriptPubKey);
        Assert.Equal(InteractiveTxParty.Remote, input.AddedBy);
        Assert.Equal(1, result.Next.ReceivedAddInputCount);
    }

    #endregion

    #region NL-219: 4096 received messages per type

    [Fact]
    public void Given_AddRemoveCycles_When_PeerSendsThe4096thTxAddInput_Then_Aborts()
    {
        // Arrange
        // (BOLT 2: "if has received 4096 tx_add_input messages during this negotiation"; removals do not reset it)
        var session = NonInitiator();
        InteractiveTxStepResult result = null!;

        // Act
        for (var i = 0; i < 4095; i++)
        {
            result = Receive(session, AddInput(0, 1));
            Assert.False(result.Aborted, $"add #{i + 1}: {result.AbortReason}");
            result = Receive(result.Next, RemoveInput(0));
            Assert.False(result.Aborted, $"remove #{i + 1}: {result.AbortReason}");
            session = result.Next;
        }

        var last = Receive(session, AddInput(0, 1));

        // Assert
        Assert.Equal(4095, session.ReceivedAddInputCount);
        Assert.Empty(session.Inputs);
        AssertAborted(last, "IT-R-01");
    }

    [Fact]
    public void Given_AddRemoveCycles_When_PeerSendsThe4096thTxAddOutput_Then_Aborts()
    {
        // Arrange
        // (BOLT 2: "it has received 4096 tx_add_output messages during this negotiation")
        var session = NonInitiator();
        InteractiveTxStepResult result;

        // Act
        for (var i = 0; i < 4095; i++)
        {
            result = Receive(session, AddOutput(0));
            Assert.False(result.Aborted, $"add #{i + 1}: {result.AbortReason}");
            result = Receive(result.Next, RemoveOutput(0));
            Assert.False(result.Aborted, $"remove #{i + 1}: {result.AbortReason}");
            session = result.Next;
        }

        var last = Receive(session, AddOutput(0));

        // Assert
        Assert.Equal(4095, session.ReceivedAddOutputCount);
        AssertAborted(last, "IT-R-02");
    }

    #endregion

    #region IT-R-02 tx_add_output as received

    [Theory]
    [InlineData(0UL, 293L, "IT-R-02")] // below P2WPKH dust
    [InlineData(1UL, 50_000L, "IT-S-01")] // wrong parity from the initiator
    public void Given_BadOutput_When_PeerAddsIt_Then_Aborts(ulong serialId, long sats, string requirementId)
    {
        // Act
        var result = Receive(NonInitiator(), AddOutput(serialId, sats));

        // Assert
        AssertAborted(result, requirementId);
    }

    [Fact]
    public void Given_LocalNonInitiator_When_PeerAddsOutputWithEvenSerialId_Then_Accepts()
    {
        // Act
        var result = Receive(NonInitiator(), AddOutput(0, 294));

        // Assert
        Assert.False(result.Aborted);
        Assert.Single(result.Next.Outputs);
        Assert.Equal(1, result.Next.ReceivedAddOutputCount);
    }

    [Fact]
    public void Given_OutputWithSerialId_When_PeerAddsSecondOutputWithSameSerialId_Then_Aborts()
    {
        // Arrange
        var first = Receive(NonInitiator(), AddOutput(0)).Next;

        // Act
        var result = Receive(first, AddOutput(0));

        // Assert
        AssertAborted(result, "IT-S-01");
    }

    [Theory]
    [InlineData("0014")]
    [InlineData("0020")]
    [InlineData("5120")]
    public void Given_SegwitScripts_When_PeerAddsOutputs_Then_Accepted(string prefix)
    {
        // Arrange
        // (BOLT 2: "MUST accept P2WSH, P2WPKH, P2TR scripts")
        var length = prefix == "0014" ? 20 : 32;
        var script = new BitcoinScript([.. Convert.FromHexString(prefix), .. new byte[length]]);

        // Act
        var result = Receive(NonInitiator(), AddOutput(0, 10_000, script));

        // Assert
        Assert.False(result.Aborted, result.AbortReason);
    }

    #endregion

    #region IT-R-03 tx_remove_*

    [Fact]
    public void Given_AddedOutput_When_PeerRemovesIt_Then_Removed()
    {
        // Arrange
        var first = Receive(NonInitiator(), AddOutput(0)).Next;

        // Act
        var result = Receive(first, RemoveOutput(0));

        // Assert
        Assert.False(result.Aborted);
        Assert.Empty(result.Next.Outputs);
    }

    [Fact]
    public void Given_AddedInput_When_PeerRemovesOutputWithThatSerialId_Then_Aborts()
    {
        // Arrange
        var first = Receive(NonInitiator(), AddInput(0, 1)).Next;

        // Act
        var result = Receive(first, RemoveOutput(0));

        // Assert
        AssertAborted(result, "IT-R-03");
    }

    [Fact]
    public void Given_AddedOutput_When_PeerRemovesInputWithThatSerialId_Then_Aborts()
    {
        // Arrange
        var first = Receive(NonInitiator(), AddOutput(0)).Next;

        // Act
        var result = Receive(first, RemoveInput(0));

        // Assert
        AssertAborted(result, "IT-R-03");
    }

    [Fact]
    public void Given_OurInput_When_PeerRemovesIt_Then_Aborts()
    {
        // Arrange
        // (BOLT 2: "the input or output identified by the serial_id was not added by the sender")
        var started = InteractiveTxSession.Create(Parameters(true, Contribution([Input(1)], [Output(50_000)]))).Start();

        // Act
        var result = started.Next.Receive(RemoveInput(0), _inspector);

        // Assert
        AssertAborted(result, "IT-R-03");
    }

    [Fact]
    public void Given_RemovedInput_When_PeerRemovesItAgain_Then_Aborts()
    {
        // Arrange
        var added = Receive(NonInitiator(), AddInput(0, 1)).Next;
        var removed = Receive(added, RemoveInput(0)).Next;

        // Act
        var result = Receive(removed, RemoveInput(0));

        // Assert
        AssertAborted(result, "IT-R-03");
    }

    [Fact]
    public void Given_SpecExampleWithRemoval_When_PeerRemovesItsOutput_Then_TheTransactionHasTheRest()
    {
        // Arrange
        // (BOLT 2 "initiator and non-initiator": A adds 2 inputs and an output it then removes; we are B with one
        // input and one output, which we send at once instead of waiting for A's second input as the spec's B does)
        var b = NonInitiator(Contribution([Input(20)], [Output(60_000)]));

        // Act
        var s1 = Receive(b, AddInput(0, 10)); // (1) -> (2) our tx_add_input
        var s2 = Receive(s1.Next, AddOutput(2)); // (3) -> our tx_add_output
        var s3 = Receive(s2.Next, AddInput(4, 11)); // (5) -> our tx_complete
        var s4 = Receive(s3.Next, RemoveOutput(2)); // (7) -> our tx_complete
        var s5 = Receive(s4.Next, Complete()); // (9): consecutive with our tx_complete, so it concludes

        // Assert
        Assert.IsType<TxAddInputMessage>(Assert.Single(s1.Outbound));
        Assert.IsType<TxAddOutputMessage>(Assert.Single(s2.Outbound));
        Assert.IsType<TxCompleteMessage>(Assert.Single(s3.Outbound));
        Assert.IsType<TxCompleteMessage>(Assert.Single(s4.Outbound));
        Assert.Empty(s5.Outbound);
        Assert.True(s5.NegotiationComplete, s5.AbortReason);
        Assert.Equal(3, s5.Next.Inputs.Count);
        var output = Assert.Single(s5.Next.Outputs);
        Assert.Equal(InteractiveTxParty.Local, output.AddedBy);
    }

    #endregion

    #region IT-R-04 tx_complete

    [Fact]
    public void Given_PeerOutputsAboveItsInputs_When_PeerCompletes_Then_WeAbortInsteadOfCompleting()
    {
        // Arrange: the peer adds a 100,000 sat input and a 100,001 sat output
        var s1 = Receive(NonInitiator(), AddInput(0, 1)).Next;
        var s2 = Receive(s1, AddOutput(2, 100_001)).Next;

        // Act
        var result = Receive(s2, Complete());

        // Assert
        AssertAborted(result, "IT-R-04");
        Assert.False(result.NegotiationComplete);
    }

    [Theory]
    [InlineData(252, false)]
    [InlineData(253, true)]
    public void Given_PeerInputs_When_Completing_Then_MoreThan252Aborts(int count, bool aborts)
    {
        // Arrange
        // (BOLT 2: "there are more than 252 inputs")
        var session = NonInitiator();
        for (var i = 0; i < count; i++)
        {
            var step = Receive(session, AddInput((ulong)i * 2, i));
            Assert.False(step.Aborted, step.AbortReason);
            session = step.Next;
        }

        // Act
        var result = Receive(session, Complete());

        // Assert
        if (aborts)
            AssertAborted(result, "IT-R-04");
        else
            Assert.True(result.NegotiationComplete, result.AbortReason);
    }

    [Theory]
    [InlineData(252, false)]
    [InlineData(253, true)]
    public void Given_PeerOutputs_When_Completing_Then_MoreThan252Aborts(int count, bool aborts)
    {
        // Arrange
        // (BOLT 2: "there are more than 252 outputs")
        var session = Receive(NonInitiator(), AddInput(0, 1)).Next;
        for (var i = 0; i < count; i++)
        {
            var step = Receive(session, AddOutput((ulong)i * 2 + 2, 330));
            Assert.False(step.Aborted, step.AbortReason);
            session = step.Next;
        }

        // Act
        var result = Receive(session, Complete());

        // Assert
        if (aborts)
            AssertAborted(result, "IT-R-04");
        else
            Assert.True(result.NegotiationComplete, result.AbortReason);
    }

    #endregion

    #region Splice: shared input and output

    [Fact]
    public void Given_SpliceInitiator_When_Starting_Then_SharedInputFirstWithoutPrevTx()
    {
        // Arrange
        // (BOLT 2 splicing: "MUST add the current channel input to the splice transaction by sending tx_add_input
        // with shared_input_txid [...] MUST NOT include prevtx for that shared input. MUST set prevtx_vout to the
        // previous funding output index.")
        var session = InteractiveTxSession.Create(Parameters(true, shared: Splice(true)));

        // Act
        var result = session.Start();

        // Assert
        var add = Assert.IsType<TxAddInputMessage>(Assert.Single(result.Outbound));
        Assert.Empty(add.Payload.PrevTx);
        Assert.Equal(1u, add.Payload.PrevTxVout);
        Assert.Equal(FundingTxId, add.SharedInputTxIdTlv?.FundingTxId);
        Assert.True(result.Next.Inputs[0].IsShared);
    }

    [Fact]
    public void Given_SpliceBetweenTwoSessions_When_Running_Then_BothAgreeOnTheSharedInputAndOutput()
    {
        // Arrange: initiator splices in 100,000 (new capacity 1,090,000 after fees)
        var a = InteractiveTxSession.Create(Parameters(true, Contribution([Input(1)]),
                                                       Splice(true, 1_090_000, 690_000, 400_000)));
        var b = NonInitiator(shared: Splice(false, 1_090_000, 400_000, 690_000));

        // Act
        var exchange = Run(a, b);

        // Assert
        Assert.True(exchange.InitiatorComplete && exchange.NonInitiatorComplete, Names(exchange.Log));
        Assert.Equal("A:TxAddInput,B:TxComplete,A:TxAddInput,B:TxComplete,A:TxAddOutput,B:TxComplete,A:TxComplete",
                     Names(exchange.Log));
        var sharedIn = Assert.Single(exchange.NonInitiator.Inputs, i => i.IsShared);
        Assert.Equal(InteractiveTxParty.Remote, sharedIn.AddedBy);
        Assert.Equal(FundingScript, sharedIn.ScriptPubKey);
        var sharedOut = Assert.Single(exchange.NonInitiator.Outputs, o => o.IsShared);
        Assert.Equal(1_090_000L, sharedOut.Amount.Satoshi);
    }

    [Fact]
    public void Given_Splice_When_PeerAddsSharedInputWithWrongTxId_Then_Aborts()
    {
        // Arrange
        // (BOLT 2 splicing: "If it doesn't match the txid of the previous funding transaction: MUST fail the
        // negotiation by sending tx_abort.")
        var session = NonInitiator(shared: Splice(false));

        // Act
        var wrongTxId = Receive(session, AddSharedInput(0, TxId.One));
        var wrongVout = Receive(session, AddSharedInput(0, vout: 0));

        // Assert
        AssertAborted(wrongTxId, "SP-TX-02");
        AssertAborted(wrongVout, "SP-TX-02");
    }

    [Fact]
    public void Given_Splice_When_PeerAddsInputWithoutPrevTxOrTlv_Then_Aborts()
    {
        // Arrange
        // (BOLT 2: "if prevtx_len is 0: shared_input_txid is not set")
        var session = NonInitiator(shared: Splice(false));
        var message = new TxAddInputMessage(new TxAddInputPayload(TestChannelId, 0, [], 1, Sequence));

        // Act
        var result = Receive(session, message);

        // Assert
        AssertAborted(result, "IT-R-01");
    }

    [Fact]
    public void Given_Splice_When_PeerAddsASecondSharedInput_Then_Aborts()
    {
        // Arrange
        // (BOLT 2: "a previously added (and not removed) input already exists with shared_input_txid set")
        var first = Receive(NonInitiator(shared: Splice(false)), AddSharedInput(0)).Next;

        // Act
        var result = Receive(first, AddSharedInput(2));

        // Assert
        AssertAborted(result, "IT-R-01");
    }

    [Fact]
    public void Given_Splice_When_PeerAddsSharedInputTlvWithAPrevTx_Then_Aborts()
    {
        // Arrange
        var session = NonInitiator(shared: Splice(false));
        var message = new TxAddInputMessage(new TxAddInputPayload(TestChannelId, 0, PrevTx(1), 1, Sequence),
                                            new Domain.Protocol.Tlv.SharedInputTxIdTlv(FundingTxId));

        // Act
        var result = Receive(session, message);

        // Assert
        AssertAborted(result, "IT-R-01");
    }

    [Fact]
    public void Given_NoSharedFunding_When_PeerAddsSharedInput_Then_Aborts()
    {
        // Act
        var result = Receive(NonInitiator(), AddSharedInput(0));

        // Assert
        AssertAborted(result, "IT-R-01");
    }

    [Fact]
    public void Given_Splice_When_PeerCompletesWithoutTheFundingOutput_Then_Aborts()
    {
        // Arrange
        // (BOLT 2 splicing: "There is not exactly one channel funding output")
        var first = Receive(NonInitiator(shared: Splice(false)), AddSharedInput(0)).Next;

        // Act
        var result = Receive(first, Complete());

        // Assert
        AssertAborted(result, "SP-TX-05");
    }

    #endregion

    #region Construction and IT-SIG-01..03

    [Fact]
    public void Given_IncompleteNegotiation_When_Constructing_Then_Throws()
    {
        // Arrange
        var session = Receive(NonInitiator(), AddInput(0, 1)).Next;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => session.WithConstructedTransaction(Construct(session)));
    }

    [Fact]
    public void Given_TransactionNotMatchingTheNegotiation_When_Constructing_Then_Throws()
    {
        // Arrange
        var completed = Receive(Receive(NonInitiator(), AddInput(0, 1)).Next, Complete()).Next;
        var tx = Construct(completed);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => completed.WithConstructedTransaction(tx with { Inputs = [] }));
        Assert.Throws<InvalidOperationException>(() => completed.WithConstructedTransaction(tx with { Locktime = 1 }));
    }

    [Fact]
    public void Given_ConstructedSession_When_TxSignaturesBeforeCommitmentSigned_Then_ThrowsAndPeerTxSignaturesAborts()
    {
        // Arrange
        // (BOLT 2 via SP-CS-02/IT-SIG-03: tx_signatures only after a valid commitment_signed)
        var (a, b) = ConstructedPair();

        // Act
        var receive = b.Receive(Signatures(a.ConstructedTx!.TxId, [P2WpkhWitness()]), _inspector);

        // Assert
        Assert.Equal(InteractiveTxSessionState.AwaitingCommitmentSigned, a.State);
        Assert.Throws<InvalidOperationException>(() => a.SendTxSignatures([P2WpkhWitness()], null));
        AssertAborted(receive, "IT-SIG-03");
    }

    [Theory]
    [InlineData(50_000L, 100_000L, true)] // the initiator contributed less: it signs first
    [InlineData(100_000L, 50_000L, false)]
    [InlineData(100_000L, 100_000L, true)] // tie: the initiator has the lower node id here
    public void Given_Contributions_When_ExchangingSignatures_Then_TheLowerSideSignsFirstAndBothEndSigned(
        long initiatorSats, long nonInitiatorSats, bool initiatorFirst)
    {
        // Arrange
        var (a, b) = ConstructedPair(initiatorSats, nonInitiatorSats);
        a = a.OnCommitmentSignedReceived();
        b = b.OnCommitmentSignedReceived();
        var (first, second) = initiatorFirst ? (a, b) : (b, a);

        // Act
        var secondTooEarly = Record.Exception(() => second.SendTxSignatures([P2WpkhWitness()], null));
        var sent = first.SendTxSignatures([P2WpkhWitness()], null);
        var received = second.Receive(Assert.Single(sent.Outbound), _inspector);
        var reply = received.Next.SendTxSignatures([P2WpkhWitness()], null);
        var closing = sent.Next.Receive(Assert.Single(reply.Outbound), _inspector);

        // Assert
        Assert.Equal(initiatorFirst, a.SendsTxSignaturesFirst());
        Assert.Equal(!initiatorFirst, b.SendsTxSignaturesFirst());
        Assert.IsType<InvalidOperationException>(secondTooEarly);
        Assert.Equal(InteractiveTxSessionState.TxSignaturesSent, sent.Next.State);
        Assert.True(sent.Next.MustBeRemembered);
        Assert.Empty(received.Outbound);
        Assert.NotNull(received.Next.RemoteWitnesses);
        Assert.Equal(InteractiveTxSessionState.Signed, reply.Next.State);
        Assert.Equal(InteractiveTxSessionState.Signed, closing.Next.State);
        Assert.Single(closing.Next.RemoteWitnesses!);
        var message = Assert.IsType<TxSignaturesMessage>(Assert.Single(sent.Outbound));
        Assert.Equal((byte[])a.ConstructedTx!.TxId, message.Payload.TxId);
    }

    [Fact]
    public void Given_WrongWitnessCount_When_SendingOurSignatures_Then_Throws()
    {
        // Arrange
        var (a, _) = ConstructedPair(50_000);
        a = a.OnCommitmentSignedReceived();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => a.SendTxSignatures([], null));
        Assert.Throws<ArgumentException>(() => a.SendTxSignatures([P2WpkhWitness(), P2WpkhWitness()], null));
        Assert.Throws<ArgumentException>(() => a.SendTxSignatures([P2WpkhWitness()], SharedSignature()));
    }

    [Theory]
    [InlineData("txid")]
    [InlineData("count")]
    [InlineData("sighash")]
    [InlineData("empty")]
    public void Given_BadPeerSignatures_When_WeHaveNotSigned_Then_Abort(string defect)
    {
        // Arrange
        // (BOLT 2: "MUST fail the negotiation if: the message contains an empty witness; the number of witnesses
        // does not equal [...]; the txid does not match [...]; a signature uses a flag that is not SIGHASH_ALL")
        var (a, b) = ConstructedPair(100_000, 50_000); // the non-initiator signs first
        a = a.OnCommitmentSignedReceived();
        var txId = a.ConstructedTx!.TxId;
        var message = defect switch
        {
            "txid" => Signatures(TxId.One, [P2WpkhWitness()]),
            "count" => Signatures(txId, [P2WpkhWitness(), P2WpkhWitness()]),
            "sighash" => Signatures(txId, [P2WpkhWitness(0x83)]),
            _ => Signatures(txId, [new Witness([])])
        };

        // Act
        var result = a.Receive(message, _inspector);

        // Assert
        Assert.False(b.SendsTxSignaturesFirst() == a.SendsTxSignaturesFirst());
        AssertAborted(result, "IT-SIG-02");
    }

    [Fact]
    public void Given_BadPeerSignatures_When_WeAlreadySigned_Then_NoAbortAndSessionUnchanged()
    {
        // Arrange
        // (BOLT 2 tx_abort: "A sending node: MUST NOT have already transmitted tx_signatures")
        var (a, _) = ConstructedPair(50_000); // the initiator signs first
        var sent = a.OnCommitmentSignedReceived().SendTxSignatures([P2WpkhWitness()], null).Next;

        // Act
        var result = sent.Receive(Signatures(TxId.One, [P2WpkhWitness()]), _inspector);

        // Assert
        Assert.Empty(result.Outbound);
        Assert.False(result.Aborted);
        Assert.Equal("IT-SIG-02", result.RequirementId);
        Assert.Same(sent, result.Next);
        Assert.Equal(InteractiveTxSessionState.TxSignaturesSent, result.Next.State);
    }

    [Fact]
    public void Given_SpliceSignatures_When_PeerOmitsSharedInputSignature_Then_ChannelFails()
    {
        // Arrange
        // (BOLT 2 splicing: "If shared_input_signature is not set: MUST send an error and fail the channel.")
        var (a, b) = SplicePair();
        var initiatorFirst = a.SendsTxSignaturesFirst();
        var (first, second) = initiatorFirst ? (a, b) : (b, a);
        var firstWitnesses = first.Inputs.Count(i => i.AddedBy == InteractiveTxParty.Local && !i.IsShared);
        var sent = first.SendTxSignatures(Enumerable.Repeat(P2WpkhWitness(), firstWitnesses).ToList(),
                                          SharedSignature());
        var sentMessage = Assert.IsType<TxSignaturesMessage>(Assert.Single(sent.Outbound));
        var withoutShared = new TxSignaturesMessage(sentMessage.Payload);

        // Act
        var exception = Assert.Throws<ChannelFailedException>(() => second.Receive(withoutShared, _inspector));
        var ok = second.Receive(sentMessage, _inspector);

        // Assert
        Assert.Equal("SP-SIG-01", exception.RequirementId);
        Assert.Equal(TestChannelId, exception.FailedChannelId);
        Assert.NotNull(ok.Next.RemoteSharedInputSignature);
        Assert.NotNull(sentMessage.SharedInputSignatureTlv);
    }

    [Fact]
    public void Given_SpliceSignatures_When_SendingWithoutSharedSignature_Then_Throws()
    {
        // Arrange
        var (a, b) = SplicePair();
        var first = a.SendsTxSignaturesFirst() ? a : b;
        var witnesses = first.Inputs.Count(i => i.AddedBy == InteractiveTxParty.Local && !i.IsShared);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            first.SendTxSignatures(Enumerable.Repeat(P2WpkhWitness(), witnesses).ToList(), null));
    }

    [Fact]
    public void Given_SpliceWithBigNonInitiatorSpliceIn_When_Ordering_Then_SharedInputCountsForTheInitiator()
    {
        // Arrange: previous capacity 1,000,000 (balances 600,000 initiator / 400,000); the non-initiator splices in
        // 900,000: 1,000,000 (initiator, shared input) > 900,000 (non-initiator) so the non-initiator signs first
        var (a, b) = SplicePair(nonInitiatorSpliceIn: 900_000);

        // Act
        var initiatorFirst = a.SendsTxSignaturesFirst();
        var nonInitiatorFirst = b.SendsTxSignaturesFirst();

        // Assert
        Assert.False(initiatorFirst);
        Assert.True(nonInitiatorFirst);
    }

    private (InteractiveTxSession Initiator, InteractiveTxSession NonInitiator) SplicePair(
        long nonInitiatorSpliceIn = 50_000)
    {
        var capacity = 1_000_000 + nonInitiatorSpliceIn;
        var a = InteractiveTxSession.Create(Parameters(true, shared: Splice(true, capacity, 600_000,
                                                                              400_000 + nonInitiatorSpliceIn)));
        var b = InteractiveTxSession.Create(Parameters(false, Contribution([Input(5, nonInitiatorSpliceIn)]),
                                                       Splice(false, capacity, 400_000 + nonInitiatorSpliceIn,
                                                              600_000), HighNodeId, LowNodeId));
        var inspector = new FakePrevTxInspector
        {
            Override = (bytes, _) => new PrevTxInspection(true, PrevTxId(bytes), 1,
                                                          LightningMoney.Satoshis(nonInitiatorSpliceIn), P2Wpkh,
                                                          true, null)
        };
        var exchange = Run(a, b, inspector);
        Assert.True(exchange.InitiatorComplete && exchange.NonInitiatorComplete, Names(exchange.Log));
        return (exchange.Initiator.WithConstructedTransaction(Construct(exchange.Initiator)).OnCommitmentSignedReceived(),
                exchange.NonInitiator.WithConstructedTransaction(Construct(exchange.NonInitiator))
                            .OnCommitmentSignedReceived());
    }

    #endregion

    #region IT-ABT-01 tx_abort

    [Fact]
    public void Given_Negotiating_When_PeerAborts_Then_WeEchoAndForget()
    {
        // Arrange
        // (BOLT 2: "if they have not sent tx_signatures: SHOULD forget the current negotiation [...]; if they have
        // not sent tx_abort: MUST echo back tx_abort")
        var session = Receive(NonInitiator(), AddInput(0, 1)).Next;

        // Act
        var result = Receive(session, AbortMessage("no more"u8.ToArray()));

        // Assert
        AssertAborted(result, "IT-ABT-01");
        Assert.Contains("no more", result.AbortReason);
    }

    [Fact]
    public void Given_WeAborted_When_PeerEchoes_Then_NoSecondAbort()
    {
        // Arrange
        var aborted = NonInitiator().Abort("changed my mind");

        // Act
        var echo = Receive(aborted.Next, AbortMessage([]));
        var again = aborted.Next.Abort("again");

        // Assert
        AssertAborted(aborted, "IT-ABT-01");
        Assert.Empty(echo.Outbound);
        Assert.Equal(InteractiveTxSessionState.Aborted, echo.Next.State);
        Assert.Empty(again.Outbound);
    }

    [Fact]
    public void Given_Aborted_When_StaleNegotiationMessagesArrive_Then_Ignored()
    {
        // Arrange
        var aborted = NonInitiator().Abort("x").Next;

        // Act
        var add = Receive(aborted, AddInput(0, 1));
        var complete = Receive(aborted, Complete());

        // Assert
        Assert.Empty(add.Outbound);
        Assert.Empty(complete.Outbound);
        Assert.Same(aborted, add.Next);
    }

    [Fact]
    public void Given_NonPrintableAbortData_When_PeerAborts_Then_ReasonIsHex()
    {
        // Arrange
        // (BOLT 2: "if data is not composed solely of printable ASCII characters [...] SHOULD NOT print out data
        // verbatim")

        // Act
        var result = Receive(NonInitiator(), AbortMessage([0x41, 0x00, 0x1b]));

        // Assert
        Assert.EndsWith("0x41001b", result.AbortReason);
    }

    [Fact]
    public void Given_OurTxSignaturesSent_When_Aborting_Then_Throws()
    {
        // Arrange
        // (BOLT 2: "A sending node: MUST NOT have already transmitted tx_signatures")
        var (a, _) = ConstructedPair(50_000);
        var sent = a.OnCommitmentSignedReceived().SendTxSignatures([P2WpkhWitness()], null).Next;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => sent.Abort("too late"));
    }

    [Fact]
    public void Given_OurTxSignaturesSent_When_PeerAborts_Then_NoEchoAndTheNegotiationIsRemembered()
    {
        // Arrange
        // (BOLT 2: "if they have already sent tx_signatures to the peer: MUST NOT forget the channel until any
        // inputs to the negotiated tx have been spent")
        var (a, _) = ConstructedPair(50_000);
        var sent = a.OnCommitmentSignedReceived().SendTxSignatures([P2WpkhWitness()], null).Next;

        // Act
        var result = sent.Receive(AbortMessage("bye"u8.ToArray()), _inspector);

        // Assert
        Assert.Empty(result.Outbound);
        Assert.False(result.Aborted);
        Assert.Equal("IT-ABT-01", result.RequirementId);
        Assert.Equal(InteractiveTxSessionState.TxSignaturesSent, result.Next.State);
        Assert.True(result.Next.MustBeRemembered);
    }

    [Theory]
    [InlineData(InteractiveTxSessionState.AwaitingCommitmentSigned)]
    [InlineData(InteractiveTxSessionState.AwaitingTxSignatures)]
    public void Given_BeforeOurTxSignatures_When_PeerAborts_Then_EchoAndAborted(InteractiveTxSessionState state)
    {
        // Arrange
        var (a, _) = ConstructedPair(50_000);
        if (state == InteractiveTxSessionState.AwaitingTxSignatures)
            a = a.OnCommitmentSignedReceived();

        // Act
        var result = a.Receive(AbortMessage([]), _inspector);

        // Assert
        AssertAborted(result, "IT-ABT-01");
        Assert.False(result.Next.MustBeRemembered);
    }

    [Fact]
    public void Given_Negotiating_When_WeAbort_Then_TxAbortCarriesThePrintableReason()
    {
        // Act
        var result = NonInitiator().Abort("fee too high\n");

        // Assert
        var abort = Assert.IsType<TxAbortMessage>(Assert.Single(result.Outbound));
        Assert.Equal("fee too high?"u8.ToArray(), abort.Payload.Data);
    }

    #endregion

    #region Restore and misuse

    [Fact]
    public void Given_StoredSessionAfterOurSignatures_When_Restored_Then_ItResumesTheExchange()
    {
        // Arrange
        var (a, b) = ConstructedPair(50_000);
        var sent = a.OnCommitmentSignedReceived().SendTxSignatures([P2WpkhWitness()], null).Next;
        var model = new InteractiveTxSessionModel
        {
            ChannelId = TestChannelId,
            SessionId = Guid.NewGuid(),
            Purpose = InteractiveTxPurpose.DualFund,
            IsInitiator = true,
            FeeratePerKw = 253,
            Locktime = 120,
            Inputs = sent.Inputs,
            Outputs = sent.Outputs,
            LocalContribution = sent.Parameters.LocalContribution,
            ConstructedTx = sent.ConstructedTx,
            OurWitnesses = sent.LocalWitnesses,
            CommitmentSignedSent = true,
            CommitmentSignedReceived = true,
            TxSignaturesSent = true,
            State = InteractiveTxSessionState.TxSignaturesSent,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
        var peerSigned = b.OnCommitmentSignedReceived();
        var peerReceived = peerSigned.Receive(Signatures(sent.ConstructedTx!.TxId, sent.LocalWitnesses!), _inspector);
        var peerReply = peerReceived.Next.SendTxSignatures([P2WpkhWitness()], null);

        // Act
        var restored = InteractiveTxSession.Restore(model, sent.Parameters);
        var result = restored.Receive(Assert.Single(peerReply.Outbound), _inspector);

        // Assert
        Assert.Equal(InteractiveTxSessionState.TxSignaturesSent, restored.State);
        Assert.True(restored.MustBeRemembered);
        Assert.Equal(sent.LocalWitnesses, restored.LocalWitnesses);
        Assert.Equal(InteractiveTxSessionState.Signed, result.Next.State);
        Assert.Throws<InvalidOperationException>(() => restored.Abort("x"));
    }

    [Theory]
    [InlineData(InteractiveTxSessionState.Negotiating)]
    [InlineData(InteractiveTxSessionState.Aborted)]
    public void Given_UnresumableRow_When_Restoring_Then_Throws(InteractiveTxSessionState state)
    {
        // Arrange
        var (a, _) = ConstructedPair();
        var model = new InteractiveTxSessionModel
        {
            ChannelId = TestChannelId,
            SessionId = Guid.NewGuid(),
            Purpose = InteractiveTxPurpose.Splice,
            IsInitiator = true,
            FeeratePerKw = 253,
            Locktime = 120,
            Inputs = a.Inputs,
            Outputs = a.Outputs,
            LocalContribution = a.Parameters.LocalContribution,
            ConstructedTx = a.ConstructedTx,
            State = state,
            CreatedAt = DateTimeOffset.UnixEpoch
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => InteractiveTxSession.Restore(model, a.Parameters));
        Assert.Throws<ArgumentException>(() =>
            InteractiveTxSession.Restore(model with { State = InteractiveTxSessionState.AwaitingTxSignatures },
                                         a.Parameters with { IsInitiator = false }));
    }

    [Fact]
    public void Given_MessageOfAnotherChannel_When_Receiving_Then_Throws()
    {
        // Arrange
        var other = new TxCompleteMessage(new TxCompletePayload(new ChannelId(new byte[32])));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => Receive(NonInitiator(), other));
    }

    [Fact]
    public void Given_TxInitRbf_When_Receiving_Then_Throws()
    {
        // Arrange (an RBF is a new session; the driver handles tx_init_rbf)
        var rbf = new TxInitRbfMessage(new TxInitRbfPayload(TestChannelId, 300, 120));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => Receive(NonInitiator(), rbf));
    }

    [Fact]
    public void Given_Session_When_Stepping_Then_ThePreviousSessionIsUnchanged()
    {
        // Arrange
        var session = NonInitiator();

        // Act
        var result = Receive(session, AddInput(0, 1));

        // Assert
        Assert.Empty(session.Inputs);
        Assert.Equal(0, session.ReceivedAddInputCount);
        Assert.Single(result.Next.Inputs);
    }

    [Fact]
    public void Given_SendsFirstBeforeConstruction_When_Asked_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => NonInitiator().SendsTxSignaturesFirst());
        Assert.Throws<InvalidOperationException>(() => NonInitiator().OnCommitmentSignedReceived());
    }

    #endregion
}