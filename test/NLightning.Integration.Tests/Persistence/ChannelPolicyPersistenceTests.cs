using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.RoutingPolicies;
using Domain.Channels.ValueObjects;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Memory;

/// <summary>
/// The per-channel routing policy overrides (table <c>ChannelPolicies</c>, migration <c>AddSpliceFundings</c>, wave sp1
/// lanes SP1-C/SP1-G) on SQLite: every field at its BOLT 7 width and as null, replace, delete, the staged view of a unit
/// of work and the stamp of <see cref="ChannelPolicyOverride.UpdatedAt"/>.
/// </summary>
public class ChannelPolicyPersistenceTests
{
    private static readonly ChannelId s_channelA = new(Enumerable.Repeat((byte)0xA1, 32).ToArray());
    private static readonly ChannelId s_channelB = new(Enumerable.Repeat((byte)0xB2, 32).ToArray());

    [Fact]
    public async Task Given_NodeDefaultPolicy_When_SavedWithoutAChannel_Then_AFreshScopeRestoresIt()
    {
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var defaults = new ChannelPolicyOverride(ChannelId.Zero, 2_500, 300, 72,
                                                 UpdatedAt: DateTimeOffset.UtcNow);
        await SaveAsync(db, uow => uow.ChannelPolicyDbRepository.UpsertAsync(defaults));
        using var restarted = CreateUnitOfWork(db);
        var restored = await restarted.ChannelPolicyDbRepository.GetAsync(ChannelId.Zero);
        Assert.Equal(defaults, restored);
        Assert.Equal(defaults, Assert.Single(await restarted.ChannelPolicyDbRepository.GetAllAsync()));
    }

    [Fact]
    public async Task Given_OverridesAtTheirMaximumWidths_When_SavedAndReloaded_Then_EveryValueIsEqual()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var updatedAt = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(2)).AddTicks(1_234);
        var full = new ChannelPolicyOverride(s_channelA, uint.MaxValue, uint.MaxValue, ushort.MaxValue, ulong.MaxValue,
                                             ulong.MaxValue - 1, updatedAt);
        var partial = new ChannelPolicyOverride(s_channelB, CltvExpiryDelta: 144, UpdatedAt: updatedAt);

        // Act
        await SaveAsync(db, async uow =>
        {
            await uow.ChannelPolicyDbRepository.UpsertAsync(full);
            await uow.ChannelPolicyDbRepository.UpsertAsync(partial);
        });

        // Assert
        using var reader = CreateUnitOfWork(db);
        Assert.Equal(full with { UpdatedAt = updatedAt.ToUniversalTime() },
                     await reader.ChannelPolicyDbRepository.GetAsync(s_channelA));
        Assert.Equal(partial with { UpdatedAt = updatedAt.ToUniversalTime() },
                     await reader.ChannelPolicyDbRepository.GetAsync(s_channelB));
        Assert.Equal(2, (await reader.ChannelPolicyDbRepository.GetAllAsync()).Count);
    }

    [Fact]
    public async Task Given_AStoredOverride_When_ReplacedWithNulls_Then_TheNullsAreStoredAndTheTimeStamped()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 27, 18, 30, 0, TimeSpan.Zero);
        await SaveAsync(db, uow => uow.ChannelPolicyDbRepository.UpsertAsync(
                                new ChannelPolicyOverride(s_channelA, 1_000, 100, 40, 1, 500_000_000, now)));

        // Act: a replacement without a time is stamped by the unit of work's clock
        var later = now.AddMinutes(5);
        await SaveAsync(db, uow => uow.ChannelPolicyDbRepository.UpsertAsync(
                                new ChannelPolicyOverride(s_channelA, FeeBaseMsat: 2_000)), new FixedTime(later));

        // Assert
        using var reader = CreateUnitOfWork(db);
        Assert.Equal(new ChannelPolicyOverride(s_channelA, 2_000, null, null, null, null, later),
                     await reader.ChannelPolicyDbRepository.GetAsync(s_channelA));
    }

    [Fact]
    public async Task Given_AnOverride_When_DeletedAndUpsertedInOneUnitOfWork_Then_TheStagedViewIsRight()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var stamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await SaveAsync(db, uow => uow.ChannelPolicyDbRepository.UpsertAsync(
                                new ChannelPolicyOverride(s_channelA, 1, 2, 3, 4, 5, stamp)));

        // Act / Assert: a delete is seen before its save, an upsert revives the row, a second delete removes it
        using (var uow = CreateUnitOfWork(db))
        {
            await uow.ChannelPolicyDbRepository.DeleteAsync(s_channelA);
            Assert.Null(await uow.ChannelPolicyDbRepository.GetAsync(s_channelA));
            await uow.ChannelPolicyDbRepository.UpsertAsync(new ChannelPolicyOverride(s_channelA, 9, UpdatedAt: stamp));
            Assert.Equal(9u, (await uow.ChannelPolicyDbRepository.GetAsync(s_channelA))!.FeeBaseMsat);
            await uow.SaveChangesAsync();
        }

        using (var uow = CreateUnitOfWork(db))
        {
            Assert.Equal(9u, (await uow.ChannelPolicyDbRepository.GetAsync(s_channelA))!.FeeBaseMsat);
            await uow.ChannelPolicyDbRepository.DeleteAsync(s_channelA);
            await uow.ChannelPolicyDbRepository.DeleteAsync(s_channelB);
            await uow.SaveChangesAsync();
        }

        using var reader = CreateUnitOfWork(db);
        Assert.Null(await reader.ChannelPolicyDbRepository.GetAsync(s_channelA));
        Assert.Empty(await reader.ChannelPolicyDbRepository.GetAllAsync());
    }

    private static UnitOfWork CreateUnitOfWork(SqliteDbTestContext db, TimeProvider? timeProvider = null) =>
        new(db.CreateDbContext(), NullLogger<UnitOfWork>.Instance, db.Sha256, new UtxoMemoryRepository(),
            timeProvider);

    private static async Task SaveAsync(SqliteDbTestContext db, Func<UnitOfWork, Task> stage,
                                        TimeProvider? timeProvider = null)
    {
        using var uow = CreateUnitOfWork(db, timeProvider);
        await stage(uow);
        await uow.SaveChangesAsync();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}