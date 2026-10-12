using System.Net;
using k8s;
using k8s.Models;

namespace NLightning.Testing.Cluster.Reach;

using Kube;
using Run;

/// <summary>Where the test process should run, given what the cluster lets it reach.</summary>
public enum RunnerPlacement
{
    /// <summary>On the host: it reaches the pods and the pods reach it (OrbStack).</summary>
    Host,

    /// <summary>In the run's namespace as a Job (<see cref="Runner.InClusterTestRunner"/>).</summary>
    InCluster
}

/// <summary>
/// What <see cref="ReachabilityCheck.RunAsync"/> found.
/// </summary>
/// <param name="Report">Every probe, labelled.</param>
/// <param name="HostReachesPods">The host connected to the pod IP and to the node's Service DNS name.</param>
/// <param name="PodHostAddress">The first candidate a pod reached the loopback listener through, else the first that
/// reached the all-interfaces listener, or null when no candidate reached the host.</param>
/// <param name="PodHostNeedsAllInterfaces">True when only the all-interfaces listener was reachable.</param>
public sealed record ReachabilityResult(ReachabilityReport Report, bool HostReachesPods, string? PodHostAddress,
                                        bool PodHostNeedsAllInterfaces)
{
    /// <summary>Host when both directions work, InCluster otherwise.</summary>
    public RunnerPlacement Placement =>
        HostReachesPods && PodHostAddress is not null ? RunnerPlacement.Host : RunnerPlacement.InCluster;
}

/// <summary>
/// Spike check 1 as a preflight: deploys an <see cref="EchoNode"/> plus a ClusterIP Service into a run and probes
/// host to pod (pod IP, pod and Service DNS names, ClusterIP, OrbStack's <c>*.k8s.orb.local</c>) and pod to host
/// (<see cref="HostEndpoints.Candidates"/> against a loopback and an all-interfaces listener in this process).
/// </summary>
public static class ReachabilityCheck
{
    /// <summary>The suffix of the ClusterIP Service the check adds next to the echo node's headless one.</summary>
    public const string ClusterIpSuffix = "-cip";

    /// <summary>The probe labels the result's verdict reads.</summary>
    public static class Labels
    {
        public const string PodIp = "host->pod-ip";
        public const string PodDns = "host->pod-dns";
        public const string ServiceDns = "host->headless-svc-dns";
        public const string ClusterIp = "host->clusterip";
        public const string ClusterIpDns = "host->clusterip-dns";
        public const string OrbK8sDns = "host->k8s.orb.local";

        /// <summary>The label of a pod-to-host probe.</summary>
        public static string PodToHost(string candidate, bool loopbackListener) =>
            $"pod->{candidate} ({(loopbackListener ? "lo" : "any")})";
    }

    /// <summary>A ClusterIP Service in front of <paramref name="node"/>'s pod (its headless Service has no IP).</summary>
    public static V1Service BuildClusterIpService(RunIdentity run, string node, int port)
    {
        ArgumentNullException.ThrowIfNull(run);
        var name = KubeNames.RequireDns1123Label(node + ClusterIpSuffix, "service name");
        return new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = run.Namespace,
                Labels = new Dictionary<string, string>(run.Labels)
            },
            Spec = new V1ServiceSpec
            {
                Type = "ClusterIP",
                Selector = RunLabels.NodeSelector(run.Id, node),
                Ports = [new V1ServicePort { Name = "probe", Port = port, TargetPort = port, Protocol = "TCP" }]
            }
        };
    }

    /// <summary>
    /// Runs the check in <paramref name="run"/>'s namespace (an echo node named <paramref name="nodeName"/> and its
    /// ClusterIP Service stay until the run is disposed). A host probe is tried up to <paramref name="attempts"/>
    /// times, 500 ms apart, and says which attempt succeeded; the ClusterIP probes are repeated until
    /// <paramref name="clusterIpWait"/> (default 20 s) after the Service was created, because a new ClusterIP is routed
    /// only after kube-proxy's next sync, and say when they first succeeded.
    /// </summary>
    public static async Task<ReachabilityResult> RunAsync(TestRun run, CancellationToken cancellationToken,
                                                          string nodeName = "echo", TimeSpan? probeTimeout = null,
                                                          Action<string>? log = null, int attempts = 3,
                                                          TimeSpan? clusterIpWait = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        var timeout = probeTimeout ?? TimeSpan.FromSeconds(3);
        attempts = Math.Max(1, attempts);
        var report = new ReachabilityReport();

        var node = await run.DeployAsync(EchoNode.Build(nodeName), TimeSpan.FromMinutes(2), cancellationToken)
                            .ConfigureAwait(false);
        var serviceWatch = System.Diagnostics.Stopwatch.StartNew();
        var service = await run.Client.CoreV1.CreateNamespacedServiceAsync(
                                   BuildClusterIpService(run.Identity, nodeName, EchoNode.Port), run.Namespace,
                                   cancellationToken: cancellationToken)
                               .ConfigureAwait(false);
        var clusterIp = service.Spec.ClusterIP;
        var endpointsAfter = await WaitForReadyEndpointAsync(run.Client, run.Namespace, service.Metadata.Name,
                                                             TimeSpan.FromSeconds(30), cancellationToken)
                                .ConfigureAwait(false);
        var clusterIpDns = run.Identity.ServiceDnsName(service.Metadata.Name);
        log?.Invoke($"[reach] echo pod {node.PodName} at {node.PodIp}, ClusterIP {clusterIp} "
                  + $"(ready endpoint after {endpointsAfter.TotalMilliseconds:F0} ms)");

        // Host to pod
        async Task HostProbe(string label, string host)
        {
            var result = await ProbeWithAttemptsAsync(host, EchoNode.Port, timeout, attempts, cancellationToken)
                            .ConfigureAwait(false);
            report.Add(label, result);
            log?.Invoke($"[reach] {label,-28} {result}");
        }

        await HostProbe(Labels.PodIp, node.PodIp!).ConfigureAwait(false);
        await HostProbe(Labels.PodDns, node.PodDnsName).ConfigureAwait(false);
        await HostProbe(Labels.ServiceDns, node.ServiceDnsName).ConfigureAwait(false);

        // A new ClusterIP is routed only once kube-proxy synced (4-6 s on OrbStack, from the host and from pods alike),
        // so it is polled until clusterIpWait after the Service was created
        async Task ClusterIpProbe(string label, string host)
        {
            var deadline = clusterIpWait ?? TimeSpan.FromSeconds(20);
            ProbeResult result;
            while (true)
            {
                result = await TcpProbe.ProbeAsync(host, EchoNode.Port, timeout, EchoNode.Banner, cancellationToken)
                                       .ConfigureAwait(false);
                if (result.Succeeded || serviceWatch.Elapsed >= deadline)
                    break;

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }

            result = result with
            {
                Detail = $"{result.Detail ?? "no banner"}, {serviceWatch.Elapsed.TotalSeconds:F1} s after the Service "
                       + "was created"
            };
            report.Add(label, result);
            log?.Invoke($"[reach] {label,-28} {result}");
        }

        await ClusterIpProbe(Labels.ClusterIp, clusterIp).ConfigureAwait(false);
        await ClusterIpProbe(Labels.ClusterIpDns, clusterIpDns).ConfigureAwait(false);
        await HostProbe(Labels.OrbK8sDns, $"{service.Metadata.Name}.{run.Namespace}.k8s.orb.local")
           .ConfigureAwait(false);

        // Pod to host
        var nodeIp = await TryReadNodeInternalIpAsync(run.Client, run.Namespace, node.PodName, cancellationToken)
                        .ConfigureAwait(false);
        var candidates = HostEndpoints.Candidates(HostEndpoints.LocalPrivateIPv4Addresses(), nodeIp);
        await using var loopback = HostListener.Start(IPAddress.Loopback);
        await using var any = HostListener.Start(IPAddress.Any);
        string? viaLoopback = null;
        string? viaAny = null;
        foreach (var candidate in candidates)
        {
            foreach (var listener in new[] { loopback, any })
            {
                var isLoopback = ReferenceEquals(listener, loopback);
                var before = listener.Accepted.Count;
                var result = await PodProbe.ProbeAsync(run.Client, run.Namespace, node.PodName, node.ContainerName,
                                                       candidate, listener.Port, (int)timeout.TotalSeconds,
                                                       listener.Banner, cancellationToken)
                                           .ConfigureAwait(false);
                if (result.Succeeded && listener.Accepted.Skip(before).FirstOrDefault() is { } seen)
                    result = result with { Detail = $"listener saw {seen}" };
                var label = Labels.PodToHost(candidate, isLoopback);
                report.Add(label, result);
                log?.Invoke($"[reach] {label,-28} {result}");
                if (!result.Succeeded)
                    continue;
                if (isLoopback)
                    viaLoopback ??= candidate;
                else
                    viaAny ??= candidate;
            }
        }

        var hostReachesPods = report.Succeeded(Labels.PodIp) && report.Succeeded(Labels.ServiceDns);
        return new ReachabilityResult(report, hostReachesPods, viaLoopback ?? viaAny,
                                      viaLoopback is null && viaAny is not null);
    }

    private static async Task<ProbeResult> ProbeWithAttemptsAsync(string host, int port, TimeSpan timeout,
                                                                  int attempts, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await TcpProbe.ProbeAsync(host, port, timeout, EchoNode.Banner, cancellationToken)
                                       .ConfigureAwait(false);
            if (attempt > 1)
                result = result with { Detail = $"{result.Detail ?? "no banner"}, attempt {attempt}" };
            if (result.Succeeded || attempt >= attempts)
                return result;

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until the Service's EndpointSlice lists a ready endpoint; returns how long that took.</summary>
    private static async Task<TimeSpan> WaitForReadyEndpointAsync(IKubernetes client, string ns, string service,
                                                                  TimeSpan timeout,
                                                                  CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var slices = await client.DiscoveryV1.ListNamespacedEndpointSliceAsync(
                                         ns, labelSelector: $"kubernetes.io/service-name={service}",
                                         cancellationToken: cancellationToken)
                                     .ConfigureAwait(false);
            if (slices.Items.Any(s => s.Endpoints?.Any(e => e.Conditions?.Ready == true) == true))
                return watch.Elapsed;
            if (watch.Elapsed >= timeout)
                throw new TimeoutException($"Service {ns}/{service} has no ready endpoint after {timeout}");

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> TryReadNodeInternalIpAsync(IKubernetes client, string ns, string podName,
                                                                  CancellationToken cancellationToken)
    {
        try
        {
            var pod = await client.CoreV1.ReadNamespacedPodAsync(podName, ns, cancellationToken: cancellationToken)
                                  .ConfigureAwait(false);
            if (pod.Status?.HostIP is { Length: > 0 } hostIp)
                return hostIp;
        }
        catch (k8s.Autorest.HttpOperationException)
        {
            // Not allowed or gone: the candidate is skipped
        }

        return null;
    }
}