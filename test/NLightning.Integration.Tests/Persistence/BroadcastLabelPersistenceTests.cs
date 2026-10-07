using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Repositories.Database.Onchain;

/// <summary>
/// NL-1186: the label LND's walletrpc <c>LabelTransaction</c> sets on one of our broadcasts is saved on its row, in any
/// state, and a missing row is reported; a collaborative wallet transaction round-trips with its purpose.
/// </summary>
public class BroadcastLabelPersistenceTests
{
    [Fact]
    public async Task Given_AStoredBroadcast_When_Labelled_Then_TheLabelIsSavedAndAMissingRowIsReported()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;
        NLightningDbContext Context() => new(options, new DatabaseTypeProvider(DatabaseType.Sqlite));
        var txId = new TxId(Enumerable.Repeat((byte)0x5A, 32).ToArray());
        await using (var context = Context())
        {
            await context.Database.MigrateAsync(ct);
            new BroadcastTransactionDbRepository(context).Add(
                new BroadcastTransactionModel(new SignedTransaction(txId, [1, 2, 3]),
                                              BroadcastPurpose.WalletCollaborative, null, 100)
                {
                    Label = "old"
                });
            await context.SaveChangesAsync(ct);
        }

        // Act
        bool labelled, missing;
        await using (var context = Context())
        {
            var repository = new BroadcastTransactionDbRepository(context);
            labelled = await repository.SetLabelAsync(txId, "new label");
            missing = await repository.SetLabelAsync(new TxId(new byte[32]), "nothing");
            await context.SaveChangesAsync(ct);
        }

        // Assert
        Assert.True(labelled);
        Assert.False(missing);
        await using (var context = Context())
        {
            var row = await new BroadcastTransactionDbRepository(context).GetByTransactionIdAsync(txId);
            Assert.NotNull(row);
            Assert.Equal("new label", row.Label);
            Assert.Equal(BroadcastPurpose.WalletCollaborative, row.Purpose);
            Assert.Null(row.Fee);
        }
    }
}