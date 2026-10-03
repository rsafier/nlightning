using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

public class BlindedPathTlvConverter : ITlvConverter<BlindedPathTlv>
{
    public BaseTlv ConvertToBase(BlindedPathTlv tlv)
    {
        tlv.Value = tlv.PathKey;

        return tlv;
    }

    public BlindedPathTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TlvConstants.BlindedPath)
        {
            throw new InvalidCastException("Invalid TLV type");
        }

        // BOLT 2: blinded_path carries a single `point` (33-byte compressed public key).
        if (baseTlv.Length != CryptoConstants.CompactPubkeyLen
         || baseTlv.Value.Length != CryptoConstants.CompactPubkeyLen)
        {
            throw new InvalidCastException("Invalid length");
        }

        try
        {
            return new BlindedPathTlv(new CompactPubKey(baseTlv.Value.ToArray()));
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
        return ConvertToBase(tlv as BlindedPathTlv
                          ?? throw new InvalidCastException($"Error converting BaseTlv to {nameof(BlindedPathTlv)}"));
    }
}