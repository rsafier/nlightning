using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Infrastructure.Bitcoin.Builders;

using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Interfaces;
using Outputs;

/// <summary>
/// Builds the BOLT 3 legacy closing transaction from a Domain <see cref="ClosingTransactionModel"/> (N10-T2).
/// </summary>
public class ClosingTransactionBuilder : IClosingTransactionBuilder
{
    /// <summary>BOLT 3: the legacy closing transaction is version 2.</summary>
    public const uint ClosingTransactionVersion = 2;

    private readonly Network _network;

    public ClosingTransactionBuilder(IOptions<NodeOptions> nodeOptions)
    {
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ??
                   throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));
    }

    /// <inheritdoc />
    public SignedTransaction Build(ClosingTransactionModel transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var funding = transaction.FundingOutput;

        var tx = Transaction.Create(_network);
        tx.Version = ClosingTransactionVersion;
        tx.LockTime = LockTime.Zero;
        tx.Inputs.Add(new OutPoint(new uint256(funding.TransactionId!.Value), funding.Index!.Value), null, null,
                      Sequence.Final);

        // BOLT 3 "Transaction Output Ordering" (BIP 69): amount, then scriptpubkey compared as by memcmp over the
        // common prefix, the shorter one first
        foreach (var output in transaction.Outputs.OrderBy(o => o.Amount.Satoshi)
                                          .ThenBy(o => (byte[])o.ScriptPubKey, ScriptComparer.Instance))
            tx.Outputs.Add(new TxOut(Money.Satoshis(output.Amount.Satoshi), new Script((byte[])output.ScriptPubKey)));

        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
    }

    /// <inheritdoc />
    public SignedTransaction AddWitness(SignedTransaction unsignedTransaction, FundingOutputInfo fundingOutput,
                                       CompactSignature localSignature, CompactSignature remoteSignature)
    {
        ArgumentNullException.ThrowIfNull(unsignedTransaction);
        ArgumentNullException.ThrowIfNull(fundingOutput);
        ArgumentNullException.ThrowIfNull(localSignature);
        ArgumentNullException.ThrowIfNull(remoteSignature);

        var localKey = new PubKey(fundingOutput.LocalFundingPubKey);
        var remoteKey = new PubKey(fundingOutput.RemoteFundingPubKey);
        var funding = new FundingOutput(fundingOutput.Amount, localKey, remoteKey);

        var local = ToTransactionSignature(localSignature, nameof(localSignature)).ToBytes();
        var remote = ToTransactionSignature(remoteSignature, nameof(remoteSignature)).ToBytes();
        var localFirst = PubKeyComparer.Instance.Compare(localKey, remoteKey) < 0;

        var tx = Transaction.Load(unsignedTransaction.RawTxBytes, _network);
        if (tx.Inputs.Count != 1)
            throw new ArgumentException("A closing transaction has exactly one input", nameof(unsignedTransaction));

        tx.Inputs[0].WitScript = new WitScript(new[]
        {
            Array.Empty<byte>(), localFirst ? local : remote, localFirst ? remote : local,
            funding.RedeemScript.ToBytes()
        });

        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes(), [localSignature, remoteSignature]);
    }

    private static TransactionSignature ToTransactionSignature(CompactSignature signature, string paramName)
    {
        if (!ECDSASignature.TryParseFromCompact(signature, out var ecdsaSignature))
            throw new ArgumentException("Invalid compact signature", paramName);

        return new TransactionSignature(ecdsaSignature, SigHash.All);
    }

    private sealed class ScriptComparer : IComparer<byte[]>
    {
        public static readonly ScriptComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}