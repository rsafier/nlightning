using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Memory;

/// <summary>
/// Migration <c>AddSpliceFundings</c> and the splice persistence of lane SP1-C (splicing plan §3.8, SP1-C-T4) on
/// SQLite with the real migrations: the data step, the funding rows, the commitments of a pending splice beside the
/// state machine's, the lock's save, the SP-I1 source the signer reloads, crash injection at every splice save
/// (SP-I7) and the dual-funding columns.
/// </summary>
public class SpliceFundingsPersistenceTests
{
    private static readonly CompactPubKey s_spliceLocalKey =
        Convert.FromHexString("0394854aa6eab5b2a8122cc726e9dded053a2184d88256816826d6231c068d4a5b");

    private static readonly CompactPubKey s_spliceRemoteKey =
        Convert.FromHexString("02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27");

    [Fact]
    public async Task Given_ChannelsFromBeforeAddSpliceFundings_When_Migrated_Then_RowsMoveUnderTheirFundingTxId()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert (provider-agnostic, as the other schema round trips)
        await SpliceFundingsSchemaRoundTrip.AssertMigrationAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_RowsOfAddSpliceHardening_When_RolledBack_Then_RefusedWhileASignedOnRecordExistsAndInboundOnlyPeersDropped()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert (provider-agnostic, as the other schema round trips)
        await SpliceHardeningSchemaRoundTrip.AssertDownAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_ANewChannel_When_Added_Then_ItsInitialFundingIsStoredWithIt()
    {
        // Arrange
        await using var harness = await SpliceHarness.CreateAsync();

        // Act
        await using var context = harness.Db.CreateDbContext();
        var set = await new ChannelFundingDbRepository(context).GetFundingSetAsync(harness.ChannelId);

        // Assert
        Assert.NotNull(set);
        Assert.Equal(ChannelFunding.FromFundingOutput(harness.Channel.FundingOutput!), set.Current);
        Assert.Empty(set.Pending);
    }

    [Fact]
    public async Task Given_AFundingOutputWithOtherKeysThanTheKeySets_When_SavedAndConfirmed_Then_TheKeySetsStayAuthoritative()
    {
        // Arrange (regression, lane SP1-C: the initial funding row once took the model's FundingOutput keys, and a
        // reload that preferred them broke the commitment signatures of channels whose FundingOutput keys differ from
        // their key sets): the initial funding row and the reload follow the key sets, and the row follows the SCID
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(true, state: Domain.Channels.Enums.ChannelState.V1FundingSigned);
        channel.FundingOutput!.RemoteFundingPubKey = s_spliceRemoteKey;
        await using (var context = db.CreateDbContext())
        {
            await new ChannelDbRepository(context, db.Sha256).AddAsync(channel);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        channel.ShortChannelId = new ShortChannelId(800_001, 2, 1);
        await using (var context = db.CreateDbContext())
        {
            await new ChannelDbRepository(context, db.Sha256).UpdateAsync(channel);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await using var readContext = db.CreateDbContext();
        var reloaded = await new ChannelDbRepository(readContext, db.Sha256).GetByIdAsync(channel.ChannelId);
        Assert.Equal(SqliteDbTestContext.RemoteFundingPubKey, reloaded!.FundingOutput!.RemoteFundingPubKey);
        var funding = Assert.Single(await new ChannelFundingDbRepository(readContext)
                                       .GetByChannelIdAsync(channel.ChannelId));
        Assert.Equal(SqliteDbTestContext.RemoteFundingPubKey, funding.RemoteFundingPubKey);
        Assert.Equal(SqliteDbTestContext.LocalFundingPubKey, funding.LocalFundingPubKey);
        Assert.Equal(new ShortChannelId(800_001, 2, 1), funding.ShortChannelId);
        var info = await new ChannelSigningInfoDbRepository(readContext).GetAsync(channel.ChannelId);
        Assert.Equal(SqliteDbTestContext.RemoteFundingPubKey, info!.Value.RemoteFundingPubKey);
    }

    [Fact]
    public async Task Given_PendingSpliceCommitments_When_Saved_Then_TheyReloadPerFundingAndTheStateMachineIsUnchanged()
    {
        // Arrange
        await using var harness = await SpliceHarness.CreateAsync();
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);
        var funding = harness.Splice(0x91);
        var us = harness.Driver.Us;
        var local = new LocalCommit(us.LocalCommit.Number, Shifted(us.LocalCommit.Spec),
                                    CommitmentDanceDriver.Signatures(0x21, us.LocalCommit.Spec.Htlcs.Count));
        var remote = new RemoteCommit(us.RemoteCommit.Number, Shifted(us.RemoteCommit.Spec),
                                      us.RemoteCommit.PerCommitmentPoint);
        var next = new RemoteNextCommit(new RemoteCommit(us.RemoteNextCommit!.Commit.Number,
                                                         Shifted(us.RemoteNextCommit.Commit.Spec),
                                                         us.RemoteNextCommit.Commit.PerCommitmentPoint),
                                        CommitmentDanceDriver.Signatures(0x22, 1));

        // Act: the splice commitment step's save
        await harness.SaveAsync(async uow =>
        {
            await uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, funding);
            await uow.ChannelFundingDbRepository.StageLocalCommitmentAsync(harness.ChannelId, funding.FundingTxId,
                                                                           local);
            await uow.ChannelFundingDbRepository.StageRemoteCommitmentAsync(
                harness.ChannelId, funding.FundingTxId, remote, CommitmentDanceDriver.Signatures(0x23, 1));
            await uow.ChannelFundingDbRepository.StageRemoteNextCommitmentAsync(harness.ChannelId,
                                                                                funding.FundingTxId, next);
        });

        // Assert
        await harness.AssertReloadEqualsAsync();
        using var reader = harness.CreateUnitOfWork();
        var repository = reader.ChannelFundingDbRepository;
        var set = await repository.GetFundingSetAsync(harness.ChannelId);
        Assert.Equal(funding, Assert.Single(set!.Pending));
        var loadedLocal = await repository.GetLocalCommitmentAsync(harness.ChannelId, funding.FundingTxId);
        Assert.NotNull(loadedLocal);
        Assert.Equal(local.Number, loadedLocal.Number);
        Assert.Equal(local.Spec.LocalMsat, loadedLocal.Spec.LocalMsat);
        Assert.Equal(local.Spec.Htlcs, loadedLocal.Spec.Htlcs);
        Assert.Equal(local.RemoteSignatures!.Signature, loadedLocal.RemoteSignatures!.Signature);
        Assert.Equal(local.RemoteSignatures.HtlcSignatures, loadedLocal.RemoteSignatures.HtlcSignatures);
        var (loadedRemote, sent) = (await repository.GetRemoteCommitmentAsync(harness.ChannelId,
                                                                               funding.FundingTxId))!.Value;
        Assert.Equal(remote.PerCommitmentPoint, loadedRemote.PerCommitmentPoint);
        Assert.Equal(remote.Spec.RemoteMsat, loadedRemote.Spec.RemoteMsat);
        Assert.NotNull(sent);
        var loadedNext = await repository.GetRemoteNextCommitmentAsync(harness.ChannelId, funding.FundingTxId);
        Assert.Equal(next.Commit.Number, loadedNext!.Commit.Number);
        Assert.Equal(next.SentSignatures.Signature, loadedNext.SentSignatures.Signature);

        // The state machine's own reload and a later transition ignore the splice's rows
        await harness.PersistAsync(harness.Driver.TryDeliverRevokeToUs()!);
        await harness.AssertReloadEqualsAsync();
        Assert.NotNull(await repository.GetRemoteNextCommitmentAsync(harness.ChannelId, funding.FundingTxId));
    }

    [Fact]
    public async Task Given_ASpliceThePeersCommitmentSignedMadePending_When_Reloaded_Then_TheEngineHasItPending()
    {
        // Arrange: the splice step's saves (ours, then the peer's splice commitment_signed on the new funding)
        await using var harness = await SpliceHarness.CreateAsync();
        var funding = harness.Splice(0x95);
        var us = harness.Driver.Us;
        var signatures = CommitmentDanceDriver.Signatures(0x31, us.LocalCommit.Spec.Htlcs.Count);
        await harness.SaveAsync(async uow =>
        {
            await uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, funding);
            await uow.ChannelFundingDbRepository.StageRemoteCommitmentAsync(
                harness.ChannelId, funding.FundingTxId,
                new RemoteCommit(us.RemoteCommit.Number, Shifted(us.RemoteCommit.Spec),
                                 us.RemoteCommit.PerCommitmentPoint), CommitmentDanceDriver.Signatures(0x32, 0));
            await uow.ChannelFundingDbRepository.StageLocalCommitmentAsync(
                harness.ChannelId, funding.FundingTxId,
                new LocalCommit(us.LocalCommit.Number, Shifted(us.LocalCommit.Spec), signatures));
        });

        // Act: a restart
        var reloaded = await harness.ReloadChannelAsync();

        // Assert: the pending splice is back in the engine, with the peer's signatures of our commitment on it
        var commitments = reloaded.Commitments!;
        Assert.Equal(funding.FundingTxId, Assert.Single(commitments.PendingFundings).FundingTxId);
        Assert.Equal(signatures.Signature, commitments.LocalCommit.SignaturesFor(funding.FundingTxId)!.Signature);
        Assert.Equal(us.LocalCommit.Number, commitments.LocalCommit.Number);
    }

    [Fact]
    public async Task Given_ASpliceOnlyWeSignedFor_When_Reloaded_Then_TheEngineHasNoPendingFunding()
    {
        // Arrange: only our commitment_signed's save (the peer's splice commitment_signed never arrived)
        await using var harness = await SpliceHarness.CreateAsync();
        var funding = harness.Splice(0x96);
        var us = harness.Driver.Us;
        await harness.SaveAsync(async uow =>
        {
            await uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, funding);
            await uow.ChannelFundingDbRepository.StageRemoteCommitmentAsync(
                harness.ChannelId, funding.FundingTxId,
                new RemoteCommit(us.RemoteCommit.Number, Shifted(us.RemoteCommit.Spec),
                                 us.RemoteCommit.PerCommitmentPoint), CommitmentDanceDriver.Signatures(0x33, 0));
        });

        // Act
        var reloaded = await harness.ReloadChannelAsync();

        // Assert: as before the restart, nothing is pending in the engine
        Assert.Empty(reloaded.Commitments!.PendingFundings);
        await harness.AssertReloadEqualsAsync();
    }

    [Fact]
    public async Task Given_TheCurrentFunding_When_StagingSpliceCommitmentsForIt_Then_Refused()
    {
        // Arrange
        await using var harness = await SpliceHarness.CreateAsync();
        using var uow = harness.CreateUnitOfWork();
        var repository = uow.ChannelFundingDbRepository;
        var current = harness.Channel.FundingOutput!.TransactionId!.Value;

        // Act / Assert: the current funding's slots are the state machine's
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.StageLocalCommitmentAsync(harness.ChannelId, current, harness.Driver.Us.LocalCommit));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.StageRemoteCommitmentAsync(harness.ChannelId, current, harness.Driver.Us.RemoteCommit,
                                                        null));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.StageLocalCommitmentAsync(harness.ChannelId, TxIdOf(0x99),
                                                       harness.Driver.Us.LocalCommit));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.UpsertAsync(harness.ChannelId,
                                         harness.Splice(0x92) with { Status = ChannelFundingStatus.Current }));
    }

    [Fact]
    public async Task Given_APersistedSpliceCommitment_When_TheSignerReloadsTheChannel_Then_ItCarriesTheSPI1Mark()
    {
        // Arrange
        await using var harness = await SpliceHarness.CreateAsync();
        var funding = harness.Splice(0x93);
        var discarded = harness.Splice(0x94) with { Status = ChannelFundingStatus.Discarded };
        var unsigned = harness.Splice(0x95);
        var us = harness.Driver.Us;

        // Act
        await harness.SaveAsync(async uow =>
        {
            await uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, funding);
            await uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, discarded);
            await uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, unsigned);
            await uow.ChannelFundingDbRepository.StageLocalCommitmentAsync(
                harness.ChannelId, funding.FundingTxId,
                new LocalCommit(us.LocalCommit.Number, us.LocalCommit.Spec, CommitmentDanceDriver.Signatures(1, 0)));
        });

        // Assert: the pending and discarded fundings, and the mark only for the one with its commitment saved
        using var reader = harness.CreateUnitOfWork();
        var info = await reader.ChannelSigningInfoDbRepository.GetAsync(harness.ChannelId);
        Assert.NotNull(info);
        Assert.Equal(harness.Channel.FundingOutput!.TransactionId!.Value, info.Value.FundingTxId);
        Assert.Equal(0u, info.Value.LocalFundingKeyIndex);
        Assert.Equal([funding, discarded, unsigned], info.Value.Fundings!);
        var mark = Assert.Single(info.Value.PersistedSpliceCommitments!);
        Assert.Equal(funding.FundingTxId, mark.Key);
        Assert.Equal(us.LocalCommit.Number, mark.Value);
        var all = await reader.ChannelSigningInfoDbRepository.GetAllAsync();
        Assert.Equal(info.Value.PersistedSpliceCommitments, all[harness.ChannelId].PersistedSpliceCommitments);
    }

    [Fact]
    public async Task Given_TheSpliceCommitmentSaveCrashes_When_TheSignerReloads_Then_NoMarkAndNoFundingRow()
    {
        // Arrange (SP-I1 across a crash: MarkSpliceCommitmentPersisted is called only after this save succeeded)
        await using var harness = await SpliceHarness.CreateAsync();
        var funding = harness.Splice(0x96);
        var us = harness.Driver.Us;

        // Act
        using (var crashing = new CrashingUnitOfWork(harness.CreateUnitOfWork(), crashAtSave: 1))
        {
            await crashing.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, funding);
            await crashing.ChannelFundingDbRepository.StageLocalCommitmentAsync(
                harness.ChannelId, funding.FundingTxId,
                new LocalCommit(us.LocalCommit.Number, us.LocalCommit.Spec, CommitmentDanceDriver.Signatures(1, 0)));
            await Assert.ThrowsAsync<SimulatedCrashException>(crashing.SaveChangesAsync);
        }

        // Assert
        using var reader = harness.CreateUnitOfWork();
        var info = await reader.ChannelSigningInfoDbRepository.GetAsync(harness.ChannelId);
        Assert.Null(info!.Value.Fundings);
        Assert.Null(info.Value.PersistedSpliceCommitments);
        Assert.Null(await reader.ChannelFundingDbRepository.GetLocalCommitmentAsync(harness.ChannelId,
                                                                                   funding.FundingTxId));
    }

    [Fact]
    public async Task Given_CrashAfterEachStatementOfEverySpliceSave_When_Reloaded_Then_NothingPartialIsStored()
    {
        // Arrange: the splice saves of lane SP1-C (splicing plan SP-I7): the negotiated funding, the splice
        // commitment step, the revocation of a batched commitment and the lock with the state machine's transition
        var crasher = new CrashAfterCommandInterceptor();
        await using var harness = await SpliceHarness.CreateAsync(crasher);
        var funding = harness.Splice(0x97, 1_000_000);
        var sibling = harness.Splice(0x98);
        var us = harness.Driver.Us;
        var lockAdd = harness.Driver.TryUsAdd(4_000_000)!;
        var saves = new List<(string Name, Func<UnitOfWork, Task> Stage)>
        {
            ("negotiated", async uow =>
            {
                await uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, funding);
                await uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, sibling);
            }),
            ("splice commitment", async uow =>
            {
                await uow.ChannelFundingDbRepository.StageLocalCommitmentAsync(
                    harness.ChannelId, funding.FundingTxId,
                    new LocalCommit(us.LocalCommit.Number, Shifted(us.LocalCommit.Spec),
                                    CommitmentDanceDriver.Signatures(1, 0)));
                await uow.ChannelFundingDbRepository.StageRemoteCommitmentAsync(
                    harness.ChannelId, funding.FundingTxId,
                    new RemoteCommit(us.RemoteCommit.Number, Shifted(us.RemoteCommit.Spec),
                                     us.RemoteCommit.PerCommitmentPoint), CommitmentDanceDriver.Signatures(2, 0));
                await uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId,
                                                                 funding with { SpliceLockedSent = true });
            }),
            ("revocation log", async uow =>
            {
                await uow.ChannelFundingDbRepository.StageRevokedCommitmentAsync(
                    harness.ChannelId, funding.FundingTxId,
                    new RemoteCommit(3, new CommitmentSpec(CommitmentSide.Remote, 253, 1, 2,
                                                           [new SpecHtlc(HtlcDirection.Outgoing, 0, 5_000_000,
                                                                         CommitmentDanceDriver.PaymentHash(1), 500)]),
                                     us.RemoteCommit.PerCommitmentPoint));
                await uow.ChannelFundingDbRepository.SetDualFundedAsync(harness.ChannelId,
                                                                        LightningMoney.Satoshis(1),
                                                                        LightningMoney.Satoshis(2));
            }),
            ("lock", async uow =>
            {
                await uow.ChannelFundingDbRepository.ApplyLockAsync(
                    harness.ChannelId, funding with { Status = ChannelFundingStatus.Current },
                    [
                        harness.CurrentFunding with { Status = ChannelFundingStatus.Replaced },
                        sibling with { Status = ChannelFundingStatus.Discarded }
                    ]);
                await uow.ChannelStateDbRepository.InitializeAsync(lockAdd.Next);
            })
        };

        foreach (var (name, stage) in saves)
        {
            var before = await harness.SnapshotRowsAsync();
            var crashes = 0;

            // Act: crash after the 1st, 2nd, ... statement until the save gets through
            for (var k = 1; ; k++)
            {
                crasher.Arm(k);
                try
                {
                    using var uow = harness.CreateUnitOfWork(withInterceptor: true);
                    await stage(uow);
                    await uow.SaveChangesAsync();
                }
                catch (DbUpdateException e) when (e.InnerException is SimulatedCrashException)
                {
                    crashes++;
                    crasher.Disarm();

                    // Assert: every table of the save is as before it
                    Assert.Equal(before, await harness.SnapshotRowsAsync());
                    continue;
                }

                crasher.Disarm();
                break;
            }

            Assert.True(crashes >= 1, $"The {name} save wrote no statement");
            var after = await harness.SnapshotRowsAsync();
            Assert.True(before != after, $"The {name} save changed nothing ({crashes} crashes):\n{after}");
        }

        // The lock's save moved the current funding and the state machine to the splice
        var channel = await harness.ReloadChannelAsync();
        Assert.Equal(funding.FundingTxId, channel.FundingOutput!.TransactionId);
        Assert.Equal(funding.CapacitySatoshis, (ulong)channel.FundingOutput.Amount.Satoshi);
        AssertSameStateMachine(harness.Driver.Us, channel.Commitments!);
    }

    [Fact]
    public async Task Given_ALockedSplice_When_TheChannelIsReloadedAndUpdatedFromAStaleModel_Then_ItKeepsTheSplice()
    {
        // Arrange
        await using var harness = await SpliceHarness.CreateAsync();
        var funding = harness.Splice(0x9a, 1_000_000) with { ShortChannelId = new ShortChannelId(900_000, 12, 1) };
        var staleModel = await harness.ReloadChannelAsync();
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, funding));
        var local = new LocalCommit(harness.Driver.Us.LocalCommit.Number, harness.Driver.Us.LocalCommit.Spec,
                                    CommitmentDanceDriver.Signatures(3, 0));
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.StageLocalCommitmentAsync(
                                    harness.ChannelId, funding.FundingTxId, local));

        // Act: the lock's save, then an update from a model that predates it
        var locked = funding with { Status = ChannelFundingStatus.Current, SpliceLockedReceived = true };
        await harness.SaveAsync(async uow =>
        {
            await uow.ChannelFundingDbRepository.ApplyLockAsync(
                harness.ChannelId, locked, [harness.CurrentFunding with { Status = ChannelFundingStatus.Replaced }]);
            await uow.ChannelStateDbRepository.InitializeAsync(harness.Driver.Us);
        });
        await harness.SaveAsync(uow => uow.ChannelDbRepository.UpdateAsync(staleModel));

        // Assert
        var channel = await harness.ReloadChannelAsync();
        Assert.Equal(funding.FundingTxId, channel.FundingOutput!.TransactionId);
        Assert.Equal(funding.OutputIndex, channel.FundingOutput.Index);
        Assert.Equal(s_spliceLocalKey, channel.FundingOutput.LocalFundingPubKey);
        Assert.Equal(s_spliceRemoteKey, channel.FundingOutput.RemoteFundingPubKey);
        Assert.Equal(funding.ShortChannelId, channel.ShortChannelId);
        AssertSameStateMachine(harness.Driver.Us, channel.Commitments!);

        using var reader = harness.CreateUnitOfWork();
        var set = await reader.ChannelFundingDbRepository.GetFundingSetAsync(harness.ChannelId);
        Assert.Equal(locked, set!.Current);
        Assert.Empty(set.Pending);
        var all = await reader.ChannelFundingDbRepository.GetByChannelIdAsync(harness.ChannelId);
        Assert.Equal(ChannelFundingStatus.Replaced, all[0].Status);
        var info = (await reader.ChannelSigningInfoDbRepository.GetAsync(harness.ChannelId))!.Value;
        Assert.Equal(funding.FundingTxId, info.FundingTxId);
        Assert.Equal(funding.LocalFundingKeyIndex, info.LocalFundingKeyIndex);
        Assert.Equal(s_spliceLocalKey, info.LocalFundingPubKey);
        Assert.Equal(funding.CapacityMsat, info.FundingSatoshis);
        Assert.Equal(harness.CurrentFunding.FundingTxId, Assert.Single(info.Fundings!).FundingTxId);
        Assert.Null(info.PersistedSpliceCommitments);

        // Only the state machine's rows are left: the old funding's slots went with the lock
        await using var context = harness.Db.CreateDbContext();
        Assert.All(await context.Commitments.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken),
                   c => Assert.Equal(funding.FundingTxId, c.FundingTxId));
    }

    [Fact]
    public async Task Given_ADiscardedSplice_When_TheModelsShortChannelIdChanges_Then_TheChannelAndItsInitialFundingFollow()
    {
        // Arrange (regression: any non-initial funding row, even a splice that never locked, froze the channel's
        // funding columns and its initial funding row)
        await using var harness = await SpliceHarness.CreateAsync();
        var splice = harness.Splice(0x9e);
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, splice));
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(
                                    harness.ChannelId, splice with { Status = ChannelFundingStatus.Discarded }));
        var pending = harness.Splice(0x9f);
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, pending));
        var model = await harness.ReloadChannelAsync();
        var scid = new ShortChannelId(900_100, 7, 0);

        // Act: a reorg (or a first confirmation) moves the initial funding's SCID
        model.ShortChannelId = scid;
        await harness.SaveAsync(uow => uow.ChannelDbRepository.UpdateAsync(model));

        // Assert
        var channel = await harness.ReloadChannelAsync();
        Assert.Equal(scid, channel.ShortChannelId);
        Assert.Equal(harness.CurrentFunding.FundingTxId, channel.FundingOutput!.TransactionId);
        using var reader = harness.CreateUnitOfWork();
        var set = await reader.ChannelFundingDbRepository.GetFundingSetAsync(harness.ChannelId);
        Assert.Equal(ChannelFundingKind.Initial, set!.Current.Kind);
        Assert.Equal(scid, set.Current.ShortChannelId);
        Assert.Equal(pending.FundingTxId, Assert.Single(set.Pending).FundingTxId);
    }

    [Fact]
    public async Task Given_RevokedCommitmentsOnTwoFundings_When_Read_Then_TheCurrentFundingsRowComesFirst()
    {
        // Arrange (SP-I5: the same number is logged per funding)
        await using var harness = await SpliceHarness.CreateAsync();
        var funding = harness.Splice(0x9b);
        var htlc = new SpecHtlc(HtlcDirection.Incoming, 0, 7_000_000, CommitmentDanceDriver.PaymentHash(2), 600);
        var point = harness.Driver.Us.RemoteCommit.PerCommitmentPoint;
        var onCurrent = new RemoteCommit(5, new CommitmentSpec(CommitmentSide.Remote, 253, 10, 20, [htlc]), point);
        var onSplice = new RemoteCommit(5, new CommitmentSpec(CommitmentSide.Remote, 253, 30, 40, [htlc]), point);

        // Act
        await harness.SaveAsync(async uow =>
        {
            await uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, funding);
            Assert.True(await uow.ChannelFundingDbRepository.StageRevokedCommitmentAsync(
                            harness.ChannelId, funding.FundingTxId, onSplice));
            Assert.True(await uow.ChannelFundingDbRepository.StageRevokedCommitmentAsync(
                            harness.ChannelId, harness.CurrentFunding.FundingTxId, onCurrent));
            Assert.False(await uow.ChannelFundingDbRepository.StageRevokedCommitmentAsync(
                             harness.ChannelId, funding.FundingTxId,
                             new RemoteCommit(6, new CommitmentSpec(CommitmentSide.Remote, 253, 1, 2, []), point)));
        });

        // Assert
        using var reader = harness.CreateUnitOfWork();
        var current = await reader.RevokedCommitmentDbRepository.GetAsync(harness.ChannelId, 5);
        Assert.Equal(10UL, current!.Spec.LocalMsat);
        Assert.Equal(2, (await reader.RevokedCommitmentDbRepository.GetByChannelIdAsync(harness.ChannelId)).Count);
    }

    [Fact]
    public async Task Given_ARevokedNumberOnTwoNonCurrentFundings_When_Read_Then_EachFundingsOwnRowIsReturned()
    {
        // Arrange (a breach spending a pending splice: the current funding has no entry for the number)
        await using var harness = await SpliceHarness.CreateAsync();
        var first = harness.Splice(0x9c);
        var second = harness.Splice(0x9d);
        var htlc = new SpecHtlc(HtlcDirection.Incoming, 0, 7_000_000, CommitmentDanceDriver.PaymentHash(3), 600);
        var point = harness.Driver.Us.RemoteCommit.PerCommitmentPoint;
        var onFirst = new RemoteCommit(7, new CommitmentSpec(CommitmentSide.Remote, 253, 50, 60, [htlc]), point);
        var onSecond = new RemoteCommit(7, new CommitmentSpec(CommitmentSide.Remote, 253, 70, 80, [htlc]), point);

        // Act: the funding with the higher txid is created first, so the fallback cannot be the txid order
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, second));
        await harness.SaveAsync(async uow =>
        {
            await uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, first);
            Assert.True(await uow.ChannelFundingDbRepository.StageRevokedCommitmentAsync(
                            harness.ChannelId, second.FundingTxId, onSecond));
            Assert.True(await uow.ChannelFundingDbRepository.StageRevokedCommitmentAsync(
                            harness.ChannelId, first.FundingTxId, onFirst));
        });

        // Assert
        using var reader = harness.CreateUnitOfWork();
        var bySecond = await reader.RevokedCommitmentDbRepository.GetAsync(harness.ChannelId, second.FundingTxId, 7);
        Assert.Equal(70UL, bySecond!.Spec.LocalMsat);
        var byFirst = await reader.RevokedCommitmentDbRepository.GetAsync(harness.ChannelId, first.FundingTxId, 7);
        Assert.Equal(50UL, byFirst!.Spec.LocalMsat);
        Assert.Null(await reader.RevokedCommitmentDbRepository.GetAsync(
                        harness.ChannelId, harness.CurrentFunding.FundingTxId, 7));
        var fallback = await reader.RevokedCommitmentDbRepository.GetAsync(harness.ChannelId, 7);
        Assert.Equal(70UL, fallback!.Spec.LocalMsat);
    }

    [Fact]
    public async Task Given_ADualFundedChannel_When_SavedAndUpdatedFromTheModel_Then_ItsContributionsAreKept()
    {
        // Arrange
        await using var harness = await SpliceHarness.CreateAsync();

        // Act
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.SetDualFundedAsync(
                                    harness.ChannelId, LightningMoney.Satoshis(600_000),
                                    LightningMoney.Satoshis(400_000)));
        await harness.SaveAsync(async uow => await uow.ChannelDbRepository.UpdateAsync(
                                                 await harness.ReloadChannelAsync()));

        // Assert
        using var reader = harness.CreateUnitOfWork();
        var contributions = await reader.ChannelFundingDbRepository.GetDualFundedContributionsAsync(harness.ChannelId);
        Assert.NotNull(contributions);
        Assert.Equal(LightningMoney.Satoshis(600_000), contributions.Value.Local);
        Assert.Equal(LightningMoney.Satoshis(400_000), contributions.Value.Remote);
        Assert.Null(await reader.ChannelFundingDbRepository.GetDualFundedContributionsAsync(TestChannelId(0x7f)));
    }

    private static CommitmentSpec Shifted(CommitmentSpec spec) =>
        new(spec.Holder, spec.FeeratePerKw, spec.LocalMsat + 1_000_000, spec.RemoteMsat, spec.Htlcs);

    /// <summary>
    /// The state machine reloaded after a lock: equal to the engine's but for its <see cref="CommitmentParams"/>, which
    /// the reload builds from the channel's new current funding.
    /// </summary>
    private static void AssertSameStateMachine(ChannelCommitments expected, ChannelCommitments actual)
    {
        Assert.Equal(expected.LocalBalanceMsat, actual.LocalBalanceMsat);
        Assert.Equal(expected.RemoteBalanceMsat, actual.RemoteBalanceMsat);
        Assert.Equal(expected.LocalNextHtlcId, actual.LocalNextHtlcId);
        Assert.Equal(expected.Htlcs.Keys, actual.Htlcs.Keys);
        Assert.Equal(expected.LocalCommit.Number, actual.LocalCommit.Number);
        Assert.Equal(expected.LocalCommit.Spec, actual.LocalCommit.Spec);
        Assert.Equal(expected.RemoteCommit.Number, actual.RemoteCommit.Number);
        Assert.Equal(expected.RemoteCommit.Spec, actual.RemoteCommit.Spec);
        Assert.Equal(expected.RemoteNextCommit is null, actual.RemoteNextCommit is null);
    }

    private static TxId TxIdOf(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static ChannelId TestChannelId(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    /// <summary>A channel with its commitment snapshot on SQLite, and its splice fixtures.</summary>
    private sealed class SpliceHarness : IAsyncDisposable
    {
        private readonly CrashAfterCommandInterceptor? _interceptor;

        public SqliteDbTestContext Db { get; }
        public ChannelModel Channel { get; }
        public ChannelId ChannelId => Channel.ChannelId;
        public CommitmentDanceDriver Driver { get; }
        public ChannelFunding CurrentFunding => ChannelFunding.FromFundingOutput(Channel.FundingOutput!)!;

        private SpliceHarness(SqliteDbTestContext db, ChannelModel channel, CommitmentDanceDriver driver,
                              CrashAfterCommandInterceptor? interceptor)
        {
            Db = db;
            Channel = channel;
            Driver = driver;
            _interceptor = interceptor;
        }

        public static async Task<SpliceHarness> CreateAsync(CrashAfterCommandInterceptor? interceptor = null)
        {
            var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
            var channel = SqliteDbTestContext.CreateChannel(true);
            var driver = new CommitmentDanceDriver(channel.ChannelId, CommitmentParams.FromChannel(channel),
                                                   channel.LocalBalance.MilliSatoshi,
                                                   channel.RemoteBalance.MilliSatoshi);
            await using (var context = db.CreateDbContext())
            {
                await new ChannelDbRepository(context, db.Sha256).AddAsync(channel);
                await new ChannelStateDbRepository(context).InitializeAsync(driver.Us);
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            return new SpliceHarness(db, channel, driver, interceptor);
        }

        /// <summary>
        /// A pending splice funding (our key index 1) with a distinct txid; a funding that gets locked keeps the
        /// channel's capacity so the engine's snapshot stays conserved (I6).
        /// </summary>
        public ChannelFunding Splice(byte seed, ulong capacitySatoshis = 1_250_000) =>
            new(TxIdOf(seed), 1, capacitySatoshis, s_spliceLocalKey, s_spliceRemoteKey, 1, 250_000_000, 0,
                ChannelFundingKind.Splice, ChannelFundingStatus.Pending, 2_000, 812_000, null, null, null);

        public UnitOfWork CreateUnitOfWork(bool withInterceptor = false) =>
            new(withInterceptor && _interceptor is not null ? Db.CreateDbContext(_interceptor) : Db.CreateDbContext(),
                NullLogger<UnitOfWork>.Instance, Db.Sha256, new UtxoMemoryRepository());

        public async Task SaveAsync(Func<UnitOfWork, Task> stage)
        {
            using var uow = CreateUnitOfWork();
            await stage(uow);
            await uow.SaveChangesAsync();
        }

        public async Task PersistAsync(CommitmentsResult result) =>
            await SaveAsync(uow => uow.ChannelStateDbRepository.ApplyAsync(result.Next, result.Transition));

        public async Task AssertReloadEqualsAsync()
        {
            await using var context = Db.CreateDbContext();
            var state = await new ChannelStateDbRepository(context).LoadAsync(ChannelId,
                                                                              CommitmentParams.FromChannel(Channel));
            CommitmentsAssert.Equal(Driver.Us, state!.Commitments);
        }

        public async Task<ChannelModel> ReloadChannelAsync()
        {
            await using var context = Db.CreateDbContext();
            return await new ChannelDbRepository(context, Db.Sha256).GetByIdAsync(ChannelId)
                ?? throw new InvalidOperationException("Channel was not reloaded");
        }

        /// <summary>Every row a splice save touches, as comparable text.</summary>
        public async Task<string> SnapshotRowsAsync()
        {
            await using var context = Db.CreateDbContext();
            var ct = TestContext.Current.CancellationToken;
            var fundings = await context.ChannelFundings.AsNoTracking().ToListAsync(ct);
            var commitments = await context.Commitments.AsNoTracking().ToListAsync(ct);
            var revoked = await context.RevokedCommitments.AsNoTracking().ToListAsync(ct);
            var htlcs = await context.Htlcs.AsNoTracking().ToListAsync(ct);
            var channel = await context.Channels.AsNoTracking().SingleAsync(c => c.ChannelId == ChannelId, ct);
            IEnumerable<string> lines =
            [
                .. fundings.OrderBy(f => f.Sequence)
                           .Select(f => $"F {f.FundingTxId} {f.Status} {f.Kind} {f.SpliceLockedSent}"),
                .. commitments.OrderBy(c => c.FundingTxId.ToString()).ThenBy(c => c.Slot)
                              .Select(c => $"C {c.FundingTxId} {c.Slot} {c.Number} {c.LocalMsat}"),
                .. revoked.OrderBy(r => r.Number).Select(r => $"R {r.FundingTxId} {r.Number}"),
                .. htlcs.OrderBy(h => h.HtlcId).Select(h => $"H {h.HtlcId} {h.State}"),
                $"CH {channel.FundingTxId} {channel.FundingAmountSatoshis} {channel.IsDualFunded} "
              + $"{channel.LocalNextHtlcId}"
            ];
            return string.Join('\n', lines);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}