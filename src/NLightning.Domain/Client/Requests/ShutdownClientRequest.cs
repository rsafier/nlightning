namespace NLightning.Domain.Client.Requests;

/// <summary>
/// Stops the node gracefully (<c>ClientCommand.Shutdown</c>, NL-591/NL-592).
/// </summary>
/// <remarks>
/// <para>The first pass (NL-591): refused while a channel has HTLCs in flight; otherwise the node refuses new
/// activity from then on (<see cref="Node.Constants.NodeDrain"/>) and the daemon stops once the answer is sent.</para>
/// <para>The second pass (NL-592) adds <see cref="Wait"/> (drain until the node is idle, at most
/// <see cref="TimeoutSeconds"/>) and <see cref="Force"/> (stop although something is in flight). A request without
/// them (an older client) keeps the first-pass behavior.</para>
/// </remarks>
public sealed class ShutdownClientRequest
{
    /// <summary>Drains and waits until the node is idle instead of refusing while something is in flight.</summary>
    public bool Wait { get; init; }

    /// <summary>
    /// How long <see cref="Wait"/> waits at most, in seconds; 0 (an older client's unset value) means the server's
    /// default (<see cref="Constants.ShutdownDefaults.DefaultWaitTimeoutSeconds"/>).
    /// </summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>
    /// Stops the node although HTLCs or negotiations are in flight; with <see cref="Wait"/>, forces the stop on
    /// timeout. Never force-closes or broadcasts anything: the HTLC expiry monitor and the BOLT 5 resolvers run at
    /// the next start.
    /// </summary>
    public bool Force { get; init; }
}