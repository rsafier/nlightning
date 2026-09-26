using NBitcoin;
using NLightning.Integration.Tests.BOLT3;
using NLightning.Integration.Tests.BOLT3.Vectors;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Builders;

using Domain.Bitcoin.ValueObjects;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Onchain;

/// <summary>
/// Shared pieces of the BOLT 5 sweep, claim and penalty tests: the Appendix C commitment on chain, its output map for
/// a given case, script execution of a signed spend and weights.
/// </summary>
internal static class SweepTestKit
{
    /// <summary>A P2WPKH wallet destination.</summary>
    public static readonly byte[] Destination =
        new Key(Enumerable.Repeat((byte)0x55, 32).ToArray()).PubKey.WitHash.ScriptPubKey.ToBytes();

    public static SweepTransactionBuilder CreateBuilder() =>
        new(Microsoft.Extensions.Options.Options.Create(new NodeOptions()));

    /// <summary>The vector's commitment (node A's commitment 42) as on chain, and its map for <paramref name="case"/>
    /// (Local: we are node A; Remote/Revoked: we are node B).</summary>
    public static (Transaction CommitTx, CommitmentOutputMap Map) MapAppendixC(Bolt3CommitmentVector vector,
                                                                               CommitmentCase commitmentCase,
                                                                               bool hasAnchors = false)
    {
        var harness = new Bolt3VectorHarness(vector, hasAnchors, asNodeB: commitmentCase != CommitmentCase.Local);
        var commitTx = Transaction.Parse(vector.CommitTxHex, Network.Main);
        var mapper = new CommitmentOutputMapper(harness.Factory, harness.CommitmentBuilder);
        var map = mapper.Map(harness.Channel, harness.Spec, commitmentCase, Bolt3AppendixCVectors.CommitmentNumber,
                             commitmentCase == CommitmentCase.Local
                                 ? null
                                 : Integration.Tests.BOLT3.Mocks.Bolt3TestCommitmentKeyDerivationService
                                              .LocalPerCommitmentPoint,
                             ChainTxMapper.FromTransaction(commitTx));
        Assert.True(map.TxIdMatched);
        return (commitTx, map);
    }

    /// <summary>Runs input <paramref name="index"/>'s witness against the output it spends.</summary>
    public static bool Verifies(SignedTransaction signed, int index, TxOut spent, out ScriptError error)
    {
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        return tx.Inputs.AsIndexedInputs().ElementAt(index).VerifyScript(spent, out error);
    }

    /// <summary>Asserts that every input of <paramref name="signed"/> spends its output.</summary>
    public static void AssertAllInputsVerify(SignedTransaction signed, IReadOnlyList<TxOut> spentOutputs)
    {
        for (var i = 0; i < spentOutputs.Count; i++)
            Assert.True(Verifies(signed, i, spentOutputs[i], out var error), $"input {i}: {error}");
    }

    /// <summary>The BIP 141 weight of a serialized transaction.</summary>
    public static long Weight(SignedTransaction signed)
    {
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        var baseSize = tx.GetSerializedSize(TransactionOptions.None);
        var totalSize = tx.GetSerializedSize();
        return 3L * baseSize + totalSize;
    }

    /// <summary>The serialized witness of input <paramref name="index"/> (item count and items).</summary>
    public static int WitnessSize(SignedTransaction signed, int index) =>
        Transaction.Load(signed.RawTxBytes, Network.Main).Inputs[index].WitScript.ToBytes().Length;

    /// <summary>The outputs of the vector's HTLC transactions, keyed by their txid.</summary>
    public static IEnumerable<(Transaction HtlcTx, Bolt3HtlcTxVector Vector)> HtlcTransactions(
        Bolt3CommitmentVector vector) =>
        vector.HtlcTxs.Select(h => (Transaction.Parse(h.TxHex, Network.Main), h));

    public static TxId TxIdOf(Transaction tx) => tx.GetHash().ToBytes();
}