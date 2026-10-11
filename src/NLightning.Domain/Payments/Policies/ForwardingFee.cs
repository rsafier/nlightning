namespace NLightning.Domain.Payments.Policies;

using Money;

/// <summary>
/// The BOLT 7 "HTLC Fees" formula: <c>fee_base_msat + ( amount_to_forward * fee_proportional_millionths / 1000000 )</c>,
/// in integer msat (the proportional part is rounded down).
/// </summary>
/// <remarks>
/// BOLT 7's worked example: 200 base, 2000 ppm, 4,999,999 msat to forward gives
/// <c>200 + 4999999 * 2000 / 1000000 = 10199</c>. The product is computed in 128 bits, so no amount overflows;
/// a result above <see cref="ulong.MaxValue"/> throws <see cref="OverflowException"/>.
/// </remarks>
public static class ForwardingFee
{
    private const ulong Million = 1_000_000;

    /// <summary>
    /// The fee in msat a node with this policy charges to forward <paramref name="amountToForwardMsat"/>.
    /// </summary>
    public static ulong CalculateMsat(ulong feeBaseMsat, uint feeProportionalMillionths, ulong amountToForwardMsat)
    {
        var proportional = (UInt128)amountToForwardMsat * feeProportionalMillionths / Million;
        return checked((ulong)(feeBaseMsat + proportional));
    }

    /// <inheritdoc cref="CalculateMsat"/>
    public static LightningMoney Calculate(ulong feeBaseMsat, uint feeProportionalMillionths,
                                           LightningMoney amountToForward)
    {
        ArgumentNullException.ThrowIfNull(amountToForward);
        return LightningMoney.MilliSatoshis(CalculateMsat(feeBaseMsat, feeProportionalMillionths,
                                                          amountToForward.MilliSatoshi));
    }

    /// <summary>
    /// The smallest incoming amount this policy accepts for an outgoing <paramref name="amountToForwardMsat"/>:
    /// <c>amount_to_forward + fee</c>. A forwarding node fails an HTLC below it with <c>fee_insufficient</c>.
    /// </summary>
    public static ulong RequiredIncomingMsat(ulong feeBaseMsat, uint feeProportionalMillionths,
                                             ulong amountToForwardMsat) =>
        checked(amountToForwardMsat + CalculateMsat(feeBaseMsat, feeProportionalMillionths, amountToForwardMsat));

    /// <summary>
    /// True when <paramref name="incomingAmountMsat"/> pays at least the fee for forwarding
    /// <paramref name="amountToForwardMsat"/> (BOLT 4: "if the HTLC does NOT pay a sufficient fee"). An incoming amount
    /// below the amount to forward never pays.
    /// </summary>
    public static bool PaysSufficientFee(ulong feeBaseMsat, uint feeProportionalMillionths, ulong incomingAmountMsat,
                                         ulong amountToForwardMsat)
    {
        if (incomingAmountMsat < amountToForwardMsat)
            return false;

        return (UInt128)incomingAmountMsat - amountToForwardMsat
            >= (UInt128)feeBaseMsat + (UInt128)amountToForwardMsat * feeProportionalMillionths / Million;
    }
}