using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Entities.Bitcoin;
using Infrastructure.Persistence.Enums;

public class SilentPaymentPersistenceTests
{
    [Fact]
    public async Task Given_LegacyWallet_When_MigratedAndSilentPaymentsSpent_Then_AllOwnersAndRecoveryMetadataRoundTrip()
    {
        // Arrange
        using var database = new SqliteTestDatabase();

        // Act & Assert
        await SilentPaymentSchemaRoundTrip.AssertAsync(() => database.CreateContext(), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_NoOwnerOrBothOwners_When_SavingUtxo_Then_TheDatabaseRejectsIt(bool bothOwners)
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext();
        var output = SilentPaymentSchemaRoundTrip.Output(10, 100);
        var address = SqliteTestDatabase.CreateWalletAddress();
        if (bothOwners)
        {
            new Infrastructure.Repositories.Database.Bitcoin.WalletAddressesDbRepository(context).AddRange([address]);
            await new Infrastructure.Repositories.Database.Bitcoin.SilentPaymentDbRepository(context)
                .UpsertOutputAsync(output, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        context.Utxos.Add(new UtxoEntity
        {
            TransactionId = output.TransactionId, Index = output.Index, AmountSats = output.AmountSats,
            BlockHeight = output.BlockHeight,
            AddressIndex = bothOwners ? address.Index : null,
            IsAddressChange = bothOwners ? address.IsChange : null,
            AddressType = bothOwners ? address.AddressType : null,
            SilentPaymentTransactionId = bothOwners ? output.TransactionId : null,
            SilentPaymentIndex = bothOwners ? output.Index : null
        });

        // Act & Assert
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_SilentPaymentForAnotherOutpoint_When_UsedAsOwner_Then_TheDatabaseRejectsIt()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext();
        var output = SilentPaymentSchemaRoundTrip.Output(12, 100);
        await new Infrastructure.Repositories.Database.Bitcoin.SilentPaymentDbRepository(context)
            .UpsertOutputAsync(output, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.Utxos.Add(new UtxoEntity
        {
            TransactionId = output.TransactionId, Index = output.Index + 1,
            AmountSats = output.AmountSats, BlockHeight = output.BlockHeight,
            SilentPaymentTransactionId = output.TransactionId, SilentPaymentIndex = output.Index
        });

        // Act & Assert
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

}