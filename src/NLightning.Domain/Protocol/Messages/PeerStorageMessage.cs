namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// <c>peer_storage</c> (type 7, BOLT 1): asks the receiver, which offered <c>option_provide_storage</c>, to keep the
/// blob and hand it back with <c>peer_storage_retrieval</c> after every reconnection.
/// </summary>
public sealed class PeerStorageMessage(PeerStoragePayload payload) : BaseMessage(MessageTypes.PeerStorage, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new PeerStoragePayload Payload => (PeerStoragePayload)base.Payload;
}