namespace NLightning.Application.Payments.Send;

using Domain.Money;

/// <summary>
/// Limits for our outgoing payments (<see cref="PaymentService"/>).
/// </summary>
/// <remarks>
/// <c>IPaymentService.PayInvoiceAsync</c> takes no fee limit, but <c>HintRouteBuilder</c> needs one: route hint fees
/// are chosen by the payee. The default follows Core Lightning's <c>pay</c> (<c>maxfeepercent</c> 0.5%,
/// <c>exemptfee</c> 5000 msat): the limit is the larger of <see cref="MaxFeeProportionalMillionths"/> of the amount and
/// <see cref="MaxFeeFloorMsat"/>.
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
    /// The fee limit for paying <paramref name="amount"/>.
    /// </summary>
    public LightningMoney GetMaxFee(LightningMoney amount)
    {
        ArgumentNullException.ThrowIfNull(amount);
        var proportional = (ulong)((UInt128)amount.MilliSatoshi * MaxFeeProportionalMillionths / 1_000_000);
        return LightningMoney.MilliSatoshis(Math.Max(proportional, MaxFeeFloorMsat));
    }
}