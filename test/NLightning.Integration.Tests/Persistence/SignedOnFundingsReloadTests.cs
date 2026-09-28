using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Reestablish;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Infrastructure.Persistence.Entities.Channel;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-494 on SQLite with the real migrations: <see cref="RemoteCommit.SignedOnFundings"/> (every funding a remote
/// commitment was signed on while splices were pending) is stored with the state machine's remote slots (migration
/// <c>AddSpliceHardening</c>) and restored by <see cref="ChannelStateDbRepository.LoadAsync"/>, so the
/// <c>revoke_and_ack</c> that arrives after a restart still logs the revocation on a funding discarded in between, and
/// the restarted node signs the next state instead of any state it already signed.
/// </summary>
public class SignedOnFundingsReloadTests
{
    private static readonly CompactPubKey s_spliceLocalKey =
        Convert.FromHexString("0394854aa6eab5b2a8122cc726e9dded053a2184d88256816826d6231c068d4a5b");

    private static readonly CompactPubKey s_spliceRemoteKey =
        Convert.FromHexString("02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27");

    [Fact]
    public async Task Given_ACommitmentSignedOnASpliceDiscardedBeforeItsRevocation_When_TheNodeRestarts_Then_ItsRevocationIsLoggedOnTheSplice()
    {
        // Arrange: commitment 1 carries an HTLC and is signed on the current funding and on a pending splice
        await using var harness = await Harness.CreateAsync();
        var splice = harness.Splice(0x97);
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, splice));
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        var commit = harness.Driver.TryUsCommit()!;
        var signedOn = harness.WithRemoteNextSignedOn(commit.Next, [harness.CurrentFunding, splice]);
        await harness.SaveAsync(uow => uow.ChannelStateDbRepository.ApplyAsync(signedOn, commit.Transition));

        // The splice is discarded before the peer revokes (an aborted negotiation, SP-I5)
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(
                                    harness.ChannelId, splice with { Status = ChannelFundingStatus.Discarded }));

        // Restart 1: the unacked commitment comes back with its fundings and the signatures we sent (retransmitted
        // verbatim by the reestablish, never signed again)
        var reloaded = await harness.ReloadAsync();
        var unacked = reloaded.RemoteNextCommit!;
        Assert.Equal([harness.CurrentFundingTxId, splice.FundingTxId],
                     unacked.Commit.GetSignedOnFundingTxIds()!);
        Assert.Equal(splice.LocalBalanceDeltaMsat, unacked.Commit.SignedOnFundings![1].LocalBalanceDeltaMsat);
        Assert.Equal(commit.Next.RemoteNextCommit!.SentSignatures.Signature, unacked.SentSignatures.Signature);
        harness.Driver.ReplaceUs(reloaded);
        await harness.PersistAsync(harness.Driver.TryDeliverRevokeToUs()!);

        // Restart 2: commitment 1 is now the peer's current one, still with its fundings
        reloaded = await harness.ReloadAsync();
        Assert.Equal(1UL, reloaded.RemoteCommit.Number);
        Assert.Equal([harness.CurrentFundingTxId, splice.FundingTxId], reloaded.RemoteCommit.GetSignedOnFundingTxIds()!);
        harness.Driver.ReplaceUs(reloaded);

        // Act: the next state, then the peer's revoke_and_ack of commitment 1
        await harness.PersistAsync(harness.Driver.TryUsAdd(6_000_000)!);
        var next = harness.Driver.TryUsCommit()!;
        await harness.PersistAsync(next);
        var raa = harness.Driver.TryDeliverRevokeToUs()!;
        await harness.PersistAsync(raa);

        // Assert: the restarted node signed commitment 2 on the current funding only (never commitment 1 again, and
        // nothing on the discarded splice) ...
        var signed = Assert.Single(next.Outbound.OfType<OutboundCommitmentSigned>());
        Assert.Equal(2UL, signed.RemoteCommitmentNumber);
        Assert.Empty(next.Next.RemoteNextCommit!.PendingFundingSignatures);

        // ... and the revocation of commitment 1 is logged on both fundings it was signed on
        Assert.Equal(splice.FundingTxId, Assert.Single(raa.Transition.RevokedRemoteCommitFundings!).FundingTxId);
        using var reader = harness.CreateUnitOfWork();
        var log = reader.RevokedCommitmentDbRepository;
        var onCurrent = Assert.Single(await log.GetByFundingAsync(harness.ChannelId, harness.CurrentFundingTxId));
        var onSplice = Assert.Single(await log.GetByFundingAsync(harness.ChannelId, splice.FundingTxId));
        Assert.Equal(1UL, onCurrent.Number);
        Assert.Equal(1UL, onSplice.Number);
        Assert.Equal(ChannelCommitments.SpecFor(onCurrent.Spec, splice), onSplice.Spec);
        Assert.NotEmpty(onSplice.Spec.Htlcs);
    }

    [Fact]
    public async Task Given_ACommitmentSignedByTheEngineOnAPendingSplice_When_TheNodeRestarts_Then_ItsFundingsAndSignaturesSurviveAndNothingIsSignedAgain()
    {
        // Arrange: the splice made pending by the engine (the peer's splice commitment_signed), then commitment 1
        // (with an HTLC) signed by the engine on the current funding and on the splice (SP-OP-03)
        await using var harness = await Harness.CreateAsync();
        var splice = harness.Splice(0x9A);
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, splice));
        await harness.PersistAsync(harness.Driver.UsReceiveSplice(splice));
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        var commit = harness.Driver.TryUsCommit()!;
        await harness.PersistAsync(commit);
        var sent = commit.Next.RemoteNextCommit!;
        Assert.Equal([harness.CurrentFundingTxId, splice.FundingTxId], sent.Commit.GetSignedOnFundingTxIds()!);
        var sentOnSplice = Assert.Single(sent.PendingFundingSignatures);
        Assert.Equal(splice.FundingTxId, sentOnSplice.FundingTxId);

        // Act: restart while the splice is still pending
        var reloaded = await harness.ReloadAsync();

        // Assert: the pending funding, the fundings commitment 1 was signed on (with the engine's deltas) and the
        // signatures we sent on each funding come back byte-exact
        Assert.Equal(splice.FundingTxId, Assert.Single(reloaded.PendingFundings).FundingTxId);
        var unacked = reloaded.RemoteNextCommit!;
        Assert.Equal(sent.Commit.Number, unacked.Commit.Number);
        Assert.Equal(sent.Commit.GetSignedOnFundingTxIds()!, unacked.Commit.GetSignedOnFundingTxIds()!);
        Assert.Equal(sent.Commit.SignedOnFundings!.Select(f => (f.LocalBalanceDeltaMsat, f.RemoteBalanceDeltaMsat)),
                     unacked.Commit.SignedOnFundings!.Select(f => (f.LocalBalanceDeltaMsat, f.RemoteBalanceDeltaMsat)));
        AssertSameSignatures(sent.SentSignatures, unacked.SentSignatures);
        var reloadedOnSplice = Assert.Single(unacked.PendingFundingSignatures);
        Assert.Equal(splice.FundingTxId, reloadedOnSplice.FundingTxId);
        AssertSameSignatures(sentOnSplice.Signatures, reloadedOnSplice.Signatures);

        // Commitment 1 is never signed again: the engine waits for its revoke_and_ack, and a peer that did not get it
        // is sent the stored diff verbatim (B2-RE-18), never a new signature
        var afterRevert = reloaded.RevertUncommitted().Next;
        Assert.False(afterRevert.CanSendCommit);
        var plan = ReestablishPlanner.Plan(
            ReestablishLocalState.From(afterRevert, true, LastSentCommitmentMessage.CommitmentSigned),
            new PeerReestablish(1, 0, new byte[32]), (_, _) => false);
        Assert.Equal(ReestablishOutcome.Resume, plan.Outcome);
        Assert.Contains(ReestablishStep.CommitDiff, plan.Steps);

        // The peer revokes commitment 0; after a second restart commitment 1 is its current one, still with its
        // fundings, and the revocation of commitment 1 (after commitment 2) is logged on both
        harness.Driver.ReplaceUs(reloaded);
        await harness.PersistAsync(harness.Driver.TryDeliverRevokeToUs()!);
        reloaded = await harness.ReloadAsync();
        Assert.Equal(1UL, reloaded.RemoteCommit.Number);
        Assert.Equal([harness.CurrentFundingTxId, splice.FundingTxId], reloaded.RemoteCommit.GetSignedOnFundingTxIds()!);
        harness.Driver.ReplaceUs(reloaded);
        await harness.PersistAsync(harness.Driver.TryUsAdd(6_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);
        var raa = harness.Driver.TryDeliverRevokeToUs()!;
        await harness.PersistAsync(raa);
        Assert.Equal(splice.FundingTxId, Assert.Single(raa.Transition.RevokedRemoteCommitFundings!).FundingTxId);
        using var reader = harness.CreateUnitOfWork();
        var onSplice = Assert.Single(await reader.RevokedCommitmentDbRepository.GetByFundingAsync(harness.ChannelId,
                                                                                               splice.FundingTxId));
        Assert.Equal(1UL, onSplice.Number);
    }

    [Fact]
    public async Task Given_ASpliceDiscardedByTheEngineBeforeTheRevocation_When_TheNodeRestarts_Then_ItsRevocationIsStillLoggedOnTheSplice()
    {
        // Arrange: commitment 1 (with an HTLC) signed by the engine on the current funding and a pending splice, and
        // the peer's revocation of commitment 0 ...
        await using var harness = await Harness.CreateAsync();
        var splice = harness.Splice(0x9B);
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, splice));
        await harness.PersistAsync(harness.Driver.UsReceiveSplice(splice));
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);
        await harness.PersistAsync(harness.Driver.TryDeliverRevokeToUs()!);

        // ... then the engine discards the splice (tx_abort), saved as the splice state port saves it
        var discard = harness.Driver.UsDiscard(splice.FundingTxId);
        await harness.SaveAsync(async uow =>
        {
            await uow.ChannelStateDbRepository.ApplyAsync(discard.Next, discard.Transition);
            await uow.ChannelFundingDbRepository.UpsertAsync(
                harness.ChannelId, splice with { Status = ChannelFundingStatus.Discarded });
        });

        // Act: restart, then commitment 2 and the peer's revocation of commitment 1
        var reloaded = await harness.ReloadAsync();
        harness.Driver.ReplaceUs(reloaded);
        await harness.PersistAsync(harness.Driver.TryUsAdd(6_000_000)!);
        var next = harness.Driver.TryUsCommit()!;
        await harness.PersistAsync(next);
        var raa = harness.Driver.TryDeliverRevokeToUs()!;
        await harness.PersistAsync(raa);

        // Assert: no pending funding any more (commitment 2 is signed on the current funding only), but the record
        // of what commitment 1 was signed on survived, so its revocation is logged on the discarded splice
        Assert.Empty(reloaded.PendingFundings);
        Assert.Equal([harness.CurrentFundingTxId, splice.FundingTxId], reloaded.RemoteCommit.GetSignedOnFundingTxIds()!);
        Assert.Single(next.Outbound.OfType<OutboundCommitmentSigned>());
        Assert.Equal(splice.FundingTxId, Assert.Single(raa.Transition.RevokedRemoteCommitFundings!).FundingTxId);
        using var reader = harness.CreateUnitOfWork();
        var onSplice = Assert.Single(await reader.RevokedCommitmentDbRepository.GetByFundingAsync(harness.ChannelId,
                                                                                               splice.FundingTxId));
        Assert.Equal(1UL, onSplice.Number);
        Assert.Equal(ChannelCommitments.SpecFor(reloaded.RemoteCommit.Spec, splice), onSplice.Spec);
    }

    [Fact]
    public async Task Given_AnUnreadableSignedOnFundingsBlob_When_Reloaded_Then_TheChannelStillLoads()
    {
        // Arrange: a blob whose length is not a multiple of an entry's
        await using var harness = await Harness.CreateAsync();
        var splice = harness.Splice(0x9C);
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, splice));
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        var commit = harness.Driver.TryUsCommit()!;
        await harness.SaveAsync(uow => uow.ChannelStateDbRepository.ApplyAsync(
                                    harness.WithRemoteNextSignedOn(commit.Next, [harness.CurrentFunding, splice]),
                                    commit.Transition));
        await using (var context = harness.Db.CreateDbContext())
        {
            var row = await context.Commitments.SingleAsync(c => c.Slot == CommitmentEntity.RemoteNextSlot,
                                                            TestContext.Current.CancellationToken);
            row.SignedOnFundings = row.SignedOnFundings![..47];
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        var reloaded = await harness.ReloadAsync();

        // Assert: read as no record (the engine falls back to the pending fundings), the state is intact
        Assert.Null(reloaded.RemoteNextCommit!.Commit.SignedOnFundings);
        Assert.Equal(commit.Next.RemoteNextCommit!.Commit.Number, reloaded.RemoteNextCommit.Commit.Number);
    }

    [Fact]
    public async Task Given_RebasedDeltas_When_Reloaded_Then_TheStoredDeltasWinOverTheFundingRows()
    {
        // Arrange: after a lock the engine rebases every entry on the locked funding, while the rows keep the deltas
        // they were written with (here: the splice row +250,000 sat, the entry 0 and the other entry -250,000 sat)
        await using var harness = await Harness.CreateAsync();
        var splice = harness.Splice(0x98);
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, splice));
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        var commit = harness.Driver.TryUsCommit()!;
        var rebased = new List<ChannelFunding>
        {
            harness.CurrentFunding with { LocalBalanceDeltaMsat = -250_000_000 },
            splice with { LocalBalanceDeltaMsat = 0 }
        };

        // Act
        await harness.SaveAsync(uow => uow.ChannelStateDbRepository.ApplyAsync(
                                    harness.WithRemoteNextSignedOn(commit.Next, rebased), commit.Transition));
        var reloaded = await harness.ReloadAsync();

        // Assert
        var restored = reloaded.RemoteNextCommit!.Commit.SignedOnFundings!;
        Assert.Equal(2, restored.Count);
        Assert.Equal(-250_000_000, restored[0].LocalBalanceDeltaMsat);
        Assert.Equal(0, restored[1].LocalBalanceDeltaMsat);
        Assert.Equal(splice.LocalFundingPubKey, restored[1].LocalFundingPubKey);
    }

    [Fact]
    public async Task Given_ACommitmentSignedOnTheCurrentFundingOnly_When_Reloaded_Then_SignedOnFundingsStaysNull()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        await harness.PersistAsync(harness.Driver.TryUsCommit()!);

        // Act
        var reloaded = await harness.ReloadAsync();

        // Assert: no column value, and the engine's fallback (the pending fundings) as before NL-494
        Assert.Null(reloaded.RemoteNextCommit!.Commit.SignedOnFundings);
        Assert.Null(reloaded.RemoteCommit.SignedOnFundings);
        await using var context = harness.Db.CreateDbContext();
        Assert.All(await context.Commitments.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken),
                   c => Assert.Null(c.SignedOnFundings));
    }

    [Fact]
    public async Task Given_AStoredFundingTxIdWithoutAFundingRow_When_Reloaded_Then_TheChannelStillLoads()
    {
        // Arrange: a signed-on list naming a funding the channel has no row for (a crash between two saves)
        await using var harness = await Harness.CreateAsync();
        var splice = harness.Splice(0x99);
        await harness.SaveAsync(uow => uow.ChannelFundingDbRepository.UpsertAsync(harness.ChannelId, splice));
        await harness.PersistAsync(harness.Driver.TryUsAdd(5_000_000)!);
        var commit = harness.Driver.TryUsCommit()!;
        await harness.SaveAsync(uow => uow.ChannelStateDbRepository.ApplyAsync(
                                    harness.WithRemoteNextSignedOn(commit.Next, [harness.CurrentFunding, splice]),
                                    commit.Transition));
        await using (var context = harness.Db.CreateDbContext())
        {
            var row = await context.Commitments.SingleAsync(c => c.Slot == CommitmentEntity.RemoteNextSlot,
                                                            TestContext.Current.CancellationToken);
            var blob = row.SignedOnFundings!.ToArray();
            blob.AsSpan(48, 32).Fill(0xEE);
            row.SignedOnFundings = blob;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        var reloaded = await harness.ReloadAsync();

        // Assert: the entry that resolves is kept and the one without a row dropped (NL-494 review), the state is
        // intact
        Assert.Equal([harness.CurrentFundingTxId], reloaded.RemoteNextCommit!.Commit.GetSignedOnFundingTxIds()!);
        Assert.Equal(commit.Next.RemoteNextCommit!.Commit.Number, reloaded.RemoteNextCommit.Commit.Number);
    }

    [Fact]
    public void Given_StoredTxIds_When_WithSignedOnFundings_Then_TheFundingsAreResolvedInOrder()
    {
        // Arrange
        var a = SpliceOf(0x01, ChannelFundingStatus.Replaced);
        var b = SpliceOf(0x02, ChannelFundingStatus.Discarded);
        var commit = new RemoteCommit(3, new CommitmentSpec(CommitmentSide.Remote, 253, 1_000, 2_000, []),
                                      CommitmentDanceDriver.Point(0x01, 3));

        // Act
        var restored = commit.WithSignedOnFundings([b.FundingTxId, a.FundingTxId], [a, b]);
        var none = commit.WithSignedOnFundings(null, [a, b]);

        // Assert
        Assert.Equal([b, a], restored.SignedOnFundings!);
        Assert.Null(none.SignedOnFundings);
        Assert.Throws<InvalidOperationException>(() => commit.WithSignedOnFundings([TxIdOf(0x03)], [a, b]));
    }

    private static void AssertSameSignatures(CommitmentSignatures expected, CommitmentSignatures actual)
    {
        Assert.Equal((byte[])expected.Signature, (byte[])actual.Signature);
        Assert.Equal(expected.HtlcSignatures.Select(h => (byte[])h), actual.HtlcSignatures.Select(h => (byte[])h));
    }

    private static ChannelFunding SpliceOf(byte seed, ChannelFundingStatus status) =>
        new(TxIdOf(seed), 0, 1_000_000, s_spliceLocalKey, s_spliceRemoteKey, seed, 0, 0, ChannelFundingKind.Splice,
            status);

    private static TxId TxIdOf(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    /// <summary>A channel with its commitment snapshot on SQLite (the <c>RevokedCommitmentPerFundingTests</c> setup).</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public SqliteDbTestContext Db { get; }
        public Domain.Channels.Models.ChannelModel Channel { get; }
        public ChannelId ChannelId => Channel.ChannelId;
        public CommitmentDanceDriver Driver { get; }
        public TxId CurrentFundingTxId => Channel.FundingOutput!.TransactionId!.Value;
        public ChannelFunding CurrentFunding => ChannelFunding.FromFundingOutput(Channel.FundingOutput!)!;

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

        /// <summary>A pending splice adding 250,000 sat to our side (key index 1).</summary>
        public ChannelFunding Splice(byte seed) =>
            new(TxIdOf(seed), 1, 1_250_000, s_spliceLocalKey, s_spliceRemoteKey, 1, 250_000_000, 0,
                ChannelFundingKind.Splice, ChannelFundingStatus.Pending, 2_000, 812_000);

        /// <summary>
        /// <paramref name="next"/> with its unacked remote commitment recorded as signed on <paramref name="fundings"/>,
        /// as the engine records it when splices are pending at signing (SP-OP-03).
        /// </summary>
        public ChannelCommitments WithRemoteNextSignedOn(ChannelCommitments next,
                                                                IReadOnlyList<ChannelFunding> fundings)
        {
            var unacked = next.RemoteNextCommit!;
            return ChannelCommitments.Restore(
                next.ChannelId, next.Params, next.LocalBalanceMsat, next.RemoteBalanceMsat, next.Htlcs.Values,
                next.FeeUpdates, next.LocalNextHtlcId, next.RemoteNextHtlcId, next.LocalCommit, next.RemoteCommit,
                unacked with { Commit = unacked.Commit with { SignedOnFundings = fundings } },
                next.RemoteNextPerCommitmentPoint);
        }

        /// <summary>The engine snapshot as a restarted node loads it, from a fresh context.</summary>
        public async Task<ChannelCommitments> ReloadAsync()
        {
            await using var context = Db.CreateDbContext();
            var state = await new ChannelStateDbRepository(context).LoadAsync(ChannelId, Driver.Us.Params);
            return state!.Commitments;
        }

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