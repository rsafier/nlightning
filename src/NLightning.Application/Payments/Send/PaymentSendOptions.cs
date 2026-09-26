namespace NLightning.Application.Payments.Send;

using Domain.Money;

/// <summary>
/// Limits for our outgoing payments (<see cref="PaymentService"/>).
/// </summary>
/// <remarks>
/// Bound from <c>Node:Payments</c>. The fee limit is mandatory (route hint fees are chosen by the payee); a call may set
/// its own (<c>PayInvoiceOptions.MaxFee</c>, NL-270), else it follows Core Lightning's <c>pay</c>
/// (<c>maxfeepercent</c> 0.5%, <c>exemptfee</c> 5000 msat): the larger of <see cref="MaxFeeProportionalMillionths"/>
/// of the amount and <see cref="MaxFeeFloorMsat"/>. The retry and split limits follow LND's defaults.
/// </remarks>
public sealed class PaymentSendOptions
{
    /// <summary>
    /// The largest routing fee, in millionths of the amount paid (default 5000 = 0.5%).
    /// </summary>
    public uint MaxFeeProportionalMillionths { get; set; } = 5_000;

    /// <summary>
    /// A routing fee up to this many msat is always accepted, whatever the amount (default 5000 msat).
    /// </summary>
    public ulong MaxFeeFloorMsat { get; set; } = 5_000;

    /// <summary>
    /// The most HTLCs a payment may have in flight at once when the call does not choose (default 16, LND's
    /// <c>max_parts</c>); 1 never splits.
    /// </summary>
    public int MaxParts { get; set; } = 16;

    /// <summary>
    /// The most HTLCs one payment call may offer in total, every part and retry together, refused offers included
    /// (default 32).
    /// </summary>
    public int MaxAttempts { get; set; } = 32;

    /// <summary>
    /// The smallest part a split plans, unless it is all that is left to send (default 10,000 msat).
    /// </summary>
    public ulong MinPartMsat { get; set; } = 10_000;

    /// <summary>
    /// Blocks added to the final CLTV after each <c>expiry_too_soon</c> or <c>final_incorrect_cltv_expiry</c>
    /// (default 6).
    /// </summary>
    public uint ExpiryTooSoonExtraBlocks { get; set; } = 6;

    /// <summary>
    /// The largest part limit a call may ask for.
    /// </summary>
    public const int MaxPartsLimit = 128;

    /// <summary>
    /// The fee limit for paying <paramref name="amount"/>.
    /// </summary>
    public LightningMoney GetMaxFee(LightningMoney amount)
    {
        ArgumentNullException.ThrowIfNull(amount);
        var proportional = (ulong)((UInt128)amount.MilliSatoshi * MaxFeeProportionalMillionths / 1_000_000);
        return LightningMoney.MilliSatoshis(Math.Max(proportional, MaxFeeFloorMsat));
    }
}