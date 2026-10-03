using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace NLightning.Testing.Cluster.Run;

/// <summary>
/// A run's namespace: built, created, and deleted only when it is provably the run's own (plan R3: cleanup by label,
/// never by name; nothing outside the harness's prefix is ever touched).
/// </summary>
public static class RunNamespace
{
    /// <summary>The namespace manifest of <paramref name="run"/>.</summary>
    public static V1Namespace Build(RunIdentity run) => Build(run, null);

    /// <summary>
    /// The namespace manifest of <paramref name="run"/> with <paramref name="annotations"/> (the owner and keep/TTL
    /// annotations the reaper reads, <see cref="RunAnnotations.ForRun"/>).
    /// </summary>
    public static V1Namespace Build(RunIdentity run, IReadOnlyDictionary<string, string>? annotations) =>
        new()
        {
            ApiVersion = "v1",
            Kind = "Namespace",
            Metadata = new V1ObjectMeta
            {
                Name = run.Namespace,
                Labels = new Dictionary<string, string>(run.Labels),
                Annotations = annotations is null || annotations.Count == 0
                                  ? null
                                  : new Dictionary<string, string>(annotations)
            }
        };

    /// <summary>The ResourceQuota manifest of <paramref name="quota"/> in <paramref name="run"/>'s namespace.</summary>
    public static V1ResourceQuota BuildQuota(RunIdentity run, NamespaceQuota quota)
    {
        var hard = new Dictionary<string, ResourceQuantity>
        {
            ["requests.cpu"] = new(quota.RequestsCpu),
            ["requests.memory"] = new(quota.RequestsMemory)
        };
        if (quota.LimitsCpu is not null)
            hard["limits.cpu"] = new ResourceQuantity(quota.LimitsCpu);
        if (quota.LimitsMemory is not null)
            hard["limits.memory"] = new ResourceQuantity(quota.LimitsMemory);
        if (quota.Pods is { } pods)
            hard["pods"] = new ResourceQuantity(pods.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (quota.PersistentVolumeClaims is { } pvcs)
            hard["persistentvolumeclaims"] =
                new ResourceQuantity(pvcs.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return new V1ResourceQuota
        {
            ApiVersion = "v1",
            Kind = "ResourceQuota",
            Metadata = new V1ObjectMeta
            {
                Name = NamespaceQuota.ObjectName,
                NamespaceProperty = run.Namespace,
                Labels = new Dictionary<string, string>(run.Labels)
            },
            Spec = new V1ResourceQuotaSpec { Hard = hard }
        };
    }

    /// <summary>
    /// Whether <paramref name="ns"/> belongs to run <paramref name="runId"/> under <paramref name="prefix"/>: its name
    /// starts with <c>&lt;prefix&gt;-</c>, it carries <see cref="RunLabels.Run"/>=<paramref name="runId"/> and the
    /// harness's managed-by label.
    /// </summary>
    public static bool IsOwnedBy(V1Namespace ns, string runId, string prefix)
    {
        ArgumentNullException.ThrowIfNull(ns);

        var labels = ns.Metadata?.Labels;
        return ns.Metadata?.Name is { } name
            && name.StartsWith(prefix + "-", StringComparison.Ordinal)
            && labels is not null
            && labels.TryGetValue(RunLabels.Run, out var run) && run == runId
            && labels.TryGetValue(RunLabels.ManagedBy, out var managedBy) && managedBy == RunLabels.ManagedByValue;
    }

    /// <summary>Creates the namespace and, when given, its quota.</summary>
    public static Task CreateAsync(IKubernetes client, RunIdentity run, NamespaceQuota? quota,
                                   CancellationToken cancellationToken) =>
        CreateAsync(client, run, quota, null, cancellationToken);

    /// <summary>
    /// Creates the namespace with <paramref name="annotations"/> and, when given, its quota; when the quota cannot be
    /// created the namespace is deleted again before the error is thrown.
    /// </summary>
    public static async Task CreateAsync(IKubernetes client, RunIdentity run, NamespaceQuota? quota,
                                         IReadOnlyDictionary<string, string>? annotations,
                                         CancellationToken cancellationToken)
    {
        await client.CoreV1.CreateNamespaceAsync(Build(run, annotations), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
        if (quota is null)
            return;

        try
        {
            await client.CoreV1.CreateNamespacedResourceQuotaAsync(BuildQuota(run, quota), run.Namespace,
                                                                   cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
        }
        catch
        {
            await DeleteAsync(client, run, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Deletes <paramref name="run"/>'s namespace (everything in it goes with it, PVCs included). With
    /// <paramref name="podGracePeriodSeconds"/> it first stops the run's pods with that grace period
    /// (<see cref="StopPodsAsync"/>), so the namespace goes in seconds instead of half a minute. Returns false when it
    /// does not exist. Does not wait for it to go (<see cref="WaitForDeletionAsync"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The namespace exists but is not the run's own.</exception>
    public static async Task<bool> DeleteAsync(IKubernetes client, RunIdentity run,
                                               CancellationToken cancellationToken,
                                               int? podGracePeriodSeconds = null)
    {
        var ns = await TryReadAsync(client, run.Namespace, cancellationToken).ConfigureAwait(false);
        if (ns is null)
            return false;
        if (!IsOwnedBy(ns, run.Id, run.NamespacePrefix))
            throw new InvalidOperationException(
                $"Refusing to delete namespace {run.Namespace}: it does not carry {RunLabels.Run}={run.Id} and "
              + $"{RunLabels.ManagedBy}={RunLabels.ManagedByValue}");

        if (podGracePeriodSeconds is { } grace && ns.Metadata.DeletionTimestamp is null)
            await StopPodsAsync(client, run.Namespace, grace, StopPodsTimeout, cancellationToken)
               .ConfigureAwait(false);

        try
        {
            await client.CoreV1.DeleteNamespaceAsync(run.Namespace, gracePeriodSeconds: 0,
                                                     cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        return true;
    }

    /// <summary>How long <see cref="DeleteAsync"/> waits for the pods it stopped before it deletes the namespace anyway.</summary>
    public static readonly TimeSpan StopPodsTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Stops every pod of a namespace whose run is over, quickly: deletes its StatefulSets without their pods (so none
    /// is recreated), deletes the pods with a grace period of <paramref name="gracePeriodSeconds"/> instead of their
    /// own (up to 30 s, plus CLN's <c>preStop</c> drain), and waits (at most <paramref name="timeout"/>) until none is
    /// still pending or running. Best effort: an API error leaves the rest to the namespace's deletion.
    /// </summary>
    /// <remarks>
    /// Why before the namespace's deletion and not after: the namespace controller, finding pods that are not finished
    /// yet, waits for the largest <c>terminationGracePeriodSeconds</c> of their specs before it looks again (30 s for
    /// bitcoind), however soon the pods are actually gone. Measured on OrbStack: run namespaces deleted with their pods
    /// still running took 12-57 s to go with 6 runs at once (up to 169 s when their deletions overlapped), and a solo
    /// one 14-21 s.
    /// </remarks>
    public static async Task StopPodsAsync(IKubernetes client, string ns, int gracePeriodSeconds, TimeSpan timeout,
                                           CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gracePeriodSeconds);
        try
        {
            await client.AppsV1.DeleteCollectionNamespacedStatefulSetAsync(ns, propagationPolicy: "Orphan",
                                                                           cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
            await client.CoreV1.DeleteCollectionNamespacedPodAsync(ns, gracePeriodSeconds: gracePeriodSeconds,
                                                                   cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var pods = await client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: cancellationToken)
                                       .ConfigureAwait(false);
                if (pods.Items.All(p => p.Status?.Phase is "Succeeded" or "Failed"))
                    return;

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpOperationException)
        {
            // The namespace's deletion stops them with their own grace
        }
    }

    /// <summary>Waits until <paramref name="name"/> no longer exists.</summary>
    /// <exception cref="TimeoutException">It still exists after <paramref name="timeout"/>.</exception>
    public static async Task WaitForDeletionAsync(IKubernetes client, string name, TimeSpan timeout,
                                                  CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (await TryReadAsync(client, name, cancellationToken).ConfigureAwait(false) is not null)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Namespace {name} still exists after {timeout}");

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The namespace, or null when it does not exist.</summary>
    public static async Task<V1Namespace?> TryReadAsync(IKubernetes client, string name,
                                                        CancellationToken cancellationToken)
    {
        try
        {
            return await client.CoreV1.ReadNamespaceAsync(name, cancellationToken: cancellationToken)
                               .ConfigureAwait(false);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}