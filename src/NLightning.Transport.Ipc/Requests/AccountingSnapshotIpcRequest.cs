using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;

/// <summary>
/// Request for AccountingSnapshot (ClientCommand 42, NL-602). It has no fields yet; keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingSnapshotIpcRequest
{
    public AccountingSnapshotClientRequest ToClientRequest() => new();
}