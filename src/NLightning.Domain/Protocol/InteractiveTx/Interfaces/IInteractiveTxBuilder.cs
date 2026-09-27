namespace NLightning.Domain.Protocol.InteractiveTx.Interfaces;

using Bitcoin.ValueObjects;
using Models;

/// <summary>
/// Builds the transaction a completed interactive-tx negotiation describes and assembles it once signed (BOLT 2
/// "Interactive Transaction Construction"). Implemented by <c>InteractiveTxBuilder</c> in Infrastructure.Bitcoin (lane
/// IT-B, IT2-T2), proven byte-exact against BOLT 3 Appendix G.
/// </summary>
public interface IInteractiveTxBuilder
{
    /// <summary>
    /// Builds the unsigned transaction: version 2, <paramref name="locktime"/>, inputs and outputs each sorted by
    /// ascending <c>serial_id</c> (BOLT 2), and its txid and estimated signed weight.
    /// </summary>
    /// <param name="locktime">The negotiated <c>nLockTime</c>.</param>
    /// <param name="inputs">Every input of both sides, in any order.</param>
    /// <param name="outputs">Every output of both sides, in any order.</param>
    /// <exception cref="ArgumentException">Duplicate <c>serial_id</c>s, or no input or no output.</exception>
    ConstructedInteractiveTx Build(uint locktime, IReadOnlyList<InteractiveTxInput> inputs,
                                   IReadOnlyList<InteractiveTxOutput> outputs);

    /// <summary>
    /// Puts the witnesses on <paramref name="transaction"/>: one per input, keyed by the input's <c>serial_id</c>
    /// (ours, the peer's from its <c>tx_signatures</c>, and the shared input's 2-of-2 witness). Each
    /// <see cref="Witness"/> is the BIP 141 witness stack serialization carried by <c>tx_signatures</c>
    /// (<c>witness_data</c>).
    /// </summary>
    /// <returns>The fully signed transaction, whose txid is <see cref="ConstructedInteractiveTx.TxId"/>.</returns>
    /// <exception cref="ArgumentException">An input has no witness, or a witness does not parse.</exception>
    SignedTransaction Finalize(ConstructedInteractiveTx transaction,
                               IReadOnlyDictionary<ulong, Witness> witnessesBySerialId);
}