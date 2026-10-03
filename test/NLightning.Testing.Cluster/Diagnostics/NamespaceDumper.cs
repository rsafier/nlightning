using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace NLightning.Testing.Cluster.Diagnostics;

using Kube;

/// <summary>What one dump of a namespace wrote.</summary>
/// <param name="Directory">The namespace's dump folder.</param>
/// <param name="Files">The files written, relative to <paramref name="Directory"/>.</param>
/// <param name="Errors">What could not be collected (each step fails alone).</param>
/// <param name="Elapsed">How long the dump took.</param>
public sealed record DiagnosticsDump(string Directory, IReadOnlyList<string> Files, IReadOnlyList<string> Errors,
                                     TimeSpan Elapsed);

/// <summary>
/// Writes one namespace's diagnostics into a folder:
/// <list type="bullet">
///   <item><c>pods.txt</c> (one line per pod), <c>workloads.txt</c> (StatefulSets, Services, NetworkPolicies),
///   <c>events.txt</c> (oldest first), <c>storage.txt</c> (PVCs and their PVs);</item>
///   <item>per pod <c>pods/&lt;pod&gt;/describe.txt</c>, <c>&lt;container&gt;.log</c> and, after a restart,
///   <c>&lt;container&gt;.previous.log</c>, and the node's state <c>state/*.json</c>
///   (<see cref="NodeStateCommands"/>);</item>
///   <item><c>summary.txt</c>: what was written and what failed.</item>
/// </list>
/// Every step runs alone (a failed one is recorded, never thrown), everything written is masked
/// (<see cref="SecretRedactor"/>), and no secret file is ever read. Only reads: it never changes the cluster.
/// </summary>
public static class NamespaceDumper
{
    /// <summary>Dumps <paramref name="ns"/> into <paramref name="directory"/> (created).</summary>
    public static async Task<DiagnosticsDump> DumpAsync(IKubernetes client, string ns, string directory,
                                                        DiagnosticsSettings settings,
                                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(settings);

        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        var ct = timeout.Token;
        var writer = new DumpWriter(directory);

        IList<V1Pod> pods = [];
        await writer.StepAsync("pods.txt", async () =>
        {
            pods = (await client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct).ConfigureAwait(false)).Items;
            return pods.Count == 0
                       ? "no pods\n"
                       : string.Join('\n', pods.OrderBy(p => p.Metadata?.Name, StringComparer.Ordinal)
                                               .Select(PodStatusReader.Describe)) + "\n";
        }).ConfigureAwait(false);

        var namespaceSteps = new[]
        {
            writer.StepAsync("events.txt", async () =>
            {
                var events = await client.CoreV1.ListNamespacedEventAsync(ns, cancellationToken: ct)
                                         .ConfigureAwait(false);
                return ResourceDescriber.DescribeEvents(events.Items);
            }),
            writer.StepAsync("storage.txt", async () =>
            {
                var claims = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(ns, cancellationToken: ct)
                                         .ConfigureAwait(false);
                var volumes = new Dictionary<string, V1PersistentVolume>(StringComparer.Ordinal);
                foreach (var name in claims.Items.Select(c => c.Spec?.VolumeName).OfType<string>().Distinct())
                {
                    // Read (never list) only the volumes bound to this namespace's claims
                    try
                    {
                        volumes[name] = await client.CoreV1.ReadPersistentVolumeAsync(name, cancellationToken: ct)
                                                    .ConfigureAwait(false);
                    }
                    catch (HttpOperationException e) when (e.Response.StatusCode is HttpStatusCode.NotFound
                                                                                 or HttpStatusCode.Forbidden)
                    {
                        // Shown as "not readable"
                    }
                }

                return ResourceDescriber.DescribeStorage(claims.Items, volumes);
            }),
            writer.StepAsync("workloads.txt", async () =>
            {
                var sets = await client.AppsV1.ListNamespacedStatefulSetAsync(ns, cancellationToken: ct)
                                       .ConfigureAwait(false);
                var services = await client.CoreV1.ListNamespacedServiceAsync(ns, cancellationToken: ct)
                                           .ConfigureAwait(false);
                var policies = await client.NetworkingV1.ListNamespacedNetworkPolicyAsync(ns, cancellationToken: ct)
                                           .ConfigureAwait(false);
                return ResourceDescriber.DescribeWorkloads(sets.Items, services.Items, policies.Items);
            })
        };

        var podSteps = pods.Select(pod => DumpPodAsync(client, ns, pod, writer, settings, ct));
        await Task.WhenAll(namespaceSteps.Concat(podSteps)).ConfigureAwait(false);

        watch.Stop();
        writer.WriteSummary(ns, watch.Elapsed, ct.IsCancellationRequested);
        return new DiagnosticsDump(directory, writer.Files, writer.Errors, watch.Elapsed);
    }

    private static async Task DumpPodAsync(IKubernetes client, string ns, V1Pod pod, DumpWriter writer,
                                           DiagnosticsSettings settings, CancellationToken ct)
    {
        var name = pod.Metadata?.Name ?? "unnamed";
        var folder = $"pods/{DumpWriter.SafeName(name)}";
        await writer.StepAsync($"{folder}/describe.txt", () => Task.FromResult(ResourceDescriber.DescribePod(pod)))
                    .ConfigureAwait(false);

        var statuses = (pod.Status?.InitContainerStatuses ?? []).Concat(pod.Status?.ContainerStatuses ?? []).ToList();
        var containers = (pod.Spec?.InitContainers ?? []).Concat(pod.Spec?.Containers ?? []).Select(c => c.Name);
        var logs = new List<Task>();
        foreach (var container in containers)
        {
            var status = statuses.FirstOrDefault(s => s.Name == container);
            var started = status?.State?.Running is not null || status?.State?.Terminated is not null
                       || status?.LastState?.Terminated is not null;
            if (!started)
            {
                writer.Note($"{folder}/{container}: no log (never started: "
                          + $"{ResourceDescriber.DescribeState(status?.State)})");
                continue;
            }

            logs.Add(writer.StepAsync($"{folder}/{DumpWriter.SafeName(container)}.log",
                                      () => ReadLogAsync(client, ns, name, container, false, settings, ct)));
            if (status?.RestartCount > 0 || status?.LastState?.Terminated is not null)
                logs.Add(writer.StepAsync($"{folder}/{DumpWriter.SafeName(container)}.previous.log",
                                          () => ReadLogAsync(client, ns, name, container, true, settings, ct)));
        }

        await Task.WhenAll(logs).ConfigureAwait(false);

        var commands = NodeStateCommands.For(pod);
        var stateContainer = NodeStateCommands.ContainerOf(pod);
        var running = statuses.FirstOrDefault(s => s.Name == stateContainer)?.State?.Running is not null;
        if (commands.Count == 0 || stateContainer is null)
            return;
        if (!running)
        {
            writer.Note($"{folder}/state: skipped, container {stateContainer} is not running");
            return;
        }

        // One at a time: these are the node's own CLIs, and a stuck node should not get four at once
        foreach (var command in commands)
            await writer.StepAsync($"{folder}/state/{command.FileName}", async () =>
            {
                using var execTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                execTimeout.CancelAfter(settings.ExecTimeout);
                var result = await client.ExecAsync(ns, name, stateContainer, command.Command, execTimeout.Token)
                                         .ConfigureAwait(false);
                return result.Succeeded
                           ? result.StdOutText
                           : $"exit {result.ExitCode}\n--- stdout\n{result.StdOutText}\n--- stderr\n{result.StdErrText}";
            }).ConfigureAwait(false);
    }

    private static async Task<string> ReadLogAsync(IKubernetes client, string ns, string pod, string container,
                                                   bool previous, DiagnosticsSettings settings,
                                                   CancellationToken ct)
    {
        await using var stream = await client.CoreV1.ReadNamespacedPodLogAsync(
                                                 pod, ns, container, previous: previous, timestamps: true,
                                                 limitBytes: settings.MaxLogBytes, cancellationToken: ct)
                                             .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Writes the files of one dump and keeps the record of what worked.</summary>
    private sealed class DumpWriter(string directory)
    {
        private readonly ConcurrentQueue<string> _files = new();
        private readonly ConcurrentQueue<string> _errors = new();
        private readonly ConcurrentQueue<string> _notes = new();

        public IReadOnlyList<string> Files => [.. _files.Order(StringComparer.Ordinal)];

        public IReadOnlyList<string> Errors => [.. _errors];

        public static string SafeName(string name) => ClusterTestScope.ToLabel(name);

        public void Note(string note) => _notes.Enqueue(note);

        /// <summary>Runs one collection step and writes its text (masked) to <paramref name="file"/>; a failure is
        /// recorded in the summary and written to the file instead.</summary>
        public async Task StepAsync(string file, Func<Task<string>> collect)
        {
            string text;
            try
            {
                text = await collect().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                var message = e is OperationCanceledException ? "timed out or cancelled" : $"{e.GetType().Name}: {e.Message}";
                _errors.Enqueue($"{file}: {message}");
                text = $"not collected: {message}\n";
            }

            try
            {
                var path = Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, SecretRedactor.Redact(text), CancellationToken.None)
                          .ConfigureAwait(false);
                _files.Enqueue(file);
            }
            catch (Exception e)
            {
                _errors.Enqueue($"{file}: not written: {e.Message}");
            }
        }

        public void WriteSummary(string ns, TimeSpan elapsed, bool timedOut)
        {
            var b = new StringBuilder();
            b.AppendLine($"namespace {ns}, dumped in {elapsed.TotalSeconds:F1} s at {DateTimeOffset.UtcNow:O}"
                       + (timedOut ? " (stopped by its timeout)" : string.Empty));
            b.AppendLine($"files ({_files.Count}):");
            foreach (var file in Files)
                b.AppendLine($"  {file}");
            foreach (var note in _notes.Order(StringComparer.Ordinal))
                b.AppendLine($"note: {note}");
            foreach (var error in _errors)
                b.AppendLine($"error: {error}");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "summary.txt"), SecretRedactor.Redact(b.ToString()));
        }
    }
}