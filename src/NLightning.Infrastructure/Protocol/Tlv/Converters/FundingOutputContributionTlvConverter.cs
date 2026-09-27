using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Tlv.Converters;

using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;

/// <summary>
/// Converts <c>funding_output_contribution</c> (<c>tx_init_rbf</c>/<c>tx_ack_rbf</c> type 0,
/// [<c>s64</c>:<c>satoshis</c>]); the value is signed so a splice-out RBF round-trips.
/// </summary>
public class FundingOutputContributionTlvConverter : ITlvConverter<FundingOutputContributionTlv>
{
    public BaseTlv ConvertToBase(FundingOutputContributionTlv tlv)
    {
        var value = new byte[FundingOutputContributionTlv.ValueLength];
        BinaryPrimitives.WriteInt64BigEndian(value, tlv.Satoshis);
        return new BaseTlv(tlv.Type, value);
    }

    public FundingOutputContributionTlv ConvertFromBase(BaseTlv baseTlv)
    {
        if (baseTlv.Type != TlvConstants.FundingOutputContribution)
        {
            throw new InvalidCastException("Invalid TLV type");
        }

        if (baseTlv.Length != FundingOutputContributionTlv.ValueLength || baseTlv.Value.Length != baseTlv.Length)
        {
            throw new InvalidCastException("Invalid length");
        }

        return new FundingOutputContributionTlv(BinaryPrimitives.ReadInt64BigEndian(baseTlv.Value));
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertFromBase(BaseTlv tlv)
    {
        return ConvertFromBase(tlv);
    }

    [ExcludeFromCodeCoverage]
    BaseTlv ITlvConverter.ConvertToBase(BaseTlv tlv)
    {
        return ConvertToBase(tlv as FundingOutputContributionTlv
                          ?? throw new InvalidCastException(
                                 $"Error casting BaseTlv to {nameof(FundingOutputContributionTlv)}"));
    }
}