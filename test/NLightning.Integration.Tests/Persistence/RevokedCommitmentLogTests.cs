using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Models;
using Domain.Protocol.Models;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Memory;

/// <summary>
/// The revocation log (BOLT 5 plan O1-T1, D2) on SQLite with the real migrations: the peer commitment a
/// <c>revoke_and_ack</c> revokes is written by <see cref="ChannelStateDbRepository.ApplyAsync"/> in the same save as the
/// revocation and its shachain entry, only when it had an HTLC, and reloads after a restart.
/// </summary>
public class RevokedCommitmentLogTests
{
    [Fact]
    public async Task Given_RaaWithHtlcs_When_Applied_Then_LogRowInSameSave()
    {
        // Arrange: our commitment #1 carries an HTLC; the RAA for #2 revokes it
        var crasher = new CrashAfterCommandInterceptor();
        await using var harness = await LogHarness.CreateAsync(crasher);
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);
        await harness.PersistAsync(harness.Driver.TryDeliverRevokeToUs()!, ShachainUpTo(0));
        await harness.PersistAsync(harness.Driver.TryUsAdd(6_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);
        var revoked = harness.Driver.Us.RemoteCommit;
        Assert.Equal(1UL, revoked.Number);
        Assert.Single(revoked.Spec.Htlcs);
        var raa = harness.Driver.TryDeliverRevokeToUs()!;
        Assert.Same(revoked, raa.Transition.RevokedRemoteCommit);
        var shachainBefore = await harness.CountShachainAsync();
        var crashes = 0;

        // Act & Assert: crash after each statement of the RAA save; the log row and the shachain entry commit together
        for (var k = 1; ; k++)
        {
            crasher.Arm(k);
            try
            {
                await harness.PersistAsync(raa, ShachainUpTo(1));
            }
            catch (DbUpdateException e) when (e.InnerException is SimulatedCrashException)
            {
                crashes++;
                crasher.Disarm();
                Assert.Empty(await harness.LoadLogAsync());
                Assert.Equal(shachainBefore, await harness.CountShachainAsync());
                continue;
            }

            crasher.Disarm();
            break;
        }

        Assert.True(crashes >= 2, $"Only {crashes} crash point(s): the RAA save did not write several statements");
        var entry = Assert.Single(await harness.LoadLogAsync());
        AssertEntry(harness.ChannelId, revoked, entry);
        Assert.Equal(shachainBefore + 1, await harness.CountShachainAsync());
    }

    [Fact]
    public async Task Given_RaaOfACommitmentWithoutHtlcs_When_Applied_Then_NoLogRow()
    {
        // Arrange: the peer's commitment #0 has no HTLC
        await using var harness = await LogHarness.CreateAsync();
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);
        var raa = harness.Driver.TryDeliverRevokeToUs()!;

        // Act
        await harness.PersistAsync(raa, ShachainUpTo(0));

        // Assert (D2: its to_local penalty needs only the keys and the secret)
        Assert.NotNull(raa.Transition.RevokedRemoteCommit);
        Assert.Empty(raa.Transition.RevokedRemoteCommit.Spec.Htlcs);
        Assert.Empty(await harness.LoadLogAsync());
        Assert.Equal(1, await harness.CountShachainAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task Given_60SimulatorTransitions_When_EachIsPersisted_Then_TheLogHoldsExactlyTheRevokedCommitmentsWithHtlcs(
        int seed)
    {
        // Arrange
        await using var harness = await LogHarness.CreateAsync(seed: seed);
        var expected = new List<RemoteCommit>();
        var revocations = 0;

        // Act & Assert: after every transition the saved log equals the revoked peer commitments that had HTLCs
        for (var i = 0; i < 60; i++)
        {
            var result = harness.Driver.NextTransition();
            if (result.Transition.RevokedRemoteCommit is { } revoked)
            {
                revocations++;
                if (revoked.Spec.Htlcs.Count > 0)
                    expected.Add(revoked);
            }

            await harness.PersistAsync(result);
            await AssertLogAsync(harness, expected);
        }

        Assert.True(revocations > 0, "The dance never revoked a commitment");
        Assert.NotEmpty(expected);

        // A restart (new unit of work) reads the same log through IUnitOfWork
        using var unitOfWork = harness.CreateUnitOfWork();
        var reloaded = await unitOfWork.RevokedCommitmentDbRepository.GetByChannelIdAsync(harness.ChannelId);
        Assert.Equal(expected.Count, reloaded.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            AssertEntry(harness.ChannelId, expected[i], reloaded[i]);
            AssertEntry(harness.ChannelId, expected[i],
                        await unitOfWork.RevokedCommitmentDbRepository.GetAsync(harness.ChannelId, expected[i].Number));
        }

        Assert.Equal(0UL, await unitOfWork.RevokedCommitmentDbRepository.GetLogStartAsync(harness.ChannelId));
    }

    [Fact]
    public async Task Given_ReplayedRaaTransition_When_AppliedAgain_Then_TheRowIsRewrittenNotDuplicated()
    {
        // Arrange
        await using var harness = await LogHarness.CreateAsync();
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);
        await harness.PersistAsync(harness.Driver.TryDeliverRevokeToUs()!);
        await harness.PersistAsync(harness.Driver.TryUsAdd(6_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);
        var raa = harness.Driver.TryDeliverRevokeToUs()!;
        await harness.PersistAsync(raa);

        // Act
        await harness.PersistAsync(raa);

        // Assert
        var entry = Assert.Single(await harness.LoadLogAsync());
        AssertEntry(harness.ChannelId, raa.Transition.RevokedRemoteCommit!, entry);
    }

    [Fact]
    public async Task Given_LogEntries_When_TheChannelIsClosedAndTheLogDeleted_Then_NothingIsLeft()
    {
        // Arrange
        await using var harness = await LogHarness.CreateAsync();
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);
        await harness.PersistAsync(harness.Driver.TryDeliverRevokeToUs()!);
        await harness.PersistAsync(harness.Driver.TryUsAdd(6_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);
        await harness.PersistAsync(harness.Driver.TryDeliverRevokeToUs()!);
        Assert.Single(await harness.LoadLogAsync());

        // Act
        using (var unitOfWork = harness.CreateUnitOfWork())
        {
            await unitOfWork.RevokedCommitmentDbRepository.DeleteByChannelIdAsync(harness.ChannelId);
            await unitOfWork.SaveChangesAsync();
        }

        // Assert
        Assert.Empty(await harness.LoadLogAsync());
    }

    [Fact]
    public async Task Given_RevocationLogFromNumberSetByTheMigration_When_TheChannelIsUpdatedFromItsModel_Then_ItIsKept()
    {
        // Arrange: a channel migrated with revoked commitments (the column is written only by AddOnchainResolution)
        await using var harness = await LogHarness.CreateAsync();
        await using (var context = harness.Db.CreateDbContext())
        {
            var row = await context.Channels.SingleAsync(TestContext.Current.CancellationToken);
            row.RevocationLogFromNumber = 17;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act: a stale model is saved through ChannelDbRepository.UpdateAsync
        await using (var context = harness.Db.CreateDbContext())
        {
            var repository = new ChannelDbRepository(context, harness.Db.Sha256);
            var channel = await repository.GetByIdAsync(harness.ChannelId);
            await repository.UpdateAsync(channel!);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        using var unitOfWork = harness.CreateUnitOfWork();
        Assert.Equal(17UL, await unitOfWork.RevokedCommitmentDbRepository.GetLogStartAsync(harness.ChannelId));
    }

    private static async Task AssertLogAsync(LogHarness harness, IReadOnlyList<RemoteCommit> expected)
    {
        var log = await harness.LoadLogAsync();
        Assert.Equal(expected.Select(c => c.Number), log.Select(e => e.Number));
        for (var i = 0; i < expected.Count; i++)
            AssertEntry(harness.ChannelId, expected[i], log[i]);
    }

    private static void AssertEntry(ChannelId channelId, RemoteCommit expected, RevokedCommitmentModel? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(channelId, actual.ChannelId);
        Assert.Equal(expected.Number, actual.Number);
        Assert.Equal(expected.Spec, actual.Spec);
    }

    /// <summary>The shachain export after the peer revealed its secrets 0..<paramref name="number"/> (one bucket per
    /// secret here, which is enough to see it commit with the RAA).</summary>
    private static ChannelStateExtras ShachainUpTo(ulong number) =>
        new()
        {
            RemoteShachain = Enumerable.Range(0, (int)number + 1)
                                       .Select(i => new ShachainEntry(
                                                   i, 281474976710655UL - (ulong)i,
                                                   CommitmentDanceDriver.SecretFor(CommitmentDanceDriver.PeerTag,
                                                                                   (ulong)i)))
                                       .ToList()
        };

    /// <summary>A channel saved in SQLite plus the driver whose "us" side is persisted.</summary>
    private sealed class LogHarness : IAsyncDisposable
    {
        private readonly IInterceptor? _interceptor;

        public SqliteDbTestContext Db { get; }
        public CommitmentDanceDriver Driver { get; }
        public ChannelId ChannelId { get; }

        private LogHarness(SqliteDbTestContext db, ChannelId channelId, CommitmentDanceDriver driver,
                           IInterceptor? interceptor)
        {
            Db = db;
            ChannelId = channelId;
            Driver = driver;
            _interceptor = interceptor;
        }

        public static async Task<LogHarness> CreateAsync(IInterceptor? interceptor = null, int seed = 1)
        {
            var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
            var channel = SqliteDbTestContext.CreateChannel(true);
            var driver = new CommitmentDanceDriver(channel.ChannelId, CommitmentParams.FromChannel(channel),
                                                   channel.LocalBalance.MilliSatoshi,
                                                   channel.RemoteBalance.MilliSatoshi, seed: seed);
            await using (var context = db.CreateDbContext())
            {
                await new ChannelDbRepository(context, db.Sha256).AddAsync(channel);
                await new ChannelStateDbRepository(context).InitializeAsync(driver.Us);
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            return new LogHarness(db, channel.ChannelId, driver, interceptor);
        }

        /// <summary>One transition, one save, on a fresh context.</summary>
        public async Task PersistAsync(CommitmentsResult result, ChannelStateExtras? extras = null)
        {
            await using var context = _interceptor is null ? Db.CreateDbContext() : Db.CreateDbContext(_interceptor);
            await new ChannelStateDbRepository(context).ApplyAsync(result.Next, result.Transition, extras);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task<IReadOnlyList<RevokedCommitmentModel>> LoadLogAsync()
        {
            await using var context = Db.CreateDbContext();
            return await new RevokedCommitmentDbRepository(context).GetByChannelIdAsync(ChannelId);
        }

        public async Task<int> CountShachainAsync()
        {
            await using var context = Db.CreateDbContext();
            return await context.RemoteShachains.CountAsync(TestContext.Current.CancellationToken);
        }

        public UnitOfWork CreateUnitOfWork() =>
            new(Db.CreateDbContext(), NullLogger<UnitOfWork>.Instance, Db.Sha256, new UtxoMemoryRepository());

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}