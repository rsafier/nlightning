using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Routing;

using Domain.Crypto.ValueObjects;
using Domain.Models;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Policies;

/// <summary>
/// Picks the route of a payment without a network graph (ONION M4-T6, ABCD roadmap §1.1 option B): the payee
/// directly when it is our peer, else our channel to the first node of a BOLT 11 route hint followed by the hint's
/// hops. Computes every hop's amount and CLTV per BOLT 4/7.
/// </summary>
/// <remarks>
/// <para>BOLT 11 <c>r</c>: each entry is (<c>pubkey</c>, <c>short_channel_id</c>, <c>fee_base_msat</c>,
/// <c>fee_proportional_millionths</c>, <c>cltv_expiry_delta</c>) of the channel from <c>pubkey</c> towards the
/// payee, so the entry's node charges that fee and requires that delta on what it forwards over that channel.
/// Working back from the payee (BOLT 7 "HTLC Fees", rounded down; BOLT 4 <c>outgoing_cltv_value</c>):</para>
/// <list type="bullet">
///   <item>payee: <c>amt_to_forward</c> = amount, <c>outgoing_cltv_value</c> = height + <c>c</c> +
///   <see cref="FinalCltvSafetyOffset"/> (blocks that may be mined while the HTLC travels);</item>
///   <item>hint entry <c>i</c> (last to first): its layer forwards what hop <c>i + 1</c> must receive, over
///   <c>short_channel_id(i)</c>; it must itself receive that amount plus
///   <c>fee_base(i) + amount * fee_ppm(i) / 1e6</c>, with a <c>cltv_expiry</c> <c>cltv_expiry_delta(i)</c> higher;</item>
///   <item>our HTLC to the first node carries what that node must receive (we charge ourselves nothing).</item>
/// </list>
/// <para>Candidates, in order: the payee directly; then each hint in invoice order. A hint that contains our own node
/// is cut after our last entry (our channel is that entry's <c>short_channel_id</c>). A candidate is used when
/// <c>hasUsableChannelTo(first node)</c> is true and its total CLTV (first HTLC <c>cltv_expiry</c> − height) is within
/// <see cref="RoutingOptions.MaxCltvExpiryDistance"/>.</para>
/// <para>Pure: it never looks at channels itself; the caller passes the predicate and resolves the channel.</para>
/// </remarks>
public sealed class HintRouteBuilder
{
    /// <summary>
    /// Blocks added to the final <c>outgoing_cltv_value</c> on top of <c>height + c</c>, so a block or two mined while
    /// the HTLC travels does not push it below the payee's <c>min_final_cltv_expiry_delta</c>.
    /// </summary>
    public const uint FinalCltvSafetyOffset = 3;

    private readonly IOptions<NodeOptions> _nodeOptions;

    public HintRouteBuilder(IOptions<NodeOptions> nodeOptions)
    {
        _nodeOptions = nodeOptions;
    }

    /// <summary>
    /// Builds the route, or throws when no candidate is usable.
    /// </summary>
    /// <param name="target">What to pay.</param>
    /// <param name="amount">What the payee must receive (the invoice amount, or the caller's for an invoice without
    /// one).</param>
    /// <param name="currentBlockHeight">Our best block height.</param>
    /// <param name="ourNodeId">Our node id.</param>
    /// <param name="hasUsableChannelTo">True when we have a usable channel to the given peer.</param>
    /// <exception cref="ArgumentException">If the amount is zero or the payee is us.</exception>
    /// <exception cref="InvalidOperationException">If no candidate route is usable (the message says why).</exception>
    public PaymentRoute Build(PaymentTarget target, LightningMoney amount, uint currentBlockHeight,
                              CompactPubKey ourNodeId, Func<CompactPubKey, bool> hasUsableChannelTo)
    {
        if (TryBuild(target, amount, currentBlockHeight, ourNodeId, hasUsableChannelTo, out var route,
                     out var failureReason))
            return route;

        throw new InvalidOperationException(failureReason);
    }

    /// <inheritdoc cref="Build" path="/param"/>
    /// <param name="route">The route, when one is usable.</param>
    /// <param name="failureReason">Why no route is usable.</param>
    /// <returns>True when a route was found.</returns>
    /// <exception cref="ArgumentException">If the amount is zero or the payee is us.</exception>
    public bool TryBuild(PaymentTarget target, LightningMoney amount, uint currentBlockHeight, CompactPubKey ourNodeId,
                         Func<CompactPubKey, bool> hasUsableChannelTo,
                         [NotNullWhen(true)] out PaymentRoute? route,
                         [NotNullWhen(false)] out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(hasUsableChannelTo);
        if (amount.IsZero)
            throw new ArgumentException("The payment amount must be positive.", nameof(amount));
        if (target.PayeeNodeId == ourNodeId)
            throw new ArgumentException("Cannot pay ourselves.", nameof(target));

        var routing = _nodeOptions.Value.Routing;
        var finalCltv = checked(currentBlockHeight + target.MinFinalCltvExpiryDelta + FinalCltvSafetyOffset);
        var reasons = new List<string>();

        foreach (var (description, path) in GetCandidates(target, ourNodeId))
        {
            var firstNode = path.Count > 0 ? path[0].CompactPubKey : target.PayeeNodeId;
            if (!hasUsableChannelTo(firstNode))
            {
                reasons.Add($"{description}: no usable channel to {firstNode}");
                continue;
            }

            var candidate = BuildAlong(path, target, amount, finalCltv);
            var totalCltvDelta = (ulong)candidate.FirstHopCltvExpiry - currentBlockHeight;
            if (totalCltvDelta > routing.MaxCltvExpiryDistance)
            {
                reasons.Add($"{description}: total CLTV delta {totalCltvDelta} exceeds "
                          + $"{routing.MaxCltvExpiryDistance} blocks");
                continue;
            }

            route = candidate;
            failureReason = null;
            return true;
        }

        route = null;
        failureReason = reasons.Count == 0
                            ? "No route to the payee: it is not our peer and the invoice has no route hints."
                            : "No usable route to the payee: " + string.Join("; ", reasons) + ".";
        return false;
    }

    private static IEnumerable<(string Description, IReadOnlyList<RoutingInfo> Path)> GetCandidates(
        PaymentTarget target, CompactPubKey ourNodeId)
    {
        yield return ("direct", []);

        for (var i = 0; i < target.RouteHints.Count; i++)
        {
            var hint = target.RouteHints[i];
            if (hint.Count == 0)
                continue;

            // A hint that passes through us starts, for us, right after our own entry
            var ourIndex = -1;
            for (var j = 0; j < hint.Count; j++)
                if (hint[j].CompactPubKey == ourNodeId)
                    ourIndex = j;

            var path = ourIndex < 0 ? hint : hint.Skip(ourIndex + 1).ToList();

            // After the cut this is the direct candidate again
            if (ourIndex >= 0 && path.Count == 0)
                continue;

            yield return ($"route hint {i}", path);
        }
    }

    private static PaymentRoute BuildAlong(IReadOnlyList<RoutingInfo> path, PaymentTarget target,
                                           LightningMoney amount, uint finalCltv)
    {
        var hops = new RouteHop[path.Count + 1];
        hops[^1] = new RouteHop(target.PayeeNodeId, amount, finalCltv, null);

        var amountMsat = amount.MilliSatoshi;
        var cltv = finalCltv;
        for (var i = path.Count - 1; i >= 0; i--)
        {
            var entry = path[i];
            hops[i] = new RouteHop(entry.CompactPubKey, LightningMoney.MilliSatoshis(amountMsat), cltv,
                                   entry.ShortChannelId);

            amountMsat = ForwardingFee.RequiredIncomingMsat(entry.FeeBaseMsat, entry.FeeProportionalMillionths,
                                                            amountMsat);
            cltv = checked(cltv + entry.CltvExpiryDelta);
        }

        return new PaymentRoute(hops, LightningMoney.MilliSatoshis(amountMsat), cltv, target.PaymentHash,
                                target.PaymentSecret, target.PaymentMetadata);
    }
}