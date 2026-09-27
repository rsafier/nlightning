using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

/// <summary>
/// Converts <c>tx_add_input_tlvs</c> type 0 <c>shared_input_txid</c> (BOLT 2: [<c>sha256</c>:<c>funding_txid</c>]).
/// </summary>
public class SharedInputTxIdTlvConverter : ITlvConverter<SharedInputTxIdTlv>
{
    public BaseTlv ConvertToBase(SharedInputTxIdTlv tlv)
    {
        return tlv;
    }

    public SharedInputTxIdTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != InteractiveTxTlvConstants.SharedInputTxId)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Length != SharedInputTxIdTlv.ValueLength || baseTlv.Value.Length != SharedInputTxIdTlv.ValueLength)
            throw new InvalidCastException("Invalid length");

        return new SharedInputTxIdTlv(baseTlv.Value[..SharedInputTxIdTlv.ValueLength]);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as SharedInputTxIdTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(SharedInputTxIdTlv)}"));
    }
}