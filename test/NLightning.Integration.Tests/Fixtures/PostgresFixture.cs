using Docker.DotNet;
using LNUnit.Setup;
using Npgsql;

namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// A Postgres container whose port is published on <c>127.0.0.1</c> (no dependency on the bridge IP being routable
/// from the host, which only OrbStack and Linux provide).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class PostgresFixture : IDisposable
{
    public const string DefaultContainerName = "postgres";
    private const string Image = "postgres";
    private const string Tag = "16.2-alpine";
    private const string DefaultDatabase = "nlightning";
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();

    public PostgresFixture() : this(DefaultContainerName)
    {
    }

    private PostgresFixture(string containerName)
    {
        ContainerName = containerName;
        try
        {
            StartPostgres().GetAwaiter().GetResult();
        }
        catch
        {
            // Dispose is never called on a fixture whose constructor threw: do not leave the container behind
            Dispose();
            throw;
        }
    }

    public string ContainerName { get; }

    /// <summary>
    /// The host port the container's 5432 is published on (on <c>127.0.0.1</c>).
    /// </summary>
    public int HostPort { get; private set; }

    /// <summary>
    /// Connection string to the <c>nlightning</c> database.
    /// </summary>
    public string? DbConnectionString { get; private set; }

    /// <summary>
    /// Starts a Postgres container under another name, for a test that must not share the <c>postgres</c> collection's
    /// container. Dispose it when done.
    /// </summary>
    public static PostgresFixture StartNamed(string containerName) => new(containerName);

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

        // Remove containers
        DockerContainerUtils.RemoveContainerAsync(_client, ContainerName).GetAwaiter().GetResult();

        _client.Dispose();
    }

    public async Task StartPostgres()
    {
        await _client.PullImageAndWaitForCompleted(Image, Tag);
        await DockerContainerUtils.RemoveContainerAsync(_client, ContainerName);

        HostPort = await DockerContainerUtils.StartWithLoopbackPortAsync(_client, $"{Image}:{Tag}", ContainerName,
                                                                         5432,
                                                                         [
                                                                             "POSTGRES_PASSWORD=superuser",
                                                                             "POSTGRES_USER=superuser",
                                                                             $"POSTGRES_DB={DefaultDatabase}"
                                                                         ]);
        DbConnectionString =
            $"Host=127.0.0.1;Port={HostPort};Database={DefaultDatabase};Username=superuser;Password=superuser";

        // Postgres restarts once after initdb, so a successful query is the only reliable readiness signal
        await DockerContainerUtils.WaitUntilReadyAsync(ContainerName, async ct =>
        {
            await using var connection = new NpgsqlConnection(DbConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(ct);
        }, s_readyTimeout);
    }

    public async Task<bool> IsRunning()
    {
        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(ContainerName);
            return inspect.State.Running;
        }
        catch
        {
            // ignored
        }

        return false;
    }
}

[CollectionDefinition("postgres")]
public class PostgresFixtureCollection : ICollectionFixture<PostgresFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}