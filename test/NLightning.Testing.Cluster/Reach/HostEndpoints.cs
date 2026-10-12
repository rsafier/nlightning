using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NLightning.Testing.Cluster.Reach;

/// <summary>
/// How a pod reaches a listener in the host test process (spike check 1, the direction LND and CLN need to dial back
/// to the in-process NLightning node).
/// </summary>
/// <remarks>
/// Measured on OrbStack (k8s v1.35, 2026-10-02; <c>docs/agents/TEST_HARNESS_PLAN.md</c> §5 "Check 1 record"):
/// <list type="bullet">
///   <item><c>host.orb.internal</c> and <c>host.docker.internal</c> resolve in every pod (cluster DNS) and OrbStack
///   forwards them to the Mac's <b>loopback</b>: a listener bound to 127.0.0.1 is reachable, and it sees the peer as
///   127.0.0.1 (our node then treats it as a loopback peer, NL-497).</item>
///   <item>The Mac's addresses (its LAN IP, its address on OrbStack's bridge) reach only a listener bound to all
///   interfaces; the peer then shows as the cluster node's IP.</item>
///   <item>The cluster node's own IP does not reach the host.</item>
/// </list>
/// Elsewhere (kind, k3d, a real cluster) none of this holds: run the test process in-cluster
/// (<see cref="Runner.InClusterTestRunner"/>) or set <see cref="HostAddressVariable"/>.
/// </remarks>
public static class HostEndpoints
{
    /// <summary>OrbStack's name of the host, forwarded to the host's loopback.</summary>
    public const string OrbStackHost = "host.orb.internal";

    /// <summary>Docker Desktop's name of the host; OrbStack provides it too.</summary>
    public const string DockerHost = "host.docker.internal";

    /// <summary>Overrides the name or address pods use to reach the host.</summary>
    public const string HostAddressVariable = "NLTG_HOST_ADDRESS";

    /// <summary>
    /// The name or address a pod dials to reach the host test process: <see cref="HostAddressVariable"/> when set,
    /// otherwise <see cref="OrbStackHost"/>.
    /// </summary>
    public static string ForPods(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var configured = environment(HostAddressVariable);
        return string.IsNullOrWhiteSpace(configured) ? OrbStackHost : configured.Trim();
    }

    /// <summary>
    /// The address a listener in the test process binds so that <see cref="ForPods"/> reaches it: loopback for the
    /// OrbStack names (nothing is exposed on the LAN), all interfaces for an explicit host address.
    /// </summary>
    public static IPAddress BindAddressFor(string podFacingHost) =>
        podFacingHost is OrbStackHost or DockerHost ? IPAddress.Loopback : IPAddress.Any;

    /// <summary>
    /// The candidates a reachability check tries from a pod, in order: the configured address, the two host names,
    /// the cluster node's IP and the host's own IPv4 addresses. Duplicates are dropped.
    /// </summary>
    public static IReadOnlyList<string> Candidates(IEnumerable<IPAddress> hostAddresses, string? nodeInternalIp,
                                                   Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(hostAddresses);
        var candidates = new List<string> { ForPods(environment), OrbStackHost, DockerHost };
        if (!string.IsNullOrWhiteSpace(nodeInternalIp))
            candidates.Add(nodeInternalIp);
        candidates.AddRange(hostAddresses.Select(a => a.ToString()));
        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The host's private IPv4 addresses on interfaces that are up (no loopback, no link-local), at most
    /// <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<IPAddress> LocalPrivateIPv4Addresses(int max = 4) =>
        NetworkInterface.GetAllNetworkInterfaces()
                        .Where(i => i.OperationalStatus == OperationalStatus.Up
                                 && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                        .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                        .Select(u => u.Address)
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
                        .Distinct()
                        .Take(max)
                        .ToList();

    /// <summary>Whether <paramref name="address"/> is an RFC 1918 IPv4 address.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}