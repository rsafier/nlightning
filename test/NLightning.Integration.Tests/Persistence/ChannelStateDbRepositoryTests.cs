using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Node.Models;
using Domain.Protocol.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Database.Node;
using Infrastructure.Repositories.Memory;

/// <summary>
/// Persists the commitment state machine through <see cref="ChannelStateDbRepository"/> on SQLite with the real
/// migrations (BOLT2 plan N5-T2, N5-T3).
/// </summary>
public class ChannelStateDbRepositoryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task Given_50SimulatorTransitions_When_EachIsPersistedAndReloaded_Then_ReloadedStateEqualsTheEngine(
        int seed)
    {
        // Arrange
        await using var harness = await StateHarness.CreateAsync(seed);
        var kinds = new HashSet<string>();

        // Act & Assert: one save per transition, reloaded from a fresh context every time
        for (var i = 0; i < 50; i++)
        {
            var result = harness.Driver.NextTransition();
            kinds.UnionWith(result.Outbound.Select(o => o.GetType().Name));
            if (result.Transition.LocalCommitChanged)
                kinds.Add("ReceivedCommit");
            if (result.Transition.RemoteCommitChanged && result.Next.RemoteNextCommit is null)
                kinds.Add("ReceivedRevoke");

            await harness.PersistAsync(result);
            await harness.AssertReloadEqualsAsync();
        }

        // Reloading the whole channel attaches the same snapshot
        var channel = await harness.ReloadChannelAsync();
        Assert.NotNull(channel.Commitments);
        CommitmentsAssert.Equal(harness.Driver.Us, channel.Commitments);
        Assert.Equal(harness.Driver.Us.LocalBalanceMsat, channel.LocalBalance.MilliSatoshi);
        Assert.Equal(harness.Driver.Us.LocalCommit.Number, channel.LocalCommitmentNumber);
        Assert.Equal(harness.Driver.Us.RemoteCommit.Number, channel.RemoteCommitmentNumber);
        Assert.Equal(harness.Driver.Us.LocalNextHtlcId, channel.LocalNextHtlcId);

        // The dance covered adds, signatures both ways and revocations
        Assert.Contains(nameof(OutboundAddHtlc), kinds);
        Assert.Contains(nameof(OutboundCommitmentSigned), kinds);
        Assert.Contains("ReceivedCommit", kinds);
        Assert.Contains("ReceivedRevoke", kinds);
    }

    [Fact]
    public async Task Given_UnackedCommitmentSigned_When_Reloaded_Then_MidDanceStateIsIdentical()
    {
        // Arrange
        await using var harness = await StateHarness.CreateAsync();
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        await harness.PersistAsync(harness.Driver.TryPeerAdd()!);
        var signed = harness.Driver.TryUsCommit()!;

        // Act
        await harness.PersistAsync(signed);

        // Assert: the unacked remote commitment, its signatures and the sent diff all come back
        var state = await harness.AssertReloadEqualsAsync();
        Assert.NotNull(state.Commitments.RemoteNextCommit);
        var number = state.Commitments.RemoteNextCommit.Commit.Number;
        Assert.Equal(CommitmentDanceDriver.DiffFor(number), state.SentCommitDiff?.ToArray());
        Assert.Equal(LastSentCommitmentMessage.CommitmentSigned, state.LastSent);
        var channel = await harness.ReloadChannelAsync();
        Assert.Equal(CommitmentDanceDriver.DiffFor(number), channel.SentCommitDiff?.ToArray());
        Assert.Equal(LastSentCommitmentMessage.CommitmentSigned, channel.LastSentCommitmentMessage);
    }

    [Fact]
    public async Task Given_UnackedCommitmentSigned_When_PeerRevokes_Then_SentDiffIsCleared()
    {
        // Arrange (decision D4: the diff is kept only until the revoke_and_ack)
        await using var harness = await StateHarness.CreateAsync();
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);

        // Act
        await harness.PersistAsync(harness.Driver.TryDeliverRevokeToUs()!);

        // Assert
        var state = await harness.AssertReloadEqualsAsync();
        Assert.Null(state.Commitments.RemoteNextCommit);
        Assert.Null(state.SentCommitDiff);
        await using var context = harness.Db.CreateDbContext();
        Assert.Equal(2, await context.Commitments.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_CrashAfterEachStatementOfASave_When_Reloaded_Then_NoPartialRowsAndThePreviousStateLoads()
    {
        // Arrange: a transition that writes several rows (three HTLCs change state, the unacked commitment, the
        // channel scalars)
        var crasher = new CrashAfterCommandInterceptor();
        await using var harness = await StateHarness.CreateAsync(interceptor: crasher);
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsAdd(6_000_000)!);
        await harness.PersistAsync(harness.Driver.TryPeerAdd()!);
        await harness.PersistAsync(harness.Driver.TryPeerCommit()!);
        var before = harness.Driver.Us;
        var rowsBefore = await harness.CountRowsAsync();
        var transition = harness.Driver.TryUsCommit()!;
        var crashes = 0;

        // Act & Assert: crash after the 1st, 2nd, ... statement until the save gets through
        for (var k = 1; ; k++)
        {
            crasher.Arm(k);
            try
            {
                await harness.PersistAsync(transition);
            }
            catch (DbUpdateException e) when (e.InnerException is SimulatedCrashException)
            {
                crashes++;
                crasher.Disarm();
                var reloaded = await harness.LoadAsync();
                CommitmentsAssert.Equal(before, reloaded.Commitments);
                Assert.Null(reloaded.SentCommitDiff);
                Assert.Equal(rowsBefore, await harness.CountRowsAsync());
                continue;
            }

            crasher.Disarm();
            break;
        }

        // At least one crash hit after a statement had already run inside the transaction
        Assert.True(crashes >= 2, $"Only {crashes} crash point(s): the save did not write several statements");
        await harness.AssertReloadEqualsAsync();
    }

    [Fact]
    public async Task Given_CrashingUnitOfWork_When_TheThirdSaveCrashes_Then_TheSecondStateReloadsAndMemoryIsNotSwapped()
    {
        // Arrange
        await using var harness = await StateHarness.CreateAsync();
        var channel = await harness.ReloadChannelAsync();
        var results = new[]
        {
            harness.Driver.TryUsAdd(5_000_000)!, harness.Driver.TryUsCommit()!, harness.Driver.TryDeliverRevokeToUs()!
        };
        var unitOfWork = new CrashingUnitOfWork(harness.CreateUnitOfWork(), crashAtSave: 3);

        // Act: persist, then swap memory only after a successful save (invariant I2)
        Exception? crash = null;
        foreach (var result in results)
        {
            try
            {
                await unitOfWork.ChannelStateDbRepository.ApplyAsync(result.Next, result.Transition,
                                                                     StateHarness.ExtrasFor(result));
                await unitOfWork.SaveChangesAsync();
                channel.UpdateCommitments(result.Next, StateHarness.ExtrasFor(result));
            }
            catch (SimulatedCrashException e)
            {
                crash = e;
                break;
            }
        }

        unitOfWork.Dispose();

        // Assert
        Assert.IsType<SimulatedCrashException>(crash);
        CommitmentsAssert.Equal(results[1].Next, channel.Commitments!);
        var reloaded = await harness.LoadAsync();
        CommitmentsAssert.Equal(results[1].Next, reloaded.Commitments);
        Assert.NotNull(reloaded.Commitments.RemoteNextCommit);
        Assert.NotNull(reloaded.SentCommitDiff);
    }

    [Fact]
    public async Task Given_UnsignedPeerAdd_When_RevertedOnDisconnect_Then_ItsRowIsDeleted()
    {
        // Arrange
        await using var harness = await StateHarness.CreateAsync();
        await harness.PersistAsync(harness.Driver.TryPeerAdd()!);
        await using (var context = harness.Db.CreateDbContext())
            Assert.Equal(1, await context.Htlcs.CountAsync(TestContext.Current.CancellationToken));

        // Act
        var reverted = harness.Driver.RevertUs();
        await harness.PersistAsync(reverted);

        // Assert
        Assert.Single(reverted.Transition.DroppedHtlcs);
        await harness.AssertReloadEqualsAsync();
        await using var readContext = harness.Db.CreateDbContext();
        Assert.Equal(0, await readContext.Htlcs.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_SettledHtlc_When_Reloaded_Then_ItIsArchivedUntilPruned()
    {
        // Arrange: run the dance until an HTLC reaches a final state
        await using var harness = await StateHarness.CreateAsync(seed: 3);
        HtlcRecord? settled = null;
        for (var i = 0; i < 500 && settled is null; i++)
        {
            var result = harness.Driver.NextTransition();
            await harness.PersistAsync(result);
            settled = result.Transition.SettledHtlcs.FirstOrDefault();
        }

        Assert.NotNull(settled);

        // Act
        var state = await harness.AssertReloadEqualsAsync();

        // Assert: kept with its final state and removal, out of the engine snapshot
        var archived = Assert.Single(state.SettledHtlcs, h => h.Key == settled.Key);
        CommitmentsAssert.HtlcEqual(settled, archived);
        Assert.DoesNotContain(settled.Key, state.Commitments.Htlcs.Keys);

        await using (var context = harness.Db.CreateDbContext())
        {
            await new ChannelStateDbRepository(context).PruneSettledHtlcsAsync(harness.ChannelId, [settled.Key]);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var pruned = await harness.AssertReloadEqualsAsync();
        Assert.DoesNotContain(pruned.SettledHtlcs, h => h.Key == settled.Key);
    }

    [Fact]
    public async Task Given_OnionSharedSecret_When_LaterTransitionsUpsertTheHtlc_Then_TheSecretIsKept()
    {
        // Arrange
        await using var harness = await StateHarness.CreateAsync();
        var add = harness.Driver.TryPeerAdd()!;
        await harness.PersistAsync(add);
        var key = add.Transition.UpsertedHtlcs.Single().Key;
        var sharedSecret = new Secret(Enumerable.Repeat((byte)0x5E, 32).ToArray());
        await using (var context = harness.Db.CreateDbContext())
        {
            await new ChannelStateDbRepository(context).SetOnionSharedSecretAsync(harness.ChannelId, key,
                                                                                  sharedSecret);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act: the peer signs, which moves the HTLC to another state
        var commit = harness.Driver.TryPeerCommit()!;
        Assert.Contains(commit.Transition.UpsertedHtlcs, h => h.Key == key);
        await harness.PersistAsync(commit);

        // Assert
        await using var readContext = harness.Db.CreateDbContext();
        var stored = await new ChannelStateDbRepository(readContext).GetOnionSharedSecretAsync(harness.ChannelId, key);
        Assert.Equal(sharedSecret, stored);
        await harness.AssertReloadEqualsAsync();
    }

    [Fact]
    public async Task Given_RemoteShachainInExtras_When_Applied_Then_ItIsSavedInTheSameTransition()
    {
        // Arrange
        await using var harness = await StateHarness.CreateAsync();
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);
        var revoke = harness.Driver.TryDeliverRevokeToUs()!;
        var secret = CommitmentDanceDriver.SecretFor(CommitmentDanceDriver.PeerTag, 0);
        ShachainEntry[] shachain = [new(0, 281474976710655, secret)];

        // Act
        await harness.PersistAsync(revoke, new ChannelStateExtras { RemoteShachain = shachain });

        // Assert
        var state = await harness.AssertReloadEqualsAsync();
        var entry = Assert.Single(state.RemoteShachain);
        Assert.Equal(shachain[0], entry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ChannelWithSnapshot_When_UpdatedFromAStaleModel_Then_TheSavedStateIsNotRolledBack(
        bool sameUnitOfWork)
    {
        // Arrange: a model loaded before the transition
        await using var harness = await StateHarness.CreateAsync();
        var stale = await harness.ReloadChannelAsync();
        var add = harness.Driver.TryUsAdd(5_000_000)!;

        // Act
        await using (var context = harness.Db.CreateDbContext())
        {
            var stateRepository = new ChannelStateDbRepository(context);
            var channelRepository = new ChannelDbRepository(context, harness.Db.Sha256);
            await stateRepository.ApplyAsync(add.Next, add.Transition);
            if (!sameUnitOfWork)
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            stale.ShortChannelId = new Domain.Channels.ValueObjects.ShortChannelId(800_000, 1, 0);
            await channelRepository.UpdateAsync(stale);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert: the channel update went through, the commitment scalars kept the transition's values
        var channel = await harness.ReloadChannelAsync();
        Assert.Equal(new Domain.Channels.ValueObjects.ShortChannelId(800_000, 1, 0), channel.ShortChannelId);
        Assert.Equal(1UL, channel.LocalNextHtlcId);
        await using var readContext = harness.Db.CreateDbContext();
        var row = await readContext.Channels.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1UL, row.LocalNextHtlcId);
        Assert.Equal(CommitmentDanceDriver.Point(CommitmentDanceDriver.PeerTag, 1),
                     row.RemoteNextPerCommitmentPoint);
        await harness.AssertReloadEqualsAsync();
    }

    [Fact]
    public async Task Given_LegacyHtlcRow_When_TheChannelIsLoaded_Then_ItIsRefused()
    {
        // Arrange (NL-025): a row in a pre-state-machine state (0 = Offered)
        await using var harness = await StateHarness.CreateAsync();
        await using (var context = harness.Db.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Htlcs" ("ChannelId", "HtlcId", "Direction", "AmountMsat", "PaymentHash", "CltvExpiry",
                    "State", "OnionRoutingPacket")
                VALUES ({0}, 0, 1, 1000, {1}, 500, 0, {2})
                """, [(byte[])harness.ChannelId, new byte[32], new byte[1366]],
                TestContext.Current.CancellationToken);
        }

        // Act
        var exception = await Assert.ThrowsAsync<LegacyHtlcStateException>(harness.ReloadChannelAsync);

        // Assert
        Assert.Contains("NL-025", exception.Message);
    }

    [Fact]
    public async Task Given_LegacyChannelAndGoodChannelOfOnePeer_When_ChannelsAreLoaded_Then_OnlyTheLegacyOneIsRefused()
    {
        // Arrange (NL-025): the harness channel is healthy, a second channel of the same peer has a legacy HTLC row
        await using var harness = await StateHarness.CreateAsync();
        var legacy = SqliteDbTestContext.CreateChannel(false);
        await using (var context = harness.Db.CreateDbContext())
        {
            await new ChannelDbRepository(context, harness.Db.Sha256).AddAsync(legacy);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Htlcs" ("ChannelId", "HtlcId", "Direction", "AmountMsat", "PaymentHash", "CltvExpiry",
                    "State", "OnionRoutingPacket")
                VALUES ({0}, 0, 1, 1000, {1}, 500, 0, {2})
                """, [(byte[])legacy.ChannelId, new byte[32], new byte[1366]],
                TestContext.Current.CancellationToken);
            await new PeerDbRepository(context).AddOrUpdateAsync(
                new PeerModel(SqliteDbTestContext.RemoteNodeId, "127.0.0.1", 9735, "IPv4"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        using var unitOfWork = harness.CreateUnitOfWork();
        var peers = await unitOfWork.GetPeersForStartupAsync();
        var byPeer = (await unitOfWork.ChannelDbRepository.GetByPeerIdAsync(SqliteDbTestContext.RemoteNodeId))
           .ToList();
        var all = (await unitOfWork.ChannelDbRepository.GetAllAsync()).ToList();

        // Assert: startup still loads the healthy channel with its snapshot; the legacy one is refused on its own
        var peer = Assert.Single(peers);
        var loaded = Assert.Single(peer.Channels!);
        Assert.Equal(harness.ChannelId, loaded.ChannelId);
        Assert.NotNull(loaded.Commitments);
        Assert.Equal(harness.ChannelId, Assert.Single(byPeer)!.ChannelId);
        Assert.Equal(harness.ChannelId, Assert.Single(all).ChannelId);
        var refused = await Assert.ThrowsAsync<LegacyHtlcStateException>(() =>
            unitOfWork.ChannelDbRepository.GetByIdAsync(legacy.ChannelId));
        Assert.Contains("NL-025", refused.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_FirstSnapshotStaged_When_UpdatedFromAModelWithoutSnapshot_Then_TheSnapshotScalarsAreKept(
        bool sameUnitOfWork)
    {
        // Arrange: a channel saved without a snapshot; its model's legacy scalars (next ids 5/7, 600k/400k sat)
        // differ from the first snapshot's
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(true);
        var @params = CommitmentParams.FromChannel(channel);
        var driver = new CommitmentDanceDriver(channel.ChannelId, @params, channel.LocalBalance.MilliSatoshi,
                                               channel.RemoteBalance.MilliSatoshi, seed: 3);
        driver.TryUsAdd(5_000_000);
        await using (var context = db.CreateDbContext())
        {
            await new ChannelDbRepository(context, db.Sha256).AddAsync(channel);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Null(channel.Commitments);
        Assert.NotEqual(channel.LocalNextHtlcId, driver.Us.LocalNextHtlcId);

        // Act: the first snapshot is staged, then the channel row is updated from the model (invariant I2: the model
        // gets its snapshot only after the save)
        await using (var context = db.CreateDbContext())
        {
            await new ChannelStateDbRepository(context).InitializeAsync(driver.Us);
            if (!sameUnitOfWork)
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            channel.ShortChannelId = new Domain.Channels.ValueObjects.ShortChannelId(800_000, 1, 0);
            await new ChannelDbRepository(context, db.Sha256).UpdateAsync(channel);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert: the channel update went through and the snapshot's scalars were not overwritten
        await using var readContext = db.CreateDbContext();
        var row = await readContext.Channels.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(driver.Us.LocalNextHtlcId, row.LocalNextHtlcId);
        Assert.Equal(driver.Us.RemoteNextHtlcId, row.RemoteNextHtlcId);
        Assert.Equal((long)driver.Us.LocalBalanceMsat, row.LocalBalanceMsat);
        var reloaded = await new ChannelDbRepository(readContext, db.Sha256).GetByIdAsync(channel.ChannelId);
        Assert.Equal(new Domain.Channels.ValueObjects.ShortChannelId(800_000, 1, 0), reloaded!.ShortChannelId);
        Assert.NotNull(reloaded.Commitments);
        CommitmentsAssert.Equal(driver.Us, reloaded.Commitments);
    }

    /// <summary>
    /// A channel saved in SQLite plus the driver whose "us" side is persisted.
    /// </summary>
    private sealed class StateHarness : IAsyncDisposable
    {
        private readonly IInterceptor? _interceptor;
        private readonly Domain.Channels.Commitments.CommitmentParams _params;

        public SqliteDbTestContext Db { get; }
        public CommitmentDanceDriver Driver { get; }
        public Domain.Channels.ValueObjects.ChannelId ChannelId { get; }

        private StateHarness(SqliteDbTestContext db, ChannelModel channel, CommitmentDanceDriver driver,
                             IInterceptor? interceptor)
        {
            Db = db;
            Driver = driver;
            ChannelId = channel.ChannelId;
            _params = CommitmentParams.FromChannel(channel);
            _interceptor = interceptor;
        }

        public static async Task<StateHarness> CreateAsync(int seed = 1, IInterceptor? interceptor = null)
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

            return new StateHarness(db, channel, driver, interceptor);
        }

        public static ChannelStateExtras? ExtrasFor(CommitmentsResult result)
        {
            var sent = result.Outbound.OfType<OutboundCommitmentSigned>().FirstOrDefault();
            if (sent is not null)
                return new ChannelStateExtras
                {
                    SentCommitDiff = CommitmentDanceDriver.DiffFor(sent.RemoteCommitmentNumber),
                    LastSent = LastSentCommitmentMessage.CommitmentSigned
                };

            return result.Outbound.OfType<OutboundRevokeAndAck>().Any()
                       ? new ChannelStateExtras { LastSent = LastSentCommitmentMessage.RevokeAndAck }
                       : null;
        }

        /// <summary>One transition, one save, on a fresh context.</summary>
        public async Task PersistAsync(CommitmentsResult result, ChannelStateExtras? extras = null)
        {
            await using var context = CreateContext();
            await new ChannelStateDbRepository(context).ApplyAsync(result.Next, result.Transition,
                                                                   extras ?? ExtrasFor(result));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task<PersistedChannelState> LoadAsync()
        {
            await using var context = Db.CreateDbContext();
            return await new ChannelStateDbRepository(context).LoadAsync(ChannelId, _params)
                ?? throw new InvalidOperationException("No commitment state");
        }

        public async Task<PersistedChannelState> AssertReloadEqualsAsync()
        {
            var state = await LoadAsync();
            CommitmentsAssert.Equal(Driver.Us, state.Commitments);
            Assert.Equal(Driver.Us.RemoteNextCommit is null, state.SentCommitDiff is null);
            return state;
        }

        public async Task<ChannelModel> ReloadChannelAsync()
        {
            await using var context = Db.CreateDbContext();
            return await new ChannelDbRepository(context, Db.Sha256).GetByIdAsync(ChannelId)
                ?? throw new InvalidOperationException("Channel was not reloaded");
        }

        public async Task<(int Htlcs, int FeeUpdates, int Commitments, int Shachain)> CountRowsAsync()
        {
            await using var context = Db.CreateDbContext();
            var ct = TestContext.Current.CancellationToken;
            return (await context.Htlcs.CountAsync(ct), await context.FeeUpdates.CountAsync(ct),
                    await context.Commitments.CountAsync(ct), await context.RemoteShachains.CountAsync(ct));
        }

        public UnitOfWork CreateUnitOfWork() =>
            new(Db.CreateDbContext(), NullLogger<UnitOfWork>.Instance, Db.Sha256, new UtxoMemoryRepository());

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private NLightningDbContext CreateContext() =>
            _interceptor is null ? Db.CreateDbContext() : Db.CreateDbContext(_interceptor);
    }

    /// <summary>
    /// Throws <see cref="SimulatedCrashException"/> right after the k-th database command of a save has executed, i.e.
    /// inside the save's transaction with the earlier statements already applied. Queries outside a save don't count.
    /// </summary>
    private sealed class CrashAfterCommandInterceptor : DbCommandInterceptor, ISaveChangesInterceptor
    {
        private int _crashAt;
        private int _executed;
        private bool _inSave;

        public void Arm(int crashAfterCommand)
        {
            _crashAt = crashAfterCommand;
            _executed = 0;
        }

        public void Disarm() => _crashAt = 0;

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
                                                                     InterceptionResult<int> result,
                                                                     CancellationToken cancellationToken = default)
        {
            _inSave = true;
            return ValueTask.FromResult(result);
        }

        public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
                                                CancellationToken cancellationToken = default)
        {
            _inSave = false;
            return ValueTask.FromResult(result);
        }

        public Task SaveChangesFailedAsync(DbContextErrorEventData eventData,
                                           CancellationToken cancellationToken = default)
        {
            _inSave = false;
            return Task.CompletedTask;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
                                                                     CommandExecutedEventData eventData,
                                                                     DbDataReader result,
                                                                     CancellationToken cancellationToken = default)
        {
            CountAndMaybeCrash(result);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
                                                             int result, CancellationToken cancellationToken = default)
        {
            CountAndMaybeCrash(null);
            return ValueTask.FromResult(result);
        }

        private void CountAndMaybeCrash(DbDataReader? reader)
        {
            if (!_inSave || _crashAt <= 0 || ++_executed != _crashAt)
                return;

            reader?.Dispose();
            throw new SimulatedCrashException(_executed);
        }
    }
}