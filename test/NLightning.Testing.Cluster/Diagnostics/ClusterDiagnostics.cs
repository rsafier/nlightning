using System.Collections.Concurrent;
using System.Text;

namespace NLightning.Testing.Cluster.Diagnostics;

using Run;

/// <summary>
/// Failure diagnostics of the harness: a dump of a run's namespace (<see cref="NamespaceDumper"/>) into
/// <c>&lt;root&gt;/&lt;test or fixture&gt;/&lt;namespace&gt;/</c>, where the root is <c>NLTG_CLUSTER_DIAG_DIR</c> or
/// <c>&lt;repo&gt;/TestResults/cluster/&lt;run id&gt;</c> (<see cref="DiagnosticsSettings"/>).
/// <para>When it dumps by itself (<see cref="DiagnosticsMode.Failure"/>, the default):</para>
/// <list type="bullet">
///   <item>a <see cref="Poll"/> times out: every live run of the current test (and its collection's fixtures);</item>
///   <item><see cref="TestRun.DeployAsync"/>'s readiness wait or <c>TopologyBuilder.BuildAsync</c> fails: that run;</item>
///   <item>a test fails (<see cref="ClusterDiagnosticsAttribute"/>): every run still alive that belongs to the test
///   or to its collection's fixtures. A run the test body already disposed is gone by then: wrap the body in
///   <see cref="CaptureOnFailureAsync{T}"/>, or keep the run in a fixture, to cover assertion failures too.</item>
/// </list>
/// <para><see cref="DiagnosticsMode.Always"/> also dumps after every test and before every run's deletion;
/// <see cref="DiagnosticsMode.Off"/> never by itself. A failure also marks the run failed, so
/// <c>NLTG_KEEP_NAMESPACE=failure</c> keeps its namespace (annotated <c>nltg.keep</c>: the reaper then waits for its TTL).
/// A dump never throws and never hides the failure that caused it.</para>
/// </summary>
public static class ClusterDiagnostics
{
    private static readonly ConcurrentDictionary<TestRun, byte> s_live = new();
    private static readonly AsyncLocal<int> s_pollCaptureSuppressed = new();

    /// <summary>The name of the file with the reasons of a failure dump, in the namespace's folder.</summary>
    public const string FailureFileName = "failure.txt";

    /// <summary>The runs alive in this process (started, not disposed).</summary>
    public static IReadOnlyCollection<TestRun> LiveRuns => [.. s_live.Keys];

    /// <summary>The live runs a failure in <paramref name="scope"/> concerns (<see cref="ClusterTestScope.Covers"/>).</summary>
    public static IReadOnlyList<TestRun> LiveRunsOf(ClusterTestScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return [.. s_live.Keys.Where(r => scope.Covers(r.Scope)).OrderBy(r => r.Namespace, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Dumps <paramref name="run"/>'s namespace now, whatever the mode, into
    /// <c>&lt;root&gt;/&lt;label&gt;/&lt;namespace&gt;</c> (label: the current test or fixture; a folder that exists
    /// gets <c>-2</c>, <c>-3</c>, ...), with <paramref name="reason"/> in <c>reason.txt</c>.
    /// </summary>
    public static async Task<DiagnosticsDump> DumpAsync(this TestRun run, string? label = null, string? reason = null,
                                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        label = label is null ? ClusterTestScope.Current().Label : ClusterTestScope.ToLabel(label);
        await run.Diagnostics.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DumpLockedAsync(run, label, reason is null ? null : ("reason.txt", reason), cancellationToken)
                      .ConfigureAwait(false);
        }
        finally
        {
            run.Diagnostics.Gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/>; when it throws, records the failure on <paramref name="run"/> (dumped unless the
    /// mode is <see cref="DiagnosticsMode.Off"/>) and rethrows the original exception.
    /// </summary>
    public static async Task<T> CaptureOnFailureAsync<T>(this TestRun run, string what, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            await CaptureFailureAsync(run, $"{what} failed: {Describe(e)}").ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc cref="CaptureOnFailureAsync{T}"/>
    public static Task CaptureOnFailureAsync(this TestRun run, string what, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return run.CaptureOnFailureAsync<bool>(what, async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>
    /// Marks <paramref name="run"/> failed with <paramref name="reason"/> and, unless the mode is
    /// <see cref="DiagnosticsMode.Off"/>, dumps it under the current test or fixture; when that label was dumped
    /// already the reason is appended to its <see cref="FailureFileName"/>. Never throws; returns the dump, or null.
    /// </summary>
    public static async Task<DiagnosticsDump?> CaptureFailureAsync(TestRun run, string reason,
                                                                   ClusterTestScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            run.Diagnostics.MarkFailed(reason);
            if (run.DiagnosticsSettings.Mode == DiagnosticsMode.Off)
                return null;

            var label = (scope ?? ClusterTestScope.Current()).Label;
            await run.Diagnostics.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (run.Diagnostics.TryGetDump(label, out var existing))
                {
                    await File.AppendAllTextAsync(Path.Combine(existing, FailureFileName),
                                                  FailureEntry(reason), CancellationToken.None)
                              .ConfigureAwait(false);
                    return null;
                }

                return await DumpLockedAsync(run, label, (FailureFileName, FailureEntry(reason)),
                                             CancellationToken.None)
                          .ConfigureAwait(false);
            }
            finally
            {
                run.Diagnostics.Gate.Release();
            }
        }
        catch (Exception e)
        {
            run.Log?.Invoke($"[nltg-diag] run {run.Id}: diagnostics not collected: {Describe(e)}");
            return null;
        }
    }

    /// <summary>
    /// Suppresses the dump on a <see cref="Poll"/> timeout for the calling flow until disposed (a test that expects a
    /// wait to time out).
    /// </summary>
    public static IDisposable SuppressPollCapture()
    {
        s_pollCaptureSuppressed.Value++;
        return new PollCaptureScope();
    }

    /// <summary>A <see cref="Poll"/> wait timed out: dumps the live runs of the current test or fixture.</summary>
    internal static async Task OnPollTimeoutAsync(string message)
    {
        if (s_pollCaptureSuppressed.Value > 0 || s_live.IsEmpty)
            return;

        var scope = ClusterTestScope.Current();
        foreach (var run in LiveRunsOf(scope))
            await CaptureFailureAsync(run, $"Poll timeout: {message}", scope).ConfigureAwait(false);
    }

    /// <summary>
    /// A test finished (<see cref="ClusterDiagnosticsAttribute"/>): on failure every live run of
    /// <paramref name="scope"/> is marked failed and dumped; with <see cref="DiagnosticsMode.Always"/> they are dumped
    /// anyway.
    /// </summary>
    internal static async Task OnTestFinishedAsync(ClusterTestScope scope, bool failed, string? failure)
    {
        foreach (var run in LiveRunsOf(scope))
        {
            if (failed)
                await CaptureFailureAsync(run, $"test failed: {failure ?? "(no message)"}", scope)
                   .ConfigureAwait(false);
            else if (run.DiagnosticsSettings.Mode == DiagnosticsMode.Always)
                await DumpIfNewAsync(run, scope.Label, "test passed (NLTG_CLUSTER_DIAG=always)").ConfigureAwait(false);
        }
    }

    /// <summary>Before a run's namespace goes: with <see cref="DiagnosticsMode.Always"/>, a dump unless this label has one.</summary>
    internal static Task BeforeDeletionAsync(TestRun run) =>
        run.DiagnosticsSettings.Mode == DiagnosticsMode.Always
            ? DumpIfNewAsync(run, ClusterTestScope.Current().Label, "run disposed (NLTG_CLUSTER_DIAG=always)")
            : Task.CompletedTask;

    internal static void Register(TestRun run) => s_live.TryAdd(run, 0);

    internal static void Unregister(TestRun run) => s_live.TryRemove(run, out _);

    internal static string Describe(Exception e)
    {
        var b = new StringBuilder($"{e.GetType().Name}: {e.Message}");
        for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
            b.Append($" ---> {inner.GetType().Name}: {inner.Message}");
        return b.ToString();
    }

    private static async Task DumpIfNewAsync(TestRun run, string label, string reason)
    {
        try
        {
            await run.Diagnostics.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!run.Diagnostics.TryGetDump(label, out _))
                    await DumpLockedAsync(run, label, ("reason.txt", reason), CancellationToken.None)
                       .ConfigureAwait(false);
            }
            finally
            {
                run.Diagnostics.Gate.Release();
            }
        }
        catch (Exception e)
        {
            run.Log?.Invoke($"[nltg-diag] run {run.Id}: diagnostics not collected: {Describe(e)}");
        }
    }

    private static async Task<DiagnosticsDump> DumpLockedAsync(TestRun run, string label, (string File, string Text)? note,
                                                               CancellationToken cancellationToken)
    {
        var root = run.DiagnosticsSettings.ResolveRoot(run.Id);
        var directory = UniqueDirectory(Path.Combine(root, label, run.Namespace));
        Directory.CreateDirectory(directory);
        if (note is { } n)
            await File.WriteAllTextAsync(Path.Combine(directory, n.File), SecretRedactor.Redact(n.Text),
                                         CancellationToken.None)
                      .ConfigureAwait(false);

        run.Diagnostics.AddDump(label, directory);
        var dump = await NamespaceDumper.DumpAsync(run.Client, run.Namespace, directory, run.DiagnosticsSettings,
                                                   cancellationToken)
                                        .ConfigureAwait(false);
        run.Log?.Invoke($"[nltg-diag] run {run.Id}: diagnostics of {run.Namespace} in {directory} "
                      + $"({dump.Files.Count} files, {dump.Errors.Count} errors, {dump.Elapsed.TotalSeconds:F1} s)");
        return dump;
    }

    private static string UniqueDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return directory;

        for (var n = 2; ; n++)
        {
            var candidate = $"{directory}-{n}";
            if (!Directory.Exists(candidate))
                return candidate;
        }
    }

    private static string FailureEntry(string reason) => $"{DateTimeOffset.UtcNow:O} {reason}\n";

    private sealed class PollCaptureScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                s_pollCaptureSuppressed.Value--;
        }
    }
}