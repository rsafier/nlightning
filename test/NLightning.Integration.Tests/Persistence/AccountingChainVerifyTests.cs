using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Integration.Tests.Persistence;

using Client.Printers;
using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Client.Enums;
using Domain.Persistence.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Memory;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>accounting verify</c> (ClientCommand 45, NL-602 A2) on a real SQLite schema: events committed and sealed through
/// the production unit of work verify in pages; a row whose amount was changed by SQL after sealing breaks the chain
/// at its ledger sequence, a deleted row at the gap; the result crosses the IPC envelope and is printed.
/// </summary>
public class AccountingChainVerifyTests : IAsyncLifetime
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 30, 15, 123, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<NLightningDbContext> _dbOptions = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _connection.OpenAsync(cancellationToken);
        _dbOptions = new DbContextOptionsBuilder<NLightningDbContext>()
                    .UseSqlite(_connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                    .Options;
        await using var context = CreateContext();
        await context.Database.MigrateAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Given_SealedEvents_When_Verified_Then_TheChainIsIntactUpToTheTip()
    {
        // Arrange
        await AddAndSealAsync(5);

        // Act
        using var unitOfWork = CreateUnitOfWork();
        var verification = await AccountingChainVerifier.VerifyAsync(unitOfWork.AccountingEventDbRepository, 2,
                                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.True(verification.IsIntact);
        Assert.Equal(5, verification.VerifiedCount);
        Assert.Equal(5, verification.TipLedgerSeq);
        var tip = await unitOfWork.AccountingEventDbRepository.GetChainTipAsync(TestContext.Current.CancellationToken);
        Assert.Equal(tip.Hash, verification.TipHash);
    }

    [Fact]
    public async Task Given_ARowsAmountChangedAfterSealing_When_Verified_Then_TheBreakIsAtItsLedgerSequence()
    {
        // Arrange
        await AddAndSealAsync(5);
        await using (var context = CreateContext())
            Assert.Equal(1, await context.Database.ExecuteSqlRawAsync(
                                "UPDATE \"AccountingEvents\" SET \"AmountMsat\" = \"AmountMsat\" + 1 "
                              + "WHERE \"LedgerSeq\" = 3", TestContext.Current.CancellationToken));

        // Act
        using var unitOfWork = CreateUnitOfWork();
        var verification = await AccountingChainVerifier.VerifyAsync(unitOfWork.AccountingEventDbRepository, 2,
                                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.False(verification.IsIntact);
        Assert.Equal(3, verification.BreakLedgerSeq);
        Assert.Equal(2, verification.VerifiedCount);
        Assert.Equal(2, verification.TipLedgerSeq);
        Assert.Contains("does not match", verification.BreakReason);
    }

    [Fact]
    public async Task Given_ADeletedRow_When_Verified_Then_TheGapIsTheBreak()
    {
        // Arrange
        await AddAndSealAsync(4);
        await using (var context = CreateContext())
            await context.Database.ExecuteSqlRawAsync("DELETE FROM \"AccountingEvents\" WHERE \"LedgerSeq\" = 2",
                                                      TestContext.Current.CancellationToken);

        // Act
        using var unitOfWork = CreateUnitOfWork();
        var verification = await AccountingChainVerifier.VerifyAsync(unitOfWork.AccountingEventDbRepository,
                                                                     cancellationToken: TestContext.Current
                                                                        .CancellationToken);

        // Assert
        Assert.Equal(2, verification.BreakLedgerSeq);
        Assert.Contains("missing", verification.BreakReason);
        Assert.Equal(1, verification.VerifiedCount);
    }

    [Fact]
    public async Task Given_ATamperedRow_When_VerifiedOverIpc_Then_TheBreakCrossesTheEnvelopeAndIsPrinted()
    {
        // Arrange: a node with the IPC commands but no books (verify does not need them)
        MessagePackSerializer.DefaultOptions = s_options;
        await AddAndSealAsync(3);
        await using (var context = CreateContext())
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE \"AccountingEvents\" SET \"AmountMsat\" = 0 WHERE \"LedgerSeq\" = 2",
                TestContext.Current.CancellationToken);
        await using var provider = BuildNode();
        var handler = provider.GetServices<IIpcCommandHandler>().Single(h => h.Command == ClientCommand.AccountingAdmin);

        // Act
        var response = await handler.HandleAsync(new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.AccountingAdmin,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(new AccountingAdminIpcRequest
            {
                Action = (int)AccountingAdminAction.Verify
            }, s_options, TestContext.Current.CancellationToken)
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var admin = MessagePackSerializer.Deserialize<AccountingAdminIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.False(admin.Verification!.IsIntact);
        Assert.Equal(2, admin.Verification.BreakLedgerSeq);
        using var output = new StringWriter();
        new AccountingAdminPrinter(output).Print(admin);
        Assert.Contains("Hash chain BROKEN at #2", output.ToString());
    }

    // Commits the events in two saves (as writers do), then seals them with the Domain sealer in one save
    private async Task AddAndSealAsync(int count)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var start = 0; start < count; start += 2)
        {
            using var writer = CreateUnitOfWork();
            for (var i = start; i < Math.Min(count, start + 2); i++)
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

        using var sealer = CreateUnitOfWork();
        var repository = sealer.AccountingEventDbRepository;
        var batch = await repository.GetUnsealedAsync(100, cancellationToken);
        var sealedKeys = await repository.GetSealedKeysAsync(batch.Select(e => e.EventKey).ToList(), cancellationToken);
        var seals = AccountingEventSealer.Seal(await repository.GetChainTipAsync(cancellationToken), batch, sealedKeys,
                                               out _);
        await repository.ApplySealsAsync(seals, cancellationToken);
        await sealer.SaveChangesAsync();
    }

    private UnitOfWork CreateUnitOfWork() =>
        new(CreateContext(), NullLogger<UnitOfWork>.Instance, new Sha256(), new UtxoMemoryRepository());

    private ServiceProvider BuildNode()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IChannelMemoryRepository>().Object);
        services.AddSingleton<IUtxoMemoryRepository, UtxoMemoryRepository>();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(CreateContext(), NullLogger<UnitOfWork>.Instance,
                                                             new Sha256(),
                                                             sp.GetRequiredService<IUtxoMemoryRepository>()));
        services.AddAccountingIpcServices();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private NLightningDbContext CreateContext() => new(_dbOptions, new DatabaseTypeProvider(DatabaseType.Sqlite));
}