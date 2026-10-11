namespace NLightning.Domain.Protocol.Onion.Models;

using Constants;

/// <summary>
/// What the origin node learned from the <c>attribution_data</c> of an <c>update_fail_htlc</c> or
/// <c>update_fulfill_htlc</c> (BOLT 4 attributable failures).
/// </summary>
/// <remarks>
/// Hops are verified in route order, first hop first, each at its position in the route. Verification stops at the
/// first hop whose truncated HMAC does not match: that hop and its upstream neighbour are the pair to penalize, because
/// nobody can tell which of the two modified the data. When not every hop supports <c>option_attribution_data</c>,
/// verification stops at the first downstream hop without support.
/// </remarks>
public sealed class AttributionVerification
{
    /// <summary>
    /// A result for a message that carried no <c>attribution_data</c> (or one that could not be checked).
    /// </summary>
    public static AttributionVerification Absent { get; } = new(false, [], null);

    /// <summary>
    /// True when <c>attribution_data</c> was present and checked.
    /// </summary>
    public bool IsPresent { get; }

    /// <summary>
    /// The hold time reported by each verified hop, first hop first, in units of
    /// <see cref="OnionConstants.AttributionHoldTimeUnitMilliseconds"/> ms (zero means "no timing information").
    /// </summary>
    public IReadOnlyList<uint> HoldTimes { get; }

    /// <summary>
    /// The first hop whose HMAC did not verify, or <c>null</c> when every hop that was checked verified.
    /// </summary>
    public int? InvalidHopIndex { get; }

    /// <summary>
    /// The number of hops whose HMAC verified (<see cref="HoldTimes"/> has one entry per verified hop).
    /// </summary>
    public int VerifiedHopCount => HoldTimes.Count;

    public AttributionVerification(bool isPresent, IReadOnlyList<uint> holdTimes, int? invalidHopIndex)
    {
        ArgumentNullException.ThrowIfNull(holdTimes);
        if (invalidHopIndex is < 0)
            throw new ArgumentOutOfRangeException(nameof(invalidHopIndex));

        IsPresent = isPresent;
        HoldTimes = holdTimes;
        InvalidHopIndex = invalidHopIndex;
    }

    /// <summary>
    /// The reported hold time of a verified hop as a duration.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">If the hop was not verified.</exception>
    public TimeSpan GetHoldTime(int hopIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hopIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(hopIndex, HoldTimes.Count);

        return AttributionHoldTime.ToDuration(HoldTimes[hopIndex]);
    }
}