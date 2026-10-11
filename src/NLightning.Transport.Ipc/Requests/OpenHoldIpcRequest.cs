using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;

/// <summary>Request for OpenHold (ClientCommand 59, NL-1392). Keys are append-only.</summary>
[MessagePackObject]
public sealed class OpenHoldIpcRequest
{
    /// <summary>Lift the start hold; false only shows the hold.</summary>
    [Key(0)] public bool Release { get; init; }

    public OpenHoldClientRequest ToClientRequest() => new(Release);
}