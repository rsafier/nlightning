namespace NLightning.Application.Channels.Backup.Models;

/// <summary>An exported static channel backup.</summary>
/// <param name="Snapshot">What it holds.</param>
/// <param name="Backup">The same, encrypted to the node key (the <c>channel.backup</c> file format).</param>
public sealed record ChannelBackupExport(ChannelBackupSnapshot Snapshot, byte[] Backup);