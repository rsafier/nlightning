using k8s.Models;

namespace NLightning.Testing.Cluster.Faults;

using Kube;
using Run;

/// <summary>
/// Builds the NetworkPolicy of a network partition (plan R11). Kubernetes policies only allow, so a partition is a
/// policy that selects one side's pods and allows everything but the other side; nodes the policy does not select
/// keep their own traffic, and their connections to the selected side are refused by the selected side's ingress.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes:
/// <list type="bullet">
///   <item><b>Isolate</b> (<c>others</c> null): the selected nodes reach only each other (and DNS, and the allowed
///   CIDRs inbound). An isolated LN node also loses its chain backend.</item>
///   <item><b>Split</b> (<c>others</c> given): the selected nodes cannot reach the other side and the other side
///   cannot reach them; both keep everything else (their bitcoind, DNS, the runner).</item>
/// </list>
/// </para>
/// <para>
/// Enforcement is the network plugin's. Measured on OrbStack (k3s, flannel host-gw with k3s's embedded kube-router
/// policy controller): policies are enforced in both directions for <em>new</em> connections, pod-to-pod packets are
/// dropped (the connect times out) and host-to-pod connections are refused; <em>established</em> TCP connections
/// survive the policy (connection tracking accepts them before the policy rules), so a partition must be followed by a
/// disconnect (the node's own <c>disconnect</c> command, or a restart) for the peers to lose each other. Deleting the
/// policy (heal) restores new connections within a second.
/// </para>
/// </remarks>
public static class PartitionPolicy
{
    /// <summary>The label every partition policy carries, with value <see cref="FaultLabelValue"/>.</summary>
    public const string FaultLabel = "nltg.fault";

    /// <summary>The value of <see cref="FaultLabel"/> on partition policies.</summary>
    public const string FaultLabelValue = "partition";

    /// <summary>The prefix of partition policy names (<c>nltg-partition-&lt;n&gt;</c>).</summary>
    public const string NamePrefix = "nltg-partition-";

    /// <summary>
    /// The policy that cuts <paramref name="isolated"/> off from <paramref name="others"/> (or from every pod but each
    /// other when <paramref name="others"/> is null) in <paramref name="run"/>'s namespace.
    /// </summary>
    /// <param name="run">The run (namespace, run label).</param>
    /// <param name="name">The policy name (see <see cref="NamePrefix"/>).</param>
    /// <param name="isolated">The aliases of the selected side.</param>
    /// <param name="others">The aliases of the other side, or null to isolate.</param>
    /// <param name="options">DNS and runner exceptions.</param>
    public static V1NetworkPolicy Build(RunIdentity run, string name, IReadOnlyCollection<string> isolated,
                                        IReadOnlyCollection<string>? others, PartitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(isolated);
        ArgumentNullException.ThrowIfNull(options);
        KubeNames.RequireDns1123Label(name, "partition name");

        var side = Validate(isolated, nameof(isolated));
        var otherSide = others is null ? null : Validate(others, nameof(others));
        if (otherSide is not null && otherSide.Intersect(side, StringComparer.Ordinal).FirstOrDefault() is { } both)
            throw new ArgumentException($"Node {both} is on both sides of the partition", nameof(others));

        // Who the selected side may still talk to: its own members (isolate) or every pod not on the other side
        // (split). A podSelector without a namespaceSelector means the run's own namespace.
        var reachable = otherSide is null
                            ? NodesSelector(run.Id, side, "In")
                            : NodesSelector(run.Id, otherSide, "NotIn", withRun: false);
        var peers = new List<V1NetworkPolicyPeer> { new() { PodSelector = reachable } };

        var ingressPeers = new List<V1NetworkPolicyPeer>(peers);
        ingressPeers.AddRange(options.AllowedIngressCidrs.Select(cidr => new V1NetworkPolicyPeer
        {
            IpBlock = new V1IPBlock { Cidr = cidr }
        }));

        var egress = new List<V1NetworkPolicyEgressRule> { new() { To = peers } };
        if (options.AllowDns)
            egress.Add(new V1NetworkPolicyEgressRule
            {
                To =
                [
                    new V1NetworkPolicyPeer
                    {
                        NamespaceSelector = new V1LabelSelector
                        {
                            MatchLabels = new Dictionary<string, string>
                            {
                                ["kubernetes.io/metadata.name"] = options.DnsNamespace
                            }
                        },
                        PodSelector = new V1LabelSelector
                        {
                            MatchLabels = new Dictionary<string, string>(options.DnsPodLabels)
                        }
                    }
                ],
                Ports =
                [
                    new V1NetworkPolicyPort { Port = 53, Protocol = "UDP" },
                    new V1NetworkPolicyPort { Port = 53, Protocol = "TCP" }
                ]
            });

        var labels = new Dictionary<string, string>(run.Labels) { [FaultLabel] = FaultLabelValue };
        return new V1NetworkPolicy
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = run.Namespace,
                Labels = labels,
                Annotations = new Dictionary<string, string>
                {
                    ["nltg.partition/isolated"] = string.Join(',', side),
                    ["nltg.partition/others"] = otherSide is null ? "*" : string.Join(',', otherSide)
                }
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = NodesSelector(run.Id, side, "In"),
                PolicyTypes = ["Ingress", "Egress"],
                Ingress = [new V1NetworkPolicyIngressRule { FromProperty = ingressPeers }],
                Egress = egress
            }
        };
    }

    /// <summary>The label selector of partition policies, for listing and cleanup.</summary>
    public static string Selector(string runId) =>
        RunLabels.ToSelector(new Dictionary<string, string> { [RunLabels.Run] = runId, [FaultLabel] = FaultLabelValue });

    private static List<string> Validate(IReadOnlyCollection<string> nodes, string parameter)
    {
        if (nodes.Count == 0)
            throw new ArgumentException("A partition side needs at least one node", parameter);

        foreach (var node in nodes)
            KubeNames.RequireDns1123Label(node, "node name", KubeNames.MaxWorkloadNameLength);

        return nodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private static V1LabelSelector NodesSelector(string runId, IReadOnlyList<string> nodes, string op,
                                                 bool withRun = true) =>
        new()
        {
            MatchLabels = withRun ? new Dictionary<string, string> { [RunLabels.Run] = runId } : null,
            MatchExpressions =
            [
                new V1LabelSelectorRequirement { Key = RunLabels.Node, OperatorProperty = op, Values = [.. nodes] }
            ]
        };
}