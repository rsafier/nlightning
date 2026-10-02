using System.Globalization;
using System.Text;
using k8s.Models;

namespace NLightning.Testing.Cluster.Diagnostics;

/// <summary>
/// <c>kubectl describe</c>-like text of the objects a dump records (pods, events, PVCs/PVs, StatefulSets, Services,
/// NetworkPolicies), with secrets masked (<see cref="SecretRedactor"/>). Pure: the dump reads, this formats.
/// </summary>
public static class ResourceDescriber
{
    /// <summary>A pod: metadata, phase, IPs, conditions, and per container its image, command, env names, probes,
    /// state, last state, ready and restart count; then its volumes.</summary>
    public static string DescribePod(V1Pod pod)
    {
        ArgumentNullException.ThrowIfNull(pod);
        var b = new StringBuilder();
        var meta = pod.Metadata;
        var status = pod.Status;
        b.AppendLine($"Name:         {meta?.Name}");
        b.AppendLine($"Namespace:    {meta?.NamespaceProperty}");
        b.AppendLine($"UID:          {meta?.Uid}");
        b.AppendLine($"Node:         {pod.Spec?.NodeName ?? "-"}");
        b.AppendLine($"Created:      {Time(meta?.CreationTimestamp)}");
        b.AppendLine($"Start time:   {Time(status?.StartTime)}");
        if (meta?.DeletionTimestamp is { } deleting)
            b.AppendLine($"Deleting:     {Time(deleting)} (grace {meta.DeletionGracePeriodSeconds}s)");
        b.AppendLine($"Labels:       {Join(meta?.Labels)}");
        b.AppendLine($"Phase:        {status?.Phase ?? "?"}{Suffix(status?.Reason)}{Suffix(status?.Message)}");
        b.AppendLine($"Pod IP:       {status?.PodIP ?? "-"}");
        b.AppendLine($"QoS:          {status?.QosClass ?? "-"}");
        b.AppendLine("Conditions:");
        foreach (var c in status?.Conditions ?? [])
            b.AppendLine($"  {c.Type,-26} {c.Status,-6} since {Time(c.LastTransitionTime)}{Suffix(c.Reason)}"
                       + $"{Suffix(c.Message)}");

        var statuses = (status?.InitContainerStatuses ?? []).Concat(status?.ContainerStatuses ?? [])
                                                            .ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (var (container, init) in (pod.Spec?.InitContainers ?? []).Select(c => (c, true))
                                                                          .Concat((pod.Spec?.Containers ?? [])
                                                                             .Select(c => (c, false))))
        {
            b.AppendLine(init ? $"Init container {container.Name}:" : $"Container {container.Name}:");
            DescribeContainer(b, container, statuses.GetValueOrDefault(container.Name));
        }

        b.AppendLine("Volumes:");
        foreach (var v in pod.Spec?.Volumes ?? [])
            b.AppendLine($"  {v.Name}: {DescribeVolumeSource(v)}");

        return SecretRedactor.Redact(b.ToString());
    }

    /// <summary>
    /// The namespace's events, oldest first (by last seen, then event time, first seen, creation), one per line:
    /// time, type, reason, object, count and message.
    /// </summary>
    public static string DescribeEvents(IEnumerable<Corev1Event> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var b = new StringBuilder();
        foreach (var e in events.OrderBy(EventTime).ThenBy(e => e.Metadata?.Name, StringComparer.Ordinal))
        {
            var count = e.Count is > 1 ? $" (x{e.Count})" : string.Empty;
            b.AppendLine($"{Time(EventTime(e))}  {e.Type,-7} {e.Reason,-24} "
                       + $"{e.InvolvedObject?.Kind}/{e.InvolvedObject?.Name}{count}: {e.Message?.Trim()}");
        }

        return SecretRedactor.Redact(b.ToString());
    }

    /// <summary>When an event last happened (UTC), for the order of <see cref="DescribeEvents"/>.</summary>
    public static DateTime EventTime(Corev1Event e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var time = e.LastTimestamp ?? e.EventTime ?? e.FirstTimestamp ?? e.Metadata?.CreationTimestamp
                ?? DateTime.MinValue;
        return time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);
    }

    /// <summary>Each PVC (phase, volume, class, capacity, access modes) and the PV bound to it (phase, reclaim
    /// policy, source path or driver, node affinity).</summary>
    public static string DescribeStorage(IEnumerable<V1PersistentVolumeClaim> claims,
                                         IReadOnlyDictionary<string, V1PersistentVolume> volumes)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(volumes);
        var b = new StringBuilder();
        foreach (var pvc in claims.OrderBy(c => c.Metadata?.Name, StringComparer.Ordinal))
        {
            var capacity = pvc.Status?.Capacity?.TryGetValue("storage", out var q) == true ? q.ToString() : "-";
            var requested = pvc.Spec?.Resources?.Requests?.TryGetValue("storage", out var r) == true
                                ? r.ToString()
                                : "-";
            b.AppendLine($"PVC {pvc.Metadata?.Name}: phase={pvc.Status?.Phase ?? "?"} "
                       + $"volume={pvc.Spec?.VolumeName ?? "-"} class={pvc.Spec?.StorageClassName ?? "-"} "
                       + $"requested={requested} capacity={capacity} "
                       + $"access={string.Join(",", pvc.Spec?.AccessModes ?? [])} created={Time(pvc.Metadata?.CreationTimestamp)}");
            foreach (var c in pvc.Status?.Conditions ?? [])
                b.AppendLine($"  condition {c.Type}={c.Status}{Suffix(c.Reason)}{Suffix(c.Message)}");
            if (pvc.Metadata?.Annotations is { } annotations)
                foreach (var (key, value) in annotations.Where(a => a.Key.Contains("storage", StringComparison.Ordinal)
                                                                 || a.Key.Contains("selected-node",
                                                                                   StringComparison.Ordinal)))
                    b.AppendLine($"  annotation {key}={value}");

            if (pvc.Spec?.VolumeName is { } name && volumes.TryGetValue(name, out var pv))
            {
                var source = pv.Spec?.HostPath?.Path is { } host ? $"hostPath {host}"
                           : pv.Spec?.Local?.Path is { } local ? $"local {local}"
                           : pv.Spec?.Csi?.Driver is { } driver ? $"csi {driver}"
                           : "-";
                var affinity = pv.Spec?.NodeAffinity?.Required?.NodeSelectorTerms?
                                 .SelectMany(t => t.MatchExpressions ?? [])
                                 .Select(e => $"{e.Key} {e.OperatorProperty} {string.Join(",", e.Values ?? [])}")
                                 .ToList() ?? [];
                b.AppendLine($"  PV {name}: phase={pv.Status?.Phase ?? "?"}{Suffix(pv.Status?.Reason)} "
                           + $"reclaim={pv.Spec?.PersistentVolumeReclaimPolicy ?? "-"} source={source} "
                           + $"affinity=[{string.Join("; ", affinity)}]");
            }
            else if (pvc.Spec?.VolumeName is { } missing)
            {
                b.AppendLine($"  PV {missing}: not readable");
            }
        }

        return b.Length == 0 ? "no PVCs\n" : b.ToString();
    }

    /// <summary>StatefulSets (ready/current/updated replicas, revision), Services (type, IP, ports) and
    /// NetworkPolicies (the partitions of <c>Faults/</c>).</summary>
    public static string DescribeWorkloads(IEnumerable<V1StatefulSet> sets, IEnumerable<V1Service> services,
                                           IEnumerable<V1NetworkPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(policies);
        var b = new StringBuilder();
        foreach (var s in sets.OrderBy(s => s.Metadata?.Name, StringComparer.Ordinal))
            b.AppendLine($"StatefulSet {s.Metadata?.Name}: replicas={s.Spec?.Replicas ?? 1} "
                       + $"ready={s.Status?.ReadyReplicas ?? 0} current={s.Status?.CurrentReplicas ?? 0} "
                       + $"updated={s.Status?.UpdatedReplicas ?? 0} generation={s.Metadata?.Generation} "
                       + $"observed={s.Status?.ObservedGeneration} revision={s.Status?.CurrentRevision ?? "-"}");
        foreach (var s in services.OrderBy(s => s.Metadata?.Name, StringComparer.Ordinal))
            b.AppendLine($"Service {s.Metadata?.Name}: type={s.Spec?.Type ?? "-"} clusterIP={s.Spec?.ClusterIP ?? "-"} "
                       + $"ports={string.Join(",", (s.Spec?.Ports ?? []).Select(p => $"{p.Name}:{p.Port}/{p.Protocol}"))} "
                       + $"selector={Join(s.Spec?.Selector)}");
        foreach (var p in policies.OrderBy(p => p.Metadata?.Name, StringComparer.Ordinal))
            b.AppendLine($"NetworkPolicy {p.Metadata?.Name}: pods={Join(p.Spec?.PodSelector?.MatchLabels)} "
                       + $"types={string.Join(",", p.Spec?.PolicyTypes ?? [])} labels={Join(p.Metadata?.Labels)}");
        return b.Length == 0 ? "no workloads\n" : b.ToString();
    }

    private static void DescribeContainer(StringBuilder b, V1Container container, V1ContainerStatus? status)
    {
        b.AppendLine($"  Image:      {container.Image} (pull {container.ImagePullPolicy ?? "-"})");
        if (status?.ImageID is { Length: > 0 } imageId)
            b.AppendLine($"  Image ID:   {imageId}");
        if (container.Command is { Count: > 0 } command)
            b.AppendLine($"  Command:    {string.Join(" ", command)}");
        if (container.Args is { Count: > 0 } args)
            b.AppendLine($"  Args:       {string.Join(" ", args)}");
        if (container.Ports is { Count: > 0 } ports)
            b.AppendLine($"  Ports:      {string.Join(", ", ports.Select(p => $"{p.Name}:{p.ContainerPort}/{p.Protocol}"))}");
        if (container.Env is { Count: > 0 } env)
            b.AppendLine($"  Env:        {string.Join(" ", env.Select(DescribeEnv))}");
        if (container.Resources is { } resources)
            b.AppendLine($"  Resources:  requests {Join(resources.Requests)}; limits {Join(resources.Limits)}");
        if (container.ReadinessProbe is { } readiness)
            b.AppendLine($"  Readiness:  {DescribeProbe(readiness)}");
        if (container.LivenessProbe is { } liveness)
            b.AppendLine($"  Liveness:   {DescribeProbe(liveness)}");
        if (container.VolumeMounts is { Count: > 0 } mounts)
            b.AppendLine($"  Mounts:     {string.Join(", ", mounts.Select(m => $"{m.Name}→{m.MountPath}"))}");
        if (status is null)
        {
            b.AppendLine("  Status:     not reported");
            return;
        }

        b.AppendLine($"  State:      {DescribeState(status.State)}");
        if (status.LastState is { } last && (last.Terminated is not null || last.Waiting is not null))
            b.AppendLine($"  Last state: {DescribeState(last)}");
        b.AppendLine($"  Ready:      {status.Ready}  Started: {status.Started?.ToString() ?? "-"}  "
                   + $"Restarts: {status.RestartCount}");
    }

    private static string DescribeEnv(V1EnvVar env)
    {
        if (env.ValueFrom is { } from)
            return $"{env.Name}=<from {(from.SecretKeyRef is not null ? "secret" : from.ConfigMapKeyRef is not null ? "configmap" : "field")}>";
        return SecretRedactor.IsSecretName(env.Name) ? $"{env.Name}={SecretRedactor.Mask}" : $"{env.Name}={env.Value}";
    }

    private static string DescribeProbe(V1Probe probe)
    {
        var action = probe.Exec?.Command is { } command ? $"exec [{string.Join(" ", command)}]"
                   : probe.TcpSocket is { } tcp ? $"tcp :{tcp.Port}"
                   : probe.HttpGet is { } http ? $"http {http.Path}:{http.Port}"
                   : "?";
        return $"{action} delay={probe.InitialDelaySeconds ?? 0}s period={probe.PeriodSeconds ?? 10}s "
             + $"timeout={probe.TimeoutSeconds ?? 1}s failures={probe.FailureThreshold ?? 3}";
    }

    /// <summary>One container state: running since, waiting (reason, message) or terminated (reason, exit code,
    /// signal, times, message).</summary>
    public static string DescribeState(V1ContainerState? state) =>
        state switch
        {
            { Running: { } r } => $"running since {Time(r.StartedAt)}",
            { Waiting: { } w } => $"waiting ({w.Reason}){Suffix(w.Message)}",
            { Terminated: { } t } => $"terminated ({t.Reason}, exit {t.ExitCode}"
                                   + (t.Signal is { } s ? $", signal {s}" : string.Empty)
                                   + $") {Time(t.StartedAt)} → {Time(t.FinishedAt)}{Suffix(t.Message)}",
            _ => "unknown"
        };

    private static string DescribeVolumeSource(V1Volume v) =>
        v.PersistentVolumeClaim is { } pvc ? $"pvc {pvc.ClaimName}"
      : v.EmptyDir is not null ? "emptyDir"
      : v.ConfigMap is { } cm ? $"configMap {cm.Name}"
      : v.Secret is { } secret ? $"secret {secret.SecretName}"
      : v.HostPath is { } host ? $"hostPath {host.Path}"
      : v.Projected is not null ? "projected"
      : "other";

    private static string Time(DateTime? time) =>
        time is { } t
            ? (t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t).ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                                                                               CultureInfo.InvariantCulture)
            : "-";

    private static string Suffix(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : $" {text.Trim()}";

    private static string Join<TValue>(IDictionary<string, TValue>? map) =>
        map is null || map.Count == 0
            ? "-"
            : string.Join(",", map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
}