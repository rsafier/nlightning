using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

/// <summary>Response for OpenHold (ClientCommand 59, NL-1392).</summary>
[MessagePackObject]
public sealed class OpenHoldIpcResponse
{
    [Key(0)] public bool Held { get; init; }
    [Key(1)] public string? Reason { get; init; }
    [Key(2)] public bool AwaitingRestore { get; init; }
    [Key(3)] public bool RestoreInProgress { get; init; }
    [Key(4)] public bool Released { get; init; }

    public static OpenHoldIpcResponse FromClientResponse(OpenHoldClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new OpenHoldIpcResponse
        {
            Held = response.Held,
            Reason = response.Reason,
            AwaitingRestore = response.AwaitingRestore,
            RestoreInProgress = response.RestoreInProgress,
            Released = response.Released
        };
    }
}