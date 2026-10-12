using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// NL-1390: migration <c>AddSharedDatabaseHardening</c> makes the ledger sequence unique. On SQLite, a database whose
/// chain two sealers already forked (one sequence on two rows) is refused by its guard, named after the repair
/// procedure, before the index is created; a database without a fork migrates.
/// </summary>
public sealed class LedgerSeqMigrationGuardTests : IDisposable
{
    private const string PreviousMigration = "20261011062023_AddSentClosingSigned";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public LedgerSeqMigrationGuardTests()
    {
        _connection.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public void Given_AForkedLedgerSequence_When_Migrated_Then_TheGuardRefusesWithTheRepairProcedure()
    {
        // Arrange: two rows sealed with ledger sequence 1
        using var context = CreateContext();
        context.GetService<IMigrator>().Migrate(PreviousMigration);
        InsertSealed(context, "a", 1);
        InsertSealed(context, "b", 1);

        // Act
        var refused = Assert.ThrowsAny<Exception>(() => context.Database.Migrate());

        // Assert
        Assert.Contains("Forked_ledger_repair_NL_1390", refused.ToString());
        Assert.Contains(PreviousMigration, context.Database.GetAppliedMigrations().Last());
    }

    [Fact]
    public void Given_ALinearLedgerSequence_When_Migrated_Then_TheUniqueIndexIsCreated()
    {
        // Arrange
        using var context = CreateContext();
        context.GetService<IMigrator>().Migrate(PreviousMigration);
        InsertSealed(context, "a", 1);
        InsertSealed(context, "b", 2);

        // Act
        context.Database.Migrate();

        // Assert: a second row with sequence 2 is refused now
        Assert.Throws<SqliteException>(() => InsertSealed(context, "c", 2));
    }

    private static void InsertSealed(NLightningDbContext context, string key, long ledgerSeq) =>
        context.Database.ExecuteSql(
            $"""INSERT INTO "AccountingEvents" ("EventKey", "Kind", "OccurredAt", "AmountMsat", "FeeMsat", "Finality", "Flags", "LedgerSeq", "Hash") VALUES ({key}, 1, 0, 1000, 0, 0, 0, {ledgerSeq}, {new byte[32]})""");

    private NLightningDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(_connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;
        return new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite));
    }
}