using System.Net;
using k8s;
using k8s.Autorest;

namespace NLightning.Testing.Cluster.Runner;

using Run;

/// <summary>
/// A run namespace created by someone else (the host that launched an in-cluster runner) and used by a
/// <see cref="TestRun"/> with <see cref="TestRunOptions.AdoptNamespace"/>: the run checks it is the run's own, never
/// creates or deletes it, and removes only the nodes it deployed when it is disposed.
/// </summary>
public static class AdoptedNamespace
{
    /// <summary>Reads the namespace and checks it carries the run's labels.</summary>
    /// <exception cref="InvalidOperationException">It does not exist or is not the run's own.</exception>
    public static async Task RequireOwnedAsync(IKubernetes client, RunIdentity run,
                                               CancellationToken cancellationToken)
    {
        var ns = await RunNamespace.TryReadAsync(client, run.Namespace, cancellationToken).ConfigureAwait(false)
              ?? throw new InvalidOperationException(
                     $"Namespace {run.Namespace} to adopt does not exist ({TestRunOptions.AdoptNamespaceVariable})");
        if (!RunNamespace.IsOwnedBy(ns, run.Id, run.NamespacePrefix))
            throw new InvalidOperationException(
                $"Refusing to adopt namespace {run.Namespace}: it does not carry {RunLabels.Run}={run.Id} and "
              + $"{RunLabels.ManagedBy}={RunLabels.ManagedByValue}");
    }

    /// <summary>
    /// Deletes the nodes' StatefulSets (their PVCs go with them, retention <c>Delete</c>) and Services, then waits
    /// until their pods are gone.
    /// </summary>
    public static async Task DeleteNodesAsync(IKubernetes client, string ns, IEnumerable<string> nodes,
                                              TimeSpan timeout, CancellationToken cancellationToken)
    {
        var names = nodes.ToList();
        foreach (var name in names)
        {
            await IgnoreNotFound(client.AppsV1.DeleteNamespacedStatefulSetAsync(
                                     name, ns, propagationPolicy: "Foreground",
                                     cancellationToken: cancellationToken))
               .ConfigureAwait(false);
            await IgnoreNotFound(client.CoreV1.DeleteNamespacedServiceAsync(
                                     name, ns, cancellationToken: cancellationToken))
               .ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow + timeout;
        foreach (var name in names)
        {
            while (await NodeExistsAsync(client, ns, name, cancellationToken).ConfigureAwait(false))
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"Node {ns}/{name} still exists after {timeout}");

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether the node's StatefulSet or its pod still exists.</summary>
    public static async Task<bool> NodeExistsAsync(IKubernetes client, string ns, string name,
                                                   CancellationToken cancellationToken)
    {
        try
        {
            await client.AppsV1.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
            return true;
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return await Kube.KubernetesHelper.TryReadPodAsync(client, ns, $"{name}-0", cancellationToken)
                                              .ConfigureAwait(false) is not null;
        }
    }

    private static async Task IgnoreNotFound<T>(Task<T> delete)
    {
        try
        {
            await delete.ConfigureAwait(false);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone
        }
    }
}