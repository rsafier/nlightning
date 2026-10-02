using System.Globalization;
using System.Text.Json;
using k8s;
using k8s.Models;

namespace NLightning.Testing.Cluster.Run;

/// <summary>
/// The annotations of a run's namespace besides its <see cref="RunOwner"/>: what the reaper reads to decide.
/// </summary>
public static class RunAnnotations
{
    /// <summary><c>true</c> when the run asked to keep its namespace (<c>NLTG_KEEP_NAMESPACE</c>): the reaper then
    /// leaves it alone after its process ends, until its TTL.</summary>
    public const string Keep = "nltg.keep";

    /// <summary>The run's own TTL in seconds, instead of the reaper's.</summary>
    public const string TtlSeconds = "nltg.ttl-seconds";

    /// <summary>The annotations of a run's namespace for <paramref name="options"/> and <paramref name="owner"/>.</summary>
    public static Dictionary<string, string> ForRun(TestRunOptions options, RunOwner? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        var annotations = owner?.ToAnnotations() ?? new Dictionary<string, string>();
        if (options.KeepNamespace)
            annotations[Keep] = "true";
        if (options.Ttl is { } ttl)
            annotations[TtlSeconds] = ((long)Math.Ceiling(ttl.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        return annotations;
    }

    /// <summary>The merge patch that marks a namespace <see cref="Keep"/>.</summary>
    public static string KeepPatch() =>
        JsonSerializer.Serialize(new { metadata = new { annotations = new Dictionary<string, string> { [Keep] = "true" } } });

    /// <summary>
    /// Marks the run's namespace <see cref="Keep"/> after the fact (a run kept because it failed), after checking that it
    /// is the run's own.
    /// </summary>
    /// <exception cref="InvalidOperationException">The namespace is missing or not the run's.</exception>
    public static async Task MarkKeptAsync(IKubernetes client, RunIdentity run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(run);
        var ns = await RunNamespace.TryReadAsync(client, run.Namespace, cancellationToken).ConfigureAwait(false);
        if (ns is null || !RunNamespace.IsOwnedBy(ns, run.Id, run.NamespacePrefix))
            throw new InvalidOperationException($"Namespace {run.Namespace} is not run {run.Id}'s own");

        await client.CoreV1.PatchNamespaceAsync(new V1Patch(KeepPatch(), V1Patch.PatchType.MergePatch), run.Namespace,
                                                cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="ns"/> is marked <see cref="Keep"/>.</summary>
    public static bool IsKept(V1Namespace ns) =>
        ns.Metadata?.Annotations is { } annotations
     && annotations.TryGetValue(Keep, out var keep)
     && keep.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The run's own TTL from <paramref name="ns"/>, or null.</summary>
    public static TimeSpan? GetTtl(V1Namespace ns) =>
        ns.Metadata?.Annotations is { } annotations
     && annotations.TryGetValue(TtlSeconds, out var text)
     && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    /// <summary>
    /// When the run started: its <see cref="RunLabels.Started"/> label, else the namespace's creation time, else null.
    /// </summary>
    public static DateTimeOffset? GetStartedAt(V1Namespace ns)
    {
        if (ns.Metadata?.Labels is { } labels
         && labels.TryGetValue(RunLabels.Started, out var text)
         && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);

        return ns.Metadata?.CreationTimestamp is { } created ? ToUtc(created) : null;
    }

    /// <summary><paramref name="time"/> as a UTC offset (the client returns UTC or local times; unspecified is UTC).</summary>
    internal static DateTimeOffset ToUtc(DateTime time) =>
        new(time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc));
}