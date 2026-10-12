using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Integration.Tests.Persistence;

using Application.Accounting;
using Client.Printers;
using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
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
/// NL-602 end to end on a real SQLite schema: events a writer committed (unsealed, in several saves) are sealed by
/// <c>listaccountingevents</c> (ClientCommand 41) itself before it pages them over the IPC envelope, the cursor walks
/// the feed in ledger order, filters apply, and the CLI printer shows them; <c>accountingsnapshot</c> (42) answers
/// from the production snapshot source.
/// </summary>
public class AccountingIpcRoundTripTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 30, 15, 123, TimeSpan.Zero);
    private static readonly ChannelId s_channel = new(Enumerable.Repeat((byte)0x33, 32).ToArray());

    [Fact]
    public async Task Given_UnsealedEvents_When_ListedOverIpc_Then_TheyAreSealedAndPagedInLedgerOrder()
    {
        // Arrange
        MessagePackSerializer.DefaultOptions = s_options;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<NLightningDbContext>()
                       .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                       .Options;
        await using (var context = CreateContext(dbOptions))
            await context.Database.MigrateAsync(cancellationToken);

        await using var provider = BuildNode(dbOptions, []);
        await AddAsync(provider,
                       Event(AccountingEventKeys.InvoiceSettled(Hash(1)), AccountingEventKind.InvoiceSettled, 50_000),
                       Event("fwd:x:0:settled", AccountingEventKind.ForwardSettled, 1_000, s_channel));
        await AddAsync(provider,
                       Event(AccountingEventKeys.PaymentSucceeded(Hash(2)), AccountingEventKind.PaymentSucceeded,
                             -20_000, fee: 12),
                       Event("fwd:x:1:settled", AccountingEventKind.ForwardSettled, 2_000, s_channel));
        var handler = provider.GetServices<IIpcCommandHandler>()
                              .Single(h => h.Command == ClientCommand.ListAccountingEvents);

        // Act: two pages of two, then the forwards of the channel only
        var first = await ListAsync(handler, new ListAccountingEventsIpcRequest { Limit = 2 });
        var second = await ListAsync(handler, new ListAccountingEventsIpcRequest
        {
            AfterLedgerSeq = first.NextAfter,
            Limit = 2
        });
        var third = await ListAsync(handler, new ListAccountingEventsIpcRequest
        {
            AfterLedgerSeq = second.NextAfter,
            Limit = 2
        });
        var forwards = await ListAsync(handler, new ListAccountingEventsIpcRequest
        {
            Kinds = [(int)AccountingEventKind.ForwardSettled],
            Channel = s_channel.ToString()
        });

        // Assert
        Assert.Equal([1L, 2L], first.Events.Select(e => e.LedgerSeq));
        Assert.Equal(2, first.NextAfter);
        Assert.True(first.HasMore);
        Assert.Equal(4, first.ChainTipLedgerSeq);
        Assert.Equal([3L, 4L], second.Events.Select(e => e.LedgerSeq));
        Assert.Empty(third.Events);
        Assert.Equal(4, third.NextAfter);
        Assert.False(third.HasMore);
        Assert.Equal(["fwd:x:0:settled", "fwd:x:1:settled"], forwards.Events.Select(e => e.EventKey));
        var payment = second.Events[0];
        Assert.Equal(-20_000, payment.AmountMsat);
        Assert.Equal(12, payment.FeeMsat);
        Assert.Equal(Hash(2).ToString(), payment.PaymentHash);

        // The chain the IPC reports is the stored one
        await using (var context = CreateContext(dbOptions))
        {
            var sealedEvents = await new Infrastructure.Repositories.Database.Accounting
                                        .AccountingEventDbRepository(context)
                                        .GetSealedRangeAsync(1, 10, cancellationToken);
            var previous = new byte[32];
            foreach (var (sealedEvent, listed) in sealedEvents.Zip(first.Events.Concat(second.Events)))
            {
                var hash = AccountingEventHasher.ComputeHash(previous, sealedEvent.LedgerSeq!.Value, sealedEvent);
                Assert.Equal(Convert.ToHexStringLower(hash), listed.Hash);
                previous = hash;
            }
        }

        using var output = new StringWriter();
        new ListAccountingEventsPrinter(output).Print(second);
        var printed = output.ToString();
        Assert.Contains("PaymentSucceeded", printed);
        Assert.Contains("-20000 msat   Fee: 12 msat", printed);
        Assert.Contains("Next page: --after 4", printed);
    }

    [Fact]
    public async Task Given_AWalletAndAChannel_When_SnapshottedOverIpc_Then_TheBucketsAreReturned()
    {
        // Arrange
        MessagePackSerializer.DefaultOptions = s_options;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<NLightningDbContext>()
                       .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                       .Options;
        await using (var context = CreateContext(dbOptions))
            await context.Database.MigrateAsync(cancellationToken);

        await using var provider = BuildNode(dbOptions, []);
        var walletAddress = SqliteTestDatabase.CreateWalletAddress();
        provider.GetRequiredService<IUtxoMemoryRepository>()
                .Add(SqliteTestDatabase.CreateUtxo(walletAddress, amountSats: 250_000));
        var handler = provider.GetServices<IIpcCommandHandler>()
                              .Single(h => h.Command == ClientCommand.AccountingSnapshot);

        // Act
        var response = await handler.HandleAsync(new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.AccountingSnapshot,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(new AccountingSnapshotIpcRequest(), s_options,
                                                      cancellationToken)
        }, cancellationToken);

        // Assert: no chain monitor, so height 0 and the UTXO (at height 100) counts as unconfirmed
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var snapshot = MessagePackSerializer.Deserialize<AccountingSnapshotIpcResponse>(response.Payload, s_options,
            cancellationToken);
        Assert.Empty(snapshot.Channels);
        Assert.Equal(0u, snapshot.BlockHeight);
        Assert.Equal(250_000_000, snapshot.WalletConfirmedMsat + snapshot.WalletUnconfirmedMsat);
        Assert.Equal(250_000_000, snapshot.TotalMsat);
    }

    private static async Task<ListAccountingEventsIpcResponse> ListAsync(IIpcCommandHandler handler,
                                                                         ListAccountingEventsIpcRequest request)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var response = await handler.HandleAsync(new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.ListAccountingEvents,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, cancellationToken)
        }, cancellationToken);
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<ListAccountingEventsIpcResponse>(response.Payload, s_options,
                                                                                  cancellationToken);
    }

    private static async Task AddAsync(IServiceProvider provider, params AccountingEventModel[] events)
    {
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var accountingEvent in events)
            unitOfWork.AccountingEventDbRepository.Add(accountingEvent);
        await unitOfWork.SaveChangesAsync();
    }

    private static ServiceProvider BuildNode(DbContextOptions<NLightningDbContext> dbOptions,
                                             List<ChannelModel> channels)
    {
        var channelMemory = new Mock<IChannelMemoryRepository>();
        channelMemory.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                     .Returns((Func<ChannelModel, bool> predicate) => channels.Where(predicate).ToList());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(channelMemory.Object);
        services.AddSingleton<IUtxoMemoryRepository, UtxoMemoryRepository>();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(CreateContext(dbOptions),
                                                             NullLogger<UnitOfWork>.Instance, new Sha256(),
                                                             sp.GetRequiredService<IUtxoMemoryRepository>()));
        services.AddSingleton(Options.Create(new AccountingOptions { SealBatchSize = 3 }));
        services.AddAccountingServices();
        services.AddAccountingIpcServices();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static NLightningDbContext CreateContext(DbContextOptions<NLightningDbContext> dbOptions) =>
        new(dbOptions, new DatabaseTypeProvider(DatabaseType.Sqlite));

    private static AccountingEventModel Event(string key, AccountingEventKind kind, long amountMsat,
                                              ChannelId? channelId = null, long fee = 0) =>
        new()
        {
            EventKey = key,
            Kind = kind,
            OccurredAt = s_at,
            ChannelId = channelId,
            PaymentHash = kind == AccountingEventKind.PaymentSucceeded ? Hash(2) : (Hash?)null,
            AmountMsat = amountMsat,
            FeeMsat = fee,
            Details = AccountingDetailsCodec.Create(("source", "test"))
        };

    private static Hash Hash(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());
}