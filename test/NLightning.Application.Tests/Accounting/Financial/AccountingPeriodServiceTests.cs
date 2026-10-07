using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Accounting.Financial;

using Application.Accounting;
using Application.Accounting.Financial;
using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Lots;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// The period close, the lock and the signed digests of the financial book (NL-602 A3-T5, D-A8, D-A13) on a SQLite
/// file through the production unit of work, with the real sealer, operational books and signer and a small financial
/// projector (<see cref="TestFinancialProjector"/>) over seeded financial entries and lots: a close and its signature,
/// the refusals, every late write landing as an adjustment in the open period, tampering found by verify, and a
/// rebuild after a close equal to the incremental book.
/// </summary>
public sealed class AccountingPeriodServiceTests
{
    private static readonly DateTimeOffset s_sep5 = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_sep20 = new(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_sep28 = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_oct1 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_october = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_ASeptemberOfIncomeAndSpending_When_Closed_Then_ThePeriodIsSignedAndItsRowsMarked()
    {
        // Arrange
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await SeedSeptemberAsync(kit);

        // Act
        var report = await kit.Periods.CloseAsync("2026-09", false, TestContext.Current.CancellationToken);

        // Assert: the row
        var period = report.Period;
        Assert.Equal("2026-09", period.PeriodId);
        Assert.Equal(AccountingPeriodState.Closed, period.State);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), period.Start);
        Assert.Equal(s_october, period.End);
        Assert.Equal(s_now, period.ClosedAt);
        Assert.False(period.Forced);
        Assert.Equal(5, period.LastLedgerSeq);
        Assert.Equal(32, period.Digest!.Length);
        Assert.True(kit.Signer.VerifyNodeMessage(new Hash(period.Digest), new CompactSignature(period.Signature!),
                                                 kit.NodeId));
        Assert.Equal(await FeedHashAsync(kit, 5), period.ChainHash);
        Assert.Equal(4, report.EntryCount);
        Assert.Equal(2, report.ReliefCount);
        Assert.Equal(2, report.OpenLotCount);
        Assert.Equal((byte[])kit.NodeId, report.NodeId);

        // The closing state: the balances of September, and a rebuild replays after the October entry's sequence
        var state = Assert.IsType<AccountingClosingState>(report.ClosingState);
        Assert.Equal(3, state.ReplayAfterLedgerSeq);
        var channels = Assert.Single(state.Balances, b => b.AccountName == "fin:channels");
        Assert.Equal(1_000_000 + 400_000 - 300_000 - 500_000, channels.BalanceMsat);

        // The period's entries, lots and reliefs are closed, October's are not
        var entries = await kit.ListFinancialEntriesAsync();
        Assert.All(entries.Where(e => e.OccurredAt < s_october), e => Assert.Equal("2026-09", e.ClosedPeriodId));
        Assert.Null(Assert.Single(entries, e => e.OccurredAt >= s_october).ClosedPeriodId);
        using var scope = kit.CreateScope();
        var lots = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingLotDbRepository;
        Assert.All(await lots.ListLotsAcquiredBeforeAsync(s_october, 0, 100, TestContext.Current.CancellationToken),
                   l => Assert.Equal("2026-09", l.ClosedPeriodId));

        // And verify agrees
        var verification = Assert.Single(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken));
        Assert.True(verification.IsIntact, verification.Problem);
        Assert.Equal(4, verification.EntryCount);
        Assert.Equal(2, verification.ReliefCount);
        Assert.Equal(2, verification.OpenLotCount);
    }

    [Fact]
    public async Task Given_ClosedPeriods_When_ListedAndShown_Then_TheyComeBackWithTheirClosingState()
    {
        // Arrange
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await SeedSeptemberAsync(kit);
        await kit.Periods.CloseAsync("2026-09", false, TestContext.Current.CancellationToken);
        kit.Clock.Now = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

        // Act
        var october = await kit.Periods.CloseAsync("2026-10-01..2026-10-31", false,
                                                   TestContext.Current.CancellationToken);
        var list = await kit.Periods.ListAsync(TestContext.Current.CancellationToken);
        var shown = await kit.Periods.GetAsync("2026-09", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["2026-09", "2026-10-01..2026-10-31"], list.Select(p => p.Period.PeriodId));
        Assert.Equal(1, october.EntryCount);
        Assert.NotNull(shown?.ClosingState);
        Assert.Null(await kit.Periods.GetAsync("2026-08", TestContext.Current.CancellationToken));
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
    }

    [Fact]
    public async Task Given_UnvaluedAndUnclassifiedRows_When_Closed_Then_RefusedUnlessForced()
    {
        // Arrange
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await kit.AddAndProjectAsync(FinancialCloseTestKit.Income("unvalued-1", 2_000, s_sep5),
                                     FinancialCloseTestKit.Spend("unclassified-1", 1_000, s_sep20));

        // Act
        var refused = await Assert.ThrowsAsync<AccountingCloseRefusedException>(
                          () => kit.Periods.CloseAsync("2026-09", false, TestContext.Current.CancellationToken));
        var forced = await kit.Periods.CloseAsync("2026-09", true, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, refused.UnvaluedPostings);
        Assert.Equal(1, refused.UnclassifiedEntries);
        Assert.Contains("--force", refused.Message);
        Assert.True(forced.Period.Forced);
        Assert.Equal(2, forced.UnvaluedPostings);
        Assert.Equal(1, forced.UnclassifiedEntries);
        Assert.True(Assert.Single(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken))
                          .IsIntact);
    }

    [Fact]
    public async Task Given_AClosedSeptember_When_InvalidClosesAreAsked_Then_EachIsRefused()
    {
        // Arrange
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await SeedSeptemberAsync(kit);
        var ct = TestContext.Current.CancellationToken;

        // Act & Assert: a period that has not ended, a book with older entries no close holds
        await Assert.ThrowsAsync<AccountingCloseRefusedException>(() => kit.Periods.CloseAsync("2026-10", false, ct));
        var gap = await Assert.ThrowsAsync<AccountingCloseRefusedException>(
                      () => kit.Periods.CloseAsync("2026-09-10..2026-09-30", false, ct));
        Assert.Contains("contiguous", gap.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Periods.CloseAsync("September", false, ct));

        // A close, then the same period again and one that leaves a gap
        await kit.Periods.CloseAsync("2026-09", false, ct);
        kit.Clock.Now = new DateTimeOffset(2027, 1, 2, 0, 0, 0, TimeSpan.Zero);
        await Assert.ThrowsAsync<AccountingCloseRefusedException>(() => kit.Periods.CloseAsync("2026-09", true, ct));
        var skip = await Assert.ThrowsAsync<AccountingCloseRefusedException>(
                       () => kit.Periods.CloseAsync("2026-11", true, ct));
        Assert.Contains("2026-10-01", skip.Message);

        // The financial book off
        kit.Projector.IsEnabled = false;
        await Assert.ThrowsAsync<AccountingCloseRefusedException>(() => kit.Periods.CloseAsync("2026-10", true, ct));
        await Assert.ThrowsAsync<AccountingCloseRefusedException>(() => kit.Periods.RebuildFinancialAsync(ct));
    }

    [Fact]
    public async Task Given_AClosedPeriod_When_AFactOfItArrivesLate_Then_ItIsAnAdjustmentDatedNowInTheOpenPeriod()
    {
        // Arrange
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await SeedSeptemberAsync(kit);
        await kit.Periods.CloseAsync("2026-09", false, TestContext.Current.CancellationToken);
        var closedBefore = (await kit.ListFinancialEntriesAsync()).Where(e => e.ClosedPeriodId is not null).ToList();
        kit.Clock.Now = s_now.AddHours(1);

        // Act: an invoice of September 29 sealed after the close
        await kit.AddAndProjectAsync(
            FinancialCloseTestKit.Income("late", 70_000, new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero)));

        // Assert: one adjustment of the late fact, in the open period, dated now
        var entries = await kit.ListFinancialEntriesAsync();
        var late = Assert.Single(entries, e => e.EventKey == "late");
        Assert.Equal(1, late.Adjustment);
        Assert.Equal(s_now.AddHours(1), late.OccurredAt);
        Assert.True(late.Flags.HasFlag(AccountingEntryFlags.Adjustment));
        Assert.Null(late.ClosedPeriodId);
        Assert.StartsWith("adjusts 2026-09: LateFact of 2026-09-29", late.Note);
        Assert.Equal(70_000, late.Postings.Single(p => p.Account == AccountRole.Channels).AmountMsat);

        // The closed period is untouched and still verifies
        Assert.Equal(closedBefore.Select(e => (e.LedgerSeq, e.Adjustment, e.OccurredAt)),
                     entries.Where(e => e.ClosedPeriodId is not null).Select(e => (e.LedgerSeq, e.Adjustment,
                                                                                    e.OccurredAt)));
        Assert.True(Assert.Single(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken))
                          .IsIntact);

        // Its lot is dated as the adjustment, outside the closed period
        using var scope = kit.CreateScope();
        var lot = Assert.Single(await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingLotDbRepository
                                           .ListLotsAcquiredBeforeAsync(DateTimeOffset.MaxValue, 0, 100,
                                                                        TestContext.Current.CancellationToken),
                                l => l.OriginalMsat == 70_000);
        Assert.Equal(s_now.AddHours(1), lot.AcquiredAt);
        Assert.Equal(1, lot.SourceAdjustment);
        Assert.Null(lot.ClosedPeriodId);
    }

    [Fact]
    public async Task Given_AClosedPeriod_When_APriceAnOverrideOrARuleTouchesIt_Then_EachIsOneAdjustment()
    {
        // Arrange: a forced close with an unvalued September invoice
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await kit.AddAndProjectAsync(FinancialCloseTestKit.Income("unvalued-1", 2_000, s_sep5));
        await kit.Periods.CloseAsync("2026-09", true, TestContext.Current.CancellationToken);
        var closed = Assert.Single(await kit.ListFinancialEntriesAsync());
        var ct = TestContext.Current.CancellationToken;

        // Act: the back-valuation cannot value the closed posting
        bool valued;
        using (var scope = kit.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            valued = await unitOfWork.AccountingBooksDbRepository.SetPostingValueAsync(
                         new AccountingPostingKey(AccountingBook.Financial, closed.LedgerSeq, 0, 0), 1.2m, "USD", 1,
                         ct);
            await unitOfWork.SaveChangesAsync();
        }

        // The price, an override and a rule change go through the sink, each once (the price twice: deduplicated)
        var staged = new List<AccountingEntry?>();
        foreach (var (reason, dedupe) in new[]
                 {
                     (AccountingAdjustmentReason.Price, "price:1:0:0"),
                     (AccountingAdjustmentReason.Price, "price:1:0:0"),
                     (AccountingAdjustmentReason.Override, "override:income:sales"),
                     (AccountingAdjustmentReason.RuleChange, "rule:7")
                 })
        {
            using var scope = kit.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            using (await kit.Periods.EnterAsync(ct))
            {
                staged.Add(await kit.Periods.StageAdjustmentAsync(unitOfWork, new AccountingAdjustment(
                                                                      reason, closed.LedgerSeq, closed.EventKey,
                                                                      closed.Kind, closed.OccurredAt,
                                                                      [
                                                                          new AccountingPosting(
                                                                              AccountRole.Received, 0)
                                                                          {
                                                                              AccountName = "fin:received"
                                                                          },
                                                                          new AccountingPosting(
                                                                              AccountRole.Received, 0)
                                                                          {
                                                                              AccountName = "income:sales"
                                                                          }
                                                                      ])
                { DedupeKey = dedupe }, ct));
                await unitOfWork.SaveChangesAsync();
            }
        }

        // Assert
        Assert.False(valued);
        Assert.Equal([1, 0, 2, 3], staged.Select(e => e?.Adjustment ?? 0));
        var entries = await kit.ListFinancialEntriesAsync();
        Assert.Equal(4, entries.Count);
        Assert.All(entries.Where(e => e.Adjustment > 0), e =>
        {
            Assert.Equal(s_now, e.OccurredAt);
            Assert.Null(e.ClosedPeriodId);
            Assert.True(e.Flags.HasFlag(AccountingEntryFlags.Adjustment));
        });
        Assert.StartsWith("[price:1:0:0] adjusts 2026-09: Price", entries[1].Note);
        Assert.Null(Assert.Single(entries, e => e.Adjustment == 0).Postings[0].FiatAmount);
        Assert.True(Assert.Single(await kit.Periods.VerifyClosesAsync(ct)).IsIntact);
    }

    [Fact]
    public async Task Given_APriceForAClosedPosting_When_TheBackValuationHandsItOver_Then_OneAdjustmentCarriesIt()
    {
        // Arrange: a forced close with an unvalued September invoice; an unvalued October one stays open
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await kit.AddAndProjectAsync(FinancialCloseTestKit.Income("unvalued-1", 2_000, s_sep5),
                                     FinancialCloseTestKit.Income("unvalued-2", 3_000, s_oct1));
        await kit.Periods.CloseAsync("2026-09", true, TestContext.Current.CancellationToken);
        var price = new AccountingPrice(7, "USD", s_sep5, 60_000m, AccountingPriceSource.Csv, s_sep5);
        var ct = TestContext.Current.CancellationToken;
        using (var scope = kit.CreateScope())
        {
            // The price row the postings name (a stored price, FK)
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.AccountingPriceDbRepository.TryAddAsync(price with { Id = 0 }, ct);
            await unitOfWork.SaveChangesAsync();
        }

        // Act
        var taken = new List<bool>();
        foreach (var (seq, index) in new[] { (1L, 0), (1L, 0), (2L, 0) })
        {
            using var scope = kit.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var posting = new AccountingUnvaluedPosting(new AccountingPostingKey(AccountingBook.Financial, seq, 0, index),
                                                        seq == 1 ? s_sep5 : s_oct1, AccountRole.Channels,
                                                        "fin:channels", seq == 1 ? 2_000 : 3_000,
                                                        seq == 1 ? "2026-09" : null);
            using (await kit.Periods.EnterAsync(ct))
            {
                taken.Add(await kit.Periods.AdjustLateValuationAsync(
                              unitOfWork, new AccountingLateValuation(posting, price with { Id = 1 }, 1.2m), ct));
                await unitOfWork.SaveChangesAsync();
            }
        }

        // Assert: taken twice (the second already there), refused for the open posting
        Assert.Equal([true, true, false], taken);
        var adjustment = Assert.Single(await kit.ListFinancialEntriesAsync(), e => e.Adjustment > 0);
        Assert.Equal(1, adjustment.LedgerSeq);
        Assert.Equal(s_now, adjustment.OccurredAt);
        var line = Assert.Single(adjustment.Postings);
        Assert.Equal(0, line.AmountMsat);
        Assert.Equal(1.2m, line.FiatAmount);
        Assert.Equal(1, line.PriceId);
        Assert.StartsWith("[price:1:0:0] adjusts 2026-09: Price", adjustment.Note);
    }

    [Fact]
    public void Given_TheDefaultSinkRegisteredFirst_When_TheAccountingServicesAreAdded_Then_ThePeriodServiceIsTheSink()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IChannelMemoryRepository>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddSingleton<IAccountingAdjustmentSink>(NullAccountingAdjustmentSink.Instance);

        // Act
        services.AddAccountingServices();
        services.AddAccountingServices();
        using var provider = services.BuildServiceProvider();

        // Assert
        var periods = provider.GetRequiredService<AccountingPeriodService>();
        Assert.Same(periods, provider.GetRequiredService<IAccountingAdjustmentSink>());
        Assert.Same(periods, provider.GetRequiredService<IAccountingPeriods>());
        // A3-T4: the financial projector (off under the default Operational profile), one instance with the lot import
        var projector = Assert.IsType<FinancialBooksProjector>(provider.GetRequiredService<IFinancialBooksProjector>());
        Assert.False(projector.IsEnabled);
        Assert.Same(projector, provider.GetRequiredService<IAccountingLots>());
        Assert.Single(services, d => d.ServiceType == typeof(IAccountingAdjustmentSink));
    }

    [Fact]
    public async Task Given_ClosedPeriods_When_TheLockIsAsked_Then_EveryTimeBeforeTheLastEndIsLocked()
    {
        // Arrange
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await SeedSeptemberAsync(kit);
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await kit.Periods.GetLockingPeriodAsync(s_sep5, ct));
        await kit.Periods.CloseAsync("2026-09", false, ct);

        // Act & Assert
        Assert.Equal("2026-09", (await kit.Periods.GetLockingPeriodAsync(s_sep5, ct))?.PeriodId);
        Assert.Equal("2026-09", (await kit.Periods.GetLockingPeriodAsync(s_sep5.AddYears(-1), ct))?.PeriodId);
        Assert.Null(await kit.Periods.GetLockingPeriodAsync(s_october, ct));

        // A fact of the open period is no adjustment
        using var scope = kit.CreateScope();
        await Assert.ThrowsAsync<ArgumentException>(
            () => kit.Periods.StageAdjustmentAsync(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                                                   new AccountingAdjustment(AccountingAdjustmentReason.LateFact, 99,
                                                                            "k", AccountingEventKind.InvoiceSettled,
                                                                            s_oct1, []), ct));
    }

    [Theory]
    [InlineData("entry", "UPDATE \"AccountingPostings\" SET \"AmountMsat\" = \"AmountMsat\" + 1000 WHERE \"Book\" = 1 "
                       + "AND \"LedgerSeq\" = 1 AND \"Index\" = 0")]
    [InlineData("entry", "UPDATE \"AccountingEntries\" SET \"ClosedPeriodId\" = NULL WHERE \"Book\" = 1 AND "
                       + "\"LedgerSeq\" = 2")]
    [InlineData("lot", "UPDATE \"AccountingLots\" SET \"FiatCost\" = '99' WHERE \"Id\" = 1")]
    [InlineData("lot", "UPDATE \"AccountingLots\" SET \"RemainingMsat\" = \"RemainingMsat\" - 1 WHERE \"Id\" = 2")]
    [InlineData("relief", "UPDATE \"AccountingLotReliefs\" SET \"Msat\" = \"Msat\" + 1 WHERE \"Id\" = 1")]
    [InlineData("digest", "UPDATE \"AccountingPeriods\" SET \"Digest\" = zeroblob(32)")]
    [InlineData("signature", "UPDATE \"AccountingPeriods\" SET \"Signature\" = zeroblob(64)")]
    [InlineData("state", "UPDATE \"AccountingPeriods\" SET \"ClosingState\" = replace(\"ClosingState\", "
                       + "'\"replayAfter\":3', '\"replayAfter\":2')")]
    [InlineData("chain", "UPDATE \"AccountingEvents\" SET \"Hash\" = zeroblob(32) WHERE \"LedgerSeq\" = 5")]
    public async Task Given_AClosedPeriod_When_ItsRowsAreTamperedWith_Then_VerifyReportsIt(string what, string sql)
    {
        // Arrange
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await SeedSeptemberAsync(kit);
        await kit.Periods.CloseAsync("2026-09", false, TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken))
                          .IsIntact);

        // Act
        await kit.ExecuteSqlAsync(sql);
        var verification = Assert.Single(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.False(verification.IsIntact);
        Assert.NotNull(verification.Problem);
        switch (what)
        {
            case "signature":
                Assert.True(verification.DigestMatches);
                Assert.False(verification.SignatureValid);
                break;
            case "chain":
                Assert.False(verification.ChainHashMatches);
                Assert.True(verification.SignatureValid);
                break;
            case "digest":
                Assert.False(verification.DigestMatches);
                Assert.False(verification.SignatureValid);
                break;
            default:
                Assert.False(verification.DigestMatches);
                Assert.True(verification.SignatureValid);
                break;
        }
    }

    [Fact]
    public async Task Given_AWritePastTheLock_When_Verified_Then_TheStrayEntryIsReported()
    {
        // Arrange: a writer that ignores the lock posts a September entry after the close
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await SeedSeptemberAsync(kit);
        await kit.Periods.CloseAsync("2026-09", false, TestContext.Current.CancellationToken);
        using (var scope = kit.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.AccountingBooksDbRepository.AddEntryAsync(
                new AccountingEntry(99, "stray", AccountingEventKind.InvoiceSettled, s_sep20, null, null, [])
                {
                    Book = AccountingBook.Financial
                }, TestContext.Current.CancellationToken);
            await unitOfWork.SaveChangesAsync();
        }

        // Act
        var verification = Assert.Single(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.True(verification.DigestMatches);
        Assert.Equal(1, verification.StrayEntryCount);
        Assert.False(verification.IsIntact);
        Assert.Contains("past the lock", verification.Problem);
    }

    [Fact]
    public async Task Given_ASecondClose_When_TheFirstIsTamperedWith_Then_BothFailVerify()
    {
        // Arrange: the second close's digest covers the first's
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await SeedSeptemberAsync(kit);
        await kit.Periods.CloseAsync("2026-09", false, TestContext.Current.CancellationToken);
        kit.Clock.Now = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        await kit.Periods.CloseAsync("2026-10", false, TestContext.Current.CancellationToken);

        // Act
        await kit.ExecuteSqlAsync("UPDATE \"AccountingPeriods\" SET \"Digest\" = zeroblob(32) WHERE \"PeriodId\" = "
                                + "'2026-09'");
        var verifications = await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, verifications.Count);
        Assert.All(verifications, v => Assert.False(v.DigestMatches));
    }

    [Fact]
    public async Task Given_AClose_When_TheFinancialBookIsRebuilt_Then_ItEqualsTheIncrementalBook()
    {
        // Arrange: September closed; October spends September's lots; a late September fact after the close
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await SeedSeptemberAsync(kit);
        await kit.Periods.CloseAsync("2026-09", false, TestContext.Current.CancellationToken);
        kit.Clock.Now = s_now.AddHours(1);
        await kit.AddAndProjectAsync(
            FinancialCloseTestKit.Income("late", 70_000, new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero)),
            FinancialCloseTestKit.Spend("oct-spend", 450_000, s_now.AddMinutes(30)),
            FinancialCloseTestKit.Income("oct-income", 30_000, s_now.AddMinutes(40)));
        var incremental = await kit.SnapshotFinancialBookAsync();

        // Act
        var projected = await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);

        // Assert: the open period replayed (the October entries; the late fact's adjustment kept, its replay skipped)
        Assert.Equal(3, projected);
        Assert.Equal(incremental, await kit.SnapshotFinancialBookAsync());
        Assert.True(Assert.Single(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken))
                          .IsIntact);

        // A second rebuild is the same again
        await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);
        Assert.Equal(incremental, await kit.SnapshotFinancialBookAsync());
    }

    [Fact]
    public async Task Given_NoClose_When_TheFinancialBookIsRebuilt_Then_ItEqualsTheIncrementalBook()
    {
        // Arrange
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await SeedSeptemberAsync(kit);
        var incremental = await kit.SnapshotFinancialBookAsync();

        // Act
        var projected = await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(5, projected);
        Assert.Equal(incremental, await kit.SnapshotFinancialBookAsync());
    }

    [Fact]
    public async Task Given_TheNullProjector_When_AsServiceDefaults_Then_TheFinancialBookIsOff()
    {
        // Arrange
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        using var periods = new AccountingPeriodService(kit.ScopeFactory, NullLogger<AccountingPeriodService>.Instance, kit.Books,
                                                        signer: kit.Signer, timeProvider: kit.Clock);

        // Act
        var refused = await Assert.ThrowsAsync<AccountingCloseRefusedException>(
                          () => periods.CloseAsync("2026-09", true, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("Accounting:Profile=Financial", refused.Message);
        Assert.Empty(await periods.VerifyClosesAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// September: two invoices (lots 1 and 2) and two payments (both relieve lot 1, FIFO); one October 1 invoice (lot
    /// 3, ledger sequence 4) in the open period, sealed before the last September payment (5).
    /// </summary>
    [Fact]
    public async Task Given_AFinancialWriteInProgress_When_ClosesAreVerified_Then_VerificationWaitsAndCanBeCancelled()
    {
        // Arrange: signed September includes lots whose remaining amounts change in October.
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await FinancialCloseTestKit.CreateAsync(s_now);
        await SeedSeptemberAsync(kit);
        await kit.Periods.CloseAsync("2026-09", false, ct);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<IReadOnlyList<AccountingCloseVerification>> verification;

        // Act: no multi-query digest may read while a financial writer owns the gate.
        using (await kit.Periods.EnterAsync(ct))
        {
            verification = kit.Periods.VerifyClosesAsync(ct);
            Assert.False(verification.IsCompleted);
            var interrupted = kit.Periods.VerifyClosesAsync(cancelled.Token);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted);
        }

        // Assert: completing the write releases verification; a cancelled reader did not leak the gate.
        Assert.True(Assert.Single(await verification).IsIntact);
        Assert.True(Assert.Single(await kit.Periods.VerifyClosesAsync(ct)).IsIntact);
    }

    private static async Task SeedSeptemberAsync(FinancialCloseTestKit kit) =>
        await kit.AddAndProjectAsync(FinancialCloseTestKit.Income("sep-income-1", 1_000_000, s_sep5),
                                     FinancialCloseTestKit.Income("sep-income-2", 400_000, s_sep20),
                                     FinancialCloseTestKit.Spend("sep-spend-1", 300_000, s_sep20.AddHours(1)),
                                     FinancialCloseTestKit.Income("oct-income-0", 5_000, s_oct1),
                                     FinancialCloseTestKit.Spend("sep-spend-2", 500_000, s_sep28));

    private static async Task<byte[]?> FeedHashAsync(FinancialCloseTestKit kit, long ledgerSeq)
    {
        using var scope = kit.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingEventDbRepository
                                .GetSealedRangeAsync(ledgerSeq, 1, TestContext.Current.CancellationToken);
        return events.Single().Hash;
    }
}