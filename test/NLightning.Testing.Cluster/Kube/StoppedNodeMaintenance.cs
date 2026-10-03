using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace NLightning.Testing.Cluster.Kube;

/// <summary>
/// A shell in a maintenance pod that mounts a stopped node's data PVC at the node's own mount path
/// (<see cref="StoppedNodeMaintenance.RunAsync"/>): what a Docker test does with <c>docker cp</c> while a container is
/// stopped (e.g. roll an LND <c>channel.db</c> back, test harness phase 6).
/// </summary>
public sealed class NodeMaintenanceShell
{
    private readonly IKubernetes _client;

    internal NodeMaintenanceShell(IKubernetes client, string ns, string podName, string container, string mountPath)
    {
        _client = client;
        Namespace = ns;
        PodName = podName;
        ContainerName = container;
        MountPath = mountPath;
    }

    public string Namespace { get; }

    /// <summary>The maintenance pod (<c>&lt;node&gt;-maint</c>).</summary>
    public string PodName { get; }

    public string ContainerName { get; }

    /// <summary>Where the node's data PVC is mounted, the node container's own mount path.</summary>
    public string MountPath { get; }

    /// <summary>Runs <paramref name="command"/> in the maintenance container (the node's image and user).</summary>
    public Task<ExecResult> ExecAsync(IReadOnlyList<string> command, CancellationToken cancellationToken) =>
        _client.ExecAsync(Namespace, PodName, ContainerName, command, cancellationToken);

    /// <summary>Runs <c>sh -c <paramref name="script"/></c> and throws when it fails.</summary>
    /// <exception cref="KubeExecException">The script exited with a non-zero code.</exception>
    public async Task<ExecResult> RunScriptAsync(string script, CancellationToken cancellationToken) =>
        (await ExecAsync(["sh", "-c", script], cancellationToken).ConfigureAwait(false))
       .EnsureSuccess($"{Namespace}/{PodName}: sh -c '{script}'");
}

/// <summary>
/// Runs code against a node's data while the node is stopped: the StatefulSet is scaled to 0 (the node's pod gets its
/// graceful stop and is gone), a maintenance pod of the node's image mounts its data PVC at the same path, the code
/// runs (<see cref="NodeMaintenanceShell"/>), the maintenance pod is deleted and the StatefulSet scaled back to 1.
/// <see cref="Nodes.KubeNodeHandle.RestartAsync(TimeSpan, Func{NodeMaintenanceShell, CancellationToken, Task}, CancellationToken)"/>
/// then waits for the new pod (same name, DNS name and PVC; a new pod IP, as after any restart).
/// </summary>
/// <remarks>
/// The maintenance pod carries the run's labels but none of the StatefulSet's selector labels, so the StatefulSet never
/// adopts it and the node's headless Service never routes to it. It runs the node container's image, security context
/// and resources (the namespace quota needs requests and limits) with <c>sleep</c> as its command (the image's
/// entrypoint is replaced), so the files it writes get the owner the node expects.
/// </remarks>
public static class StoppedNodeMaintenance
{
    /// <summary>The maintenance pod's name suffix.</summary>
    public const string PodSuffix = "-maint";

    /// <summary>The label naming the node a maintenance pod works on.</summary>
    public const string MaintenanceLabel = "nltg.maintenance";

    /// <summary>
    /// Stops the StatefulSet <paramref name="name"/> (scale 0), runs <paramref name="whileStopped"/> in a maintenance
    /// pod on its data PVC and starts it again (scale 1; the caller waits for the new pod). The StatefulSet is scaled
    /// back even when <paramref name="whileStopped"/> throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node keeps no data on a PVC.</exception>
    public static async Task RunAsync(IKubernetes client, string ns, string name, TimeSpan timeout,
                                      Func<NodeMaintenanceShell, CancellationToken, Task> whileStopped,
                                      CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(whileStopped);

        var set = await client.AppsV1.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken: cancellationToken)
                              .ConfigureAwait(false);
        var pod = BuildMaintenancePod(set, ns, name);
        var mountPath = pod.Spec.Containers[0].VolumeMounts![0].MountPath;

        await ScaleAsync(client, ns, name, 0, cancellationToken).ConfigureAwait(false);
        try
        {
            await WaitPodGoneAsync(client, ns, $"{name}-0", timeout, cancellationToken).ConfigureAwait(false);
            await DeleteAndWaitAsync(client, ns, pod.Metadata.Name, timeout, cancellationToken).ConfigureAwait(false);
            await client.CoreV1.CreateNamespacedPodAsync(pod, ns, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
            try
            {
                await client.WaitForPodRunningAsync(ns, pod.Metadata.Name, timeout, cancellationToken)
                            .ConfigureAwait(false);
                await whileStopped(new NodeMaintenanceShell(client, ns, pod.Metadata.Name,
                                                            pod.Spec.Containers[0].Name, mountPath),
                                   cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // The PVC is ReadWriteOnce: the node's new pod must not start while this one still holds it
                using var cleanup = new CancellationTokenSource(timeout);
                await DeleteAndWaitAsync(client, ns, pod.Metadata.Name, timeout, cleanup.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            using var scaleBack = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ScaleAsync(client, ns, name, 1, scaleBack.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The maintenance pod of the StatefulSet <paramref name="set"/>: its main container's image, security context and
    /// resources, its data PVC (<c>data-&lt;name&gt;-0</c>) at the container's mount path, <c>sleep</c> as the command,
    /// the template's labels without the selector's.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node keeps no data on a PVC.</exception>
    public static V1Pod BuildMaintenancePod(V1StatefulSet set, string ns, string name)
    {
        ArgumentNullException.ThrowIfNull(set);

        var template = set.Spec.Template;
        var main = template.Spec.Containers.FirstOrDefault(c => c.Name == name) ?? template.Spec.Containers[0];
        var claim = set.Spec.VolumeClaimTemplates?.FirstOrDefault(c => c.Metadata.Name == DataVolume.VolumeName);
        var mount = main.VolumeMounts?.FirstOrDefault(m => m.Name == DataVolume.VolumeName);
        if (claim is null || mount is null)
            throw new InvalidOperationException(
                $"{ns}/{name} keeps no data on a PVC: there is nothing to work on while it is stopped");

        var selector = set.Spec.Selector?.MatchLabels ?? new Dictionary<string, string>();
        var labels = (template.Metadata?.Labels ?? new Dictionary<string, string>())
                    .Where(l => !selector.ContainsKey(l.Key))
                    .ToDictionary(l => l.Key, l => l.Value);
        labels[MaintenanceLabel] = name;

        return new V1Pod
        {
            ApiVersion = "v1",
            Kind = "Pod",
            Metadata = new V1ObjectMeta { Name = name + PodSuffix, NamespaceProperty = ns, Labels = labels },
            Spec = new V1PodSpec
            {
                Containers =
                [
                    new V1Container
                    {
                        Name = "maint",
                        Image = main.Image,
                        ImagePullPolicy = main.ImagePullPolicy,
                        Command = ["sleep", "3600"],
                        Args = null,
                        SecurityContext = main.SecurityContext,
                        Resources = main.Resources,
                        VolumeMounts = [new V1VolumeMount { Name = DataVolume.VolumeName, MountPath = mount.MountPath }]
                    }
                ],
                Volumes =
                [
                    new V1Volume
                    {
                        Name = DataVolume.VolumeName,
                        PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                        {
                            ClaimName = $"{claim.Metadata.Name}-{name}-0"
                        }
                    }
                ],
                RestartPolicy = "Never",
                TerminationGracePeriodSeconds = 0,
                EnableServiceLinks = false,
                AutomountServiceAccountToken = false,
                SecurityContext = template.Spec.SecurityContext
            }
        };
    }

    private static Task ScaleAsync(IKubernetes client, string ns, string name, int replicas,
                                   CancellationToken cancellationToken) =>
        client.AppsV1.PatchNamespacedStatefulSetScaleAsync(
            new V1Patch("{\"spec\":{\"replicas\":" + replicas + "}}", V1Patch.PatchType.MergePatch), name, ns,
            cancellationToken: cancellationToken);

    private static async Task DeleteAndWaitAsync(IKubernetes client, string ns, string podName, TimeSpan timeout,
                                                 CancellationToken cancellationToken)
    {
        try
        {
            await client.CoreV1.DeleteNamespacedPodAsync(podName, ns, gracePeriodSeconds: 0,
                                                         cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await WaitPodGoneAsync(client, ns, podName, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static Task WaitPodGoneAsync(IKubernetes client, string ns, string podName, TimeSpan timeout,
                                         CancellationToken cancellationToken) =>
        Poll.UntilAsync(async ct => await client.TryReadPodAsync(ns, podName, ct).ConfigureAwait(false) is null,
                        timeout, Poll.DefaultInterval, $"pod {ns}/{podName} gone", cancellationToken);
}