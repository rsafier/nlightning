namespace NLightning.Domain.Protocol.Onion.Tlv;

using Channels.ValueObjects;
using Constants;
using Protocol.Tlv;

/// <summary>
/// Onion hop payload TLV 6 (<c>short_channel_id</c>), the outgoing channel.
/// </summary>
/// <remarks>
/// Not to be confused with <see cref="ShortChannelIdTlv"/>, which belongs to the channel_ready TLV namespace.
/// </remarks>
public class OnionShortChannelIdTlv : BaseTlv
{
    /// <summary>
    /// The outgoing channel's short channel id.
    /// </summary>
    public ShortChannelId ShortChannelId { get; }

    public OnionShortChannelIdTlv(ShortChannelId shortChannelId) : base(OnionPayloadTlvTypes.ShortChannelId)
    {
        ShortChannelId = shortChannelId;

        Value = shortChannelId;
        Length = Value.Length;
    }
}