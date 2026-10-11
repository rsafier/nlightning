using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.ValueObjects;
using Domain.Client.Responses;

/// <summary>
/// Response for ExportChanBackup (ClientCommand 21). Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class ExportChanBackupIpcResponse
{
    /// <summary>The encrypted backup (the <c>channel.backup</c> file format).</summary>
    [Key(0)] public required byte[] Backup { get; init; }

    /// <summary>The channels it holds.</summary>
    [Key(1)] public required List<ChannelId> ChannelIds { get; init; }

    /// <summary>The node's backup file, when one is configured.</summary>
    [Key(2)] public string? FilePath { get; init; }

    /// <summary>The channels it holds that are simple taproot channels (NL-877 T5).</summary>
    [Key(3)] public List<ChannelId>? SimpleTaprootChannelIds { get; init; }

    public static ExportChanBackupIpcResponse FromClientResponse(ExportChanBackupClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new ExportChanBackupIpcResponse
        {
            Backup = clientResponse.Backup,
            ChannelIds = clientResponse.ChannelIds.ToList(),
            FilePath = clientResponse.FilePath,
            SimpleTaprootChannelIds = clientResponse.SimpleTaprootChannelIds?.ToList()
        };
    }
}