namespace NLightning.Integration.Tests.Fixtures.Postgres;

/// <summary>The pure parts of <see cref="PostgresFixture"/> and its cluster backend (no cluster call).</summary>
public class PostgresBackendTests
{
    [Fact]
    public void Given_HostPortAndDatabase_When_TheConnectionStringIsBuilt_Then_ItKeepsTheFixturesFormat()
    {
        // Act
        var connectionString = PostgresFixture.BuildConnectionString("192.168.194.14", 5432,
                                                                     PostgresFixture.DefaultDatabase);

        // Assert: PostgresTests swaps "Database=nlightning" for its own databases
        Assert.Equal("Host=192.168.194.14;Port=5432;Database=nlightning;Username=superuser;Password=superuser",
                     connectionString);
        Assert.Contains("Database=nlightning", connectionString);
    }

    [Theory]
    [InlineData(PostgresFixture.DefaultNodeName, "postgres")]
    [InlineData("nltg-harness-postgres", "postgres-nltg-harness-postgres")]
    public void Given_AContainerName_When_TheClusterSuiteIsNamed_Then_TheCollectionsServerIsPostgres(string name,
        string suite)
    {
        // Act / Assert
        Assert.Equal(suite, ClusterPostgresBackend.SuiteFor(name));
    }

    [Theory]
    [InlineData("postgres", "postgres")]
    [InlineData("nltg-harness-postgres", "nltg-harness-postgres")]
    [InlineData("Not_A_Label", "postgres")]
    [InlineData("a-name-that-is-far-too-long-for-a-statefulset-of-the-harness", "postgres")]
    public void Given_AContainerName_When_TheClusterNodeIsNamed_Then_ItIsAValidWorkloadName(string name, string node)
    {
        // Act / Assert
        Assert.Equal(node, ClusterPostgresBackend.NodeNameFor(name));
    }

    [Fact]
    public void Given_AClusterBackend_When_Created_Then_ItStartsNothing()
    {
        // Act
        var cluster = new ClusterPostgresBackend("unused");

        // Assert
        Assert.Equal(5432, cluster.Port);
        Assert.Throws<InvalidOperationException>(() => cluster.Host);
    }

    [Fact]
    public void Given_NoClusterOptIn_When_TheFixtureIsBuilt_Then_ItStartsNothingAndEveryMemberSkips()
    {
        // Arrange: a recording skip (Assert.Skip would skip this test itself)
        var skips = new List<string>();
        var probed = false;

        // Act
        using var fixture = new PostgresFixture(PostgresFixture.DefaultNodeName, _ => null, () => probed = true,
                                                reason =>
                                                {
                                                    skips.Add(reason);
                                                    throw new TestSkipped(reason);
                                                });

        // Assert: no Kubernetes configuration read, and each member skips with the reason (NL-866)
        Assert.False(probed);
        Assert.Contains("cluster backend only (NL-866)", fixture.UnavailableReason);
        Assert.Throws<TestSkipped>(() => fixture.DbConnectionString);
        Assert.Throws<TestSkipped>(() => fixture.ConnectionStringFor("other"));
        Assert.Throws<TestSkipped>(() => fixture.Host);
        Assert.Throws<TestSkipped>(() => fixture.HostPort);
        Assert.Equal(4, skips.Count);
    }

    [Fact]
    public void Given_TheClusterOptInWithoutAKubeConfiguration_When_TheFixtureIsBuilt_Then_ItThrowsInsteadOfSkipping()
    {
        // Act
        var error = Assert.Throws<InvalidOperationException>(
            () => new PostgresFixture(PostgresFixture.DefaultNodeName, _ => "cluster",
                                      () => throw new FileNotFoundException("no kubeconfig"),
                                      reason => Assert.Fail($"skipped: {reason}")));

        // Assert (NL-860)
        Assert.Contains("no Kubernetes cluster is configured to run the Postgres fixture on", error.Message);
    }

    private sealed class TestSkipped(string reason) : Exception(reason);
}