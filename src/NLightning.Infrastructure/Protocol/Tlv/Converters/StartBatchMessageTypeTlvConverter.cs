using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

/// <summary>
/// Converts <c>start_batch_tlvs</c> type 1 (<c>message_type</c>, [<c>u16</c>:<c>message_type</c>], BOLT 2 "Batching
/// channel messages").
/// </summary>
public class StartBatchMessageTypeTlvConverter : ITlvConverter<StartBatchMessageTypeTlv>
{
    public BaseTlv ConvertToBase(StartBatchMessageTypeTlv tlv)
    {
        return tlv;
    }

    public StartBatchMessageTypeTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TlvConstants.StartBatchMessageType)
        {
            throw new InvalidCastException("Invalid TLV type");
        }

        if (baseTlv.Length != StartBatchMessageTypeTlv.ValueLength || baseTlv.Value.Length != baseTlv.Length)
        {
            throw new InvalidCastException("Invalid length");
        }

        return new StartBatchMessageTypeTlv(BinaryPrimitives.ReadUInt16BigEndian(baseTlv.Value));
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as StartBatchMessageTypeTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(StartBatchMessageTypeTlv)}"));
    }
}