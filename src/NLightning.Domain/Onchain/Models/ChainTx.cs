namespace NLightning.Domain.Onchain.Models;

using Bitcoin.ValueObjects;

/// <summary>
/// A Bitcoin transaction seen on chain, as the pure on-chain logic needs it (BOLT 5 plan §3.1). Built from the raw
/// transaction by <c>Infrastructure.Bitcoin/Onchain/ChainTxMapper</c>; Domain never parses Bitcoin bytes itself.
/// </summary>
/// <param name="TxId">The transaction id (internal byte order, as <see cref="Bitcoin.ValueObjects.TxId"/> everywhere).</param>
/// <param name="Version">The transaction version.</param>
/// <param name="LockTime">The raw <c>nLockTime</c>.</param>
/// <param name="Inputs">The inputs, in order.</param>
/// <param name="Outputs">The outputs, in order (the index is the vout).</param>
public sealed record ChainTx(
    TxId TxId,
    uint Version,
    uint LockTime,
    IReadOnlyList<ChainTxInput> Inputs,
    IReadOnlyList<ChainTxOutput> Outputs)
{
    /// <summary>
    /// The index of the input that spends <paramref name="txId"/>:<paramref name="vout"/>, or -1.
    /// </summary>
    public int IndexOfInputSpending(TxId txId, uint vout)
    {
        for (var i = 0; i < Inputs.Count; i++)
        {
            if (Inputs[i].PreviousVout == vout && Inputs[i].PreviousTxId == txId)
                return i;
        }

        return -1;
    }
}

/// <summary>
/// One input of a <see cref="ChainTx"/>.
/// </summary>
/// <param name="PreviousTxId">The txid of the spent output.</param>
/// <param name="PreviousVout">The index of the spent output.</param>
/// <param name="Sequence">The raw <c>nSequence</c>.</param>
/// <param name="Witness">The witness stack items, in order (empty for a non-segwit input).</param>
public sealed record ChainTxInput(TxId PreviousTxId, uint PreviousVout, uint Sequence, IReadOnlyList<byte[]> Witness);

/// <summary>
/// One output of a <see cref="ChainTx"/>.
/// </summary>
/// <param name="AmountSat">The amount in satoshis.</param>
/// <param name="ScriptPubKey">The output script.</param>
public sealed record ChainTxOutput(ulong AmountSat, byte[] ScriptPubKey);