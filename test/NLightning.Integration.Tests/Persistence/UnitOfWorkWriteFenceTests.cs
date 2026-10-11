using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Node.Fencing;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Payment;
using Infrastructure.Repositories.Memory;
using static PaymentSchemaRoundTrip;

/// <summary>
/// NL-1341: with a node write fence, every <see cref="UnitOfWork"/> save runs in one transaction that the fence checks
/// after the writes and before the commit. A refusal commits nothing (invoices, payments, accounting events and LND's
/// indexes alike) and gives the indexes back; a fence that allows it commits the whole save at once.
/// </summary>
public class UnitOfWorkWriteFenceTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_ARefusingFence_When_ASaveRuns_Then_NothingIsCommitted(bool async)
    {
        // Arrange: the fence looks at the save's own transaction and finds the staged rows there
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var allocator = new LndIndexAllocator();
        var seenInTransaction = -1L;
        var fence = new FakeNodeWriteFence
        {
            Refuse = true,
            OnSave = (connection, transaction) => seenInTransaction = Count(connection, transaction, "Invoices")
        };

        // Act
        await using (var context = db.CreateDbContext())
        {
            var unitOfWork = CreateUnitOfWork(context, allocator, fence);
            await StageAsync(unitOfWork, 0x11);
            if (async)
                await Assert.ThrowsAsync<NodeFencedException>(() => unitOfWork.SaveChangesAsync());
            else
                Assert.Throws<NodeFencedException>(() => unitOfWork.SaveChanges());
        }

        // Assert
        Assert.Equal(1, fence.SaveChecks);
        Assert.Equal(1, seenInTransaction);
        await AssertCountsAsync(db, 0, 0, 0);
    }

    [Fact]
    public async Task Given_AFenceThatAllowsIt_When_ASaveRuns_Then_EverythingIsCommittedWithItsIndexes()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var allocator = new LndIndexAllocator();
        var fence = new FakeNodeWriteFence();

        // Act
        await using (var context = db.CreateDbContext())
        {
            var unitOfWork = CreateUnitOfWork(context, allocator, fence);
            await StageAsync(unitOfWork, 0x21);
            await unitOfWork.SaveChangesAsync();
        }

        // Assert
        Assert.Equal(1, fence.SaveChecks);
        await AssertCountsAsync(db, 1, 1, 1);
        await using var read = db.CreateDbContext();
        Assert.Equal(1L, (await read.Invoices.SingleAsync(TestContext.Current.CancellationToken)).AddIndex);
        Assert.Equal(1L, (await read.Payments.SingleAsync(TestContext.Current.CancellationToken)).PaymentIndex);
    }

    [Fact]
    public async Task Given_ARefusedSave_When_TheFenceAllowsTheNextOne_Then_NoIndexIsSkippedAndTheChangesWereKept()
    {
        // Arrange: the first save is refused
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var allocator = new LndIndexAllocator();
        var fence = new FakeNodeWriteFence { Refuse = true };
        await using var context = db.CreateDbContext();
        var unitOfWork = CreateUnitOfWork(context, allocator, fence);
        await StageAsync(unitOfWork, 0x31);
        await Assert.ThrowsAsync<NodeFencedException>(() => unitOfWork.SaveChangesAsync());

        // Act: the same unit of work saves again once the fence allows it (its changes are still pending)
        fence.Refuse = false;
        await unitOfWork.SaveChangesAsync();

        // Assert
        await AssertCountsAsync(db, 1, 1, 1);
        await using var read = db.CreateDbContext();
        Assert.Equal(1L, (await read.Invoices.SingleAsync(TestContext.Current.CancellationToken)).AddIndex);
        Assert.Equal(1L, (await read.Payments.SingleAsync(TestContext.Current.CancellationToken)).PaymentIndex);
    }

    [Fact]
    public async Task Given_ACallerTransaction_When_ASaveRuns_Then_TheFenceSeesItAndTheCallerDecidesTheCommit()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        DbTransaction? checkedTransaction = null;
        var fence = new FakeNodeWriteFence { OnSave = (_, transaction) => checkedTransaction = transaction };
        await using var context = db.CreateDbContext();
        await using var callerTransaction =
            await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var unitOfWork = CreateUnitOfWork(context, new LndIndexAllocator(), fence);
        await StageAsync(unitOfWork, 0x41);

        // Act: the save does not commit the caller's transaction, which then rolls back
        await unitOfWork.SaveChangesAsync();
        Assert.Same(callerTransaction.GetDbTransaction(), checkedTransaction);
        await callerTransaction.RollbackAsync(TestContext.Current.CancellationToken);

        // Assert
        await AssertCountsAsync(db, 0, 0, 0);
    }

    private static UnitOfWork CreateUnitOfWork(NLightningDbContext context, LndIndexAllocator allocator,
                                               INodeWriteFence fence) =>
        new(context, NullLogger<UnitOfWork>.Instance, new Sha256(), new UtxoMemoryRepository(),
            indexAllocator: allocator, writeFence: fence);

    /// <summary>An invoice and a payment (both take an LND index) and an accounting event, in one save.</summary>
    private static async Task StageAsync(UnitOfWork unitOfWork, byte seed)
    {
        await unitOfWork.InvoiceDbRepository.AddAsync(CreateInvoice(seed, null, s_now));
        await unitOfWork.PaymentDbRepository.AddAsync(CreatePayment((byte)(seed + 3), s_now));
        unitOfWork.AccountingEventDbRepository.Add(new AccountingEventModel
        {
            EventKey = $"fence-test:{seed}",
            Kind = AccountingEventKind.PaymentSucceeded,
            OccurredAt = s_now,
            AmountMsat = 1_000,
            Finality = AccountingFinality.Final
        });
    }

    private static async Task AssertCountsAsync(SqliteDbTestContext db, int invoices, int payments,
                                                int accountingEvents)
    {
        await using var read = db.CreateDbContext();
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.Equal(invoices, await read.Invoices.CountAsync(cancellationToken));
        Assert.Equal(payments, await read.Payments.CountAsync(cancellationToken));
        Assert.Equal(accountingEvents, await read.AccountingEvents.CountAsync(cancellationToken));
    }

    private static long Count(DbConnection connection, DbTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}