namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Protocol.Models;
using Infrastructure.Repositories.Database.Bitcoin;
using Infrastructure.Repositories.Database.Channel;

public class ChannelDbRepositoryTests
{
    private static async Task<ChannelModel> SaveAndReloadAsync(SqliteDbTestContext db, ChannelModel channel)
    {
        await using (var writeContext = db.CreateDbContext())
        {
            var repository = new ChannelDbRepository(writeContext, db.MessageSerializer, db.Sha256);
            await repository.AddAsync(channel);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readContext = db.CreateDbContext();
        var readRepository = new ChannelDbRepository(readContext, db.MessageSerializer, db.Sha256);
        return await readRepository.GetByIdAsync(channel.ChannelId)
            ?? throw new InvalidOperationException("Channel was not reloaded");
    }

    [Fact]
    public async Task Given_ChannelWithHtlcsInEveryState_When_Reloaded_Then_HtlcsLandInTheRightLists()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = SqliteDbTestContext.CreateChannel(true).ChannelId;
        var channel = SqliteDbTestContext.CreateChannel(
            true,
            localOffered: [SqliteDbTestContext.CreateHtlc(channelId, 0, HtlcDirection.Outgoing, HtlcState.Offered)],
            localFulfilled:
            [
                SqliteDbTestContext.CreateHtlc(channelId, 1, HtlcDirection.Outgoing, HtlcState.Fulfilled)
            ],
            localOld:
            [
                SqliteDbTestContext.CreateHtlc(channelId, 2, HtlcDirection.Outgoing, HtlcState.Failed),
                SqliteDbTestContext.CreateHtlc(channelId, 3, HtlcDirection.Outgoing, HtlcState.Expired)
            ],
            remoteOffered: [SqliteDbTestContext.CreateHtlc(channelId, 0, HtlcDirection.Incoming, HtlcState.Offered)],
            remoteFulfilled:
            [
                SqliteDbTestContext.CreateHtlc(channelId, 1, HtlcDirection.Incoming, HtlcState.Fulfilled)
            ],
            remoteOld: [SqliteDbTestContext.CreateHtlc(channelId, 2, HtlcDirection.Incoming, HtlcState.Failed)]);

        // Act
        var reloaded = await SaveAndReloadAsync(db, channel);

        // Assert
        Assert.Equal([0UL], reloaded.LocalOfferedHtlcs!.Select(h => h.Id));
        Assert.Equal([1UL], reloaded.LocalFulfilledHtlcs!.Select(h => h.Id));
        Assert.Equal([2UL, 3UL], reloaded.LocalOldHtlcs!.Select(h => h.Id).Order());
        Assert.Equal([0UL], reloaded.RemoteOfferedHtlcs!.Select(h => h.Id));
        Assert.Equal([1UL], reloaded.RemoteFulfilledHtlcs!.Select(h => h.Id));
        Assert.Equal([2UL], reloaded.RemoteOldHtlcs!.Select(h => h.Id));
        Assert.All(reloaded.LocalOfferedHtlcs!, h => Assert.Equal(HtlcDirection.Outgoing, h.Direction));
        Assert.All(reloaded.RemoteOfferedHtlcs!, h => Assert.Equal(HtlcDirection.Incoming, h.Direction));
    }

    [Fact]
    public async Task Given_Channel_When_Reloaded_Then_FundingOutputKeepsTheRemoteFundingPubKey()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(true);

        // Act
        var reloaded = await SaveAndReloadAsync(db, channel);

        // Assert
        Assert.NotNull(reloaded.FundingOutput);
        Assert.Equal(SqliteDbTestContext.LocalFundingPubKey, reloaded.FundingOutput.LocalFundingPubKey);
        Assert.Equal(SqliteDbTestContext.RemoteFundingPubKey, reloaded.FundingOutput.RemoteFundingPubKey);
        Assert.Equal(channel.FundingOutput!.TransactionId, reloaded.FundingOutput.TransactionId);
        Assert.Equal(channel.FundingOutput.Index, reloaded.FundingOutput.Index);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_ChannelInEitherRole_When_Reloaded_Then_ObscuringFactorUsesOpenerBasepointFirst(
        bool isInitiator)
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(isInitiator);
        var expected = isInitiator
                           ? new CommitmentNumber(SqliteDbTestContext.LocalPaymentBasepoint,
                                                  SqliteDbTestContext.RemotePaymentBasepoint, db.Sha256)
                           : new CommitmentNumber(SqliteDbTestContext.RemotePaymentBasepoint,
                                                  SqliteDbTestContext.LocalPaymentBasepoint, db.Sha256);

        // Act
        var reloaded = await SaveAndReloadAsync(db, channel);

        // Assert
        Assert.NotNull(reloaded.CommitmentNumber);
        Assert.Equal(expected.ObscuringFactor, reloaded.CommitmentNumber.ObscuringFactor);
        Assert.Equal(channel.CommitmentNumber!.ObscuringFactor, reloaded.CommitmentNumber.ObscuringFactor);
    }

    [Fact]
    public async Task Given_ChannelWithChangeAddress_When_Reloaded_Then_ChangeAddressIsRestored()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var changeAddress = new WalletAddressModel(AddressType.P2Wpkh, 3, true,
                                                   "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080");
        await using (var addressContext = db.CreateDbContext())
        {
            new WalletAddressesDbRepository(addressContext).AddRange([
                new WalletAddressModel(AddressType.P2Wpkh, 3, false, "bcrt1qnotchange"),
                changeAddress
            ]);
            await addressContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var channel = SqliteDbTestContext.CreateChannel(true, changeAddress: changeAddress);

        // Act
        var reloaded = await SaveAndReloadAsync(db, channel);

        // Assert
        Assert.NotNull(reloaded.ChangeAddress);
        Assert.Equal(changeAddress.AddressType, reloaded.ChangeAddress.AddressType);
        Assert.Equal(changeAddress.Index, reloaded.ChangeAddress.Index);
        Assert.True(reloaded.ChangeAddress.IsChange);
        Assert.Equal(changeAddress.Address, reloaded.ChangeAddress.Address);
    }

    [Fact]
    public async Task Given_ChannelWithoutChangeAddress_When_ChangeAddressAddedByUpdate_Then_ChangeAddressIsRestored()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var changeAddress = new WalletAddressModel(AddressType.P2Wpkh, 0, true, "bcrt1qchange");
        await using (var addressContext = db.CreateDbContext())
        {
            new WalletAddressesDbRepository(addressContext).AddRange([changeAddress]);
            await addressContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var channel = SqliteDbTestContext.CreateChannel(true);
        var reloaded = await SaveAndReloadAsync(db, channel);
        Assert.Null(reloaded.ChangeAddress);
        reloaded.ChangeAddress = changeAddress;

        // Act
        await using (var updateContext = db.CreateDbContext())
        {
            var repository = new ChannelDbRepository(updateContext, db.MessageSerializer, db.Sha256);
            await repository.UpdateAsync(reloaded);
            await updateContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await using var readContext = db.CreateDbContext();
        var updated = await new ChannelDbRepository(readContext, db.MessageSerializer, db.Sha256)
                         .GetByIdAsync(channel.ChannelId);
        Assert.NotNull(updated?.ChangeAddress);
        Assert.Equal(changeAddress.Address, updated.ChangeAddress.Address);
        Assert.True(updated.ChangeAddress.IsChange);
    }

    [Fact]
    public async Task Given_HtlcWithSignature_When_ReloadedThroughChannel_Then_SignatureIsKept()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = SqliteDbTestContext.CreateChannel(true).ChannelId;
        var signature = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray();
        var channel = SqliteDbTestContext.CreateChannel(
            true,
            remoteOffered:
            [
                SqliteDbTestContext.CreateHtlc(channelId, 0, HtlcDirection.Incoming, HtlcState.Offered, signature)
            ]);

        // Act
        var reloaded = await SaveAndReloadAsync(db, channel);

        // Assert
        var htlc = Assert.Single(reloaded.RemoteOfferedHtlcs!);
        Assert.NotNull(htlc.Signature);
        Assert.Equal(signature, htlc.Signature.Value);
    }

    private static async Task UpdateInFreshContextAsync(SqliteDbTestContext db, ChannelModel channel)
    {
        await using var updateContext = db.CreateDbContext();
        var repository = new ChannelDbRepository(updateContext, db.MessageSerializer, db.Sha256);
        await repository.UpdateAsync(channel);
        await updateContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<ChannelModel> ReloadAsync(SqliteDbTestContext db, ChannelModel channel)
    {
        await using var readContext = db.CreateDbContext();
        return await new ChannelDbRepository(readContext, db.MessageSerializer, db.Sha256)
                    .GetByIdAsync(channel.ChannelId)
            ?? throw new InvalidOperationException("Channel was not reloaded");
    }

    [Fact]
    public async Task Given_PersistedChannel_When_UpdatedWithANewHtlcOnAFreshContext_Then_HtlcIsInserted()
    {
        // Arrange (NL-192: a new HTLC child used to be marked Modified, failing with a concurrency exception)
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var reloaded = await SaveAndReloadAsync(db, SqliteDbTestContext.CreateChannel(true));
        reloaded.LocalOfferedHtlcs!.Add(
            SqliteDbTestContext.CreateHtlc(reloaded.ChannelId, 0, HtlcDirection.Outgoing, HtlcState.Offered));

        // Act
        await UpdateInFreshContextAsync(db, reloaded);

        // Assert
        var updated = await ReloadAsync(db, reloaded);
        Assert.Equal([0UL], updated.LocalOfferedHtlcs!.Select(h => h.Id));
    }

    [Fact]
    public async Task Given_PersistedHtlcs_When_UpdatedWithChangedAddedAndRemovedHtlcs_Then_HtlcRowsMatchTheModel()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = SqliteDbTestContext.CreateChannel(true).ChannelId;
        var reloaded = await SaveAndReloadAsync(db, SqliteDbTestContext.CreateChannel(
                                                    true,
                                                    localOffered:
                                                    [
                                                        SqliteDbTestContext.CreateHtlc(
                                                            channelId, 0, HtlcDirection.Outgoing, HtlcState.Offered),
                                                        SqliteDbTestContext.CreateHtlc(
                                                            channelId, 1, HtlcDirection.Outgoing, HtlcState.Offered)
                                                    ]));

        // HTLC 0 is fulfilled, HTLC 1 disappears and incoming HTLC 0 is new
        reloaded.LocalOfferedHtlcs!.Clear();
        reloaded.LocalFulfilledHtlcs!.Add(
            SqliteDbTestContext.CreateHtlc(channelId, 0, HtlcDirection.Outgoing, HtlcState.Fulfilled));
        reloaded.RemoteOfferedHtlcs!.Add(
            SqliteDbTestContext.CreateHtlc(channelId, 0, HtlcDirection.Incoming, HtlcState.Offered));

        // Act
        await UpdateInFreshContextAsync(db, reloaded);

        // Assert
        var updated = await ReloadAsync(db, reloaded);
        Assert.Empty(updated.LocalOfferedHtlcs!);
        Assert.Equal([0UL], updated.LocalFulfilledHtlcs!.Select(h => h.Id));
        Assert.Equal([0UL], updated.RemoteOfferedHtlcs!.Select(h => h.Id));
        await using var countContext = db.CreateDbContext();
        Assert.Equal(2, countContext.Htlcs.Count());
    }

    [Fact]
    public async Task Given_ChannelAddedInTheSameContext_When_UpdatedWithHtlcChanges_Then_HtlcRowsMatchTheModel()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = SqliteDbTestContext.CreateChannel(true).ChannelId;
        var channel = SqliteDbTestContext.CreateChannel(
            true,
            localOffered: [SqliteDbTestContext.CreateHtlc(channelId, 0, HtlcDirection.Outgoing, HtlcState.Offered)]);

        // Act
        await using (var context = db.CreateDbContext())
        {
            var repository = new ChannelDbRepository(context, db.MessageSerializer, db.Sha256);
            await repository.AddAsync(channel);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            channel.LocalOfferedHtlcs!.Clear();
            channel.RemoteOfferedHtlcs!.Add(
                SqliteDbTestContext.CreateHtlc(channelId, 5, HtlcDirection.Incoming, HtlcState.Offered));
            await repository.UpdateAsync(channel);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        var updated = await ReloadAsync(db, channel);
        Assert.Empty(updated.LocalOfferedHtlcs!);
        Assert.Equal([5UL], updated.RemoteOfferedHtlcs!.Select(h => h.Id));
    }

    [Fact]
    public async Task Given_ChannelAddedButNotSaved_When_UpdatedInTheSameContext_Then_OnlyTheUpdatedHtlcsAreSaved()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = SqliteDbTestContext.CreateChannel(true).ChannelId;
        var channel = SqliteDbTestContext.CreateChannel(
            true,
            localOffered: [SqliteDbTestContext.CreateHtlc(channelId, 0, HtlcDirection.Outgoing, HtlcState.Offered)]);

        // Act
        await using (var context = db.CreateDbContext())
        {
            var repository = new ChannelDbRepository(context, db.MessageSerializer, db.Sha256);
            await repository.AddAsync(channel);

            channel.LocalOfferedHtlcs!.Clear();
            channel.LocalOfferedHtlcs.Add(
                SqliteDbTestContext.CreateHtlc(channelId, 1, HtlcDirection.Outgoing, HtlcState.Offered));
            channel.LocalOfferedHtlcs.Add(
                SqliteDbTestContext.CreateHtlc(channelId, 2, HtlcDirection.Outgoing, HtlcState.Offered));
            await repository.UpdateAsync(channel);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        var updated = await ReloadAsync(db, channel);
        Assert.Equal([1UL, 2UL], updated.LocalOfferedHtlcs!.Select(h => h.Id).Order());
    }
}