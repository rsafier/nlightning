namespace NLightning.Testing.Cluster.Tests.Run.Matrix;

using Cluster.Run.Matrix;

public class MatrixPlannerTests
{
    [Fact]
    public void Given_NoSuites_When_Planned_Then_EverySuiteIsPlannedInCatalogOrder()
    {
        // Act
        var plan = MatrixPlanner.Plan(null, 6, lndClusterBackendWired: true);

        // Assert
        Assert.Equal(SuiteCatalog.Names, plan.Select(p => p.Suite.Name));
        // Skipped by default: the suites whose cluster proof is pending, and tor (Docker only)
        Assert.Equal([.. PendingSuites(), "tor"], plan.Where(p => !p.Runs).Select(p => p.Suite.Name));
        Assert.All(plan.Where(p => p.Runs), p => Assert.False(p.Serial));
    }

    [Fact]
    public void Given_ASuiteWhoseClusterProofIsPending_When_PlannedByDefault_Then_ItIsSkippedWithTheReason()
    {
        // Arrange
        var pending = PendingSuites().FirstOrDefault();
        if (pending is null)
            Assert.Skip("every suite of the catalog is proven on the cluster");

        // Act
        var plan = MatrixPlanner.Plan(null, 6, lndClusterBackendWired: true);

        // Assert: left out of the default matrix, with how to run it
        var suite = plan.Single(p => p.Suite.Name == pending);
        Assert.False(suite.Runs);
        Assert.Equal(0, suite.Namespaces);
        Assert.Contains("not in the default matrix until its cluster proof is made", suite.SkipReason);
        Assert.Contains($"--suite {pending}", suite.SkipReason);
    }

    [Fact]
    public void Given_ASuiteWhoseClusterProofIsPending_When_Named_Then_ItRuns()
    {
        // Arrange
        var pending = PendingSuites().FirstOrDefault();
        if (pending is null)
            Assert.Skip("every suite of the catalog is proven on the cluster");

        // Act
        var plan = MatrixPlanner.Plan(["abcd", pending], 6, lndClusterBackendWired: true);

        // Assert: naming a suite is how its proof gets made (in catalog order, the longest first)
        Assert.Equal(SuiteCatalog.Names.Where(n => n == pending || n == "abcd"), plan.Select(p => p.Suite.Name));
        Assert.All(plan, p => Assert.True(p.Runs));
        Assert.All(plan, p => Assert.Equal(1, p.Namespaces));
    }

    [Fact]
    public void Given_SuitesInAnyOrder_When_Planned_Then_TheLongestStartFirst()
    {
        // Act
        var plan = MatrixPlanner.Plan(["postgres", "ldk", "cln"], 6, false);

        // Assert
        Assert.Equal(["cln", "ldk", "postgres"], plan.Select(p => p.Suite.Name));
    }

    [Fact]
    public void Given_TheLndFixtureNotWired_When_Planned_Then_TheLndSuitesAreSkippedWithTheReason()
    {
        // Act
        var plan = MatrixPlanner.Plan(null, 6, lndClusterBackendWired: false);

        // Assert
        var skipped = plan.Where(p => !p.Runs).ToList();
        Assert.Equal(["lnd", "gossip", "day0", "onchain", "anchors", "abcd", "taproot", "tor"],
                     skipped.Select(p => p.Suite.Name));
        Assert.All(skipped.Where(p => p.Suite.Name != "tor"),
                   p => Assert.Contains("would start Docker containers", p.SkipReason));
        Assert.StartsWith("Docker only", skipped.Single(p => p.Suite.Name == "tor").SkipReason);
        Assert.All(skipped, p => Assert.Equal(0, p.Namespaces));
    }

    [Fact]
    public void Given_ABudgetBelowASuitesParallelNeed_When_Planned_Then_ItRunsSeriallyWithinTheBudget()
    {
        // Act
        var plan = MatrixPlanner.Plan(["postgres", "faults", "cln"], 2, false);

        // Assert
        var postgres = plan.Single(p => p.Suite.Name == "postgres");
        Assert.True(postgres.Serial);
        Assert.Equal("none", postgres.Parallel);
        Assert.Equal(2, postgres.Namespaces);
        var faults = plan.Single(p => p.Suite.Name == "faults");
        Assert.False(faults.Serial);
        Assert.Equal("collections", faults.Parallel);
        Assert.Equal(2, faults.Namespaces);
    }

    [Fact]
    public void Given_ABudgetBelowASuitesSerialNeed_When_Planned_Then_ItIsRefused()
    {
        // Act
        var e = Assert.Throws<ArgumentException>(() => MatrixPlanner.Plan(["postgres"], 1, false));

        // Assert
        Assert.Contains("needs 2 namespace(s) even serially", e.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void Given_ABudgetOutsideTheCap_When_Planned_Then_ItIsRefused(int budget)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => MatrixPlanner.Plan(["cln"], budget, false));
    }

    [Fact]
    public void Given_TheWholeCap_When_Planned_Then_TwelveNamespacesAreAccepted()
    {
        // Act: NL-844 raised the cap from 6 to 12
        var plan = MatrixPlanner.Plan(["lnd"], 12, true);

        // Assert
        Assert.Single(plan);
    }

    [Fact]
    public void Given_ASuiteNamedTwice_When_Planned_Then_ItIsRefused()
    {
        // Act
        var e = Assert.Throws<ArgumentException>(() => MatrixPlanner.Plan(["cln", "CLN"], 6, false));

        // Assert
        Assert.Contains("named twice", e.Message);
    }

    [Fact]
    public void Given_APlannedSuite_When_Written_Then_TheLineCarriesWhatTheRunnerReads()
    {
        // Arrange
        var postgres = MatrixPlanner.Plan(["postgres"], 2, false).Single();

        // Act
        var fields = postgres.ToLine().Split('|');

        // Assert
        Assert.Equal(10, fields.Length);
        Assert.Equal(["run", "postgres", "integration", "on", "2", "none", "600"], fields[..7]);
        Assert.Equal("-class NLightning.Integration.Tests.Docker.PostgresTests "
                   + "-class NLightning.Integration.Tests.Docker.TaprootPostgresCrashTests "
                   + "-class NLightning.Integration.Tests.Cluster.Live.ServerDatabaseClusterTests", fields[7]);
        Assert.Equal("-trait Database=Postgres -trait- Database=SqlServer", fields[8]);
    }

    [Fact]
    public void Given_ASuiteWithoutSelection_When_Written_Then_TheEmptyFieldIsADash()
    {
        // Act
        var fields = MatrixPlanner.Plan(["cln"], 6, false).Single().ToLine().Split('|');

        // Assert
        Assert.Equal("-", fields[7]);
        Assert.Equal("-trait Category=Interop.Cln -trait- Database=SqlServer", fields[8]);
    }

    [Fact]
    public void Given_PlanLines_When_Parsed_Then_TheNameStateAndReasonComeBack()
    {
        // Arrange
        var plan = MatrixPlanner.Plan(["cln", "tor"], 6, false);

        // Act
        var cln = PlannedSuite.ParseLine(plan[0].ToLine());
        var (torName, torRuns, _, torReason) = PlannedSuite.ParseLine(plan[1].ToLine());

        // Assert
        Assert.Equal(("cln", true, 1, (string?)null), cln);
        Assert.Equal("tor", torName);
        Assert.False(torRuns);
        Assert.StartsWith("Docker only", torReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("run|cln")]
    [InlineData("maybe|cln|integration|off|1|collections|60|-|-|x")]
    [InlineData("run|cln|integration|off|x|collections|60|-|-|x")]
    public void Given_ABadPlanLine_When_Parsed_Then_ItIsRefused(string line)
    {
        // Act & Assert
        Assert.Throws<FormatException>(() => PlannedSuite.ParseLine(line));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("public class LightningRegtestNetworkFixture : IDisposable { }", false)]
    [InlineData("private readonly ILndNetworkBackend _backend = new DockerLndBackend();", false)]
    [InlineData("_backend = TestBackend.Current == TestBackendKind.Cluster ? new ClusterLndBackend() : new DockerLndBackend();",
                true)]
    public void Given_TheFixtureSource_When_Probed_Then_OnlyTheWiredFixtureCounts(string? source, bool wired)
    {
        // Act & Assert
        Assert.Equal(wired, LndBackendProbe.IsWired(source));
    }

    [Fact]
    public void Given_ARepositoryWithoutTheFixture_When_Probed_Then_ItIsNotWired()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("nltg-probe").FullName;
        try
        {
            // Act & Assert
            Assert.False(LndBackendProbe.IsWiredIn(root));

            var path = Path.Combine(root, LndBackendProbe.FixturePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "ILndNetworkBackend backend = new DockerLndBackend();");
            Assert.False(LndBackendProbe.IsWiredIn(root));
            File.WriteAllText(path, "ILndNetworkBackend backend = cluster ? new ClusterLndBackend() : docker;");
            Assert.True(LndBackendProbe.IsWiredIn(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The catalog's suites whose cluster proof is pending, in catalog order (the LND suites not proven yet).</summary>
    private static IReadOnlyList<string> PendingSuites() =>
        SuiteCatalog.All.Where(s => s.ClusterProofPending is not null).Select(s => s.Name).ToList();
}