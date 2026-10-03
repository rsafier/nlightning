using System.Globalization;
using System.Text;

namespace NLightning.Testing.Cluster.Run.Matrix;

/// <summary>How a suite of the matrix ended.</summary>
public enum SuiteOutcome
{
    /// <summary>Its run was green.</summary>
    Green,

    /// <summary>Its run failed in at most the rerun limit of classes, and each of them was green rerun alone (flake rule).</summary>
    RerunGreen,

    /// <summary>A real failure: a class failed again alone, too many classes failed, or the run itself broke.</summary>
    Failed,

    /// <summary>The hang timeout killed its run.</summary>
    TimedOut,

    /// <summary>The plan skipped it (Docker only, not ported yet).</summary>
    Skipped,

    /// <summary>Planned but never started or never finished (the runner was stopped).</summary>
    NotRun
}

/// <summary>The most run namespaces of a batch the runner saw at once (all, active), and its budget.</summary>
public sealed record NamespacePeak(int All, int Active, int Budget);

/// <summary>One suite's line of the summary.</summary>
public sealed record SuiteReport(
    string Name,
    int PlannedNamespaces,
    string? SkipReason,
    SuiteAttempt? First,
    IReadOnlyList<SuiteAttempt> Reruns,
    SuiteOutcome Outcome)
{
    public bool IsFailure => Outcome is SuiteOutcome.Failed or SuiteOutcome.TimedOut or SuiteOutcome.NotRun;
}

/// <summary>
/// The matrix runner's decisions and summary: which failed classes to rerun alone (the flake rule: at most
/// <c>rerunMax</c> classes, never after a hang timeout, a broken run or a fixture error), how a suite ended, and the
/// table with the diagnostics folders of every failed suite. Folder layout: <c>&lt;batch&gt;/plan.txt</c> (the plan
/// lines), <c>&lt;batch&gt;/&lt;suite&gt;/</c> (the first run) and <c>&lt;batch&gt;/&lt;suite&gt;/rerun-&lt;n&gt;/</c>.
/// </summary>
public static class MatrixReport
{
    public const string PlanFile = "plan.txt";

    /// <summary>The runner's sampled peak: <c>&lt;all&gt; &lt;active&gt; &lt;budget&gt;</c>.</summary>
    public const string PeakFile = "peak-namespaces";
    public const string RerunPrefix = "rerun-";

    /// <summary>The default number of failed classes the runner reruns alone.</summary>
    public const int DefaultRerunMax = 3;

    /// <summary>
    /// The classes to rerun alone after <paramref name="first"/>: its failed classes when there are 1 to
    /// <paramref name="rerunMax"/> of them, the run was not killed by the hang timeout and reported no error outside
    /// its tests; none otherwise.
    /// </summary>
    public static IReadOnlyList<string> ClassesToRerun(SuiteAttempt first, int rerunMax)
    {
        ArgumentNullException.ThrowIfNull(first);
        var failed = first.Results.FailedClasses;
        return first.TimedOut || first.ExitCode is null || !first.Results.Found || first.Results.Errors > 0
            || failed.Count == 0 || failed.Count > rerunMax
                   ? []
                   : failed;
    }

    /// <summary>How the suite ended, from its first run and the reruns of its failed classes.</summary>
    public static SuiteOutcome Judge(string? skipReason, SuiteAttempt? first, IReadOnlyList<SuiteAttempt> reruns,
                                     int rerunMax)
    {
        ArgumentNullException.ThrowIfNull(reruns);
        if (skipReason is not null)
            return SuiteOutcome.Skipped;
        if (first is null || (first.ExitCode is null && !first.TimedOut))
            return SuiteOutcome.NotRun;
        if (first.TimedOut)
            return SuiteOutcome.TimedOut;
        if (first.IsGreen)
            return SuiteOutcome.Green;

        var toRerun = ClassesToRerun(first, rerunMax);
        if (toRerun.Count == 0)
            return SuiteOutcome.Failed;

        // Every failed class rerun alone and green (a rerun of a class counts once, its last attempt)
        var lastByClass = reruns.Where(r => r.RerunClass is not null)
                                .GroupBy(r => r.RerunClass!, StringComparer.Ordinal)
                                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        return toRerun.All(c => lastByClass.TryGetValue(c, out var r) && r.IsGreen)
                   ? SuiteOutcome.RerunGreen
                   : SuiteOutcome.Failed;
    }

    /// <summary>Reads the batch folder: the plan and every planned suite's attempts.</summary>
    /// <exception cref="FileNotFoundException">No plan in the folder.</exception>
    public static IReadOnlyList<SuiteReport> Read(string batchDirectory, int rerunMax)
    {
        ArgumentNullException.ThrowIfNull(batchDirectory);
        var planPath = Path.Combine(batchDirectory, PlanFile);
        if (!File.Exists(planPath))
            throw new FileNotFoundException($"no {PlanFile} in {batchDirectory}", planPath);

        var reports = new List<SuiteReport>();
        foreach (var line in File.ReadAllLines(planPath).Where(l => l.Trim().Length > 0))
        {
            var (name, runs, namespaces, skipReason) = PlannedSuite.ParseLine(line.Trim());
            var dir = Path.Combine(batchDirectory, name);
            var first = runs && Directory.Exists(dir) ? SuiteAttempt.Read(dir) : null;
            var reruns = runs && Directory.Exists(dir)
                             ? Directory.EnumerateDirectories(dir, RerunPrefix + "*")
                                        .Select(d => (Dir: d, N: RerunNumber(d)))
                                        .Where(x => x.N > 0)
                                        .OrderBy(x => x.N)
                                        .Select(x => SuiteAttempt.Read(x.Dir))
                                        .ToList()
                             : [];
            reports.Add(new SuiteReport(name, namespaces, skipReason, first, reruns,
                                        Judge(skipReason, first, reruns, rerunMax)));
        }

        return reports;
    }

    /// <summary>
    /// The summary: one table row per suite (result, tests, passed/failed/skipped/not run (Explicit tests the mode left
    /// out), rerun, start, wall, fixture ready,
    /// NS = the namespaces its runs created / the namespaces it was planned to hold at once, dumps, first error), then
    /// the flakes, the skips and the log and diagnostics folders of every failed suite (relative to
    /// <paramref name="repoRoot"/>), then the totals and the runner's sampled namespace peak.
    /// </summary>
    public static string Format(IReadOnlyList<SuiteReport> reports, string repoRoot, long? batchStartEpoch,
                                long? nowEpoch, NamespacePeak? peak = null)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(repoRoot);
        var rows = new List<string[]>
        {
            new[]
            {
                "SUITE", "RESULT", "TESTS", "PASSED", "FAILED", "SKIPPED", "NOT RUN", "RERUN", "START", "WALL",
                "FIXTURE READY", "NS", "DIAG", "FIRST ERROR"
            }
        };
        var notes = new List<string>();
        foreach (var r in reports)
        {
            var first = r.First;
            var results = first?.Results ?? XunitRunResult.Missing;
            string Count(int n) => results.Found ? n.ToString(CultureInfo.InvariantCulture) : "-";
            var attempts = first is null ? [] : new[] { first }.Concat(r.Reruns).ToList();
            var namespaces = attempts.SelectMany(a => a.Namespaces).Distinct(StringComparer.Ordinal).Count();
            var dumps = attempts.SelectMany(a => a.DiagDumps).ToList();
            var ready = first?.FixturesReady.Count > 0
                            ? string.Join(",", first.FixturesReady.Select(f => Seconds(f.Seconds)))
                            : "-";
            var rerun = r.Reruns.Count == 0
                            ? "-"
                            : $"{r.Reruns.Count(a => a.IsGreen)}/{r.Reruns.Count} green";
            var start = first?.StartEpoch is { } s && batchStartEpoch is { } b ? $"+{s - b}s" : "-";
            var wall = first?.WallSeconds is { } w
                           ? $"{w + r.Reruns.Sum(a => a.WallSeconds ?? 0)}s"
                           : "-";
            var error = r.Outcome switch
            {
                SuiteOutcome.Skipped => "",
                SuiteOutcome.TimedOut => $"hang timeout after {first?.WallSeconds}s",
                SuiteOutcome.NotRun => "not run (runner stopped?)",
                SuiteOutcome.Green => "",
                _ => results.FirstError ?? (first?.ExitCode is { } code && code != 0 ? $"exit {code}" : "")
            };
            if (r.Outcome == SuiteOutcome.Failed && error.Length == 0)
                error = !results.Found ? "no results file" : results.Total == 0 ? "no tests ran" : "failed";

            rows.Add([
                r.Name, OutcomeText(r.Outcome), Count(results.Total), Count(results.Passed), Count(results.Failed),
                Count(results.Skipped), Count(results.NotRun), rerun, start, wall, ready,
                r.Outcome == SuiteOutcome.Skipped ? "-" : $"{namespaces}/{r.PlannedNamespaces}",
                r.Outcome == SuiteOutcome.Skipped ? "-" : dumps.Count.ToString(CultureInfo.InvariantCulture),
                Truncate(error, 100)
            ]);

            if (r.Outcome == SuiteOutcome.Skipped)
                notes.Add($"{r.Name}: skipped: {r.SkipReason}");
            foreach (var a in r.Reruns)
                notes.Add($"{r.Name}: {a.RerunClass} rerun alone: {(a.IsGreen ? "green (flake)" : "FAILED again")}"
                        + $" ({Relative(a.Directory, repoRoot)})");
            if (r.IsFailure || dumps.Count > 0)
            {
                if (r.IsFailure && first is not null)
                    notes.Add($"{r.Name}: log {Relative(Path.Combine(first.Directory, SuiteAttempt.LogFile), repoRoot)}");
                notes.AddRange(dumps.Select(d => $"{r.Name}: diag {Relative(d, repoRoot)}"));
            }

            if (r.IsFailure && first is not null && dumps.Count == 0)
                notes.Add($"{r.Name}: no diagnostics collected");
        }

        var builder = new StringBuilder();
        var widths = Enumerable.Range(0, rows[0].Length).Select(i => rows.Max(row => row[i].Length)).ToArray();
        foreach (var row in rows)
            builder.Append(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd()).Append('\n');

        foreach (var note in notes)
            builder.Append(note).Append('\n');

        var ran = reports.Count(r => r.Outcome != SuiteOutcome.Skipped);
        builder.Append(CultureInfo.InvariantCulture,
                       $"{ran} suite(s) run: {reports.Count(r => r.Outcome == SuiteOutcome.Green)} green, "
                     + $"{reports.Count(r => r.Outcome == SuiteOutcome.RerunGreen)} rerun-green, "
                     + $"{reports.Count(r => r.IsFailure)} failed; "
                     + $"{reports.Count(r => r.Outcome == SuiteOutcome.Skipped)} skipped");
        if (batchStartEpoch is { } begin && nowEpoch is { } now)
            builder.Append(CultureInfo.InvariantCulture, $"; matrix wall {now - begin}s");

        builder.Append('\n');
        if (peak is not null)
            builder.Append(CultureInfo.InvariantCulture,
                           $"peak run namespaces of the batch (sampled): {peak.All} ({peak.Active} active, the rest "
                         + $"terminating); budget {peak.Budget}\n");

        return builder.ToString();
    }

    /// <summary>The runner's sampled peak in <paramref name="batchDirectory"/>, or null.</summary>
    public static NamespacePeak? ReadPeak(string batchDirectory)
    {
        ArgumentNullException.ThrowIfNull(batchDirectory);
        var path = Path.Combine(batchDirectory, PeakFile);
        if (!File.Exists(path))
            return null;

        var parts = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var numbers = parts.Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                                            ? n
                                            : -1)
                           .ToList();
        return numbers.Count == 3 && numbers.All(n => n >= 0)
                   ? new NamespacePeak(numbers[0], numbers[1], numbers[2])
                   : null;
    }

    /// <summary>The runner's exit code: 1 when a suite really failed (rerun-green suites and skips are fine).</summary>
    public static int ExitCode(IReadOnlyList<SuiteReport> reports) =>
        reports.Any(r => r.IsFailure) ? 1 : 0;

    private static string OutcomeText(SuiteOutcome outcome) => outcome switch
    {
        SuiteOutcome.Green => "green",
        SuiteOutcome.RerunGreen => "rerun-green",
        SuiteOutcome.Failed => "FAILED",
        SuiteOutcome.TimedOut => "TIMEOUT",
        SuiteOutcome.Skipped => "skipped",
        _ => "NOT RUN"
    };

    private static int RerunNumber(string directory) =>
        int.TryParse(Path.GetFileName(directory)[RerunPrefix.Length..], NumberStyles.None,
                     CultureInfo.InvariantCulture, out var n)
            ? n
            : 0;

    private static string Seconds(double seconds) =>
        seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..(length - 3)] + "...";

    private static string Relative(string path, string repoRoot) =>
        Path.GetRelativePath(repoRoot, path);
}