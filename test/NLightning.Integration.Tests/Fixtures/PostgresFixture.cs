using Npgsql;

namespace NLightning.Integration.Tests.Fixtures;

using Postgres;

/// <summary>
/// A PostgreSQL server for the database round trips (<c>Docker/PostgresTests</c>) and the server-database restarts.
/// Where it runs is <see cref="TestBackend"/>'s (<c>NLTG_TEST_BACKEND</c>): a Docker container with its port published
/// on <c>127.0.0.1</c> by default (<see cref="DockerPostgresBackend"/>; no dependency on the bridge IP being routable
/// from the host, which only OrbStack and Linux provide), or a pod in a run namespace of the Kubernetes harness
/// (<see cref="ClusterPostgresBackend"/>, test harness phase 4) reached at its pod IP. Same image release, database,
/// user and password on both; the tests see the same members.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class PostgresFixture : IDisposable
{
    public const string DefaultContainerName = "postgres";

    /// <summary>The database the server creates at its first start.</summary>
    public const string DefaultDatabase = "nlightning";

    /// <summary>The superuser and its password (<c>POSTGRES_USER</c>/<c>POSTGRES_PASSWORD</c>).</summary>
    public const string User = "superuser";

    public const string Password = "superuser";

    /// <summary>How long the server may take to answer <c>SELECT 1</c> from this process.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);

    private readonly IPostgresBackend _backend;

    public PostgresFixture() : this(DefaultContainerName)
    {
    }

    private PostgresFixture(string containerName)
    {
        ContainerName = containerName;
        _backend = TestBackend.Current == TestBackendKind.Cluster
                       ? new ClusterPostgresBackend(containerName)
                       : new DockerPostgresBackend(containerName);
        try
        {
            StartPostgres().GetAwaiter().GetResult();
        }
        catch
        {
            // Dispose is never called on a fixture whose constructor threw: do not leave the server behind
            Dispose();
            throw;
        }
    }

    /// <summary>The container (Docker) or node (cluster) name.</summary>
    public string ContainerName { get; }

    /// <summary>Where the server runs (<see cref="TestBackend.Current"/> when the fixture was created).</summary>
    public TestBackendKind Backend => _backend.Kind;

    /// <summary>The host this process connects to (<c>127.0.0.1</c> on Docker, the pod IP on the cluster).</summary>
    public string Host => _backend.Host;

    /// <summary>
    /// The port at <see cref="Host"/>: the host port the container's 5432 is published on (Docker), or 5432 (cluster).
    /// </summary>
    public int HostPort => _backend.Port;

    /// <summary>
    /// Connection string to the <c>nlightning</c> database.
    /// </summary>
    public string? DbConnectionString { get; private set; }

    /// <summary>
    /// Starts a server under another name, for a test that must not share the <c>postgres</c> collection's server (a
    /// container of its own on Docker, a run namespace of its own on the cluster). Dispose it when done.
    /// </summary>
    public static PostgresFixture StartNamed(string containerName) => new(containerName);

    /// <summary>
    /// An Npgsql connection string to <paramref name="database"/> at <paramref name="host"/> (the format the fixture
    /// always had: <c>PostgresTests</c> swaps <c>Database=nlightning</c> for its own databases).
    /// </summary>
    public static string BuildConnectionString(string host, int port, string database) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
                      $"Host={host};Port={port};Database={database};Username={User};Password={Password}");

    /// <summary>Opens a connection and runs <c>SELECT 1</c> (the readiness check of both backends).</summary>
    public static async Task SelectOneAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// A connection string to another database on the same server (EF's migrate creates it).
    /// </summary>
    public string ConnectionStringFor(string databaseName)
    {
        ArgumentNullException.ThrowIfNull(DbConnectionString);
        return new NpgsqlConnectionStringBuilder(DbConnectionString) { Database = databaseName }.ConnectionString;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        // Remove the container, or delete the run namespace
        _backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async Task StartPostgres()
    {
        await _backend.StartAsync(CancellationToken.None);
        DbConnectionString = BuildConnectionString(Host, HostPort, DefaultDatabase);
    }

    public Task<bool> IsRunning() => _backend.IsRunningAsync(CancellationToken.None);
}

[CollectionDefinition("postgres")]
public class PostgresFixtureCollection : ICollectionFixture<PostgresFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}