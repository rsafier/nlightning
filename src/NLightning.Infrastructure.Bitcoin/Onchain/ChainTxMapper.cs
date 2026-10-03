using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Onchain;

using Domain.Onchain.Models;

/// <summary>
/// Maps NBitcoin transactions to the Domain's <see cref="ChainTx"/> (BOLT 5 plan §3.1). Txids and outpoints keep the
/// internal byte order used by <c>TxId</c> everywhere (<c>uint256.ToBytes()</c>).
/// </summary>
public static class ChainTxMapper
{
    /// <summary>Maps a parsed transaction.</summary>
    public static ChainTx FromTransaction(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var inputs = transaction.Inputs
                                .Select(input => new ChainTxInput(input.PrevOut.Hash.ToBytes(), input.PrevOut.N,
                                                                  input.Sequence.Value,
                                                                  input.WitScript is { } witness
                                                                      ? witness.Pushes.Select(p => p.ToArray())
                                                                               .ToArray()
                                                                      : []))
                                .ToArray();
        var outputs = transaction.Outputs
                                 .Select(output => new ChainTxOutput((ulong)output.Value.Satoshi,
                                                                     output.ScriptPubKey.ToBytes()))
                                 .ToArray();

        return new ChainTx(transaction.GetHash().ToBytes(), transaction.Version, transaction.LockTime.Value, inputs,
                           outputs);
    }

    /// <summary>Parses and maps raw transaction bytes; false (never an exception) when they are not a transaction.</summary>
    public static bool TryParse(byte[] rawTransaction, out ChainTx? chainTx)
    {
        chainTx = null;
        if (rawTransaction is null || rawTransaction.Length == 0)
            return false;

        try
        {
            var transaction = Transaction.Load(rawTransaction, Network.Main);
            if (transaction.Inputs.Count == 0 && transaction.Outputs.Count == 0)
                return false;

            chainTx = FromTransaction(transaction);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}