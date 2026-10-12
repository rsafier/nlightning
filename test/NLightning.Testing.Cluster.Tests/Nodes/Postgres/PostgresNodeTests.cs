namespace NLightning.Testing.Cluster.Tests.Nodes.Postgres;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Nodes.Postgres;
using Cluster.Run;

public class PostgresNodeTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "postgres" }, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Given_DefaultOptions_When_TheWorkloadIsBuilt_Then_ItRunsThePinnedImageWithTheFixturesCredentials()
    {
        // Act
        var workload = PostgresNode.Workload(new PostgresNodeOptions());

        // Assert
        Assert.Equal("postgres", workload.Name);
        Assert.Equal(NodeKind.Postgres, workload.Kind);
        Assert.Equal(ImageVersions.Postgres, workload.Image);
        Assert.NotNull(ImageVersions.Postgres.Digest);
        Assert.Equal("superuser", workload.Env["POSTGRES_USER"]);
        Assert.Equal("superuser", workload.Env["POSTGRES_PASSWORD"]);
        Assert.Equal("nlightning", workload.Env["POSTGRES_DB"]);
        Assert.Equal("/var/lib/postgresql/data/pgdata", workload.Env["PGDATA"]);
        Assert.Equal(new WorkloadPort("postgres", 5432), Assert.Single(workload.Ports));
        Assert.Equal(new DataVolume("/var/lib/postgresql/data", "1Gi", Storage: NodeStorage.Ephemeral), workload.Data);
        Assert.Empty(workload.Args);
        Assert.Null(workload.Command);
    }

    [Fact]
    public void Given_AWorkload_When_Built_Then_ItIsReadyOnlyWhenTheServerAnswersOverTcp()
    {
        // Act
        var container = PostgresNode.Workload(new PostgresNodeOptions()).Build(s_run).StatefulSet.Spec.Template.Spec
                                    .Containers[0];

        // Assert: TCP, not the Unix socket the image's temporary init server listens on
        Assert.Equal(["pg_isready", "-h", "127.0.0.1", "-p", "5432", "-U", "superuser", "-d", "nlightning"],
                     container.ReadinessProbe.Exec.Command);
        Assert.Equal(1, container.ReadinessProbe.PeriodSeconds);
    }

    [Fact]
    public void Given_APersistentServer_When_TheWorkloadIsBuilt_Then_ItsDataIsOnAPvc()
    {
        // Act
        var workload = PostgresNode.Workload(new PostgresNodeOptions
        {
            Name = "pg2",
            Storage = NodeStorage.Persistent,
            Database = "other"
        });

        // Assert
        Assert.False(workload.Data!.IsEphemeral);
        Assert.Equal("pg2", workload.Name);
        Assert.Equal("other", workload.Env["POSTGRES_DB"]);
    }

    [Fact]
    public void Given_HostAndDatabase_When_TheConnectionStringIsBuilt_Then_ItNamesThemWithTheCredentials()
    {
        // Act
        var connectionString = PostgresNode.BuildConnectionString("10.42.0.7", 5432, "nltg_x", "superuser", "pw");

        // Assert
        Assert.Equal("Host=10.42.0.7;Port=5432;Database=nltg_x;Username=superuser;Password=pw", connectionString);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Given_AnEmptyDatabase_When_TheWorkloadIsBuilt_Then_ItThrows(string database)
    {
        // Act / Assert
        Assert.ThrowsAny<ArgumentException>(() => PostgresNode.Workload(new PostgresNodeOptions { Database = database }));
    }
}