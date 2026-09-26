namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Tlv;

/// <summary>
/// Represents an init message.
/// </summary>
/// <remarks>
/// The init message is used to communicate the features of the node.
/// The message type is 16.
/// </remarks>
public sealed class InitMessage : BaseMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new InitPayload Payload { get => (InitPayload)base.Payload; }

    public NetworksTlv? NetworksTlv { get; }

    public RemoteAddressTlv? RemoteAddressTlv { get; }

    /// <summary>
    /// The raw value of a received <c>remote_addr</c> that is not a valid address descriptor (NL-344). The TLV is odd
    /// and only advisory, so it never fails the init: <see cref="RemoteAddressTlv"/> is then null and the receiver
    /// logs this and drops it. Never serialized.
    /// </summary>
    public byte[]? UndecodableRemoteAddress { get; init; }

    public InitMessage(InitPayload payload, NetworksTlv? networksTlv = null, RemoteAddressTlv? remoteAddressTlv = null)
        : base(MessageTypes.Init, payload)
    {
        NetworksTlv = networksTlv;
        RemoteAddressTlv = remoteAddressTlv;

        if (networksTlv is not null || remoteAddressTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(networksTlv, remoteAddressTlv);
        }
    }
}