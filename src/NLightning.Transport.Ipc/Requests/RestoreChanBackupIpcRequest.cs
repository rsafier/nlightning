using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;

/// <summary>
/// Request for RestoreChanBackup (ClientCommand 23). Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class RestoreChanBackupIpcRequest
{
    /// <summary>The encrypted backup.</summary>
    [Key(0)] public required byte[] Backup { get; init; }

    public RestoreChanBackupClientRequest ToClientRequest() => new() { Backup = Backup };
}