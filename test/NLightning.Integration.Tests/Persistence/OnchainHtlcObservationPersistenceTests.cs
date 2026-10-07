namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Models;
using Infrastructure.Repositories.Database.Onchain;

public class OnchainHtlcObservationPersistenceTests
{
    [Fact]
    public async Task Given_AnObservation_When_CommittedAndReopened_Then_OutcomeAndDirectionAreDurablyDistinct()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var at = DateTimeOffset.UtcNow;
        await using (var context = database.CreateContext())
        {
            var repository = new OnchainHtlcObservationDbRepository(context);
            Assert.False(await repository.ContainsAsync(ChannelId.Zero, HtlcDirection.Incoming, 7, true));
            repository.Add(new OnchainHtlcObservationModel(ChannelId.Zero, HtlcDirection.Incoming, 7, true, at));
            Assert.True(await repository.ContainsAsync(ChannelId.Zero, HtlcDirection.Incoming, 7, true));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act & Assert: no channel row is required; checkpoints outlive operational close rows.
        await using var reopened = database.CreateContext();
        var persisted = new OnchainHtlcObservationDbRepository(reopened);
        Assert.True(await persisted.ContainsAsync(ChannelId.Zero, HtlcDirection.Incoming, 7, true));
        Assert.False(await persisted.ContainsAsync(ChannelId.Zero, HtlcDirection.Incoming, 7, false));
        Assert.False(await persisted.ContainsAsync(ChannelId.Zero, HtlcDirection.Outgoing, 7, true));
        Assert.Equal(at.UtcTicks, reopened.OnchainHtlcObservations.Single().ObservedAt.UtcTicks);
    }

    [Fact]
    public async Task Given_AnObservation_When_SaveIsAbandoned_Then_ANewUnitOfWorkCanStillObserveIt()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await using (var context = database.CreateContext())
        {
            new OnchainHtlcObservationDbRepository(context).Add(new OnchainHtlcObservationModel(
                ChannelId.Zero, HtlcDirection.Outgoing, ulong.MaxValue, false, DateTimeOffset.UtcNow));
        }

        // Act & Assert
        await using var reopened = database.CreateContext();
        var repository = new OnchainHtlcObservationDbRepository(reopened);
        Assert.False(await repository.ContainsAsync(ChannelId.Zero, HtlcDirection.Outgoing, ulong.MaxValue, false));
        repository.Add(new OnchainHtlcObservationModel(ChannelId.Zero, HtlcDirection.Outgoing, ulong.MaxValue, false,
                                                    DateTimeOffset.UtcNow));
        await reopened.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(await repository.ContainsAsync(ChannelId.Zero, HtlcDirection.Outgoing, ulong.MaxValue, false));
    }
}