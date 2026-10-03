using k8s.Models;

namespace NLightning.Testing.Cluster.Faults;

using Kube;

/// <summary>
/// Turns on the pod setting that <see cref="FaultInjector.PauseAsync"/> and <see cref="FaultInjector.CrashAsync"/>
/// need: a shared process namespace (<c>shareProcessNamespace: true</c>), so the node's process is not PID 1.
/// </summary>
/// <remarks>
/// Linux ignores SIGSTOP and SIGKILL sent to a PID namespace's init process from inside that namespace (measured on
/// OrbStack: <c>kill -STOP 1</c> returns 0 and the process stays in state <c>S</c>), and an exec runs inside the
/// container's namespace. With a shared process namespace the pod's <c>pause</c> process is PID 1 and the node's
/// process is an ordinary one: an exec can stop, continue and kill it, and nothing outside the pod is needed (no
/// privileged sidecar, no container runtime CLI). The node still receives SIGTERM on a graceful restart (the runtime
/// signals the container's own init process).
/// </remarks>
public static class ProcessFaultSupport
{
    /// <summary>
    /// Sets <c>shareProcessNamespace</c> on the workload's pod (after any <see cref="NodeWorkload.CustomizePod"/> hook
    /// already set, which still runs). Returns the workload.
    /// </summary>
    public static NodeWorkload WithProcessFaults(this NodeWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(workload);

        var previous = workload.CustomizePod;
        workload.CustomizePod = spec =>
        {
            previous?.Invoke(spec);
            spec.ShareProcessNamespace = true;
        };
        return workload;
    }

    /// <summary>Whether the pod shares its process namespace (see <see cref="WithProcessFaults"/>).</summary>
    public static bool IsEnabled(V1Pod pod)
    {
        ArgumentNullException.ThrowIfNull(pod);
        return pod.Spec?.ShareProcessNamespace == true;
    }
}