using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;

/// <summary>Request for RotateMacaroonRootKey (ClientCommand 58, NL-1389). It has no fields yet; keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class RotateMacaroonRootKeyIpcRequest
{
    public RotateMacaroonRootKeyClientRequest ToClientRequest() => new();
}