using System.Collections.Concurrent;

namespace NLightning.Testing.Cluster.Diagnostics;

/// <summary>
/// A run's diagnostics bookkeeping: whether something failed in it (then <c>NLTG_KEEP_NAMESPACE=failure</c> keeps its
/// namespace) and the dumps already written, by label, so one failure seen by several hooks (a <see cref="Poll"/>
/// timeout inside a topology build inside a failed test) is dumped once and its other reasons appended.
/// </summary>
public sealed class RunDiagnosticsState
{
    private readonly ConcurrentDictionary<string, string> _dumps = new(StringComparer.Ordinal);
    private string? _failure;

    /// <summary>Whether a failure was recorded (<see cref="MarkFailed"/>).</summary>
    public bool Failed => Volatile.Read(ref _failure) is not null;

    /// <summary>The first failure recorded, or null.</summary>
    public string? FailureReason => Volatile.Read(ref _failure);

    /// <summary>The dump folders written so far, by label.</summary>
    public IReadOnlyDictionary<string, string> Dumps => _dumps;

    /// <summary>One dump at a time per run (hooks can fire together).</summary>
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Records a failure; the first reason is kept.</summary>
    public void MarkFailed(string reason) =>
        Interlocked.CompareExchange(ref _failure, string.IsNullOrWhiteSpace(reason) ? "failed" : reason, null);

    internal bool TryGetDump(string label, out string directory) => _dumps.TryGetValue(label, out directory!);

    internal void AddDump(string label, string directory) => _dumps.TryAdd(label, directory);
}