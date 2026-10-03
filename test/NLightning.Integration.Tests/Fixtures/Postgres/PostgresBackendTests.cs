namespace NLightning.Integration.Tests.Fixtures.Postgres;

/// <summary>The pure parts of <see cref="PostgresFixture"/>'s two backends (no container, no cluster).</summary>
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
    [InlineData(PostgresFixture.DefaultContainerName, "postgres")]
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
    public void Given_AClusterBackend_When_Created_Then_ItReportsItsKindWithoutStarting()
    {
        // Act
        var cluster = new ClusterPostgresBackend("unused");

        // Assert
        Assert.Equal(TestBackendKind.Cluster, cluster.Kind);
        Assert.Equal(5432, cluster.Port);
        Assert.Throws<InvalidOperationException>(() => cluster.Host);
    }
}