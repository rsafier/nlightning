using System.Globalization;
using System.Text;

namespace NLightning.Testing.Cluster.Reach;

/// <summary>Which way a reachability probe goes.</summary>
public enum ProbeDirection
{
    /// <summary>The test process on the host connects to a pod or Service.</summary>
    HostToPod,

    /// <summary>A pod connects to a listener in the test process (a peer dialling back to our in-process node).</summary>
    PodToHost
}

/// <summary>
/// The outcome of one probe.
/// </summary>
/// <param name="Direction">Which way it went.</param>
/// <param name="Target">What was dialled, <c>host:port</c>.</param>
/// <param name="Succeeded">Whether it connected (and the banner matched, when one was expected).</param>
/// <param name="Elapsed">How long it took (for a pod probe: the exec round trip).</param>
/// <param name="Error">Why it failed, or null.</param>
/// <param name="ResolvedAddress">The address the name resolved to, when known.</param>
/// <param name="Detail">The banner read, or the remote endpoint the host listener saw.</param>
public sealed record ProbeResult(ProbeDirection Direction, string Target, bool Succeeded, TimeSpan Elapsed,
                                 string? Error, string? ResolvedAddress = null, string? Detail = null)
{
    internal static ProbeResult Failed(ProbeDirection direction, string target, TimeSpan elapsed, string error,
                                       string? resolved = null) =>
        new(direction, target, false, elapsed, error, resolved);

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(Direction == ProbeDirection.HostToPod ? "host->pod " : "pod->host ")
               .Append(Target)
               .Append(Succeeded ? " OK " : " FAIL ")
               .Append(Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture))
               .Append(" ms");
        if (ResolvedAddress is not null)
            builder.Append(" [").Append(ResolvedAddress).Append(']');
        if (Detail is not null)
            builder.Append(" (").Append(Detail).Append(')');
        if (Error is not null)
            builder.Append(": ").Append(Error);
        return builder.ToString();
    }
}

/// <summary>
/// Every probe of a reachability check, labelled, in the order they ran.
/// </summary>
public sealed class ReachabilityReport
{
    private readonly List<(string Label, ProbeResult Result)> _entries = [];

    public IReadOnlyList<(string Label, ProbeResult Result)> Entries => _entries;

    public void Add(string label, ProbeResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(result);
        _entries.Add((label, result));
    }

    /// <summary>The result recorded under <paramref name="label"/>.</summary>
    /// <exception cref="KeyNotFoundException">No such probe.</exception>
    public ProbeResult this[string label] =>
        _entries.FirstOrDefault(e => e.Label == label).Result
     ?? throw new KeyNotFoundException($"No probe labelled {label}");

    /// <summary>Whether the probe under <paramref name="label"/> ran and succeeded.</summary>
    public bool Succeeded(string label) => _entries.Any(e => e.Label == label && e.Result.Succeeded);

    /// <summary>One line per probe, for test output.</summary>
    public override string ToString() =>
        string.Join(Environment.NewLine, _entries.Select(e => $"{e.Label,-28} {e.Result}"));
}