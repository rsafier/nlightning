using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;

public class TrampolineOnionPacketTlvConverter : ITlvConverter<TrampolineOnionPacketTlv>
{
    public BaseTlv ConvertToBase(TrampolineOnionPacketTlv tlv)
    {
        return new BaseTlv(tlv.Type, tlv.Packet.ToArray());
    }

    public TrampolineOnionPacketTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != OnionPayloadTlvTypes.TrampolineOnionPacket)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != (ulong)baseTlv.Value.Length || baseTlv.Value.Length < TrampolineOnionPacketTlv.MinLength)
            throw new InvalidCastException("Invalid length");

        return new TrampolineOnionPacketTlv(baseTlv.Value);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as TrampolineOnionPacketTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(TrampolineOnionPacketTlv)}"));
    }
}