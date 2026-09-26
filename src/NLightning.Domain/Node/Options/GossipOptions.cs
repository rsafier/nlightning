namespace NLightning.Domain.Node.Options;

using Protocol.ValueObjects;

/// <summary>
/// BOLT 7 options of the node's own public channels (configuration section <c>Gossip</c>, BOLT 7 plan §3.2 and D12).
/// </summary>
public sealed class GossipOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Gossip";

    /// <summary>
    /// The depth BOLT 7 requires before a channel is announced: <c>announcement_signatures</c> and
    /// <c>channel_announcement</c> are only sent, and a <c>channel_announcement</c> is only accepted, at 6
    /// confirmations.
    /// </summary>
    public const uint MinimumAnnouncementDepth = 6;

    /// <summary>
    /// Whether a peer may open a public channel to us (<c>announce_channel</c> set in <c>open_channel</c>). When false
    /// such an <c>open_channel</c> is refused with an <c>error</c> (BOLT 2 lets the receiver fail a channel whose
    /// <c>announce_channel</c> it does not want). Default true.
    /// </summary>
    public bool AcceptPublicChannels { get; set; } = true;

    /// <summary>
    /// Whether <c>openchannel --public</c> is allowed on mainnet. Public channels stay off on mainnet until the BOLT 7
    /// plan's Proof G1 passed (D12). Default false.
    /// </summary>
    public bool AllowPublicChannelsOnMainnet { get; set; }

    /// <summary>
    /// The confirmations of the funding transaction before our <c>announcement_signatures</c> go out. Only regtest may
    /// use less than <see cref="MinimumAnnouncementDepth"/>; elsewhere a lower value is raised to it.
    /// </summary>
    public uint AnnouncementDepth { get; set; } = MinimumAnnouncementDepth;

    /// <summary>
    /// The depth in effect on <paramref name="network"/>: <see cref="AnnouncementDepth"/> (at least 1) on regtest,
    /// else at least <see cref="MinimumAnnouncementDepth"/>.
    /// </summary>
    public uint GetAnnouncementDepth(BitcoinNetwork network) =>
        network == BitcoinNetwork.Regtest
            ? Math.Max(1U, AnnouncementDepth)
            : Math.Max(MinimumAnnouncementDepth, AnnouncementDepth);
}