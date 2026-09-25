using Docker.DotNet;
using LNUnit.Setup;
using Microsoft.Data.SqlClient;

namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// A SQL Server container whose port is published on <c>127.0.0.1</c> (no dependency on the bridge IP being routable
/// from the host, which only OrbStack and Linux provide). The image is x64 only and runs emulated on Apple Silicon,
/// so it gets a generous readiness deadline.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class SqlServerFixture : IDisposable
{
    public const string DefaultContainerName = "sqlserver";
    private const string Image = "mcr.microsoft.com/mssql/server";
    private const string Tag = "2022-latest";
    private const string Password = "Superuser1234*";
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(4);

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();

    public SqlServerFixture() : this(DefaultContainerName)
    {
    }

    private SqlServerFixture(string containerName)
    {
        ContainerName = containerName;
        try
        {
            StartSqlServer().GetAwaiter().GetResult();
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
    /// The host port the container's 1433 is published on (on <c>127.0.0.1</c>).
    /// </summary>
    public int HostPort { get; private set; }

    /// <summary>
    /// Connection string to <c>tempdb</c>.
    /// </summary>
    public string? DbConnectionString { get; private set; }

    /// <summary>
    /// Starts a SQL Server container under another name, for a test that must not share the <c>sqlserver</c>
    /// collection's container. Dispose it when done.
    /// </summary>
    public static SqlServerFixture StartNamed(string containerName) => new(containerName);

    /// <summary>
    /// A connection string to another database on the same server (EF's migrate creates it).
    /// </summary>
    public string ConnectionStringFor(string databaseName)
    {
        ArgumentNullException.ThrowIfNull(DbConnectionString);
        return new SqlConnectionStringBuilder(DbConnectionString) { InitialCatalog = databaseName }.ConnectionString;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        // Remove containers
        DockerContainerUtils.RemoveContainerAsync(_client, ContainerName).GetAwaiter().GetResult();

        _client.Dispose();
    }

    public async Task StartSqlServer()
    {
        await _client.PullImageAndWaitForCompleted(Image, Tag);
        await DockerContainerUtils.RemoveContainerAsync(_client, ContainerName);

        HostPort = await DockerContainerUtils.StartWithLoopbackPortAsync(_client, $"{Image}:{Tag}", ContainerName,
                                                                         1433,
                                                                         [
                                                                             $"MSSQL_SA_PASSWORD={Password}",
                                                                             "ACCEPT_EULA=Y"
                                                                         ]);
        DbConnectionString =
            $"Server=127.0.0.1,{HostPort};Database=tempdb;User Id=sa;Password={Password};Trust Server Certificate=True;";

        await DockerContainerUtils.WaitUntilReadyAsync(ContainerName, async ct =>
        {
            await using var connection = new SqlConnection(DbConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(ct);
        }, s_readyTimeout);
    }

    public bool IsRunning()
    {
        try
        {
            var inspectResult = _client.Containers.InspectContainerAsync(ContainerName).GetAwaiter().GetResult();
            return inspectResult.State.Running;
        }
        catch
        {
            // ignored
        }

        return false;
    }
}

[CollectionDefinition("sqlserver")]
public class SqlServerFixtureCollection : ICollectionFixture<SqlServerFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}