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
    public static V1Namespace Build(RunIdentity run) =>
        new()
        {
            ApiVersion = "v1",
            Kind = "Namespace",
            Metadata = new V1ObjectMeta
            {
                Name = run.Namespace,
                Labels = new Dictionary<string, string>(run.Labels)
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
    public static async Task CreateAsync(IKubernetes client, RunIdentity run, NamespaceQuota? quota,
                                         CancellationToken cancellationToken)
    {
        await client.CoreV1.CreateNamespaceAsync(Build(run), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
        if (quota is not null)
            await client.CoreV1.CreateNamespacedResourceQuotaAsync(BuildQuota(run, quota), run.Namespace,
                                                                   cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes <paramref name="run"/>'s namespace (everything in it goes with it, PVCs included). Returns false when it
    /// does not exist.
    /// </summary>
    /// <exception cref="InvalidOperationException">The namespace exists but is not the run's own.</exception>
    public static async Task<bool> DeleteAsync(IKubernetes client, RunIdentity run,
                                               CancellationToken cancellationToken)
    {
        var ns = await TryReadAsync(client, run.Namespace, cancellationToken).ConfigureAwait(false);
        if (ns is null)
            return false;
        if (!IsOwnedBy(ns, run.Id, run.NamespacePrefix))
            throw new InvalidOperationException(
                $"Refusing to delete namespace {run.Namespace}: it does not carry {RunLabels.Run}={run.Id} and "
              + $"{RunLabels.ManagedBy}={RunLabels.ManagedByValue}");

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