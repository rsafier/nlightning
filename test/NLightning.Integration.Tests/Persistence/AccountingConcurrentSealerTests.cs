using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Persistence;

using Application.Accounting;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Node.Fencing;
using Domain.Persistence.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-1390: the accounting feed's ledger sequence is unique and a seal only updates a row still unsealed, so two
/// sealers over one SQLite file (two processes) never fork the hash chain: the second save of a batch sealed from the
/// same tip fails whole, and the sealer service seals it again from the new tip.
/// </summary>
public sealed class AccountingConcurrentSealerTests : IDisposable
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteFileTestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task Given_TwoSealersOfTheSameBatch_When_BothSave_Then_TheSecondFailsAndTheChainIsTheFirsts()
    {
        // Arrange: both read the same unsealed rows and tip and seal them
        var cancellationToken = TestContext.Current.CancellationToken;
        await AddEventsAsync(0, 3);
        using var first = CreateUnitOfWork();
        using var second = CreateUnitOfWork();
        await StageSealsAsync(first, 10);
        await StageSealsAsync(second, 10);

        // Act
        await first.SaveChangesAsync();
        var refused = await Record.ExceptionAsync(() => second.SaveChangesAsync());

        // Assert
        Assert.IsType<DbUpdateException>(refused, exactMatch: false);
        await AssertChainAsync(3, cancellationToken);
    }

    [Fact]
    public async Task Given_TwoSealersOfDifferentRowsFromOneTip_When_BothSave_Then_TheLedgerSequenceIsNotReused()
    {
        // Arrange: the first sealer saw only the first row (the second was not committed yet when it read), the second
        // only the second (the first row was marked a duplicate of nothing: it is simply left out of its batch)
        var cancellationToken = TestContext.Current.CancellationToken;
        await AddEventsAsync(0, 2);
        using var first = CreateUnitOfWork();
        using var second = CreateUnitOfWork();
        var unsealed = await first.AccountingEventDbRepository.GetUnsealedAsync(10, cancellationToken);
        var tip = await first.AccountingEventDbRepository.GetChainTipAsync(cancellationToken);
        await first.AccountingEventDbRepository.ApplySealsAsync(
            AccountingEventSealer.Seal(tip, [unsealed[0]], new HashSet<string>(), out _), cancellationToken);
        await second.AccountingEventDbRepository.ApplySealsAsync(
            AccountingEventSealer.Seal(tip, [unsealed[1]], new HashSet<string>(), out _), cancellationToken);

        // Act
        await first.SaveChangesAsync();
        var refused = await Record.ExceptionAsync(() => second.SaveChangesAsync());

        // Assert: ledger sequence 1 is the first row's only; the second row stays unsealed for the next round
        Assert.IsType<DbUpdateException>(refused, exactMatch: false);
        await AssertChainAsync(1, cancellationToken);
        using var read = CreateUnitOfWork();
        Assert.Single(await read.AccountingEventDbRepository.GetUnsealedAsync(10, cancellationToken));
    }

    [Fact]
    public async Task Given_TwoSealerServicesOverOneDatabase_When_EventsArriveWhileBothSeal_Then_OneLinearChain()
    {
        // Arrange: two processes, each with its own sealer service
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var processA = BuildProcess();
        await using var processB = BuildProcess();
        var options = Microsoft.Extensions.Options.Options.Create(new AccountingOptions { SealBatchSize = 4 });
        await using var sealerA = new AccountingEventSealerService(processA.GetRequiredService<IServiceScopeFactory>(),
                                                                   NullLogger<AccountingEventSealerService>.Instance,
                                                                   options);
        await using var sealerB = new AccountingEventSealerService(processB.GetRequiredService<IServiceScopeFactory>(),
                                                                   NullLogger<AccountingEventSealerService>.Instance,
                                                                   options);
        const int events = 60;

        // Act: events are written while both seal as fast as they can
        var writing = Task.Run(async () =>
        {
            for (var i = 0; i < events; i += 3)
                await AddEventsAsync(i, 3);
        }, cancellationToken);
        async Task SealUntilWrittenAsync(AccountingEventSealerService sealer)
        {
            while (!writing.IsCompleted)
                await sealer.SealNowAsync(cancellationToken);
            await sealer.SealNowAsync(cancellationToken);
        }

        await Task.WhenAll(writing, SealUntilWrittenAsync(sealerA), SealUntilWrittenAsync(sealerB));
        await sealerA.SealNowAsync(cancellationToken);

        // Assert: every event sealed exactly once on one linear chain
        Assert.Equal(events, sealerA.TotalSealed + sealerB.TotalSealed);
        await AssertChainAsync(events, cancellationToken);
    }

    [Fact]
    public async Task Given_AFencedSealer_When_ItSaves_Then_TheRefusalIsNotTakenForAConflict()
    {
        // Arrange: a process the node write fence refuses
        var cancellationToken = TestContext.Current.CancellationToken;
        await AddEventsAsync(0, 3);
        var fence = new FakeNodeWriteFence { Refuse = true };
        await using var process = BuildProcess(fence);
        await using var sealer = new AccountingEventSealerService(process.GetRequiredService<IServiceScopeFactory>(),
                                                                  NullLogger<AccountingEventSealerService>.Instance);

        // Act & Assert: the refusal propagates (the round is retried later), nothing is sealed or counted
        await Assert.ThrowsAsync<NodeFencedException>(() => sealer.SealNowAsync(cancellationToken));
        Assert.Equal(0, sealer.TotalConflicts);
        using var read = CreateUnitOfWork();
        Assert.Equal(3, (await read.AccountingEventDbRepository.GetUnsealedAsync(10, cancellationToken)).Count);
    }

    [Fact]
    public void Given_SaveFailures_When_Classified_Then_OnlyTheSealConflictsAre()
    {
        // Arrange
        using var unitOfWork = CreateUnitOfWork();
        var repository = unitOfWork.AccountingEventDbRepository;

        // Act & Assert
        Assert.True(repository.IsSealConflict(new DbUpdateConcurrencyException("0 rows")));
        Assert.False(repository.IsSealConflict(new DbUpdateException("other")));
        Assert.False(repository.IsSealConflict(new NodeFencedException("fenced")));
        Assert.False(repository.IsSealConflict(new InvalidOperationException("down")));
    }

    private async Task AssertChainAsync(int sealedCount, CancellationToken cancellationToken)
    {
        using var unitOfWork = CreateUnitOfWork();
        var verification = await AccountingChainVerifier.VerifyAsync(unitOfWork.AccountingEventDbRepository, 7,
                                                                     cancellationToken);
        Assert.True(verification.IsIntact);
        Assert.Equal(sealedCount, verification.VerifiedCount);
        Assert.Equal(sealedCount, verification.TipLedgerSeq);
    }

    private static async Task StageSealsAsync(IUnitOfWork unitOfWork, int max)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = unitOfWork.AccountingEventDbRepository;
        var batch = await repository.GetUnsealedAsync(max, cancellationToken);
        var sealedKeys = await repository.GetSealedKeysAsync(batch.Select(e => e.EventKey).ToList(), cancellationToken);
        var seals = AccountingEventSealer.Seal(await repository.GetChainTipAsync(cancellationToken), batch, sealedKeys,
                                               out _);
        await repository.ApplySealsAsync(seals, cancellationToken);
    }

    private async Task AddEventsAsync(int start, int count)
    {
        using var writer = CreateUnitOfWork();
        for (var i = start; i < start + count; i++)
            writer.AccountingEventDbRepository.Add(new AccountingEventModel
            {
                EventKey = $"test:{i}",
                Kind = AccountingEventKind.ForwardSettled,
                OccurredAt = s_at.AddMinutes(i),
                AmountMsat = 1_000 + i,
                Details = AccountingDetailsCodec.Create(("description", $"forward {i}"))
            });
        await writer.SaveChangesAsync();
    }

    private UnitOfWork CreateUnitOfWork() =>
        new(_database.CreateContext(), NullLogger<UnitOfWork>.Instance, new Sha256(), new UtxoMemoryRepository());

    private ServiceProvider BuildProcess(INodeWriteFence? writeFence = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUtxoMemoryRepository, UtxoMemoryRepository>();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(_database.CreateContext(), NullLogger<UnitOfWork>.Instance,
                                                             new Sha256(),
                                                             sp.GetRequiredService<IUtxoMemoryRepository>(),
                                                             writeFence: writeFence));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}