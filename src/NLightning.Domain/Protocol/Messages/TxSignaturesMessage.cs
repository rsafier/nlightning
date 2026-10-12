namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a tx_signatures message.
/// </summary>
/// <remarks>
/// The tx_signatures message signals the provision of transaction signatures (BOLT 2 "The tx_signatures Message",
/// IT-W-03).
/// The message type is 71.
/// </remarks>
public sealed class TxSignaturesMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new TxSignaturesPayload Payload { get => (TxSignaturesPayload)base.Payload; }

    /// <summary>
    /// <c>shared_input_signature</c> (TLV 0): the sender's signature of the shared funding input of a splice. Written
    /// and read strictly by <c>TxSignaturesMessageTypeSerializer</c> through <c>SharedInputSignatureTlvConverter</c>.
    /// </summary>
    public SharedInputSignatureTlv? SharedInputSignatureTlv { get; }

    /// <summary>
    /// BOLTs PR #1324 <c>shared_input_partial_signature</c> (TLV 2): the sender's MuSig2 partial signature with nonce of
    /// the shared taproot input of a splice (instead of <see cref="SharedInputSignatureTlv"/>).
    /// </summary>
    public SharedInputPartialSignatureTlv? SharedInputPartialSignatureTlv { get; }

    /// <param name="payload">The tx_signatures payload.</param>
    /// <param name="sharedInputSignatureTlv">The <c>shared_input_signature</c> TLV, if any.</param>
    /// <param name="sharedInputPartialSignatureTlv">The <c>shared_input_partial_signature</c> TLV, if any.</param>
    public TxSignaturesMessage(TxSignaturesPayload payload, SharedInputSignatureTlv? sharedInputSignatureTlv = null,
                               SharedInputPartialSignatureTlv? sharedInputPartialSignatureTlv = null)
        : base(MessageTypes.TxSignatures, payload)
    {
        SharedInputSignatureTlv = sharedInputSignatureTlv;
        SharedInputPartialSignatureTlv = sharedInputPartialSignatureTlv;

        if (SharedInputSignatureTlv is not null || SharedInputPartialSignatureTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(SharedInputSignatureTlv, SharedInputPartialSignatureTlv);
        }
    }
}