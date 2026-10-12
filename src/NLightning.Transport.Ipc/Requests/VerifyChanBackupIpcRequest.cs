using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;

/// <summary>
/// Request for VerifyChanBackup (ClientCommand 22). Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class VerifyChanBackupIpcRequest
{
    /// <summary>The encrypted backup.</summary>
    [Key(0)] public required byte[] Backup { get; init; }

    public VerifyChanBackupClientRequest ToClientRequest() => new() { Backup = Backup };
}