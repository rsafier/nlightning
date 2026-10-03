namespace NLightning.Domain.Protocol.Onion.Models;

using Constants;

/// <summary>
/// Conversions for <c>htlc_hold_times</c> entries: u32 values in units of 100 ms (BOLT 4).
/// </summary>
public static class AttributionHoldTime
{
    /// <summary>
    /// Converts how long this node held an HTLC (from receiving its <c>update_add_htlc</c> to sending the
    /// <c>update_fail_htlc</c>/<c>update_fulfill_htlc</c> upstream) into a hold time, rounding down and saturating at
    /// <see cref="uint.MaxValue"/>. A negative duration is reported as zero ("no timing information").
    /// </summary>
    public static uint FromDuration(TimeSpan held)
    {
        if (held <= TimeSpan.Zero)
            return 0;

        var units = held.Ticks / (TimeSpan.TicksPerMillisecond * OnionConstants.AttributionHoldTimeUnitMilliseconds);
        return units >= uint.MaxValue ? uint.MaxValue : (uint)units;
    }

    /// <summary>
    /// Converts a reported hold time into a duration.
    /// </summary>
    public static TimeSpan ToDuration(uint holdTime)
    {
        return TimeSpan.FromMilliseconds((double)holdTime * OnionConstants.AttributionHoldTimeUnitMilliseconds);
    }
}