using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Codecs;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;

public class RecipientBlindedPathsTlvConverter : ITlvConverter<RecipientBlindedPathsTlv>
{
    public BaseTlv ConvertToBase(RecipientBlindedPathsTlv tlv)
    {
        return new BaseTlv(tlv.Type, PaymentBlindedPathCodec.EncodeList(tlv.Paths));
    }

    public RecipientBlindedPathsTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != OnionPayloadTlvTypes.RecipientBlindedPaths)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != (ulong)baseTlv.Value.Length)
            throw new InvalidCastException("Invalid length");

        if (!PaymentBlindedPathCodec.TryReadList(baseTlv.Value, out var paths, out var reason))
            throw new InvalidCastException($"Invalid recipient_blinded_paths: {reason}");

        return new RecipientBlindedPathsTlv(paths);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as RecipientBlindedPathsTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(RecipientBlindedPathsTlv)}"));
    }
}