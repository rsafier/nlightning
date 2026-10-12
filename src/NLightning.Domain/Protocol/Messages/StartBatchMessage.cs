namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents a start_batch message (BOLT 2 "Batching channel messages", type 127).
/// </summary>
/// <remarks>
/// Announces that the next <see cref="StartBatchPayload.BatchSize"/> messages form one logical message: with pending
/// splices, one <c>commitment_signed</c> per active funding (SP-OP-03). It is channel-scoped but never reaches a channel
/// handler: the per-peer inbound loop groups the batch (splicing plan D15, SP1-A-T3) into a
/// <see cref="CommitmentSignedBatch"/>.
/// </remarks>
public sealed class StartBatchMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new StartBatchPayload Payload { get => (StartBatchPayload)base.Payload; }

    /// <summary>
    /// <c>start_batch_tlvs</c> type 1: the type of the batched messages (132 for splicing), or null when absent.
    /// </summary>
    public StartBatchMessageTypeTlv? MessageTypeTlv { get; }

    public StartBatchMessage(StartBatchPayload payload, StartBatchMessageTypeTlv? messageTypeTlv = null)
        : base(MessageTypes.StartBatch, payload)
    {
        MessageTypeTlv = messageTypeTlv;

        if (MessageTypeTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(MessageTypeTlv);
        }
    }
}