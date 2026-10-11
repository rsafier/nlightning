namespace NLightning.Integration.Tests.Docker.Utils;

/// <summary>
/// The database providers an <see cref="NLightningTestNode"/> can run on.
/// </summary>
public enum TestDatabaseProvider
{
    Sqlite,
    Postgres,
    SqlServer
}

/// <summary>
/// The database of an <see cref="NLightningTestNode"/>: the provider and the connection string the daemon's
/// <c>Database</c> section gets. The node migrates it on every start, which also creates a missing Postgres or SQL
/// Server database, so give every node its own database name.
/// </summary>
public sealed record TestNodeDatabase(TestDatabaseProvider Provider, string ConnectionString)
{
    /// <summary>
    /// The SQLite file, or <c>null</c> for a server database.
    /// </summary>
    public string? SqliteFilePath { get; private init; }

    /// <summary>
    /// The value of the daemon's <c>Database:Provider</c> setting.
    /// </summary>
    public string ConfigurationProviderName => Provider switch
    {
        TestDatabaseProvider.Sqlite => "Sqlite",
        TestDatabaseProvider.Postgres => "Postgres",
        TestDatabaseProvider.SqlServer => "SqlServer",
        _ => throw new ArgumentOutOfRangeException(nameof(Provider), Provider, null)
    };

    public static TestNodeDatabase Sqlite(string filePath) =>
        new(TestDatabaseProvider.Sqlite, $"Data Source={filePath}") { SqliteFilePath = filePath };

    /// <param name="connectionString">
    /// A connection string naming the node's own database, e.g. from <c>PostgresFixture.ConnectionStringFor</c>.
    /// </param>
    public static TestNodeDatabase Postgres(string connectionString) =>
        new(TestDatabaseProvider.Postgres, connectionString);

    /// <param name="connectionString">
    /// A connection string naming the node's own database, e.g. from <c>SqlServerFixture.ConnectionStringFor</c>.
    /// </param>
    public static TestNodeDatabase SqlServer(string connectionString) =>
        new(TestDatabaseProvider.SqlServer, connectionString);
}