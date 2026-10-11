using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Channels.ValueObjects;
using Domain.Client.Requests;

/// <summary>
/// Request for ExportChanBackup (ClientCommand 21). Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class ExportChanBackupIpcRequest
{
    /// <summary>Only this channel, when set.</summary>
    [Key(0)] public ChannelId? ChannelId { get; init; }

    public ExportChanBackupClientRequest ToClientRequest() => new() { ChannelId = ChannelId };
}