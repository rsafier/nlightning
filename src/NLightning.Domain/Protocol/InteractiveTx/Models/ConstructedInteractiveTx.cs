namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Bitcoin.ValueObjects;

/// <summary>
/// The unsigned transaction a completed negotiation describes (BOLT 2: after two consecutive <c>tx_complete</c>),
/// built by <see cref="Interfaces.IInteractiveTxBuilder.Build"/>.
/// </summary>
/// <param name="TxId">Its txid (witnesses do not change it); <c>tx_signatures</c> carries it (IT-SIG-02).</param>
/// <param name="UnsignedTx">The serialized transaction without witnesses.</param>
/// <param name="Locktime">The negotiated <c>nLockTime</c>.</param>
/// <param name="Inputs">The inputs in transaction order (ascending <c>serial_id</c>).</param>
/// <param name="Outputs">The outputs in transaction order (ascending <c>serial_id</c>).</param>
/// <param name="EstimatedWeight">The weight of the signed transaction, estimated from the inputs' types (the
/// <c>tx_complete</c> check against 400,000, IT-R-04).</param>
/// <param name="SharedOutputIndex">The index of the shared funding output, or null when there is none.</param>
public sealed record ConstructedInteractiveTx(
    TxId TxId,
    byte[] UnsignedTx,
    uint Locktime,
    IReadOnlyList<InteractiveTxInput> Inputs,
    IReadOnlyList<InteractiveTxOutput> Outputs,
    long EstimatedWeight,
    uint? SharedOutputIndex);