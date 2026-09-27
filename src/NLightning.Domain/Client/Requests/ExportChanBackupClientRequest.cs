namespace NLightning.Domain.Client.Requests;

using Channels.ValueObjects;

/// <summary>
/// Exports the static channel backup of the node, encrypted to its key (<c>ClientCommand.ExportChanBackup</c>).
/// </summary>
public sealed class ExportChanBackupClientRequest
{
    /// <summary>Only this channel, when set; otherwise every backed-up channel.</summary>
    public ChannelId? ChannelId { get; init; }
}