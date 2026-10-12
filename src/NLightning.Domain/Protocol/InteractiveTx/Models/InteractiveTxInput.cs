namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Bitcoin.ValueObjects;
using Enums;
using Money;

/// <summary>
/// One input of an interactive-tx negotiation, as currently added by either side (BOLT 2 <c>tx_add_input</c>).
/// </summary>
/// <param name="SerialId">The <c>serial_id</c>: even when added by the initiator, odd otherwise (IT-S-01); the
/// transaction's inputs are sorted by it.</param>
/// <param name="AddedBy">The side that added it.</param>
/// <param name="PrevTxId">The txid of the spent output (from <c>prevtx</c>, or <c>shared_input_txid</c>).</param>
/// <param name="PrevTxVout">The index of the spent output (<c>prevtx_vout</c>).</param>
/// <param name="Sequence">The input's <c>nSequence</c>, at most 0xFFFFFFFD.</param>
/// <param name="Amount">The value of the spent output.</param>
/// <param name="ScriptPubKey">The scriptPubKey of the spent output (a witness program, IT-R-01).</param>
/// <param name="PrevTx">The serialized previous transaction (<c>prevtx</c>); null for the shared input, which is sent
/// with <c>prevtx_len</c> = 0.</param>
/// <param name="IsShared">Whether this is the channel's shared funding input (a splice, <c>shared_input_txid</c>).</param>
public sealed record InteractiveTxInput(
    ulong SerialId,
    InteractiveTxParty AddedBy,
    TxId PrevTxId,
    uint PrevTxVout,
    uint Sequence,
    LightningMoney Amount,
    BitcoinScript ScriptPubKey,
    byte[]? PrevTx,
    bool IsShared);