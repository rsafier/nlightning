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
/// <param name="ScriptPubKey">The funding script: the P2WSH 2-of-2, or the MuSig2 P2TR output of a simple taproot
/// channel.</param>
/// <param name="InputWeight">The weight of the input once signed (both signatures and the witness script, or the
/// key-path signature of a taproot funding), charged to the initiator (IT-S-03).</param>
/// <param name="IsTaproot">A simple taproot channel's funding (NL-965, BOLTs PR #1324): the input is spent by MuSig2
/// key path, so <c>tx_signatures</c> carries <c>shared_input_partial_signature</c> (type 2) instead of the ECDSA
/// <c>shared_input_signature</c> (type 0).</param>
public sealed record SharedFundingInput(
    TxId TxId,
    uint Vout,
    LightningMoney Amount,
    BitcoinScript ScriptPubKey,
    int InputWeight,
    bool IsTaproot = false);