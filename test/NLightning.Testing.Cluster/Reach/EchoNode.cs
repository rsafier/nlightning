namespace NLightning.Testing.Cluster.Reach;

using Images;
using Kube;
using Nodes;

/// <summary>
/// A busybox node that answers every TCP connection on <see cref="Port"/> with <see cref="Banner"/> (<c>tcpsvd</c>,
/// concurrent connections) and has <c>nc</c> and <c>nslookup</c> for probes from inside the cluster.
/// </summary>
public static class EchoNode
{
    public const int Port = 8080;

    public const string Banner = "nltg-echo";

    public const string PortName = "echo";

    /// <summary>The node's workload; it stops at once on SIGTERM.</summary>
    public static NodeWorkload Build(string name = "echo")
    {
        var workload = new NodeWorkload(name, NodeKind.Other, ImageVersions.Busybox)
        {
            Command =
            [
                "sh", "-c",
                $"trap 'exit 0' TERM; tcpsvd 0.0.0.0 {Port} echo {Banner} & wait"
            ],
            Resources = WorkloadResources.Tiny,
            TerminationGracePeriodSeconds = 2
        };
        workload.Ports.Add(new WorkloadPort(PortName, Port));
        return workload;
    }
}