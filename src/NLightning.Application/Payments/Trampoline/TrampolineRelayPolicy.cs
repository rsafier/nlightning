namespace NLightning.Application.Payments.Trampoline;

using Domain.Money;
using Domain.Protocol.Onion.Models;

/// <summary>
/// Whether a complete incoming set of a trampoline relay pays for what it asks (BOLTs PR 836 TR-R-09, NL-875 TR3):
/// pure, over <see cref="TrampolineOptions"/>.
/// </summary>
/// <remarks>
/// <para>Fee: sum in − amount out ≥ <c>FeeBaseMsat</c> + amount out × <c>FeeProportionalMillionths</c> / 10⁶. Expiry:
/// lowest incoming <c>cltv_expiry</c> − <c>outgoing_cltv_value</c> ≥ <c>CltvExpiryDelta</c>, the
/// <c>outgoing_cltv_value</c> above the current height, and the lowest incoming <c>cltv_expiry</c> more than
/// <c>MinCltvMarginBlocks</c> above it. Every refusal is <c>trampoline_fee_or_expiry_insufficient</c> carrying our
/// policy, which tells the payer what to offer next (D-TR6).</para>
/// <para>An accepted set gives the outgoing leg its fee budget (what is left after our fee) and the highest expiry its
/// first HTLC may carry (the lowest incoming expiry minus our delta).</para>
/// </remarks>
public static class TrampolineRelayPolicy
{
    /// <summary>Checks a complete set.</summary>
    /// <param name="options">Our trampoline policy.</param>
    /// <param name="sumIn">The sum of the incoming parts' amounts.</param>
    /// <param name="minCltvIn">The lowest incoming part's <c>cltv_expiry</c>.</param>
    /// <param name="amountOut">The trampoline payload's <c>amt_to_forward</c>.</param>
    /// <param name="cltvOut">The trampoline payload's <c>outgoing_cltv_value</c>.</param>
    /// <param name="height">The current block height.</param>
    public static TrampolineRelayDecision Evaluate(TrampolineOptions options, LightningMoney sumIn, uint minCltvIn,
                                                   LightningMoney amountOut, uint cltvOut, uint height)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sumIn);
        ArgumentNullException.ThrowIfNull(amountOut);

        var fee = FeeOf(options, amountOut);
        if (sumIn.MilliSatoshi < amountOut.MilliSatoshi
         || sumIn.MilliSatoshi - amountOut.MilliSatoshi < fee.MilliSatoshi)
            return Refuse(options, $"fee too low: {sumIn.MilliSatoshi} msat in for {amountOut.MilliSatoshi} msat out, "
                                 + $"our fee is {fee.MilliSatoshi} msat");

        if (cltvOut <= height)
            return Refuse(options, $"outgoing_cltv_value {cltvOut} is not above the height {height}");

        if (minCltvIn < cltvOut || minCltvIn - cltvOut < options.CltvExpiryDelta)
            return Refuse(options, $"expiry delta too low: incoming {minCltvIn}, outgoing {cltvOut}, our delta is "
                                 + $"{options.CltvExpiryDelta}");

        if ((ulong)minCltvIn <= (ulong)height + options.MinCltvMarginBlocks)
            return Refuse(options, $"incoming expiry {minCltvIn} too close to the height {height} (margin "
                                 + $"{options.MinCltvMarginBlocks} blocks)");

        var maxFee = LightningMoney.MilliSatoshis(sumIn.MilliSatoshi - amountOut.MilliSatoshi - fee.MilliSatoshi);
        return new TrampolineRelayDecision(true, maxFee, minCltvIn - options.CltvExpiryDelta, fee, null, null);
    }

    /// <summary>Our trampoline fee for forwarding <paramref name="amountOut"/>.</summary>
    public static LightningMoney FeeOf(TrampolineOptions options, LightningMoney amountOut)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(amountOut);
        var proportional = (ulong)((UInt128)amountOut.MilliSatoshi * options.FeeProportionalMillionths / 1_000_000);
        return LightningMoney.MilliSatoshis(checked(options.FeeBaseMsat + proportional));
    }

    /// <summary><c>trampoline_fee_or_expiry_insufficient</c> with our policy.</summary>
    public static FailureMessage InsufficientFailure(TrampolineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return FailureMessage.TrampolineFeeOrExpiryInsufficient(options.FeeBaseMsat, options.FeeProportionalMillionths,
                                                                options.CltvExpiryDelta);
    }

    private static TrampolineRelayDecision Refuse(TrampolineOptions options, string reason) =>
        new(false, null, null, null, InsufficientFailure(options), reason);
}

/// <summary>What <see cref="TrampolineRelayPolicy.Evaluate"/> decided.</summary>
/// <param name="IsAccepted">The set pays for the relay.</param>
/// <param name="MaxFee">Accepted: the routing fee budget of the outgoing leg (sum in − amount out − our fee).</param>
/// <param name="MaxFirstHopCltvExpiry">Accepted: the highest expiry of the leg's first HTLC (lowest incoming expiry −
/// our delta).</param>
/// <param name="OurFee">Accepted: our trampoline fee.</param>
/// <param name="Failure">Refused: the failure for every part (NODE|26 with our policy).</param>
/// <param name="Reason">Refused: why, for the logs and the relay row.</param>
public sealed record TrampolineRelayDecision(bool IsAccepted, LightningMoney? MaxFee, uint? MaxFirstHopCltvExpiry,
                                             LightningMoney? OurFee, FailureMessage? Failure, string? Reason);