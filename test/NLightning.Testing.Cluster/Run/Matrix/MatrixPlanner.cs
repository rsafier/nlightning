using System.Globalization;

namespace NLightning.Testing.Cluster.Run.Matrix;

/// <summary>One suite as the matrix runner will treat it.</summary>
/// <param name="Suite">The catalog entry.</param>
/// <param name="SkipReason">Null when the suite runs; otherwise why it does not.</param>
/// <param name="Namespaces">The run namespaces it holds while it runs (its admission weight).</param>
/// <param name="Serial">Run with <c>-parallel none</c> to fit the namespace budget.</param>
public sealed record PlannedSuite(MatrixSuite Suite, string? SkipReason, int Namespaces, bool Serial)
{
    public const char Separator = '|';

    public bool Runs => SkipReason is null;

    /// <summary>xunit's <c>-parallel</c> option for the suite.</summary>
    public string Parallel => Serial ? "none" : "collections";

    /// <summary>
    /// One line for the runner: <c>run|skip</c>, name, project, explicit mode, namespaces, parallel, timeout in seconds,
    /// selection, constraints (the global ones included), reason or description; fields separated by
    /// <see cref="Separator"/>, arguments by one space, an empty field as <c>-</c>.
    /// </summary>
    public string ToLine()
    {
        string Args(IEnumerable<string> args) => string.Join(' ', args) is { Length: > 0 } joined ? joined : "-";

        return string.Join(Separator,
                           Runs ? "run" : "skip",
                           Suite.Name,
                           Suite.Project,
                           Suite.Explicit,
                           Namespaces.ToString(CultureInfo.InvariantCulture),
                           Parallel,
                           ((long)Suite.Timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                           Args(Suite.Selection),
                           Args(Suite.Constraints.Concat(SuiteCatalog.GlobalConstraints)),
                           SkipReason ?? Suite.Description);
    }

    /// <summary>The name and the skip reason of a line written by <see cref="ToLine"/> (null for a run line).</summary>
    /// <exception cref="FormatException">Not such a line.</exception>
    public static (string Name, bool Runs, int Namespaces, string? SkipReason) ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var fields = line.Split(Separator);
        if (fields.Length != 10 || fields[0] is not ("run" or "skip")
         || !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var namespaces))
            throw new FormatException($"not a plan line: '{line}'");

        var runs = fields[0] == "run";
        return (fields[1], runs, namespaces, runs ? null : fields[9]);
    }
}

/// <summary>
/// Turns the suites a caller asked for into the matrix plan, in the catalog's order (the longest first; every suite
/// when none are named): Docker-only and not-yet-wired suites skipped with a reason, and each suite's admission weight
/// within the namespace budget (a suite whose parallel collections need more than the budget runs with
/// <c>-parallel none</c> when that fits).
/// </summary>
public static class MatrixPlanner
{
    /// <summary>The machine-wide cap on harness run namespaces (<see cref="RunAdmission.DefaultMaxRuns"/>).</summary>
    public const int MaxNamespaces = RunAdmission.DefaultMaxRuns;

    /// <exception cref="ArgumentException">
    /// An unknown or repeated suite, a budget outside 1..<see cref="MaxNamespaces"/>, or a suite that needs more
    /// namespaces than the budget even serially.
    /// </exception>
    public static IReadOnlyList<PlannedSuite> Plan(IReadOnlyList<string>? names, int maxNamespaces,
                                                   bool lndClusterBackendWired)
    {
        if (maxNamespaces is < 1 or > MaxNamespaces)
            throw new ArgumentException($"the namespace budget must be 1-{MaxNamespaces}, not {maxNamespaces}",
                                        nameof(maxNamespaces));

        var suites = names is null || names.Count == 0
                         ? SuiteCatalog.All
                         : names.Select(SuiteCatalog.Get).ToList();
        var repeated = suites.GroupBy(s => s.Name).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
            throw new ArgumentException($"suite '{repeated.Key}' is named twice", nameof(names));

        // The catalog's order whatever the caller's: the longest suites start first
        suites = suites.OrderBy(s => SuiteCatalog.Names.ToList().IndexOf(s.Name)).ToList();

        return suites.Select(s => PlanOne(s, maxNamespaces, lndClusterBackendWired)).ToList();
    }

    private static PlannedSuite PlanOne(MatrixSuite suite, int maxNamespaces, bool lndClusterBackendWired)
    {
        if (suite.DockerOnlyReason is { } dockerOnly)
            return new PlannedSuite(suite, $"Docker only: {dockerOnly}", 0, false);

        if (suite.Requirement == SuiteRequirement.LndClusterBackend && !lndClusterBackendWired)
            return new PlannedSuite(
                suite,
                "not ported yet: LightningRegtestNetworkFixture does not delegate to ILndNetworkBackend (phase 3 "
              + "wiring), so it would start Docker containers; run it with the Docker scripts",
                0, false);

        if (suite.Namespaces <= maxNamespaces)
            return new PlannedSuite(suite, null, suite.Namespaces, false);

        if (suite.SerialNamespaces <= maxNamespaces)
            return new PlannedSuite(suite, null, suite.SerialNamespaces, true);

        throw new ArgumentException(
            $"suite '{suite.Name}' needs {suite.SerialNamespaces} namespace(s) even serially; the budget is "
          + $"{maxNamespaces}", nameof(maxNamespaces));
    }
}