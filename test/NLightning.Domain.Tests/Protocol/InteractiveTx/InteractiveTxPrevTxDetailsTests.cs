namespace NLightning.Domain.Tests.Protocol.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using static InteractiveTxTestData;

/// <summary>
/// BOLTs PR #1324 <c>prevtx_details</c> (tx_add_input type 2, Eclair 0.14.3's 1111; NL-957): a taproot input sent
/// without <c>prevtx</c>, accepted only when its script is a witness program of version 1-16, refused with a
/// <c>prevtx</c> or <c>shared_input_txid</c>, and at <c>tx_complete</c> only if every input is taproot.
/// </summary>
public class InteractiveTxPrevTxDetailsTests
{
    private static readonly TxId s_detailsTxId = new(Enumerable.Repeat((byte)0x99, 32).ToArray());

    private readonly FakePrevTxInspector _inspector = new();

    private static InteractiveTxSession NonInitiator(InteractiveTxContribution? contribution = null,
                                                     SharedFundingSpec? shared = null) =>
        InteractiveTxSession.Create(Parameters(false, contribution, shared));

    private static TxAddInputMessage AddDetailsInput(ulong serialId, BitcoinScript script, ulong sats = 100_000,
                                                     TxId? txId = null, uint vout = 0, byte[]? prevTx = null,
                                                     bool eclairType = false, SharedInputTxIdTlv? shared = null) =>
        new(new TxAddInputPayload(TestChannelId, serialId, prevTx ?? [], vout, Sequence), shared,
            new PrevTxDetailsTlv(txId ?? s_detailsTxId, sats, script,
                                 eclairType ? InteractiveTxTlvConstants.PrevTxDetailsEclair : null));

    private static void AssertAborted(InteractiveTxStepResult result, string requirementId)
    {
        Assert.True(result.Aborted, "expected tx_abort");
        Assert.Equal(requirementId, result.RequirementId);
        Assert.IsType<TxAbortMessage>(Assert.Single(result.Outbound));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_P2TrPrevTxDetails_When_PeerAddsInput_Then_ItIsRecordedWithoutPrevTx(bool eclairType)
    {
        // Act
        var result = NonInitiator().Receive(AddDetailsInput(0, P2Tr, 123_456, vout: 3, eclairType: eclairType),
                                            _inspector);

        // Assert
        Assert.False(result.Aborted, result.AbortReason);
        var input = Assert.Single(result.Next.Inputs);
        Assert.Equal(s_detailsTxId, input.PrevTxId);
        Assert.Equal(3u, input.PrevTxVout);
        Assert.Equal(123_456L, input.Amount.Satoshi);
        Assert.Equal(P2Tr, input.ScriptPubKey);
        Assert.Null(input.PrevTx);
        Assert.False(input.IsShared);
        Assert.Equal(InteractiveTxParty.Remote, input.AddedBy);
        Assert.True(InteractiveTxRules.UsesPrevTxDetails(input));
    }

    [Fact]
    public void Given_SegwitV0ScriptInPrevTxDetails_When_PeerAddsInput_Then_Aborts()
    {
        // Arrange
        // (PR #1324: "the scriptPubKey in prevtx_details is not exactly a 1-byte push opcode (for the numeric values 1
        // to 16) followed by a data push between 2 and 40 bytes")

        // Act
        var p2Wpkh = NonInitiator().Receive(AddDetailsInput(0, P2Wpkh), _inspector);
        var p2Wsh = NonInitiator().Receive(AddDetailsInput(0, P2Wsh), _inspector);
        var nonWitness = NonInitiator().Receive(AddDetailsInput(0, new BitcoinScript([0x6a, 0x01, 0x01])),
                                                _inspector);

        // Assert
        AssertAborted(p2Wpkh, "IT-R-01");
        AssertAborted(p2Wsh, "IT-R-01");
        AssertAborted(nonWitness, "IT-R-01");
    }

    [Fact]
    public void Given_PrevTxAndPrevTxDetails_When_PeerAddsInput_Then_Aborts()
    {
        // Arrange
        // (PR #1324: "if prevtx_len is not 0: prevtx_details is also set")
        var message = AddDetailsInput(0, P2Tr, prevTx: PrevTx(1));

        // Act
        var result = NonInitiator().Receive(message, _inspector);

        // Assert
        AssertAborted(result, "IT-R-01");
    }

    [Fact]
    public void Given_SharedInputTxIdAndPrevTxDetails_When_PeerAddsInput_Then_Aborts()
    {
        // Arrange
        var message = AddDetailsInput(0, P2Tr, txId: FundingTxId, vout: 1, shared: new SharedInputTxIdTlv(FundingTxId));

        // Act
        var result = NonInitiator(shared: Splice(false)).Receive(message, _inspector);

        // Assert
        AssertAborted(result, "IT-R-01");
    }

    [Fact]
    public void Given_AmountAboveMaxMoney_When_PeerAddsInput_Then_Aborts()
    {
        // Act
        var result = NonInitiator().Receive(AddDetailsInput(0, P2Tr, InteractiveTxRules.MaxMoneySatoshis + 1),
                                            _inspector);

        // Assert
        AssertAborted(result, "IT-R-01");
    }

    [Fact]
    public void Given_AnAddedOutpoint_When_PeerAddsItAgainWithPrevTxDetails_Then_Aborts()
    {
        // Arrange
        // (PR #1324: "prevtx_details and prevtx_vout are identical to a previously added (and not removed) input")
        var first = NonInitiator().Receive(AddDetailsInput(0, P2Tr), _inspector).Next;

        // Act
        var result = first.Receive(AddDetailsInput(2, P2Tr), _inspector);

        // Assert
        AssertAborted(result, "IT-R-01");
    }

    [Fact]
    public void Given_Splice_When_PeerAddsTheFundingOutpointWithPrevTxDetails_Then_Aborts()
    {
        // Act
        var result = NonInitiator(shared: Splice(false))
           .Receive(AddDetailsInput(0, P2Tr, txId: FundingTxId, vout: 1), _inspector);

        // Assert
        AssertAborted(result, "SP-TX-01");
    }

    [Fact]
    public void Given_OnlyTaprootInputs_When_PeerCompletes_Then_TheNegotiationCompletes()
    {
        // Arrange
        var s1 = NonInitiator().Receive(AddDetailsInput(0, P2Tr), _inspector).Next;
        var s2 = s1.Receive(AddOutput(2, 50_000), _inspector).Next;

        // Act
        var result = s2.Receive(Complete(), _inspector);

        // Assert
        Assert.False(result.Aborted, result.AbortReason);
        Assert.True(result.NegotiationComplete);
    }

    [Fact]
    public void Given_PrevTxDetailsAndAPeerSegwitV0Input_When_PeerCompletes_Then_Aborts()
    {
        // Arrange
        // (PR #1324 tx_complete: "there are inputs that use prevtx_details instead of providing the whole prevtx but
        // some inputs are not taproot inputs"; FakePrevTxInspector's prevtx outputs are P2WPKH)
        var s1 = NonInitiator().Receive(AddDetailsInput(0, P2Tr), _inspector).Next;
        var s2 = s1.Receive(AddInput(2, 7), _inspector).Next;
        var s3 = s2.Receive(AddOutput(4, 50_000), _inspector).Next;

        // Act
        var result = s3.Receive(Complete(), _inspector);

        // Assert
        AssertAborted(result, "IT-R-04");
        Assert.False(result.NegotiationComplete);
    }

    [Fact]
    public void Given_PrevTxDetailsAndOurSegwitV0Input_When_PeerCompletes_Then_Aborts()
    {
        // Arrange: our own P2WPKH contribution makes the transaction malleable without the peer's prevtx
        var session = NonInitiator(Contribution([Input(1)], [Output(10_000)]));
        var s1 = session.Receive(AddDetailsInput(0, P2Tr), _inspector).Next;
        var s2 = s1.Receive(AddOutput(2, 50_000), _inspector).Next;

        // Act
        var result = s2.Receive(Complete(), _inspector);

        // Assert
        AssertAborted(result, "IT-R-04");
    }
}