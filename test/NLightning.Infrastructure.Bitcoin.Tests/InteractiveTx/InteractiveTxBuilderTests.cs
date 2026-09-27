using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Infrastructure.Bitcoin.InteractiveTx;

/// <summary>
/// <see cref="InteractiveTxBuilder"/> (splicing plan IT2-T2); the byte-exact proof is BOLT 3 Appendix G in
/// <c>Integration.Tests/BOLT3/AppendixGVectorTests</c>.
/// </summary>
public class InteractiveTxBuilderTests
{
    private static readonly Key s_key = new(Convert.FromHexString(
                                                "2222222222222222222222222222222222222222222222222222222222222222"));

    private static readonly byte[] s_p2Wpkh = s_key.PubKey.WitHash.ScriptPubKey.ToBytes();
    private static readonly byte[] s_p2Tr = s_key.PubKey.GetTaprootFullPubKey().ScriptPubKey.ToBytes();
    private static readonly byte[] s_p2Wsh = s_key.PubKey.ScriptPubKey.WitHash.ScriptPubKey.ToBytes();

    [Fact]
    public void Given_InputsAndOutputsOutOfOrder_When_Building_Then_TheyAreSortedBySerialId()
    {
        // Arrange
        var builder = new InteractiveTxBuilder();
        var inputs = new[] { Input(9, 1), Input(2, 2), Input(4, 3) };
        var outputs = new[] { Output(7, 1_000), Output(0, 2_000, true), Output(3, 3_000) };

        // Act
        var constructed = builder.Build(500, inputs, outputs);
        var tx = Transaction.Load(constructed.UnsignedTx, Network.RegTest);

        // Assert
        Assert.Equal(new ulong[] { 2, 4, 9 }, constructed.Inputs.Select(i => i.SerialId));
        Assert.Equal(new ulong[] { 0, 3, 7 }, constructed.Outputs.Select(o => o.SerialId));
        Assert.Equal(new uint[] { 2, 3, 1 }, tx.Inputs.Select(i => i.PrevOut.N));
        Assert.Equal(new long[] { 2_000, 3_000, 1_000 }, tx.Outputs.Select(o => o.Value.Satoshi));
        Assert.Equal(0u, constructed.SharedOutputIndex);
        Assert.Equal(500u, tx.LockTime.Value);
        Assert.Equal(2u, tx.Version);
        Assert.Equal(tx.GetHash().ToBytes(), (byte[])constructed.TxId);
        Assert.False(tx.HasWitness);
    }

    [Fact]
    public void Given_EachInputsSequence_When_Building_Then_ItIsKept()
    {
        // Arrange
        var builder = new InteractiveTxBuilder();
        var inputs = new[] { Input(0, 0, sequence: 0xFFFFFFFD), Input(1, 1, sequence: 5) };

        // Act
        var tx = Transaction.Load(builder.Build(0, inputs, [Output(0 + 2, 1_000)]).UnsignedTx, Network.RegTest);

        // Assert
        Assert.Equal(new uint[] { 0xFFFFFFFD, 5 }, tx.Inputs.Select(i => i.Sequence.Value));
    }

    [Fact]
    public void Given_NoSharedOutput_When_Building_Then_TheSharedOutputIndexIsNull()
    {
        // Act
        var constructed = new InteractiveTxBuilder().Build(0, [Input(0, 0)], [Output(2, 1_000)]);

        // Assert
        Assert.Null(constructed.SharedOutputIndex);
    }

    [Fact]
    public void Given_AUsedSerialIdAcrossInputsAndOutputs_When_Building_Then_ItThrows()
    {
        // Arrange
        var builder = new InteractiveTxBuilder();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => builder.Build(0, [Input(4, 0), Input(4, 1)], [Output(6, 1_000)]));
        Assert.Throws<ArgumentException>(() => builder.Build(0, [Input(4, 0)], [Output(6, 1_000), Output(6, 2)]));
        Assert.Throws<ArgumentException>(() => builder.Build(0, [Input(4, 0)], [Output(4, 1_000)]));
    }

    [Fact]
    public void Given_NoInputOrNoOutputOrTwoSharedOutputs_When_Building_Then_ItThrows()
    {
        // Arrange
        var builder = new InteractiveTxBuilder();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => builder.Build(0, [], [Output(0, 1_000)]));
        Assert.Throws<ArgumentException>(() => builder.Build(0, [Input(0, 0)], []));
        Assert.Throws<ArgumentException>(() => builder.Build(0, [Input(0, 0)],
                                                             [Output(2, 1_000, true), Output(4, 1_000, true)]));
        Assert.Throws<ArgumentException>(() => builder.Build(0, [Input(0, 0)],
                                                             [new InteractiveTxOutput(
                                                                  2, InteractiveTxParty.Local, 1_500L,
                                                                  s_p2Wpkh, false)]));
    }

    [Fact]
    public void Given_InputTypes_When_Building_Then_TheWeightEstimateCountsTheirWitnesses()
    {
        // Arrange
        var builder = new InteractiveTxBuilder();
        var inputs = new[]
        {
            Input(0, 0, s_p2Wpkh), Input(2, 1, s_p2Tr), Input(4, 2, s_p2Wsh),
            Input(6, 3, s_p2Wsh, shared: true)
        };

        // Act
        var constructed = builder.Build(0, inputs, [Output(8, 1_000)]);
        var tx = Transaction.Load(constructed.UnsignedTx, Network.RegTest);

        // Assert: size x 4 + marker/flag + 108 + 67 + 107 + 222
        Assert.Equal(tx.GetSerializedSize(TransactionOptions.None) * 4 + 2 + 108 + 67 + 107 + 222,
                     constructed.EstimatedWeight);
    }

    [Fact]
    public void Given_EveryWitness_When_Finalizing_Then_TheyArePlacedByInputSerialId()
    {
        // Arrange
        var builder = new InteractiveTxBuilder();
        var constructed = builder.Build(0, [Input(5, 0), Input(2, 1)], [Output(4, 1_000)]);
        var witnesses = new Dictionary<ulong, Witness>
        {
            [5] = Serialize([0x55], [0x56, 0x57]),
            [2] = Serialize([0x22])
        };

        // Act
        var signed = builder.Finalize(constructed, witnesses);
        var tx = Transaction.Load(signed.RawTxBytes, Network.RegTest);

        // Assert: serial 2 is input 0
        Assert.Equal([[0x22]], tx.Inputs[0].WitScript.Pushes.Select(p => p.ToArray()));
        Assert.Equal([[0x55], [0x56, 0x57]], tx.Inputs[1].WitScript.Pushes.Select(p => p.ToArray()));
        Assert.Equal((byte[])constructed.TxId, (byte[])signed.TxId);
    }

    [Fact]
    public void Given_AMissingUnknownOrMalformedWitness_When_Finalizing_Then_ItThrows()
    {
        // Arrange
        var builder = new InteractiveTxBuilder();
        var constructed = builder.Build(0, [Input(0, 0), Input(1, 1)], [Output(2, 1_000)]);
        var good = Serialize([0x01]);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => builder.Finalize(constructed, new Dictionary<ulong, Witness>
        {
            [0] = good
        }));
        Assert.Throws<ArgumentException>(() => builder.Finalize(constructed, new Dictionary<ulong, Witness>
        {
            [0] = good,
            [1] = good,
            [3] = good
        }));
        Assert.Throws<ArgumentException>(() => builder.Finalize(constructed, new Dictionary<ulong, Witness>
        {
            [0] = good,
            [1] = new Witness([0x02, 0x01, 0x01]) // two items announced, one present
        }));
        Assert.Throws<ArgumentException>(() => builder.Finalize(constructed, new Dictionary<ulong, Witness>
        {
            [0] = good,
            [1] = new Witness([0x00]) // an empty witness signs nothing
        }));
    }

    private static InteractiveTxInput Input(ulong serialId, uint vout, byte[]? script = null, uint sequence = 0xFFFFFFFD,
                                            bool shared = false) =>
        new(serialId, serialId % 2 == 0 ? InteractiveTxParty.Local : InteractiveTxParty.Remote,
            new TxId(Enumerable.Repeat((byte)(vout + 1), 32).ToArray()), vout, sequence, LightningMoney.Satoshis(10_000),
            script ?? s_p2Wpkh, shared ? null : [0x00], shared);

    private static InteractiveTxOutput Output(ulong serialId, long sats, bool shared = false) =>
        new(serialId, serialId % 2 == 0 ? InteractiveTxParty.Local : InteractiveTxParty.Remote,
            LightningMoney.Satoshis(sats), s_p2Wpkh, shared);

    private static Witness Serialize(params byte[][] items) =>
        new(InteractiveTxTransactionReader.WriteWitness(new WitScript(items.Select(Op.GetPushOp).ToArray())));
}