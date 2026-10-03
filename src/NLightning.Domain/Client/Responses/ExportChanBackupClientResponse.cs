namespace NLightning.Domain.Client.Responses;

using Channels.ValueObjects;

/// <summary>
/// The encrypted static channel backup (<c>ClientCommand.ExportChanBackup</c>).
/// </summary>
/// <param name="Backup">The encrypted backup (the <c>channel.backup</c> file format).</param>
/// <param name="ChannelIds">The channels it holds.</param>
/// <param name="FilePath">The node's backup file, kept up to date on every channel open and close; null when none is
/// configured.</param>
public sealed record ExportChanBackupClientResponse(
    byte[] Backup,
    IReadOnlyList<ChannelId> ChannelIds,
    string? FilePath);