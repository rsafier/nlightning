namespace NLightning.Application.Channels.Backup.Models;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.ValueObjects;

/// <summary>
/// The decrypted content of a static channel backup: which node and chain it belongs to, when it was made and its
/// channels.
/// </summary>
/// <param name="ChainHash">The chain the channels live on.</param>
/// <param name="NodeId">The node that made the backup (the node key the file is encrypted to).</param>
/// <param name="CreatedAt">When the backup was made (whole seconds).</param>
/// <param name="Channels">The backed-up channels.</param>
public sealed record ChannelBackupSnapshot(
    ChainHash ChainHash,
    CompactPubKey NodeId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ChannelBackupEntry> Channels);