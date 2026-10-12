namespace NLightning.Infrastructure.Bitcoin.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// <see cref="IInteractiveTxTransactionParser"/> over the strict <see cref="InteractiveTxTransactionReader"/>: exactly
/// one transaction, no trailing bytes; each input's witness in the BIP 141 serialization <c>tx_signatures</c> carries.
/// </summary>
public sealed class InteractiveTxTransactionParser : IInteractiveTxTransactionParser
{
    /// <inheritdoc />
    public ParsedInteractiveTx? TryParse(ReadOnlyMemory<byte> transaction)
    {
        if (!InteractiveTxTransactionReader.TryReadTransaction(transaction.Span, out var tx) || tx is null)
            return null;

        var inputs = tx.Inputs.Select(i => new ParsedTxInput(
                                          new TxId(i.PrevOut.Hash.ToBytes()), i.PrevOut.N, i.Sequence.Value,
                                          i.WitScript is { PushCount: > 0 } witScript
                                              ? (Witness?)new Witness(
                                                  InteractiveTxTransactionReader.WriteWitness(witScript))
                                              : null))
                       .ToList();
        var outputs = tx.Outputs.Select(o => new ParsedTxOutput(LightningMoney.Satoshis(o.Value.Satoshi),
                                                                 o.ScriptPubKey.ToBytes()))
                        .ToList();

        return new ParsedInteractiveTx(new TxId(tx.GetHash().ToBytes()), tx.Version, tx.LockTime.Value, inputs,
                                       outputs);
    }
}