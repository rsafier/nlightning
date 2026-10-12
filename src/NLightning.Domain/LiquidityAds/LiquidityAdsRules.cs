using System.Security.Cryptography;
using System.Text;

namespace NLightning.Domain.LiquidityAds;

using Constants;
using Crypto.ValueObjects;
using Enums;
using Models;

/// <summary>
/// The rules of a liquidity purchase (BOLT PR #1153, as Eclair 0.14.3 applies them in <c>LiquidityAds.scala</c>).
/// Pure: the signature itself is made and checked by the caller's node-key signer
/// (<c>ILightningSigner.SignNodeMessage</c>/<c>VerifyNodeMessage</c>, ECDSA over <see cref="SignedData"/>).
/// </summary>
public static class LiquidityAdsRules
{
    /// <summary>
    /// The fee of a purchase: the mining fee refund <c>feerate_per_kw x funding_weight / 1000</c> (rounded down) and the
    /// service fee <c>fee_base</c> (+ <c>channel_creation_fee</c> for a new channel) + <c>min(requested, contributed)
    /// x fee_basis / 10,000</c> (computed in millisatoshis, rounded down to the satoshi). Extra liquidity the seller adds
    /// beyond the request is free.
    /// </summary>
    public static LiquidityFees ComputeFees(FundingRate rate, uint fundingFeeratePerKw, ulong requestedSat,
                                            ulong contributedSat, bool isChannelCreation)
    {
        var mining = (ulong)fundingFeeratePerKw * rate.FundingWeight / 1_000;
        var proportionalMsat = (UInt128)Math.Min(requestedSat, contributedSat) * 1_000 * rate.FeeBasis / 10_000;
        var flat = (ulong)rate.FeeBaseSat + (isChannelCreation ? rate.ChannelCreationFeeSat : 0UL);
        return new LiquidityFees(mining, checked(flat + (ulong)(proportionalMsat / 1_000)));
    }

    /// <summary>
    /// What the seller signs: <c>SHA256("liquidity_ads_purchase" || funding_rate || funding_script)</c>, where the
    /// funding script is the new funding output's script.
    /// </summary>
    public static Hash SignedData(FundingRate rate, ReadOnlySpan<byte> fundingScript)
    {
        var tag = Encoding.ASCII.GetBytes(LiquidityAdsConstants.SignatureTag);
        var rateBytes = LiquidityAdsCodec.EncodeFundingRate(rate);
        var data = new byte[tag.Length + rateBytes.Length + fundingScript.Length];
        tag.CopyTo(data, 0);
        rateBytes.CopyTo(data, tag.Length);
        fundingScript.CopyTo(data.AsSpan(tag.Length + rateBytes.Length));
        return new Hash(SHA256.HashData(data));
    }

    /// <summary>
    /// The seller's check of a request (Eclair <c>WillFundRates.validateRequest</c>): a payment type it accepts (and
    /// this node implements: only <see cref="LiquidityPaymentType.FromChannelBalance"/>), one of its rates, and an
    /// amount inside that rate's range.
    /// </summary>
    public static LiquidityAdsRefusal ValidateRequest(WillFundRates ourRates, RequestFunding request)
    {
        ArgumentNullException.ThrowIfNull(ourRates);
        ArgumentNullException.ThrowIfNull(request);

        if (request.PaymentDetails.Type != (ulong)LiquidityPaymentType.FromChannelBalance
         || !ourRates.SupportsBit(request.PaymentDetails.Type))
            return LiquidityAdsRefusal.UnsupportedPaymentType;
        if (!ourRates.Rates.Contains(request.Rate))
            return LiquidityAdsRefusal.UnknownRate;
        return request.Rate.IsCompatible(request.RequestedSat)
                   ? LiquidityAdsRefusal.None
                   : LiquidityAdsRefusal.AmountOutOfRange;
    }

    /// <summary>
    /// The buyer's check of the seller's answer (Eclair <c>RequestFunding.validateRemoteFunding</c>), in Eclair's order:
    /// present, signed by the seller over the funding script, a contribution of at least the request, the requested
    /// rate; then, when <paramref name="maxFeeSat"/> is given, a fee within it.
    /// </summary>
    /// <param name="request">What we asked for.</param>
    /// <param name="willFund">The seller's answer, null when it sent none.</param>
    /// <param name="fundingScript">The new funding output's script, as we compute it.</param>
    /// <param name="remoteContributionSat">What the seller contributes.</param>
    /// <param name="fundingFeeratePerKw">The interactive transaction's feerate.</param>
    /// <param name="isChannelCreation">Whether the purchase opens the channel.</param>
    /// <param name="verifySignature">Checks a signature of a hash against the seller's node id.</param>
    /// <param name="maxFeeSat">Our fee limit, if any.</param>
    /// <param name="fees">The fees, when accepted.</param>
    public static LiquidityAdsRefusal ValidateWillFund(RequestFunding request, WillFund? willFund,
                                                       ReadOnlySpan<byte> fundingScript, ulong remoteContributionSat,
                                                       uint fundingFeeratePerKw, bool isChannelCreation,
                                                       Func<Hash, CompactSignature, bool> verifySignature,
                                                       ulong? maxFeeSat, out LiquidityFees fees)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(verifySignature);
        fees = default;

        if (willFund is null)
            return LiquidityAdsRefusal.Missing;
        if (!verifySignature(SignedData(request.Rate, fundingScript), willFund.Signature))
            return LiquidityAdsRefusal.BadSignature;
        if (remoteContributionSat < request.RequestedSat)
            return LiquidityAdsRefusal.AmountTooLow;
        if (willFund.Rate != request.Rate)
            return LiquidityAdsRefusal.RateMismatch;

        fees = ComputeFees(request.Rate, fundingFeeratePerKw, request.RequestedSat, remoteContributionSat,
                           isChannelCreation);
        return maxFeeSat is { } max && fees.TotalSat > max ? LiquidityAdsRefusal.FeeTooHigh : LiquidityAdsRefusal.None;
    }

    /// <summary>
    /// A request for <paramref name="amountSat"/> paid from the channel balance at the seller's first compatible rate
    /// (Eclair <c>LiquidityAds.requestFunding</c>); null when no rate sells the amount or the seller does not accept
    /// payment from the channel balance.
    /// </summary>
    public static RequestFunding? CreateRequest(WillFundRates sellerRates, ulong amountSat)
    {
        ArgumentNullException.ThrowIfNull(sellerRates);
        return sellerRates.FindRate(amountSat) is { } rate && sellerRates.Supports(LiquidityPaymentType.FromChannelBalance)
                   ? new RequestFunding(amountSat, rate, LiquidityPaymentDetails.FromChannelBalance)
                   : null;
    }
}