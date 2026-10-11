namespace NLightning.Application.Payments.Routing;

using Domain.Money;
using Domain.Protocol.Onion.Models;

/// <summary>
/// Sender side of BOLT 4 route blinding (ONION M5): turns a route to a blinded path's introduction node into a route to
/// the recipient behind the path.
/// </summary>
/// <remarks>
/// <para>The route to the introduction node is planned as if the introduction node were the payee of the amount plus
/// the path's fee (<see cref="BlindedPayInfo.ComputeFeeMsat"/>) with the path's CLTV delta as the final delta. Its
/// last hop (the introduction node) is then replaced by the blinded hops: the introduction node under its real id with
/// <c>current_path_key</c> = <c>first_path_key</c>, the others under their blinded ids, all with their
/// <c>encrypted_recipient_data</c>; the final one receives the amount with <c>outgoing_cltv_value</c> = the
/// introduction node's <c>cltv_expiry</c> minus the path's CLTV delta.</para>
/// <para>Stateless.</para>
/// </remarks>
public static class BlindedRouteComposer
{
    /// <summary>
    /// The amount the introduction node must receive for the recipient to get <paramref name="amount"/>.
    /// </summary>
    /// <exception cref="OverflowException">If it does not fit in 64 bits.</exception>
    public static LightningMoney GetIntroductionAmount(BlindedPayInfo payInfo, LightningMoney amount)
    {
        ArgumentNullException.ThrowIfNull(payInfo);
        ArgumentNullException.ThrowIfNull(amount);
        return LightningMoney.MilliSatoshis(checked(amount.MilliSatoshi + payInfo.ComputeFeeMsat(amount.MilliSatoshi)));
    }

    /// <summary>
    /// Why the path cannot carry <paramref name="amount"/> (its HTLC limits, an unknown even feature, no hop), or null
    /// when it can.
    /// </summary>
    public static string? CheckUsable(BlindedPaymentPath path, LightningMoney amount)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(amount);

        if (path.Path.Hops.Count == 0)
            return "the blinded path has no hop";

        var features = path.PayInfo.Features.Span;
        for (var i = 0; i < features.Length; i++)
        {
            // Big-endian bit field: an even bit set (bit 0 of the last byte is bit 0) is required and unknown to us
            var bitBase = (features.Length - 1 - i) * 8;
            for (var bit = 0; bit < 8; bit += 2)
                if ((features[i] & (1 << bit)) != 0)
                    return $"the blinded path requires the unknown feature bit {bitBase + bit}";
        }

        ulong introAmount;
        try
        {
            introAmount = GetIntroductionAmount(path.PayInfo, amount).MilliSatoshi;
        }
        catch (OverflowException)
        {
            return "the blinded path's fee overflows";
        }

        if (introAmount < path.PayInfo.HtlcMinimumMsat)
            return $"{introAmount} msat is below the blinded path's minimum {path.PayInfo.HtlcMinimumMsat} msat";
        if (path.PayInfo.HtlcMaximumMsat != 0 && introAmount > path.PayInfo.HtlcMaximumMsat)
            return $"{introAmount} msat is above the blinded path's maximum {path.PayInfo.HtlcMaximumMsat} msat";

        return null;
    }

    /// <summary>
    /// Replaces the last hop of <paramref name="toIntroduction"/> (the introduction node, as its payee) with the hops
    /// of <paramref name="path"/>.
    /// </summary>
    /// <param name="toIntroduction">A route whose payee is <c>path.Path.FirstNodeId</c>, receiving
    /// <see cref="GetIntroductionAmount"/> with a <c>cltv_expiry</c> at least the path's CLTV delta.</param>
    /// <param name="path">The blinded path.</param>
    /// <param name="amount">What the recipient receives.</param>
    /// <param name="totalAmount">The final hop's <c>total_amount_msat</c> (the amount, unless this is one part of a
    /// multi-part payment).</param>
    /// <param name="pathIndex">The path's index in the payment's paths (<see cref="PaymentRoute.BlindedPathIndex"/>).
    /// </param>
    /// <exception cref="ArgumentException">If the route does not end at the introduction node, or its amount or
    /// expiry cannot cover the path.</exception>
    public static PaymentRoute Compose(PaymentRoute toIntroduction, BlindedPaymentPath path, LightningMoney amount,
                                       LightningMoney? totalAmount = null, int? pathIndex = null)
    {
        ArgumentNullException.ThrowIfNull(toIntroduction);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(amount);

        var blinded = path.Path;
        if (blinded.Hops.Count == 0)
            throw new ArgumentException("The blinded path has no hop.", nameof(path));
        if (toIntroduction.BlindedStartIndex is not null)
            throw new ArgumentException("The route already ends in a blinded path.", nameof(toIntroduction));

        var introduction = toIntroduction.Hops[^1];
        if (introduction.NodeId != blinded.FirstNodeId)
            throw new ArgumentException("The route does not end at the blinded path's introduction node.",
                                        nameof(toIntroduction));
        if (introduction.AmountToForward < GetIntroductionAmount(path.PayInfo, amount))
            throw new ArgumentException("The route does not bring the blinded path's fee to the introduction node.",
                                        nameof(toIntroduction));
        if (introduction.OutgoingCltvValue < path.PayInfo.CltvExpiryDelta)
            throw new ArgumentException("The route's expiry is below the blinded path's CLTV delta.",
                                        nameof(toIntroduction));

        var finalCltv = introduction.OutgoingCltvValue - path.PayInfo.CltvExpiryDelta;
        var hops = new List<RouteHop>(toIntroduction.Hops.Count - 1 + blinded.Hops.Count);
        hops.AddRange(toIntroduction.Hops.Take(toIntroduction.Hops.Count - 1));
        for (var i = 0; i < blinded.Hops.Count; i++)
        {
            var isFinal = i == blinded.Hops.Count - 1;
            // BOLT 4: the introduction node peels its layer with its real node id, the others with their blinded ids
            var nodeId = i == 0 ? blinded.FirstNodeId : blinded.Hops[i].BlindedNodeId;
            hops.Add(new RouteHop(nodeId, amount, finalCltv, null)
            {
                EncryptedRecipientData = blinded.Hops[i].EncryptedRecipientData,
                CurrentPathKey = i == 0 ? blinded.FirstPathKey : null,
                IsBlindedRelay = !isFinal
            });
        }

        return new PaymentRoute(hops, toIntroduction.FirstHopAmount, toIntroduction.FirstHopCltvExpiry,
                                toIntroduction.PaymentHash, toIntroduction.PaymentSecret, null, totalAmount ?? amount)
        {
            BlindedPathIndex = pathIndex
        };
    }
}