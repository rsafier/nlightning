using System.Text;
using k8s.Models;

namespace NLightning.Testing.Cluster.Kube;

/// <summary>
/// Reads a pod's status: whether it is ready, whether waiting for it is hopeless, and a one-line summary for timeout
/// and failure messages.
/// </summary>
public static class PodStatusReader
{
    /// <summary>
    /// Container waiting reasons that never resolve by themselves, so a wait fails at once instead of timing out.
    /// </summary>
    public static IReadOnlySet<string> FatalWaitingReasons { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "ErrImageNeverPull",
        "InvalidImageName",
        "ImagePullBackOff",
        "ErrImagePull",
        "CreateContainerConfigError",
        "CreateContainerError",
        "CrashLoopBackOff"
    };

    /// <summary>Whether the pod is Running with an IP (its containers may not be ready yet).</summary>
    public static bool IsRunning(V1Pod pod) =>
        pod.Status?.Phase == "Running" && !string.IsNullOrEmpty(pod.Status.PodIP);

    /// <summary>
    /// Whether the pod is Running with an IP, its <c>Ready</c> condition is true and every container is ready (the
    /// readiness probe passed), and it is not being deleted.
    /// </summary>
    public static bool IsReady(V1Pod pod) =>
        IsRunning(pod)
     && pod.Metadata?.DeletionTimestamp is null
     && pod.Status!.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true
     && pod.Status.ContainerStatuses is { Count: > 0 } statuses
     && statuses.All(c => c.Ready);

    /// <summary>
    /// Why waiting for the pod is hopeless (phase Failed, or a container in a <see cref="FatalWaitingReasons"/> state),
    /// or null while it may still come up.
    /// </summary>
    public static string? GetFatalReason(V1Pod pod)
    {
        if (pod.Status?.Phase == "Failed")
            return $"pod failed: {pod.Status.Reason} {pod.Status.Message}".Trim();

        var statuses = (pod.Status?.InitContainerStatuses ?? []).Concat(pod.Status?.ContainerStatuses ?? []);
        foreach (var status in statuses)
        {
            var waiting = status.State?.Waiting;
            if (waiting?.Reason is { } reason && FatalWaitingReasons.Contains(reason))
                return $"container {status.Name}: {reason} {waiting.Message}".Trim();
        }

        return null;
    }

    /// <summary>A one-line summary: phase, IP, conditions and each container's state.</summary>
    public static string Describe(V1Pod? pod)
    {
        if (pod is null)
            return "pod not found";

        var builder = new StringBuilder();
        builder.Append($"{pod.Metadata?.Name} phase={pod.Status?.Phase ?? "?"} ip={pod.Status?.PodIP ?? "-"}");
        foreach (var condition in pod.Status?.Conditions ?? [])
            builder.Append($" {condition.Type}={condition.Status}");
        foreach (var status in pod.Status?.ContainerStatuses ?? [])
        {
            var state = status.State?.Waiting is { } w ? $"waiting({w.Reason})"
                      : status.State?.Terminated is { } t ? $"terminated({t.Reason},{t.ExitCode})"
                      : status.State?.Running is not null ? "running"
                      : "?";
            builder.Append($" [{status.Name} {state} ready={status.Ready} restarts={status.RestartCount}]");
        }

        return builder.ToString();
    }
}