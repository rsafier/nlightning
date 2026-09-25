namespace NLightning.Domain.Channels.Commitments;

using Bitcoin.Transactions.Enums;

/// <summary>
/// The static inputs of the commitment state machine.
/// </summary>
/// <param name="LocalIsFunder">True when we opened the channel and pay the commitment fee (and both anchors).</param>
/// <param name="FundingSatoshis">The funding output amount.</param>
/// <param name="OptionAnchors">True when <c>option_anchors</c> applies (HTLC tx fees 0, 1124 base weight, two 330-sat
/// anchors paid by the funder).</param>
/// <param name="Local">What we announced (binds the peer's offers, our commitment's dust limit, the peer's reserve).</param>
/// <param name="Remote">What the peer announced (binds our offers, the peer's commitment's dust limit, our reserve).</param>
/// <param name="MaxDustHtlcExposureMsat">Our <c>max_dust_htlc_exposure_msat</c> policy; null disables the check.</param>
public sealed record CommitmentParams(
    bool LocalIsFunder,
    ulong FundingSatoshis,
    bool OptionAnchors,
    CommitmentParty Local,
    CommitmentParty Remote,
    ulong? MaxDustHtlcExposureMsat = null)
{
    public ulong FundingMsat => checked(FundingSatoshis * 1_000);

    /// <summary>The parameters of the holder of a <paramref name="side"/> commitment (its dust limit applies).</summary>
    public CommitmentParty Holder(CommitmentSide side) => side == CommitmentSide.Local ? Local : Remote;

    /// <summary>The reserve (msat) we must keep: announced by the peer.</summary>
    public ulong LocalReserveMsat => checked(Remote.ChannelReserveSatoshis * 1_000);

    /// <summary>The reserve (msat) the peer must keep: announced by us.</summary>
    public ulong RemoteReserveMsat => checked(Local.ChannelReserveSatoshis * 1_000);
}