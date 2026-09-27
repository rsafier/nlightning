namespace NLightning.Domain.Client.Responses;

using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// What <c>restorechanbackup</c> did (<c>ClientCommand.RestoreChanBackup</c>).
/// </summary>
/// <param name="CreatedAt">When the backup was made.</param>
/// <param name="Channels">One result per backed-up channel.</param>
/// <param name="Peers">One connection result per peer of a restored channel.</param>
public sealed record RestoreChanBackupClientResponse(
    DateTimeOffset CreatedAt,
    IReadOnlyList<ChanRestoreChannelInfo> Channels,
    IReadOnlyList<ChanRestorePeerInfo> Peers);

/// <summary>The restore of one backed-up channel.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="RemoteNodeId">The peer.</param>
/// <param name="CapacitySat">The capacity.</param>
/// <param name="OptionAnchors">Whether it is an <c>option_anchors</c> channel.</param>
/// <param name="Outcome">What was done: <c>Restore</c>, <c>AlreadyExists</c>, <c>KeysMismatch</c>, <c>Duplicate</c>
/// or <c>Failed</c>.</param>
/// <param name="Detail">Why, or what happens next.</param>
public sealed record ChanRestoreChannelInfo(
    ChannelId ChannelId,
    CompactPubKey RemoteNodeId,
    ulong CapacitySat,
    bool OptionAnchors,
    string Outcome,
    string Detail);

/// <summary>The connection to one peer after the restore.</summary>
/// <param name="NodeId">The peer.</param>
/// <param name="Address">The address tried (<c>host:port</c>), if any.</param>
/// <param name="Connected">Whether it is connected (the force close was asked for).</param>
/// <param name="Error">Why it is not.</param>
public sealed record ChanRestorePeerInfo(CompactPubKey NodeId, string? Address, bool Connected, string? Error);