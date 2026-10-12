using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// A SQLite database file with the real Sqlite migrations applied. Every context opens a connection of its own to the
/// file, so two service providers over it behave as two processes over one database (no shared connection, SQLite's
/// own file locking between them). The file is deleted on dispose.
/// </summary>
internal sealed class SqliteFileTestDatabase : IDisposable
{
    private readonly string _directory;

    public SqliteFileTestDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nltg-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "nltg.db"),
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();

        using var context = CreateContext();
        context.Database.Migrate();
    }

    public string ConnectionString { get; }

    public NLightningDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(ConnectionString, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;
        return new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best effort
        }
    }
}