using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;

/// <summary>
/// Request for ChainStatus (ClientCommand 16, NL-216). It has no fields yet; keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class ChainStatusIpcRequest
{
    public ChainStatusClientRequest ToClientRequest() => new();
}