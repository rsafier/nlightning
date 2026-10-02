using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace NLightning.Testing.Cluster.Topology;

using Kube;
using Nodes;
using Run;

/// <summary>
/// A ClusterIP Service in front of one node's p2p port: an address that stays the same across restarts (plan R11,
/// the Kubernetes answer to NL-262). A pod gets another IP when it is recreated, and Lightning implementations keep
/// the IP they resolved (CLN stores it and redials it); a SYN to a vanished pod IP gets no answer, so the redial
/// hangs for minutes. Through the Service's virtual IP the dial is refused at once while the node is down (no
/// endpoints) and reaches the new pod as soon as it is ready.
/// </summary>
public static class StableNodeAddress
{
    /// <summary>The suffix of the Service name (<c>&lt;node&gt;-p2p</c>).</summary>
    public const string Suffix = "-p2p";

    /// <summary>The Service name of <paramref name="nodeName"/>.</summary>
    public static string ServiceName(string nodeName) =>
        KubeNames.RequireDns1123Label(nodeName, "node name", KubeNames.MaxWorkloadNameLength) + Suffix;

    /// <summary>The DNS name peers dial (it resolves to the stable ClusterIP).</summary>
    public static string DnsName(RunIdentity run, string nodeName) => run.ServiceDnsName(ServiceName(nodeName));

    /// <summary>The ClusterIP Service of <paramref name="nodeName"/>'s <paramref name="port"/>.</summary>
    public static V1Service Build(RunIdentity run, string nodeName, NodeKind kind, int port)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = new V1ObjectMeta
            {
                Name = ServiceName(nodeName),
                NamespaceProperty = run.Namespace,
                Labels = RunLabels.ForNode(run.Labels, nodeName, kind)
            },
            Spec = new V1ServiceSpec
            {
                Type = "ClusterIP",
                Selector = RunLabels.NodeSelector(run.Id, nodeName),
                Ports = [new V1ServicePort { Name = "p2p", Port = port, TargetPort = port, Protocol = "TCP" }]
            }
        };
    }

    /// <summary>Creates the Service (an existing one is kept) and returns its DNS name.</summary>
    public static async Task<string> EnsureAsync(IKubernetes client, RunIdentity run, string nodeName, NodeKind kind,
                                                 int port, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            await client.CoreV1.CreateNamespacedServiceAsync(Build(run, nodeName, kind, port), run.Namespace,
                                                             cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Conflict)
        {
            // Already there (a redeploy of the node)
        }

        return DnsName(run, nodeName);
    }
}