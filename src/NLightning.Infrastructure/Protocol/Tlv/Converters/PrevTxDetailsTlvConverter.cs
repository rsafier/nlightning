using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Crypto.Constants;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

/// <summary>
/// Converts <c>tx_add_input_tlvs</c> <c>prevtx_details</c> (BOLTs PR #1324 type 2, Eclair 0.14.3's type 1111):
/// [<c>sha256</c>:<c>prevtx_txid</c>] [<c>u64</c>:<c>amount_satoshis</c>] [<c>...*byte</c>:<c>scriptpubkey</c>] (NL-957).
/// </summary>
public class PrevTxDetailsTlvConverter : ITlvConverter<PrevTxDetailsTlv>
{
    public BaseTlv ConvertToBase(PrevTxDetailsTlv tlv)
    {
        return tlv;
    }

    public PrevTxDetailsTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != InteractiveTxTlvConstants.PrevTxDetails
         && baseTlv.Type != InteractiveTxTlvConstants.PrevTxDetailsEclair)
            throw new InvalidCastException("Invalid TLV type");

        if (baseTlv.Value.Length < PrevTxDetailsTlv.FixedLength || baseTlv.Length != (ulong)baseTlv.Value.Length)
            throw new InvalidCastException("Invalid length");

        var value = baseTlv.Value;
        var txId = value[..CryptoConstants.Sha256HashLen];
        var amount = BinaryPrimitives.ReadUInt64BigEndian(value.AsSpan(CryptoConstants.Sha256HashLen, sizeof(ulong)));
        var script = value[PrevTxDetailsTlv.FixedLength..];

        return new PrevTxDetailsTlv(txId, amount, script, baseTlv.Type);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as PrevTxDetailsTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(PrevTxDetailsTlv)}"));
    }
}