using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace NLightning.Testing.Cluster.Run;

/// <summary>What the reaper decided for one run namespace.</summary>
public enum ReapVerdict
{
    /// <summary>Its owner is alive (or unknown) and it is within its TTL.</summary>
    Keep,

    /// <summary>Already being deleted.</summary>
    Terminating,

    /// <summary>Older than its TTL.</summary>
    Expired,

    /// <summary>Its owner process on this host is gone (or its pid was reused).</summary>
    OwnerGone,

    /// <summary>Selected by <see cref="ReaperOptions.RunFilter"/> with <see cref="ReaperOptions.Force"/>.</summary>
    Forced
}

/// <summary>One run namespace as the reaper sees it.</summary>
public sealed record ReapCandidate(string Namespace, string RunId, string? Suite, DateTimeOffset? StartedAt,
                                   TimeSpan? Age, TimeSpan Ttl, RunOwner? Owner, bool Kept, ReapVerdict Verdict,
                                   string Reason)
{
    /// <summary>Whether the reaper deletes it.</summary>
    public bool ShouldReap => Verdict is ReapVerdict.Expired or ReapVerdict.OwnerGone or ReapVerdict.Forced;
}

/// <summary>The outcome of reaping one candidate.</summary>
public sealed record ReapOutcome(ReapCandidate Candidate, bool Deleted, string? Error);

/// <summary>
/// The reaper (plan R3): deletes the namespaces of runs whose owner process is gone or which are older than their TTL.
/// It only ever looks at namespaces named <c>&lt;prefix&gt;-&lt;run&gt;</c> that carry <see cref="RunLabels.Run"/> =
/// <c>&lt;run&gt;</c> and the harness's managed-by label (and, by default, <c>nltg.spike=true</c>), and re-checks
/// that right before each delete. An owner on another host is never judged gone: only the TTL reaps those runs.
/// </summary>
public static class RunReaper
{
    /// <summary>The label selector of the namespaces the reaper lists.</summary>
    public static string Selector(ReaperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var selector = $"{RunLabels.ManagedBy}={RunLabels.ManagedByValue},{RunLabels.Run}";
        return options.RequireSpikeLabel ? $"{selector},{RunLabels.Spike}=true" : selector;
    }

    /// <summary>
    /// Whether <paramref name="ns"/> is a harness run namespace under <paramref name="prefix"/>: named
    /// <c>&lt;prefix&gt;-&lt;run label&gt;</c>, managed by the harness and, with <paramref name="requireSpike"/>,
    /// labelled <c>nltg.spike=true</c>.
    /// </summary>
    public static bool IsHarnessNamespace(V1Namespace ns, string prefix, bool requireSpike)
    {
        ArgumentNullException.ThrowIfNull(ns);
        var labels = ns.Metadata?.Labels;
        if (ns.Metadata?.Name is not { } name || labels is null
         || !labels.TryGetValue(RunLabels.Run, out var run) || string.IsNullOrEmpty(run)
         || name != $"{prefix}-{run}"
         || !RunNamespace.IsOwnedBy(ns, run, prefix))
            return false;

        return !requireSpike || (labels.TryGetValue(RunLabels.Spike, out var spike) && spike == "true");
    }

    /// <summary>
    /// Whether run <paramref name="runId"/> matches <paramref name="filter"/>: the same id, or one of the ids a process
    /// derives from it (<c>&lt;filter&gt;-&lt;n&gt;</c>, see <see cref="TestRun"/>).
    /// </summary>
    public static bool MatchesRun(string runId, string filter) =>
        runId == filter || runId.StartsWith(filter + "-", StringComparison.Ordinal);

    /// <summary>
    /// The reaper's view of <paramref name="ns"/> at <paramref name="now"/>, or null when it is not a harness run
    /// namespace under the options' prefix or not selected by <see cref="ReaperOptions.RunFilter"/>.
    /// </summary>
    public static ReapCandidate? Evaluate(V1Namespace ns, ReaperOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (!IsHarnessNamespace(ns, options.Prefix, options.RequireSpikeLabel))
            return null;

        var labels = ns.Metadata.Labels;
        var runId = labels[RunLabels.Run];
        if (options.RunFilter is { } filter && !MatchesRun(runId, filter))
            return null;

        var startedAt = RunAnnotations.GetStartedAt(ns);
        var age = startedAt is { } started ? now - started : (TimeSpan?)null;
        var ttl = RunAnnotations.GetTtl(ns) ?? options.Ttl;
        var owner = RunOwner.FromNamespace(ns);
        var kept = RunAnnotations.IsKept(ns);
        var suite = labels.TryGetValue(RunLabels.Suite, out var s) ? s : null;

        ReapCandidate Candidate(ReapVerdict verdict, string reason) =>
            new(ns.Metadata.Name, runId, suite, startedAt, age, ttl, owner, kept, verdict, reason);

        if (ns.Metadata.DeletionTimestamp is not null || ns.Status?.Phase == "Terminating")
            return Candidate(ReapVerdict.Terminating, "already terminating");
        if (options.RunFilter is not null && options.Force)
            return Candidate(ReapVerdict.Forced, $"run matches {options.RunFilter} (forced)");
        if (age is { } a && a > ttl)
            return Candidate(ReapVerdict.Expired, $"older than its TTL ({Format(a)} > {Format(ttl)})");
        if (owner is null)
            return Candidate(ReapVerdict.Keep, "no owner recorded; TTL only");
        if (kept)
            return Candidate(ReapVerdict.Keep, "kept (nltg.keep); TTL only");
        if (!owner.Host.Equals(options.LocalHost, StringComparison.OrdinalIgnoreCase))
            return Candidate(ReapVerdict.Keep, $"owner on another host ({owner.Host}); TTL only");

        return options.ProcessProbe.Probe(owner.Pid, owner.ProcessStartUnixMs) switch
        {
            OwnerProcessState.Gone => Candidate(ReapVerdict.OwnerGone, $"owner process {owner.Pid} is gone"),
            OwnerProcessState.PidReused => Candidate(ReapVerdict.OwnerGone,
                                                     $"owner process {owner.Pid} is gone (pid reused)"),
            _ => Candidate(ReapVerdict.Keep, $"owner process {owner.Pid} is alive")
        };
    }

    /// <summary>The run namespaces under the options' prefix, oldest first, with the reaper's verdict.</summary>
    public static async Task<IReadOnlyList<ReapCandidate>> ListAsync(IKubernetes client, ReaperOptions options,
                                                                     CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var list = await client.CoreV1.ListNamespaceAsync(labelSelector: Selector(options),
                                                          cancellationToken: cancellationToken)
                               .ConfigureAwait(false);
        var now = options.Now ?? DateTimeOffset.UtcNow;
        return list.Items
                   .Select(ns => Evaluate(ns, options, now))
                   .OfType<ReapCandidate>()
                   .OrderBy(c => c.StartedAt ?? DateTimeOffset.MaxValue)
                   .ThenBy(c => c.Namespace, StringComparer.Ordinal)
                   .ToList();
    }

    /// <summary>
    /// Deletes every candidate of <see cref="ListAsync"/> that <see cref="ReapCandidate.ShouldReap"/> (none with
    /// <see cref="ReaperOptions.DryRun"/>), each after re-reading it and checking it again; with
    /// <see cref="ReaperOptions.WaitForDeletion"/> waits until they are gone.
    /// </summary>
    public static async Task<IReadOnlyList<ReapOutcome>> ReapAsync(IKubernetes client, ReaperOptions options,
                                                                   CancellationToken cancellationToken)
    {
        var candidates = await ListAsync(client, options, cancellationToken).ConfigureAwait(false);
        var outcomes = new List<ReapOutcome>();
        foreach (var candidate in candidates.Where(c => c.ShouldReap))
        {
            if (options.DryRun)
            {
                outcomes.Add(new ReapOutcome(candidate, false, null));
                continue;
            }

            outcomes.Add(await DeleteAsync(client, candidate, options, cancellationToken).ConfigureAwait(false));
        }

        if (options.WaitForDeletion)
            foreach (var outcome in outcomes.Where(o => o.Deleted))
                await RunNamespace.WaitForDeletionAsync(client, outcome.Candidate.Namespace, options.DeletionTimeout,
                                                        cancellationToken).ConfigureAwait(false);

        return outcomes;
    }

    private static async Task<ReapOutcome> DeleteAsync(IKubernetes client, ReapCandidate candidate,
                                                       ReaperOptions options, CancellationToken cancellationToken)
    {
        // Re-read: the verdict must still hold for the namespace as it is now, not as it was listed.
        var ns = await RunNamespace.TryReadAsync(client, candidate.Namespace, cancellationToken).ConfigureAwait(false);
        if (ns is null)
            return new ReapOutcome(candidate, false, "already gone");

        var current = Evaluate(ns, options, options.Now ?? DateTimeOffset.UtcNow);
        if (current is null || !current.ShouldReap || current.RunId != candidate.RunId)
            return new ReapOutcome(candidate, false, "no longer reapable");

        try
        {
            await client.CoreV1.DeleteNamespaceAsync(candidate.Namespace, gracePeriodSeconds: 0,
                                                     propagationPolicy: "Background",
                                                     cancellationToken: cancellationToken).ConfigureAwait(false);
            options.Log?.Invoke($"[nltg-reaper] deleted {candidate.Namespace}: {candidate.Reason}");
            return new ReapOutcome(candidate, true, null);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ReapOutcome(candidate, false, "already gone");
        }
    }

    /// <summary>A short age: <c>45s</c>, <c>12m</c>, <c>3h05m</c>, <c>2d04h</c>.</summary>
    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span.TotalMinutes < 1)
            return $"{(int)span.TotalSeconds}s";
        if (span.TotalHours < 1)
            return $"{(int)span.TotalMinutes}m";
        return span.TotalDays < 1 ? $"{(int)span.TotalHours}h{span.Minutes:00}m" : $"{(int)span.TotalDays}d{span.Hours:00}h";
    }
}