namespace NLightning.Application.Channels.Backup.Models;

using Domain.Channels.Enums;

/// <summary>
/// What <c>verifychanbackup</c> found in a backup.
/// </summary>
/// <param name="IsValid">True when the backup decrypts with this node's key, is for this node and chain, and every
/// channel's key index derives the keys it recorded.</param>
/// <param name="Error">Why the backup is not valid, when it is not.</param>
/// <param name="Snapshot">The decrypted backup, when it decrypted.</param>
/// <param name="Channels">One check per backed-up channel (empty when it did not decrypt).</param>
public sealed record ChannelBackupVerification(
    bool IsValid,
    string? Error,
    ChannelBackupSnapshot? Snapshot,
    IReadOnlyList<ChannelBackupEntryCheck> Channels);

/// <summary>The check of one backed-up channel.</summary>
/// <param name="Entry">The channel as backed up.</param>
/// <param name="KeysMatch">Whether our key index re-derives the recorded funding key and payment basepoint.</param>
/// <param name="LocalState">The channel's state in this node's database, when it is there.</param>
public sealed record ChannelBackupEntryCheck(ChannelBackupEntry Entry, bool KeysMatch, ChannelState? LocalState);

/// <summary>What a write of the backup file did.</summary>
public enum ChannelBackupWriteOutcome
{
    /// <summary>The file was written.</summary>
    Written,

    /// <summary>The file already held the same channels; nothing written.</summary>
    Unchanged,

    /// <summary>No file is configured, or the automatic backup is off.</summary>
    Disabled,

    /// <summary>
    /// Refused: the database holds no channel at all while the file holds channels (a wiped or new database). The file
    /// is kept for <c>restorechanbackup</c>.
    /// </summary>
    KeptExisting
}

/// <summary>The result of a write of the backup file.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ChannelCount">The channels in the file after the call (the kept file's count for
/// <see cref="ChannelBackupWriteOutcome.KeptExisting"/>).</param>
/// <param name="FilePath">The file, when one is configured.</param>
/// <param name="MovedAsidePath">Where an existing file that did not decrypt with our key was moved.</param>
public sealed record ChannelBackupWriteResult(
    ChannelBackupWriteOutcome Outcome,
    int ChannelCount,
    string? FilePath,
    string? MovedAsidePath = null);