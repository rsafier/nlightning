namespace NLightning.Domain.Client.Requests;

/// <summary>
/// Restores a static channel backup of this node (<c>ClientCommand.RestoreChanBackup</c>): every backed-up channel
/// not in the database becomes a recovery channel whose peer is asked to force close.
/// </summary>
public sealed class RestoreChanBackupClientRequest
{
    /// <summary>The encrypted backup, as <c>exportchanbackup</c> or the <c>channel.backup</c> file holds it.</summary>
    public required byte[] Backup { get; init; }
}