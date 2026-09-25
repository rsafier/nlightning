using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Bitcoin;
using Infrastructure.Repositories.Memory;

public class UnitOfWorkUtxoTests
{
    [Fact]
    public async Task Given_SaveChangesFails_When_AddUtxo_Then_MemoryRepositoryIsUnchanged()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var memoryRepository = new UtxoMemoryRepository();
        // No wallet address row: the utxo FK makes SaveChanges fail
        var utxo = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress());
        using var unitOfWork = CreateUnitOfWork(database.CreateContext(), memoryRepository);

        // Act
        unitOfWork.AddUtxo(utxo);
        await Assert.ThrowsAsync<DbUpdateException>(() => unitOfWork.SaveChangesAsync());

        // Assert
        Assert.False(memoryRepository.TryGetUtxo(utxo.TxId, utxo.Index, out _));
    }

    [Fact]
    public async Task Given_SaveChangesSucceeds_When_AddUtxo_Then_MemoryAndDatabaseContainUtxo()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var memoryRepository = new UtxoMemoryRepository();
        var walletAddress = SqliteTestDatabase.CreateWalletAddress();
        var utxo = SqliteTestDatabase.CreateUtxo(walletAddress);
        using var unitOfWork = CreateUnitOfWork(database.CreateContext(), memoryRepository);
        unitOfWork.WalletAddressesDbRepository.AddRange([walletAddress]);

        // Act
        unitOfWork.AddUtxo(utxo);
        var inMemoryBeforeSave = memoryRepository.TryGetUtxo(utxo.TxId, utxo.Index, out _);
        await unitOfWork.SaveChangesAsync();

        // Assert
        Assert.False(inMemoryBeforeSave);
        Assert.True(memoryRepository.TryGetUtxo(utxo.TxId, utxo.Index, out _));
        await using var readContext = database.CreateContext();
        Assert.NotNull(await new UtxoDbRepository(readContext).GetByIdAsync(utxo.TxId, utxo.Index));
    }

    [Fact]
    public async Task Given_SaveChangesFails_When_TrySpendUtxo_Then_MemoryRepositoryStillHasUtxo()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var memoryRepository = new UtxoMemoryRepository();
        // The utxo only exists in memory, so deleting it from the database affects 0 rows and SaveChanges fails
        var utxo = SqliteTestDatabase.CreateUtxo(SqliteTestDatabase.CreateWalletAddress());
        memoryRepository.Add(utxo);
        using var unitOfWork = CreateUnitOfWork(database.CreateContext(), memoryRepository);

        // Act
        unitOfWork.TrySpendUtxo(utxo.TxId, utxo.Index);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => unitOfWork.SaveChangesAsync());

        // Assert
        Assert.True(memoryRepository.TryGetUtxo(utxo.TxId, utxo.Index, out _));
    }

    [Fact]
    public async Task Given_UtxoAddedInSameUnitOfWork_When_TrySpendUtxo_Then_NeitherMemoryNorDatabaseHasIt()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var memoryRepository = new UtxoMemoryRepository();
        var walletAddress = SqliteTestDatabase.CreateWalletAddress();
        var utxo = SqliteTestDatabase.CreateUtxo(walletAddress);
        using var unitOfWork = CreateUnitOfWork(database.CreateContext(), memoryRepository);
        unitOfWork.WalletAddressesDbRepository.AddRange([walletAddress]);
        unitOfWork.AddUtxo(utxo);

        // Act
        unitOfWork.TrySpendUtxo(utxo.TxId, utxo.Index);
        await unitOfWork.SaveChangesAsync();

        // Assert
        Assert.False(memoryRepository.TryGetUtxo(utxo.TxId, utxo.Index, out _));
        await using var readContext = database.CreateContext();
        Assert.Null(await new UtxoDbRepository(readContext).GetByIdAsync(utxo.TxId, utxo.Index));
    }

    [Fact]
    public async Task Given_SavedUtxo_When_TrySpendUtxoInANewUnitOfWork_Then_NeitherMemoryNorDatabaseHasIt()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var memoryRepository = new UtxoMemoryRepository();
        var walletAddress = SqliteTestDatabase.CreateWalletAddress();
        var utxo = SqliteTestDatabase.CreateUtxo(walletAddress);
        using (var setupUnitOfWork = CreateUnitOfWork(database.CreateContext(), memoryRepository))
        {
            setupUnitOfWork.WalletAddressesDbRepository.AddRange([walletAddress]);
            setupUnitOfWork.AddUtxo(utxo);
            await setupUnitOfWork.SaveChangesAsync();
        }

        using var unitOfWork = CreateUnitOfWork(database.CreateContext(), memoryRepository);

        // Act
        unitOfWork.TrySpendUtxo(utxo.TxId, utxo.Index);
        await unitOfWork.SaveChangesAsync();

        // Assert
        Assert.False(memoryRepository.TryGetUtxo(utxo.TxId, utxo.Index, out _));
        await using var readContext = database.CreateContext();
        Assert.Null(await new UtxoDbRepository(readContext).GetByIdAsync(utxo.TxId, utxo.Index));
    }

    private static UnitOfWork CreateUnitOfWork(NLightningDbContext context, UtxoMemoryRepository memoryRepository)
    {
        return new UnitOfWork(context, new Mock<ILogger<UnitOfWork>>().Object, new Mock<ISha256>().Object,
                              memoryRepository);
    }
}