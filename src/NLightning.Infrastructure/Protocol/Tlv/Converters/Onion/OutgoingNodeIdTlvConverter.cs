using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;

public class OutgoingNodeIdTlvConverter : ITlvConverter<OutgoingNodeIdTlv>
{
    public BaseTlv ConvertToBase(OutgoingNodeIdTlv tlv)
    {
        byte[] value = tlv.OutgoingNodeId;

        return new BaseTlv(tlv.Type, value.ToArray());
    }

    public OutgoingNodeIdTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != OnionPayloadTlvTypes.OutgoingNodeId)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != CryptoConstants.CompactPubkeyLen
         || baseTlv.Value.Length != CryptoConstants.CompactPubkeyLen)
            throw new InvalidCastException("Invalid length");

        try
        {
            return new OutgoingNodeIdTlv(new CompactPubKey(baseTlv.Value.ToArray()));
        }
        catch (ArgumentException e)
        {
            throw new InvalidCastException("Invalid outgoing node id", e);
        }
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as OutgoingNodeIdTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(OutgoingNodeIdTlv)}"));
    }
}