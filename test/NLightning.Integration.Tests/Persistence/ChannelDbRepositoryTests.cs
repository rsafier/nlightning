namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.Enums;
using Domain.Channels.Models;
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
}