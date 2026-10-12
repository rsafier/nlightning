namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents an tx_add_input message.
/// </summary>
/// <remarks>
/// The tx_add_input message is used to add an input to the transaction (BOLT 2 "The tx_add_input Message", IT-W-01).
/// The message type is 66.
/// </remarks>
public sealed class TxAddInputMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new TxAddInputPayload Payload { get => (TxAddInputPayload)base.Payload; }

    /// <summary>
    /// <c>shared_input_txid</c> (TLV 0): set when the input is the channel's shared funding output (a splice), whose
    /// <c>prevtx</c> is then empty. Written and read strictly by <c>TxAddInputMessageTypeSerializer</c> through
    /// <c>SharedInputTxIdTlvConverter</c>.
    /// </summary>
    public SharedInputTxIdTlv? SharedInputTxIdTlv { get; }

    /// <summary>
    /// <c>prevtx_details</c> (BOLTs PR #1324 type 2, read as Eclair 0.14.3's 1111 too): the spent output of a taproot
    /// input sent without <c>prevtx</c> (NL-957). We never send it: our inputs always carry their <c>prevtx</c>.
    /// </summary>
    public PrevTxDetailsTlv? PrevTxDetailsTlv { get; }

    /// <param name="payload">The tx_add_input payload.</param>
    /// <param name="sharedInputTxIdTlv">The <c>shared_input_txid</c> TLV, if any.</param>
    /// <param name="prevTxDetailsTlv">The <c>prevtx_details</c> TLV, if any.</param>
    public TxAddInputMessage(TxAddInputPayload payload, SharedInputTxIdTlv? sharedInputTxIdTlv = null,
                             PrevTxDetailsTlv? prevTxDetailsTlv = null)
        : base(MessageTypes.TxAddInput, payload)
    {
        SharedInputTxIdTlv = sharedInputTxIdTlv;
        PrevTxDetailsTlv = prevTxDetailsTlv;

        if (SharedInputTxIdTlv is null && PrevTxDetailsTlv is null)
            return;

        Extension = new TlvStream();
        if (SharedInputTxIdTlv is not null)
            Extension.Add(SharedInputTxIdTlv);
        if (PrevTxDetailsTlv is not null)
            Extension.Add(PrevTxDetailsTlv);
    }
}