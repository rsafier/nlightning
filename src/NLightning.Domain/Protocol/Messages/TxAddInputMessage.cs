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

    /// <param name="payload">The tx_add_input payload.</param>
    /// <param name="sharedInputTxIdTlv">The <c>shared_input_txid</c> TLV, if any.</param>
    public TxAddInputMessage(TxAddInputPayload payload, SharedInputTxIdTlv? sharedInputTxIdTlv = null)
        : base(MessageTypes.TxAddInput, payload)
    {
        SharedInputTxIdTlv = sharedInputTxIdTlv;

        if (SharedInputTxIdTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(SharedInputTxIdTlv);
        }
    }
}