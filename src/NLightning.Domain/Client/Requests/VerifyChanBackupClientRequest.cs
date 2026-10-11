namespace NLightning.Domain.Client.Requests;

/// <summary>
/// Checks a static channel backup against the node's key, chain and key derivation
/// (<c>ClientCommand.VerifyChanBackup</c>).
/// </summary>
public sealed class VerifyChanBackupClientRequest
{
    /// <summary>The encrypted backup, as <c>exportchanbackup</c> or the <c>channel.backup</c> file holds it.</summary>
    public required byte[] Backup { get; init; }
}