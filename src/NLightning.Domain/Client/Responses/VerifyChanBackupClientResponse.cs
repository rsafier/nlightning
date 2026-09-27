namespace NLightning.Domain.Client.Responses;

using Bitcoin.ValueObjects;
using Channels.Enums;
using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// What the node found in a static channel backup (<c>ClientCommand.VerifyChanBackup</c>).
/// </summary>
/// <param name="IsValid">True when it decrypts with the node's key, is for this node and chain, and every channel's
/// key index derives the keys it recorded.</param>
/// <param name="Error">Why it is not valid.</param>
/// <param name="CreatedAt">When the backup was made, when it decrypted.</param>
/// <param name="Channels">Its channels, when it decrypted.</param>
public sealed record VerifyChanBackupClientResponse(
    bool IsValid,
    string? Error,
    DateTimeOffset? CreatedAt,
    IReadOnlyList<ChanBackupChannelInfo> Channels);

/// <summary>One channel of a static channel backup.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="RemoteNodeId">The peer.</param>
/// <param name="Addresses">The peer's last known addresses (<c>host:port</c>).</param>
/// <param name="FundingTxId">The funding transaction (internal byte order).</param>
/// <param name="FundingOutputIndex">The funding output index.</param>
/// <param name="CapacitySat">The capacity.</param>
/// <param name="ShortChannelId">The short channel id, once the funding confirmed.</param>
/// <param name="IsInitiator">Whether we funded it.</param>
/// <param name="OptionAnchors">Whether it is an <c>option_anchors</c> channel.</param>
/// <param name="KeysMatch">Whether our key index re-derives the recorded keys.</param>
/// <param name="LocalState">The channel's state in this node's database, when it is there.</param>
public sealed record ChanBackupChannelInfo(
    ChannelId ChannelId,
    CompactPubKey RemoteNodeId,
    IReadOnlyList<string> Addresses,
    TxId FundingTxId,
    ushort FundingOutputIndex,
    ulong CapacitySat,
    ShortChannelId? ShortChannelId,
    bool IsInitiator,
    bool OptionAnchors,
    bool KeysMatch,
    ChannelState? LocalState);