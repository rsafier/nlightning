using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;

public class OnionShortChannelIdTlvConverter : ITlvConverter<OnionShortChannelIdTlv>
{
    public BaseTlv ConvertToBase(OnionShortChannelIdTlv tlv)
    {
        byte[] value = tlv.ShortChannelId;

        return new BaseTlv(tlv.Type, value.ToArray());
    }

    public OnionShortChannelIdTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != OnionPayloadTlvTypes.ShortChannelId)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != ShortChannelId.Length || baseTlv.Value.Length != ShortChannelId.Length)
            throw new InvalidCastException("Invalid length");

        return new OnionShortChannelIdTlv(new ShortChannelId(baseTlv.Value.ToArray()));
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as OnionShortChannelIdTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(OnionShortChannelIdTlv)}"));
    }
}