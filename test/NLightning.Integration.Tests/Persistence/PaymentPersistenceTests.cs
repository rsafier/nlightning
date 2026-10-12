using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Database.Payment;
using Infrastructure.Repositories.Memory;
using static PaymentSchemaRoundTrip;

/// <summary>
/// Invoices, payments, forward circuits and HTLC origins on SQLite with the real migrations (ABCD W1-C: BOLT2 N8,
/// ONION M4-T7, NL-137), plus the dust-policy and inferred-limits reload (NL-242) and the cheap existence check
/// (NL-243).
/// </summary>
public class PaymentPersistenceTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_RowsFromBeforeAddInvoicesPaymentsAndCircuits_When_Migrated_Then_TheyMoveForwardAndTheNewTablesRoundTrip()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert (the same rows and assertions as the Docker Postgres/SQL Server tests)
        await PaymentSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_RowsFromBeforeAddLndIndexes_When_Migrated_Then_TheyGetDenseIndexesInCreationOrder()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        // Act & Assert (the same rows and assertions as the Postgres test, NL-1165)
        await LndIndexSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)), DatabaseType.Sqlite,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_InvoicePaymentAndCircuit_When_EachStateIsSavedAndReloaded_Then_EveryFieldRoundTrips()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);

        // Act & Assert
        await AssertTablesRoundTripAsync(db.CreateDbContext, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_StoredInvoice_When_AddedAgain_Then_ItThrowsAndTheFirstIsKept()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var invoice = CreateInvoice(0x61, LightningMoney.MilliSatoshis(1_000));
        await SaveAsync(db, c => new InvoiceDbRepository(c).AddAsync(invoice));

        // Act & Assert
        await using var context = db.CreateDbContext();
        var repository = new InvoiceDbRepository(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AddAsync(CreateInvoice(0x61, LightningMoney.MilliSatoshis(2_000))));
        AssertInvoice(invoice, await repository.GetByPaymentHashAsync(invoice.PaymentHash));
    }

    [Fact]
    public async Task Given_UnknownInvoice_When_Updated_Then_ItThrows()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = db.CreateDbContext();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InvoiceDbRepository(context).UpdateAsync(CreateInvoice(0x62, null)));
    }

    [Fact]
    public async Task Given_InvoiceStagedInThisUnitOfWork_When_ReadBack_Then_ItIsFoundBeforeTheSave()
    {
        // Arrange (the final hop may accept and settle in the same unit of work as the channel transition)
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = db.CreateDbContext();
        var repository = new InvoiceDbRepository(context);
        var invoice = CreateInvoice(0x63, LightningMoney.MilliSatoshis(5_000));
        await repository.AddAsync(invoice);

        // Act
        var staged = await repository.GetByPaymentHashAsync(invoice.PaymentHash);
        staged!.Accept(LightningMoney.MilliSatoshis(5_000));
        await repository.UpdateAsync(staged);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Assert
        await using var readContext = db.CreateDbContext();
        var stored = await new InvoiceDbRepository(readContext).GetByPaymentHashAsync(invoice.PaymentHash);
        Assert.Equal(InvoiceStatus.Accepted, stored!.Status);
        Assert.Equal(LightningMoney.MilliSatoshis(5_000), stored.AmountReceived);
    }

    [Fact]
    public async Task Given_FiveInvoices_When_Listed_Then_NewestFirstAndPaged()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        await SaveAsync(db, async c =>
        {
            var repository = new InvoiceDbRepository(c);
            for (byte i = 0; i < 5; i++)
                await repository.AddAsync(CreateInvoice((byte)(0x70 + i * 3), null, s_now.AddMinutes(i)));
        });

        // Act
        await using var context = db.CreateDbContext();
        var repository = new InvoiceDbRepository(context);
        var firstPage = await repository.ListAsync(0, 2);
        var secondPage = await repository.ListAsync(2, 2);
        var rest = await repository.ListAsync(4, 10);

        // Assert
        Assert.Equal(new[] { s_now.AddMinutes(4), s_now.AddMinutes(3) }, firstPage.Select(i => i.CreatedAt));
        Assert.Equal(new[] { s_now.AddMinutes(2), s_now.AddMinutes(1) }, secondPage.Select(i => i.CreatedAt));
        Assert.Equal(new[] { s_now }, rest.Select(i => i.CreatedAt));
        Assert.Empty(await repository.ListAsync(0, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListAsync(-1, 1));
    }

    [Fact]
    public async Task Given_FiveInvoicesSavedThroughTheUnitOfWork_When_ListedByIndex_Then_IndexesAreDenseAndPaged()
    {
        // Arrange (LND's add_index/settle_index, NL-1163/NL-1165): one allocator, as the node has
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var allocator = new LndIndexAllocator();
        for (byte i = 0; i < 5; i++)
        {
            var invoice = CreateInvoice((byte)(0x70 + i * 3), null, s_now.AddMinutes(i));
            if (i == 1)
                invoice.Cancel();
            await SaveThroughUnitOfWorkAsync(db, allocator, u => u.InvoiceDbRepository.AddAsync(invoice));
        }

        // The fourth one settles: it takes settle_index 1
        var settling = CreateInvoice(0x79, null, s_now.AddMinutes(3));
        settling.Accept(LightningMoney.MilliSatoshis(1_000));
        settling.Settle(s_now.AddMinutes(10));
        await SaveThroughUnitOfWorkAsync(db, allocator, u => u.InvoiceDbRepository.UpdateAsync(settling));

        // Act
        await using var context = db.CreateDbContext();
        var repository = new InvoiceDbRepository(context);
        var forward = await repository.ListByIndexAsync(new LndIndexQuery(1, null, true, 2), false);
        var backward = await repository.ListByIndexAsync(new LndIndexQuery(null, 5, false, 2), false);
        var open = await repository.ListByIndexAsync(new LndIndexQuery(null, null, true, 10), true);
        var dated = await repository.ListByIndexAsync(
                        new LndIndexQuery(null, null, true, 10, s_now.AddMinutes(1), s_now.AddMinutes(2)), false);
        var settled = await repository.ListSettledAfterAsync(0, 10);

        // Assert
        Assert.Equal(new ulong?[] { 2, 3 }, forward.Select(i => i.AddIndex));
        Assert.Equal(new ulong?[] { 4, 3 }, backward.Select(i => i.AddIndex));
        Assert.Equal(new ulong?[] { 1, 3, 5 }, open.Select(i => i.AddIndex));
        Assert.Equal(new ulong?[] { 2, 3 }, dated.Select(i => i.AddIndex));
        var only = Assert.Single(settled);
        Assert.Equal(settling.PaymentHash, only.PaymentHash);
        Assert.Equal(1ul, only.SettleIndex);
        Assert.Equal(4ul, only.AddIndex);
        Assert.Empty(await repository.ListByIndexAsync(new LndIndexQuery(null, null, true, 0), false));
    }

    [Fact]
    public async Task Given_PaymentsSavedThroughTheUnitOfWork_When_ListedByIndexAndCounted_Then_SucceededOnlyFilters()
    {
        // Arrange (NL-1163/NL-1165)
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var allocator = new LndIndexAllocator();
        var first = CreatePayment(0x91, s_now);
        var second = CreatePayment(0x94, s_now.AddSeconds(1));
        second.Succeed(SecretOf(0x95), s_now.AddSeconds(2));
        var third = CreatePayment(0x97, s_now.AddSeconds(2));
        foreach (var payment in new[] { first, second, third })
            await SaveThroughUnitOfWorkAsync(db, allocator, u => u.PaymentDbRepository.AddAsync(payment));

        // Act
        await using var context = db.CreateDbContext();
        var repository = new PaymentDbRepository(context);
        var all = await repository.ListByIndexAsync(new LndIndexQuery(null, null, true, 10), false);
        var newest = await repository.ListByIndexAsync(new LndIndexQuery(1, null, false, 1), false);
        var succeeded = await repository.ListByIndexAsync(new LndIndexQuery(null, null, true, 10), true);

        // Assert
        Assert.Equal(new ulong?[] { 1, 2, 3 }, all.Select(p => p.PaymentIndex));
        Assert.Equal(first.PaymentHash, all[0].PaymentHash);
        AssertPayment(third, Assert.Single(newest));
        Assert.Equal(2ul, Assert.Single(succeeded).PaymentIndex);
        Assert.Equal(2, all[0].Route.Count);
        Assert.Equal(3, await repository.CountAsync(false));
        Assert.Equal(1, await repository.CountAsync(true));
    }

    [Fact]
    public async Task Given_ASaveThatFails_When_TheNextSaveTakesAnIndex_Then_NoIndexIsSkipped()
    {
        // Arrange: the second save fails (a trigger refuses the insert): its index is given back
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var allocator = new LndIndexAllocator();
        await SaveThroughUnitOfWorkAsync(db, allocator,
                                         u => u.InvoiceDbRepository.AddAsync(CreateInvoice(0x31, null, s_now)));
        await ExecuteAsync(db, "CREATE TRIGGER fail_once BEFORE INSERT ON \"Invoices\" "
                             + "BEGIN SELECT RAISE(ABORT, 'refused'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveThroughUnitOfWorkAsync(
            db, allocator, u => u.InvoiceDbRepository.AddAsync(CreateInvoice(0x34, null, s_now.AddSeconds(1)))));
        await ExecuteAsync(db, "DROP TRIGGER fail_once;");

        // Act
        await SaveThroughUnitOfWorkAsync(db, allocator,
                                         u => u.InvoiceDbRepository.AddAsync(CreateInvoice(0x37, null,
                                                                                           s_now.AddSeconds(2))));

        // Assert
        await using var context = db.CreateDbContext();
        var listed = await new InvoiceDbRepository(context).ListByIndexAsync(new LndIndexQuery(null, null, true, 10),
                                                                               false);
        Assert.Equal(new ulong?[] { 1, 2 }, listed.Select(i => i.AddIndex));
    }

    [Fact]
    public async Task Given_FailedPayment_When_RetriedWithAShorterRoute_Then_TheAttemptAndItsHopsAreReplaced()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var failed = CreatePayment(0x81);
        failed.Fail(null, null, "no route", s_now);
        await SaveAsync(db, c => new PaymentDbRepository(c).AddAsync(failed));
        var retry = CreatePayment(0x81, s_now.AddSeconds(30), hops: 1);

        // Act
        await SaveAsync(db, c => new PaymentDbRepository(c).AddAsync(retry));

        // Assert: one row, the retry's fields, only the retry's hop
        await using var context = db.CreateDbContext();
        AssertPayment(retry, await new PaymentDbRepository(context).GetByPaymentHashAsync(retry.PaymentHash));
        Assert.Single(await context.Payments.ToListAsync(TestContext.Current.CancellationToken));
        var hop = Assert.Single(await context.PaymentHops.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, hop.HopIndex);
        Assert.Equal(retry.Route[0].NodeId, hop.NodeId);
    }

    [Fact]
    public async Task Given_AnInFlightPaymentWithAFee_When_ItFailsAndIsUpdated_Then_TheStoredFeeIsZero()
    {
        // Arrange (NL-982): the attempt offered 2,001 msat in fees
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var payment = CreatePayment(0x87);
        await SaveAsync(db, c => new PaymentDbRepository(c).AddAsync(payment));
        payment.Fail(null, 0, "refused", s_now);

        // Act
        await SaveAsync(db, c => new PaymentDbRepository(c).UpdateAsync(payment));

        // Assert: a failed payment paid nothing
        await using var context = db.CreateDbContext();
        var stored = await new PaymentDbRepository(context).GetByPaymentHashAsync(payment.PaymentHash);
        Assert.Equal(PaymentStatus.Failed, stored!.Status);
        Assert.True(stored.Fee.IsZero);
    }

    [Fact]
    public async Task Given_VerifiedHoldTimes_When_ThePaymentIsUpdatedOrReplaced_Then_EachHopKeepsItsHoldTime()
    {
        // Arrange - BOLT 4 attribution_data verified at the origin (NL-326): hop 0 verified, hop 1 not
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var payment = CreatePayment(0x85);
        await SaveAsync(db, c => new PaymentDbRepository(c).AddAsync(payment));
        payment.RecordHoldTimes([TimeSpan.FromMilliseconds(2_500)]);
        payment.Fail(null, 1, "failed with hold times", s_now);

        // Act: the outcome's update writes the hold times of the stored route
        await SaveAsync(db, c => new PaymentDbRepository(c).UpdateAsync(payment));

        // Assert
        await using (var context = db.CreateDbContext())
        {
            var stored = await new PaymentDbRepository(context).GetByPaymentHashAsync(payment.PaymentHash);
            AssertPayment(payment, stored);
            Assert.Equal([TimeSpan.FromMilliseconds(2_500), null], stored!.Route.Select(h => h.HoldTime));
        }

        // Act: a retry replaces the failed row with a route that already carries hold times (a fulfilled part)
        var retry = CreatePayment(0x85, s_now.AddSeconds(30));
        retry.RecordHoldTimes([TimeSpan.FromMilliseconds(300), TimeSpan.Zero]);
        await SaveAsync(db, c => new PaymentDbRepository(c).AddAsync(retry));

        // Assert
        await using var readContext = db.CreateDbContext();
        var replaced = await new PaymentDbRepository(readContext).GetByPaymentHashAsync(retry.PaymentHash);
        AssertPayment(retry, replaced);
        Assert.Equal([TimeSpan.FromMilliseconds(300), TimeSpan.Zero], replaced!.Route.Select(h => h.HoldTime));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_InFlightOrSucceededPayment_When_AddedAgain_Then_ItThrowsAndIsKept(bool succeeded)
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var payment = CreatePayment(0x82);
        if (succeeded)
            payment.Succeed(SecretOf(0x99), s_now);
        await SaveAsync(db, c => new PaymentDbRepository(c).AddAsync(payment));

        // Act & Assert
        await using var context = db.CreateDbContext();
        var repository = new PaymentDbRepository(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AddAsync(CreatePayment(0x82, s_now.AddSeconds(1), hops: 1)));
        AssertPayment(payment, await repository.GetByPaymentHashAsync(payment.PaymentHash));
    }

    [Fact]
    public async Task Given_PaymentsInEveryState_When_InFlightAreRead_Then_OnlyInFlightOnesComeBackOldestFirst()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var newer = CreatePayment(0x83, s_now.AddSeconds(2));
        var older = CreatePayment(0x86, s_now);
        older.AddOutgoingHtlc(new ChannelId(Enumerable.Repeat((byte)0x0C, 32).ToArray()), 4);
        var done = CreatePayment(0x89, s_now.AddSeconds(1));
        done.Succeed(SecretOf(0x8A), s_now.AddSeconds(3));
        await SaveAsync(db, async c =>
        {
            var repository = new PaymentDbRepository(c);
            await repository.AddAsync(newer);
            await repository.AddAsync(older);
            await repository.AddAsync(done);
        });

        // Act
        await using var context = db.CreateDbContext();
        var inFlight = await new PaymentDbRepository(context).GetInFlightAsync();

        // Assert
        Assert.Equal(2, inFlight.Count);
        AssertPayment(older, inFlight[0]);
        AssertPayment(newer, inFlight[1]);
    }

    [Fact]
    public async Task Given_UnknownPayment_When_Updated_Then_ItThrows()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = db.CreateDbContext();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PaymentDbRepository(context).UpdateAsync(CreatePayment(0x84)));
    }

    [Fact]
    public async Task Given_StoredCircuit_When_AddedAgain_Then_ItThrows()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var circuit = CreateCircuit(1);
        await SaveAsync(db, c => new ForwardCircuitDbRepository(c).AddAsync(circuit));

        // Act & Assert
        await using var context = db.CreateDbContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ForwardCircuitDbRepository(context).AddAsync(CreateCircuit(1)));
    }

    [Fact]
    public async Task Given_PendingCircuit_When_FailedThroughItsOutgoingHtlc_Then_TheHtlcIsRecordedAndItIsResolved()
    {
        // Arrange (the Offered save may never happen: a crash between the add and it, ForwardCircuitModel docs)
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var circuit = CreateCircuit(2);
        var other = CreateCircuit(3);
        await SaveAsync(db, async c =>
        {
            await new ForwardCircuitDbRepository(c).AddAsync(circuit);
            await new ForwardCircuitDbRepository(c).AddAsync(other);
        });
        var outgoing = new ChannelId(Enumerable.Repeat((byte)0x0B, 32).ToArray());

        // Act
        circuit.MarkFailed(outgoing, 9, s_now.AddSeconds(4));
        await SaveAsync(db, c => new ForwardCircuitDbRepository(c).UpdateAsync(circuit));

        // Assert
        await using var context = db.CreateDbContext();
        var repository = new ForwardCircuitDbRepository(context);
        AssertCircuit(circuit, await repository.GetByOutgoingAsync(outgoing, 9));
        Assert.Null(await repository.GetByOutgoingAsync(outgoing, 10));
        var unresolved = Assert.Single(await repository.GetUnresolvedAsync());
        AssertCircuit(other, unresolved);
    }

    [Fact]
    public async Task Given_ARefusedOffer_When_TheCircuitIsFailedWithItsSource_Then_TheReasonIsStored()
    {
        // Arrange (NL-457): a policy refusal records the channel it is about, without an outgoing HTLC
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var circuit = CreateCircuit(5);
        await SaveAsync(db, c => new ForwardCircuitDbRepository(c).AddAsync(circuit));
        var outgoing = new ChannelId(Enumerable.Repeat((byte)0x0C, 32).ToArray());

        // Act
        circuit.MarkFailed(s_now.AddSeconds(4), failureSource: outgoing);
        await SaveAsync(db, c => new ForwardCircuitDbRepository(c).UpdateAsync(circuit));

        // Assert
        await using var context = db.CreateDbContext();
        var stored = await new ForwardCircuitDbRepository(context)
                        .GetByIncomingAsync(circuit.IncomingChannelId, circuit.IncomingHtlcId);
        AssertCircuit(circuit, stored);
        Assert.Null(stored!.OutgoingChannelId);
        Assert.Equal(outgoing, stored.FailureSource);
        Assert.Null(stored.FailureCode);
    }

    [Fact]
    public async Task Given_CircuitOfferedInThisUnitOfWork_When_FoundByItsOutgoingHtlc_Then_TheStagedCircuitIsReturned()
    {
        // Arrange (the downstream resolution can be handled in the same unit of work as the Offered update)
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var circuit = CreateCircuit(4);
        await SaveAsync(db, c => new ForwardCircuitDbRepository(c).AddAsync(circuit));
        var outgoing = new ChannelId(Enumerable.Repeat((byte)0x0D, 32).ToArray());
        await using var context = db.CreateDbContext();
        var repository = new ForwardCircuitDbRepository(context);
        var staged = await repository.GetByIncomingAsync(circuit.IncomingChannelId, circuit.IncomingHtlcId);
        staged!.AddOutgoingHtlc(outgoing, 1);
        await repository.UpdateAsync(staged);

        // Act
        var found = await repository.GetByOutgoingAsync(outgoing, 1);

        // Assert
        AssertCircuit(staged, found);
    }

    [Fact]
    public async Task Given_OutgoingAddStagedInTheSameUnitOfWork_When_ItsOriginIsSet_Then_BothCommitTogetherAndLaterTransitionsKeepIt()
    {
        // Arrange (IChannelOperations.OfferHtlcAsync: the add and its origin in one save)
        await using var harness = await OriginHarness.CreateAsync();
        var add = harness.Driver.TryUsAdd(4_000_000)!;
        var key = add.Transition.UpsertedHtlcs.Single().Key;
        var paymentHash = add.Next.Htlcs[key].PaymentHash;
        var origin = HtlcOrigin.Local(paymentHash);

        // Act
        await using (var context = harness.Db.CreateDbContext())
        {
            var repository = new ChannelStateDbRepository(context);
            await repository.ApplyAsync(add.Next, add.Transition);
            await repository.SetHtlcOriginAsync(harness.ChannelId, key, origin);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await harness.PersistAsync(harness.Driver.TryUsCommit()!);

        // Assert
        await using var readContext = harness.Db.CreateDbContext();
        var reader = new ChannelStateDbRepository(readContext);
        Assert.Equal(origin, await reader.GetHtlcOriginAsync(harness.ChannelId, key));
        Assert.Equal(new[] { (harness.ChannelId, key) }, await reader.FindHtlcsByOriginAsync(origin));
        Assert.Empty(await reader.FindHtlcsByOriginAsync(HtlcOrigin.Local(new Hash(new byte[32]))));
    }

    [Fact]
    public async Task Given_ForwardedOrigin_When_Stored_Then_ItIsFoundByTheIncomingHtlcOnly()
    {
        // Arrange
        await using var harness = await OriginHarness.CreateAsync();
        var add = harness.Driver.TryUsAdd(3_000_000)!;
        await harness.PersistAsync(add);
        var key = add.Transition.UpsertedHtlcs.Single().Key;
        var incoming = new ChannelId(Enumerable.Repeat((byte)0x0E, 32).ToArray());
        var origin = HtlcOrigin.Forwarded(incoming, 5);

        // Act
        await using (var context = harness.Db.CreateDbContext())
        {
            await new ChannelStateDbRepository(context).SetHtlcOriginAsync(harness.ChannelId, key, origin);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await using var readContext = harness.Db.CreateDbContext();
        var reader = new ChannelStateDbRepository(readContext);
        Assert.Equal(origin, await reader.GetHtlcOriginAsync(harness.ChannelId, key));
        Assert.Equal(new[] { (harness.ChannelId, key) }, await reader.FindHtlcsByOriginAsync(origin));
        Assert.Empty(await reader.FindHtlcsByOriginAsync(HtlcOrigin.Forwarded(incoming, 6)));
        Assert.Empty(await reader.FindHtlcsByOriginAsync(HtlcOrigin.Forwarded(harness.ChannelId, 5)));
        Assert.Null(await reader.GetHtlcOriginAsync(harness.ChannelId, new HtlcKey(HtlcDirection.Incoming, 0)));
    }

    [Fact]
    public async Task Given_TrampolineOrigin_When_Stored_Then_ItRoundTripsAsKind3AndIsFoundByItsPaymentHashOnly()
    {
        // Arrange (NL-875: the outgoing HTLC of a trampoline relay's payment)
        await using var harness = await OriginHarness.CreateAsync();
        var add = harness.Driver.TryUsAdd(3_000_000)!;
        await harness.PersistAsync(add);
        var key = add.Transition.UpsertedHtlcs.Single().Key;
        var paymentHash = add.Next.Htlcs[key].PaymentHash;
        var origin = HtlcOrigin.Trampoline(paymentHash);

        // Act
        await using (var context = harness.Db.CreateDbContext())
        {
            await new ChannelStateDbRepository(context).SetHtlcOriginAsync(harness.ChannelId, key, origin);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await using var readContext = harness.Db.CreateDbContext();
        var reader = new ChannelStateDbRepository(readContext);
        Assert.Equal(origin, await reader.GetHtlcOriginAsync(harness.ChannelId, key));
        Assert.Equal((byte)3, readContext.Htlcs.Single(h => h.HtlcId == key.Id).OriginKind);
        Assert.Equal(new[] { (harness.ChannelId, key) }, await reader.FindHtlcsByOriginAsync(origin));
        Assert.Empty(await reader.FindHtlcsByOriginAsync(HtlcOrigin.Local(paymentHash)));
        Assert.Empty(await reader.FindHtlcsByOriginAsync(HtlcOrigin.Trampoline(new Hash(new byte[32]))));
    }

    [Fact]
    public async Task Given_InvalidOriginOrUnknownHtlc_When_OriginIsSet_Then_ItThrows()
    {
        // Arrange
        await using var harness = await OriginHarness.CreateAsync();
        await using var context = harness.Db.CreateDbContext();
        var repository = new ChannelStateDbRepository(context);
        var key = new HtlcKey(HtlcDirection.Outgoing, 0);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.SetHtlcOriginAsync(harness.ChannelId, key, default));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindHtlcsByOriginAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SetHtlcOriginAsync(harness.ChannelId, key, HtlcOrigin.Local(new Hash(new byte[32]))));
    }

    [Fact]
    public async Task Given_SnapshotWithADustPolicy_When_TheChannelIsReloaded_Then_ThePolicyComesBack()
    {
        // Arrange (NL-242)
        await using var harness = await OriginHarness.CreateAsync(maxDustHtlcExposureMsat: 5_000_000);
        await harness.PersistAsync(harness.Driver.TryUsAdd(1_000_000)!);

        // Act: a stale model update in between must not drop it
        await using (var context = harness.Db.CreateDbContext())
        {
            await new ChannelDbRepository(context, harness.Db.Sha256).UpdateAsync(harness.Channel);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var reloaded = await harness.ReloadChannelAsync();

        // Assert
        Assert.NotNull(reloaded.Commitments);
        Assert.Equal(5_000_000UL, reloaded.Commitments.Params.MaxDustHtlcExposureMsat);
        CommitmentsAssert.Equal(harness.Driver.Us, reloaded.Commitments);
    }

    [Fact]
    public async Task Given_ChannelWithInferredParams_When_TheSnapshotIsReloaded_Then_ItsLimitsStayUnenforced()
    {
        // Arrange (regression: the reload built the params without HasInferredLimits, so a channel migrated by
        // SplitChannelParams would have been failed over its guessed limits after a restart)
        await using var harness = await OriginHarness.CreateAsync();
        await using (var context = harness.Db.CreateDbContext())
        {
            var config = await context.ChannelConfigs.SingleAsync(c => c.ChannelId == harness.ChannelId,
                                                                   TestContext.Current.CancellationToken);
            config.HasInferredParams = true;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        var reloaded = await harness.ReloadChannelAsync();

        // Assert
        Assert.True(reloaded.ChannelParams.HasInferredParams);
        Assert.NotNull(reloaded.Commitments);
        Assert.True(reloaded.Commitments.Params.HasInferredLimits);
    }

    [Fact]
    public async Task Given_StoredAndUnknownChannels_When_ExistenceIsChecked_Then_ItAnswersWithoutLoadingThem()
    {
        // Arrange (NL-243: a channel with legacy HTLC rows is refused by GetByIdAsync, but it exists)
        await using var harness = await OriginHarness.CreateAsync();
        await using (var context = harness.Db.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Htlcs" ("ChannelId", "HtlcId", "Direction", "AmountMsat", "PaymentHash", "CltvExpiry",
                    "State", "OnionRoutingPacket")
                VALUES ({0}, 99, 1, 1000, {1}, 500, 0, {2})
                """, [(byte[])harness.ChannelId, new byte[32], Array.Empty<byte>()],
                TestContext.Current.CancellationToken);
        }

        // Act & Assert
        await using var readContext = harness.Db.CreateDbContext();
        var repository = new ChannelDbRepository(readContext, harness.Db.Sha256);
        Assert.True(await repository.ExistsAsync(harness.ChannelId));
        Assert.False(await repository.ExistsAsync(new ChannelId(Enumerable.Repeat((byte)0xEE, 32).ToArray())));
        await Assert.ThrowsAsync<LegacyHtlcStateException>(() => repository.GetByIdAsync(harness.ChannelId));
    }

    private static ForwardCircuitModel CreateCircuit(ulong incomingHtlcId) =>
        new(new ChannelId(Enumerable.Repeat((byte)0x0F, 32).ToArray()), incomingHtlcId,
            LightningMoney.MilliSatoshis(10_001_000), 800, new Hash(Enumerable.Repeat((byte)incomingHtlcId, 32)
                                                                        .ToArray()),
            SecretOf((byte)(0xC0 + incomingHtlcId)), new ShortChannelId(900_000, 7, 1),
            LightningMoney.MilliSatoshis(10_000_000), 760, s_now.AddSeconds(incomingHtlcId));

    private static async Task ExecuteAsync(SqliteDbTestContext db, string sql)
    {
        await using var context = db.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
    }

    private static async Task SaveThroughUnitOfWorkAsync(SqliteDbTestContext db, LndIndexAllocator allocator,
                                                         Func<UnitOfWork, Task> stage)
    {
        await using var context = db.CreateDbContext();
        var unitOfWork = new UnitOfWork(context, NullLogger<UnitOfWork>.Instance, new Sha256(),
                                              new UtxoMemoryRepository(), indexAllocator: allocator);
        await stage(unitOfWork);
        await unitOfWork.SaveChangesAsync();
    }

    private static async Task SaveAsync(SqliteDbTestContext db, Func<NLightningDbContext, Task> stage)
    {
        await using var context = db.CreateDbContext();
        await stage(context);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A stored channel with a first snapshot, driven by <see cref="CommitmentDanceDriver"/>.</summary>
    private sealed class OriginHarness : IAsyncDisposable
    {
        public SqliteDbTestContext Db { get; }
        public Domain.Channels.Models.ChannelModel Channel { get; }
        public CommitmentDanceDriver Driver { get; }
        public ChannelId ChannelId => Channel.ChannelId;

        private OriginHarness(SqliteDbTestContext db, Domain.Channels.Models.ChannelModel channel,
                              CommitmentDanceDriver driver)
        {
            Db = db;
            Channel = channel;
            Driver = driver;
        }

        public static async Task<OriginHarness> CreateAsync(ulong? maxDustHtlcExposureMsat = null)
        {
            var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
            var channel = SqliteDbTestContext.CreateChannel(true);
            var driver = new CommitmentDanceDriver(channel.ChannelId,
                                                   CommitmentParams.FromChannel(channel, maxDustHtlcExposureMsat),
                                                   channel.LocalBalance.MilliSatoshi,
                                                   channel.RemoteBalance.MilliSatoshi, seed: 3);
            await using (var context = db.CreateDbContext())
            {
                await new ChannelDbRepository(context, db.Sha256).AddAsync(channel);
                await new ChannelStateDbRepository(context).InitializeAsync(driver.Us);
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            return new OriginHarness(db, channel, driver);
        }

        public async Task PersistAsync(CommitmentsResult result)
        {
            await using var context = Db.CreateDbContext();
            await new ChannelStateDbRepository(context).ApplyAsync(result.Next, result.Transition);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task<Domain.Channels.Models.ChannelModel> ReloadChannelAsync()
        {
            await using var context = Db.CreateDbContext();
            return await new ChannelDbRepository(context, Db.Sha256).GetByIdAsync(ChannelId)
                ?? throw new InvalidOperationException("Channel was not reloaded");
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}