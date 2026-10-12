namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Bitcoin.ValueObjects;

/// <summary>An input of a <see cref="ParsedInteractiveTx"/>.</summary>
/// <param name="PrevTxId">The txid of the spent output.</param>
/// <param name="PrevTxVout">The index of the spent output.</param>
/// <param name="Sequence">Its <c>nSequence</c>.</param>
/// <param name="Witness">Its witness as the BIP 141 witness stack serialization that <c>tx_signatures</c> carries, or
/// null when the input has no witness item.</param>
public sealed record ParsedTxInput(TxId PrevTxId, uint PrevTxVout, uint Sequence, Witness? Witness);