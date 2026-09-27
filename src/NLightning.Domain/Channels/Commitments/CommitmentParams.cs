namespace NLightning.Domain.Channels.Commitments;

using Bitcoin.Transactions.Enums;
using Models;
using Splicing;
using Splicing.Enums;
using ValueObjects;

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
/// <param name="HasInferredLimits">True when some announced limits are guesses (a channel migrated by
/// <c>SplitChannelParams</c>, see <see cref="ChannelParams.HasInferredParams"/>): the receiver-side limit checks that
/// would fail the channel (B2-ADD-R01 htlc_minimum, B2-ADD-R03 max_accepted_htlcs / max_htlc_value_in_flight, the
/// reserve part of B2-ADD-R02) are skipped. Rules that do not depend on the announced limits (amount 0, cltv, fee
/// affordability) still apply, and our own offers are still checked against the (possibly guessed) peer limits, which
/// can only refuse a send, never fail the channel.</param>
/// <param name="Funding">The channel's current funding (splicing plan SP1-0), handed to the signer and verifier ports;
/// null when unknown (an engine built by hand in tests; such an engine cannot hold a pending splice). The pending
/// fundings live in <see cref="ChannelCommitments.PendingFundings"/>; a lock
/// (<see cref="ChannelCommitments.LockFunding"/>) replaces this and <see cref="FundingSatoshis"/>, which is always the
/// current capacity.</param>
public sealed record CommitmentParams(
    bool LocalIsFunder,
    ulong FundingSatoshis,
    bool OptionAnchors,
    CommitmentParty Local,
    CommitmentParty Remote,
    ulong? MaxDustHtlcExposureMsat = null,
    bool HasInferredLimits = false,
    ChannelFunding? Funding = null)
{
    public ulong FundingMsat => checked(FundingSatoshis * 1_000);

    /// <summary>The parameters of the holder of a <paramref name="side"/> commitment (its dust limit applies).</summary>
    public CommitmentParty Holder(CommitmentSide side) => side == CommitmentSide.Local ? Local : Remote;

    /// <summary>The reserve (msat) we must keep: announced by the peer.</summary>
    public ulong LocalReserveMsat => checked(Remote.ChannelReserveSatoshis * 1_000);

    /// <summary>The reserve (msat) the peer must keep: announced by us.</summary>
    public ulong RemoteReserveMsat => checked(Local.ChannelReserveSatoshis * 1_000);

    /// <summary>
    /// The reserve (msat) we must keep on the commitments of <paramref name="funding"/> (splicing plan D9, SP-I6): the
    /// announced one (<see cref="LocalReserveMsat"/>) on the initial funding (or when the funding is unknown), else the
    /// larger of it and 1 % of that funding's capacity.
    /// </summary>
    public ulong LocalReserveMsatOn(ChannelFunding? funding) => ReserveMsatOn(Remote.ChannelReserveSatoshis, funding);

    /// <summary>The reserve (msat) the peer must keep on the commitments of <paramref name="funding"/> (see
    /// <see cref="LocalReserveMsatOn"/>).</summary>
    public ulong RemoteReserveMsatOn(ChannelFunding? funding) => ReserveMsatOn(Local.ChannelReserveSatoshis, funding);

    /// <summary>
    /// The reserve (msat) we hold the peer to when we <b>receive</b> its updates on the commitments of
    /// <paramref name="funding"/>: the announced one on the initial funding, else the <b>smaller</b> of it and 1 % of
    /// that funding's capacity. BOLT 2 defines no reserve for a spliced v1 channel and Eclair uses 1 % of the capacity
    /// there, so a peer that keeps only that must not be failed (splicing plan D9, Q3); our own sends keep the stricter
    /// <see cref="LocalReserveMsatOn"/>.
    /// </summary>
    public ulong RemoteReceiveReserveMsatOn(ChannelFunding? funding) =>
        checked((funding is null || funding.Kind == ChannelFundingKind.Initial
                     ? Local.ChannelReserveSatoshis
                     : Math.Min(Local.ChannelReserveSatoshis, funding.CapacitySatoshis / 100)) * 1_000);

    private static ulong ReserveMsatOn(ulong announcedSatoshis, ChannelFunding? funding) =>
        checked((funding is null || funding.Kind == ChannelFundingKind.Initial
                     ? announcedSatoshis
                     : Math.Max(announcedSatoshis, funding.CapacitySatoshis / 100)) * 1_000);

    /// <summary>
    /// The engine parameters of an opened channel: funder, funding amount, anchors and both parties' announced limits,
    /// kept with the same direction rules as <see cref="ChannelParams"/> (<c>Local</c> is what we announced), and
    /// <see cref="ChannelParams.HasInferredParams"/> carried as <see cref="HasInferredLimits"/>.
    /// </summary>
    /// <param name="channel">A channel whose funding output is known.</param>
    /// <param name="maxDustHtlcExposureMsat">Our dust exposure policy; null disables the check.</param>
    /// <exception cref="InvalidOperationException">The channel has no funding output yet.</exception>
    public static CommitmentParams FromChannel(ChannelModel channel, ulong? maxDustHtlcExposureMsat = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var fundingOutput = channel.FundingOutput
                         ?? throw new InvalidOperationException("The channel has no funding output yet");

        return new CommitmentParams(channel.IsInitiator, (ulong)fundingOutput.Amount.Satoshi,
                                    channel.ChannelParams.OptionAnchorOutputs, ToParty(channel.ChannelParams.Local),
                                    ToParty(channel.ChannelParams.Remote), maxDustHtlcExposureMsat,
                                    channel.ChannelParams.HasInferredParams,
                                    ChannelFunding.FromFundingOutput(fundingOutput));
    }

    private static CommitmentParty ToParty(ChannelParty party) =>
        new((ulong)party.DustLimitAmount.Satoshi, (ulong)party.ChannelReserveAmount.Satoshi,
            party.HtlcMinimumAmount.MilliSatoshi, party.MaxAcceptedHtlcs, party.MaxHtlcValueInFlight.MilliSatoshi);
}