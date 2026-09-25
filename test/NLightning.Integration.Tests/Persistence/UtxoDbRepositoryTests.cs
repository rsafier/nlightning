namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Infrastructure.Repositories.Database.Bitcoin;

public class UtxoDbRepositoryTests
{
    [Fact]
    public async Task Given_SavedUtxo_When_GetByIdAsync_Then_ReturnsUtxo()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var walletAddress = SqliteTestDatabase.CreateWalletAddress();
        var utxo = SqliteTestDatabase.CreateUtxo(walletAddress, index: 3);
        await using (var context = database.CreateContext())
        {
            new WalletAddressesDbRepository(context).AddRange([walletAddress]);
            new UtxoDbRepository(context).Add(utxo);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readContext = database.CreateContext();
        var repository = new UtxoDbRepository(readContext);

        // Act
        var result = await repository.GetByIdAsync(utxo.TxId, utxo.Index, true);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(utxo.TxId, result.TxId);
        Assert.Equal(utxo.Index, result.Index);
        Assert.Equal(utxo.Amount, result.Amount);
        Assert.NotNull(result.WalletAddress);
        Assert.Equal(walletAddress.Address, result.WalletAddress.Address);
    }

    [Fact]
    public async Task Given_UtxoLockedToChannelAndUsedInTransaction_When_Reloaded_Then_FieldsArePreserved()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var walletAddress = SqliteTestDatabase.CreateWalletAddress();
        var utxo = SqliteTestDatabase.CreateUtxo(walletAddress);
        var channelIdBytes = new byte[32];
        Array.Fill(channelIdBytes, (byte)0xAB);
        var channelId = new ChannelId(channelIdBytes);
        var usedInTxIdBytes = new byte[32];
        Array.Fill(usedInTxIdBytes, (byte)0xCD);
        var usedInTxId = new TxId(usedInTxIdBytes);
        utxo.LockedToChannelId = channelId;
        utxo.UsedInTransactionId = usedInTxId;

        await using (var context = database.CreateContext())
        {
            new WalletAddressesDbRepository(context).AddRange([walletAddress]);
            new UtxoDbRepository(context).Add(utxo);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readContext = database.CreateContext();
        var repository = new UtxoDbRepository(readContext);

        // Act
        var result = (await repository.GetUnspentAsync()).Single();

        // Assert
        Assert.Equal(channelId, result.LockedToChannelId);
        Assert.Equal(usedInTxId, result.UsedInTransactionId);
    }
}