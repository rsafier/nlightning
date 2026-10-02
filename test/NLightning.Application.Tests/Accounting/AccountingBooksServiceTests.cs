using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Accounting;

using Application.Accounting;
using Application.Accounting.Books;
using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Persistence.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;

/// <summary>
/// The operational books' projector (NL-602 A2) over the production unit of work on a SQLite file, with a stub rules
/// delegate: one entry per sealed event in batches with the cursor in the same save, exactly-once across a save that
/// throws, books off then on catching up, a rules failure stopping the round until it is fixed, a rebuild equal to the
/// incremental projection, and the reconcile lines against a fake snapshot.
/// </summary>
public sealed class AccountingBooksServiceTests : IAsyncLifetime
{
    private const string FailingKey = "boom";

    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"nltg-books-{Guid.NewGuid():N}.db");

    private ServiceProvider? _provider;
    private AccountingEventSealerService? _sealer;
    private volatile bool _failRules;

    private ServiceProvider Provider => _provider ?? throw new InvalidOperationException("Not initialized");
    private AccountingEventSealerService Sealer => _sealer ?? throw new InvalidOperationException("Not initialized");

    public async ValueTask InitializeAsync()
    {
        _provider = BuildProvider(_databasePath);
        using (var scope = Provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                       .MigrateAsync(TestContext.Current.CancellationToken);

        _sealer = new AccountingEventSealerService(Provider.GetRequiredService<IServiceScopeFactory>(),
                                                   NullLogger<AccountingEventSealerService>.Instance,
                                                   Options.Create(new AccountingOptions
                                                   {
                                                       SealInterval = TimeSpan.FromHours(1)
                                                   }));
    }

    public async ValueTask DisposeAsync()
    {
        if (_sealer is not null)
            await _sealer.DisposeAsync();
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
    public async Task Given_SealedEventsOverSeveralBatches_When_Projected_Then_EachHasOneEntryAndTheCursorIsTheTip()
    {
        // Arrange
        await using var books = CreateBooks(batchSize: 2);
        using var recorder = new MetricRecorder(books.Meter);
        await AddAndSealAsync(Invoice("i1", 1_000), Invoice("i2", 200), Failed("f1"), Wallet("w1", 500),
                              Invoice("i3", 30));

        // Act
        var projected = await books.ProjectNowAsync(TestContext.Current.CancellationToken);
        var again = await books.ProjectNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(5, projected);
        Assert.Equal(0, again);
        var entries = await ListEntriesAsync();
        Assert.Equal(["i1", "i2", "f1", "w1", "i3"], entries.Select(e => e.EventKey));
        Assert.Equal([1L, 2L, 3L, 4L, 5L], entries.Select(e => e.LedgerSeq));
        Assert.Empty(entries[2].Postings);
        Assert.Equal(5, await GetCursorAsync());
        var balances = await GetBalancesAsync();
        Assert.Equal(1_230, balances[AccountRole.Channels]);
        Assert.Equal(-1_230, balances[AccountRole.Received]);
        Assert.Equal(500, balances[AccountRole.Wallet]);
        Assert.Equal(5, recorder.Sum("nlightning.accounting.entries.projected"));
        Assert.Equal(5, books.TotalProjected);
    }

    [Fact]
    public async Task Given_ASaveThatThrows_When_TheNextRoundRuns_Then_EveryEventIsProjectedExactlyOnce()
    {
        // Arrange: four events in batches of two, the second batch's save crashes
        await AddAndSealAsync(Invoice("c1", 100), Invoice("c2", 20), Invoice("c3", 3), Invoice("c4", 4_000));
        var crashing = new CrashingScopeFactory(Provider.GetRequiredService<IServiceScopeFactory>(), crashScope: 2);
        await using (var crashed = CreateBooks(batchSize: 2, scopeFactory: crashing))
        {
            // Act (the crash)
            await Assert.ThrowsAsync<SimulatedCrashException>(() => crashed.ProjectNowAsync(
                                                                  TestContext.Current.CancellationToken));
        }

        Assert.Equal(2, await GetCursorAsync());
        Assert.Equal(2, (await ListEntriesAsync()).Count);

        // Act (a restart)
        await using var restarted = CreateBooks(batchSize: 2);
        var projected = await restarted.ProjectNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, projected);
        Assert.Equal(["c1", "c2", "c3", "c4"], (await ListEntriesAsync()).Select(e => e.EventKey));
        Assert.Equal(4, await GetCursorAsync());
        var balances = await GetBalancesAsync();
        Assert.Equal(4_123, balances[AccountRole.Channels]);
        Assert.Equal(-4_123, balances[AccountRole.Received]);
    }

    [Fact]
    public async Task Given_TheBooksOff_When_TurnedOnLater_Then_NothingRanAndTheyCatchUpFromTheCursor()
    {
        // Arrange
        await AddAndSealAsync(Invoice("o1", 10));
        await using (var on = CreateBooks())
            await on.ProjectNowAsync(TestContext.Current.CancellationToken);
        await AddAndSealAsync(Invoice("o2", 20), Invoice("o3", 30));

        // Act: books off
        await using (var off = CreateBooks(enabled: false))
        {
            off.Start();
            var projectedOff = await off.ProjectNowAsync(TestContext.Current.CancellationToken);

            // Assert (off)
            Assert.False(off.IsEnabled);
            Assert.Equal(0, projectedOff);
            await Assert.ThrowsAsync<InvalidOperationException>(() => off.RebuildAsync(
                                                                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => off.ReconcileAsync(
                                                                    TestContext.Current.CancellationToken));
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, await GetCursorAsync());

        // Act: on again
        await using var onAgain = CreateBooks();
        var projected = await onAgain.ProjectNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, projected);
        Assert.Equal(["o1", "o2", "o3"], (await ListEntriesAsync()).Select(e => e.EventKey));
        Assert.Equal(60, (await GetBalancesAsync())[AccountRole.Channels]);
    }

    [Fact]
    public async Task Given_TheRulesThrowOnAnEvent_When_Projected_Then_TheRoundStopsThereAndTheNextRoundResumes()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var books = CreateBooks(logger: logger);
        using var recorder = new MetricRecorder(books.Meter);
        await AddAndSealAsync(Invoice("r1", 1), Invoice(FailingKey, 2), Invoice("r3", 3));
        _failRules = true;

        // Act
        var first = await books.ProjectNowAsync(TestContext.Current.CancellationToken);
        var second = await books.ProjectNowAsync(TestContext.Current.CancellationToken);

        // Assert: stopped before the failing event, never skipped, logged critical once and metered every time
        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(1, await GetCursorAsync());
        Assert.Equal(["r1"], (await ListEntriesAsync()).Select(e => e.EventKey));
        Assert.Contains(FailingKey, books.ProjectionError, StringComparison.Ordinal);
        Assert.Single(logger.Criticals, c => c.Contains(FailingKey, StringComparison.Ordinal));
        Assert.Equal(2, recorder.Sum("nlightning.accounting.projection.failures"));

        // Act: the rules fixed
        _failRules = false;
        var third = await books.ProjectNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, third);
        Assert.Null(books.ProjectionError);
        Assert.Equal(["r1", FailingKey, "r3"], (await ListEntriesAsync()).Select(e => e.EventKey));
        Assert.Equal(6, (await GetBalancesAsync())[AccountRole.Channels]);
    }

    [Fact]
    public async Task Given_PostingsThatDoNotBalance_When_Projected_Then_TheyAreARulesFailure()
    {
        // Arrange
        await using var books = CreateBooks(rules: (_, _) => [new AccountingPosting(AccountRole.Channels, 1)]);
        await AddAndSealAsync(Invoice("u1", 1));

        // Act
        var projected = await books.ProjectNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, projected);
        Assert.Equal(0, await GetCursorAsync());
        Assert.Contains("do not balance", books.ProjectionError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Given_AnIncrementalProjection_When_Rebuilt_Then_TheBooksAreTheSame()
    {
        // Arrange: reversals of an entry staged in the same batch and of one saved in an earlier batch
        await using var books = CreateBooks(batchSize: 2);
        await AddAndSealAsync(Invoice("a", 1_000), Wallet("w", 500), Invoice("b", 200));
        await books.ProjectNowAsync(TestContext.Current.CancellationToken);
        await AddAndSealAsync(Reversal("rev-b", "b"), Reversal("rev-a", "a"), Failed("memo"));
        await books.ProjectNowAsync(TestContext.Current.CancellationToken);
        var incremental = await ListEntriesAsync();
        var incrementalBalances = await GetBalancesAsync();

        // Act
        var rebuilt = await books.RebuildAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(6, rebuilt);
        var entries = await ListEntriesAsync();
        Assert.Equal(Describe(incremental), Describe(entries));
        Assert.Equal(incrementalBalances.OrderBy(b => b.Key), (await GetBalancesAsync()).OrderBy(b => b.Key));
        Assert.Equal(6, await GetCursorAsync());
        Assert.Equal(0, incrementalBalances[AccountRole.Channels]);
        Assert.Equal(500, incrementalBalances[AccountRole.Wallet]);
        Assert.Equal([new AccountingPosting(AccountRole.Channels, -200), new AccountingPosting(AccountRole.Received, 200)],
                     entries.Single(e => e.EventKey == "rev-b").Postings);
    }

    [Fact]
    public async Task Given_ASnapshot_When_Reconciled_Then_EachBucketIsComparedLoggedAndMetered()
    {
        // Arrange: the books hold 1,000 msat in channels and nothing on chain
        var logger = new RecordingLogger();
        var channelOpen = Bucket(0x01, new ShortChannelId(800_000, 1, 0), ChannelState.Open, local: 1_000);
        var awaitingFunding = Bucket(0x02, null, ChannelState.V1FundingSigned, local: 5_000);
        var onchain = Bucket(0x03, null, ChannelState.OnchainResolving, pending: 300, pendingHtlc: 100, sweeps: 2);
        var failedWithClose = Bucket(0x04, new ShortChannelId(800_001, 1, 0), ChannelState.Failed, local: 77,
                                     pending: 0, sweeps: 1);
        var snapshot = new AccountingSnapshot(s_at, 812_000, [channelOpen, awaitingFunding, onchain, failedWithClose],
                                              new WalletBalanceBucket(2_000, 100, 50));
        var source = new Mock<INodeSnapshotSource>();
        source.Setup(s => s.TakeSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        await using var books = CreateBooks(logger: logger, snapshotSource: source.Object);
        using var recorder = new MetricRecorder(books.Meter);
        await AddAndSealAsync(Invoice("i1", 1_000));

        // Act
        var result = await books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, result.LedgerSeq);
        Assert.Equal(812_000u, result.BlockHeight);
        Assert.False(result.IsClean);
        var lines = result.Lines.ToDictionary(l => l.Account);
        Assert.Equal((1_000L, 1_000L), (lines[AccountRole.Channels].BooksMsat, lines[AccountRole.Channels].NodeMsat));
        Assert.Equal((0L, 400L), (lines[AccountRole.Pending].BooksMsat, lines[AccountRole.Pending].NodeMsat));
        Assert.Equal((0L, 2_100L), (lines[AccountRole.Wallet].BooksMsat, lines[AccountRole.Wallet].NodeMsat));
        Assert.Equal((0L, 0L), (lines[AccountRole.Clearing].BooksMsat, lines[AccountRole.Clearing].NodeMsat));
        Assert.All(result.Lines, l => Assert.False(string.IsNullOrEmpty(l.Note)));
        Assert.Equal(2, logger.Warnings.Count(w => w.Contains("drifts", StringComparison.Ordinal)));
        Assert.Equal(-2_100, recorder.Last("nlightning.accounting.reconcile.drift_msat", "account", "Wallet"));
        Assert.Equal(0, recorder.Last("nlightning.accounting.reconcile.drift_msat", "account", "Channels"));
        Assert.Same(result, books.LastReconcile);
    }

    [Fact]
    public void Given_SnapshotBuckets_When_TheLinesAreBuilt_Then_OnlyFundedOffChainChannelsCountAsChannels()
    {
        // Arrange
        var snapshot = new AccountingSnapshot(s_at, 1,
                                              [
                                                  Bucket(0x01, null, ChannelState.ReadyForUs, local: 10),
                                                  Bucket(0x02, new ShortChannelId(1, 1, 1), ChannelState.Closing,
                                                         local: 20),
                                                  Bucket(0x03, null, ChannelState.ReadyForThem, local: 40),
                                                  Bucket(0x04, new ShortChannelId(1, 1, 2), ChannelState.Failed,
                                                         local: 80)
                                              ], new WalletBalanceBucket(0, 0, 0));

        // Act
        var lines = AccountingBooksService.BuildReconcileLines(snapshot, new Dictionary<AccountRole, long>(), "k: x");

        // Assert
        Assert.Equal(110, lines.Single(l => l.Account == AccountRole.Channels).NodeMsat);
        Assert.All(lines, l => Assert.StartsWith("books stopped at k: x", l.Note, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Given_TheLoopStarted_When_EventsAreSealed_Then_TheyAreProjectedAndReconciledUntilStopped()
    {
        // Arrange
        var source = new Mock<INodeSnapshotSource>();
        source.Setup(s => s.TakeSnapshotAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AccountingSnapshot(s_at, 1, [], new WalletBalanceBucket(0, 0, 0)));
        await using var books = CreateBooks(interval: TimeSpan.FromMilliseconds(50), snapshotSource: source.Object,
                                            snapshotInterval: TimeSpan.FromMilliseconds(50));
        await AddAndSealAsync(Invoice("l1", 5));

        // Act
        books.Start();
        await WaitUntilAsync(() => GetCursorAsync().GetAwaiter().GetResult() == 1 && books.LastReconcile is not null);
        await AddAndSealAsync(Invoice("l2", 6));
        await WaitUntilAsync(() => GetCursorAsync().GetAwaiter().GetResult() == 2);
        await books.StopAsync();
        await AddAndSealAsync(Invoice("l3", 7));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["l1", "l2"], (await ListEntriesAsync()).Select(e => e.EventKey));
        Assert.NotNull(books.LastReconcile);
        Assert.True(books.LastReconcile.LedgerSeq >= 1);
    }

    [Fact]
    public async Task Given_UnsealedEvents_When_ProjectedNow_Then_TheSealerSealsThemFirst()
    {
        // Arrange
        await using var books = CreateBooks(sealer: Sealer);
        await AddAsync(Invoice("s1", 1), Invoice("s2", 2));

        // Act
        var projected = await books.ProjectNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, projected);
        Assert.Equal(["s1", "s2"], (await ListEntriesAsync()).Select(e => e.EventKey));
    }

    [Fact]
    public void Given_TheApplicationServices_When_Registered_Then_TheBooksAreOneInstance()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<Domain.Channels.Interfaces.IChannelMemoryRepository>().Object);
        services.AddSingleton(new Mock<Domain.Bitcoin.Interfaces.IUtxoMemoryRepository>().Object);

        // Act
        services.AddAccountingServices();
        services.AddAccountingServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Assert
        Assert.Same(provider.GetRequiredService<AccountingBooksService>(),
                    provider.GetRequiredService<IAccountingBooks>());
        Assert.Single(services, d => d.ServiceType == typeof(IAccountingBooks));
        Assert.True(provider.GetRequiredService<IAccountingBooks>().IsEnabled);
    }

    private AccountingBooksService CreateBooks(
        int batchSize = 500, bool enabled = true, TimeSpan? interval = null, TimeSpan? snapshotInterval = null,
        IServiceScopeFactory? scopeFactory = null, ILogger<AccountingBooksService>? logger = null,
        INodeSnapshotSource? snapshotSource = null, IAccountingEventSealer? sealer = null,
        Func<AccountingEventModel, Func<string, AccountingEntry?>, IReadOnlyList<AccountingPosting>>? rules = null) =>
        new(scopeFactory ?? Provider.GetRequiredService<IServiceScopeFactory>(),
            logger ?? NullLogger<AccountingBooksService>.Instance,
            Options.Create(new AccountingOptions
            {
                Enabled = enabled,
                SealBatchSize = batchSize,
                SealInterval = interval ?? TimeSpan.FromHours(1),
                SnapshotInterval = snapshotInterval ?? TimeSpan.FromHours(1)
            }), sealer, snapshotSource, rules: rules ?? StubRules);

    /// <summary>A stand-in for <see cref="AccountingPostingRules.Post"/> (another lane writes the real rules).</summary>
    private IReadOnlyList<AccountingPosting> StubRules(AccountingEventModel accountingEvent,
                                                       Func<string, AccountingEntry?> findEntry)
    {
        if (_failRules && accountingEvent.EventKey == FailingKey)
            throw new InvalidOperationException("stub rules cannot post this");

        switch (accountingEvent.Kind)
        {
            case AccountingEventKind.InvoiceSettled:
                return
                [
                    new AccountingPosting(AccountRole.Channels, accountingEvent.AmountMsat),
                    new AccountingPosting(AccountRole.Received, -accountingEvent.AmountMsat)
                ];
            case AccountingEventKind.WalletReceived:
                return
                [
                    new AccountingPosting(AccountRole.Wallet, accountingEvent.AmountMsat),
                    new AccountingPosting(AccountRole.TransfersIn, -accountingEvent.AmountMsat)
                ];
            case AccountingEventKind.Reversal:
                var reversed = findEntry(accountingEvent.Details[AccountingConfirmations.ReversesDetail])
                            ?? throw new InvalidOperationException("the reversed entry is unknown");
                return reversed.Postings.Select(p => p with { AmountMsat = -p.AmountMsat }).ToList();
            default:
                return [];
        }
    }

    private static AccountingEventModel Invoice(string key, long amountMsat) =>
        new()
        {
            EventKey = key,
            Kind = AccountingEventKind.InvoiceSettled,
            OccurredAt = s_at,
            AmountMsat = amountMsat
        };

    private static AccountingEventModel Wallet(string key, long amountMsat) =>
        new()
        {
            EventKey = key,
            Kind = AccountingEventKind.WalletReceived,
            OccurredAt = s_at,
            AmountMsat = amountMsat
        };

    private static AccountingEventModel Failed(string key) =>
        new() { EventKey = key, Kind = AccountingEventKind.PaymentFailed, OccurredAt = s_at };

    private static AccountingEventModel Reversal(string key, string reverses) =>
        new()
        {
            EventKey = key,
            Kind = AccountingEventKind.Reversal,
            OccurredAt = s_at,
            Details = AccountingDetailsCodec.Create((AccountingConfirmations.ReversesDetail, reverses))
        };

    private static ChannelBalanceBucket Bucket(byte seed, ShortChannelId? scid, ChannelState state, long local = 0,
                                               long pending = 0, long pendingHtlc = 0, int sweeps = 0) =>
        new(new ChannelId(Enumerable.Repeat(seed, 32).ToArray()), scid, state, null, 10_000, local, 0, 0, 0, pending,
            pendingHtlc, sweeps, true);

    private static List<string> Describe(IEnumerable<AccountingEntry> entries) =>
        entries.Select(e => $"{e.LedgerSeq}|{e.EventKey}|{e.Kind}|{e.OccurredAt:O}|"
                          + string.Join(",", e.Postings.Select(p => $"{p.Account}:{p.AmountMsat}")))
               .ToList();

    private async Task AddAndSealAsync(params AccountingEventModel[] events)
    {
        await AddAsync(events);
        await Sealer.SealNowAsync(TestContext.Current.CancellationToken);
    }

    private async Task AddAsync(params AccountingEventModel[] events)
    {
        await using var scope = Provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var accountingEvent in events)
            unitOfWork.AccountingEventDbRepository.Add(accountingEvent);
        await unitOfWork.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<AccountingEntry>> ListEntriesAsync()
    {
        await using var scope = Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository
                          .ListEntriesAsync(new AccountingEntryQuery(0, 1_000), TestContext.Current.CancellationToken);
    }

    private async Task<long> GetCursorAsync()
    {
        await using var scope = Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository
                          .GetCursorAsync(TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyDictionary<AccountRole, long>> GetBalancesAsync()
    {
        await using var scope = Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository
                          .GetBalancesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The condition was not met in time");

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
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

    /// <summary>Hands out real scopes, except that the unit of work of scope number <c>crashScope</c> (1-based)
    /// throws <see cref="SimulatedCrashException"/> at its first save: a process that died in that save.</summary>
    private sealed class CrashingScopeFactory(IServiceScopeFactory inner, int crashScope) : IServiceScopeFactory
    {
        private int _scopes;

        public IServiceScope CreateScope()
        {
            var scope = inner.CreateScope();
            return Interlocked.Increment(ref _scopes) == crashScope ? new CrashingScope(scope) : scope;
        }

        private sealed class CrashingScope(IServiceScope inner) : IServiceScope, IServiceProvider
        {
            private CrashingUnitOfWork? _unitOfWork;

            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) =>
                serviceType == typeof(IUnitOfWork)
                    ? _unitOfWork ??= new CrashingUnitOfWork(
                          inner.ServiceProvider.GetRequiredService<IUnitOfWork>(), 1)
                    : inner.ServiceProvider.GetService(serviceType);

            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class RecordingLogger : ILogger<AccountingBooksService>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public List<string> Warnings => Of(LogLevel.Warning);

        public List<string> Criticals => Of(LogLevel.Critical);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));

        private List<string> Of(LogLevel level) =>
            _entries.Where(e => e.Level == level).Select(e => e.Message).ToList();
    }

    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();

        private readonly ConcurrentQueue<(string Instrument, long Value, Dictionary<string, object?> Tags)>
            _measurements = new();

        public MetricRecorder(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var copy = new Dictionary<string, object?>();
                foreach (var tag in tags)
                    copy[tag.Key] = tag.Value;
                _measurements.Enqueue((instrument.Name, value, copy));
            });
            _listener.Start();
        }

        public long Sum(string instrument) => _measurements.Where(m => m.Instrument == instrument).Sum(m => m.Value);

        public long Last(string instrument, string tag, string value) =>
            _measurements.Last(m => m.Instrument == instrument && Equals(m.Tags.GetValueOrDefault(tag), value)).Value;

        public void Dispose() => _listener.Dispose();
    }
}