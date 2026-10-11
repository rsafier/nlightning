using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-479 (splicing plan SP-I3, SP-I5, SP2-C-T3) on SQLite with the real migrations: the <c>revoke_and_ack</c> that
/// revokes a number revokes it on every funding it was signed on, so <see cref="ChannelStateDbRepository.ApplyAsync"/>
/// logs it once per funding with that funding's balances in the same save, and the on-chain side reads a funding's log
/// with <c>GetByFundingAsync</c>. Also SP-I2: the pending funding's commitment slots follow every transition.
/// </summary>
public class RevokedCommitmentPerFundingTests
{
    private static readonly CompactPubKey s_spliceLocalKey =
        Convert.FromHexString("0394854aa6eab5b2a8122cc726e9dded053a2184d88256816826d6231c068d4a5b");

    private static readonly CompactPubKey s_spliceRemoteKey =
        Convert.FromHexString("02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27");

    [Fact]
    public async Task Given_ARevokeAndAckWhileASpliceIsPending_When_Applied_Then_TheNumberIsLoggedOnBothFundings()
    {
        // Arrange: commitment 1 carries an HTLC and was signed on the current funding and on the pending splice
        await using var harness = await Harness.CreateAsync();
        var raa = await harness.RevokeCommitmentWithAnHtlcAsync();
        var revoked = raa.Transition.RevokedRemoteCommit!;
        var splice = harness.Splice(0x91);
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, splice));

        // Act
        await harness.PersistAsync(raa with
        {
            Transition = raa.Transition with { RevokedRemoteCommitFundings = [splice] }
        });

        // Assert
        using var reader = harness.CreateUnitOfWork();
        var log = reader.RevokedCommitmentDbRepository;
        var onCurrent = Assert.Single(await log.GetByFundingAsync(harness.ChannelId, harness.CurrentFundingTxId));
        Assert.Equal(revoked.Number, onCurrent.Number);
        Assert.Equal(revoked.Spec, onCurrent.Spec);
        Assert.Equal(harness.CurrentFundingTxId, onCurrent.FundingTxId);

        var onSplice = Assert.Single(await log.GetByFundingAsync(harness.ChannelId, splice.FundingTxId));
        Assert.Equal(revoked.Number, onSplice.Number);
        Assert.Equal(splice.FundingTxId, onSplice.FundingTxId);
        Assert.Equal(ChannelCommitments.SpecFor(revoked.Spec, splice), onSplice.Spec);
        Assert.Equal(revoked.Spec.LocalMsat + 250_000_000, onSplice.Spec.LocalMsat);
        Assert.Equal(revoked.Spec.Htlcs, onSplice.Spec.Htlcs);

        var byNumber = await log.GetAsync(harness.ChannelId, splice.FundingTxId, revoked.Number);
        Assert.Equal(onSplice.Spec, byNumber!.Spec);
        Assert.Equal(2, (await log.GetByChannelIdAsync(harness.ChannelId)).Count);
    }

    [Fact]
    public async Task Given_ADiscardedFundingTheRevokedNumberWasSignedOn_When_Applied_Then_ItsRowIsKept()
    {
        // Arrange (SP-I5: a funding discarded since the commitment was signed still gets its revocation data)
        await using var harness = await Harness.CreateAsync();
        var raa = await harness.RevokeCommitmentWithAnHtlcAsync();
        var discarded = harness.Splice(0x92) with { Status = ChannelFundingStatus.Discarded };
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, discarded));

        // Act
        await harness.PersistAsync(raa with
        {
            Transition = raa.Transition with { RevokedRemoteCommitFundings = [discarded] }
        });

        // Assert
        using var reader = harness.CreateUnitOfWork();
        var row = Assert.Single(await reader.RevokedCommitmentDbRepository.GetByFundingAsync(
                                    harness.ChannelId, discarded.FundingTxId));
        Assert.Equal(raa.Transition.RevokedRemoteCommit!.Number, row.Number);
    }

    [Fact]
    public async Task Given_TheCurrentFundingListedAmongTheRevokedFundings_When_Applied_Then_OneRowForIt()
    {
        // Arrange: after a lock the rebased list may name the (new) current funding; it is logged once
        await using var harness = await Harness.CreateAsync();
        var raa = await harness.RevokeCommitmentWithAnHtlcAsync();
        var current = ChannelFunding.FromFundingOutput(harness.Channel.FundingOutput!)!;

        // Act
        await harness.PersistAsync(raa with
        {
            Transition = raa.Transition with { RevokedRemoteCommitFundings = [current] }
        });

        // Assert
        using var reader = harness.CreateUnitOfWork();
        var row = Assert.Single(await reader.RevokedCommitmentDbRepository.GetByChannelIdAsync(harness.ChannelId));
        Assert.Equal(raa.Transition.RevokedRemoteCommit!.Spec, row.Spec);
    }

    [Fact]
    public async Task Given_AFundingWithoutLog_When_ReadByFunding_Then_Empty()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        await harness.PersistAsync(await harness.RevokeCommitmentWithAnHtlcAsync());

        // Act
        using var reader = harness.CreateUnitOfWork();
        var rows = await reader.RevokedCommitmentDbRepository.GetByFundingAsync(harness.ChannelId, TxIdOf(0x93));

        // Assert
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Given_RowsStagedInTheUnitOfWork_When_ReadByFunding_Then_TheyAreSeenBeforeTheSave()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        var raa = await harness.RevokeCommitmentWithAnHtlcAsync();
        var splice = harness.Splice(0x94);
        using var uow = harness.CreateUnitOfWork();
        await uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, splice);

        // Act
        await uow.ChannelStateDbRepository.ApplyAsync(raa.Next, raa.Transition with
        {
            RevokedRemoteCommitFundings = [splice]
        });
        var staged = await uow.RevokedCommitmentDbRepository.GetByFundingAsync(harness.ChannelId, splice.FundingTxId);

        // Assert
        Assert.Single(staged);
    }

    [Fact]
    public async Task Given_APendingSplice_When_ATransitionChangesTheCommitments_Then_ItsSlotsFollowTheStateMachine()
    {
        // Arrange (SP-I2): the state machine at commitment 1 with a pending splice whose signatures it carries
        await using var harness = await Harness.CreateAsync();
        var raa = await harness.RevokeCommitmentWithAnHtlcAsync();
        var splice = harness.Splice(0x95);
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, splice));
        var next = raa.Next;
        var localSignatures = CommitmentDanceDriver.Signatures(0x31, next.LocalCommit.Spec.Htlcs.Count);
        var withSplice = ChannelCommitments.Restore(
            next.ChannelId, next.Params, next.LocalBalanceMsat, next.RemoteBalanceMsat, next.Htlcs.Values,
            next.FeeUpdates, next.LocalNextHtlcId, next.RemoteNextHtlcId,
            next.LocalCommit with { PendingFundingSignatures = [new FundingSignatures(splice.FundingTxId, localSignatures)] },
            next.RemoteCommit, next.RemoteNextCommit, next.RemoteNextPerCommitmentPoint, [splice]);

        // Act
        await harness.SaveAsync(uow => uow.ChannelStateDbRepository.ApplyAsync(withSplice, raa.Transition with
        {
            LocalCommitChanged = true,
            RemoteCommitChanged = true,
            RevokedRemoteCommitFundings = [splice]
        }));

        // Assert
        using var reader = harness.CreateUnitOfWork();
        var fundings = reader.ChannelFundingDbRepository;
        var local = await fundings.GetLocalCommitmentAsync(harness.ChannelId, splice.FundingTxId);
        Assert.NotNull(local);
        Assert.Equal(next.LocalCommit.Number, local.Number);
        Assert.Equal(ChannelCommitments.SpecFor(next.LocalCommit.Spec, splice), local.Spec);
        Assert.Equal(localSignatures.Signature, local.RemoteSignatures!.Signature);
        Assert.Equal(localSignatures.HtlcSignatures, local.RemoteSignatures.HtlcSignatures);

        var (remote, _) = (await fundings.GetRemoteCommitmentAsync(harness.ChannelId, splice.FundingTxId))!.Value;
        Assert.Equal(next.RemoteCommit.Number, remote.Number);
        Assert.Equal(ChannelCommitments.SpecFor(next.RemoteCommit.Spec, splice), remote.Spec);
        Assert.Equal(next.RemoteCommit.PerCommitmentPoint, remote.PerCommitmentPoint);
        Assert.Null(await fundings.GetRemoteNextCommitmentAsync(harness.ChannelId, splice.FundingTxId));

        // The state machine's own slots are unchanged by it
        await using var context = harness.Db.CreateDbContext();
        var state = await new ChannelStateDbRepository(context).LoadAsync(harness.ChannelId, next.Params);
        Assert.Equal(next.LocalCommit.Spec, state!.Commitments.LocalCommit.Spec);
    }

    [Fact]
    public async Task Given_AnUnackedCommitmentOnThePendingSplice_When_Applied_Then_ItsSlotHoldsTheSentSignatures()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        var commit = harness.Driver.TryUsCommit()!;
        var next = commit.Next;
        var splice = harness.Splice(0x96);
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, splice));
        var sent = CommitmentDanceDriver.Signatures(0x41, 1);
        var withSplice = ChannelCommitments.Restore(
            next.ChannelId, next.Params, next.LocalBalanceMsat, next.RemoteBalanceMsat, next.Htlcs.Values,
            next.FeeUpdates, next.LocalNextHtlcId, next.RemoteNextHtlcId,
            next.LocalCommit with
            {
                PendingFundingSignatures = [new FundingSignatures(splice.FundingTxId,
                                                                  CommitmentDanceDriver.Signatures(0x42, 0))]
            },
            next.RemoteCommit,
            next.RemoteNextCommit! with { PendingFundingSignatures = [new FundingSignatures(splice.FundingTxId, sent)] },
            next.RemoteNextPerCommitmentPoint, [splice]);

        // Act
        await harness.SaveAsync(uow => uow.ChannelStateDbRepository.ApplyAsync(withSplice, commit.Transition with
        {
            RemoteCommitChanged = true
        }));

        // Assert
        using var reader = harness.CreateUnitOfWork();
        var unacked = await reader.ChannelFundingDbRepository.GetRemoteNextCommitmentAsync(harness.ChannelId,
                                                                                            splice.FundingTxId);
        Assert.NotNull(unacked);
        Assert.Equal(next.RemoteNextCommit!.Commit.Number, unacked.Commit.Number);
        Assert.Equal(ChannelCommitments.SpecFor(next.RemoteNextCommit.Commit.Spec, splice), unacked.Commit.Spec);
        Assert.Equal(sent.Signature, unacked.SentSignatures.Signature);
        Assert.Equal(sent.HtlcSignatures, unacked.SentSignatures.HtlcSignatures);
    }

    private static TxId TxIdOf(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    /// <summary>A channel with its commitment snapshot on SQLite (the <c>RevokedCommitmentLogTests</c> setup).</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public SqliteDbTestContext Db { get; }
        public Domain.Channels.Models.ChannelModel Channel { get; }
        public ChannelId ChannelId => Channel.ChannelId;
        public CommitmentDanceDriver Driver { get; }
        public TxId CurrentFundingTxId => Channel.FundingOutput!.TransactionId!.Value;

        private Harness(SqliteDbTestContext db, Domain.Channels.Models.ChannelModel channel,
                        CommitmentDanceDriver driver)
        {
            Db = db;
            Channel = channel;
            Driver = driver;
        }

        public static async Task<Harness> CreateAsync()
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

            return new Harness(db, channel, driver);
        }

        /// <summary>
        /// Persists add, commit, revoke, add, commit and returns (unsaved) the <c>revoke_and_ack</c> that revokes the
        /// peer's commitment 1, which carries an HTLC.
        /// </summary>
        public async Task<CommitmentsResult> RevokeCommitmentWithAnHtlcAsync()
        {
            await PersistAsync(Driver.TryUsAdd(5_000_000)!);
            await PersistAsync(Driver.TryUsCommit()!);
            await PersistAsync(Driver.TryDeliverRevokeToUs()!);
            await PersistAsync(Driver.TryUsAdd(6_000_000)!);
            await PersistAsync(Driver.TryUsCommit()!);
            var raa = Driver.TryDeliverRevokeToUs()!;
            Assert.NotEmpty(raa.Transition.RevokedRemoteCommit!.Spec.Htlcs);
            return raa;
        }

        /// <summary>A pending splice adding 250,000 sat to our side (key index 1).</summary>
        public ChannelFunding Splice(byte seed) =>
            new(TxIdOf(seed), 1, 1_250_000, s_spliceLocalKey, s_spliceRemoteKey, 1, 250_000_000, 0,
                ChannelFundingKind.Splice, ChannelFundingStatus.Pending, 2_000, 812_000);

        public UnitOfWork CreateUnitOfWork() =>
            new(Db.CreateDbContext(), NullLogger<UnitOfWork>.Instance, Db.Sha256, new UtxoMemoryRepository());

        public async Task SaveAsync(Func<UnitOfWork, Task> stage)
        {
            using var uow = CreateUnitOfWork();
            await stage(uow);
            await uow.SaveChangesAsync();
        }

        public Task PersistAsync(CommitmentsResult result) =>
            SaveAsync(uow => uow.ChannelStateDbRepository.ApplyAsync(result.Next, result.Transition));

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}