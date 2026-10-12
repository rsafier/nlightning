using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NLightning.Integration.Tests.Docker;

using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;

public partial class PostgresTests
{
    [Fact]
    public async Task Given_NoResetOnClose_When_AConnectionReturnsWithAnOpenTransaction_Then_TheNextUserFindsItRolledBack()
    {
        // Arrange (NL-1404: the node's own composition, pool of one so the next scope gets the same backend)
        var ct = TestContext.Current.CancellationToken;
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionStringFor("nltg_no_reset"))
        {
            MaxPoolSize = 1
        }.ConnectionString;
        await using var provider = BuildNode(connectionString);
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
            Assert.True(new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()).NoResetOnClose);
            // EF's migration lock must end with the migration (it takes no session-level lock)
            await context.Database.MigrateAsync(ct);
        }

        int migratePid;
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
            await context.Database.OpenConnectionAsync(ct);
            var connection = context.Database.GetDbConnection();
            migratePid = await ScalarAsync<int>(connection, "SELECT pg_backend_pid()", ct);
            Assert.Equal(0L, await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory'", ct));
            Assert.Equal(0L, await ScalarAsync<long>(connection,
                                                    "SELECT count(*) FROM pg_stat_activity WHERE datname = "
                                                  + "current_database() AND state LIKE 'idle in transaction%'", ct));
        }

        // Act: a transaction that writes and takes a transaction-scoped lock is left open when its scope ends
        int openPid;
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
            await context.Database.BeginTransactionAsync(ct);
            var connection = context.Database.GetDbConnection();
            openPid = await ScalarAsync<int>(connection, "SELECT pg_backend_pid()", ct);
            await ExecuteAsync(connection, "CREATE TABLE nl1404_probe (x int)", ct);
            await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(1404)", ct);
        }

        // Assert: the next user of the same pooled backend sees no transaction, no table and no lock
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
            await context.Database.OpenConnectionAsync(ct);
            var connection = context.Database.GetDbConnection();
            var pid = await ScalarAsync<int>(connection, "SELECT pg_backend_pid()", ct);
            Assert.Equal(migratePid, openPid);
            Assert.Equal(openPid, pid);
            Assert.True(await ScalarAsync<bool>(connection, "SELECT to_regclass('nl1404_probe') IS NULL", ct));
            Assert.Equal(0L, await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory'", ct));
            Assert.Equal(0L, await ScalarAsync<long>(connection,
                                                    "SELECT count(*) FROM pg_stat_activity WHERE datname = "
                                                  + "current_database() AND state LIKE 'idle in transaction%'", ct));
        }
    }

    private static ServiceProvider BuildNode(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Database:Provider"] = "Postgres",
                               ["Database:ConnectionString"] = connectionString
                           })
                           .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistenceInfrastructureServices(configuration);
        return services.BuildServiceProvider();
    }

    private static async Task<T> ScalarAsync<T>(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}