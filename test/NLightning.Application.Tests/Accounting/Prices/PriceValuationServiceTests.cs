using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Mocks;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace NLightning.Application.Tests.Accounting.Prices;

using Application.Accounting;
using Application.Accounting.Prices;
using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Prices;
using Domain.Crypto.Hashes;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;

/// <summary>
/// The back-valuation of the financial books (NL-602 A3-T2) over the production unit of work on a SQLite file, with
/// financial (Book 1) entries seeded the way the financial projector (A3-T4) writes them: stored prices value postings
/// within <c>MaxAge</c> (the boundary included), missing hours are fetched once each and stored, a closed period is
/// never filled (the adjustment rule gets it), paging gets past postings that cannot be valued, <c>Source=None</c> makes
/// no request at all, and the import/fetch commands store prices.
/// </summary>
public sealed class PriceValuationServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset s_hour = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan s_maxAge = TimeSpan.FromHours(26);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"nltg-prices-{Guid.NewGuid():N}.db");

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider? _provider;
    private long _nextSeq = 1;

    private ServiceProvider Provider => _provider ?? throw new InvalidOperationException("Not initialized");

    public async ValueTask InitializeAsync()
    {
        _provider = BuildProvider(_databasePath);
        using var scope = Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                   .MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();

        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless
            }
        }
    }

    [Fact]
    public async Task Given_StoredPrices_When_ARoundRuns_Then_PostingsWithinMaxAgeAreValuedAndTheBoundaryCounts()
    {
        // Arrange: a price at 10:00; postings at 10:30, exactly MaxAge after the price, and one tick past it
        await StorePricesAsync((s_hour, 86_000m));
        var inHour = await AddEntryAsync(s_hour.AddMinutes(30), 150_000);
        var atBoundary = await AddEntryAsync(s_hour + s_maxAge, 2_000);
        var pastBoundary = await AddEntryAsync(s_hour + s_maxAge + TimeSpan.FromTicks(1), 3_000);
        await using var service = CreateService(new AccountingPriceOptions { Source = AccountingPriceSourceMode.None });

        // Act
        var round = await service.ValueNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(6, round.Listed);
        Assert.Equal(4, round.Valued);
        Assert.Equal(2, round.Unpriced);
        Assert.Equal(0, round.Fetched);
        var priceId = (await ListPricesAsync()).Single().Id;
        var valued = await GetEntryAsync(inHour);
        Assert.Equal([0.129m, -0.129m], valued.Postings.Select(p => p.FiatAmount));
        Assert.All(valued.Postings, p => Assert.Equal("USD", p.FiatCurrency));
        Assert.All(valued.Postings, p => Assert.Equal(priceId, p.PriceId));
        Assert.Equal(AccountingEntryFlags.None, valued.Flags & AccountingEntryFlags.Unvalued);
        Assert.Equal([0.00172m, -0.00172m], (await GetEntryAsync(atBoundary)).Postings.Select(p => p.FiatAmount));
        var unvalued = await GetEntryAsync(pastBoundary);
        Assert.All(unvalued.Postings, p => Assert.Null(p.FiatAmount));
        Assert.True(unvalued.Flags.HasFlag(AccountingEntryFlags.Unvalued));
        var balances = await GetBalancesAsync();
        Assert.Equal(0.129m + 0.00172m, balances.Single(b => b.AccountName == "assets:lightning").FiatAmount);
    }

    [Fact]
    public async Task Given_HoursWithoutAPrice_When_ARoundRuns_Then_EachHourIsAskedOnceStoredAndUsed()
    {
        // Arrange: two postings in the 10:00 hour and one at 13:10
        var source = new StubPriceSource(at => Price(AccountingValuationHour(at), 80_000m));
        await AddEntryAsync(s_hour.AddMinutes(40), 1_000);
        await AddEntryAsync(s_hour.AddMinutes(5), 1_000);
        await AddEntryAsync(s_hour.AddHours(3).AddMinutes(10), 1_000);
        await using var service = CreateService(new AccountingPriceOptions(), source);

        // Act
        var round = await service.ValueNowAsync(TestContext.Current.CancellationToken);
        var again = await service.ValueNowAsync(TestContext.Current.CancellationToken);

        // Assert: asked at each hour's earliest posting, stored, and every posting valued
        Assert.Equal([s_hour.AddMinutes(5), s_hour.AddHours(3).AddMinutes(10)], source.Asked);
        Assert.Equal(2, round.Fetched);
        Assert.Equal(2, round.Stored);
        Assert.Equal(6, round.Valued);
        Assert.Equal(0, again.Listed);
        var prices = await ListPricesAsync();
        Assert.Equal([s_hour, s_hour.AddHours(3)], prices.Select(p => p.Time));
        Assert.All(prices, p => Assert.Equal(AccountingPriceSource.Http, p.Source));
    }

    [Fact]
    public async Task Given_TheSourceHasNoPriceForAnHour_When_AnOlderStoredPriceIsWithinMaxAge_Then_ItValuesAfterTheAsk()
    {
        // Arrange: a stored price three hours before the posting; the source has nothing for the posting's hour
        await StorePricesAsync((s_hour.AddHours(-3), 81_000m));
        var source = new StubPriceSource(_ => null);
        var seq = await AddEntryAsync(s_hour.AddMinutes(10), 100_000_000_000);
        await using var service = CreateService(new AccountingPriceOptions(), source);

        // Act
        var round = await service.ValueNowAsync(TestContext.Current.CancellationToken);

        // Assert: the hour was asked once, then D-A11's nearest price within MaxAge applied
        Assert.Equal([s_hour.AddMinutes(10)], source.Asked);
        Assert.Equal(2, round.Valued);
        Assert.Equal(0, round.Deferred);
        Assert.Equal([81_000m, -81_000m], (await GetEntryAsync(seq)).Postings.Select(p => p.FiatAmount));
    }

    [Fact]
    public async Task Given_ACurrentHourWithoutItsPriceYet_When_ARoundRuns_Then_ItWaitsInsteadOfTakingAnOlderPrice()
    {
        // Arrange: now is 10:30; the source still answers 09:00 for the 10:00 hour
        _time.Set(s_hour.AddMinutes(30));
        await StorePricesAsync((s_hour.AddHours(-1), 81_000m));
        var source = new StubPriceSource(_ => Price(s_hour.AddHours(-1), 81_000m));
        var seq = await AddEntryAsync(s_hour.AddMinutes(10), 1_000);
        await using var service = CreateService(new AccountingPriceOptions(), source);

        // Act
        var early = await service.ValueNowAsync(TestContext.Current.CancellationToken);
        _time.Set(s_hour + PriceValuationService.RecentHourWait);
        var later = await service.ValueNowAsync(TestContext.Current.CancellationToken);

        // Assert: waited while the hour was recent; asked again after the retry delay, then took the older price
        Assert.Equal(1, early.Fetched);
        Assert.Equal(2, early.Deferred);
        Assert.Equal(0, early.Valued);
        Assert.Equal(1, later.Fetched);
        Assert.Equal(2, later.Valued);
        Assert.Equal(2, source.Asked.Count);
        Assert.All((await GetEntryAsync(seq)).Postings, p => Assert.NotNull(p.FiatAmount));
    }

    [Fact]
    public async Task Given_MoreMissingHoursThanTheRoundMayAsk_When_ARoundRuns_Then_OnlyThatManyAreAsked()
    {
        // Arrange
        var source = new StubPriceSource(at => Price(AccountingValuationHour(at), 80_000m));
        for (var i = 0; i < 5; i++)
            await AddEntryAsync(s_hour.AddHours(i), 1_000);
        await using var service = CreateService(new AccountingPriceOptions { MaxFetchesPerRound = 2 }, source);

        // Act
        var first = await service.ValueNowAsync(TestContext.Current.CancellationToken);
        var second = await service.ValueNowAsync(TestContext.Current.CancellationToken);

        // Assert: the hours not asked yet wait for their own price instead of taking an older one
        Assert.Equal(2, first.Fetched);
        Assert.Equal(4, first.Valued);
        Assert.Equal(6, first.Deferred);
        Assert.Equal(2, second.Fetched);
        Assert.Equal(4, second.Valued);
        Assert.Equal(4, source.Asked.Count);
        var prices = await ListPricesAsync();
        Assert.Equal(4, prices.Count);
    }

    [Fact]
    public async Task Given_SourceNone_When_RoundsRun_Then_NoRequestIsMadeAtAllAndImportedPricesStillValue()
    {
        // Arrange: the production registration with a handler that counts what it is asked (and that it was built)
        var handler = new FakePriceHttpHandler();
        var handlerBuilt = 0;
        var options = new AccountingPriceOptions
        {
            Source = AccountingPriceSourceMode.None,
            CsvFile = Path.Combine(Path.GetTempPath(), $"nltg-none-{Guid.NewGuid():N}.csv")
        };
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(MsOptions.Create(options));
        services.AddAccountingPriceSources(_ =>
        {
            handlerBuilt++;
            return handler;
        });
        await using var sources = services.BuildServiceProvider();
        await AddEntryAsync(s_hour.AddMinutes(20), 5_000);
        await AddEntryAsync(s_hour.AddDays(-5), 5_000);
        await using var service = CreateService(options, sources.GetRequiredService<IPriceSource>());

        // Act
        var round = await service.ValueNowAsync(TestContext.Current.CancellationToken);
        service.Start();
        service.Nudge();
        var import = await service.ImportAsync(null, [new AccountingPricePoint(s_hour, 90_000m)],
                                               TestContext.Current.CancellationToken);
        await service.StopAsync();

        // Assert
        Assert.Equal(0, round.Fetched);
        Assert.Equal(4, round.Unpriced);
        Assert.Equal(0, handlerBuilt);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, service.TotalFetched);
        Assert.Equal(1, import.Added);
        Assert.Equal(2, import.Valuation?.Valued);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.FetchAsync(s_hour, s_hour.AddHours(1), TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Given_AClosedPeriod_When_APriceIsFound_Then_ItsPostingsAreNeverFilledAndTheRuleGetsThemOnce()
    {
        // Arrange: September closed; one entry marked closed, one only inside the period's range; one open entry
        var september = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var october = september.AddMonths(1);
        var marked = await AddEntryAsync(september.AddDays(3), 1_000);
        var inRange = await AddEntryAsync(september.AddDays(20), 2_000);
        var open = await AddEntryAsync(s_hour, 3_000);
        await using (var scope = Provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.AccountingPeriodDbRepository.AddAsync(
                new AccountingPeriod("2026-09", september, october, AccountingPeriodState.Closed, october, 2, null,
                                     null, null, false, null), TestContext.Current.CancellationToken);
            await unitOfWork.AccountingBooksDbRepository.MarkEntriesClosedAsync(
                AccountingBook.Financial, "2026-09", september, september.AddDays(10),
                TestContext.Current.CancellationToken);
            await unitOfWork.SaveChangesAsync();
        }

        var sink = new RecordingSink(true);
        var source = new StubPriceSource(at => Price(AccountingValuationHour(at), 70_000m));
        await using var service = CreateService(new AccountingPriceOptions(), source, sink);

        // Act
        var first = await service.ValueNowAsync(TestContext.Current.CancellationToken);
        var second = await service.ValueNowAsync(TestContext.Current.CancellationToken);

        // Assert: the closed postings stay unvalued and the rule saw each once, with its value
        Assert.Equal(4, first.LateValuations);
        Assert.Equal(2, first.Valued);
        Assert.Equal(0, second.LateValuations);
        Assert.Equal(4, sink.Valuations.Count);
        Assert.Equal(4, sink.Valuations.Select(v => v.Posting.Key).Distinct().Count());
        var lateOfMarked = sink.Valuations.Where(v => v.Posting.Key.LedgerSeq == marked).ToList();
        Assert.Equal([0.0007m, -0.0007m], lateOfMarked.Select(v => v.FiatAmount));
        Assert.All(lateOfMarked, v => Assert.Equal("2026-09", v.Posting.ClosedPeriodId));
        Assert.All(sink.Valuations, v => Assert.True(v.Price.Id > 0));
        Assert.All((await GetEntryAsync(marked)).Postings, p => Assert.Null(p.FiatAmount));
        Assert.All((await GetEntryAsync(inRange)).Postings, p => Assert.Null(p.FiatAmount));
        Assert.All((await GetEntryAsync(open)).Postings, p => Assert.NotNull(p.FiatAmount));
        Assert.Equal(2, sink.Entered);
        Assert.Equal(0, sink.Held);
    }

    [Fact]
    public async Task Given_NoAdjustmentRule_When_AClosedPostingHasAPrice_Then_ItStaysUnvaluedAndIsCounted()
    {
        // Arrange
        var september = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var closed = await AddEntryAsync(september.AddDays(1), 1_000);
        await StorePricesAsync((september.AddDays(1).AddHours(-1), 60_000m));
        await using (var scope = Provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.AccountingPeriodDbRepository.AddAsync(
                new AccountingPeriod("2026-09", september, september.AddMonths(1), AccountingPeriodState.Closed,
                                     september.AddMonths(1), 1, null, null, null, false, null),
                TestContext.Current.CancellationToken);
            await unitOfWork.SaveChangesAsync();
        }

        await using var service = CreateService(new AccountingPriceOptions { Source = AccountingPriceSourceMode.None });

        // Act
        var round = await service.ValueNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, round.Valued);
        Assert.Equal(2, round.ClosedLeftUnvalued);
        Assert.All((await GetEntryAsync(closed)).Postings, p => Assert.Null(p.FiatAmount));
    }

    [Fact]
    public async Task Given_AnHourTheSourceHasNoPriceFor_When_RoundsRepeat_Then_ItIsAskedAgainOnlyAfterTheRetryDelay()
    {
        // Arrange
        var source = new StubPriceSource(_ => null);
        await AddEntryAsync(s_hour.AddMinutes(10), 1_000);
        await using var service = CreateService(new AccountingPriceOptions(), source);

        // Act
        await service.ValueNowAsync(TestContext.Current.CancellationToken);
        await service.ValueNowAsync(TestContext.Current.CancellationToken);
        _time.Advance(service.RetryDelay);
        await service.ValueNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, source.Asked.Count);
    }

    [Fact]
    public async Task Given_MorePostingsThanAPageThatCannotBeValued_When_ARoundRuns_Then_LaterPagesAreStillValued()
    {
        // Arrange: three old entries (six postings) with no price, then one with a price; pages of two postings
        for (var i = 0; i < 3; i++)
            await AddEntryAsync(s_hour.AddDays(-10).AddHours(i), 1_000);
        var priced = await AddEntryAsync(s_hour.AddMinutes(1), 1_000);
        await StorePricesAsync((s_hour, 50_000m));
        await using var service = CreateService(new AccountingPriceOptions { Source = AccountingPriceSourceMode.None },
                                                pageSize: 2);

        // Act
        var round = await service.ValueNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(8, round.Listed);
        Assert.Equal(2, round.Valued);
        Assert.Equal(6, round.Unpriced);
        Assert.All((await GetEntryAsync(priced)).Postings, p => Assert.NotNull(p.FiatAmount));
    }

    [Fact]
    public async Task Given_AnImport_When_SomeTimesAreStored_Then_TheyKeepTheirPriceAndTheRestIsStoredAsImport()
    {
        // Arrange
        await StorePricesAsync((s_hour, 1m));
        await using var service = CreateService(new AccountingPriceOptions { Source = AccountingPriceSourceMode.None });

        // Act
        var result = await service.ImportAsync("usd",
                                               [
                                                   new AccountingPricePoint(s_hour, 2m),
                                                   new AccountingPricePoint(s_hour.AddHours(1), 3.123456789m)
                                               ], TestContext.Current.CancellationToken);
        var listed = await service.ListAsync(null, s_hour, null, 10, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("USD", result.Currency);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.AlreadyStored);
        Assert.Equal([1m, 3.12345679m], listed.Select(p => p.Price));
        Assert.Equal(AccountingPriceSource.Import, listed[1].Source);
    }

    [Fact]
    public async Task Given_AnImportWithABadRow_When_Imported_Then_NothingIsStored()
    {
        // Arrange
        await using var service = CreateService(new AccountingPriceOptions { Source = AccountingPriceSourceMode.None });

        // Act
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => service.ImportAsync(null,
                                      [
                                          new AccountingPricePoint(s_hour, 2m),
                                          new AccountingPricePoint(s_hour.AddHours(1), -3m)
                                      ], TestContext.Current.CancellationToken));

        // Assert
        Assert.StartsWith("Price 2:", error.Message);
        Assert.Empty(await ListPricesAsync());
    }

    [Fact]
    public async Task Given_AFetch_When_SomeHoursAreStored_Then_OnlyTheOthersAreAskedAndStored()
    {
        // Arrange: 10:00 stored, 11:00 and 12:00 missing, 12:00 unavailable
        await StorePricesAsync((s_hour.AddMinutes(15), 1m));
        var source = new StubPriceSource(at => at == s_hour.AddHours(2) ? null : Price(at, 2m));
        await using var service = CreateService(new AccountingPriceOptions(), source);

        // Act
        var result = await service.FetchAsync(s_hour.AddMinutes(30), s_hour.AddHours(3),
                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(s_hour, result.Since);
        Assert.Equal(3, result.Hours);
        Assert.Equal(1, result.AlreadyCovered);
        Assert.Equal(2, result.Requested);
        Assert.Equal(1, result.Stored);
        Assert.Equal(1, result.Unavailable);
        Assert.Equal([s_hour.AddHours(1), s_hour.AddHours(2)], source.Asked);
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.FetchAsync(s_hour, s_hour.AddDays(32), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.FetchAsync(s_hour, s_hour, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_AFetchedPriceFarFromAStoredOne_When_Fetched_Then_ItIsRefusedAndNeverStored()
    {
        // Arrange - NL-678: 10:00 stored; the source answers 100 times the price at 11:00 (a decimal-point mistake)
        // and a real move at 12:00
        await StorePricesAsync((s_hour, 86_000m));
        var source = new StubPriceSource(at => Price(at, at == s_hour.AddHours(1) ? 8_600_000m : 87_000m));
        await using var service = CreateService(new AccountingPriceOptions(), source);

        // Act
        var result = await service.FetchAsync(s_hour, s_hour.AddHours(3), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.Requested);
        Assert.Equal(1, result.Stored);
        Assert.Equal(1, result.Unavailable);
        Assert.Equal([(s_hour, 86_000m), (s_hour.AddHours(2), 87_000m)],
                     (await ListPricesAsync()).Select(p => (p.Time, p.Price)));
    }

    [Fact]
    public async Task Given_AFetchedPriceFarFromALaterStoredOne_When_ARoundRuns_Then_ItIsRefusedAndThePostingWaits()
    {
        // Arrange - NL-678: a posting at 11:30, only a later stored price (13:00); the source answers a hundredth of it
        await StorePricesAsync((s_hour.AddHours(3), 86_000m));
        var seq = await AddEntryAsync(s_hour.AddHours(1).AddMinutes(30), 1_000);
        var source = new StubPriceSource(at => Price(AccountingValuationHour(at), 860m));
        await using var service = CreateService(new AccountingPriceOptions(), source);

        // Act
        var round = await service.ValueNowAsync(TestContext.Current.CancellationToken);

        // Assert: asked, refused, nothing stored, the posting still unvalued
        Assert.Equal(1, round.Fetched);
        Assert.Equal(0, round.Stored);
        Assert.Equal(0, round.Valued);
        Assert.Single(await ListPricesAsync());
        Assert.All((await GetEntryAsync(seq)).Postings, p => Assert.Null(p.FiatAmount));
    }

    [Fact]
    public async Task Given_TheSanityBoundOff_When_AFarPriceIsFetched_Then_ItIsStored()
    {
        // Arrange
        await StorePricesAsync((s_hour, 86_000m));
        var source = new StubPriceSource(at => Price(at, 8_600_000m));
        await using var service = CreateService(new AccountingPriceOptions { MaxPriceJumpFactor = 0 }, source);

        // Act
        var result = await service.FetchAsync(s_hour, s_hour.AddHours(2), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, result.Stored);
    }

    [Fact]
    public async Task Given_TheBooksOff_When_AskedToValue_Then_NothingRuns()
    {
        // Arrange
        var source = new StubPriceSource(at => Price(at, 2m));
        await AddEntryAsync(s_hour, 1_000);
        await using var service = CreateService(new AccountingPriceOptions(), source, booksEnabled: false);

        // Act
        service.Start();
        var round = await service.ValueNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(service.IsEnabled);
        Assert.Same(AccountingValuationRoundResult.Empty, round);
        Assert.Empty(source.Asked);
    }

    [Fact]
    public async Task Given_TheLoopStarted_When_ItsFirstRoundRuns_Then_ThePostingsAreValued()
    {
        // Arrange
        var source = new StubPriceSource(at => Price(AccountingValuationHour(at), 2m));
        var seq = await AddEntryAsync(s_hour, 1_000);
        await using var service = CreateService(new AccountingPriceOptions(), source);

        // Act
        service.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.TotalValued < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        await service.StopAsync();

        // Assert
        Assert.Equal(2, service.TotalValued);
        Assert.All((await GetEntryAsync(seq)).Postings, p => Assert.NotNull(p.FiatAmount));
    }

    private static DateTimeOffset AccountingValuationHour(DateTimeOffset at) => AccountingValuation.HourStart(at);

    private static AccountingPrice Price(DateTimeOffset time, decimal price) =>
        new(0, "USD", time, price, AccountingPriceSource.Http, time);

    private PriceValuationService CreateService(AccountingPriceOptions options, IPriceSource? source = null,
                                                IAccountingAdjustmentSink? sink = null, bool booksEnabled = true,
                                                int pageSize = PriceValuationService.DefaultPageSize) =>
        new(Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PriceValuationService>.Instance,
            MsOptions.Create(new AccountingOptions { Enabled = booksEnabled }), MsOptions.Create(options), source,
            sink, _time)
        {
            PageSize = pageSize
        };

    /// <summary>A balanced financial entry of <paramref name="msat"/> (an asset line and an income line), unvalued.</summary>
    private async Task<long> AddEntryAsync(DateTimeOffset at, long msat)
    {
        var seq = _nextSeq++;
        await using var scope = Provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.AccountingBooksDbRepository.AddEntryAsync(
            new AccountingEntry(seq, $"inv:{seq}:settled", AccountingEventKind.InvoiceSettled, at, null, null,
                                [
                                    new AccountingPosting(AccountRole.Channels, msat)
                                    {
                                        AccountName = "assets:lightning"
                                    },
                                    new AccountingPosting(AccountRole.Received, -msat)
                                    {
                                        AccountName = "income:sales"
                                    }
                                ])
            {
                Book = AccountingBook.Financial,
                Flags = AccountingEntryFlags.Unvalued
            }, TestContext.Current.CancellationToken);
        await unitOfWork.SaveChangesAsync();
        return seq;
    }

    private async Task StorePricesAsync(params (DateTimeOffset Time, decimal Price)[] prices)
    {
        await using var scope = Provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var (time, price) in prices)
        {
            await unitOfWork.AccountingPriceDbRepository.TryAddAsync(
                new AccountingPrice(0, "USD", time, price, AccountingPriceSource.Csv, time),
                TestContext.Current.CancellationToken);
        }

        await unitOfWork.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<AccountingPrice>> ListPricesAsync()
    {
        await using var scope = Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingPriceDbRepository
                          .ListAsync("USD", null, null, 100, TestContext.Current.CancellationToken);
    }

    private async Task<AccountingEntry> GetEntryAsync(long seq)
    {
        await using var scope = Provider.CreateAsyncScope();
        var entries = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository
                                 .GetEntriesByKeyAsync(AccountingBook.Financial, $"inv:{seq}:settled",
                                                       TestContext.Current.CancellationToken);
        return Assert.Single(entries);
    }

    private async Task<IReadOnlyList<AccountingAccountBalance>> GetBalancesAsync()
    {
        await using var scope = Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository
                          .GetAccountBalancesAsync(AccountingBook.Financial, TestContext.Current.CancellationToken);
    }

    private static ServiceProvider BuildProvider(string databasePath)
    {
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Database:Provider"] = "sqlite",
                               ["Database:ConnectionString"] = $"Data Source={databasePath}"
                           })
                           .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISha256, Sha256>();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        return services.BuildServiceProvider();
    }

    private sealed class StubPriceSource(Func<DateTimeOffset, AccountingPrice?> answer) : IPriceSource
    {
        private readonly Lock _gate = new();
        private readonly List<DateTimeOffset> _asked = [];

        public IReadOnlyList<DateTimeOffset> Asked
        {
            get
            {
                lock (_gate)
                    return _asked.ToList();
            }
        }

        public Task<AccountingPrice?> GetPriceAsync(string currency, DateTimeOffset time,
                                                    CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _asked.Add(time);
            return Task.FromResult(answer(time));
        }
    }

    private sealed class RecordingSink(bool take) : IAccountingAdjustmentSink
    {
        public List<AccountingLateValuation> Valuations { get; } = [];

        public int Entered { get; private set; }

        public int Held { get; private set; }

        public Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
        {
            Entered++;
            Held++;
            return Task.FromResult<IDisposable>(new Release(this));
        }

        public Task<AccountingPeriod?> GetLockingPeriodAsync(DateTimeOffset time,
                                                             CancellationToken cancellationToken = default) =>
            Task.FromResult<AccountingPeriod?>(null);

        public Task<AccountingEntry?> StageAdjustmentAsync(IUnitOfWork unitOfWork, AccountingAdjustment adjustment,
                                                           CancellationToken cancellationToken = default) =>
            Task.FromResult<AccountingEntry?>(null);

        public Task<bool> AdjustLateValuationAsync(IUnitOfWork unitOfWork, AccountingLateValuation valuation,
                                                   CancellationToken cancellationToken = default)
        {
            // Only under the write lock
            Assert.Equal(1, Held);
            Valuations.Add(valuation);
            return Task.FromResult(take);
        }

        private sealed class Release(RecordingSink sink) : IDisposable
        {
            public void Dispose() => sink.Held--;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;

        public void Set(DateTimeOffset now) => _now = now;
    }
}