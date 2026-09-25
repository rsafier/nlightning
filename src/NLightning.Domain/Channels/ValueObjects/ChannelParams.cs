namespace NLightning.Domain.Channels.ValueObjects;

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

    /// <summary>Whether the channel type has <c>option_anchors</c>.</summary>
    public bool OptionAnchorOutputs { get; }

    /// <summary>Whether <c>option_scid_alias</c> is in the channel type (Compulsory) or only negotiated (Optional).</summary>
    public FeatureSupport UseScidAlias { get; }

    public ChannelParams(ChannelParty local, ChannelParty remote, LightningMoney feeRateAmountPerKw, uint minimumDepth,
                         bool optionAnchorOutputs, FeatureSupport useScidAlias)
    {
        Local = local;
        Remote = remote;
        FeeRateAmountPerKw = feeRateAmountPerKw;
        MinimumDepth = minimumDepth;
        OptionAnchorOutputs = optionAnchorOutputs;
        UseScidAlias = useScidAlias;
    }

    /// <summary>
    /// Returns a copy with the peer's parameters replaced (the initiator learns them from <c>accept_channel</c>).
    /// </summary>
    public ChannelParams WithRemote(ChannelParty remote) =>
        new(Local, remote, FeeRateAmountPerKw, MinimumDepth, OptionAnchorOutputs, UseScidAlias);

    /// <summary>
    /// The <c>channel_type</c> these parameters describe: <c>option_static_remotekey</c>, plus <c>option_anchors</c>,
    /// <c>option_scid_alias</c> (only when it is part of the type) and <c>option_zeroconf</c> (minimum depth 0).
    /// </summary>
    public FeatureSet ToChannelType()
    {
        var channelType = FeatureSet.NewBasicChannelType();
        if (OptionAnchorOutputs)
            channelType.SetFeature(Feature.OptionAnchors, true);

        if (UseScidAlias == FeatureSupport.Compulsory)
            channelType.SetFeature(Feature.OptionScidAlias, true);

        // Set the bit directly: SetFeature(Feature) would also add option_scid_alias, a BOLT 9 dependency that is not
        // part of the channel type
        if (MinimumDepth == 0)
            channelType.SetFeature((int)Feature.OptionZeroconf - 1, true);

        return channelType;
    }
}