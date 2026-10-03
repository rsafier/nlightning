namespace NLightning.Domain.Channels.ValueObjects;

using Bitcoin.Transactions.Enums;
using Bitcoin.Transactions.Extensions;
using Domain.Enums;
using Money;
using Node;

/// <summary>
/// The parameters of a channel: what each party announced, plus the values both parties share.
/// </summary>
/// <remarks>
/// Direction rules (BOLT 2): <see cref="Local"/> holds the values <b>we</b> sent and <see cref="Remote"/> the values
/// the peer sent. So <c>Local.HtlcMinimumAmount</c>, <c>Local.MaxAcceptedHtlcs</c> and
/// <c>Local.MaxHtlcValueInFlight</c> bind the HTLCs the peer offers us, and the <c>Remote</c> ones bind our offers.
/// <c>Local.ChannelReserveAmount</c> is what the peer must keep; <c>Remote.ChannelReserveAmount</c> is what we must
/// keep. Our commitment uses <c>Local.DustLimitAmount</c> and <c>Remote.ToSelfDelay</c>; the peer's commitment uses
/// <c>Remote.DustLimitAmount</c> and <c>Local.ToSelfDelay</c>.
/// </remarks>
public readonly record struct ChannelParams
{
    /// <summary>The parameters we announced.</summary>
    public ChannelParty Local { get; }

    /// <summary>The parameters the peer announced.</summary>
    public ChannelParty Remote { get; }

    /// <summary>The commitment feerate.</summary>
    public LightningMoney FeeRateAmountPerKw { get; }

    /// <summary>The funding depth the accepter asked for.</summary>
    public uint MinimumDepth { get; }

    private readonly bool _optionAnchorOutputs;

    /// <summary>
    /// Whether the commitment has the anchors semantics (BOLT 3): <c>option_anchors</c> in the channel type, and always
    /// for an <see cref="OptionSimpleTaproot"/> channel, which keeps them (two 330 sat anchors, zero-fee HTLC
    /// transactions with <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> peer signatures, the 1-CSV to_remote; NL-904 item
    /// 2). Where the scripts or weights matter, read <see cref="CommitmentFormat"/> instead.
    /// </summary>
    public bool OptionAnchorOutputs => _optionAnchorOutputs || OptionSimpleTaproot;

    /// <summary>
    /// Whether the channel type is <c>option_simple_taproot</c> (bolt-simple-taproot.md, NL-877 T3): a MuSig2 funding
    /// output and P2TR commitment outputs. Such a channel also has <see cref="OptionAnchorOutputs"/>.
    /// </summary>
    public bool OptionSimpleTaproot { get; init; }

    /// <summary>
    /// The BOLT 3 commitment format of the channel type: the one place the commitment factory reads it from.
    /// <see cref="Bitcoin.Transactions.Enums.CommitmentFormat.SimpleTaproot"/> for an <see cref="OptionSimpleTaproot"/>
    /// channel, else <see cref="Bitcoin.Transactions.Enums.CommitmentFormat.Anchors"/> or
    /// <see cref="Bitcoin.Transactions.Enums.CommitmentFormat.StaticRemoteKey"/>.
    /// </summary>
    public CommitmentFormat CommitmentFormat => OptionSimpleTaproot
                                                    ? CommitmentFormat.SimpleTaproot
                                                    : CommitmentFormatExtensions.FromOptionAnchors(OptionAnchorOutputs);

    /// <summary>Whether <c>option_scid_alias</c> is in the channel type (Compulsory) or only negotiated (Optional).</summary>
    public FeatureSupport UseScidAlias { get; }

    /// <summary>
    /// True for channels opened before the parameters were split per side (NL-194): migration
    /// <c>SplitChannelParams</c> copied the one stored set into both <see cref="Local"/> and <see cref="Remote"/>, so
    /// some values are guesses. As non-initiator we announced the node's htlc_minimum_msat, not the stored (opener's)
    /// one; as initiator the peer's accept_channel limits (htlc_minimum_msat, max_accepted_htlcs,
    /// max_htlc_value_in_flight_msat, channel_reserve_satoshis) were never stored, and our own open_channel may have
    /// used node defaults instead of the stored values. Don't enforce these limits as protocol rules for such a
    /// channel (e.g. failing it over an HTLC below <c>Local.HtlcMinimumAmount</c>).
    /// </summary>
    public bool HasInferredParams { get; init; }

    /// <summary>
    /// The <c>announce_channel</c> bit of <c>open_channel.channel_flags</c> (BOLT 2): the opener asked for a public
    /// channel, announced with <c>announcement_signatures</c> and <c>channel_announcement</c> (BOLT 7) once it is deep
    /// enough. False for private channels and for channels stored before migration <c>AddGossipGraph</c>.
    /// </summary>
    public bool AnnounceChannel { get; init; }

    public ChannelParams(ChannelParty local, ChannelParty remote, LightningMoney feeRateAmountPerKw, uint minimumDepth,
                         bool optionAnchorOutputs, FeatureSupport useScidAlias)
    {
        Local = local;
        Remote = remote;
        FeeRateAmountPerKw = feeRateAmountPerKw;
        MinimumDepth = minimumDepth;
        _optionAnchorOutputs = optionAnchorOutputs;
        UseScidAlias = useScidAlias;
    }

    /// <summary>These parameters with <paramref name="local"/> as ours.</summary>
    public ChannelParams WithLocal(ChannelParty local) =>
        new(local, Remote, FeeRateAmountPerKw, MinimumDepth, OptionAnchorOutputs, UseScidAlias)
        {
            HasInferredParams = HasInferredParams,
            AnnounceChannel = AnnounceChannel,
            OptionSimpleTaproot = OptionSimpleTaproot
        };

    /// <summary>
    /// Returns a copy with the peer's parameters replaced (the initiator learns them from <c>accept_channel</c>).
    /// </summary>
    public ChannelParams WithRemote(ChannelParty remote) =>
        new(Local, remote, FeeRateAmountPerKw, MinimumDepth, OptionAnchorOutputs, UseScidAlias)
        {
            HasInferredParams = HasInferredParams,
            AnnounceChannel = AnnounceChannel,
            OptionSimpleTaproot = OptionSimpleTaproot
        };

    /// <summary>
    /// The <c>channel_type</c> these parameters describe: <c>option_static_remotekey</c>, plus <c>option_anchors</c>,
    /// <c>option_scid_alias</c> (only when it is part of the type) and <c>option_zeroconf</c> (minimum depth 0). A simple
    /// taproot channel is bit 80 without <c>option_static_remotekey</c> and <c>option_anchors</c> (bolt-simple-taproot.md;
    /// LND accepts exactly {80}, {80, 46}, {80, 50} and {80, 46, 50}), plus the same scid_alias and zeroconf bits.
    /// </summary>
    public FeatureSet ToChannelType()
    {
        FeatureSet channelType;
        if (OptionSimpleTaproot)
        {
            // Raw bit 80 until lane WIRE's Feature.OptionSimpleTaproot (81) is merged; the integrator switches it
            channelType = FeatureSet.DeserializeFromBytes([]);
            channelType.SetFeature(TaprootChannelType.CompulsoryBit, true);
        }
        else
        {
            channelType = FeatureSet.NewBasicChannelType();
            if (OptionAnchorOutputs)
                channelType.SetFeature(Feature.OptionAnchors, true);
        }

        if (UseScidAlias == FeatureSupport.Compulsory)
            channelType.SetFeature(Feature.OptionScidAlias, true);

        // Set the bit directly: SetFeature(Feature) would also add option_scid_alias, a BOLT 9 dependency that is not
        // part of the channel type
        if (MinimumDepth == 0)
            channelType.SetFeature((int)Feature.OptionZeroconf - 1, true);

        return channelType;
    }
}