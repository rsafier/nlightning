namespace NLightning.Testing.Cluster.Run.Matrix;

/// <summary>
/// The suites <c>scripts/run-cluster.sh</c> knows (<c>--suite</c> runs one, <c>--matrix</c> several), in the matrix's
/// default order: <c>lnd</c> (2 namespaces) first, then the longest first by their measured wall time (phase 6 proof),
/// so the slowest suite never starts last. The runner's admission queue starts any queued suite that fits, so a
/// 2-namespace suite queued behind 1-namespace ones would wait until two slots free at once. The longest collections
/// are split in two suites (<c>gossip</c>/<c>day0</c>, <c>eclair</c>/<c>eclair2</c>), each its own process and
/// topology, so no suite runs much longer than the CLN suite. Every suite runs on the cluster backend
/// (<c>NLTG_TEST_BACKEND=cluster</c>), its fixtures' only backend (NL-820, NL-866), except <c>tor</c>, which stays on
/// Docker (owner decision) and is listed as skipped. SQL Server tests never run (owner decision): <see cref="GlobalConstraints"/> leave them out everywhere.
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
                "-class-", $"{Docker}.SpliceLndObserverTests", "-class-", $"{Docker}.TaprootPostgresCrashTests"
            ],
            "off", Namespaces: 2, SerialNamespaces: 2, TimeSpan.FromMinutes(90), SuiteRequirement.LndClusterBackend),
        new("cln", "the CLN interop suite (Category=Interop.Cln)", "integration",
            [], ["-trait", "Category=Interop.Cln"], "off", 1, 1, TimeSpan.FromMinutes(40)),
        new("gossip", "BOLT 7 gossip proofs (Docker.Gossip, GossipRegtestCollection)", "integration",
            ["-class", $"{Docker}.Gossip.*"],
            ["-class-", $"{Docker}.Gossip.Capture.*"],
            "off", 1, 1, TimeSpan.FromMinutes(60), SuiteRequirement.LndClusterBackend),
        new("eclair", "the Eclair interop suite without its splice classes (Category=Interop.Eclair)", "integration",
            [],
            [
                "-trait", "Category=Interop.Eclair", "-class-", $"{Docker}.Interop.Eclair.EclairSpliceTests",
                "-class-", $"{Docker}.Interop.Eclair.EclairTaprootSpliceTests"
            ],
            "off", 1, 1, TimeSpan.FromMinutes(45)),
        new("ldk", "the LDK interop suite (Category=Interop.Ldk)", "integration",
            [], ["-trait", "Category=Interop.Ldk"], "off", 1, 1, TimeSpan.FromMinutes(30)),
        // The Eclair suite's longest classes, split from eclair (its own Eclair topology) to shorten the matrix; the
        // taproot splice proofs (taproot wave t03) joined them
        new("eclair2", "the Eclair splice tests (EclairSpliceTests, EclairTaprootSpliceTests, Category=Interop.Eclair)",
            "integration",
            [
                "-class", $"{Docker}.Interop.Eclair.EclairSpliceTests",
                "-class", $"{Docker}.Interop.Eclair.EclairTaprootSpliceTests"
            ],
            ["-trait", "Category=Interop.Eclair"],
            "off", 1, 1, TimeSpan.FromMinutes(30)),
        // The rest of the gossip-regtest collection, split from gossip (its own network) to shorten the matrix
        new("day0", "the day-0 flows, the LND splice observer and the public channel policy (GossipRegtestCollection)",
            "integration",
            [
                "-class", $"{Docker}.Day0.*", "-class", $"{Docker}.ChannelPolicyPublicFlowTests",
                "-class", $"{Docker}.SpliceLndObserverTests"
            ],
            [], "off", 1, 1, TimeSpan.FromMinutes(30), SuiteRequirement.LndClusterBackend),
        new("onchain", "BOLT 5 legacy on-chain proofs and the channel backups (OnchainRegtestCollection)",
            "integration",
            ["-class", $"{Docker}.Onchain.Onchain*", "-class", $"{Docker}.BackupRestoreFlowTests"],
            [], "off", 1, 1, TimeSpan.FromMinutes(90), SuiteRequirement.LndClusterBackend),
        new("anchors", "BOLT 5 anchors proofs (Docker.Onchain.Anchors, OnchainRegtestCollection)", "integration",
            ["-namespace", $"{Docker}.Onchain.Anchors"],
            [], "off", 1, 1, TimeSpan.FromMinutes(60), SuiteRequirement.LndClusterBackend),
        new("faults", "partition and ZMQ-loss tests (Cluster/Live, one topology per class)", "integration",
            ["-class", $"{Cluster}.PartitionClusterTests", "-class", $"{Cluster}.ChainMonitorZmqClusterTests"],
            ["-trait", "Category=Cluster"], "only", 2, 1, TimeSpan.FromMinutes(15)),
        new("abcd", "the ABCD multi-hop suite (Docker.Abcd)", "integration",
            ["-namespace", $"{Docker}.Abcd"],
            [], "off", 1, 1, TimeSpan.FromMinutes(45), SuiteRequirement.LndClusterBackend),
        // Simple taproot channels against LND (taproot plan T6): their own network, one LND run with
        // --protocol.simple-taproot-chans (the flag also forces LND's RBF close, so the shared regtest network's nodes
        // never get it); also the regtest e2e of public taproot channels between NLightning nodes on its bitcoind
        // (taproot gossip, NL-878)
        new("taproot", "simple taproot channels against LND and public taproot channels between NLightning nodes "
                     + "(Docker.Taproot, LndTaprootRegtestCollection)",
            "integration",
            ["-namespace", $"{Docker}.Taproot"],
            [], "off", 1, 1, TimeSpan.FromMinutes(30), SuiteRequirement.LndClusterBackend),
        // TaprootPostgresCrashTests: plan D-T4's taproot crash proof on Postgres (NL-960)
        new("postgres", "PostgresTests, TaprootPostgresCrashTests and ServerDatabaseClusterTests on Postgres pods",
            "integration",
            [
                "-class", $"{Docker}.PostgresTests", "-class", $"{Docker}.TaprootPostgresCrashTests",
                "-class", $"{Cluster}.ServerDatabaseClusterTests"
            ],
            ["-trait", "Database=Postgres"], "on", 3, 2, TimeSpan.FromMinutes(10)),
        // Cashu plan C2 (NL-993): CDK's cdk-mintd on our CDK payment processor, with cdk-cli as the wallet (pods)
        new("cashu", "the Cashu mint proof (Category=Interop.Cashu)", "integration",
            [], ["-trait", "Category=Interop.Cashu"], "off", 1, 1, TimeSpan.FromMinutes(20)),
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