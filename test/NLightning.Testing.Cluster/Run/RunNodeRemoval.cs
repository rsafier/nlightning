using System.Net;
using k8s;
using k8s.Autorest;

namespace NLightning.Testing.Cluster.Run;

using Kube;
using Runner;
using Topology;

/// <summary>
/// Removes one node from a live run (a node a test added next to a warm topology, e.g. a second CLN with other
/// flags): its StatefulSet, its pod (stopped with a short grace period instead of its own, as the run's teardown does),
/// its Services (the headless one and a <see cref="StableNodeAddress"/> <c>&lt;node&gt;-p2p</c>) and its data PVC.
/// Returns once the pod and the PVC are gone, so a node of the same name can be deployed again at once.
/// </summary>
public static class RunNodeRemoval
{
    /// <summary>The PVC of <paramref name="nodeName"/>'s data volume (the StatefulSet's claim template, ordinal 0).</summary>
    public static string DataClaimName(string nodeName) => $"{DataVolume.VolumeName}-{nodeName}-0";

    /// <summary>Removes the node <paramref name="nodeName"/> of namespace <paramref name="ns"/>.</summary>
    /// <exception cref="TimeoutException">Its pod or PVC still exists after <paramref name="timeout"/>.</exception>
    public static async Task RemoveAsync(IKubernetes client, string ns, string nodeName, int podGracePeriodSeconds,
                                         TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfNegative(podGracePeriodSeconds);

        // The StatefulSet first (so nothing recreates the pod), then the pod with the short grace: a later delete with a
        // shorter grace period replaces the pod's own (CLN's preStop drain alone is 5 s)
        await IgnoreNotFound(client.AppsV1.DeleteNamespacedStatefulSetAsync(
                                 nodeName, ns, propagationPolicy: "Background", cancellationToken: cancellationToken))
           .ConfigureAwait(false);
        await IgnoreNotFound(client.CoreV1.DeleteNamespacedPodAsync(
                                 $"{nodeName}-0", ns, gracePeriodSeconds: podGracePeriodSeconds,
                                 cancellationToken: cancellationToken))
           .ConfigureAwait(false);
        await IgnoreNotFound(client.CoreV1.DeleteNamespacedServiceAsync(nodeName, ns,
                                                                         cancellationToken: cancellationToken))
           .ConfigureAwait(false);
        await IgnoreNotFound(client.CoreV1.DeleteNamespacedServiceAsync(StableNodeAddress.ServiceName(nodeName), ns,
                                                                         cancellationToken: cancellationToken))
           .ConfigureAwait(false);
        await IgnoreNotFound(client.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(
                                 DataClaimName(nodeName), ns, cancellationToken: cancellationToken))
           .ConfigureAwait(false);

        await Poll.UntilDoneAsync(async ct =>
        {
            if (await AdoptedNamespace.NodeExistsAsync(client, ns, nodeName, ct).ConfigureAwait(false))
                return "its StatefulSet or pod still exists";

            return await ClaimExistsAsync(client, ns, DataClaimName(nodeName), ct).ConfigureAwait(false)
                       ? "its data PVC still exists"
                       : null;
        }, timeout, $"node {ns}/{nodeName} removed", cancellationToken, TimeSpan.FromMilliseconds(250))
                  .ConfigureAwait(false);
    }

    private static async Task<bool> ClaimExistsAsync(IKubernetes client, string ns, string claim,
                                                     CancellationToken cancellationToken)
    {
        try
        {
            await client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(claim, ns,
                                                                         cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
            return true;
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
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