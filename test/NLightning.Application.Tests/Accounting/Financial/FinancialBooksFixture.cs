using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Accounting.Financial;

using Application.Accounting;
using Application.Accounting.Export.Financial;
using Application.Accounting.Reports.Financial;
using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Crypto.Hashes;
using Domain.Persistence.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;

/// <summary>
/// A financial book seeded by hand on a SQLite file through the production unit of work (NL-602 A3-T6): prices, ten
/// sealed feed events, the financial entries of an opening balance, a deposit, a channel open, an invoice, a payment
/// with two reliefs and a realized gain, an unvalued forward, an unclassified payment and its reclassifying adjustment, a
/// withdrawal a year later (long-term gain), a payment whose lines leave a rounding residue, and a failed payment with no
/// postings; the lots and reliefs of FIFO over the pooled lots. Every amount is computed by hand in the comments.
/// </summary>
/// <remarks>
/// <para>Prices (USD per BTC, so per msat over 10^11): P0 2025-12-31 30,000; P1 2026-01-01 40,000; P2 2026-02-10 50,000;
/// P3 2026-03-01 60,000; P4 2027-03-20 100,000; P5 2027-03-21 33,333.33333333.</para>
/// <para>Lots: L1 deposit 1e9 msat for 400 (P1); L2 invoice 2e8 msat for 100 (P2); L3 forward fee 5,000 msat, unvalued;
/// L4 opening 5e7 msat for 15 (P0, estimated). Reliefs: R1 L1 1e6 (the funding fee, 0.4 / 0.4), R2 L4 5e7 (15 / 30),
/// R3 L1 50,010,000 (20.004 / 30.006), R4 L1 1e6 (0.4 / proceeds unvalued), R5 L1 300,200,000 (120.08 / 300.2, held
/// more than a year). Open: L1 647,790,000 (cost 259.116), L2, L3.</para>
/// </remarks>
public sealed class FinancialBooksFixture : IAsyncDisposable
{
    public const string Usd = "USD";

    public static readonly DateTimeOffset Now = new(2027, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"nltg-financial-{Guid.NewGuid():N}.db");

    private ServiceProvider? _provider;

    private FinancialBooksFixture()
    {
    }

    public ServiceProvider Provider => _provider ?? throw new InvalidOperationException("Not initialized");

    public Mock<IAccountingBooks> Books { get; } = new();

    public Mock<IFinancialBooksProjector> Projection { get; } = new();

    public FixedTime Clock { get; } = new(Now);

    /// <summary>The stored prices' ids, P0 to P5.</summary>
    public long[] PriceIds { get; private set; } = [];

    /// <summary>A migrated, seeded database.</summary>
    public static async Task<FinancialBooksFixture> CreateAsync(bool seed = true)
    {
        var fixture = new FinancialBooksFixture();
        fixture._provider = BuildProvider(fixture._databasePath);
        fixture.Books.SetupGet(b => b.IsEnabled).Returns(true);
        fixture.Projection.SetupGet(p => p.IsEnabled).Returns(true);
        await using (var scope = fixture.Provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database.MigrateAsync();

        if (seed)
            await fixture.SeedAsync();
        return fixture;
    }

    public AccountingFinancialReportService CreateReports(bool withProjection = true,
                                                          INodeSnapshotSource? snapshotSource = null) =>
        new(Provider.GetRequiredService<IServiceScopeFactory>(), Books.Object,
            NullLogger<AccountingFinancialReportService>.Instance, Options.Create(new AccountingOptions()), null,
            withProjection ? Projection.Object : null, snapshotSource, Clock);

    public AccountingFinancialExportService CreateExports() =>
        new(Provider.GetRequiredService<IServiceScopeFactory>(), Books.Object,
            NullLogger<AccountingFinancialExportService>.Instance, null, Projection.Object);

    /// <summary>Runs <paramref name="action"/> on a fresh unit of work and saves it.</summary>
    public async Task WriteAsync(Func<IUnitOfWork, Task> action)
    {
        await using var scope = Provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await action(unitOfWork);
        await unitOfWork.SaveChangesAsync();
    }

    /// <summary>Reads through a fresh unit of work.</summary>
    public async Task<T> ReadAsync<T>(Func<IUnitOfWork, Task<T>> read)
    {
        await using var scope = Provider.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    /// <summary>Every financial entry, in (sequence, adjustment) order.</summary>
    public Task<IReadOnlyList<AccountingEntry>> ReadEntriesAsync() =>
        ReadAsync(u => u.AccountingBooksDbRepository.ListEntriesAsync(
                      new AccountingEntryQuery(0, 1_000) { Book = AccountingBook.Financial }));

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

    public static DateTimeOffset At(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>A financial line.</summary>
    public static AccountingPosting Line(AccountRole role, string name, long msat, decimal? fiat = null,
                                         long? priceId = null) =>
        new(role, msat)
        {
            AccountName = name,
            FiatAmount = fiat,
            FiatCurrency = fiat is null ? null : Usd,
            PriceId = fiat is null ? null : priceId
        };

    private async Task SeedAsync()
    {
        // Prices first: a posting keeps the id the save assigned
        (DateTimeOffset Time, decimal Price)[] prices =
        [
            (At(2025, 12, 31), 30_000m), (At(2026, 1, 1), 40_000m), (At(2026, 2, 10), 50_000m),
            (At(2026, 3, 1), 60_000m), (At(2027, 3, 20), 100_000m), (At(2027, 3, 21), 33_333.33333333m)
        ];
        await WriteAsync(async u =>
        {
            foreach (var (time, price) in prices)
                await u.AccountingPriceDbRepository.TryAddAsync(
                    new AccountingPrice(0, Usd, time, price, AccountingPriceSource.Csv, Now));
        });
        var stored = await ReadAsync(u => u.AccountingPriceDbRepository.ListAsync(Usd, null, null, 100));
        PriceIds = stored.OrderBy(p => p.Time).Select(p => p.Id).ToArray();
        var (p0, p1, p2, p3, p4, p5) = (PriceIds[0], PriceIds[1], PriceIds[2], PriceIds[3], PriceIds[4], PriceIds[5]);

        // The feed's events (sealed: the exports' descriptions come from them)
        AccountingEventKind[] kinds =
        [
            AccountingEventKind.OpeningBalance, AccountingEventKind.WalletReceived, AccountingEventKind.ChannelFunded,
            AccountingEventKind.InvoiceSettled, AccountingEventKind.PaymentSucceeded,
            AccountingEventKind.ForwardSettled, AccountingEventKind.PaymentSucceeded,
            AccountingEventKind.WalletSent, AccountingEventKind.PaymentSucceeded, AccountingEventKind.PaymentFailed
        ];
        DateTimeOffset[] times =
        [
            At(2025, 12, 31, 12), At(2026, 1, 1, 0, 10), At(2026, 1, 5), At(2026, 2, 10, 12), At(2026, 3, 2),
            At(2026, 3, 5), At(2026, 3, 6), At(2027, 3, 20, 6), At(2027, 3, 21, 6), At(2027, 3, 22)
        ];
        await WriteAsync(u =>
        {
            for (var i = 0; i < kinds.Length; i++)
            {
                var details = new Dictionary<string, string>();
                if (i == 3)
                {
                    details["label"] = "shop";
                    details["description"] = "Coffee; \"large\"\nwith milk";
                }

                u.AccountingEventDbRepository.Add(new AccountingEventModel
                {
                    EventKey = Key(i + 1),
                    Kind = kinds[i],
                    OccurredAt = times[i],
                    Details = details
                });
            }

            return Task.CompletedTask;
        });
        await using (var sealer = new AccountingEventSealerService(
                         Provider.GetRequiredService<IServiceScopeFactory>(),
                         NullLogger<AccountingEventSealerService>.Instance, Options.Create(new AccountingOptions())))
            await sealer.SealNowAsync();

        await WriteAsync(async u =>
        {
            var books = u.AccountingBooksDbRepository;
            var lots = u.AccountingLotDbRepository;

            // 1 OpeningBalance: 5e7 msat at P0 = 15 (lot L4, estimated basis)
            await AddAsync(books, 1, AccountingEventKind.OpeningBalance, times[0],
                           Line(AccountRole.Wallet, "assets:onchain:wallet", 50_000_000, 15m, p0),
                           Line(AccountRole.Opening, "equity:opening-balances", -50_000_000, -15m, p0));

            // 2 WalletReceived: 1e9 msat at P1 = 400 (lot L1)
            await AddAsync(books, 2, AccountingEventKind.WalletReceived, times[1],
                           Line(AccountRole.Wallet, "assets:onchain:wallet", 1_000_000_000, 400m, p1),
                           Line(AccountRole.TransfersIn, "equity:transfers:in", -1_000_000_000, -400m, p1));

            // 3 ChannelFunded: 5e8 moved to the channel (a transfer), the 1e6 fee disposed of at P1 (0.4, no gain)
            await AddAsync(books, 3, AccountingEventKind.ChannelFunded, times[2],
                           Line(AccountRole.Channels, "assets:lightning:channels", 500_000_000, 200m, p1),
                           Line(AccountRole.FeeFunding, "expenses:fees:funding", 1_000_000, 0.4m, p1),
                           Line(AccountRole.Wallet, "assets:onchain:wallet", -501_000_000, -200.4m));

            // 4 InvoiceSettled: 2e8 msat income at fair value P2 = 100 (lot L2), classified by rule 7
            await AddAsync(books, 4, AccountingEventKind.InvoiceSettled, times[3],
                           [
                               Line(AccountRole.Channels, "assets:lightning:channels", 200_000_000, 100m, p2),
                               Line(AccountRole.Received, "income:sales", -200_000_000, -100m, p2)
                           ], classification: AccountingClassificationSource.Rule, ruleId: 7);

            // 5 PaymentSucceeded: 1e8 + 1e4 fee at P3 (60 + 0.006); relieves L4 5e7 (cost 15) and L1 50,010,000 (cost
            // 20.004): cost 35.004, gain 25.002
            await AddAsync(books, 5, AccountingEventKind.PaymentSucceeded, times[4],
                           Line(AccountRole.Sent, "expenses:payments", 100_000_000, 60m, p3),
                           Line(AccountRole.RoutingFees, "expenses:fees:routing", 10_000, 0.006m, p3),
                           Line(AccountRole.Channels, "assets:lightning:channels", -100_010_000, -35.004m),
                           Line(AccountRole.Channels, "income:gains:realized", 0, -25.002m));

            // 6 ForwardSettled: 5,000 msat fee earned, no price yet (lot L3 unvalued)
            await AddAsync(books, 6, AccountingEventKind.ForwardSettled, times[5],
                           [
                               Line(AccountRole.Channels, "assets:lightning:channels", 5_000),
                               Line(AccountRole.Routing, "income:routing", -5_000)
                           ], AccountingEntryFlags.Unvalued, AccountingClassificationSource.Default);

            // 7 PaymentSucceeded: 1e6 msat, unvalued and unclassified; relieves L1 1e6 (cost 0.4, proceeds unvalued)
            await AddAsync(books, 7, AccountingEventKind.PaymentSucceeded, times[6],
                           [
                               Line(AccountRole.Sent, "expenses:unclassified", 1_000_000),
                               Line(AccountRole.Channels, "assets:lightning:channels", -1_000_000)
                           ], AccountingEntryFlags.Unvalued | AccountingEntryFlags.Unclassified,
                           AccountingClassificationSource.Default);

            // 7:1 its reclassification by an override, dated when it was made
            await books.AddEntryAsync(new AccountingEntry(7, Key(7), AccountingEventKind.PaymentSucceeded,
                                                          At(2026, 3, 20), null, null,
                                                          [
                                                              Line(AccountRole.Sent, "expenses:payments", 1_000_000),
                                                              Line(AccountRole.Sent, "expenses:unclassified",
                                                                   -1_000_000)
                                                          ])
            {
                Book = AccountingBook.Financial,
                Adjustment = 1,
                Flags = AccountingEntryFlags.Adjustment | AccountingEntryFlags.Unvalued,
                Classification = AccountingClassificationSource.Override
            });

            // 8 WalletSent: 3e8 out at P4 = 300 + fee 2e5 = 0.2; relieves L1 300,200,000 (cost 120.08): gain 180.12,
            // long term (L1 is from 2026-01-01)
            await AddAsync(books, 8, AccountingEventKind.WalletSent, times[7],
                           Line(AccountRole.TransfersOut, "equity:transfers:out", 300_000_000, 300m, p4),
                           Line(AccountRole.FeeWithdraw, "expenses:fees:withdraw", 200_000, 0.2m, p4),
                           Line(AccountRole.Wallet, "assets:onchain:wallet", -300_200_000, -120.08m),
                           Line(AccountRole.Wallet, "income:gains:realized", 0, -180.12m));

            // 9 PaymentSucceeded of 1 + 1 msat at P5, each line valued on its own: 0.00000033 + 0.00000033 against
            // 0.00000067 leaves -0.00000001
            await AddAsync(books, 9, AccountingEventKind.PaymentSucceeded, times[8],
                           Line(AccountRole.Sent, "expenses:payments", 1, 0.00000033m, p5),
                           Line(AccountRole.RoutingFees, "expenses:fees:routing", 1, 0.00000033m, p5),
                           Line(AccountRole.Channels, "assets:lightning:channels", -2, -0.00000067m, p5));

            // 10 PaymentFailed: nothing
            await AddAsync(books, 10, AccountingEventKind.PaymentFailed, times[9]);
            await books.SetCursorAsync(AccountingBook.Financial, 10);

            var l1 = await lots.AddLotAsync(Lot(At(2026, 1, 1, 0, 10), AccountingLotOrigin.Acquisition, 2,
                                                1_000_000_000, 647_790_000, 400m, p1));
            var l2 = await lots.AddLotAsync(Lot(At(2026, 2, 10, 12), AccountingLotOrigin.Acquisition, 4, 200_000_000,
                                                200_000_000, 100m, p2));
            await lots.AddLotAsync(Lot(At(2026, 3, 5), AccountingLotOrigin.Acquisition, 6, 5_000, 5_000, null, null));
            var l4 = await lots.AddLotAsync(Lot(At(2025, 12, 31, 12), AccountingLotOrigin.Opening, 1, 50_000_000, 0,
                                                15m, p0, basisEstimated: true));
            Assert.Equal((1, 2, 4), (l1, l2, l4));

            lots.AddRelief(new AccountingLotRelief(0, l1, 3, 0, times[2], 1_000_000, 0.4m, 0.4m, null));
            lots.AddRelief(new AccountingLotRelief(0, l4, 5, 0, times[4], 50_000_000, 15m, 30m, null));
            lots.AddRelief(new AccountingLotRelief(0, l1, 5, 0, times[4], 50_010_000, 20.004m, 30.006m, null));
            lots.AddRelief(new AccountingLotRelief(0, l1, 7, 0, times[6], 1_000_000, 0.4m, null, null));
            lots.AddRelief(new AccountingLotRelief(0, l1, 8, 0, times[7], 300_200_000, 120.08m, 300.2m, null));
        });
    }

    public static string Key(long seq) => $"fixture:{seq}";

    private static AccountingLot Lot(DateTimeOffset at, AccountingLotOrigin origin, long seq, long original,
                                     long remaining, decimal? cost, long? priceId, bool basisEstimated = false) =>
        new(0, at, origin, seq, 0, null, null, original, remaining, cost, cost is null ? null : Usd, priceId,
            basisEstimated, null);

    private static Task AddAsync(IAccountingBooksDbRepository books, long seq, AccountingEventKind kind,
                                 DateTimeOffset at, params AccountingPosting[] postings) =>
        AddAsync(books, seq, kind, at, postings, AccountingEntryFlags.None, AccountingClassificationSource.Default);

    private static Task AddAsync(IAccountingBooksDbRepository books, long seq, AccountingEventKind kind,
                                 DateTimeOffset at, AccountingPosting[] postings,
                                 AccountingEntryFlags flags = AccountingEntryFlags.None,
                                 AccountingClassificationSource classification = AccountingClassificationSource.Default,
                                 long? ruleId = null) =>
        books.AddEntryAsync(new AccountingEntry(seq, Key(seq), kind, at, null, null, postings)
        {
            Book = AccountingBook.Financial,
            Flags = flags,
            Classification = classification,
            RuleId = ruleId
        });

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

    /// <summary>A clock that stands still.</summary>
    public sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}