using System.Globalization;
using System.Text;

namespace NLightning.Testing.Cluster.Run;

using Kube;
using Nodes;

/// <summary>
/// The labels every object of a run carries (plan R1, R3). Cleanup and the reaper select on them, never on names.
/// </summary>
public static class RunLabels
{
    /// <summary>The run id; on the namespace and on every object in it.</summary>
    public const string Run = "nltg.run";

    /// <summary>The suite (test collection) that started the run.</summary>
    public const string Suite = "nltg.suite";

    /// <summary>When the run started, in Unix seconds (the reaper's TTL compares it).</summary>
    public const string Started = "nltg.started";

    /// <summary><c>true</c> on the spike's objects only.</summary>
    public const string Spike = "nltg.spike";

    /// <summary>The node's plain alias in its run (<c>alice</c>, <c>miner</c>, <c>cln</c>).</summary>
    public const string Node = "nltg.node";

    /// <summary>The <see cref="NodeKind"/> of a node, in lower case.</summary>
    public const string Kind = "nltg.kind";

    /// <summary>The standard managed-by label.</summary>
    public const string ManagedBy = "app.kubernetes.io/managed-by";

    /// <summary>The value of <see cref="ManagedBy"/>.</summary>
    public const string ManagedByValue = "nltg-test-harness";

    /// <summary>
    /// The labels of a run's namespace and of every object in it.
    /// </summary>
    public static Dictionary<string, string> ForRun(string runId, string suite, DateTimeOffset startedAt, bool spike)
    {
        var labels = new Dictionary<string, string>
        {
            [Run] = runId,
            [Suite] = SanitizeValue(suite),
            [Started] = startedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            [ManagedBy] = ManagedByValue
        };
        if (spike)
            labels[Spike] = "true";

        return labels;
    }

    /// <summary>
    /// The labels of one node's objects: the run's plus <see cref="Node"/> and <see cref="Kind"/>.
    /// </summary>
    public static Dictionary<string, string> ForNode(IReadOnlyDictionary<string, string> runLabels, string nodeName,
                                                     NodeKind kind)
    {
        var labels = new Dictionary<string, string>(runLabels)
        {
            [Node] = nodeName,
            [Kind] = KindValue(kind)
        };
        return labels;
    }

    /// <summary>
    /// The immutable selector of one node's StatefulSet and Service: its run and its alias.
    /// </summary>
    public static Dictionary<string, string> NodeSelector(string runId, string nodeName) =>
        new() { [Run] = runId, [Node] = nodeName };

    /// <summary>The <see cref="Kind"/> label value of <paramref name="kind"/>.</summary>
    public static string KindValue(NodeKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>
    /// A label selector string (<c>a=b,c=d</c>, keys in ordinal order) for list and delete calls.
    /// </summary>
    public static string ToSelector(IEnumerable<KeyValuePair<string, string>> labels) =>
        string.Join(',', labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}"));

    /// <summary>
    /// Makes <paramref name="value"/> a valid label value: characters outside <c>[A-Za-z0-9-_.]</c> become '-', the
    /// ends are trimmed to alphanumerics and the length is capped at 63. Empty input stays empty.
    /// </summary>
    public static string SanitizeValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');

        var sanitized = builder.ToString();
        if (sanitized.Length > KubeNames.MaxLabelValueLength)
            sanitized = sanitized[..KubeNames.MaxLabelValueLength];

        return sanitized.Trim('-', '_', '.');
    }
}