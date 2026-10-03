using System.Globalization;
using System.Text;

namespace NLightning.Testing.Cluster.Run.Matrix;

/// <summary>
/// <c>nltg-cluster matrix ...</c>: the pure half of <c>scripts/run-cluster.sh</c>'s suite matrix (no cluster access):
/// the suite catalog, the plan, which failed classes to rerun alone, and the summary with its exit code.
/// </summary>
public static class MatrixCli
{
    public const string UsageText =
        """
        nltg-cluster matrix: the suite matrix of scripts/run-cluster.sh (reads files only, never the cluster)

        Usage:
          nltg-cluster matrix list
              the suites, their tests, namespaces and hang timeouts
          nltg-cluster matrix plan [--suites a,b,...] [--max-namespaces M] [--repo R]
              one line per suite: run|skip, name, project, explicit, namespaces, parallel, timeout s,
              selection, constraints, reason or description ('|'-separated; default: every suite)
          nltg-cluster matrix rerun-classes <attempt folder> [--max N]
              the failed classes to rerun alone (none after a hang timeout, an error or more than N, default 3)
          nltg-cluster matrix summary <batch folder> [--repo R] [--started <epoch s>] [--rerun-max N] [--named]
              the summary table; exit code 1 when a suite really failed, 3 when nothing ran or (--named: the
              suites were named by the caller) a named suite was skipped
          nltg-cluster matrix green-attempts <folder>
              the attempt folders under <folder> (any depth) whose run was green: exit 0, no timeout, results green
        """;

    /// <summary>Runs <paramref name="args"/> (the words after <c>matrix</c>); returns the exit code.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Count == 0 || args[0] is "help" or "-h" or "--help")
        {
            await output.WriteLineAsync(UsageText).ConfigureAwait(false);
            return args.Count == 0 ? ClusterCli.Usage : ClusterCli.Ok;
        }

        try
        {
            var options = Options.Parse(args);
            switch (args[0])
            {
                case "list":
                    await output.WriteAsync(FormatCatalog()).ConfigureAwait(false);
                    return ClusterCli.Ok;
                case "plan":
                    var wired = options.Repo is { } repo && LndBackendProbe.IsWiredIn(repo);
                    foreach (var planned in MatrixPlanner.Plan(options.Suites, options.MaxNamespaces, wired))
                        await output.WriteLineAsync(planned.ToLine()).ConfigureAwait(false);
                    return ClusterCli.Ok;
                case "green-attempts":
                    foreach (var dir in SuiteAttempt.GreenAttemptsUnder(options.Folder!))
                        await output.WriteLineAsync(dir).ConfigureAwait(false);
                    return ClusterCli.Ok;
                case "rerun-classes":
                    var attempt = SuiteAttempt.Read(options.Folder!);
                    foreach (var c in MatrixReport.ClassesToRerun(attempt, options.RerunMax))
                        await output.WriteLineAsync(c).ConfigureAwait(false);
                    return ClusterCli.Ok;
                default: // summary
                    var reports = MatrixReport.Read(options.Folder!, options.RerunMax);
                    await output.WriteAsync(MatrixReport.Format(reports, options.Repo ?? Directory.GetCurrentDirectory(),
                                                                options.Started,
                                                                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                                                MatrixReport.ReadPeak(options.Folder!)))
                                .ConfigureAwait(false);
                    var code = MatrixReport.ExitCode(reports, options.Named);
                    if (code == MatrixReport.NothingRanExitCode)
                        await error.WriteLineAsync(
                                  "nltg-cluster matrix: no tests ran for a suite that was asked for (skipped: "
                                + string.Join(", ", reports.Where(r => r.Outcome == SuiteOutcome.Skipped)
                                                         .Select(r => r.Name))
                                + ")")
                                   .ConfigureAwait(false);
                    return code;
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or FileNotFoundException)
        {
            await error.WriteLineAsync($"nltg-cluster matrix: {e.Message}").ConfigureAwait(false);
            return ClusterCli.Usage;
        }
    }

    /// <summary>The catalog as a table.</summary>
    public static string FormatCatalog()
    {
        var rows = new List<string[]> { new[] { "SUITE", "NS", "SERIAL NS", "TIMEOUT", "EXPLICIT", "WHAT" } };
        rows.AddRange(SuiteCatalog.All.Select(s => new[]
        {
            s.Name, s.Namespaces.ToString(CultureInfo.InvariantCulture),
            s.SerialNamespaces.ToString(CultureInfo.InvariantCulture),
            $"{(int)s.Timeout.TotalMinutes}m", s.Explicit,
            s.Description + (s.DockerOnlyReason is { } d ? $" [Docker only: {d}]" : "")
                          + (s.Requirement == SuiteRequirement.LndClusterBackend
                                 ? " [needs the LND fixture's cluster backend]"
                                 : "")
                          + (s.ClusterProofPending is { } pending
                                 ? $" [not in the default matrix: {pending}]"
                                 : "")
        }));
        var widths = Enumerable.Range(0, rows[0].Length).Select(i => rows.Max(r => r[i].Length)).ToArray();
        var builder = new StringBuilder();
        foreach (var row in rows)
            builder.Append(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd()).Append('\n');

        return builder.ToString();
    }

    /// <summary>The parsed options of one matrix command.</summary>
    internal sealed record Options(
        IReadOnlyList<string>? Suites,
        int MaxNamespaces,
        string? Repo,
        string? Folder,
        int RerunMax,
        long? Started,
        bool Named = false)
    {
        /// <exception cref="ArgumentException">An unknown command or option, or a missing or bad value.</exception>
        public static Options Parse(IReadOnlyList<string> args)
        {
            var command = args[0];
            if (command is not ("list" or "plan" or "rerun-classes" or "summary" or "green-attempts"))
                throw new ArgumentException($"unknown matrix command '{command}'");

            var options = new Options(null, MatrixPlanner.MaxNamespaces, null, null, MatrixReport.DefaultRerunMax, null);
            var i = 1;
            if (command is "rerun-classes" or "summary" or "green-attempts")
            {
                if (args.Count < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{command} needs a folder");

                options = options with { Folder = args[1] };
                i = 2;
            }

            for (; i < args.Count; i++)
            {
                var arg = args[i];
                string Value() =>
                    i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                        ? args[++i]
                        : throw new ArgumentException($"{arg} needs a value");

                options = (command, arg) switch
                {
                    ("plan", "--suites") => options with
                    {
                        Suites = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    },
                    ("plan", "--max-namespaces") => options with { MaxNamespaces = Number(arg, Value(), 1) },
                    ("plan" or "summary", "--repo") => options with { Repo = Value() },
                    ("summary", "--started") => options with { Started = Number(arg, Value(), 0) },
                    ("summary", "--named") => options with { Named = true },
                    ("rerun-classes", "--max") or ("summary", "--rerun-max") =>
                        options with { RerunMax = Number(arg, Value(), 0) },
                    _ => throw new ArgumentException($"unknown option '{arg}' for matrix {command}")
                };
            }

            return options;
        }

        private static int Number(string option, string value, int min) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min
                ? n
                : throw new ArgumentException($"{option} needs a whole number >= {min}, not '{value}'");
    }
}