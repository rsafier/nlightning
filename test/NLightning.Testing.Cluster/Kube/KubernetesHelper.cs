using System.Net;
using System.Text;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace NLightning.Testing.Cluster.Kube;

/// <summary>
/// Pod, exec, Service and PVC primitives on <see cref="IKubernetes"/>. Ported from LNUnit PR #10's
/// <c>KubernetesHelper</c> (pod readiness wait, exec file reads, headless Service, PVC) and changed where the harness
/// needs it: cancellation and <see cref="TimeSpan"/> timeouts everywhere, waits that fail fast on hopeless pod states,
/// a binary-safe exec that returns the exit code (PR #10 shared one buffer between stdout and stderr and failed on any
/// stderr output), and waits for a <em>new</em> pod after a restart.
/// </summary>
public static class KubernetesHelper
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The largest file <see cref="WriteFileAsync"/> sends (it travels base64 encoded in the command line).</summary>
    public const int MaxWriteFileBytes = 256 * 1024;

    /// <summary>
    /// Waits until the pod is Running with an IP and ready (see <see cref="PodStatusReader.IsReady"/>). With
    /// <paramref name="previousUid"/>, a pod with that UID (the one before a restart) does not count.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pod reached a hopeless state (<see cref="PodStatusReader.GetFatalReason"/>).</exception>
    /// <exception cref="TimeoutException">Not ready after <paramref name="timeout"/>.</exception>
    public static Task<V1Pod> WaitForPodReadyAsync(this IKubernetes client, string ns, string podName,
                                                   TimeSpan timeout, CancellationToken cancellationToken,
                                                   string? previousUid = null) =>
        client.WaitForPodAsync(ns, podName, PodStatusReader.IsReady, "ready", timeout, previousUid,
                               cancellationToken);

    /// <summary>
    /// Waits until the pod is Running with an IP, ready or not (to exec into a node before its readiness probe can
    /// pass, e.g. to create a wallet).
    /// </summary>
    public static Task<V1Pod> WaitForPodRunningAsync(this IKubernetes client, string ns, string podName,
                                                     TimeSpan timeout, CancellationToken cancellationToken,
                                                     string? previousUid = null) =>
        client.WaitForPodAsync(ns, podName, PodStatusReader.IsRunning, "running", timeout, previousUid,
                               cancellationToken);

    /// <summary>Waits until the StatefulSet has all its replicas ready at its current generation.</summary>
    public static async Task<V1StatefulSet> WaitForStatefulSetReadyAsync(this IKubernetes client, string ns,
                                                                         string name, TimeSpan timeout,
                                                                         CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        V1StatefulSet? set = null;
        while (true)
        {
            set = await client.AppsV1.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken: cancellationToken)
                              .ConfigureAwait(false);
            var replicas = set.Spec?.Replicas ?? 1;
            if (set.Status is { } status && status.ObservedGeneration >= set.Metadata.Generation
                                         && status.ReadyReplicas == replicas)
                return set;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"StatefulSet {ns}/{name} not ready after {timeout}: {set.Status?.ReadyReplicas ?? 0}/{replicas}");

            await Task.Delay(s_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The pod, or null when it does not exist (yet).</summary>
    public static async Task<V1Pod?> TryReadPodAsync(this IKubernetes client, string ns, string podName,
                                                     CancellationToken cancellationToken)
    {
        try
        {
            return await client.CoreV1.ReadNamespacedPodAsync(podName, ns, cancellationToken: cancellationToken)
                               .ConfigureAwait(false);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes a pod (a StatefulSet recreates it with the same name and volumes). Grace 0 is a kill. Returns the UID
    /// of the deleted pod, or null when there was none.
    /// </summary>
    public static async Task<string?> DeletePodAsync(this IKubernetes client, string ns, string podName,
                                                     int gracePeriodSeconds, CancellationToken cancellationToken)
    {
        var pod = await client.TryReadPodAsync(ns, podName, cancellationToken).ConfigureAwait(false);
        if (pod is null)
            return null;

        try
        {
            await client.CoreV1.DeleteNamespacedPodAsync(podName, ns, gracePeriodSeconds: gracePeriodSeconds,
                                                         cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // Gone in between
        }

        return pod.Metadata.Uid;
    }

    /// <summary>
    /// Runs <paramref name="command"/> (no shell unless the command is one) in a container and returns its exit code
    /// and raw output.
    /// </summary>
    public static async Task<ExecResult> ExecAsync(this IKubernetes client, string ns, string podName,
                                                   string container, IReadOnlyList<string> command,
                                                   CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(command.Count);

        using var stdOut = new MemoryStream();
        using var stdErr = new MemoryStream();
        var exitCode = await client.NamespacedPodExecAsync(
                                       podName, ns, container, command, false,
                                       async (_, output, error) =>
                                       {
                                           await Task.WhenAll(output.CopyToAsync(stdOut, cancellationToken),
                                                              error.CopyToAsync(stdErr, cancellationToken))
                                                     .ConfigureAwait(false);
                                       }, cancellationToken)
                                   .ConfigureAwait(false);
        return new ExecResult(exitCode, stdOut.ToArray(), stdErr.ToArray());
    }

    /// <summary>A file's bytes, read with <c>cat</c> (binary safe; LND's <c>admin.macaroon</c>).</summary>
    /// <exception cref="KubeExecException">The file cannot be read.</exception>
    public static async Task<byte[]> ReadFileAsync(this IKubernetes client, string ns, string podName,
                                                   string container, string path,
                                                   CancellationToken cancellationToken)
    {
        var result = await client.ExecAsync(ns, podName, container, ["cat", path], cancellationToken)
                                 .ConfigureAwait(false);
        return result.EnsureSuccess($"cat {path} in {ns}/{podName}").StdOut;
    }

    /// <summary>A text file's content (UTF-8; LND's <c>tls.cert</c>).</summary>
    public static async Task<string> ReadTextFileAsync(this IKubernetes client, string ns, string podName,
                                                       string container, string path,
                                                       CancellationToken cancellationToken) =>
        Encoding.UTF8.GetString(await client.ReadFileAsync(ns, podName, container, path, cancellationToken)
                                            .ConfigureAwait(false));

    /// <summary>Polls until the file can be read (a node writes it at startup), then returns its bytes.</summary>
    /// <exception cref="TimeoutException">Still unreadable after <paramref name="timeout"/>.</exception>
    public static async Task<byte[]> WaitForFileAsync(this IKubernetes client, string ns, string podName,
                                                      string container, string path, TimeSpan timeout,
                                                      CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var result = await client.ExecAsync(ns, podName, container, ["cat", path], cancellationToken)
                                     .ConfigureAwait(false);
            if (result.Succeeded)
                return result.StdOut;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"{path} not readable in {ns}/{podName} after {timeout}: {result.StdErrText.Trim()}");

            await Task.Delay(s_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a small file into a container (with <c>sh</c> and <c>base64 -d</c>, which busybox and Debian images
    /// have), creating its directory. At most <see cref="MaxWriteFileBytes"/>; mount a ConfigMap for more.
    /// </summary>
    public static async Task WriteFileAsync(this IKubernetes client, string ns, string podName, string container,
                                            string path, ReadOnlyMemory<byte> content,
                                            CancellationToken cancellationToken)
    {
        if (content.Length > MaxWriteFileBytes)
            throw new ArgumentOutOfRangeException(nameof(content),
                                                  $"{content.Length} bytes; at most {MaxWriteFileBytes}");

        string[] command =
        [
            "sh", "-c", "mkdir -p \"$(dirname \"$1\")\" && printf '%s' \"$0\" | base64 -d > \"$1\"",
            Convert.ToBase64String(content.Span), path
        ];
        var result = await client.ExecAsync(ns, podName, container, command, cancellationToken)
                                 .ConfigureAwait(false);
        result.EnsureSuccess($"writing {path} in {ns}/{podName}");
    }

    /// <summary>The container's log (the last <paramref name="tailLines"/> lines when given), for diagnostics.</summary>
    public static async Task<string> ReadLogAsync(this IKubernetes client, string ns, string podName,
                                                  string container, int? tailLines,
                                                  CancellationToken cancellationToken, bool previous = false)
    {
        await using var stream = await client.CoreV1.ReadNamespacedPodLogAsync(
                                                 podName, ns, container, previous: previous, tailLines: tailLines,
                                                 cancellationToken: cancellationToken)
                                             .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a node's headless Service, then its StatefulSet.</summary>
    public static async Task ApplyAsync(this IKubernetes client, NodeWorkloadManifests manifests,
                                        CancellationToken cancellationToken)
    {
        var ns = manifests.StatefulSet.Metadata.NamespaceProperty;
        await client.CoreV1.CreateNamespacedServiceAsync(manifests.Service, ns, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
        await client.AppsV1.CreateNamespacedStatefulSetAsync(manifests.StatefulSet, ns,
                                                             cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
    }

    /// <summary>
    /// A standalone ReadWriteOnce PVC (a StatefulSet's own data comes from its claim template instead). Ported from
    /// PR #10's <c>CreatePVC</c>.
    /// </summary>
    public static V1PersistentVolumeClaim BuildPersistentVolumeClaim(string ns, string name, string size,
                                                                     IReadOnlyDictionary<string, string> labels,
                                                                     string? storageClassName = null) =>
        new()
        {
            ApiVersion = "v1",
            Kind = "PersistentVolumeClaim",
            Metadata = new V1ObjectMeta
            {
                Name = KubeNames.RequireDns1123Label(name, "PVC name"),
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>(labels)
            },
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = ["ReadWriteOnce"],
                StorageClassName = storageClassName,
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(size) }
                }
            }
        };

    /// <summary>Creates <see cref="BuildPersistentVolumeClaim"/>'s claim.</summary>
    public static Task<V1PersistentVolumeClaim> CreatePersistentVolumeClaimAsync(
        this IKubernetes client, string ns, string name, string size, IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken, string? storageClassName = null) =>
        client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(
            BuildPersistentVolumeClaim(ns, name, size, labels, storageClassName), ns,
            cancellationToken: cancellationToken);

    private static async Task<V1Pod> WaitForPodAsync(this IKubernetes client, string ns, string podName,
                                                     Func<V1Pod, bool> condition, string what, TimeSpan timeout,
                                                     string? previousUid, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        V1Pod? pod = null;
        while (true)
        {
            pod = await client.TryReadPodAsync(ns, podName, cancellationToken).ConfigureAwait(false);
            if (pod is not null && (previousUid is null || pod.Metadata.Uid != previousUid))
            {
                if (condition(pod))
                    return pod;
                if (PodStatusReader.GetFatalReason(pod) is { } reason)
                    throw new InvalidOperationException($"Pod {ns}/{podName} will not become {what}: {reason}");
            }

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Pod {ns}/{podName} not {what} after {timeout}: {PodStatusReader.Describe(pod)}");

            await Task.Delay(s_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}