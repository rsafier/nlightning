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
    /// Route through the gossip graph (BOLT 7 plan G4-T3) when the payee is neither our peer nor reachable over the
    /// invoice's route hints from one of our peers, and to find more paths for a split (default true). Without a graph
    /// (gossip disabled) this changes nothing.
    /// </summary>
    public bool UseGraph { get; set; } = true;

    /// <summary>
    /// The diverse graph paths the planner asks the pathfinder for per amount it tries (plan §3.10 "k paths", default 3).
    /// </summary>
    public int GraphPathsPerRound { get; set; } = 3;

    /// <summary>
    /// The largest random CLTV offset added to the payee's CLTV of a graph route (BOLT 7 shadow route, B7-RT-01;
    /// default 144 blocks, 0 turns it off). It is cut down so the route stays within
    /// <c>Routing.MaxCltvExpiryDistance</c>.
    /// </summary>
    public uint ShadowCltvMaxOffset { get; set; } = 144;

    /// <summary>
    /// How fast what mission control learnt about a channel's liquidity fades (plan D8: 1 hour half-life, never
    /// persisted).
    /// </summary>
    public TimeSpan MissionControlHalfLife { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a node that sent a NODE failure is not routed through by later payments (default 1 hour; the payment
    /// that got the failure avoids it until it ends).
    /// </summary>
    public TimeSpan NodeFailurePenalty { get; set; } = TimeSpan.FromHours(1);

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