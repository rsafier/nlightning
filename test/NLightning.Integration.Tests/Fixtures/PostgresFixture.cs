using Npgsql;

namespace NLightning.Integration.Tests.Fixtures;

using Postgres;

/// <summary>
/// A PostgreSQL server for the database round trips (<c>Docker/PostgresTests</c>) and the server-database restarts: a
/// pod in a run namespace of the Kubernetes harness (<see cref="ClusterPostgresBackend"/>, test harness phase 4) reached
/// at its pod IP, the fixture's only backend since NL-866 retired the Docker container. Without
/// <c>NLTG_TEST_BACKEND=cluster</c> the fixture starts nothing and every test that uses it is skipped with the reason
/// (<see cref="UnavailableReason"/>); with it, a missing Kubernetes configuration fails the fixture (its constructor
/// throws, NL-860). Run the suite with <c>scripts/run-cluster.sh --matrix postgres</c>.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class PostgresFixture : IDisposable
{
    /// <summary>The node name of the <c>postgres</c> collection's server (its run's suite is <c>postgres</c>).</summary>
    public const string DefaultNodeName = "postgres";

    /// <summary>The database the server creates at its first start.</summary>
    public const string DefaultDatabase = "nlightning";

    /// <summary>The superuser and its password (<c>POSTGRES_USER</c>/<c>POSTGRES_PASSWORD</c>).</summary>
    public const string User = "superuser";

    public const string Password = "superuser";

    /// <summary>How long the server may take to answer <c>SELECT 1</c> from this process.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);

    private readonly ClusterAvailability _availability;
    private readonly ClusterPostgresBackend? _backend;
    private readonly string? _connectionString;

    public PostgresFixture() : this(DefaultNodeName, Environment.GetEnvironmentVariable,
                                    ClusterAvailability.KubeConfigurationProbe)
    {
    }

    /// <param name="nodeName">The server's node name.</param>
    /// <param name="environment">Reads environment variables (<see cref="TestBackend.EnvironmentVariable"/>).</param>
    /// <param name="kubeConfiguration">Throws when no Kubernetes configuration can be built.</param>
    /// <param name="skip">Skips the current test with a reason (<see cref="Assert.Skip"/> when null).</param>
    /// <exception cref="InvalidOperationException">
    /// Under <c>NLTG_TEST_BACKEND=cluster</c> without a Kubernetes configuration (NL-860).
    /// </exception>
    internal PostgresFixture(string nodeName, Func<string, string?> environment, Action kubeConfiguration,
                             Action<string>? skip = null)
    {
        NodeName = nodeName;
        _availability = new ClusterAvailability("the Postgres fixture", "NL-866",
                                                "scripts/run-cluster.sh --matrix postgres", environment,
                                                kubeConfiguration, skip);
        if (_availability.UnavailableReason is not null)
        {
            Console.WriteLine($"[fixture] Postgres fixture {NodeName} not started: {UnavailableReason}");
            return;
        }

        _availability.ThrowIfMisconfigured();
        _backend = new ClusterPostgresBackend(nodeName);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _backend.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            _connectionString = BuildConnectionString(_backend.Host, _backend.Port, DefaultDatabase);
            Console.WriteLine($"[fixture] Postgres fixture {NodeName} (cluster) ready in "
                            + $"{watch.Elapsed.TotalSeconds:F1} s");
        }
        catch
        {
            // Dispose is never called on a fixture whose constructor threw: do not leave the namespace behind
            Dispose();
            throw;
        }
    }

    /// <summary>The server's node name.</summary>
    public string NodeName { get; }

    /// <summary>Why the fixture does not run in this process (the skip reason of its tests); null on the cluster.</summary>
    public string? UnavailableReason => _availability.UnavailableReason;

    /// <summary>The host this process connects to (the pod IP).</summary>
    public string Host => Backend.Host;

    /// <summary>The server's port at <see cref="Host"/> (5432).</summary>
    public int HostPort => Backend.Port;

    /// <summary>
    /// Connection string to the <c>nlightning</c> database.
    /// </summary>
    public string DbConnectionString
    {
        get
        {
            _ = Backend;
            return _connectionString ?? throw new InvalidOperationException("The Postgres fixture is not running");
        }
    }

    /// <summary>
    /// Starts a server under another name, for a test that must not share the <c>postgres</c> collection's server (a
    /// run namespace of its own). Skips the current test without <c>NLTG_TEST_BACKEND=cluster</c>. Dispose it when
    /// done.
    /// </summary>
    public static PostgresFixture StartNamed(string nodeName)
    {
        var postgres = new PostgresFixture(nodeName, Environment.GetEnvironmentVariable,
                                           ClusterAvailability.KubeConfigurationProbe);
        postgres.SkipIfUnavailable();
        return postgres;
    }

    /// <summary>
    /// <see cref="StartNamed"/> for a cluster test (<c>Category=Cluster</c>, selected explicitly): it runs on the
    /// cluster whatever <c>NLTG_TEST_BACKEND</c> says, as the other cluster tests do.
    /// </summary>
    public static PostgresFixture StartNamedOnCluster(string nodeName) =>
        new(nodeName, _ => "cluster", ClusterAvailability.KubeConfigurationProbe);

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
    public string ConnectionStringFor(string databaseName) =>
        new NpgsqlConnectionStringBuilder(DbConnectionString) { Database = databaseName }.ConnectionString;

    /// <summary>
    /// Skips the current test when the fixture does not run in this process (<see cref="UnavailableReason"/>).
    /// </summary>
    public void SkipIfUnavailable() => _availability.SkipIfUnavailable();

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        // Delete the run namespace
        _backend?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public Task<bool> IsRunning() => Backend.IsRunningAsync(CancellationToken.None);

    private ClusterPostgresBackend Backend
    {
        get
        {
            SkipIfUnavailable();
            return _backend ?? throw new InvalidOperationException(UnavailableReason);
        }
    }
}

[CollectionDefinition("postgres")]
public class PostgresFixtureCollection : ICollectionFixture<PostgresFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}