namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// <c>peer_storage_retrieval</c> (type 9, BOLT 1): the last blob the sender stored for the receiver, sent after the
/// <c>init</c> exchange of every connection (and before <c>channel_reestablish</c>).
/// </summary>
public sealed class PeerStorageRetrievalMessage(PeerStorageRetrievalPayload payload)
    : BaseMessage(MessageTypes.PeerStorageRetrieval, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new PeerStorageRetrievalPayload Payload => (PeerStorageRetrievalPayload)base.Payload;
}