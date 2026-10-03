namespace NLightning.Domain.Node;

using Bitcoin.Transactions.Enums;
using Bitcoin.Transactions.Extensions;

/// <summary>
/// The <c>option_simple_taproot</c> channel type (bolt-simple-taproot.md, NL-877 T3): feature bit 80 in
/// <c>channel_type</c>, without <c>option_static_remotekey</c> (12) and <c>option_anchors</c> (22), optionally with
/// <c>option_scid_alias</c> (46) and <c>option_zeroconf</c> (50). LND 0.21 accepts exactly {80}, {80, 46}, {80, 50} and
/// {80, 46, 50}.
/// </summary>
/// <remarks>
/// The bit is raw while the <c>Feature</c> enum has no <c>OptionSimpleTaproot</c> member yet (wave t02 lane WIRE adds
/// <c>OptionSimpleTaproot = 81</c>; the integrator switches these to it).
/// </remarks>
public static class TaprootChannelType
{
    /// <summary>The compulsory (even) bit of <c>option_simple_taproot</c>, the one a <c>channel_type</c> carries.</summary>
    public const int CompulsoryBit = 80;

    /// <summary>The compulsory bit of <c>option_static_remotekey</c>.</summary>
    private const int StaticRemoteKeyBit = 12;

    /// <summary>The compulsory bit of <c>option_anchors</c>.</summary>
    private const int AnchorsBit = 22;

    /// <summary>
    /// True when <paramref name="channelType"/> is a simple taproot channel type: bit 80 set and neither
    /// <c>option_static_remotekey</c> nor <c>option_anchors</c> (either bit of each). Other bits (scid_alias, zeroconf)
    /// are left to the channel type negotiation.
    /// </summary>
    public static bool IsTaprootChannelType(FeatureSet? channelType)
    {
        if (channelType is null)
            return false;

        var bits = channelType.GetSetBits();
        return bits.Contains(CompulsoryBit)
            && !bits.Contains(StaticRemoteKeyBit) && !bits.Contains(StaticRemoteKeyBit + 1)
            && !bits.Contains(AnchorsBit) && !bits.Contains(AnchorsBit + 1);
    }

    /// <summary>
    /// The commitment format of a channel opened with <paramref name="channelType"/>:
    /// <see cref="CommitmentFormat.SimpleTaproot"/> for a taproot type (<see cref="IsTaprootChannelType"/>), else
    /// <see cref="CommitmentFormat.Anchors"/> or <see cref="CommitmentFormat.StaticRemoteKey"/> from
    /// <paramref name="hasAnchors"/>.
    /// </summary>
    public static CommitmentFormat GetCommitmentFormat(FeatureSet? channelType, bool hasAnchors) =>
        IsTaprootChannelType(channelType)
            ? CommitmentFormat.SimpleTaproot
            : CommitmentFormatExtensions.FromOptionAnchors(hasAnchors);
}