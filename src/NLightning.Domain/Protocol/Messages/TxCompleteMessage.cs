namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a tx_complete message.
/// </summary>
/// <remarks>
/// The tx_complete message signals the conclusion of a peer's transaction contributions.
/// The message type is 70.
/// </remarks>
public sealed class TxCompleteMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new TxCompletePayload Payload { get => (TxCompletePayload)base.Payload; }

    /// <summary>
    /// BOLTs PR #1324 <c>commit_nonces</c> (TLV 4): the sender's verification nonces for the commitment of the
    /// negotiated transaction and the next one. Sent in every tx_complete of a simple taproot session.
    /// </summary>
    public CommitNoncesTlv? CommitNoncesTlv { get; }

    /// <summary>
    /// BOLTs PR #1324 <c>funding_nonce</c> (TLV 6): the sender's signing nonce for the shared taproot input of a splice.
    /// </summary>
    public FundingNonceTlv? FundingNonceTlv { get; }

    /// <param name="payload">The tx_complete payload.</param>
    /// <param name="commitNoncesTlv">The <c>commit_nonces</c> TLV, if any.</param>
    /// <param name="fundingNonceTlv">The <c>funding_nonce</c> TLV, if any.</param>
    public TxCompleteMessage(TxCompletePayload payload, CommitNoncesTlv? commitNoncesTlv = null,
                             FundingNonceTlv? fundingNonceTlv = null)
        : base(MessageTypes.TxComplete, payload)
    {
        CommitNoncesTlv = commitNoncesTlv;
        FundingNonceTlv = fundingNonceTlv;

        if (CommitNoncesTlv is not null || FundingNonceTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(CommitNoncesTlv, FundingNonceTlv);
        }
    }
}