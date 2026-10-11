namespace NLightning.Testing.Cluster.Faults;

/// <summary>
/// One process of a node's main container as <c>/proc/&lt;pid&gt;/stat</c> shows it.
/// </summary>
/// <param name="Pid">The pid in the pod's process namespace.</param>
/// <param name="State">The kernel state letter (<c>R</c>, <c>S</c>, <c>D</c>, <c>T</c> stopped, <c>Z</c> zombie, ...).</param>
/// <param name="Name">The process name (<c>comm</c>).</param>
public sealed record ContainerProcess(int Pid, char State, string Name)
{
    /// <summary>Stopped by a signal (<c>T</c>) or by tracing (<c>t</c>).</summary>
    public bool IsStopped => State is 'T' or 't';

    /// <summary>Exited (a zombie not reaped yet, or dead).</summary>
    public bool HasExited => State is 'Z' or 'X' or 'x';
}

/// <summary>
/// The processes of a node's main container.
/// </summary>
/// <param name="MainProcessIsPid1">PID 1 of the pod is one of them, so it cannot be paused or crashed from inside
/// (see <see cref="ProcessFaultSupport"/>).</param>
/// <param name="Processes">The processes, the listing shell excluded.</param>
public sealed record ContainerProcessList(bool MainProcessIsPid1, IReadOnlyList<ContainerProcess> Processes)
{
    /// <summary>The process with <paramref name="pid"/>, or null when it is gone.</summary>
    public ContainerProcess? Find(int pid) => Processes.FirstOrDefault(p => p.Pid == pid);
}