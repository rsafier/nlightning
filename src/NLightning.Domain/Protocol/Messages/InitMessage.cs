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

    /// <summary>Liquidity ads (TLV 1339, NL-771).</summary>
    public WillFundRatesTlv? WillFundRatesTlv { get; }

    /// <summary>
    /// The raw value of a received <c>remote_addr</c> that is not a valid address descriptor (NL-344). The TLV is odd
    /// and only advisory, so it never fails the init: <see cref="RemoteAddressTlv"/> is then null and the receiver
    /// logs this and drops it. Never serialized.
    /// </summary>
    public byte[]? UndecodableRemoteAddress { get; init; }

    /// <summary>
    /// The raw value of a received liquidity ads <c>option_will_fund</c> (TLV 1339, NL-771) that does not decode. The
    /// record is odd and only advisory, so it never fails the init: <see cref="WillFundRatesTlv"/> is then null and the
    /// receiver logs this and drops it. Never serialized.
    /// </summary>
    public byte[]? UndecodableWillFundRates { get; init; }

    public InitMessage(InitPayload payload, NetworksTlv? networksTlv = null, RemoteAddressTlv? remoteAddressTlv = null, WillFundRatesTlv? willFundRatesTlv = null)
        : base(MessageTypes.Init, payload)
    {
        WillFundRatesTlv = willFundRatesTlv;
        NetworksTlv = networksTlv;
        RemoteAddressTlv = remoteAddressTlv;

        if (networksTlv is not null || remoteAddressTlv is not null || willFundRatesTlv is not null)
        {
            Extension = new TlvStream();
            Extension.Add(networksTlv, remoteAddressTlv, willFundRatesTlv);
        }
    }
}