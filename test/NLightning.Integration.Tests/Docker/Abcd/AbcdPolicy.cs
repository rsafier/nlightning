namespace NLightning.Integration.Tests.Docker.Abcd;

using Domain.Node.Options;

/// <summary>
/// One forwarding policy of the ABCD network (ABCD roadmap §3): what the node is configured with and what the route
/// hints through it carry. Bob's and Carol's values differ so a fee mix-up shows.
/// </summary>
public sealed record AbcdPolicy(uint FeeBaseMsat, uint FeeProportionalMillionths, ushort CltvExpiryDelta)
{
    public static readonly AbcdPolicy Bob = new(1_000, 100, 40);
    public static readonly AbcdPolicy Carol = new(2_000, 500, 40);

    /// <summary>
    /// BOLT 7 "HTLC Fees": <c>fee_base_msat + amount_to_forward * fee_proportional_millionths / 1000000</c>, rounded
    /// down, computed here independently of the node's own <c>ForwardingFee</c>.
    /// </summary>
    public long FeeFor(long amountToForwardMsat) =>
        FeeBaseMsat + amountToForwardMsat * FeeProportionalMillionths / 1_000_000;

    public void ApplyTo(RoutingOptions routing)
    {
        routing.FeeBaseMsat = FeeBaseMsat;
        routing.FeeProportionalMillionths = FeeProportionalMillionths;
        routing.CltvExpiryDelta = CltvExpiryDelta;
    }
}

/// <summary>
/// The fees of a payment of <see cref="AmountMsat"/> to David, Alice → Bob → Carol → David:
/// <c>fee_C = 2000 + floor(X*500/1e6)</c>, <c>amt_BC = X + fee_C</c>, <c>fee_B = 1000 + floor(amt_BC*100/1e6)</c>.
/// </summary>
public sealed record AbcdPathFees(long AmountMsat, long FeeCarolMsat, long AmountBobToCarolMsat, long FeeBobMsat)
{
    /// <summary>
    /// What Alice sends Bob over A–B.
    /// </summary>
    public long AmountAliceToBobMsat => AmountBobToCarolMsat + FeeBobMsat;

    public long TotalFeeMsat => FeeBobMsat + FeeCarolMsat;

    public static AbcdPathFees ToDavid(long amountMsat)
    {
        var feeCarol = AbcdPolicy.Carol.FeeFor(amountMsat);
        var amountBobToCarol = amountMsat + feeCarol;
        return new AbcdPathFees(amountMsat, feeCarol, amountBobToCarol, AbcdPolicy.Bob.FeeFor(amountBobToCarol));
    }
}