namespace NLightning.Testing.Cluster.Run;

using Kube;

/// <summary>What the reaper looks at and how it decides.</summary>
public sealed record ReaperOptions
{
    /// <summary>The default TTL: a run older than this is reaped whatever its owner.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(6);

    /// <summary>The namespace prefix; it must start with <c>nltg</c> (the reaper never looks elsewhere).</summary>
    public string Prefix { get; init; } = TestRunOptions.SpikeNamespacePrefix;

    /// <summary>Only namespaces labelled <c>nltg.spike=true</c> (the default while the harness is a spike).</summary>
    public bool RequireSpikeLabel { get; init; } = true;

    public TimeSpan Ttl { get; init; } = DefaultTtl;

    /// <summary>Only runs whose id is this or starts with <c>&lt;this&gt;-</c>.</summary>
    public string? RunFilter { get; init; }

    /// <summary>With <see cref="RunFilter"/>: reap the selected runs even if their owner is alive or they are kept.</summary>
    public bool Force { get; init; }

    /// <summary>Decide and report, delete nothing.</summary>
    public bool DryRun { get; init; }

    public bool WaitForDeletion { get; init; }

    public TimeSpan DeletionTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>This host's name, compared with <see cref="RunOwner.Host"/>.</summary>
    public string LocalHost { get; init; } = Environment.MachineName;

    public IProcessProbe ProcessProbe { get; init; } = LocalProcessProbe.Instance;

    /// <summary>The time to judge ages at; null for now.</summary>
    public DateTimeOffset? Now { get; init; }

    public Action<string>? Log { get; init; }

    /// <summary>Throws when the options could select anything outside the harness's namespaces.</summary>
    public void Validate()
    {
        KubeNames.RequireDns1123Label(Prefix, "namespace prefix", TestRunOptions.MaxPrefixLength);
        if (!Prefix.StartsWith("nltg", StringComparison.Ordinal))
            throw new ArgumentException($"The reaper only works under an nltg prefix, not '{Prefix}'", nameof(Prefix));
        if (Ttl <= TimeSpan.Zero)
            throw new ArgumentException("The TTL must be positive", nameof(Ttl));
        if (Force && RunFilter is null)
            throw new ArgumentException("Force needs a run filter", nameof(Force));
        if (RunFilter is not null && string.IsNullOrWhiteSpace(RunFilter))
            throw new ArgumentException("The run filter is blank", nameof(RunFilter));
    }
}