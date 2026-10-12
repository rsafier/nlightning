namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Bitcoin.ValueObjects;
using Money;

/// <summary>
/// What <see cref="Interfaces.IPrevTxInspector.Inspect"/> found in a <c>tx_add_input</c>'s <c>prevtx</c> (BOLT 2
/// IT-R-01, NL-041).
/// </summary>
/// <param name="IsValid">Whether <c>prevtx</c> parsed as a transaction and <c>prevtx_vout</c> is in range. When false,
/// <paramref name="FailureReason"/> says why and the other values are null (or 0) where they could not be read.</param>
/// <param name="TxId">The txid of <c>prevtx</c>; null when it did not parse.</param>
/// <param name="OutputCount">The number of outputs of <c>prevtx</c>; 0 when it did not parse.</param>
/// <param name="Amount">The value of output <c>prevtx_vout</c>; null when out of range.</param>
/// <param name="ScriptPubKey">The scriptPubKey of output <c>prevtx_vout</c>; null when out of range.</param>
/// <param name="IsWitnessProgram">Whether that scriptPubKey is a witness program (a 1-byte push of 0-16 followed by a
/// 2-40 byte push, BIP 141); the receiver fails the negotiation otherwise.</param>
/// <param name="FailureReason">Why the input is invalid, for the <c>tx_abort</c>; null when valid.</param>
public sealed record PrevTxInspection(
    bool IsValid,
    TxId? TxId,
    int OutputCount,
    LightningMoney? Amount,
    BitcoinScript? ScriptPubKey,
    bool IsWitnessProgram,
    string? FailureReason);