using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Money;
using Infrastructure.Bitcoin.InteractiveTx;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// <see cref="PrevTxInspector"/> (splicing plan IT2-T1, IT-R-01, NL-041): BOLT 2 fails the negotiation on a
/// <c>prevtx</c> that is not a transaction, a <c>prevtx_vout</c> out of range and a spent script that is not a witness
/// program.
/// </summary>
public class PrevTxInspectorTests
{
    private static readonly Key s_key = new(Convert.FromHexString(
                                                "1111111111111111111111111111111111111111111111111111111111111111"));

    public static TheoryData<string, string> AcceptedScripts => new()
    {
        { "P2WPKH", Hex(s_key.PubKey.WitHash.ScriptPubKey) },
        { "P2WSH", Hex(s_key.PubKey.ScriptPubKey.WitHash.ScriptPubKey) },
        { "P2TR", Hex(s_key.PubKey.GetTaprootFullPubKey().ScriptPubKey) },
        // BOLT 2 accepts every witness program: a future version and the smallest program (pay-to-anchor)
        { "witness v2", "5220" + new string('a', 64) },
        { "P2A", "51024e73" },
        { "v16 40 bytes", "6028" + new string('b', 80) }
    };

    public static TheoryData<string, string> RejectedScripts => new()
    {
        { "P2PKH", Hex(s_key.PubKey.Hash.ScriptPubKey) },
        { "P2SH", Hex(s_key.PubKey.ScriptPubKey.Hash.ScriptPubKey) },
        { "P2SH-P2WPKH", Hex(s_key.PubKey.WitHash.ScriptPubKey.Hash.ScriptPubKey) },
        { "bare P2PK", Hex(s_key.PubKey.ScriptPubKey) },
        { "bare multisig", Hex(PayToMultiSigTemplate.Instance.GenerateScriptPubKey(1, s_key.PubKey)) },
        { "OP_RETURN", "6a0401020304" },
        { "empty", "" },
        { "v0 1-byte program", "000101" },
        { "v0 41-byte program", "0029" + new string('c', 82) },
        { "program with a trailing opcode", "0014" + new string('d', 40) + "87" },
        { "OP_PUSHDATA1 program", "004c14" + new string('e', 40) },
        { "OP_1NEGATE version", "4f20" + new string('f', 64) }
    };

    [Theory]
    [MemberData(nameof(AcceptedScripts))]
    public void Given_AWitnessProgramOutput_When_Inspecting_Then_ItIsValid(string name, string scriptHex)
    {
        // Arrange
        var (tx, bytes) = CreateTx(Convert.FromHexString(scriptHex));
        var inspector = new PrevTxInspector();

        // Act
        var result = inspector.Inspect(bytes, 1);

        // Assert
        Assert.True(result.IsValid, name);
        Assert.True(result.IsWitnessProgram, name);
        Assert.Null(result.FailureReason);
        Assert.Equal(tx.GetHash().ToBytes(), (byte[])result.TxId!.Value);
        Assert.Equal(2, result.OutputCount);
        Assert.Equal(LightningMoney.Satoshis(70_000), result.Amount);
        Assert.Equal(scriptHex, Convert.ToHexString((byte[])result.ScriptPubKey!.Value).ToLowerInvariant());
    }

    [Theory]
    [MemberData(nameof(RejectedScripts))]
    public void Given_ANonWitnessOutput_When_Inspecting_Then_ItIsRejected(string name, string scriptHex)
    {
        // Arrange
        var (tx, bytes) = CreateTx(Convert.FromHexString(scriptHex));
        var inspector = new PrevTxInspector();

        // Act
        var result = inspector.Inspect(bytes, 1);

        // Assert
        Assert.False(result.IsValid, name);
        Assert.False(result.IsWitnessProgram, name);
        Assert.Contains("not a witness program", result.FailureReason);
        Assert.Equal(tx.GetHash().ToBytes(), (byte[])result.TxId!.Value);
        Assert.Equal(LightningMoney.Satoshis(70_000), result.Amount);
    }

    [Fact]
    public void Given_AVoutOutOfRange_When_Inspecting_Then_ItIsInvalidWithTheOutputCount()
    {
        // Arrange
        var (tx, bytes) = CreateTx(s_key.PubKey.WitHash.ScriptPubKey.ToBytes());
        var inspector = new PrevTxInspector();

        // Act
        var result = inspector.Inspect(bytes, 2);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(2, result.OutputCount);
        Assert.Equal(tx.GetHash().ToBytes(), (byte[])result.TxId!.Value);
        Assert.Null(result.Amount);
        Assert.Null(result.ScriptPubKey);
        Assert.Contains("prevtx_vout 2", result.FailureReason);
    }

    public static TheoryData<string, string> InvalidTransactions
    {
        get
        {
            var (_, bytes) = CreateTx(s_key.PubKey.WitHash.ScriptPubKey.ToBytes());
            var hex = Convert.ToHexString(bytes);
            return new TheoryData<string, string>
            {
                { "empty", "" },
                { "garbage", "deadbeef" },
                { "truncated", hex[..(hex.Length - 10)] },
                { "trailing byte", hex + "00" },
                { "two transactions", hex + hex },
                // version, no input (read as a segwit marker without a flag), no output, locktime
                { "no input", "02000000" + "00" + "00" + "00000000" }
            };
        }
    }

    [Theory]
    [MemberData(nameof(InvalidTransactions))]
    public void Given_BytesThatAreNotExactlyOneTransaction_When_Inspecting_Then_ItIsInvalidWithoutThrowing(
        string name, string hex)
    {
        // Arrange
        var inspector = new PrevTxInspector();

        // Act
        var result = inspector.Inspect(Convert.FromHexString(hex), 0);

        // Assert
        Assert.False(result.IsValid, name);
        Assert.Null(result.TxId);
        Assert.Equal(0, result.OutputCount);
        Assert.Equal("prevtx is not a valid transaction", result.FailureReason);
    }

    [Fact]
    public void Given_ATransactionWithWitnesses_When_Inspecting_Then_TheTxIdExcludesThem()
    {
        // Arrange
        var (tx, _) = CreateTx(s_key.PubKey.WitHash.ScriptPubKey.ToBytes());
        tx.Inputs[0].WitScript = new WitScript(Op.GetPushOp([1, 2, 3]));
        var inspector = new PrevTxInspector();

        // Act
        var result = inspector.Inspect(tx.ToBytes(), 1);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(tx.GetHash().ToBytes(), (byte[])result.TxId!.Value);
    }

    [Theory]
    [InlineData(1u, true)]
    [InlineData(6u, true)]
    [InlineData(0u, false)]
    public async Task Given_ConfirmationsFromBitcoind_When_CheckingConfirmed_Then_AtLeastOneIsConfirmed(
        uint confirmations, bool expected)
    {
        // Arrange
        var txId = new TxId(Enumerable.Repeat((byte)7, 32).ToArray());
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetTransactionConfirmationsAsync(new uint256((byte[])txId))).ReturnsAsync(confirmations);
        var inspector = new PrevTxInspector(null, chain.Object);

        // Act
        var confirmed = await inspector.IsConfirmedAsync(txId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, confirmed);
    }

    [Fact]
    public async Task Given_BitcoindFails_When_CheckingConfirmed_Then_TheInputIsUnconfirmed()
    {
        // Arrange
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetTransactionConfirmationsAsync(It.IsAny<uint256>()))
             .ThrowsAsync(new InvalidOperationException("rpc down"));
        var inspector = new PrevTxInspector(null, chain.Object);

        // Act
        var confirmed = await inspector.IsConfirmedAsync(TxId.One, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(confirmed);
    }

    [Fact]
    public async Task Given_NoChainService_When_CheckingConfirmed_Then_TheInputIsUnconfirmed()
    {
        // Arrange
        var inspector = new PrevTxInspector();

        // Act
        var confirmed = await inspector.IsConfirmedAsync(TxId.One, TestContext.Current.CancellationToken);
        var outputConfirmed = await inspector.IsOutputConfirmedAsync(TxId.One, 0,
                                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.False(confirmed);
        Assert.False(outputConfirmed);
    }

    [Fact]
    public async Task Given_AConfirmedUnspentOutputWithoutTxIndex_When_CheckingTheOutput_Then_ItIsConfirmed()
    {
        // Arrange: getrawtransaction knows nothing (no txindex), gettxout sees the output at height 100
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetTransactionConfirmationsAsync(It.IsAny<uint256>())).ReturnsAsync(0u);
        chain.Setup(c => c.GetConfirmedUnspentOutputAsync(It.Is<OutPoint>(o => o.N == 3)))
             .ReturnsAsync((new TxOut(Money.Satoshis(1_000), s_key.PubKey.WitHash.ScriptPubKey), 100u));
        var inspector = new PrevTxInspector(null, chain.Object);

        // Act
        var confirmed = await inspector.IsOutputConfirmedAsync(TxId.One, 3, TestContext.Current.CancellationToken);
        var otherConfirmed = await inspector.IsOutputConfirmedAsync(TxId.One, 4,
                                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.True(confirmed);
        Assert.False(otherConfirmed);
    }

    [Fact]
    public async Task Given_ACancelledToken_When_CheckingConfirmed_Then_ItThrows()
    {
        // Arrange
        var inspector = new PrevTxInspector(null, new Mock<IBitcoinChainService>().Object);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspector.IsConfirmedAsync(TxId.One, cts.Token));
    }

    private static (Transaction Tx, byte[] Bytes) CreateTx(byte[] secondOutputScript)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Version = 2;
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256(Enumerable.Repeat((byte)9, 32).ToArray()), 1)));
        tx.Outputs.Add(new TxOut(Money.Satoshis(50_000), s_key.PubKey.WitHash.ScriptPubKey));
        tx.Outputs.Add(new TxOut(Money.Satoshis(70_000), new Script(secondOutputScript)));
        return (tx, tx.ToBytes());
    }

    private static string Hex(Script script) => Convert.ToHexString(script.ToBytes()).ToLowerInvariant();
}