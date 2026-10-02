using Microsoft.Data.Sqlite;
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
using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Models;
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

/// <summary>
/// A node's accounting on a SQLite file for the period close tests (NL-602 A3-T5): the real sealer, operational books
/// (with stub posting rules) and <see cref="AccountingPeriodService"/>, a real <see cref="LocalLightningSigner"/>, a
/// settable clock and <see cref="TestFinancialProjector"/>, a small financial projector that honours the lock.
/// </summary>
internal sealed class FinancialCloseTestKit : IAsyncDisposable
{
    public const string Currency = "USD";

    /// <summary>The price the test projector values every posting at: 0.0006 USD per msat-thousand (60,000 USD/BTC).</summary>
    public const decimal UsdPerSat = 0.0006m;

    private static readonly byte[] s_nodePrivateKey =
        Convert.FromHexString("2222222222222222222222222222222222222222222222222222222222222222");

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"nltg-close-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _provider;

    private FinancialCloseTestKit(DateTimeOffset now)
    {
        Clock = new SettableTimeProvider(now);
        _provider = BuildProvider(_databasePath);
        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new AccountingOptions { SealInterval = TimeSpan.FromHours(1) });
        Sealer = new AccountingEventSealerService(scopeFactory, NullLogger<AccountingEventSealerService>.Instance,
                                                  options);
        Books = new AccountingBooksService(scopeFactory, NullLogger<AccountingBooksService>.Instance, options, Sealer,
                                           rules: OperationalRules);
        Signer = CreateSigner();
        Projector = new TestFinancialProjector(scopeFactory);
        Periods = new AccountingPeriodService(scopeFactory, NullLogger<AccountingPeriodService>.Instance, Books,
                                              Projector, Signer, Clock);
        Projector.Sink = Periods;
    }

    public SettableTimeProvider Clock { get; }
    public AccountingEventSealerService Sealer { get; }
    public AccountingBooksService Books { get; }
    public ILightningSigner Signer { get; }
    public TestFinancialProjector Projector { get; }
    public AccountingPeriodService Periods { get; }
    public CompactPubKey NodeId => Signer.GetNodePublicKey();

    public static async Task<FinancialCloseTestKit> CreateAsync(DateTimeOffset now)
    {
        var kit = new FinancialCloseTestKit(now);
        using var scope = kit._provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                   .MigrateAsync(TestContext.Current.CancellationToken);
        return kit;
    }

    public IServiceScope CreateScope() => _provider.CreateScope();

    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>A settled invoice of <paramref name="msat"/> at <paramref name="at"/> (an acquisition: a lot).</summary>
    public static AccountingEventModel Income(string key, long msat, DateTimeOffset at) =>
        new() { EventKey = key, Kind = AccountingEventKind.InvoiceSettled, OccurredAt = at, AmountMsat = msat };

    /// <summary>A payment of <paramref name="msat"/> at <paramref name="at"/> (a disposal: lots relieved FIFO).</summary>
    public static AccountingEventModel Spend(string key, long msat, DateTimeOffset at) =>
        new() { EventKey = key, Kind = AccountingEventKind.PaymentSucceeded, OccurredAt = at, AmountMsat = -msat };

    /// <summary>Writes the events, seals them and projects both books.</summary>
    public async Task AddAndProjectAsync(params AccountingEventModel[] events)
    {
        using (var scope = CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            foreach (var accountingEvent in events)
                unitOfWork.AccountingEventDbRepository.Add(accountingEvent);
            await unitOfWork.SaveChangesAsync();
        }

        await Books.ProjectNowAsync(TestContext.Current.CancellationToken);
        await Projector.ProjectAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Runs raw SQL on the database (tampering).</summary>
    public async Task ExecuteSqlAsync(string sql)
    {
        using var scope = CreateScope();
        await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                   .ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
    }

    /// <summary>Every financial entry, ledger and adjustment order.</summary>
    public async Task<IReadOnlyList<AccountingEntry>> ListFinancialEntriesAsync()
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository
                          .ListEntriesAsync(new AccountingEntryQuery(0, 10_000)
                          {
                              Book = AccountingBook.Financial,
                              AfterAdjustment = -1
                          }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The whole financial book as text, for "rebuilt equals incremental": entries with their postings and closed
    /// period, balances, cursor, lots and reliefs (relief ids left out: a replayed relief gets a new one).
    /// </summary>
    public async Task<string> SnapshotFinancialBookAsync()
    {
        using var scope = CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var lines = new List<string>();
        foreach (var entry in await ListFinancialEntriesAsync())
            lines.Add($"E {entry.LedgerSeq}/{entry.Adjustment} {entry.EventKey} {entry.Kind} "
                    + $"{entry.OccurredAt.UtcTicks} {entry.Flags} {entry.ClosedPeriodId} {entry.Note} ["
                    + string.Join(", ", entry.Postings.Select(p => $"{p.Account}:{p.AccountName}:{p.AmountMsat}:"
                                                                 + $"{p.FiatAmount}:{p.FiatCurrency}"))
                    + "]");

        var books = unitOfWork.AccountingBooksDbRepository;
        foreach (var balance in await books.GetAccountBalancesAsync(AccountingBook.Financial,
                                                                    TestContext.Current.CancellationToken))
            lines.Add($"B {balance.Account} {balance.AccountName} {balance.BalanceMsat} "
                    + AccountingClosingState.FormatFiat(balance.FiatAmount));

        lines.Add($"C {await books.GetCursorAsync(AccountingBook.Financial, TestContext.Current.CancellationToken)}");
        var lots = unitOfWork.AccountingLotDbRepository;
        // In acquisition order, not id order: a replayed lot gets a new id
        var allLots = await lots.ListLotsAcquiredBeforeAsync(DateTimeOffset.MaxValue, 0, 10_000,
                                                             TestContext.Current.CancellationToken);
        foreach (var lot in allLots.OrderBy(l => l.AcquiredAt).ThenBy(l => l.SourceLedgerSeq)
                                   .ThenBy(l => l.SourceAdjustment))
        {
            lines.Add($"L {lot.AcquiredAt.UtcTicks} {lot.Origin} {lot.SourceLedgerSeq}/{lot.SourceAdjustment} "
                    + $"{lot.OriginalMsat} {lot.RemainingMsat} {lot.FiatCost} {lot.ClosedPeriodId}");
            foreach (var relief in await lots.ListReliefsByLotAsync(lot.Id, TestContext.Current.CancellationToken))
                lines.Add($"  R {relief.LedgerSeq}/{relief.Adjustment} {relief.RelievedAt.UtcTicks} {relief.Msat} "
                        + $"{relief.FiatCostRelieved} {relief.Proceeds} {relief.ClosedPeriodId}");
        }

        return string.Join('\n', lines);
    }

    public async ValueTask DisposeAsync()
    {
        Periods.Dispose();
        await Books.DisposeAsync();
        await Sealer.DisposeAsync();
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

    /// <summary>The operational rules of the tests: an invoice is Dr Channels, Cr Received; a payment Cr Channels, Dr
    /// Sent.</summary>
    private static IReadOnlyList<AccountingPosting> OperationalRules(AccountingEventModel accountingEvent,
                                                                     Func<string, AccountingEntry?> find) =>
        accountingEvent.Kind switch
        {
            AccountingEventKind.InvoiceSettled =>
            [
                new AccountingPosting(AccountRole.Channels, accountingEvent.AmountMsat),
                new AccountingPosting(AccountRole.Received, -accountingEvent.AmountMsat)
            ],
            AccountingEventKind.PaymentSucceeded =>
            [
                new AccountingPosting(AccountRole.Channels, accountingEvent.AmountMsat),
                new AccountingPosting(AccountRole.Sent, -accountingEvent.AmountMsat)
            ],
            _ => []
        };

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
}

/// <summary>A clock the tests move.</summary>
internal sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// A minimal financial projector (A3-T4 stands in for it in production) that keeps the period close's contract: it
/// reads its cursor and lots from the database every round, holds the sink's write lock from its check to its save,
/// and hands an operational entry dated in a locked period to the sink as a late fact. Every operational line becomes
/// a financial one named <c>fin:&lt;role&gt;</c> valued at <see cref="FinancialCloseTestKit.UsdPerSat"/> (none for an
/// event key starting with <c>unvalued</c>; <c>expenses:unclassified</c> and the flag for one starting with
/// <c>unclassified</c>); an income line opens a lot, a spend line relieves the open lots oldest first.
/// </summary>
internal sealed class TestFinancialProjector(IServiceScopeFactory scopeFactory) : IFinancialBooksProjector
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IAccountingAdjustmentSink? Sink { get; set; }

    public bool IsEnabled { get; set; } = true;

    public async Task<int> ProjectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var total = 0;
            while (true)
            {
                using var scope = scopeFactory.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var books = unitOfWork.AccountingBooksDbRepository;
                var cursor = await books.GetCursorAsync(AccountingBook.Financial, cancellationToken);
                var page = await books.ListEntriesAsync(new AccountingEntryQuery(cursor, 100), cancellationToken);
                if (page.Count == 0)
                    return total;

                using (await Sink!.EnterAsync(cancellationToken))
                {
                    foreach (var operational in page)
                    {
                        total += await ProjectOneAsync(unitOfWork, operational, cancellationToken);
                        await books.SetCursorAsync(AccountingBook.Financial, operational.LedgerSeq, cancellationToken);
                    }

                    await unitOfWork.SaveChangesAsync();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> action,
                                              CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ProjectOneAsync(IUnitOfWork unitOfWork, AccountingEntry operational,
                                            CancellationToken cancellationToken)
    {
        var unvalued = operational.EventKey.StartsWith("unvalued", StringComparison.Ordinal);
        var unclassified = operational.EventKey.StartsWith("unclassified", StringComparison.Ordinal);
        var flags = (unvalued ? AccountingEntryFlags.Unvalued : AccountingEntryFlags.None)
                  | (unclassified ? AccountingEntryFlags.Unclassified : AccountingEntryFlags.None);
        var postings = operational.Postings.Select(p => new AccountingPosting(p.Account, p.AmountMsat)
        {
            AccountName = unclassified && p.Account == AccountRole.Sent
                              ? "expenses:unclassified"
                              : "fin:" + p.Account.ToString().ToLowerInvariant(),
            FiatAmount = unvalued ? null : Value(p.AmountMsat),
            FiatCurrency = unvalued ? null : FinancialCloseTestKit.Currency
        }).ToList();

        var at = operational.OccurredAt;
        var adjustment = 0;
        if (await Sink!.GetLockingPeriodAsync(operational.OccurredAt, cancellationToken) is not null)
        {
            var staged = await Sink.StageAdjustmentAsync(unitOfWork, new AccountingAdjustment(
                                                             AccountingAdjustmentReason.LateFact,
                                                             operational.LedgerSeq, operational.EventKey,
                                                             operational.Kind, operational.OccurredAt, postings)
            {
                Flags = flags
            }, cancellationToken);
            if (staged is null)
                return 0;

            at = staged.OccurredAt;
            adjustment = staged.Adjustment;
        }
        else
        {
            await unitOfWork.AccountingBooksDbRepository.AddEntryAsync(
                new AccountingEntry(operational.LedgerSeq, operational.EventKey, operational.Kind, at,
                                    operational.ChannelId, operational.PaymentHash, postings, operational.Note)
                {
                    Book = AccountingBook.Financial,
                    Flags = flags,
                    Classification = AccountingClassificationSource.Default
                }, cancellationToken);
        }

        var lots = unitOfWork.AccountingLotDbRepository;
        foreach (var posting in postings)
        {
            if (posting.Account == AccountRole.Received && posting.AmountMsat < 0)
            {
                var msat = -posting.AmountMsat;
                await lots.AddLotAsync(new AccountingLot(0, at, AccountingLotOrigin.Acquisition, operational.LedgerSeq,
                                                         adjustment, null, null, msat, msat,
                                                         unvalued ? null : Value(msat),
                                                         unvalued ? null : FinancialCloseTestKit.Currency, null,
                                                         false, null), cancellationToken);
            }
            else if (posting.Account == AccountRole.Sent && posting.AmountMsat > 0)
            {
                var left = posting.AmountMsat;
                foreach (var lot in await lots.ListOpenLotsAsync(cancellationToken: cancellationToken))
                {
                    if (left == 0)
                        break;

                    var take = Math.Min(left, lot.RemainingMsat);
                    left -= take;
                    await lots.UpdateLotAsync(lot with { RemainingMsat = lot.RemainingMsat - take }, cancellationToken);
                    lots.AddRelief(new AccountingLotRelief(0, lot.Id, operational.LedgerSeq, adjustment, at, take,
                                                           lot.FiatCost is { } cost
                                                               ? cost * take / lot.OriginalMsat
                                                               : null, Value(take), null));
                }
            }
        }

        return 1;
    }

    private static decimal Value(long msat) => msat / 1_000m * FinancialCloseTestKit.UsdPerSat;
}