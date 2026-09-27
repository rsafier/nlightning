namespace NLightning.Application.Channels.Backup.Interfaces;

using Domain.Channels.ValueObjects;
using Models;

/// <summary>
/// Static channel backups (SCB), like LND's <c>exportchanbackup</c>/<c>verifychanbackup</c>: what a node with the
/// same key file needs to have its peers force close the channels after losing its database, encrypted to the node
/// key. See <see cref="ChannelBackupCodec"/> and <see cref="ChannelBackupCipher"/> for the format.
/// </summary>
public interface IChannelBackupService
{
    /// <summary>
    /// The backup of every channel whose funding outpoint is known and that is not Closed/Stale, read from the
    /// database, or only <paramref name="channelId"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="channelId"/> is set and not backed up (unknown, closed,
    /// or before its funding outpoint).</exception>
    Task<ChannelBackupSnapshot> CreateSnapshotAsync(ChannelId? channelId, CancellationToken cancellationToken);

    /// <summary><see cref="CreateSnapshotAsync"/> and the same snapshot encrypted to the node key.</summary>
    /// <exception cref="KeyNotFoundException">As <see cref="CreateSnapshotAsync"/>.</exception>
    Task<ChannelBackupExport> ExportAsync(ChannelId? channelId, CancellationToken cancellationToken);

    /// <summary>Decrypts and decodes a backup with the node key.</summary>
    /// <exception cref="ChannelBackupException">Not a backup of this node's key, tampered with, or malformed.
    /// </exception>
    ChannelBackupSnapshot Decrypt(ReadOnlySpan<byte> backup);

    /// <summary>Checks a backup (never throws for a bad one).</summary>
    Task<ChannelBackupVerification> VerifyAsync(ReadOnlyMemory<byte> backup, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the backup file (<see cref="ChannelBackupOptions.FilePath"/>) atomically when its channels changed.
    /// </summary>
    Task<ChannelBackupWriteResult> WriteFileAsync(CancellationToken cancellationToken);
}