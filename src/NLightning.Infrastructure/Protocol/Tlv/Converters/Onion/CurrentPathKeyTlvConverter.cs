using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;

public class CurrentPathKeyTlvConverter : ITlvConverter<CurrentPathKeyTlv>
{
    public BaseTlv ConvertToBase(CurrentPathKeyTlv tlv)
    {
        byte[] value = tlv.PathKey;

        return new BaseTlv(tlv.Type, value.ToArray());
    }

    public CurrentPathKeyTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != OnionPayloadTlvTypes.CurrentPathKey)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != CryptoConstants.CompactPubkeyLen
         || baseTlv.Value.Length != CryptoConstants.CompactPubkeyLen)
            throw new InvalidCastException("Invalid length");

        try
        {
            return new CurrentPathKeyTlv(new CompactPubKey(baseTlv.Value.ToArray()));
        }
        catch (ArgumentException e)
        {
            throw new InvalidCastException("Invalid path key", e);
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
        return ConvertToBase(tlv as CurrentPathKeyTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(CurrentPathKeyTlv)}"));
    }
}