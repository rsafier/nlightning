namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Bitcoin.ValueObjects;
using Money;

/// <summary>
/// The channel's current funding output, spent by a splice as the shared input (BOLT 2 "Channel Splicing": sent by the
/// initiator in a <c>tx_add_input</c> with <c>prevtx_len</c> = 0 and <c>shared_input_txid</c>).
/// </summary>
/// <param name="TxId">The current funding txid.</param>
/// <param name="Vout">The current funding output index.</param>
/// <param name="Amount">The current channel capacity.</param>
/// <param name="ScriptPubKey">The P2WSH 2-of-2 funding script.</param>
/// <param name="InputWeight">The weight of the input once signed (both signatures and the witness script), charged to
/// the initiator (IT-S-03).</param>
public sealed record SharedFundingInput(
    TxId TxId,
    uint Vout,
    LightningMoney Amount,
    BitcoinScript ScriptPubKey,
    int InputWeight);