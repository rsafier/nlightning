namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Bitcoin.ValueObjects;

/// <summary>
/// A transaction read by <see cref="Interfaces.IInteractiveTxTransactionParser"/>.
/// </summary>
/// <param name="TxId">Its txid (witnesses excluded).</param>
/// <param name="Version">Its version.</param>
/// <param name="Locktime">Its <c>nLockTime</c>.</param>
/// <param name="Inputs">Its inputs, in transaction order.</param>
/// <param name="Outputs">Its outputs, in transaction order.</param>
public sealed record ParsedInteractiveTx(
    TxId TxId,
    uint Version,
    uint Locktime,
    IReadOnlyList<ParsedTxInput> Inputs,
    IReadOnlyList<ParsedTxOutput> Outputs);