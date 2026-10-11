using System.Buffers.Binary;

namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Money;

/// <summary>
/// Funding Output Contribution TLV.
/// </summary>
/// <remarks>
/// BOLT 2 <c>tx_init_rbf_tlvs</c> and <c>tx_ack_rbf_tlvs</c> type 0 (<c>funding_output_contribution</c>):
/// [<c>s64</c>:<c>satoshis</c>], big-endian two's complement. The value is signed: an RBF of a splice-out carries the
/// sender's negative contribution (BOLT 2 "Channel Splicing", SP-S-02), so it is held as a <see cref="long"/> and never
/// as a <see cref="LightningMoney"/> (which cannot be negative). <see cref="BaseTlv.Value"/> holds the wire bytes.
/// </remarks>
public class FundingOutputContributionTlv : BaseTlv
{
    /// <summary>The size of the TLV value: an s64.</summary>
    public const int ValueLength = sizeof(long);

    /// <summary>
    /// The sender's signed contribution to the funding output, in satoshis (negative for a splice-out).
    /// </summary>
    public long Satoshis { get; }

    /// <summary>
    /// Creates the TLV from a signed contribution in satoshis.
    /// </summary>
    /// <param name="satoshis">The contribution in satoshis; negative for a splice-out.</param>
    public FundingOutputContributionTlv(long satoshis) : base(TlvConstants.FundingOutputContribution)
    {
        Satoshis = satoshis;

        var value = new byte[ValueLength];
        BinaryPrimitives.WriteInt64BigEndian(value, satoshis);
        Value = value;
        Length = ValueLength;
    }

    /// <summary>
    /// Creates the TLV from a non-negative contribution (a dual-funded open or a splice-in), rounded down to whole
    /// satoshis.
    /// </summary>
    /// <param name="amount">The contribution.</param>
    public FundingOutputContributionTlv(LightningMoney amount) : this(amount.Satoshi)
    {
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Type, Length, Satoshis);
    }

    public override bool Equals(object? obj)
    {
        return obj is FundingOutputContributionTlv fundingOutputContributionTlv && Equals(fundingOutputContributionTlv);
    }

    private bool Equals(FundingOutputContributionTlv other)
    {
        return Type.Equals(other.Type) && Length.Equals(other.Length) && Satoshis.Equals(other.Satoshis);
    }
}