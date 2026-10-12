using System.Diagnostics;
using k8s;

namespace NLightning.Testing.Cluster.Reach;

using Kube;

/// <summary>
/// A TCP connect from inside a pod, with busybox <c>nc</c> (the echo node of <see cref="EchoNode"/> has it): the pod
/// dials <c>host:port</c> and prints the first line the listener sends.
/// </summary>
public static class PodProbe
{
    /// <summary>The command a pod runs to dial <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static IReadOnlyList<string> Command(string host, int port, int timeoutSeconds) =>
    [
        "sh", "-c", "echo probe | nc -w \"$2\" \"$0\" \"$1\"", host,
        port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
    ];

    /// <summary>
    /// Dials from the pod. Succeeds when <c>nc</c> exits 0 and its output contains <paramref name="expectedBanner"/>.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(IKubernetes client, string ns, string podName, string container,
                                                     string host, int port, int timeoutSeconds,
                                                     string expectedBanner, CancellationToken cancellationToken)
    {
        var target = $"{host}:{port}";
        var watch = Stopwatch.StartNew();
        var result = await client.ExecAsync(ns, podName, container, Command(host, port, timeoutSeconds),
                                            cancellationToken)
                                 .ConfigureAwait(false);
        var output = result.StdOutText.Trim();
        if (result.Succeeded && output.Contains(expectedBanner, StringComparison.Ordinal))
            return new ProbeResult(ProbeDirection.PodToHost, target, true, watch.Elapsed, null);

        var error = result.StdErrText.Trim();
        return ProbeResult.Failed(ProbeDirection.PodToHost, target, watch.Elapsed,
                                  $"nc exit {result.ExitCode}"
                                + (error.Length > 0 ? $": {error}" : "")
                                + (output.Length > 0 ? $" (output '{output}')" : ""));
    }
}