namespace NLightning.Testing.Cluster.Run;

using Kube;

/// <summary>
/// The names and labels of one run, computed without a cluster: <see cref="TestRun"/> creates them, the manifest
/// builders read them.
/// </summary>
public sealed class RunIdentity
{
    private RunIdentity(string id, string ns, string prefix, string suite, DateTimeOffset startedAt, bool spike)
    {
        Id = id;
        Namespace = ns;
        NamespacePrefix = prefix;
        Suite = suite;
        StartedAt = startedAt;
        Spike = spike;
        Labels = RunLabels.ForRun(id, suite, startedAt, spike);
    }

    /// <summary>The run id (lower case, DNS-1123).</summary>
    public string Id { get; }

    /// <summary>The run's namespace, <c>&lt;prefix&gt;-&lt;id&gt;</c>.</summary>
    public string Namespace { get; }

    /// <summary>The namespace prefix (only namespaces under it are ever deleted).</summary>
    public string NamespacePrefix { get; }

    public string Suite { get; }

    public DateTimeOffset StartedAt { get; }

    public bool Spike { get; }

    /// <summary>The labels of the namespace and of every object in it (see <see cref="RunLabels.ForRun"/>).</summary>
    public IReadOnlyDictionary<string, string> Labels { get; }

    /// <summary>
    /// The identity of a run started at <paramref name="startedAt"/> with <paramref name="options"/>; a null
    /// <see cref="TestRunOptions.RunId"/> generates the id (the environment is read by
    /// <see cref="TestRunOptions.FromEnvironment"/>, not here).
    /// </summary>
    public static RunIdentity Create(TestRunOptions options, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(options);

        var prefix = KubeNames.RequireDns1123Label(options.NamespacePrefix, "namespace prefix",
                                                   TestRunOptions.MaxPrefixLength);
        var id = TestRunId.Resolve(options.RunId);
        var ns = KubeNames.RequireDns1123Label($"{prefix}-{id}", "namespace");
        return new RunIdentity(id, ns, prefix, options.Suite, startedAt, options.Spike);
    }

    /// <summary>The DNS name of <paramref name="service"/> in the run's namespace.</summary>
    public string ServiceDnsName(string service) => $"{service}.{Namespace}.svc.cluster.local";

    public override string ToString() => Namespace;
}