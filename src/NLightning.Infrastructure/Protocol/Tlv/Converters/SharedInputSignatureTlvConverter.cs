using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

/// <summary>
/// Converts <c>tx_signatures_tlvs</c> type 0 <c>shared_input_signature</c> (BOLT 2: [<c>signature</c>:<c>signature</c>],
/// a 64-byte compact signature).
/// </summary>
public class SharedInputSignatureTlvConverter : ITlvConverter<SharedInputSignatureTlv>
{
    public BaseTlv ConvertToBase(SharedInputSignatureTlv tlv)
    {
        return tlv;
    }

    public SharedInputSignatureTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != InteractiveTxTlvConstants.SharedInputSignature)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != SharedInputSignatureTlv.ValueLength
         || baseTlv.Value.Length != SharedInputSignatureTlv.ValueLength)
            throw new InvalidCastException("Invalid length");

        return new SharedInputSignatureTlv(baseTlv.Value[..SharedInputSignatureTlv.ValueLength]);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as SharedInputSignatureTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(SharedInputSignatureTlv)}"));
    }
}