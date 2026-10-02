using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Accounting;

using Application.Accounting;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Crypto.Hashes;
using Domain.Persistence.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using TestUtils;

/// <summary>
/// The accounting feed's sealer service (NL-602) over the production unit of work on a SQLite file: rows written in
/// several saves get a dense ledger sequence and one hash chain across rounds, repeated keys are marked and metered,
/// batches loop until the table is sealed, rounds never overlap, and the background loop seals at start and on
/// <see cref="AccountingEventSealerService.Nudge"/>.
/// </summary>
public sealed class AccountingEventSealerServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"nltg-accounting-{Guid.NewGuid():N}.db");

    private ServiceProvider? _provider;

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

    [Fact]
    public async Task Given_RowsSavedInSeveralSaves_When_SealedInTwoRounds_Then_TheSequenceAndChainContinue()
    {
        // Arrange
        await using var sealer = CreateSealer();
        await AddAsync("a", "b");
        await AddAsync("c");

        // Act
        var first = await sealer.SealNowAsync(TestContext.Current.CancellationToken);
        await AddAsync("d");
        await AddAsync("e");
        var second = await sealer.SealNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, first.Sealed);
        Assert.Equal(3, first.Tip.LedgerSeq);
        Assert.Equal(2, second.Sealed);
        Assert.Equal(0, second.Duplicates);
        Assert.Equal(5, second.Tip.LedgerSeq);
        var sealedEvents = await ReadSealedAsync();
        Assert.Equal(["a", "b", "c", "d", "e"], sealedEvents.Select(e => e.EventKey));
        Assert.Equal([1L, 2L, 3L, 4L, 5L], sealedEvents.Select(e => e.LedgerSeq!.Value));
        AssertChain(sealedEvents);
        Assert.Equal(second.Tip.Hash, sealedEvents[^1].Hash);
        Assert.Equal(5, sealer.TotalSealed);
    }

    [Fact]
    public async Task Given_MoreRowsThanABatch_When_SealedNow_Then_EveryBatchIsSealedInOneRound()
    {
        // Arrange
        await using var sealer = CreateSealer(batchSize: 2);
        await AddAsync("k1", "k2", "k3", "k4", "k5");

        // Act
        var result = await sealer.SealNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(5, result.Sealed);
        var sealedEvents = await ReadSealedAsync();
        Assert.Equal(5, sealedEvents.Count);
        AssertChain(sealedEvents);
        await using var scope = Provider.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingEventDbRepository
                                .GetUnsealedAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_RepeatedKeys_When_Sealed_Then_TheyAreMarkedDuplicateLoggedAndMetered()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var sealer = CreateSealer(logger: logger);
        using var recorder = new MetricRecorder(sealer.Meter);
        await AddAsync("x");
        await sealer.SealNowAsync(TestContext.Current.CancellationToken);
        await AddAsync("x", "y");
        await AddAsync("y");

        // Act
        var result = await sealer.SealNowAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, result.Sealed);
        Assert.Equal(2, result.Duplicates);
        Assert.Equal(2, sealer.TotalDuplicates);
        Assert.Equal(2, recorder.Sum("nlightning.accounting.events.duplicate"));
        Assert.Equal(2, recorder.Sum("nlightning.accounting.events.sealed"));
        Assert.Contains(logger.Warnings, w => w.Contains("x", StringComparison.Ordinal));
        Assert.Contains(logger.Warnings, w => w.Contains("y", StringComparison.Ordinal));
        var sealedEvents = await ReadSealedAsync();
        Assert.Equal(["x", "y"], sealedEvents.Select(e => e.EventKey));
        await using var scope = Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
        Assert.Equal(2, await context.AccountingEvents.CountAsync(
                            e => e.LedgerSeq == null && (e.Flags & (int)AccountingEventFlags.Duplicate) != 0,
                            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_ConcurrentSealCalls_When_TheyRun_Then_RoundsDoNotOverlapAndTheChainStaysDense()
    {
        // Arrange
        await using var sealer = CreateSealer(batchSize: 3);
        await AddAsync(Enumerable.Range(0, 20).Select(i => $"c{i}").ToArray());

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 4)
                                                   .Select(_ => sealer.SealNowAsync(
                                                               TestContext.Current.CancellationToken)));

        // Assert
        Assert.Equal(20, results.Sum(r => r.Sealed));
        var sealedEvents = await ReadSealedAsync();
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), sealedEvents.Select(e => e.LedgerSeq!.Value));
        AssertChain(sealedEvents);
    }

    [Fact]
    public async Task Given_TheLoopStarted_When_RowsAreSavedAndNudged_Then_TheyAreSealedUntilStopped()
    {
        // Arrange: an interval long enough that only the start and the nudges run rounds
        await using var sealer = CreateSealer(interval: TimeSpan.FromHours(1));
        await AddAsync("s1", "s2");

        // Act
        sealer.Start();
        await WaitUntilSealedAsync(2);
        await AddAsync("s3");
        sealer.Nudge();
        await WaitUntilSealedAsync(3);
        await sealer.StopAsync();
        await AddAsync("s4");
        sealer.Nudge();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        var sealedEvents = await ReadSealedAsync();
        Assert.Equal(["s1", "s2", "s3"], sealedEvents.Select(e => e.EventKey));
        AssertChain(sealedEvents);
    }

    [Fact]
    public async Task Given_AFailingRound_When_TheLoopRuns_Then_ItIsLoggedAndTheNextRoundSeals()
    {
        // Arrange: the first round's unit of work cannot read the table
        var logger = new RecordingLogger();
        var failures = 1;
        var services = new ServiceCollection();
        services.AddScoped<IUnitOfWork>(sp =>
        {
            if (Interlocked.Decrement(ref failures) >= 0)
                throw new InvalidOperationException("database unavailable");
            return Provider.CreateScope().ServiceProvider.GetRequiredService<IUnitOfWork>();
        });
        await using var failingProvider = services.BuildServiceProvider();
        await AddAsync("f1");
        await using var sealer = new AccountingEventSealerService(
            failingProvider.GetRequiredService<IServiceScopeFactory>(), logger,
            Options.Create(new AccountingOptions { SealInterval = TimeSpan.FromHours(1) }));

        // Act
        sealer.Start();
        await WaitUntilAsync(() => logger.Errors.Count > 0);
        sealer.Nudge();
        await WaitUntilSealedAsync(1);

        // Assert
        Assert.Contains(logger.Errors, e => e.Contains("Could not seal", StringComparison.Ordinal));
        Assert.Equal(["f1"], (await ReadSealedAsync()).Select(e => e.EventKey));
    }

    [Fact]
    public void Given_ANonPositiveInterval_When_Read_Then_TheDefaultApplies()
    {
        // Arrange
        var sealer = new AccountingEventSealerService(Provider.GetRequiredService<IServiceScopeFactory>(),
                                                      NullLogger<AccountingEventSealerService>.Instance,
                                                      Options.Create(new AccountingOptions
                                                      {
                                                          SealInterval = TimeSpan.Zero
                                                      }));

        // Act
        var interval = sealer.Interval;

        // Assert
        Assert.Equal(AccountingOptions.DefaultSealInterval, interval);
    }

    [Fact]
    public void Given_TheApplicationServices_When_Registered_Then_TheSealerIsOneInstanceAndTheSnapshotSourceResolves()
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
        Assert.Same(provider.GetRequiredService<AccountingEventSealerService>(),
                    provider.GetRequiredService<IAccountingEventSealer>());
        Assert.IsType<NodeSnapshotSource>(provider.GetRequiredService<INodeSnapshotSource>());
        Assert.Single(services, d => d.ServiceType == typeof(IAccountingEventSealer));
    }

    private AccountingEventSealerService CreateSealer(int batchSize = 500, TimeSpan? interval = null,
                                                      ILogger<AccountingEventSealerService>? logger = null) =>
        new(Provider.GetRequiredService<IServiceScopeFactory>(),
            logger ?? NullLogger<AccountingEventSealerService>.Instance,
            Options.Create(new AccountingOptions
            {
                SealBatchSize = batchSize,
                SealInterval = interval ?? TimeSpan.FromHours(1)
            }));

    private async Task AddAsync(params string[] keys)
    {
        await using var scope = Provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var key in keys)
        {
            unitOfWork.AccountingEventDbRepository.Add(new AccountingEventModel
            {
                EventKey = key,
                Kind = AccountingEventKind.InvoiceSettled,
                OccurredAt = s_at,
                AmountMsat = 1_000
            });
        }

        await unitOfWork.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<AccountingEventModel>> ReadSealedAsync()
    {
        await using var scope = Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingEventDbRepository
                          .GetSealedRangeAsync(1, 1_000, TestContext.Current.CancellationToken);
    }

    private async Task WaitUntilSealedAsync(int count) =>
        await WaitUntilAsync(() => ReadSealedAsync().GetAwaiter().GetResult().Count >= count);

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

    private static void AssertChain(IReadOnlyList<AccountingEventModel> sealedEvents)
    {
        var previous = new byte[32];
        foreach (var sealedEvent in sealedEvents)
        {
            Assert.Equal(AccountingEventHasher.ComputeHash(previous, sealedEvent.LedgerSeq!.Value, sealedEvent),
                         sealedEvent.Hash);
            previous = sealedEvent.Hash!;
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

    private sealed class RecordingLogger : ILogger<AccountingEventSealerService>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public List<string> Warnings => _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message)
                                                .ToList();

        public List<string> Errors => _entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Instrument, long Value)> _measurements = new();

        public MetricRecorder(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
                                                            _measurements.Enqueue((instrument.Name, value)));
            _listener.Start();
        }

        public long Sum(string instrument) => _measurements.Where(m => m.Instrument == instrument).Sum(m => m.Value);

        public void Dispose() => _listener.Dispose();
    }
}