using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters.Onion;

using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;

public class EncryptedRecipientDataTlvConverter : ITlvConverter<EncryptedRecipientDataTlv>
{
    public BaseTlv ConvertToBase(EncryptedRecipientDataTlv tlv)
    {
        return new BaseTlv(tlv.Type, tlv.EncryptedRecipientData.ToArray());
    }

    public EncryptedRecipientDataTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != OnionPayloadTlvTypes.EncryptedRecipientData)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != (ulong)baseTlv.Value.Length)
            throw new InvalidCastException("Invalid length");

        return new EncryptedRecipientDataTlv(baseTlv.Value);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as EncryptedRecipientDataTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(EncryptedRecipientDataTlv)}"));
    }
}