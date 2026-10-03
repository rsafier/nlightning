namespace NLightning.Testing.Cluster.Run.Matrix;

/// <summary>
/// The suites <c>scripts/run-cluster.sh</c> knows (<c>--suite</c> runs one, <c>--matrix</c> several), in the matrix's
/// default order: the longest first, so the slowest suite never starts last. Every suite runs on the cluster backend
/// (<c>NLTG_TEST_BACKEND=cluster</c>) except <c>tor</c>, which stays on Docker (owner decision) and is listed as
/// skipped. SQL Server tests never run (owner decision): <see cref="GlobalConstraints"/> leave them out everywhere.
/// </summary>
public static class SuiteCatalog
{
    private const string Docker = "NLightning.Integration.Tests.Docker";
    private const string Cluster = "NLightning.Integration.Tests.Cluster.Live";

    /// <summary>Constraints added to every suite: no SQL Server test (owner decision).</summary>
    public static IReadOnlyList<string> GlobalConstraints { get; } = ["-trait-", "Database=SqlServer"];

    /// <summary>Every suite, in the matrix's default order.</summary>
    public static IReadOnlyList<MatrixSuite> All { get; } =
    [
        // One fixture collection (the regtest network) plus MultiNodeHarnessTests' own Postgres pod: 2 namespaces with
        // the collections in parallel (the container-free Docker.Utils tests hold none). The classes of the other
        // LightningRegtestNetworkFixture collections (postgres, onchain-regtest, gossip-regtest) run in their own
        // suites, so lnd never needs hf-lnd-wire's -parallel none (SuiteCatalogMembershipTests keeps it that way).
        new("lnd", "the LND regtest collection (Docker namespace, LightningRegtestNetworkFixtureCollection)",
            "integration",
            ["-namespace", Docker, "-namespace", $"{Docker}.Utils"],
            [
                // Classes of the namespace that belong to other suites or are SQL Server only
                "-class-", $"{Docker}.PostgresTests", "-class-", $"{Docker}.SqlServerTests",
                "-class-", $"{Docker}.BackupRestoreFlowTests", "-class-", $"{Docker}.ChannelPolicyPublicFlowTests",
                "-class-", $"{Docker}.SpliceLndObserverTests"
            ],
            "off", Namespaces: 2, SerialNamespaces: 2, TimeSpan.FromMinutes(90), SuiteRequirement.LndClusterBackend),
        new("onchain", "BOLT 5 legacy on-chain proofs and the channel backups (OnchainRegtestCollection)",
            "integration",
            ["-class", $"{Docker}.Onchain.Onchain*", "-class", $"{Docker}.BackupRestoreFlowTests"],
            [], "off", 1, 1, TimeSpan.FromMinutes(90), SuiteRequirement.LndClusterBackend,
            ClusterProofPending: "only BackupRestoreFlowTests has run on the cluster; OnchainO5Tests' LND channel.db "
                               + "rollback skips there (it drives Docker containers)"),
        new("anchors", "BOLT 5 anchors proofs (Docker.Onchain.Anchors, OnchainRegtestCollection)", "integration",
            ["-namespace", $"{Docker}.Onchain.Anchors"],
            [], "off", 1, 1, TimeSpan.FromMinutes(60), SuiteRequirement.LndClusterBackend,
            ClusterProofPending: "not run on the cluster yet; AnchorsO5Tests and AnchorsPackageRelayTests skip there "
                               + "(they drive Docker containers)"),
        new("gossip", "BOLT 7 gossip proofs, day-0 flows and the LND splice observer (GossipRegtestCollection)",
            "integration",
            [
                "-class", $"{Docker}.Gossip.*", "-class", $"{Docker}.Day0.*",
                "-class", $"{Docker}.ChannelPolicyPublicFlowTests", "-class", $"{Docker}.SpliceLndObserverTests"
            ],
            ["-class-", $"{Docker}.Gossip.Capture.*"],
            "off", 1, 1, TimeSpan.FromMinutes(60), SuiteRequirement.LndClusterBackend,
            ClusterProofPending: "only ChannelPolicyPublicFlowTests and SpliceLndObserverTests have run on the "
                               + "cluster"),
        new("eclair", "the Eclair interop suite (Category=Interop.Eclair)", "integration",
            [], ["-trait", "Category=Interop.Eclair"], "off", 1, 1, TimeSpan.FromMinutes(45)),
        new("cln", "the CLN interop suite (Category=Interop.Cln)", "integration",
            [], ["-trait", "Category=Interop.Cln"], "off", 1, 1, TimeSpan.FromMinutes(40)),
        new("abcd", "the ABCD multi-hop suite (Docker.Abcd)", "integration",
            ["-namespace", $"{Docker}.Abcd"],
            [], "off", 1, 1, TimeSpan.FromMinutes(45), SuiteRequirement.LndClusterBackend),
        new("ldk", "the LDK interop suite (Category=Interop.Ldk)", "integration",
            [], ["-trait", "Category=Interop.Ldk"], "off", 1, 1, TimeSpan.FromMinutes(30)),
        new("faults", "partition and ZMQ-loss tests (Cluster/Live, one topology per class)", "integration",
            ["-class", $"{Cluster}.PartitionClusterTests", "-class", $"{Cluster}.ChainMonitorZmqClusterTests"],
            ["-trait", "Category=Cluster"], "only", 2, 1, TimeSpan.FromMinutes(15)),
        new("postgres", "PostgresTests and ServerDatabaseClusterTests on Postgres pods", "integration",
            ["-class", $"{Docker}.PostgresTests", "-class", $"{Cluster}.ServerDatabaseClusterTests"],
            ["-trait", "Database=Postgres"], "on", 3, 2, TimeSpan.FromMinutes(10)),
        new("tor", "the Tor interop suite (Category=Interop.Tor)", "integration",
            [], ["-trait", "Category=Interop.Tor"], "off", 1, 1, TimeSpan.FromMinutes(30),
            DockerOnlyReason: "Tor interop stays on Docker (owner decision); run scripts/run-interop.sh tor")
    ];

    /// <summary>The names, in the matrix's default order.</summary>
    public static IReadOnlyList<string> Names { get; } = All.Select(s => s.Name).ToList();

    /// <summary>The suite named <paramref name="name"/> (case-insensitive).</summary>
    /// <exception cref="ArgumentException">No such suite.</exception>
    public static MatrixSuite Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return All.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"unknown suite '{name}' ({string.Join(", ", Names)})", nameof(name));
    }
}