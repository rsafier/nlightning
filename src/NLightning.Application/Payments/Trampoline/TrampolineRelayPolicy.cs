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
/// <para>Our fee and delta are the price of the relay, routing included (D-TR6: they "cover typical routes", as in
/// Eclair, which wrote the proposal): an accepted set gives the outgoing leg the whole difference as its fee budget
/// (sum in − amount out; we keep what the route leaves) and lets its first HTLC expire as late as the lowest incoming
/// expiry minus our plain forwarding delta (<c>Node:Routing:CltvExpiryDelta</c>, what a forward keeps to resolve the
/// incoming HTLC after the outgoing one; at most our trampoline delta). A payer that pays exactly the policy a NODE|26
/// returned is then routed beyond our direct peers (NL-875 TR5: with our fee and delta kept out of the leg's budget,
/// such a payment found no route past one hop).</para>
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
    /// <param name="forwardingCltvExpiryDelta">Our plain forwarding delta (<c>Node:Routing:CltvExpiryDelta</c>): the
    /// blocks kept between the leg's first HTLC and the lowest incoming expiry (at most our trampoline delta).</param>
    public static TrampolineRelayDecision Evaluate(TrampolineOptions options, LightningMoney sumIn, uint minCltvIn,
                                                   LightningMoney amountOut, uint cltvOut, uint height,
                                                   ushort forwardingCltvExpiryDelta)
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

        var maxFee = LightningMoney.MilliSatoshis(sumIn.MilliSatoshi - amountOut.MilliSatoshi);
        var keptDelta = Math.Min(forwardingCltvExpiryDelta, options.CltvExpiryDelta);
        return new TrampolineRelayDecision(true, maxFee, minCltvIn - keptDelta, fee, null, null);
    }

    /// <summary>
    /// Checks a complete set of a blinded trampoline hop (BOLTs PR 836 TR-R-10, NL-895 D-NL895-2): the recipient fixed
    /// the hop's price in the path's <c>payment_relay</c>, which <paramref name="amountOut"/> and
    /// <paramref name="cltvOut"/> already come from (the outer total and expiry, see
    /// <c>IncomingOnionTrampolineRelay</c>; <c>payment_constraints</c> checked there too), so our
    /// <c>Node:Trampoline</c> fee and delta do not apply and a refusal is never NODE|26.
    /// </summary>
    /// <remarks>
    /// <para>What is left is our own safety, as for a blinded forward (<c>HtlcForwardingPolicy</c>): the set covers the
    /// amount out, the outgoing expiry is above the height, the lowest incoming expiry keeps our plain forwarding delta
    /// (<c>Node:Routing:CltvExpiryDelta</c>) above it (the leg's first HTLC may expire no later than that) and our
    /// <c>MinCltvMarginBlocks</c> above the height. The leg's budget is what <c>payment_relay</c> granted: the fee is
    /// sum in − amount out, the first HTLC's expiry at most the lowest incoming expiry − our forwarding delta.</para>
    /// <para>A refusal carries <c>invalid_onion_blinding</c> (with an empty <c>sha256_of_onion</c>: the relay engine
    /// answers each part as the blinded rules say, our own error at the introduction node, malformed past it).</para>
    /// </remarks>
    public static TrampolineRelayDecision EvaluateBlinded(TrampolineOptions options, LightningMoney sumIn,
                                                          uint minCltvIn, LightningMoney amountOut, uint cltvOut,
                                                          uint height, ushort forwardingCltvExpiryDelta)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sumIn);
        ArgumentNullException.ThrowIfNull(amountOut);

        if (sumIn.MilliSatoshi < amountOut.MilliSatoshi)
            return RefuseBlinded($"{sumIn.MilliSatoshi} msat in for {amountOut.MilliSatoshi} msat out");

        if (cltvOut <= height)
            return RefuseBlinded($"outgoing_cltv_value {cltvOut} is not above the height {height}");

        if (minCltvIn < cltvOut || minCltvIn - cltvOut < forwardingCltvExpiryDelta)
            return RefuseBlinded($"payment_relay leaves {(long)minCltvIn - cltvOut} blocks between the incoming "
                               + $"expiry {minCltvIn} and the outgoing {cltvOut}, our forwarding delta is "
                               + $"{forwardingCltvExpiryDelta}");

        if ((ulong)minCltvIn <= (ulong)height + options.MinCltvMarginBlocks)
            return RefuseBlinded($"incoming expiry {minCltvIn} too close to the height {height} (margin "
                               + $"{options.MinCltvMarginBlocks} blocks)");

        var fee = LightningMoney.MilliSatoshis(sumIn.MilliSatoshi - amountOut.MilliSatoshi);
        return new TrampolineRelayDecision(true, fee, minCltvIn - forwardingCltvExpiryDelta, fee, null, null);
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

    private static TrampolineRelayDecision RefuseBlinded(string reason) =>
        new(false, null, null, null, FailureMessage.InvalidOnionBlinding(new byte[32]), $"blinded hop: {reason}");
}

/// <summary>What <see cref="TrampolineRelayPolicy.Evaluate"/> decided.</summary>
/// <param name="IsAccepted">The set pays for the relay.</param>
/// <param name="MaxFee">Accepted: the routing fee budget of the outgoing leg (sum in − amount out: our fee covers the
/// route).</param>
/// <param name="MaxFirstHopCltvExpiry">Accepted: the highest expiry of the leg's first HTLC (lowest incoming expiry −
/// our plain forwarding delta).</param>
/// <param name="OurFee">Accepted: our trampoline fee (the least the set must pay above the amount out).</param>
/// <param name="Failure">Refused: the failure for every part (NODE|26 with our policy).</param>
/// <param name="Reason">Refused: why, for the logs and the relay row.</param>
public sealed record TrampolineRelayDecision(bool IsAccepted, LightningMoney? MaxFee, uint? MaxFirstHopCltvExpiry,
                                             LightningMoney? OurFee, FailureMessage? Failure, string? Reason);