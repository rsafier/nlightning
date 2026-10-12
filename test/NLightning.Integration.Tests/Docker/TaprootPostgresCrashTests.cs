using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NLightning.Integration.Tests.Docker;

using Application.Tests.Channels.Taproot;
using Fixtures;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// Plan D-T4's crash proof of a simple taproot channel on Postgres (NL-960): <see cref="TaprootCrashProof"/>, linked
/// from Application.Tests with its two-node harness, with every node on a database of its own on the <c>postgres</c>
/// collection's server (a pod of the cluster's <c>postgres</c> suite). The SQLite run is Application.Tests'
/// <c>TaprootSqliteCrashTests</c>.
/// </summary>
[Collection("postgres")]
[Trait("Database", "Postgres")]
public class TaprootPostgresCrashTests
{
    private readonly PostgresFixture _fixture;

    public TaprootPostgresCrashTests(PostgresFixture fixture)
    {
        fixture.SkipIfUnavailable(); // the fixture runs on the cluster only (NL-866)
        _fixture = fixture;
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("Bob")]
    public async Task Given_ADatabaseCrashAtEverySave_When_TheNodeRestarts_Then_NoNonceIsReusedAndTheChannelGoesOn(
        string crashing)
    {
        // Arrange - the schema once, in a template every node database is copied from
        var template = await PostgresTaprootHarnessDatabase.CreateTemplateAsync(_fixture,
                                                                               TestContext.Current.CancellationToken);

        // Act and Assert: one run per save of the crashing node, each on new databases
        var saves = await TaprootCrashProof.RunAsync(crashing,
                                                     () => new PostgresTaprootHarnessDatabase(_fixture, template));
        Console.WriteLine($"[taproot-postgres] {crashing}: {saves} crash points green");
    }
}

/// <summary>
/// The harness nodes' databases on the Postgres fixture's server: each a copy of a migrated template database
/// (<c>CREATE DATABASE ... TEMPLATE</c>), dropped when the harness is disposed.
/// </summary>
internal sealed class PostgresTaprootHarnessDatabase : ITaprootHarnessDatabase
{
    private const string TemplateDatabase = "nltg_taproot_template";

    private readonly PostgresFixture _fixture;
    private readonly string _template;
    private readonly List<string> _databases = [];

    public PostgresTaprootHarnessDatabase(PostgresFixture fixture, string template)
    {
        _fixture = fixture;
        _template = template;
    }

    public string Provider => "postgres";
    public bool CreatesMigrated => true;

    /// <summary>Creates (again) the template database with the latest schema.</summary>
    /// <returns>Its name.</returns>
    public static async Task<string> CreateTemplateAsync(PostgresFixture fixture, CancellationToken cancellationToken)
    {
        await WaitForServerAsync(fixture, cancellationToken);
        await ExecuteAsync(fixture, $"DROP DATABASE IF EXISTS {TemplateDatabase} WITH (FORCE)", cancellationToken);

        var connectionString = fixture.ConnectionStringFor(TemplateDatabase);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseNpgsql(connectionString,
                                x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Postgres"))
                     .UseSnakeCaseNamingConvention()
                     .Options;
        await using (var context = new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.PostgreSql)))
        {
            await context.Database.MigrateAsync(cancellationToken);
        }

        // A template must have no open session when it is copied
        await using (var connection = new NpgsqlConnection(connectionString))
            NpgsqlConnection.ClearPool(connection);
        return TemplateDatabase;
    }

    public async Task<string> CreateAsync(string name)
    {
        var database = $"nltg_taproot_{name}_{Guid.NewGuid():N}";
        await ExecuteAsync(_fixture, $"CREATE DATABASE {database} TEMPLATE {_template}", CancellationToken.None);
        _databases.Add(database);
        return _fixture.ConnectionStringFor(database);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var database in _databases)
        {
            await using (var connection = new NpgsqlConnection(_fixture.ConnectionStringFor(database)))
                NpgsqlConnection.ClearPool(connection);
            await ExecuteAsync(_fixture, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)", CancellationToken.None);
        }
    }

    /// <summary>Runs <paramref name="sql"/> on the fixture's own database (database commands cannot run in a
    /// transaction, so one statement per call).</summary>
    private static async Task ExecuteAsync(PostgresFixture fixture, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.DbConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Postgres may still be starting after its port opens.</summary>
    private static async Task WaitForServerAsync(PostgresFixture fixture, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + PostgresFixture.ReadyTimeout;
        while (true)
        {
            try
            {
                await PostgresFixture.SelectOneAsync(fixture.DbConnectionString, cancellationToken);
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }
}