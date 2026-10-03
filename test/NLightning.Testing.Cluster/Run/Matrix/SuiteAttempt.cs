using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace NLightning.Testing.Cluster.Run.Matrix;

/// <summary>
/// One test process the matrix runner started for a suite (its first run, or a failed class rerun alone), as it left
/// it in its folder: <c>run-id</c>, <c>exit</c> (<c>&lt;code&gt; &lt;wall s&gt; &lt;start epoch&gt;</c>, written when
/// the process ended), <c>timedout</c> (the hang timeout killed it), <c>class</c> (a rerun's class),
/// <c>output.log</c>, <c>results.xml</c> and <c>diag/</c>.
/// </summary>
public sealed partial record SuiteAttempt(
    string Directory,
    string? RunId,
    int? ExitCode,
    int? WallSeconds,
    long? StartEpoch,
    bool TimedOut,
    string? RerunClass,
    XunitRunResult Results,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<FixtureReady> FixturesReady,
    IReadOnlyList<string> DiagDumps)
{
    public const string ExitFile = "exit";
    public const string TimedOutFile = "timedout";
    public const string RunIdFile = "run-id";
    public const string ClassFile = "class";
    public const string LogFile = "output.log";
    public const string ResultsFile = "results.xml";
    public const string DiagFolder = "diag";

    /// <summary>The process ended by itself with exit code 0 and its results are green.</summary>
    public bool IsGreen => ExitCode == 0 && !TimedOut && Results.IsGreen;

    /// <summary>Reads the attempt in <paramref name="directory"/> (missing files leave their fields empty).</summary>
    public static SuiteAttempt Read(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        string? Text(string file)
        {
            var path = Path.Combine(directory, file);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        int? code = null, wall = null;
        long? start = null;
        if (Text(ExitFile) is { } exit)
        {
            var parts = exit.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            code = parts.Length > 0 && int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                                                    out var c)
                       ? c
                       : null;
            wall = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
                       ? w
                       : null;
            start = parts.Length > 2 && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture,
                                                      out var s)
                        ? s
                        : null;
        }

        // A suite's log reaches hundreds of MB (every node logs into it): read it line by line; the runner gzips the
        // logs of green runs once it has summarized them
        var (namespaces, fixtures) = ScanLog(LogLines(Path.Combine(directory, LogFile)));
        return new SuiteAttempt(directory, Text(RunIdFile), code, wall, start,
                                File.Exists(Path.Combine(directory, TimedOutFile)), Text(ClassFile),
                                XunitResults.Read(Path.Combine(directory, ResultsFile)),
                                namespaces, fixtures, DumpsIn(Path.Combine(directory, DiagFolder)));
    }

    /// <summary>
    /// The run namespaces a test process logged as created (<c>[nltg-cluster] ... namespace X created</c>, sorted) and
    /// the fixtures it logged as ready (<c>[fixture] &lt;what&gt; ready in &lt;s&gt; s</c>, in order).
    /// </summary>
    public static (IReadOnlyList<string> Namespaces, IReadOnlyList<FixtureReady> FixturesReady) ScanLog(
        IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);
        var fixtures = new List<FixtureReady>();
        foreach (var line in lines)
        {
            if (line.Contains(" created", StringComparison.Ordinal))
                foreach (Match m in NamespaceCreated().Matches(line))
                    namespaces.Add(m.Groups[1].Value);

            if (line.Contains("[fixture]", StringComparison.Ordinal))
                foreach (Match m in FixtureReadyLine().Matches(line))
                    fixtures.Add(new FixtureReady(m.Groups[1].Value.Trim(),
                                                  double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
        }

        return (namespaces.ToList(), fixtures);
    }

    /// <summary>The lines of <paramref name="logPath"/>, or of <c>&lt;logPath&gt;.gz</c>; none when neither exists.</summary>
    public static IEnumerable<string> LogLines(string logPath)
    {
        ArgumentNullException.ThrowIfNull(logPath);
        if (File.Exists(logPath))
        {
            foreach (var line in File.ReadLines(logPath))
                yield return line;

            yield break;
        }

        if (!File.Exists(logPath + ".gz"))
            yield break;

        using var reader = new StreamReader(new GZipStream(File.OpenRead(logPath + ".gz"), CompressionMode.Decompress));
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    /// <summary>The diagnostics dumps under <paramref name="diagRoot"/>: folders with a <c>summary.txt</c>.</summary>
    public static IReadOnlyList<string> DumpsIn(string diagRoot) =>
        System.IO.Directory.Exists(diagRoot)
            ? System.IO.Directory.EnumerateFiles(diagRoot, "summary.txt", SearchOption.AllDirectories)
                    .Select(f => Path.GetDirectoryName(f)!)
                    .Order(StringComparer.Ordinal)
                    .ToList()
            : [];

    [GeneratedRegex(@"namespace (nltg-[a-z0-9-]+) created")]
    private static partial Regex NamespaceCreated();

    [GeneratedRegex(@"\[fixture\] ([^\r\n]*?) ready in ([0-9]+(?:\.[0-9]+)?) s")]
    private static partial Regex FixtureReadyLine();
}

/// <summary>A fixture's ready line: what was ready and after how many seconds.</summary>
public sealed record FixtureReady(string What, double Seconds);