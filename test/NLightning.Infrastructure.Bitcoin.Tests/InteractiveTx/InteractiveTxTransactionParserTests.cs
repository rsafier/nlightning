using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.InteractiveTx;

using Infrastructure.Bitcoin.InteractiveTx;

/// <summary>
/// <see cref="InteractiveTxTransactionParser"/>: the Domain view of a transaction that Application checks before it
/// signs and reads its witnesses from (splicing plan IT2-T3).
/// </summary>
public class InteractiveTxTransactionParserTests
{
    private static readonly Key s_key = new(Enumerable.Repeat((byte)0x21, 32).ToArray());

    [Fact]
    public void Given_AnUnsignedTransaction_When_Parsing_Then_EveryFieldIsRead()
    {
        // Arrange
        var tx = CreateTx();

        // Act
        var parsed = new InteractiveTxTransactionParser().TryParse(tx.ToBytes());

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(tx.GetHash().ToBytes(), (byte[])parsed.TxId);
        Assert.Equal(2u, parsed.Version);
        Assert.Equal(120u, parsed.Locktime);
        Assert.Equal(2, parsed.Inputs.Count);
        Assert.Equal(tx.Inputs[1].PrevOut.Hash.ToBytes(), (byte[])parsed.Inputs[1].PrevTxId);
        Assert.Equal(7u, parsed.Inputs[1].PrevTxVout);
        Assert.Equal(0xFFFFFFFEu, parsed.Inputs[1].Sequence);
        Assert.All(parsed.Inputs, i => Assert.Null(i.Witness));
        var output = Assert.Single(parsed.Outputs);
        Assert.Equal(12_345, output.Amount.Satoshi);
        Assert.Equal(s_key.PubKey.WitHash.ScriptPubKey.ToBytes(), (byte[])output.ScriptPubKey);
    }

    [Fact]
    public void Given_ASignedTransaction_When_Parsing_Then_EachWitnessIsItsTxSignaturesForm()
    {
        // Arrange: a witness on input 1 only
        var tx = CreateTx();
        tx.Inputs[1].WitScript = new WitScript(new[] { new byte[] { 0xAA, 0xBB }, new byte[] { 0x01 } });

        // Act
        var parsed = new InteractiveTxTransactionParser().TryParse(tx.ToBytes());

        // Assert: txid unchanged by witnesses; count 2, item 0xAABB, item 0x01
        Assert.NotNull(parsed);
        Assert.Equal(tx.GetHash().ToBytes(), (byte[])parsed.TxId);
        Assert.Null(parsed.Inputs[0].Witness);
        Assert.Equal(new byte[] { 0x02, 0x02, 0xAA, 0xBB, 0x01, 0x01 }, (byte[])parsed.Inputs[1].Witness!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0200")]
    public void Given_BytesThatAreNotOneTransaction_When_Parsing_Then_ItIsNull(string hex)
    {
        // Act
        var parsed = new InteractiveTxTransactionParser().TryParse(Convert.FromHexString(hex));

        // Assert
        Assert.Null(parsed);
    }

    [Fact]
    public void Given_TrailingBytes_When_Parsing_Then_ItIsNull()
    {
        // Arrange
        var bytes = CreateTx().ToBytes().Concat(new byte[] { 0x00 }).ToArray();

        // Act & Assert
        Assert.Null(new InteractiveTxTransactionParser().TryParse(bytes));
    }

    private static Transaction CreateTx()
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Version = 2;
        tx.LockTime = new LockTime(120);
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256(Enumerable.Repeat((byte)1, 32).ToArray()), 0))
        {
            Sequence = new Sequence(0xFFFFFFFD)
        });
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256(Enumerable.Repeat((byte)2, 32).ToArray()), 7))
        {
            Sequence = new Sequence(0xFFFFFFFE)
        });
        tx.Outputs.Add(new TxOut(Money.Satoshis(12_345), s_key.PubKey.WitHash.ScriptPubKey));
        return tx;
    }
}