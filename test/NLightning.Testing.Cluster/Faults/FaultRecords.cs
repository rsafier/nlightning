using System.Globalization;

namespace NLightning.Testing.Cluster.Faults;

/// <summary>The faults <see cref="FaultInjector"/> injects.</summary>
public enum FaultKind
{
    /// <summary>Graceful pod replacement: SIGTERM, the grace period, a new pod with the same name and PVC.</summary>
    Restart,

    /// <summary>Pod deleted with a 1 s grace period (see <see cref="FaultInjector.KillAsync"/>).</summary>
    Kill,

    /// <summary>SIGKILL to the node's processes: the container restarts in place, same pod, IP and PVC.</summary>
    Crash,

    /// <summary>
    /// The node stopped cleanly by its own command; the container restarts in place, same pod, IP and data (an
    /// <c>emptyDir</c> included). See <see cref="FaultInjector.RestartInPlaceAsync"/>.
    /// </summary>
    RestartInPlace,

    /// <summary>SIGSTOP to the node's processes.</summary>
    Pause,

    /// <summary>SIGCONT to the node's processes.</summary>
    Resume,

    /// <summary>A NetworkPolicy cutting nodes off.</summary>
    Partition,

    /// <summary>The partition's NetworkPolicy deleted.</summary>
    Heal
}

/// <summary>
/// One injected fault, for the run's timeline (plan R14: results say what was done to which node and when).
/// </summary>
/// <param name="StartedAt">When the fault started.</param>
/// <param name="Kind">What was done.</param>
/// <param name="Target">The node alias(es) or partition name.</param>
/// <param name="Duration">How long the call took (for restarts: until the node was ready again).</param>
/// <param name="Detail">A one-line description (pod UIDs, pids, policy name).</param>
public sealed record FaultEvent(DateTimeOffset StartedAt, FaultKind Kind, string Target, TimeSpan Duration,
                                string Detail)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
                      $"{StartedAt:HH:mm:ss.fff} {Kind} {Target} ({Duration.TotalSeconds:F1} s): {Detail}");
}

/// <summary>
/// What a restart, kill or crash did to a node's pod.
/// </summary>
/// <param name="Node">The node alias.</param>
/// <param name="Kind"><see cref="FaultKind.Restart"/>, <see cref="FaultKind.Kill"/>, <see cref="FaultKind.Crash"/> or
/// <see cref="FaultKind.RestartInPlace"/>.</param>
/// <param name="PodUidBefore">The pod UID before (null when no pod existed).</param>
/// <param name="PodUidAfter">The pod UID once ready again (the same as before after a crash).</param>
/// <param name="PodIpBefore">The pod IP before.</param>
/// <param name="PodIpAfter">The pod IP once ready again (it may change: peers must use the DNS names).</param>
/// <param name="RestartCountBefore">The main container's restart count before.</param>
/// <param name="RestartCountAfter">The main container's restart count after (it grows only on a crash or a restart in
/// place).</param>
/// <param name="Duration">From the fault to the node being ready again.</param>
public sealed record NodeReplacement(string Node, FaultKind Kind, string? PodUidBefore, string? PodUidAfter,
                                     string? PodIpBefore, string? PodIpAfter, int RestartCountBefore,
                                     int RestartCountAfter, TimeSpan Duration)
{
    /// <summary>Whether the pod object was replaced (restart, kill) rather than restarted in place (crash).</summary>
    public bool NewPod => PodUidBefore != PodUidAfter;
}

/// <summary>
/// A paused node: the processes that got SIGSTOP.
/// </summary>
/// <param name="Node">The node alias.</param>
/// <param name="StoppedPids">The pids that were stopped (and that <see cref="FaultInjector.ResumeAsync"/> continues).</param>
/// <param name="Duration">How long the pause took until every process was stopped.</param>
public sealed record PausedNode(string Node, IReadOnlyList<int> StoppedPids, TimeSpan Duration);

/// <summary>
/// A partition in force: its NetworkPolicy and the two sides.
/// </summary>
/// <param name="Name">The NetworkPolicy's name in the run's namespace.</param>
/// <param name="Isolated">The selected side's aliases.</param>
/// <param name="Others">The other side's aliases, or null when the selected side is isolated from every pod (or, for
/// <see cref="PartitionShape.Outside"/> and <see cref="PartitionShape.Ports"/>, has no other side).</param>
/// <param name="Shape">What the policy cuts (<see cref="PartitionPolicy"/>).</param>
/// <param name="OpenPorts">The ports still open, for <see cref="PartitionShape.Ports"/>.</param>
public sealed record NetworkPartition(string Name, IReadOnlyList<string> Isolated, IReadOnlyList<string>? Others,
                                      PartitionShape Shape = PartitionShape.Isolate,
                                      IReadOnlyList<int>? OpenPorts = null)
{
    public override string ToString() =>
        Shape switch
        {
            PartitionShape.Outside => $"{Name}: [{string.Join(',', Isolated)}] | outside the run (host)",
            PartitionShape.Ports =>
                $"{Name}: [{string.Join(',', Isolated)}] open only on ports {string.Join(',', OpenPorts ?? [])}",
            _ => $"{Name}: [{string.Join(',', Isolated)}] | [{(Others is null ? "*" : string.Join(',', Others))}]"
        };
}

/// <summary>The shapes of a <see cref="NetworkPartition"/> (<see cref="PartitionPolicy"/>).</summary>
public enum PartitionShape
{
    /// <summary>The selected nodes reach only each other (and DNS).</summary>
    Isolate,

    /// <summary>The selected nodes and the other side cannot reach each other.</summary>
    Split,

    /// <summary>The selected nodes keep the run's pods and lose the host (the test process, our in-process nodes).</summary>
    Outside,

    /// <summary>New connections into the selected nodes reach only some ports.</summary>
    Ports
}

/// <summary>
/// The fault cannot be injected into this node as it is deployed (e.g. a pause of a node whose process is PID 1).
/// </summary>
public sealed class FaultNotSupportedException(string message) : Exception(message);