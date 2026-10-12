namespace NLightning.Domain.Channels.Commitments;

/// <summary>
/// The channel parameters one node announced in <c>open_channel</c>/<c>accept_channel</c>.
/// </summary>
/// <remarks>
/// Direction rules (BOLT 2 §open_channel, plan §3.2): the values <b>we</b> announced
/// (<see cref="CommitmentParams.Local"/>) bind the HTLCs the <b>peer</b> offers us (minimum, max accepted, max in
/// flight), <see cref="ChannelReserveSatoshis"/> is what the <b>other</b> node must keep, and
/// <see cref="DustLimitSatoshis"/> applies to the announcing node's <b>own</b> commitment.
/// </remarks>
/// <param name="DustLimitSatoshis">Trimming threshold of this node's own commitment.</param>
/// <param name="ChannelReserveSatoshis">The balance the other node must keep.</param>
/// <param name="HtlcMinimumMsat">The smallest HTLC this node accepts.</param>
/// <param name="MaxAcceptedHtlcs">How many HTLCs the other node may have offered to this node at once.</param>
/// <param name="MaxHtlcValueInFlightMsat">The total value the other node may have offered to this node at once.</param>
public sealed record CommitmentParty(
    ulong DustLimitSatoshis,
    ulong ChannelReserveSatoshis,
    ulong HtlcMinimumMsat,
    ushort MaxAcceptedHtlcs,
    ulong MaxHtlcValueInFlightMsat);