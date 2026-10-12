namespace NLightning.Domain.Channels.Policies;

using Money;
using Node.Options;

/// <summary>
/// The <c>max_htlc_value_in_flight_msat</c> we announce in <c>open_channel</c>, <c>accept_channel</c>,
/// <c>open_channel2</c> and <c>accept_channel2</c> (BOLT 2: a cap on the total value of outstanding HTLCs the peer
/// offers us). The value is fixed for the channel's lifetime: no message changes it, a splice included, so it is the
/// peer's in-flight cap (and the <c>htlc_maximum_msat</c> its <c>channel_update</c> may announce, BOLT 7) for as long
/// as the channel lives.
/// </summary>
/// <remarks>
/// NL-880: a share of the opening capacity (<see cref="NodeOptions.AllowUpToPercentageOfChannelFundsInFlight"/>)
/// froze the peer's sends at that share of the channel's first size, however much a splice later added (a 140k
/// channel spliced to 210k kept the peer at 48,000,000 msat). A channel opened with <c>option_splice</c> negotiated can
/// grow, so it announces no cap (<see cref="NoLimit"/>, the u64 maximum, as CLN does by default); the peer's sends stay
/// bound by its balance and the reserve, and our <c>channel_update</c> by the current capacity.
/// <see cref="NodeOptions.LimitInFlightOnSpliceableChannels"/> keeps the share on those channels too.
/// </remarks>
public static class MaxHtlcValueInFlightRules
{
    /// <summary>No cap: the largest <c>u64</c>.</summary>
    public static LightningMoney NoLimit => LightningMoney.MilliSatoshis(ulong.MaxValue);

    /// <summary>
    /// Our announced <c>max_htlc_value_in_flight_msat</c> for a channel whose capacity known at the open is
    /// <paramref name="capacity"/>: <see cref="NoLimit"/> when <paramref name="spliceNegotiated"/> (and
    /// <see cref="NodeOptions.LimitInFlightOnSpliceableChannels"/> is off), otherwise
    /// <see cref="NodeOptions.AllowUpToPercentageOfChannelFundsInFlight"/> percent of it.
    /// </summary>
    public static LightningMoney GetAnnounced(NodeOptions nodeOptions, LightningMoney capacity, bool spliceNegotiated)
    {
        ArgumentNullException.ThrowIfNull(nodeOptions);
        ArgumentNullException.ThrowIfNull(capacity);

        if (spliceNegotiated && !nodeOptions.LimitInFlightOnSpliceableChannels)
            return NoLimit;

        return LightningMoney.Satoshis(nodeOptions.AllowUpToPercentageOfChannelFundsInFlight * capacity.Satoshi / 100M);
    }
}