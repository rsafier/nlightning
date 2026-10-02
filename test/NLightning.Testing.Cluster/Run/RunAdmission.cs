using k8s;
using k8s.Models;

namespace NLightning.Testing.Cluster.Run;

/// <summary>
/// The cluster-wide cap on concurrent runs (plan R4: the runner only caps how many runs it starts; the quota and the
/// scheduler do the rest). The cluster itself is the semaphore: every process counts the live run namespaces under
/// the prefix, whoever created them. Before creating its namespace a run waits until fewer than the cap exist; after
/// creating it, it ranks the live namespaces by creation time (then name, so every process computes the same order)
/// and, if two processes raced past the cap, the later one deletes its namespace and waits again.
/// </summary>
public static class RunAdmission
{
    /// <summary>The cap; <c>0</c> or <c>off</c> turns it off.</summary>
    public const string MaxRunsVariable = "NLTG_MAX_CONCURRENT_RUNS";

    /// <summary>The default cap (the spike's rule: at most 6 spike namespaces at once).</summary>
    public const int DefaultMaxRuns = 6;

    /// <summary>How often a waiting run looks again.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>How often a waiting run asks the reaper to clear orphaned runs that hold slots.</summary>
    public static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The cap from <see cref="MaxRunsVariable"/>: unset is <see cref="DefaultMaxRuns"/>, <c>0</c>/<c>off</c> is
    /// none (null).
    /// </summary>
    /// <exception cref="ArgumentException">The value is neither a non-negative number nor <c>off</c>.</exception>
    public static int? ParseMaxRuns(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DefaultMaxRuns;
        if (value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!int.TryParse(value.Trim(), System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out var max))
            throw new ArgumentException($"{MaxRunsVariable}='{value}' is not a number or 'off'", nameof(value));

        return max == 0 ? null : max;
    }

    /// <summary>
    /// Whether <paramref name="ns"/> holds a run slot under <paramref name="prefix"/>: a namespace named
    /// <c>&lt;prefix&gt;-*</c> (longer prefixes included) with a run label and the harness's managed-by label,
    /// whatever its lane or spike label, terminating ones included (they still hold their pods). Wider than the
    /// reaper's test on purpose: counting too many only makes a run wait.
    /// </summary>
    public static bool HoldsSlot(V1Namespace ns, string prefix)
    {
        ArgumentNullException.ThrowIfNull(ns);
        var labels = ns.Metadata?.Labels;
        return ns.Metadata?.Name is { } name
            && name.StartsWith(prefix + "-", StringComparison.Ordinal)
            && labels is not null
            && labels.TryGetValue(RunLabels.Run, out var run) && !string.IsNullOrEmpty(run)
            && labels.TryGetValue(RunLabels.ManagedBy, out var managedBy) && managedBy == RunLabels.ManagedByValue;
    }

    /// <summary>
    /// The 0-based place of <paramref name="name"/> among <paramref name="live"/> ordered by creation time, then name;
    /// -1 when it is not among them.
    /// </summary>
    public static int Rank(IEnumerable<V1Namespace> live, string name)
    {
        ArgumentNullException.ThrowIfNull(live);
        var ordered = live.OrderBy(ns => ns.Metadata.CreationTimestamp is { } t
                                             ? RunAnnotations.ToUtc(t)
                                             : DateTimeOffset.MaxValue)
                          .ThenBy(ns => ns.Metadata.Name, StringComparer.Ordinal)
                          .Select(ns => ns.Metadata.Name)
                          .ToList();
        return ordered.IndexOf(name);
    }

    /// <summary>The live run namespaces under <paramref name="prefix"/>.</summary>
    public static async Task<IReadOnlyList<V1Namespace>> ListLiveAsync(IKubernetes client, string prefix,
                                                                       CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var list = await client.CoreV1.ListNamespaceAsync(
                                    labelSelector: $"{RunLabels.ManagedBy}={RunLabels.ManagedByValue},{RunLabels.Run}",
                                    cancellationToken: cancellationToken)
                               .ConfigureAwait(false);
        return list.Items.Where(ns => HoldsSlot(ns, prefix)).ToList();
    }

    /// <summary>
    /// Waits until fewer than <paramref name="maxRuns"/> run namespaces live under <paramref name="prefix"/>,
    /// letting the reaper clear runs whose owner is gone every <see cref="ReapInterval"/> meanwhile.
    /// </summary>
    /// <exception cref="TimeoutException">Still full after <paramref name="timeout"/>.</exception>
    public static async Task WaitForCapacityAsync(IKubernetes client, string prefix, int maxRuns, TimeSpan timeout,
                                                  Action<string>? log, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRuns);
        var deadline = DateTime.UtcNow + timeout;
        var nextReap = DateTime.UtcNow;
        var logged = false;
        while (true)
        {
            var live = await ListLiveAsync(client, prefix, cancellationToken).ConfigureAwait(false);
            if (live.Count < maxRuns)
                return;

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"{live.Count} runs under {prefix} still hold the {maxRuns} slots after {timeout}: "
                  + string.Join(", ", live.Select(ns => ns.Metadata.Name)));

            if (!logged)
            {
                log?.Invoke($"[nltg-cluster] {live.Count}/{maxRuns} runs under {prefix}; waiting for a slot");
                logged = true;
            }

            if (DateTime.UtcNow >= nextReap)
            {
                nextReap = DateTime.UtcNow + ReapInterval;
                await RunReaper.ReapAsync(client, new ReaperOptions { Prefix = prefix, RequireSpikeLabel = false, Log = log },
                                          cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(PollInterval + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)), cancellationToken)
                      .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether <paramref name="run"/>'s (created) namespace is within the first <paramref name="maxRuns"/> live run
    /// namespaces of its prefix.
    /// </summary>
    public static async Task<bool> IsAdmittedAsync(IKubernetes client, RunIdentity run, int maxRuns,
                                                   CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var live = await ListLiveAsync(client, run.NamespacePrefix, cancellationToken).ConfigureAwait(false);
        var rank = Rank(live, run.Namespace);
        return rank >= 0 && rank < maxRuns;
    }
}