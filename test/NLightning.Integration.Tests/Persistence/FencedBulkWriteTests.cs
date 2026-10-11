using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Channels.ValueObjects;
using Domain.Node.Fencing;
using Domain.Protocol.Onion.Models;
using Domain.Signing;
using Infrastructure.Persistence.Entities.Node;
using Infrastructure.Repositories.Database.Accounting;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Database.Helpers;
using Infrastructure.Repositories.Database.Node;
using Infrastructure.Repositories.Database.Payment;

/// <summary>
/// NL-1342: the database writes that bypass <c>UnitOfWork.SaveChanges</c> (the accounting books' clear, reset and
/// rollback, the lots' clear, the onion replay prune, the durable signer guard's raise and the first signing enrollment)
/// run in one transaction the node write fence checks before the commit. A refusal leaves nothing of the write in the
/// database, while the fence saw the write inside that transaction; a fence that allows it commits it.
/// </summary>
public class FencedBulkWriteTests
{
    private static readonly DateTimeOffset s_sep = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_oct = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private const string Period = "2026-09";

    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0xC7, 32).ToArray());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AFence_When_TheOperationalBookIsCleared_Then_ItsVerdictDecidesTheCommit(bool refuse)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(cancellationToken);
        await using (var context = db.CreateDbContext())
        {
            var books = new AccountingBooksDbRepository(context);
            await books.AddEntryAsync(Entry(1, 0, s_sep, AccountingBook.Operational), cancellationToken);
            await books.SetCursorAsync(1, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        var seen = -1L;
        var fence = new FakeNodeWriteFence
        {
            Refuse = refuse,
            OnSave = (connection, transaction) => seen = Count(connection, transaction, "AccountingEntries")
        };

        // Act
        await using (var context = db.CreateDbContext())
        {
            var clear = new AccountingBooksDbRepository(context, fence).ClearAsync(cancellationToken);
            if (refuse)
                await Assert.ThrowsAsync<NodeFencedException>(() => clear);
            else
                await clear;
        }

        // Assert: the fence saw the deletes in the write's transaction
        Assert.Equal(1, fence.SaveChecks);
        Assert.Equal(0, seen);
        await using var read = db.CreateDbContext();
        Assert.Equal(refuse ? 1 : 0, await read.AccountingEntries.CountAsync(cancellationToken));
        Assert.Equal(refuse ? 1 : 0, await read.AccountingCursor.CountAsync(cancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AFence_When_TheFinancialBookIsResetToItsClose_Then_ItsVerdictDecidesTheCommit(bool refuse)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(cancellationToken);
        await SeedFinancialBookAsync(db, cancellationToken);
        var fence = new FakeNodeWriteFence { Refuse = refuse };
        var closing = new AccountingAccountBalance(AccountingBook.Financial, AccountRole.Channels, "fin:channels",
                                                   1_000, 0m);

        // Act
        await using (var context = db.CreateDbContext())
        {
            var reset = new AccountingBooksDbRepository(context, fence)
               .ResetToCloseAsync(AccountingBook.Financial, new AccountingBookReset(1, [closing]), cancellationToken);
            if (refuse)
                await Assert.ThrowsAsync<NodeFencedException>(() => reset);
            else
                await reset;
        }

        // Assert
        Assert.Equal(1, fence.SaveChecks);
        await AssertFinancialBookAsync(db, refuse, cancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AFence_When_TheOpenEntriesAreRolledBack_Then_ItsVerdictDecidesTheCommit(bool refuse)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(cancellationToken);
        await SeedFinancialBookAsync(db, cancellationToken);
        var fence = new FakeNodeWriteFence { Refuse = refuse };

        // Act
        await using (var context = db.CreateDbContext())
        {
            var rollback = new AccountingBooksDbRepository(context, fence)
               .RollbackOpenEntriesAsync(AccountingBook.Financial, 2, cancellationToken);
            if (refuse)
                await Assert.ThrowsAsync<NodeFencedException>(() => rollback);
            else
                await rollback;
        }

        // Assert
        Assert.Equal(1, fence.SaveChecks);
        await AssertFinancialBookAsync(db, refuse, cancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AFence_When_TheLotsAreCleared_Then_ItsVerdictDecidesTheCommit(bool refuse)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(cancellationToken);
        await SeedFinancialBookAsync(db, cancellationToken);
        var fence = new FakeNodeWriteFence { Refuse = refuse };

        // Act
        await using (var context = db.CreateDbContext())
        {
            var clear = new AccountingLotDbRepository(context, fence).ClearAsync(cancellationToken);
            if (refuse)
                await Assert.ThrowsAsync<NodeFencedException>(() => clear);
            else
                await clear;
        }

        // Assert
        Assert.Equal(1, fence.SaveChecks);
        await using var read = db.CreateDbContext();
        Assert.Equal(refuse ? 2 : 0, await read.AccountingLots.CountAsync(cancellationToken));
        Assert.Equal(refuse ? 1 : 0, await read.AccountingLotReliefs.CountAsync(cancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AFence_When_TheOnionReplaySetIsPruned_Then_ItsVerdictDecidesTheCommit(bool refuse)
    {
        // Arrange: two entries, one expired below height 200
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(cancellationToken);
        await using (var context = db.CreateDbContext())
        {
            var replay = new OnionReplayDbRepository(context);
            replay.Add(new OnionReplayEntry(Enumerable.Repeat((byte)0x01, 32).ToArray(), s_channelId, 0, 150));
            replay.Add(new OnionReplayEntry(Enumerable.Repeat((byte)0x02, 32).ToArray(), s_channelId, 1, 250));
            await context.SaveChangesAsync(cancellationToken);
        }

        var seen = -1L;
        var fence = new FakeNodeWriteFence
        {
            Refuse = refuse,
            OnSave = (connection, transaction) => seen = Count(connection, transaction, "OnionReplayEntries")
        };

        // Act
        await using (var context = db.CreateDbContext())
        {
            var prune = new OnionReplayDbRepository(context, fence).DeleteExpiredAsync(200);
            if (refuse)
                await Assert.ThrowsAsync<NodeFencedException>(() => prune);
            else
                Assert.Equal(1, await prune);
        }

        // Assert
        Assert.Equal(1, fence.SaveChecks);
        Assert.Equal(1, seen);
        await using var read = db.CreateDbContext();
        Assert.Equal(refuse ? 2 : 1, await read.OnionReplayEntries.CountAsync(cancellationToken));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Given_AFence_When_TheSignerGuardIsRaised_Then_ItsVerdictDecidesTheCommit(bool refuse,
                                                                                               bool rowExists)
    {
        // Arrange: either no row yet (the insert path) or a lower row (the conditional update path)
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(cancellationToken);
        var before = new ChannelSignerGuard(3, 2, 4);
        if (rowExists)
            await using (var context = db.CreateDbContext())
                await new ChannelSignerGuardDbRepository(context).RaiseAsync(s_channelId, before);

        var seen = -1L;
        var fence = new FakeNodeWriteFence
        {
            Refuse = refuse,
            OnSave = (connection, transaction) => seen = Count(connection, transaction, "ChannelSignerGuards")
        };
        var raised = new ChannelSignerGuard(9, 8, 10, 7, true);

        // Act
        await using (var context = db.CreateDbContext())
        {
            var raise = new ChannelSignerGuardDbRepository(context, fence).RaiseAsync(s_channelId, raised);
            if (refuse)
                await Assert.ThrowsAsync<NodeFencedException>(() => raise);
            else
                await raise;
        }

        // Assert: the fence saw the row in the raise's transaction
        Assert.Equal(1, fence.SaveChecks);
        Assert.Equal(1, seen);
        await using var read = db.CreateDbContext();
        var stored = await new ChannelSignerGuardDbRepository(read).GetAsync(s_channelId);
        Assert.Equal(refuse ? rowExists ? before : null : raised, stored);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AFence_When_AFreshDatabaseIsEnrolled_Then_ItsVerdictDecidesTheCommit(bool refuse)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(cancellationToken);
        var seen = -1L;
        var fence = new FakeNodeWriteFence
        {
            Refuse = refuse,
            OnSave = (connection, transaction) => seen = Count(connection, transaction, "NodeSigningEnrollments")
        };

        // Act
        await using (var context = db.CreateDbContext())
        {
            var validate = new NodeSigningEnrollmentStore(context, fence).ValidateAsync(SigningContext(),
                                                                                        cancellationToken);
            if (refuse)
                await Assert.ThrowsAsync<NodeFencedException>(() => validate);
            else
                await validate;
        }

        // Assert
        Assert.Equal(1, fence.SaveChecks);
        Assert.Equal(1, seen);
        await using var read = db.CreateDbContext();
        Assert.Equal(refuse ? 0 : 1,
                     await read.Set<NodeSigningEnrollmentEntity>().CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Given_AnEnrolledDatabase_When_ARefusingFenceValidatesIt_Then_NothingIsWrittenOrChecked()
    {
        // Arrange: a standby validates a database that is already enrolled to its context
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(cancellationToken);
        await using (var context = db.CreateDbContext())
            await new NodeSigningEnrollmentStore(context).ValidateAsync(SigningContext(), cancellationToken);
        var fence = new FakeNodeWriteFence { Refuse = true };

        // Act
        await using (var context = db.CreateDbContext())
            await new NodeSigningEnrollmentStore(context, fence).ValidateAsync(SigningContext(), cancellationToken);

        // Assert
        Assert.Equal(0, fence.SaveChecks);
    }

    [Fact]
    public async Task Given_NoFence_When_AWriteThatNeedsNoTransactionRuns_Then_NoTransactionIsOpened()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = db.CreateDbContext();
        IDbContextTransaction? during = null;

        // Act
        var result = await FencedWrite.RunAsync(context, null, () =>
        {
            during = context.Database.CurrentTransaction;
            return Task.FromResult(42);
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(42, result);
        Assert.Null(during);
    }

    [Fact]
    public async Task Given_ACallerTransaction_When_AFencedWriteRuns_Then_TheFenceChecksItAndTheCallerDecidesTheCommit()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(cancellationToken);
        await using (var context = db.CreateDbContext())
        {
            new OnionReplayDbRepository(context)
               .Add(new OnionReplayEntry(Enumerable.Repeat((byte)0x03, 32).ToArray(), s_channelId, 0, 100));
            await context.SaveChangesAsync(cancellationToken);
        }

        DbTransaction? checkedTransaction = null;
        var fence = new FakeNodeWriteFence { OnSave = (_, transaction) => checkedTransaction = transaction };
        await using var caller = db.CreateDbContext();
        await using var callerTransaction = await caller.Database.BeginTransactionAsync(cancellationToken);

        // Act: the prune runs in the caller's transaction, which then rolls back
        Assert.Equal(1, await new OnionReplayDbRepository(caller, fence).DeleteExpiredAsync(200));
        Assert.Same(callerTransaction.GetDbTransaction(), checkedTransaction);
        await callerTransaction.RollbackAsync(cancellationToken);

        // Assert
        await using var read = db.CreateDbContext();
        Assert.Equal(1, await read.OnionReplayEntries.CountAsync(cancellationToken));
    }

    /// <summary>
    /// Financial book: 1/0 (September, closed) and 2/0 (October), cursor 2; lot 1 by 1/0 (closed) and lot 2 by 2/0,
    /// which relieves 100 msat of lot 1. Both the reset to cursor 1 and the rollback from 2 remove 2/0, lot 2 and the relief.
    /// </summary>
    private static async Task SeedFinancialBookAsync(SqliteDbTestContext db, CancellationToken cancellationToken)
    {
        await using var context = db.CreateDbContext();
        var books = new AccountingBooksDbRepository(context);
        var lots = new AccountingLotDbRepository(context);
        await books.AddEntryAsync(Entry(1, 0, s_sep) with { ClosedPeriodId = Period }, cancellationToken);
        await books.AddEntryAsync(Entry(2, 0, s_oct), cancellationToken);
        await books.SetCursorAsync(AccountingBook.Financial, 2, cancellationToken);
        await lots.AddLotAsync(Lot(1, s_sep, 1, 1_000, 900, Period), cancellationToken);
        await lots.AddLotAsync(Lot(2, s_oct, 2, 1_000, 1_000, null), cancellationToken);
        lots.AddRelief(new AccountingLotRelief(0, 1, 2, 0, s_oct, 100, null, null, null));
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task AssertFinancialBookAsync(SqliteDbTestContext db, bool unchanged,
                                                       CancellationToken cancellationToken)
    {
        await using var read = db.CreateDbContext();
        var books = new AccountingBooksDbRepository(read);
        var entries = await books.ListEntriesAsync(new AccountingEntryQuery(0, 100)
        {
            Book = AccountingBook.Financial,
            AfterAdjustment = -1
        }, cancellationToken);
        Assert.Equal(unchanged ? [1L, 2L] : [1L], entries.Select(e => e.LedgerSeq));
        Assert.Equal(unchanged ? 2 : 1, await books.GetCursorAsync(AccountingBook.Financial, cancellationToken));
        Assert.Equal(unchanged ? 2 : 1, await read.AccountingLots.CountAsync(cancellationToken));
        Assert.Equal(unchanged ? 1 : 0, await read.AccountingLotReliefs.CountAsync(cancellationToken));
        Assert.Equal(unchanged ? 900 : 1_000,
                     (await read.AccountingLots.AsNoTracking().SingleAsync(l => l.Id == 1, cancellationToken))
                    .RemainingMsat);
    }

    private static NodeSigningContext SigningContext() =>
        new("node-a", "owner-a", "signer-a", "regtest", SqliteDbTestContext.RemoteNodeId);

    private static AccountingEntry Entry(long ledgerSeq, int adjustment, DateTimeOffset at,
                                         AccountingBook book = AccountingBook.Financial) =>
        new(ledgerSeq, $"k{ledgerSeq}", AccountingEventKind.InvoiceSettled, at, null, null,
            [
                new AccountingPosting(AccountRole.Channels, 1_000)
                {
                    AccountName = book == AccountingBook.Financial ? "fin:channels" : null
                },
                new AccountingPosting(AccountRole.Received, -1_000)
                {
                    AccountName = book == AccountingBook.Financial ? "fin:received" : null
                }
            ])
        {
            Book = book,
            Adjustment = adjustment
        };

    private static AccountingLot Lot(long id, DateTimeOffset at, long source, long original, long remaining,
                                     string? period) =>
        new(id, at, AccountingLotOrigin.Acquisition, source, 0, null, null, original, remaining, null, null, null,
            false, period);

    private static long Count(DbConnection connection, DbTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}