namespace NLightning.Domain.Node.Options;

/// <summary>
/// The node's shutdown behavior beyond the IPC command (NL-592).
/// </summary>
public sealed class ShutdownOptions
{
    /// <summary>
    /// How long a stop by signal (SIGTERM, e.g. <c>nltg daemon --stop</c> or systemd) drains before it stops the
    /// node: it refuses new activity (NL-591's drain) and waits up to this many seconds for the HTLCs and
    /// negotiations in flight to resolve, then stops whatever the state. <c>shutdown --wait</c> is the operator's
    /// longer form of this. Default 0 = off (stop as before); the IPC command's
    /// <see cref="Client.Constants.ShutdownDefaults.DefaultWaitTimeoutSeconds"/> stays the default there.
    /// </summary>
    public int DrainOnSignalSeconds { get; set; }
}