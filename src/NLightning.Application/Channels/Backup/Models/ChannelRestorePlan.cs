namespace NLightning.Application.Channels.Backup.Models;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;

/// <summary>What <c>restorechanbackup</c> does, or did, with one backed-up channel.</summary>
public enum ChannelRestoreAction
{
    /// <summary>A recovery channel is made: the peer is asked to force close and our output is swept.</summary>
    Restore,

    /// <summary>Skipped: the channel is already in the database (its state is kept as it is).</summary>
    AlreadyExists,

    /// <summary>Skipped: its key index does not derive the keys it recorded (another key file).</summary>
    KeysMismatch,

    /// <summary>Skipped: the same channel id appears twice in the backup (the first one is used).</summary>
    Duplicate,

    /// <summary>The recovery channel could not be stored or registered (see the detail).</summary>
    Failed
}

/// <summary>The plan for one backed-up channel.</summary>
/// <param name="Entry">The channel as backed up.</param>
/// <param name="Action">What is done with it.</param>
/// <param name="ExistingState">Its state in the database, for <see cref="ChannelRestoreAction.AlreadyExists"/> (null
/// when the row exists but can't be read).</param>
/// <param name="LocalBasepoints">Our re-derived basepoints, for <see cref="ChannelRestoreAction.Restore"/>.</param>
/// <param name="LocalFundingPubKey">Our re-derived funding key of the entry's current funding (at
/// <see cref="ChannelBackupEntry.LocalFundingKeyIndex"/>), for <see cref="ChannelRestoreAction.Restore"/>.</param>
public sealed record ChannelRestorePlanItem(
    ChannelBackupEntry Entry,
    ChannelRestoreAction Action,
    ChannelState? ExistingState = null,
    ChannelBasepoints? LocalBasepoints = null,
    Domain.Crypto.ValueObjects.CompactPubKey? LocalFundingPubKey = null);

/// <summary>The outcome of <c>restorechanbackup</c> for one channel.</summary>
/// <param name="Entry">The channel as backed up.</param>
/// <param name="Action">What was done.</param>
/// <param name="Detail">Why it was skipped or failed, or what happens next.</param>
public sealed record ChannelRestoreChannelResult(ChannelBackupEntry Entry, ChannelRestoreAction Action, string Detail);

/// <summary>The connection attempt to one peer of the restored channels.</summary>
/// <param name="NodeId">The peer.</param>
/// <param name="Address">The address tried (<c>host:port</c>), or null when the backup had none.</param>
/// <param name="Connected">True when the peer is connected after the restore (its channels got the data-loss
/// <c>channel_reestablish</c>).</param>
/// <param name="Error">Why the connection failed.</param>
public sealed record ChannelRestorePeerResult(
    Domain.Crypto.ValueObjects.CompactPubKey NodeId,
    string? Address,
    bool Connected,
    string? Error);

/// <summary>The result of <c>restorechanbackup</c>.</summary>
/// <param name="Snapshot">The decrypted backup.</param>
/// <param name="Channels">One result per backed-up channel, in backup order.</param>
/// <param name="Peers">One result per peer of a restored channel.</param>
public sealed record ChannelRestoreResult(
    ChannelBackupSnapshot Snapshot,
    IReadOnlyList<ChannelRestoreChannelResult> Channels,
    IReadOnlyList<ChannelRestorePeerResult> Peers)
{
    /// <summary>How many recovery channels were made.</summary>
    public int RestoredCount => Channels.Count(c => c.Action == ChannelRestoreAction.Restore);
}