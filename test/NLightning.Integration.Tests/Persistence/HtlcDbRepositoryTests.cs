namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Infrastructure.Repositories.Database.Channel;

public class HtlcDbRepositoryTests
{
    private static async Task<ChannelId> SeedAsync(SqliteDbTestContext db, params Htlc[] htlcs)
    {
        var channel = SqliteDbTestContext.CreateChannel(true);

        await using var context = db.CreateDbContext();
        await new ChannelDbRepository(context, db.MessageSerializer, db.Sha256).AddAsync(channel);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var htlcRepository = new HtlcDbRepository(context, db.MessageSerializer);
        foreach (var htlc in htlcs)
            await htlcRepository.AddAsync(channel.ChannelId, htlc);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return channel.ChannelId;
    }

    [Fact]
    public async Task Given_HtlcsInSeveralStates_When_GetByChannelIdAndState_Then_OnlyMatchingStateIsReturned()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = SqliteDbTestContext.CreateChannel(true).ChannelId;
        await SeedAsync(db,
                        SqliteDbTestContext.CreateHtlc(channelId, 0, HtlcDirection.Outgoing, HtlcState.Offered),
                        SqliteDbTestContext.CreateHtlc(channelId, 1, HtlcDirection.Outgoing, HtlcState.Fulfilled),
                        SqliteDbTestContext.CreateHtlc(channelId, 2, HtlcDirection.Incoming, HtlcState.Offered));

        await using var context = db.CreateDbContext();
        var repository = new HtlcDbRepository(context, db.MessageSerializer);

        // Act
        var offered = (await repository.GetByChannelIdAndStateAsync(channelId, HtlcState.Offered)).ToList();
        var fulfilled = (await repository.GetByChannelIdAndStateAsync(channelId, HtlcState.Fulfilled)).ToList();

        // Assert
        Assert.Equal([0UL, 2UL], offered.Select(h => h.Id).Order());
        Assert.Equal([1UL], fulfilled.Select(h => h.Id));
    }

    [Fact]
    public async Task Given_HtlcsInBothDirections_When_GetByChannelIdAndDirection_Then_OnlyMatchingDirectionIsReturned()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = SqliteDbTestContext.CreateChannel(true).ChannelId;
        await SeedAsync(db,
                        SqliteDbTestContext.CreateHtlc(channelId, 0, HtlcDirection.Outgoing, HtlcState.Offered),
                        SqliteDbTestContext.CreateHtlc(channelId, 1, HtlcDirection.Outgoing, HtlcState.Fulfilled),
                        SqliteDbTestContext.CreateHtlc(channelId, 0, HtlcDirection.Incoming, HtlcState.Offered));

        await using var context = db.CreateDbContext();
        var repository = new HtlcDbRepository(context, db.MessageSerializer);

        // Act
        var outgoing = (await repository.GetByChannelIdAndDirectionAsync(channelId, HtlcDirection.Outgoing))
           .ToList();
        var incoming = (await repository.GetByChannelIdAndDirectionAsync(channelId, HtlcDirection.Incoming))
           .ToList();

        // Assert
        Assert.Equal([0UL, 1UL], outgoing.Select(h => h.Id).Order());
        Assert.All(outgoing, h => Assert.Equal(HtlcDirection.Outgoing, h.Direction));
        var single = Assert.Single(incoming);
        Assert.Equal(HtlcDirection.Incoming, single.Direction);
    }

    [Fact]
    public async Task Given_HtlcWithSignature_When_AddedAndReloaded_Then_SignatureIsKept()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channelId = SqliteDbTestContext.CreateChannel(true).ChannelId;
        var signature = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray();
        await SeedAsync(db,
                        SqliteDbTestContext.CreateHtlc(channelId, 4, HtlcDirection.Incoming, HtlcState.Offered,
                                                       signature));

        await using var context = db.CreateDbContext();
        var repository = new HtlcDbRepository(context, db.MessageSerializer);

        // Act
        var htlc = await repository.GetByIdAsync(channelId, 4, HtlcDirection.Incoming);

        // Assert
        Assert.NotNull(htlc);
        Assert.NotNull(htlc.Value.Signature);
        Assert.Equal(signature, htlc.Value.Signature.Value);
    }
}