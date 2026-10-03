using System.Diagnostics;

namespace NLightning.Testing.Cluster.Tests.Live.Faults;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Run;

/// <summary>
/// Workloads and helpers of the fault tests against a real cluster.
/// </summary>
internal static class FaultClusterTestSupport
{
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);

    public const int ProbePort = 9000;
    public const int StreamPort = 9001;

    /// <summary>
    /// A busybox node with a PVC at <c>/data</c>: it logs <c>START</c> and (on SIGTERM) <c>TERM</c> lines to
    /// <c>/data/events</c>, increments <c>/data/counter</c> every 0.2 s, appends what connects to
    /// <see cref="ProbePort"/> to <c>/data/recv</c> and what streams into <see cref="StreamPort"/> to
    /// <c>/data/stream</c>, and is ready once <c>/data/ready</c> exists.
    /// </summary>
    public static NodeWorkload BusyboxNode(string name) =>
        new(name, NodeKind.Other, ImageVersions.Busybox)
        {
            Command =
            [
                "sh", "-c",
                """
                rm -f /data/ready
                trap 'echo TERM $(date +%s) >> /data/events; exit 0' TERM
                echo START $(date +%s) >> /data/events
                (while true; do nc -l -p 9000 >> /data/recv 2>/dev/null; done) &
                (while true; do nc -l -p 9001 >> /data/stream 2>/dev/null; done) &
                touch /data/ready
                i=0
                while true; do i=$((i + 1)); echo $i > /data/counter; sleep 0.2; done
                """
            ],
            Resources = WorkloadResources.Tiny,
            Data = new DataVolume("/data", "64Mi"),
            ReadinessProbe = Probes.Exec(["test", "-f", "/data/ready"], periodSeconds: 1),
            TerminationGracePeriodSeconds = 5
        };

    public static TestRunOptions Options(string suite) =>
        TestRunOptions.FromEnvironment(suite) with
        {
            Quota = NamespaceQuota.Spike,
            Log = Log
        };

    public static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    public static async Task<string> ShAsync(INodeHandle node, string script, CancellationToken ct) =>
        (await node.ExecAsync(["sh", "-c", script], ct)).EnsureSuccess($"{node}: {script}").StdOutText.Trim();

    /// <summary>Opens a new TCP connection from <paramref name="from"/> to <paramref name="host"/>:9000 and sends a line.</summary>
    public static async Task<bool> CanConnectAsync(INodeHandle from, string host, string tag, CancellationToken ct)
    {
        var result = await from.ExecAsync(
                         ["sh", "-c", $"echo {tag} | nc -w 2 {host} {ProbePort}"], ct);
        return result.Succeeded;
    }

    /// <summary>Polls <paramref name="condition"/> until it holds; returns how long that took.</summary>
    public static async Task<TimeSpan> EventuallyAsync(Func<Task<bool>> condition, TimeSpan timeout, string what,
                                                       CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > timeout)
                throw new TimeoutException($"{what} not true after {timeout}");
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        return watch.Elapsed;
    }

    /// <summary>The <c>Address:</c> values of a busybox <c>nslookup</c> (the server's own line has a port).</summary>
    public static async Task<IReadOnlyList<string>> LookupAsync(INodeHandle from, string name, CancellationToken ct)
    {
        var result = await from.ExecAsync(["nslookup", name], ct);
        return result.StdOutText.Split('\n')
                     .Where(l => l.StartsWith("Address:", StringComparison.Ordinal))
                     .Select(l => l["Address:".Length..].Trim())
                     .Where(a => !a.Contains(':', StringComparison.Ordinal) || !a.Contains('.', StringComparison.Ordinal))
                     .ToList();
    }

    public static async Task<long> CounterAsync(INodeHandle node, CancellationToken ct) =>
        long.Parse(await ShAsync(node, "cat /data/counter", ct), System.Globalization.CultureInfo.InvariantCulture);
}