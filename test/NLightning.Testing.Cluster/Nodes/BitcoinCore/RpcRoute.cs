namespace NLightning.Testing.Cluster.Nodes.BitcoinCore;

/// <summary>
/// How the test process reaches bitcoind's RPC (plan R5: no host ports).
/// </summary>
public enum RpcRoute
{
    /// <summary>
    /// <see cref="ServiceDns"/> when the test process runs in the cluster, <see cref="PodIp"/> when the pod IP is
    /// routable from the host (OrbStack), <see cref="Exec"/> otherwise.
    /// </summary>
    Auto,

    /// <summary>HTTP to the pod IP (OrbStack routes pod IPs to the Mac).</summary>
    PodIp,

    /// <summary>HTTP to the headless Service's DNS name (in-cluster runner, or a host that resolves cluster names).</summary>
    ServiceDns,

    /// <summary><c>bitcoin-cli</c> in the pod through Kubernetes exec: works from anywhere, one exec per call.</summary>
    Exec
}

/// <summary>
/// What the host could reach of a node: a TCP connect to its RPC port by pod IP and by Service DNS name.
/// </summary>
/// <param name="PodIp">The pod IP probed.</param>
/// <param name="PodIpReachable">Whether the RPC port answered on the pod IP.</param>
/// <param name="PodIpConnectTime">How long the connect took, when it succeeded.</param>
/// <param name="ServiceDnsName">The DNS name probed.</param>
/// <param name="ServiceDnsReachable">Whether the name resolved and the RPC port answered.</param>
/// <param name="ServiceDnsError">Why not, when it did not.</param>
public sealed record HostRouteProbe(string? PodIp, bool PodIpReachable, TimeSpan? PodIpConnectTime,
                                    string ServiceDnsName, bool ServiceDnsReachable, string? ServiceDnsError)
{
    public override string ToString() =>
        $"pod IP {PodIp ?? "-"}: {(PodIpReachable ? $"reachable ({PodIpConnectTime?.TotalMilliseconds:F0} ms)" : "not reachable")}; "
      + $"{ServiceDnsName}: {(ServiceDnsReachable ? "reachable" : $"not reachable ({ServiceDnsError})")}";
}

/// <summary>Resolves <see cref="RpcRoute.Auto"/>.</summary>
public static class RpcRouteSelector
{
    /// <summary>
    /// The concrete route: an explicit one as it is; <see cref="RpcRoute.Auto"/> by where the process runs and what
    /// the probe found.
    /// </summary>
    public static RpcRoute Select(RpcRoute requested, bool inCluster, bool podIpReachable) =>
        requested switch
        {
            RpcRoute.Auto when inCluster => RpcRoute.ServiceDns,
            RpcRoute.Auto when podIpReachable => RpcRoute.PodIp,
            RpcRoute.Auto => RpcRoute.Exec,
            _ => requested
        };
}