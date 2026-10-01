namespace NLightning.Domain.Payments.Models;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// One part (HTLC) of a split or retried payment as it was offered (NL-321): the channel and HTLC id it went out on,
/// its state, and its route with the per-hop Sphinx shared secrets the origin needs to decrypt the part's error onion
/// after a restart.
/// </summary>
/// <remarks>
/// The payment row (<see cref="PaymentModel"/>) stays the fast path: it records one part (route, shared secrets and
/// HTLC id) and is rewritten as that part changes. Every offered part is additionally stored here, so a failure of a
/// part that is not the recorded one can still be decrypted after a restart, and the startup reconciliation
/// (<c>PaymentService.ReconcileInFlightPaymentsAsync</c>) can tell a part whose HTLC died while the node was down from
/// one that is still in flight.
/// </remarks>
/// <param name="PaymentHash">The payment hash of the payment the part belongs to.</param>
/// <param name="PartIndex">The position among the payment's offered parts, in offer order (0 first).</param>
/// <param name="ChannelId">The channel the part's HTLC was offered on.</param>
/// <param name="HtlcId">The id of the part's HTLC on <paramref name="ChannelId"/>.</param>
/// <param name="State">Where the part stands.</param>
/// <param name="Hops">The route of the part's onion, with each hop's shared secret (see <see cref="PaymentHop"/>).</param>
public sealed class PaymentPartModel(Hash paymentHash, byte partIndex, ChannelId channelId, ulong htlcId,
                                     PaymentPartState state, IReadOnlyList<PaymentHop> hops)
{
    public Hash PaymentHash { get; } = paymentHash;

    public byte PartIndex { get; } = partIndex;

    public ChannelId ChannelId { get; } = channelId;

    public ulong HtlcId { get; } = htlcId;

    public PaymentPartState State { get; set; } = state;

    /// <summary>
    /// The route of the part's onion, first hop (our peer) first and the payee last, with each hop's Sphinx shared
    /// secret; hop <c>i</c>'s <see cref="PaymentHop.HoldTime"/> is what that hop reported in a verified
    /// <c>attribution_data</c> of this part's failure or fulfill, when it was recorded.
    /// </summary>
    public IReadOnlyList<PaymentHop> Hops { get; private set; } = [.. hops];

    /// <summary>
    /// Records the hold times the hops of this part reported in a verified <c>attribution_data</c> (BOLT 4), first hop
    /// first: hop <c>i</c> gets <paramref name="holdTimes"/>[i]; hops past the list (not verified) keep what they had.
    /// </summary>
    /// <returns>True when a hop changed.</returns>
    public bool RecordHoldTimes(IReadOnlyList<TimeSpan> holdTimes)
    {
        ArgumentNullException.ThrowIfNull(holdTimes);

        var changed = false;
        var hops = Hops.ToList();
        for (var i = 0; i < hops.Count && i < holdTimes.Count; i++)
        {
            if (hops[i].HoldTime == holdTimes[i])
                continue;

            hops[i] = hops[i] with { HoldTime = holdTimes[i] };
            changed = true;
        }

        if (changed)
            Hops = hops;
        return changed;
    }
}