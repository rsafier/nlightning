using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Accounting.Financial;

using Application.Accounting;
using Application.Accounting.Books;
using Application.Accounting.Financial;
using Application.Accounting.Prices;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Lots;
using Domain.Accounting.Models;
using Domain.Accounting.Prices;
using Domain.Accounting.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using TestUtils;

/// <summary>
/// A node's accounting on a SQLite file for the financial projector (NL-602 A3-T4): the real sealer, the operational
/// books with the production posting rules, <see cref="FinancialBooksProjector"/>, the period close
/// (<see cref="AccountingPeriodService"/>, the lock) with a real <see cref="LocalLightningSigner"/>, the back-valuation
/// (<see cref="PriceValuationService"/> with no source: it values with stored prices) and a settable clock.
/// </summary>
internal sealed class FinancialProjectorTestKit : IAsyncDisposable
{
    public const string Usd = "USD";

    private static readonly byte[] s_nodePrivateKey =
        Convert.FromHexString("3333333333333333333333333333333333333333333333333333333333333333");

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"nltg-financial-projector-{Guid.NewGuid():N}.db");

    private readonly ServiceProvider _provider;
    private int _txCounter;

    private FinancialProjectorTestKit(DateTimeOffset now, AccountingCostBasisMethod method, AccountingProfile profile,
                                      int pageSize)
    {
        Clock = new SettableTimeProvider(now);
        _provider = BuildProvider(_databasePath);
        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new AccountingOptions
        {
            SealInterval = TimeSpan.FromHours(1),
            SealBatchSize = pageSize,
            Profile = profile,
            CostBasis = method
        });
        var priceOptions = Options.Create(new AccountingPriceOptions { Source = AccountingPriceSourceMode.None });
        Sealer = new AccountingEventSealerService(scopeFactory, NullLogger<AccountingEventSealerService>.Instance,
                                                  options);
        Books = new AccountingBooksService(scopeFactory, NullLogger<AccountingBooksService>.Instance, options, Sealer);
        Signer = CreateSigner();
        Projector = new FinancialBooksProjector(scopeFactory, NullLogger<FinancialBooksProjector>.Instance, options,
                                                priceOptions, () => Periods, false, Clock, () => CatchingUp);
        Periods = new AccountingPeriodService(scopeFactory, NullLogger<AccountingPeriodService>.Instance, Books,
                                              Projector, Signer, Clock);
        Valuation = new PriceValuationService(scopeFactory, NullLogger<PriceValuationService>.Instance, options,
                                              priceOptions, null, Periods, Clock);
        Classification = new AccountingClassificationService(scopeFactory,
                                                             NullLogger<AccountingClassificationService>.Instance,
                                                             options, Books, Sealer, Clock, Periods);
    }

    public SettableTimeProvider Clock { get; }

    /// <summary>What the projector is told of the back-valuation (NL-658): catching up over old history.</summary>
    public bool CatchingUp { get; set; }
    public AccountingEventSealerService Sealer { get; }
    public AccountingBooksService Books { get; }
    public ILightningSigner Signer { get; }
    public FinancialBooksProjector Projector { get; }
    public AccountingPeriodService Periods { get; }
    public PriceValuationService Valuation { get; }
    public AccountingClassificationService Classification { get; }

    public static async Task<FinancialProjectorTestKit> CreateAsync(
        DateTimeOffset now, AccountingCostBasisMethod method = AccountingCostBasisMethod.Fifo,
        AccountingProfile profile = AccountingProfile.Financial, int pageSize = 500)
    {
        var kit = new FinancialProjectorTestKit(now, method, profile, pageSize);
        using var scope = kit._provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                   .MigrateAsync(TestContext.Current.CancellationToken);
        return kit;
    }

    public IServiceScope CreateScope() => _provider.CreateScope();

    /// <summary>Writes the events (one save each, so the sealer keeps their order).</summary>
    public async Task AddAsync(params AccountingEventModel[] events)
    {
        foreach (var accountingEvent in events)
        {
            using var scope = CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            unitOfWork.AccountingEventDbRepository.Add(accountingEvent);
            await unitOfWork.SaveChangesAsync();
        }
    }

    /// <summary>Seals and projects the operational book, then the financial one.</summary>
    public async Task<int> ProjectAsync()
    {
        await Books.ProjectNowAsync(TestContext.Current.CancellationToken);
        return await Projector.ProjectAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Stores prices (USD per BTC) at the given times.</summary>
    public async Task AddPricesAsync(params (DateTimeOffset Time, decimal Price)[] prices)
    {
        using var scope = CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var (time, price) in prices)
            await unitOfWork.AccountingPriceDbRepository.TryAddAsync(
                new AccountingPrice(0, Usd, time, price, AccountingPriceSource.Csv, time),
                TestContext.Current.CancellationToken);
        await unitOfWork.SaveChangesAsync();
    }

    public async Task<T> ReadAsync<T>(Func<IUnitOfWork, Task<T>> read)
    {
        using var scope = CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    /// <summary>Every entry of a book, (sequence, adjustment) order.</summary>
    public Task<IReadOnlyList<AccountingEntry>> ListEntriesAsync(AccountingBook book) =>
        ReadAsync(u => u.AccountingBooksDbRepository.ListEntriesAsync(
                      new AccountingEntryQuery(0, 10_000) { Book = book, AfterAdjustment = -1 },
                      TestContext.Current.CancellationToken));

    /// <summary>Every sealed event by ledger sequence.</summary>
    public async Task<IReadOnlyDictionary<long, AccountingEventModel>> EventsBySeqAsync()
    {
        var events = await ReadAsync(u => u.AccountingEventDbRepository.ListAsync(new AccountingEventQuery(0, 10_000),
                                                                             TestContext.Current.CancellationToken));
        return events.ToDictionary(e => e.LedgerSeq!.Value);
    }

    /// <summary>Every stored price.</summary>
    public Task<IReadOnlyList<AccountingPrice>> ListPricesAsync() =>
        ReadAsync(u => u.AccountingPriceDbRepository.ListAsync(Usd, null, null, 10_000,
                                                               TestContext.Current.CancellationToken));

    /// <summary>Every lot (acquisition order) with its reliefs.</summary>
    public async Task<IReadOnlyList<(AccountingLot Lot, IReadOnlyList<AccountingLotRelief> Reliefs)>> ListLotsAsync()
    {
        using var scope = CreateScope();
        var lots = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingLotDbRepository;
        var all = await lots.ListLotsAcquiredBeforeAsync(DateTimeOffset.MaxValue, 0, 10_000,
                                                         TestContext.Current.CancellationToken);
        var result = new List<(AccountingLot, IReadOnlyList<AccountingLotRelief>)>();
        foreach (var lot in all.OrderBy(l => l.AcquiredAt).ThenBy(l => l.SourceLedgerSeq).ThenBy(l => l.Id))
            result.Add((lot, await lots.ListReliefsByLotAsync(lot.Id, TestContext.Current.CancellationToken)));
        return result;
    }

    /// <summary>
    /// The whole financial book as text ("rebuilt equals incremental"): entries with postings, balances, cursor, lots
    /// in acquisition order with their reliefs (ids left out: a replayed lot or relief gets a new one).
    /// </summary>
    public async Task<string> SnapshotFinancialAsync()
    {
        var lines = new List<string>();
        foreach (var entry in await ListEntriesAsync(AccountingBook.Financial))
            lines.Add(Describe(entry));

        using (var scope = CreateScope())
        {
            var books = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository;
            // A rollback leaves an emptied account's row at zero: only the accounts with a balance count
            foreach (var balance in await books.GetAccountBalancesAsync(AccountingBook.Financial,
                                                                        TestContext.Current.CancellationToken))
            {
                if (balance.BalanceMsat != 0 || balance.FiatAmount != 0m)
                    lines.Add($"B {balance.Account} {balance.AccountName} {balance.BalanceMsat} "
                            + AccountingClosingState.FormatFiat(balance.FiatAmount));
            }

            lines.Add($"C {await books.GetCursorAsync(AccountingBook.Financial, TestContext.Current.CancellationToken)}");
        }

        foreach (var (lot, reliefs) in await ListLotsAsync())
        {
            lines.Add($"L {lot.AcquiredAt.UtcTicks} {lot.Origin} {lot.SourceLedgerSeq}/{lot.SourceAdjustment} "
                    + $"{lot.OriginalMsat} {lot.RemainingMsat} {lot.FiatCost} {lot.FiatCurrency} {lot.BasisEstimated} "
                    + $"{lot.ClosedPeriodId}");
            foreach (var relief in reliefs.OrderBy(r => r.LedgerSeq).ThenBy(r => r.Adjustment))
                lines.Add($"  R {relief.LedgerSeq}/{relief.Adjustment} {relief.RelievedAt.UtcTicks} {relief.Msat} "
                        + $"{relief.FiatCostRelieved} {relief.Proceeds} {relief.ClosedPeriodId}");
        }

        return string.Join('\n', lines);
    }

    /// <summary>The operational book as text (its entries, balances and cursor).</summary>
    public async Task<string> SnapshotOperationalAsync()
    {
        var lines = new List<string>();
        foreach (var entry in await ListEntriesAsync(AccountingBook.Operational))
            lines.Add(Describe(entry));

        using var scope = CreateScope();
        var books = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository;
        foreach (var balance in await books.GetAccountBalancesAsync(AccountingBook.Operational,
                                                                    TestContext.Current.CancellationToken))
            lines.Add($"B {balance.Account} {balance.AccountName} {balance.BalanceMsat} {balance.FiatAmount}");
        lines.Add($"C {await books.GetCursorAsync(TestContext.Current.CancellationToken)}");
        return string.Join('\n', lines);
    }

    public static string Describe(AccountingEntry entry) =>
        string.Create(CultureInfo.InvariantCulture,
                      $"E {entry.LedgerSeq}/{entry.Adjustment} {entry.EventKey} {entry.Kind} "
                    + $"{entry.OccurredAt.UtcTicks} {entry.Flags} {entry.Classification} {entry.RuleId} "
                    + $"{entry.ClosedPeriodId} {entry.Note} [")
      + string.Join(", ", entry.Postings.Select(p => $"{p.Account}:{p.AccountName}:{p.AmountMsat}:{p.FiatAmount}:"
                                                   + $"{p.FiatCurrency}"))
      + "]";

    public async ValueTask DisposeAsync()
    {
        await Valuation.DisposeAsync();
        await Projector.DisposeAsync();
        Periods.Dispose();
        await Books.DisposeAsync();
        await Sealer.DisposeAsync();
        await _provider.DisposeAsync();
        SqliteTestPools.Clear(_databasePath);
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

    #region Events

    public static AccountingEventModel Opening(string bucket, long msat, DateTimeOffset at) =>
        Event(AccountingEventKeys.OpeningBalance(bucket), AccountingEventKind.OpeningBalance, at, msat, 0,
              (AccountingDetailKeys.Bucket, bucket.StartsWith("channel", StringComparison.Ordinal)
                                                ? AccountingDetailKeys.ChannelBucket
                                                : bucket));

    public static AccountingEventModel Cutover(DateTimeOffset at) =>
        Event(AccountingEventKeys.Cutover(), AccountingEventKind.OpeningBalance, at, 0);

    public AccountingEventModel Deposit(long msat, DateTimeOffset at, string? label = null) =>
        Event($"wallet:{NextTx()}:0:in", AccountingEventKind.WalletReceived, at, msat, 0,
              (AccountingDetailKeys.Source, AccountingDetailKeys.ExternalSource), (AccountingDetailKeys.Label, label));

    /// <summary>A wallet output of one of our own transactions (change, a close's output).</summary>
    public AccountingEventModel WalletIn(long msat, DateTimeOffset at, string source = "broadcast") =>
        Event($"wallet:{NextTx()}:1:in", AccountingEventKind.WalletReceived, at, msat, 0,
              (AccountingDetailKeys.Source, source));

    public AccountingEventModel WalletSpent(long msat, DateTimeOffset at) =>
        Event($"wallet:{NextTx()}:0:spent", AccountingEventKind.WalletOutputSpent, at, -msat);

    public AccountingEventModel Funded(long msat, long fee, DateTimeOffset at) =>
        Event($"chan:{NextTx()}:funded", AccountingEventKind.ChannelFunded, at, msat, fee);

    public AccountingEventModel Invoice(long msat, DateTimeOffset at, bool selfPayment = false, string? label = null) =>
        Event($"inv:{NextTx()}:settled", AccountingEventKind.InvoiceSettled, at, msat, 0,
              (AccountingDetailKeys.SelfPayment, selfPayment ? AccountingDetailKeys.True : null),
              (AccountingDetailKeys.Label, label));

    public AccountingEventModel Payment(long msat, long fee, DateTimeOffset at, bool selfPayment = false) =>
        Event($"pay:{NextTx()}:succeeded", AccountingEventKind.PaymentSucceeded, at, -(msat + fee), fee,
              (AccountingDetailKeys.SelfPayment, selfPayment ? AccountingDetailKeys.True : null));

    public AccountingEventModel Forward(long feeMsat, DateTimeOffset at) =>
        Event($"fwd:{NextTx()}:settled", AccountingEventKind.ForwardSettled, at, feeMsat);

    public AccountingEventModel MutualClose(long balanceMsat, long fee, DateTimeOffset at) =>
        Event($"chan:{NextTx()}:closed", AccountingEventKind.ChannelClosedMutual, at, -balanceMsat, fee);

    public AccountingEventModel Withdrawal(long msat, long fee, DateTimeOffset at) =>
        Event($"wsend:{NextTx()}", AccountingEventKind.WalletSent, at, -msat, fee);

    public static AccountingEventModel Reversal(AccountingEventModel original, DateTimeOffset at, uint height) =>
        AccountingConfirmations.CreateReversal(original, at, height);

    public static AccountingEventModel Event(string key, AccountingEventKind kind, DateTimeOffset at, long amountMsat,
                                             long feeMsat = 0, params (string Key, string? Value)[] details) =>
        new()
        {
            EventKey = key,
            Kind = kind,
            OccurredAt = at,
            // On-chain facts carry a block (a reorg reverses them)
            BlockHeight = kind is AccountingEventKind.WalletReceived or AccountingEventKind.WalletOutputSpent
                                  or AccountingEventKind.WalletSent or AccountingEventKind.ChannelFunded
                                  or AccountingEventKind.ChannelClosedMutual
                              ? 800_000u
                              : null,
            AmountMsat = amountMsat,
            FeeMsat = feeMsat,
            Details = details.Where(d => d.Value is not null).ToDictionary(d => d.Key, d => d.Value!)
        };

    private string NextTx() => (++_txCounter).ToString("x64", CultureInfo.InvariantCulture);

    #endregion

    private static ILightningSigner CreateSigner()
    {
        using var key = new Key(s_nodePrivateKey);
        CompactPubKey nodeId = key.PubKey.ToBytes();
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(x => x.GetNodeKeyPair())
                  .Returns(() => new CryptoKeyPair(s_nodePrivateKey.ToArray(), nodeId));
        return new LocalLightningSigner(new Mock<IFundingOutputBuilder>().Object,
                                        new Mock<IKeyDerivationService>().Object,
                                        new Mock<ILogger<LocalLightningSigner>>().Object, new NodeOptions(),
                                        keyManager.Object, new Mock<IUtxoMemoryRepository>().Object);
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

    /// <summary>A UTF-8 file without a byte order mark.</summary>
    public static Encoding Utf8 { get; } = new UTF8Encoding(false);
}