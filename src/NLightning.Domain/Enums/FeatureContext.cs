namespace NLightning.Domain.Enums;

/// <summary>
/// The contexts a feature bit may be presented in (the Context column of BOLT 9).
/// </summary>
/// <remarks>
/// <see href="https://github.com/lightning/bolts/blob/master/09-features.md">BOLT-9</see>
/// </remarks>
[Flags]
public enum FeatureContext
{
    None = 0,

    /// <summary>
    /// I: presented in the <c>init</c> message.
    /// </summary>
    Init = 1 << 0,

    /// <summary>
    /// N: presented in <c>node_announcement</c> messages.
    /// </summary>
    NodeAnnouncement = 1 << 1,

    /// <summary>
    /// C: presented in the <c>channel_announcement</c> message.
    /// </summary>
    ChannelAnnouncement = 1 << 2,

    /// <summary>
    /// 9: presented in BOLT 11 invoices.
    /// </summary>
    Invoice = 1 << 3,

    /// <summary>
    /// B: presented in the <c>allowed_features</c> field of a blinded path.
    /// </summary>
    BlindedPath = 1 << 4,

    /// <summary>
    /// T: used in the <c>channel_type</c> field when opening channels.
    /// </summary>
    ChannelType = 1 << 5
}