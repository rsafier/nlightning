using System.Globalization;
using System.Text;
using k8s;

namespace NLightning.Testing.Cluster.Run;

using Matrix;

/// <summary>
/// The harness's command line (<c>nltg-cluster</c>, project <c>test/NLightning.Testing.Cluster.Cli</c>): <c>list</c>
/// shows the run namespaces and what the reaper would do with each, <c>reap</c> deletes the reapable ones, and
/// <c>matrix ...</c> is the pure half of <c>scripts/run-cluster.sh</c>'s suite matrix (<see cref="MatrixCli"/>).
/// </summary>
public static class ClusterCli
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;

    public const string UsageText =
        """
        nltg-cluster: run namespaces of the Kubernetes test harness

        Usage:
          nltg-cluster list [options]    the run namespaces, oldest first, with the reaper's verdict
          nltg-cluster reap [options]    delete runs whose owner process is gone or that are older than their TTL
          nltg-cluster matrix ...        the suite matrix of scripts/run-cluster.sh (nltg-cluster matrix help)

        Options:
          --prefix <p>     namespace prefix (default nltg-spike; must start with nltg)
          --ttl <t>        reap runs older than this (default 6h; 90s, 30m, 6h, 2d or seconds)
          --run <id>       only runs <id> and <id>-<n>
          --force          with --run: reap them even if their owner is alive or they are kept
          --all            also runs without nltg.spike=true
          --dry-run        reap: report, delete nothing
          --wait           reap: wait until the namespaces are gone
          --context <c>    kubeconfig context (default NLTG_KUBE_CONTEXT, then the current one)
        """;

    /// <summary>Runs the command in <paramref name="args"/>; returns the exit code.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error,
                                           Func<string?, IKubernetes>? clientFactory,
                                           CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Count == 0 || args[0] is "help" or "-h" or "--help")
        {
            await output.WriteLineAsync(UsageText).ConfigureAwait(false);
            return args.Count == 0 ? Usage : Ok;
        }

        if (args[0] == "matrix")
            return await MatrixCli.RunAsync(args.Skip(1).ToList(), output, error).ConfigureAwait(false);

        ParsedArgs parsed;
        try
        {
            parsed = Parse(args);
            parsed.Options.Validate();
        }
        catch (ArgumentException e)
        {
            await error.WriteLineAsync($"nltg-cluster: {e.Message}").ConfigureAwait(false);
            await error.WriteLineAsync(UsageText).ConfigureAwait(false);
            return Usage;
        }

        using var client = (clientFactory ?? KubeClientFactory.Create)(parsed.Context);
        var options = parsed.Options with { Log = line => error.WriteLine(line) };
        if (parsed.Command == "list")
        {
            var candidates = await RunReaper.ListAsync(client, options, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(FormatTable(candidates, options.Now ?? DateTimeOffset.UtcNow, null))
                        .ConfigureAwait(false);
            return Ok;
        }

        var outcomes = await RunReaper.ReapAsync(client, options, cancellationToken).ConfigureAwait(false);
        var actions = outcomes.ToDictionary(o => o.Candidate.Namespace,
                                            o => options.DryRun ? "would delete"
                                                 : o.Deleted ? "deleted"
                                                 : $"skipped: {o.Error}");
        var listed = await RunReaper.ListAsync(client, options, cancellationToken)
                                    .ConfigureAwait(false);
        var shown = outcomes.Select(o => o.Candidate)
                            .Concat(listed.Where(c => !actions.ContainsKey(c.Namespace)))
                            .ToList();
        await output.WriteAsync(FormatTable(shown, options.Now ?? DateTimeOffset.UtcNow, actions))
                    .ConfigureAwait(false);
        var deleted = outcomes.Count(o => o.Deleted);
        await output.WriteLineAsync(options.DryRun
                                        ? $"{outcomes.Count} namespace(s) would be deleted"
                                        : $"{deleted} namespace(s) deleted").ConfigureAwait(false);
        return Ok;
    }

    /// <summary>The parsed command line.</summary>
    internal sealed record ParsedArgs(string Command, ReaperOptions Options, string? Context);

    /// <exception cref="ArgumentException">An unknown command or option, or a missing or bad value.</exception>
    internal static ParsedArgs Parse(IReadOnlyList<string> args)
    {
        var command = args[0];
        if (command is not ("list" or "reap"))
            throw new ArgumentException($"unknown command '{command}'");

        var options = new ReaperOptions();
        string? context = null;
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            string Value() =>
                i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : throw new ArgumentException($"{arg} needs a value");

            options = arg switch
            {
                "--prefix" => options with { Prefix = Value() },
                "--ttl" => options with { Ttl = ParseDuration(Value()) },
                "--run" => options with { RunFilter = TestRunId.Normalize(Value()) },
                "--force" => options with { Force = true },
                "--all" => options with { RequireSpikeLabel = false },
                "--dry-run" when command == "reap" => options with { DryRun = true },
                "--wait" when command == "reap" => options with { WaitForDeletion = true },
                "--context" => SetContext(options, Value(), ref context),
                _ => throw new ArgumentException($"unknown option '{arg}' for {command}")
            };
        }

        if (command == "list" && options.Force)
            throw new ArgumentException("--force only applies to reap");

        return new ParsedArgs(command, options, context);

        static ReaperOptions SetContext(ReaperOptions options, string value, ref string? context)
        {
            context = value;
            return options;
        }
    }

    /// <summary>A duration: <c>90s</c>, <c>30m</c>, <c>6h</c>, <c>2d</c>, or whole seconds.</summary>
    /// <exception cref="ArgumentException">Not one of those.</exception>
    public static TimeSpan ParseDuration(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        var (number, unit) = text.Length > 0 && char.IsAsciiLetter(text[^1])
                                 ? (text[..^1], char.ToLowerInvariant(text[^1]))
                                 : (text, 's');
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
         || value <= 0)
            throw new ArgumentException($"'{text}' is not a duration (90s, 30m, 6h, 2d)");

        return unit switch
        {
            's' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => throw new ArgumentException($"'{text}' is not a duration (90s, 30m, 6h, 2d)")
        };
    }

    /// <summary>The candidates as a fixed-width table (one header line, one line each).</summary>
    public static string FormatTable(IReadOnlyList<ReapCandidate> candidates, DateTimeOffset now,
                                     IReadOnlyDictionary<string, string>? actions)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            return "no run namespaces\n";

        var rows = new List<string[]> { new[] { "NAMESPACE", "SUITE", "AGE", "TTL", "OWNER", "VERDICT", "REASON" } };
        foreach (var c in candidates)
        {
            var reason = actions is not null && actions.TryGetValue(c.Namespace, out var action)
                             ? $"{c.Reason} -> {action}"
                             : c.Reason;
            rows.Add([
                c.Namespace, c.Suite ?? "-", c.StartedAt is { } s ? RunReaper.Format(now - s) : "?",
                RunReaper.Format(c.Ttl), c.Owner?.ToString() ?? "-", c.Verdict.ToString(), reason
            ]);
        }

        var widths = Enumerable.Range(0, rows[0].Length).Select(i => rows.Max(r => r[i].Length)).ToArray();
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
                builder.Append(i == row.Length - 1 ? row[i] : row[i].PadRight(widths[i] + 2));

            builder.Append('\n');
        }

        return builder.ToString();
    }
}