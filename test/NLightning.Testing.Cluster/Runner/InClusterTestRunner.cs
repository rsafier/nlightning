using System.Diagnostics;
using System.Text;
using k8s;
using k8s.Models;

namespace NLightning.Testing.Cluster.Runner;

using Kube;
using Run;

/// <summary>
/// What an in-cluster test run returned.
/// </summary>
/// <param name="ExitCode">The test process's exit code (xunit v3: 0 when every selected test passed).</param>
/// <param name="Log">Everything the runner printed.</param>
/// <param name="PodName">The Job's pod.</param>
/// <param name="StartedAfter">From creating the Job to its container running.</param>
/// <param name="Elapsed">From creating the Job to its container's exit.</param>
public sealed record RunnerJobResult(int ExitCode, string Log, string PodName, TimeSpan StartedAfter,
                                     TimeSpan Elapsed)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs tests inside a run's namespace as a Job (plan R5, the fallback when the host cannot reach the pods or the
/// pods cannot reach the host): creates <see cref="RunnerRbac"/> and the <see cref="TestRunnerJob"/>, streams the
/// runner's log line by line while it runs, and returns its exit code.
/// </summary>
public static class InClusterTestRunner
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Runs <paramref name="job"/> in <paramref name="run"/>'s namespace and waits for it (at most its
    /// <see cref="TestRunnerJob.ActiveDeadline"/> plus a minute).
    /// </summary>
    /// <param name="run">The run whose namespace the runner adopts.</param>
    /// <param name="job">The Job (image, test arguments).</param>
    /// <param name="onLine">Called with each log line as it arrives, or null.</param>
    /// <param name="cancellationToken">Cancels the wait (the Job keeps running until the namespace goes).</param>
    /// <exception cref="InvalidOperationException">The pod cannot start (e.g. <c>ErrImageNeverPull</c>: build the
    /// image first).</exception>
    public static async Task<RunnerJobResult> RunAsync(TestRun run, TestRunnerJob job, Action<string>? onLine,
                                                       CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(job);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(job.ActiveDeadline + TimeSpan.FromMinutes(1));
        var ct = timeoutSource.Token;
        var client = run.Client;
        var ns = run.Namespace;

        await RunnerRbac.ApplyAsync(client, RunnerRbac.Build(run.Identity), ct).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        await client.BatchV1.CreateNamespacedJobAsync(job.Build(run.Identity), ns, cancellationToken: ct)
                    .ConfigureAwait(false);

        var pod = await WaitForStartAsync(client, ns, job.Name, ct).ConfigureAwait(false);
        var podName = pod.Metadata.Name;
        var startedAfter = watch.Elapsed;

        var log = new StringBuilder();
        await StreamLogAsync(client, ns, podName, job.Name, line =>
        {
            log.AppendLine(line);
            onLine?.Invoke(line);
        }, ct).ConfigureAwait(false);

        var exitCode = await WaitForExitCodeAsync(client, ns, podName, job.Name, ct).ConfigureAwait(false);
        return new RunnerJobResult(exitCode, log.ToString(), podName, startedAfter, watch.Elapsed);
    }

    /// <summary>The label selector of a Job's pods.</summary>
    public static string PodSelector(string jobName) => $"batch.kubernetes.io/job-name={jobName}";

    /// <summary>
    /// The exit code of the runner container once it terminated, or null while it has not.
    /// </summary>
    public static int? GetExitCode(V1Pod pod, string container) =>
        pod.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == container)?.State?.Terminated?.ExitCode;

    private static async Task<V1Pod> WaitForStartAsync(IKubernetes client, string ns, string jobName,
                                                       CancellationToken cancellationToken)
    {
        while (true)
        {
            var pods = await client.CoreV1.ListNamespacedPodAsync(ns, labelSelector: PodSelector(jobName),
                                                                  cancellationToken: cancellationToken)
                                   .ConfigureAwait(false);
            if (pods.Items.FirstOrDefault() is { } pod)
            {
                if (pod.Status?.Phase is "Running" or "Succeeded" or "Failed"
                 && GetFatalWaitingReason(pod) is null)
                    return pod;
                if (GetFatalWaitingReason(pod) is { } reason)
                    throw new InvalidOperationException(
                        $"Runner pod {ns}/{pod.Metadata.Name} cannot start: {reason}"
                      + (reason.Contains("ErrImageNeverPull", StringComparison.Ordinal)
                             ? $" (build {RunnerImage.Repository} with test/NLightning.Testing.Cluster/Runner/image/build.sh)"
                             : ""));
            }

            await Task.Delay(s_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? GetFatalWaitingReason(V1Pod pod)
    {
        foreach (var status in pod.Status?.ContainerStatuses ?? [])
            if (status.State?.Waiting?.Reason is { } reason
             && PodStatusReader.FatalWaitingReasons.Contains(reason))
                return $"{reason} {status.State.Waiting.Message}".Trim();

        return null;
    }

    private static async Task StreamLogAsync(IKubernetes client, string ns, string podName, string container,
                                             Action<string> onLine, CancellationToken cancellationToken)
    {
        await using var stream = await client.CoreV1.ReadNamespacedPodLogAsync(
                                                 podName, ns, container, follow: true,
                                                 cancellationToken: cancellationToken)
                                             .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            onLine(line);
    }

    private static async Task<int> WaitForExitCodeAsync(IKubernetes client, string ns, string podName,
                                                        string container, CancellationToken cancellationToken)
    {
        while (true)
        {
            var pod = await client.CoreV1.ReadNamespacedPodAsync(podName, ns, cancellationToken: cancellationToken)
                                  .ConfigureAwait(false);
            if (GetExitCode(pod, container) is { } exitCode)
                return exitCode;

            await Task.Delay(s_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}