namespace NLightning.Testing.Cluster.Tests.Run.Matrix;

using Cluster.Run.Matrix;

public class SuiteCatalogTests
{
    [Fact]
    public void Given_TheCatalog_When_Read_Then_EveryPortedSuiteAndTorAreThere()
    {
        // Assert
        Assert.Equal([
                         "lnd", "cln", "gossip", "eclair", "ldk", "eclair2", "day0", "onchain", "anchors", "faults",
                         "abcd", "postgres", "tor", "cashu"
                     ],
                     SuiteCatalog.Names);
    }

    [Fact]
    public void Given_EverySuite_When_Checked_Then_ItsFieldsFitTheRunnersLineFormat()
    {
        foreach (var suite in SuiteCatalog.All)
        {
            // Names: lower case, no '-' (the runner's run ids are <batch>-<suite>[-r<n>] and the reaper matches prefixes)
            Assert.Matches("^[a-z0-9]{1,8}$", suite.Name);
            Assert.True(suite.Project is "integration" or "cluster", suite.Name);
            Assert.True(suite.Explicit is "off" or "on" or "only", suite.Name);
            Assert.InRange(suite.Namespaces, 1, MatrixPlanner.MaxNamespaces);
            Assert.InRange(suite.SerialNamespaces, 1, suite.Namespaces);
            Assert.True(suite.Timeout > TimeSpan.Zero, suite.Name);

            // The runner splits fields on '|' and arguments on spaces, and never globs them
            foreach (var arg in suite.Selection.Concat(suite.Constraints))
            {
                Assert.DoesNotContain(' ', arg);
                Assert.DoesNotContain('|', arg);
                Assert.NotEmpty(arg);
            }

            Assert.DoesNotContain('|', suite.Description);
            Assert.DoesNotContain('|', suite.DockerOnlyReason ?? "");
            AssertOptionsHaveValues(suite.Selection);
            AssertOptionsHaveValues(suite.Constraints);
        }
    }

    [Fact]
    public void Given_TheSelections_When_Read_Then_OnlyFilterKindsACallerMayReplaceAreUsed()
    {
        // A caller's --class/--method and a rerun's -class replace the selection; xunit ORs filters of one kind, so a
        // selection holds only -class/-namespace, and constraints only what must always apply
        foreach (var suite in SuiteCatalog.All)
        {
            Assert.All(suite.Selection.Where((_, i) => i % 2 == 0), o => Assert.Contains(o, new[] { "-class", "-namespace" }));
            Assert.All(suite.Constraints.Where((_, i) => i % 2 == 0),
                       o => Assert.Contains(o, new[] { "-trait", "-trait-", "-class-" }));
        }
    }

    [Fact]
    public void Given_TheSuites_When_Read_Then_TorIsDockerOnlyAndTheLndSuitesNeedTheirClusterBackend()
    {
        // Assert
        Assert.NotNull(SuiteCatalog.Get("tor").DockerOnlyReason);
        Assert.NotNull(SuiteCatalog.Get("cashu").DockerOnlyReason);
        Assert.All(SuiteCatalog.All.Where(s => s.Name is not ("tor" or "cashu")), s => Assert.Null(s.DockerOnlyReason));
        Assert.Equal(["lnd", "gossip", "day0", "onchain", "anchors", "abcd"],
                     SuiteCatalog.All.Where(s => s.Requirement == SuiteRequirement.LndClusterBackend)
                                 .Select(s => s.Name));
        // lnd, gossip, day0, onchain, anchors and abcd are proven on the cluster (in the default matrix); an LND suite still
        // waiting for its proof carries ClusterProofPending
        Assert.All(["lnd", "gossip", "day0", "onchain", "anchors", "abcd"],
                   name => Assert.Null(SuiteCatalog.Get(name).ClusterProofPending));
    }

    [Fact]
    public void Given_TheGlobalConstraints_When_Read_Then_SqlServerTestsNeverRun()
    {
        // Assert
        Assert.Equal(["-trait-", "Database=SqlServer"], SuiteCatalog.GlobalConstraints);
    }

    [Theory]
    [InlineData("CLN", "cln")]
    [InlineData(" postgres ", "postgres")]
    public void Given_ASuiteName_When_Got_Then_CaseAndSpacesDoNotMatter(string name, string expected)
    {
        // Act & Assert
        Assert.Equal(expected, SuiteCatalog.Get(name).Name);
    }

    [Fact]
    public void Given_AnUnknownSuite_When_Got_Then_TheErrorListsTheKnownOnes()
    {
        // Act
        var e = Assert.Throws<ArgumentException>(() => SuiteCatalog.Get("bogus"));

        // Assert
        Assert.Contains("unknown suite 'bogus'", e.Message);
        Assert.Contains("cln", e.Message);
    }

    [Fact]
    public void Given_TheCatalogTable_When_Formatted_Then_EverySuiteHasARow()
    {
        // Act
        var table = MatrixCli.FormatCatalog().TrimEnd('\n').Split('\n');

        // Assert
        Assert.Equal(SuiteCatalog.All.Count + 1, table.Length);
        Assert.StartsWith("SUITE", table[0]);
        Assert.Contains(table, l => l.StartsWith("tor ", StringComparison.Ordinal) && l.Contains("Docker only"));
    }

    private static void AssertOptionsHaveValues(IReadOnlyList<string> args)
    {
        Assert.True(args.Count % 2 == 0, string.Join(' ', args));
        for (var i = 0; i < args.Count; i += 2)
        {
            Assert.StartsWith("-", args[i]);
            Assert.False(args[i + 1].StartsWith('-'), string.Join(' ', args));
        }
    }
}