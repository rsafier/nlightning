using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.ValueObjects;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Response for RestoreChanBackup (ClientCommand 23). Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class RestoreChanBackupIpcResponse
{
    /// <summary>When the backup was made (UNIX seconds).</summary>
    [Key(0)] public long CreatedAt { get; init; }

    [Key(1)] public required List<ChanRestoreChannelIpcInfo> Channels { get; init; }
    [Key(2)] public required List<ChanRestorePeerIpcInfo> Peers { get; init; }

    public static RestoreChanBackupIpcResponse FromClientResponse(RestoreChanBackupClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new RestoreChanBackupIpcResponse
        {
            CreatedAt = clientResponse.CreatedAt.ToUnixTimeSeconds(),
            Channels = clientResponse.Channels.Select(c => new ChanRestoreChannelIpcInfo
            {
                ChannelId = c.ChannelId,
                RemoteNodeId = c.RemoteNodeId,
                CapacitySat = c.CapacitySat,
                OptionAnchors = c.OptionAnchors,
                OptionSimpleTaproot = c.OptionSimpleTaproot,
                Outcome = c.Outcome,
                Detail = c.Detail
            }).ToList(),
            Peers = clientResponse.Peers.Select(p => new ChanRestorePeerIpcInfo
            {
                NodeId = p.NodeId,
                Address = p.Address,
                Connected = p.Connected,
                Error = p.Error
            }).ToList()
        };
    }
}

/// <summary>One channel of a <see cref="RestoreChanBackupIpcResponse"/>.</summary>
[MessagePackObject]
public sealed class ChanRestoreChannelIpcInfo
{
    [Key(0)] public required ChannelId ChannelId { get; init; }
    [Key(1)] public required CompactPubKey RemoteNodeId { get; init; }
    [Key(2)] public ulong CapacitySat { get; init; }
    [Key(3)] public bool OptionAnchors { get; init; }
    [Key(4)] public required string Outcome { get; init; }
    [Key(5)] public required string Detail { get; init; }

    /// <summary>Whether it is a simple taproot channel (NL-877 T5).</summary>
    [Key(6)] public bool OptionSimpleTaproot { get; init; }
}

/// <summary>One peer of a <see cref="RestoreChanBackupIpcResponse"/>.</summary>
[MessagePackObject]
public sealed class ChanRestorePeerIpcInfo
{
    [Key(0)] public required CompactPubKey NodeId { get; init; }
    [Key(1)] public string? Address { get; init; }
    [Key(2)] public bool Connected { get; init; }
    [Key(3)] public string? Error { get; init; }
}