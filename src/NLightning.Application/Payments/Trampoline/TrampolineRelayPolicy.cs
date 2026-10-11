namespace NLightning.Application.Payments.Trampoline;

using Channels.RoutingPolicies;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Onion.Models;
using Policy;

/// <summary>
/// Whether a complete incoming set of a trampoline relay pays for what it asks (BOLTs PR 836 TR-R-09, NL-875 TR3):
/// pure, over <see cref="TrampolineOptions"/> and the node's <see cref="RoutingOptions"/>.
/// </summary>
/// <remarks>
/// <para>Fee: sum in − amount out ≥ <c>FeeBaseMsat</c> + amount out × <c>FeeProportionalMillionths</c> / 10⁶. Expiry:
/// lowest incoming <c>cltv_expiry</c> − <c>outgoing_cltv_value</c> ≥ <c>CltvExpiryDelta</c>. A refusal for either is
/// <c>trampoline_fee_or_expiry_insufficient</c> carrying our policy, which tells the payer what to offer next (D-TR6).
/// </para>
/// <para>Expiry bounds (NL-922, as <c>HtlcForwardingPolicy</c> for a forward): an outgoing expiry within
/// <c>Node:Routing:ExpiryTooSoonBlocks</c> of the height (expiry_too_soon; <c>outgoing_cltv_value</c>, plus the
/// recipient's path delta when we pay its blinded paths, <see cref="TrampolineRelaySet.EarliestOutgoingCltv"/>), an
/// incoming <c>cltv_expiry</c> more than <c>Node:Routing:MaxCltvExpiryDistance</c> above it (expiry_too_far) and a
/// lowest incoming expiry within <c>MinCltvMarginBlocks</c> of it are refused with <c>temporary_trampoline_failure</c>:
/// our fee and delta are not what is missing, so NODE|26 would send the payer after a policy it already pays. Before
/// NL-922 only the leg's planner bounded a far expiry (it caps the first hop at <c>MaxCltvExpiryDistance</c> above the
/// height, so such a leg found no route), after the relay had taken a <c>MaxRelaysInFlight</c> slot and started the
/// leg, and nothing bounded the incoming expiries.</para>
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
    /// <param name="routing">Our forwarding options: the plain forwarding delta (the blocks kept between the leg's first
    /// HTLC and the lowest incoming expiry, at most our trampoline delta), <c>ExpiryTooSoonBlocks</c> and
    /// <c>MaxCltvExpiryDistance</c>.</param>
    /// <param name="set">The complete set.</param>
    public static TrampolineRelayDecision Evaluate(TrampolineOptions options, RoutingOptions routing,
                                                   TrampolineRelaySet set)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(set);

        var (sumIn, minCltvIn, maxCltvIn, amountOut, cltvOut, height) = set;
        var fee = FeeOf(options, amountOut);
        if (sumIn.MilliSatoshi < amountOut.MilliSatoshi
         || sumIn.MilliSatoshi - amountOut.MilliSatoshi < fee.MilliSatoshi)
            return Refuse(options, $"fee too low: {sumIn.MilliSatoshi} msat in for {amountOut.MilliSatoshi} msat out, "
                                 + $"our fee is {fee.MilliSatoshi} msat");

        if (DescribeTooSoon(routing, set.EarliestOutgoingCltv, height) is { } tooSoon)
            return RefuseTemporarily(tooSoon);

        if (minCltvIn < cltvOut || minCltvIn - cltvOut < options.CltvExpiryDelta)
            return Refuse(options, $"expiry delta too low: incoming {minCltvIn}, outgoing {cltvOut}, our delta is "
                                 + $"{options.CltvExpiryDelta}");

        if (DescribeTooFar(routing, maxCltvIn, height) is { } tooFar)
            return RefuseTemporarily(tooFar);

        if ((ulong)minCltvIn <= (ulong)height + options.MinCltvMarginBlocks)
            return RefuseTemporarily($"incoming expiry {minCltvIn} too close to the height {height} (margin "
                                   + $"{options.MinCltvMarginBlocks} blocks)");

        var maxFee = LightningMoney.MilliSatoshis(sumIn.MilliSatoshi - amountOut.MilliSatoshi);
        var keptDelta = Math.Min(routing.CltvExpiryDelta, options.CltvExpiryDelta);
        return new TrampolineRelayDecision(true, maxFee, minCltvIn - keptDelta, fee, null, null);
    }

    /// <summary>
    /// Whether a blinded trampoline hop's <c>payment_relay</c> is at least our policy for that hop (NL-922, D-NL922-1):
    /// checked for every part before it joins a relay, as <c>HtlcForwardingPolicy</c> checks a blinded forward's.
    /// </summary>
    /// <remarks>
    /// <para>The recipient fixed the hop's price in <c>payment_relay</c> (D-NL895-2), but from our own
    /// <c>channel_update</c>: a <c>payment_relay</c> below the policy of the hop is refused (else anyone could route a
    /// free circular rebalance through us). <paramref name="policy"/> is the outgoing channel's configured policy for a
    /// hop the recipient data names by <c>short_channel_id</c> (its <c>setchannelpolicy</c> override, else
    /// <c>Node:Routing</c>), and <c>Node:Routing</c> for a <c>next_node_id</c> hop (no channel is named); never
    /// <c>Node:Trampoline</c>, which prices the unblinded trampoline service.</para>
    /// <para>BOLT 7 grace period, exactly as for a forward (<c>HtlcForwardingPolicy.PaymentRelayCoversFee</c> and
    /// <c>LenientCltvExpiryDelta</c>): the fee and the <c>cltv_expiry_delta</c> may each satisfy a policy the channel's
    /// last changes replaced within <c>ChannelPolicyStore.PreviousPolicyGracePeriod</c> instead of the current one.</para>
    /// </remarks>
    /// <param name="paymentRelay">The recipient data's <c>payment_relay</c>.</param>
    /// <param name="policy">Our policy for the hop.</param>
    /// <param name="previousPolicies">The policies the hop's channel replaced within the grace period (none for a
    /// <c>next_node_id</c> hop).</param>
    /// <returns>Accepted with the delta we keep for the hop: the channel's <c>cltv_expiry_delta</c> (the smallest of the
    /// current one and those still in grace), which <see cref="EvaluateBlinded"/> keeps below the lowest incoming expiry
    /// and as the leg's first-hop cap; or refused with the reason.</returns>
    public static BlindedHopPriceCheck CheckBlindedHopPrice(BlindedPaymentRelay paymentRelay,
                                                            ConfiguredChannelPolicy policy,
                                                            IReadOnlyList<ConfiguredChannelPolicy> previousPolicies)
    {
        ArgumentNullException.ThrowIfNull(paymentRelay);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(previousPolicies);

        if (!HtlcForwardingPolicy.PaymentRelayCoversFee(paymentRelay.FeeBaseMsat,
                                                        paymentRelay.FeeProportionalMillionths, policy,
                                                        previousPolicies))
            return new BlindedHopPriceCheck(false, 0,
                                            $"payment_relay fee {paymentRelay.FeeBaseMsat} msat + "
                                          + $"{paymentRelay.FeeProportionalMillionths} ppm is below our policy "
                                          + $"{policy.FeeBaseMsat} msat + {policy.FeeProportionalMillionths} ppm");

        var keptDelta = HtlcForwardingPolicy.LenientCltvExpiryDelta(policy, previousPolicies);
        if (paymentRelay.CltvExpiryDelta < keptDelta)
            return new BlindedHopPriceCheck(false, 0,
                                            $"payment_relay cltv_expiry_delta {paymentRelay.CltvExpiryDelta} is below "
                                          + $"our {keptDelta}");

        return new BlindedHopPriceCheck(true, keptDelta, null);
    }

    /// <summary>
    /// Checks a complete set of a blinded trampoline hop (BOLTs PR 836 TR-R-10, NL-895 D-NL895-2, NL-922): the
    /// recipient fixed the hop's price in the path's <c>payment_relay</c>, which <see cref="TrampolineRelaySet.AmountOut"/>
    /// and <see cref="TrampolineRelaySet.CltvOut"/> already come from (the outer total and expiry, see
    /// <c>IncomingOnionTrampolineRelay</c>; <c>payment_constraints</c> checked there too) and which every part was
    /// checked against with <see cref="CheckBlindedHopPrice"/>, so our <c>Node:Trampoline</c> fee and delta do not apply
    /// and a refusal is never NODE|26.
    /// </summary>
    /// <remarks>
    /// <para>What it checks, in order: the set covers the amount out; the outgoing expiry is more than
    /// <c>Node:Routing:ExpiryTooSoonBlocks</c> above the height; the lowest incoming expiry keeps
    /// <paramref name="keptCltvExpiryDelta"/> (the hop's delta from <see cref="CheckBlindedHopPrice"/>) above the
    /// outgoing one; no incoming expiry is more than <c>Node:Routing:MaxCltvExpiryDistance</c> above the height; the
    /// lowest incoming expiry is more than <c>MinCltvMarginBlocks</c> above it. The leg's budget is what
    /// <c>payment_relay</c> granted: the fee is sum in − amount out, the first HTLC's expiry at most the lowest incoming
    /// expiry − <paramref name="keptCltvExpiryDelta"/>.</para>
    /// <para>A refusal carries <c>invalid_onion_blinding</c> (with an empty <c>sha256_of_onion</c>: the relay engine
    /// answers each part as the blinded rules say, our own error at the introduction node, malformed past it).</para>
    /// </remarks>
    /// <param name="options">Our trampoline options (only <c>MinCltvMarginBlocks</c> is read).</param>
    /// <param name="routing">Our forwarding options (<c>ExpiryTooSoonBlocks</c>, <c>MaxCltvExpiryDistance</c>).</param>
    /// <param name="set">The complete set.</param>
    /// <param name="keptCltvExpiryDelta">The blocks we keep between the leg's first HTLC and the lowest incoming
    /// expiry.</param>
    public static TrampolineRelayDecision EvaluateBlinded(TrampolineOptions options, RoutingOptions routing,
                                                          TrampolineRelaySet set, ushort keptCltvExpiryDelta)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(set);

        var (sumIn, minCltvIn, maxCltvIn, amountOut, cltvOut, height) = set;
        if (sumIn.MilliSatoshi < amountOut.MilliSatoshi)
            return RefuseBlinded($"{sumIn.MilliSatoshi} msat in for {amountOut.MilliSatoshi} msat out");

        if (DescribeTooSoon(routing, set.EarliestOutgoingCltv, height) is { } tooSoon)
            return RefuseBlinded(tooSoon);

        if (minCltvIn < cltvOut || minCltvIn - cltvOut < keptCltvExpiryDelta)
            return RefuseBlinded($"payment_relay leaves {(long)minCltvIn - cltvOut} blocks between the incoming "
                               + $"expiry {minCltvIn} and the outgoing {cltvOut}, the hop's delta is "
                               + $"{keptCltvExpiryDelta}");

        if (DescribeTooFar(routing, maxCltvIn, height) is { } tooFar)
            return RefuseBlinded(tooFar);

        if ((ulong)minCltvIn <= (ulong)height + options.MinCltvMarginBlocks)
            return RefuseBlinded($"incoming expiry {minCltvIn} too close to the height {height} (margin "
                               + $"{options.MinCltvMarginBlocks} blocks)");

        var fee = LightningMoney.MilliSatoshis(sumIn.MilliSatoshi - amountOut.MilliSatoshi);
        return new TrampolineRelayDecision(true, fee, minCltvIn - keptCltvExpiryDelta, fee, null, null);
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

    /// <summary>expiry_too_soon, inclusive as in <c>HtlcForwardingPolicy</c> (and LND).</summary>
    private static string? DescribeTooSoon(RoutingOptions routing, ulong outgoingCltv, uint height) =>
        outgoingCltv <= (ulong)height + routing.ExpiryTooSoonBlocks
            ? $"expiry too soon: the outgoing expiry {outgoingCltv} is within {routing.ExpiryTooSoonBlocks} blocks of "
            + $"the height {height}"
            : null;

    /// <summary>expiry_too_far, as in <c>HtlcForwardingPolicy</c>.</summary>
    private static string? DescribeTooFar(RoutingOptions routing, uint maxCltvIn, uint height) =>
        (ulong)maxCltvIn > (ulong)height + routing.MaxCltvExpiryDistance
            ? $"expiry too far: incoming cltv_expiry {maxCltvIn} is more than {routing.MaxCltvExpiryDistance} blocks "
            + $"above the height {height}"
            : null;

    private static TrampolineRelayDecision Refuse(TrampolineOptions options, string reason) =>
        new(false, null, null, null, InsufficientFailure(options), reason);

    private static TrampolineRelayDecision RefuseTemporarily(string reason) =>
        new(false, null, null, null, FailureMessage.TemporaryTrampolineFailure(), reason);

    private static TrampolineRelayDecision RefuseBlinded(string reason) =>
        new(false, null, null, null, FailureMessage.InvalidOnionBlinding(new byte[32]), $"blinded hop: {reason}");
}

/// <summary>A complete incoming set of a trampoline relay, as the policy reads it.</summary>
/// <param name="SumIn">The sum of the incoming parts' amounts.</param>
/// <param name="MinCltvIn">The lowest incoming part's <c>cltv_expiry</c>.</param>
/// <param name="MaxCltvIn">The highest incoming part's <c>cltv_expiry</c> (expiry_too_far).</param>
/// <param name="AmountOut">What the next node must receive (<c>amt_to_forward</c>, or from <c>payment_relay</c>).
/// </param>
/// <param name="CltvOut">The expiry the next node must receive (<c>outgoing_cltv_value</c>, or from
/// <c>payment_relay</c>).</param>
/// <param name="Height">The current block height.</param>
public sealed record TrampolineRelaySet(LightningMoney SumIn, uint MinCltvIn, uint MaxCltvIn, LightningMoney AmountOut,
                                        uint CltvOut, uint Height)
{
    /// <summary>
    /// When the relay pays the recipient's blinded paths (<c>recipient_blinded_paths</c>): the smallest of their
    /// aggregated <c>cltv_expiry_delta</c>, which the leg adds to <see cref="CltvOut"/> (the payer sets that near the
    /// height, BOLT 12 style); 0 for a next node. Only expiry_too_soon reads it: the leg's HTLCs expire at least that
    /// much later.
    /// </summary>
    public ushort RecipientPathCltvExpiryDelta { get; init; }

    /// <summary>The earliest expiry the leg's outgoing HTLCs can carry.</summary>
    public ulong EarliestOutgoingCltv => (ulong)CltvOut + RecipientPathCltvExpiryDelta;
}

/// <summary>What <see cref="TrampolineRelayPolicy.CheckBlindedHopPrice"/> decided.</summary>
/// <param name="IsAccepted">The <c>payment_relay</c> is at least our policy for the hop.</param>
/// <param name="KeptCltvExpiryDelta">Accepted: the hop's <c>cltv_expiry_delta</c> we keep (grace included).</param>
/// <param name="Reason">Refused: why.</param>
public sealed record BlindedHopPriceCheck(bool IsAccepted, ushort KeptCltvExpiryDelta, string? Reason);

/// <summary>What <see cref="TrampolineRelayPolicy.Evaluate"/> decided.</summary>
/// <param name="IsAccepted">The set pays for the relay.</param>
/// <param name="MaxFee">Accepted: the routing fee budget of the outgoing leg (sum in − amount out: our fee covers the
/// route).</param>
/// <param name="MaxFirstHopCltvExpiry">Accepted: the highest expiry of the leg's first HTLC (lowest incoming expiry −
/// the delta we keep).</param>
/// <param name="OurFee">Accepted: our trampoline fee (the least the set must pay above the amount out).</param>
/// <param name="Failure">Refused: the failure for every part (NODE|26 with our policy for the fee and the delta,
/// <c>temporary_trampoline_failure</c> for the expiry bounds, <c>invalid_onion_blinding</c> for a blinded hop).</param>
/// <param name="Reason">Refused: why, for the logs and the relay row.</param>
public sealed record TrampolineRelayDecision(bool IsAccepted, LightningMoney? MaxFee, uint? MaxFirstHopCltvExpiry,
                                             LightningMoney? OurFee, FailureMessage? Failure, string? Reason);