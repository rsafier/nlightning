namespace NLightning.Domain.LiquidityAds.Models;

using Crypto.ValueObjects;

/// <summary>
/// A seller's answer to a liquidity request (BOLT PR #1153 <c>will_fund</c>, in <c>accept_channel2</c>,
/// <c>tx_ack_rbf</c> and <c>splice_ack</c>): the rate, the new funding output's script and the seller's signature of
/// both (<see cref="LiquidityAdsRules.SignedData"/>).
/// </summary>
public sealed record WillFund
{
    public WillFund(FundingRate rate, byte[] fundingScript, CompactSignature signature)
    {
        ArgumentNullException.ThrowIfNull(fundingScript);
        if (fundingScript.Length > ushort.MaxValue)
            throw new ArgumentException("The funding script is longer than 65535 bytes", nameof(fundingScript));

        Rate = rate;
        FundingScript = fundingScript.ToArray();
        Signature = signature;
    }

    /// <summary>The rate the seller sells at (the requested one).</summary>
    public FundingRate Rate { get; }

    /// <summary>The script of the funding output the purchase goes into.</summary>
    public byte[] FundingScript { get; }

    /// <summary>The seller's node-key signature of the rate and the funding script.</summary>
    public CompactSignature Signature { get; }

    /// <inheritdoc />
    public bool Equals(WillFund? other) =>
        other is not null && Rate == other.Rate && FundingScript.AsSpan().SequenceEqual(other.FundingScript)
     && ((ReadOnlySpan<byte>)Signature).SequenceEqual(other.Signature);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Rate, FundingScript.Length);
}