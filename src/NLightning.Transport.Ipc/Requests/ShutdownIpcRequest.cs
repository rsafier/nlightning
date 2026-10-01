using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;

/// <summary>
/// Request for Shutdown (ClientCommand 39, NL-591). It has no fields yet; keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class ShutdownIpcRequest
{
    public ShutdownClientRequest ToClientRequest() => new();
}