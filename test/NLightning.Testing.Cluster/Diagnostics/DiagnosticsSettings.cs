namespace NLightning.Testing.Cluster.Diagnostics;

/// <summary>When the harness collects a run's diagnostics on its own (<see cref="DiagnosticsSettings.ModeVariable"/>).</summary>
public enum DiagnosticsMode
{
    /// <summary>Never by itself; <see cref="ClusterDiagnostics.DumpAsync(Run.TestRun, string?, string?, CancellationToken)"/> still works.</summary>
    Off,

    /// <summary>On a failed test, a failed topology build or node deployment, and a <see cref="Poll"/> timeout (the default).</summary>
    Failure,

    /// <summary>After every test and before every run's namespace is deleted, failed or not.</summary>
    Always
}

/// <summary>
/// Where and when the harness writes a run's diagnostics (pod logs, pod status, events, storage and each node's own
/// state) when something fails.
/// </summary>
public sealed record DiagnosticsSettings
{
    /// <summary><c>always</c>, <c>failure</c> (default) or <c>off</c>.</summary>
    public const string ModeVariable = "NLTG_CLUSTER_DIAG";

    /// <summary>
    /// The root directory of the dumps (<c>scripts/run-cluster.sh</c> sets <c>TestResults/cluster/&lt;batch&gt;/&lt;run&gt;/diag</c>);
    /// unset means <c>&lt;repo&gt;/TestResults/cluster/&lt;run id&gt;</c>.
    /// </summary>
    public const string DirectoryVariable = "NLTG_CLUSTER_DIAG_DIR";

    /// <summary>The settings of a process without the variables: on failure, under the repository's TestResults.</summary>
    public static DiagnosticsSettings Default { get; } = new();

    public DiagnosticsMode Mode { get; init; } = DiagnosticsMode.Failure;

    /// <summary>The root of the dumps; null for <see cref="DefaultRoot"/>.</summary>
    public string? RootDirectory { get; init; }

    /// <summary>How long one dump of a namespace may take in all.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long each node-state command (an exec into a pod) may take.</summary>
    public TimeSpan ExecTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The most bytes kept of one container log (the end of the log is kept).</summary>
    public int MaxLogBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>The settings from <see cref="ModeVariable"/> and <see cref="DirectoryVariable"/>.</summary>
    public static DiagnosticsSettings FromEnvironment(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var directory = environment(DirectoryVariable);
        return new DiagnosticsSettings
        {
            Mode = ParseMode(environment(ModeVariable)),
            RootDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim()
        };
    }

    /// <summary>
    /// <c>off</c>/<c>0</c>/<c>false</c>/<c>none</c> → <see cref="DiagnosticsMode.Off"/>, <c>always</c>/<c>all</c> →
    /// <see cref="DiagnosticsMode.Always"/>, anything else (unset, <c>failure</c>, <c>on</c>) →
    /// <see cref="DiagnosticsMode.Failure"/>.
    /// </summary>
    public static DiagnosticsMode ParseMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "off" or "0" or "false" or "none" or "no" => DiagnosticsMode.Off,
            "always" or "all" => DiagnosticsMode.Always,
            _ => DiagnosticsMode.Failure
        };

    /// <summary>The root of <paramref name="runId"/>'s dumps: <see cref="RootDirectory"/> or <see cref="DefaultRoot"/>.</summary>
    public string ResolveRoot(string runId) => RootDirectory ?? DefaultRoot(runId);

    /// <summary>
    /// <c>&lt;repo&gt;/TestResults/cluster/&lt;run id&gt;</c>: the repository is the first directory up from the current
    /// one (then from the test assembly's) that holds <c>NLightning.sln</c>; the current directory when none does.
    /// </summary>
    public static string DefaultRoot(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var repo = FindRepositoryRoot(Environment.CurrentDirectory)
                ?? FindRepositoryRoot(AppContext.BaseDirectory)
                ?? Environment.CurrentDirectory;
        return Path.Combine(repo, "TestResults", "cluster", runId);
    }

    internal static string? FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NLightning.sln")))
                return directory.FullName;
        }

        return null;
    }
}