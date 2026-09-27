using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Payments.Send.Blinded;

using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Routing;

/// <summary>
/// A blinded payment path whose introduction node is this node (BOLT 12 plan B12-PAY-02): our own hops are decrypted
/// here, so the payment starts at the first hop that is not us, which gets the next path_key in
/// <c>update_add_htlc</c> (BOLT 4 "Route Blinding": a blinded hop that is not the introduction node reads its
/// path_key from <c>update_add_htlc</c>).
/// </summary>
/// <remarks>
/// <para>CLN puts us as the introduction node of its invoice paths when we are its only peer. We play the role of the
/// introduction node for our own HTLC: the amount and expiry the introduction node would have received (the path's
/// aggregate <c>blinded_payinfo</c>) are turned into what our hop forwards with its <c>payment_relay</c>, as a
/// forwarding node computes it, and our <c>payment_constraints</c> are checked.</para>
/// <para>Immutable; built by <see cref="TryResolve"/>.</para>
/// </remarks>
public sealed class SelfIntroducedBlindedPath
{
    private SelfIntroducedBlindedPath(BlindedPaymentPath path, IReadOnlyList<BlindedHopUnblinding> ourHops,
                                      CompactPubKey nextNodeId, IReadOnlyList<BlindedPathHop> remainingHops)
    {
        Path = path;
        OurHops = ourHops;
        NextNodeId = nextNodeId;
        RemainingHops = remainingHops;
    }

    /// <summary>
    /// The path as the recipient gave it.
    /// </summary>
    public BlindedPaymentPath Path { get; }

    /// <summary>
    /// Our own hops of the path, decrypted, first first.
    /// </summary>
    public IReadOnlyList<BlindedHopUnblinding> OurHops { get; }

    /// <summary>
    /// The real node id of the first hop that is not us: our peer, whom the HTLC is offered to.
    /// </summary>
    public CompactPubKey NextNodeId { get; }

    /// <summary>
    /// The path_key our <c>update_add_htlc</c> carries (the last of our hops' next path_key).
    /// </summary>
    public CompactPubKey NextPathKey => OurHops[^1].NextPathKey;

    /// <summary>
    /// The hops after ours, the recipient last.
    /// </summary>
    public IReadOnlyList<BlindedPathHop> RemainingHops { get; }

    /// <summary>
    /// Decrypts our hops of <paramref name="path"/> (its introduction node must be <paramref name="ourNodeId"/>).
    /// </summary>
    /// <param name="path">The recipient's path.</param>
    /// <param name="ourNodeId">Our node id.</param>
    /// <param name="routeBlindingService">Decrypts with the node key.</param>
    /// <param name="channels">Our channels, to name the peer of a <c>short_channel_id</c> (real SCID or alias).</param>
    /// <param name="resolved">The path from our peer on.</param>
    /// <param name="reason">Why the path cannot be used.</param>
    public static bool TryResolve(BlindedPaymentPath path, CompactPubKey ourNodeId,
                                  IRouteBlindingService routeBlindingService, IEnumerable<ChannelModel> channels,
                                  [NotNullWhen(true)] out SelfIntroducedBlindedPath? resolved,
                                  [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(routeBlindingService);
        ArgumentNullException.ThrowIfNull(channels);

        resolved = null;
        if (path.Path.FirstNodeId != ourNodeId)
        {
            reason = "we are not its introduction node";
            return false;
        }

        var channelList = channels as IReadOnlyCollection<ChannelModel> ?? channels.ToList();
        var hops = path.Path.Hops;
        var ourHops = new List<BlindedHopUnblinding>();
        var pathKey = path.Path.FirstPathKey;
        var index = 0;
        while (true)
        {
            if (index >= hops.Count - 1)
            {
                reason = "the path ends at this node (a node cannot pay itself)";
                return false;
            }

            BlindedHopUnblinding unblinding;
            try
            {
                unblinding = routeBlindingService.UnblindAsLocalNode(pathKey, hops[index].EncryptedRecipientData);
            }
            catch (OnionException)
            {
                reason = $"our hop {index} of the path does not decrypt with our node key";
                return false;
            }

            var data = unblinding.RecipientData;
            if (data.PathId is not null)
            {
                reason = $"our hop {index} of the path is a final hop (it has a path_id)";
                return false;
            }

            if (data.PaymentRelay is null)
            {
                reason = $"our hop {index} of the path has no payment_relay";
                return false;
            }

            if (data.HasAnyAllowedFeature)
            {
                reason = $"our hop {index} of the path requires unknown features";
                return false;
            }

            ourHops.Add(unblinding);
            var next = data.NextNodeId;
            if (next is null && data.ShortChannelId is { } scid)
                next = channelList.FirstOrDefault(c => c.ShortChannelId == scid
                                                    || c.RemoteAlias == scid
                                                    || c.LocalAliases?.Contains(scid) == true)?.RemoteNodeId;
            if (next is not { } nextNodeId)
            {
                reason = data.ShortChannelId is { } unknown
                             ? $"our hop {index} of the path names channel {unknown}, which is not ours"
                             : $"our hop {index} of the path names no next node";
                return false;
            }

            index++;
            if (nextNodeId != ourNodeId)
            {
                resolved = new SelfIntroducedBlindedPath(path, ourHops, nextNodeId, hops.Skip(index).ToList());
                reason = null;
                return true;
            }

            pathKey = unblinding.NextPathKey;
        }
    }

    /// <summary>
    /// What our HTLC to <see cref="NextNodeId"/> carries when the introduction node (us) would have received
    /// <paramref name="introductionAmountMsat"/> with <paramref name="introductionCltvExpiry"/>: each of our hops
    /// applies its <c>payment_relay</c> (BOLT 4 reader of a non-final blinded hop) after its
    /// <c>payment_constraints</c> are checked.
    /// </summary>
    public bool TryComputeFirstHop(ulong introductionAmountMsat, uint introductionCltvExpiry, out ulong amountMsat,
                                   out uint cltvExpiry, [NotNullWhen(false)] out string? reason)
    {
        amountMsat = introductionAmountMsat;
        cltvExpiry = introductionCltvExpiry;
        foreach (var hop in OurHops)
        {
            var data = hop.RecipientData;
            if (data.PaymentConstraints is { } constraints)
            {
                if (cltvExpiry > constraints.MaxCltvExpiry)
                {
                    reason = $"the path expired at our hop (max_cltv_expiry {constraints.MaxCltvExpiry})";
                    return false;
                }

                if (amountMsat < constraints.HtlcMinimumMsat)
                {
                    reason = $"{amountMsat} msat is below our hop's htlc_minimum {constraints.HtlcMinimumMsat} msat";
                    return false;
                }
            }

            var relay = data.PaymentRelay!;
            if (!relay.TryComputeAmountToForward(amountMsat, out amountMsat)
             || !relay.TryComputeOutgoingCltvValue(cltvExpiry, out cltvExpiry))
            {
                reason = "our hop's payment_relay does not fit the amount or expiry";
                return false;
            }
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// The route from our peer to the recipient: every remaining hop by its blinded id with its
    /// <c>encrypted_recipient_data</c> and no <c>current_path_key</c>; the final one receives
    /// <paramref name="amount"/> with <paramref name="finalCltv"/>.
    /// </summary>
    /// <param name="amount">What the recipient receives on this route.</param>
    /// <param name="totalAmount">The final <c>total_amount_msat</c>.</param>
    /// <param name="firstHopAmountMsat">Our HTLC's amount (<see cref="TryComputeFirstHop"/>).</param>
    /// <param name="firstHopCltvExpiry">Our HTLC's <c>cltv_expiry</c>.</param>
    /// <param name="finalCltv">The recipient's <c>outgoing_cltv_value</c>: the introduction node's
    /// <c>cltv_expiry</c> minus the path's aggregate delta.</param>
    /// <param name="paymentHash">The payment hash.</param>
    /// <param name="pathIndex">The path's index in the payment's paths.</param>
    public PaymentRoute ComposeRoute(LightningMoney amount, LightningMoney totalAmount, ulong firstHopAmountMsat,
                                     uint firstHopCltvExpiry, uint finalCltv, Hash paymentHash, int pathIndex)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(totalAmount);

        var hops = new List<RouteHop>(RemainingHops.Count);
        for (var i = 0; i < RemainingHops.Count; i++)
        {
            hops.Add(new RouteHop(RemainingHops[i].BlindedNodeId, amount, finalCltv, null)
            {
                EncryptedRecipientData = RemainingHops[i].EncryptedRecipientData,
                IsBlindedRelay = i < RemainingHops.Count - 1
            });
        }

        return new PaymentRoute(hops, LightningMoney.MilliSatoshis(firstHopAmountMsat), firstHopCltvExpiry,
                                paymentHash, new Secret(new byte[32]), null, totalAmount, NextPathKey)
        {
            BlindedPathIndex = pathIndex
        };
    }
}