using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Constants;
using Domain.Client.Requests;

/// <summary>
/// Request for Shutdown (ClientCommand 39, NL-591/NL-592). Keys are append-only: an older client sends none and gets
/// the first-pass behavior (refuse while HTLCs are in flight, else stop).
/// </summary>
[MessagePackObject]
public sealed class ShutdownIpcRequest
{
    /// <summary>Drain and wait until the node is idle (NL-592) instead of refusing while something is in flight.</summary>
    [Key(0)] public bool Wait { get; init; }

    /// <summary>
    /// How long <see cref="Wait"/> waits at most, in seconds; 0 (an older client's unset value) means
    /// <see cref="ShutdownDefaults.DefaultWaitTimeoutSeconds"/>.
    /// </summary>
    [Key(1)] public int TimeoutSeconds { get; init; }

    /// <summary>Stop although HTLCs or negotiations are in flight; with <see cref="Wait"/>, force on timeout (NL-592).</summary>
    [Key(2)] public bool Force { get; init; }

    public ShutdownClientRequest ToClientRequest() =>
        new() { Wait = Wait, TimeoutSeconds = TimeoutSeconds, Force = Force };
}