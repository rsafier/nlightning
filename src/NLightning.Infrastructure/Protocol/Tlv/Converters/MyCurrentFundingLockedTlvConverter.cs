using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

/// <summary>
/// Converts <c>channel_reestablish_tlvs</c> type 5 (<c>my_current_funding_locked</c>,
/// [<c>sha256</c>:<c>my_current_funding_locked_txid</c>] [<c>byte</c>:<c>retransmit_flags</c>], SP-RE-02).
/// </summary>
public class MyCurrentFundingLockedTlvConverter : ITlvConverter<MyCurrentFundingLockedTlv>
{
    public BaseTlv ConvertToBase(MyCurrentFundingLockedTlv tlv)
    {
        return tlv;
    }

    public MyCurrentFundingLockedTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TlvConstants.MyCurrentFundingLocked)
        {
            throw new InvalidCastException("Invalid TLV type");
        }

        if (baseTlv.Length != MyCurrentFundingLockedTlv.ValueLength || baseTlv.Value.Length != baseTlv.Length)
        {
            throw new InvalidCastException("Invalid length");
        }

        var txId = new TxId(baseTlv.Value[..CryptoConstants.Sha256HashLen]);
        return new MyCurrentFundingLockedTlv(txId, baseTlv.Value[CryptoConstants.Sha256HashLen]);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as MyCurrentFundingLockedTlv
                          ?? throw new InvalidCastException(
                                 $"Error converting BaseTlv to {nameof(MyCurrentFundingLockedTlv)}"));
    }
}