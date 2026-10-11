using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Integration.Tests.Persistence;

using Application.Accounting;
using Application.Accounting.Books;
using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
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
/// The books' commands (ClientCommand 43/44, NL-602 A2) end to end on a real SQLite schema: events a writer committed
/// (unsealed) are sealed and projected by the production books service when a report is asked, the balance sheet,
/// income statement and register come from the stored entries and running balances, and an export streamed in pages
/// over the envelope concatenates to the whole journal. The posting rules are a stand-in (lane B1 implements
/// <see cref="AccountingPostingRules"/>).
/// </summary>
public class AccountingBooksIpcRoundTripTests : IAsyncLifetime
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_at = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly ChannelId s_channel = new(Enumerable.Repeat((byte)0x33, 32).ToArray());

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<NLightningDbContext> _dbOptions = null!;

    public async ValueTask InitializeAsync()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        _dbOptions = new DbContextOptionsBuilder<NLightningDbContext>()
                    .UseSqlite(_connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                    .Options;
        await using var context = CreateContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Given_CommittedEvents_When_ReportedAndExportedOverIpc_Then_TheStoredBooksAnswer()
    {
        // Arrange: a deposit, an invoice, a forward and a payment, committed unsealed in two saves
        await using var provider = BuildNode();
        await AddAsync(provider,
            Event("wallet:a:0:in", AccountingEventKind.WalletReceived, s_at, 1_000_000),
            Event("inv:1:settled", AccountingEventKind.InvoiceSettled, s_at.AddDays(1), 50_000, s_channel));
        await AddAsync(provider,
            Event("fwd:1:0:settled", AccountingEventKind.ForwardSettled, s_at.AddDays(2), 1_000, s_channel),
            Event("pay:2:succeeded", AccountingEventKind.PaymentSucceeded, s_at.AddDays(3), -20_100, s_channel, 100));

        // Act
        var balance = await ReportAsync(provider, new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.BalanceSheet
        });
        var income = await ReportAsync(provider, new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.IncomeStatement,
            SinceUnixSeconds = s_at.AddDays(1).ToUnixTimeSeconds(),
            UntilUnixSeconds = s_at.AddDays(3).ToUnixTimeSeconds()
        });
        var register = await ReportAsync(provider, new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.Register,
            Account = "Routing"
        });
        var journal = new StringWriter();
        var entries = 0;
        var after = 0L;
        while (true)
        {
            var page = await ExportAsync(provider, new AccountingExportIpcRequest
            {
                Format = (int)AccountingExportFormat.Hledger,
                AfterLedgerSeq = after,
                Limit = 3
            }, TestContext.Current.CancellationToken);
            await journal.WriteAsync(page.Text);
            entries += page.EntryCount;
            if (!page.HasMore)
                break;
            after = page.NextAfter;
        }

        var whole = await ExportAsync(provider, new AccountingExportIpcRequest
        {
            Format = (int)AccountingExportFormat.Hledger,
            Limit = 1_000
        }, TestContext.Current.CancellationToken);

        // Assert: assets = deposit + income - payment; equity = the deposit; earnings = 50,000 + 1,000 - 20,100
        var sheet = balance.BalanceSheet!;
        Assert.Equal(4, balance.ProjectedLedgerSeq);
        Assert.Equal(1_000_000 + 50_000 + 1_000 - 20_100, sheet.TotalAssetsMsat);
        Assert.Equal(1_000_000, sheet.TotalEquityMsat);
        Assert.Equal(30_900, sheet.RetainedEarningsMsat);
        Assert.True(sheet.IsBalanced);
        Assert.Equal(51_000, income.IncomeStatement!.TotalIncomeMsat);
        Assert.Empty(income.IncomeStatement.Expenses);
        var routing = Assert.Single(register.Register!.Entries);
        Assert.Equal("fwd:1:0:settled", routing.EventKey);
        Assert.Equal(["assets:lightning:channels", "income:lightning:routing"], routing.Postings.Select(p => p.Name));
        Assert.Equal(4, entries);
        Assert.Equal(whole.Text, journal.ToString());
        Assert.Contains("    expenses:lightning:routing-fees", whole.Text);
        Assert.Contains("; seq: 4", whole.Text);
    }

    // The stand-in rules: the plan's §6.1 postings for the four kinds used here
    private static IReadOnlyList<AccountingPosting> Rules(AccountingEventModel e, Func<string, AccountingEntry?> _) =>
        e.Kind switch
        {
            AccountingEventKind.WalletReceived =>
            [
                new AccountingPosting(AccountRole.Wallet, e.AmountMsat),
                new AccountingPosting(AccountRole.TransfersIn, -e.AmountMsat)
            ],
            AccountingEventKind.InvoiceSettled =>
            [
                new AccountingPosting(AccountRole.Channels, e.AmountMsat),
                new AccountingPosting(AccountRole.Received, -e.AmountMsat)
            ],
            AccountingEventKind.ForwardSettled =>
            [
                new AccountingPosting(AccountRole.Channels, e.AmountMsat),
                new AccountingPosting(AccountRole.Routing, -e.AmountMsat)
            ],
            AccountingEventKind.PaymentSucceeded =>
            [
                new AccountingPosting(AccountRole.Channels, e.AmountMsat),
                new AccountingPosting(AccountRole.Sent, -e.AmountMsat - e.FeeMsat),
                new AccountingPosting(AccountRole.RoutingFees, e.FeeMsat)
            ],
            _ => []
        };

    private static async Task<AccountingReportIpcResponse> ReportAsync(IServiceProvider provider,
                                                                       AccountingReportIpcRequest request)
    {
        var response = await Handler(provider, ClientCommand.AccountingReport)
                          .HandleAsync(Envelope(ClientCommand.AccountingReport, request),
                                       TestContext.Current.CancellationToken);
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<AccountingReportIpcResponse>(response.Payload, s_options,
                                                                             TestContext.Current.CancellationToken);
    }

    private static async Task<AccountingExportIpcResponse> ExportAsync(IServiceProvider provider,
                                                                       AccountingExportIpcRequest request,
                                                                       CancellationToken cancellationToken)
    {
        var response = await Handler(provider, ClientCommand.AccountingExport)
                          .HandleAsync(Envelope(ClientCommand.AccountingExport, request), cancellationToken);
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<AccountingExportIpcResponse>(response.Payload, s_options,
                                                                             cancellationToken);
    }

    private static IIpcCommandHandler Handler(IServiceProvider provider, ClientCommand command) =>
        provider.GetServices<IIpcCommandHandler>().Single(h => h.Command == command);

    private static IpcEnvelope Envelope<T>(ClientCommand command, T request) => new()
    {
        Version = 1,
        Command = command,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
    };

    private static async Task AddAsync(IServiceProvider provider, params AccountingEventModel[] events)
    {
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var accountingEvent in events)
            unitOfWork.AccountingEventDbRepository.Add(accountingEvent);
        await unitOfWork.SaveChangesAsync();
    }

    private static AccountingEventModel Event(string key, AccountingEventKind kind, DateTimeOffset at, long amountMsat,
                                              ChannelId? channelId = null, long fee = 0) => new()
                                              {
                                                  EventKey = key,
                                                  Kind = kind,
                                                  OccurredAt = at,
                                                  ChannelId = channelId,
                                                  AmountMsat = amountMsat,
                                                  FeeMsat = fee
                                              };

    private ServiceProvider BuildNode()
    {
        var channelMemory = new Mock<IChannelMemoryRepository>();
        channelMemory.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(channelMemory.Object);
        services.AddSingleton<IUtxoMemoryRepository, UtxoMemoryRepository>();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(CreateContext(), NullLogger<UnitOfWork>.Instance,
                                                             new Sha256(),
                                                             sp.GetRequiredService<IUtxoMemoryRepository>()));
        services.AddSingleton(Options.Create(new AccountingOptions()));
        services.AddSingleton(sp => new AccountingBooksService(sp.GetRequiredService<IServiceScopeFactory>(),
                                                               sp.GetRequiredService<ILogger<AccountingBooksService>>(),
                                                               sp.GetService<IOptions<AccountingOptions>>(),
                                                               sp.GetService<IAccountingEventSealer>(),
                                                               rules: Rules));
        services.AddAccountingServices();
        services.AddAccountingIpcServices();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private NLightningDbContext CreateContext() => new(_dbOptions, new DatabaseTypeProvider(DatabaseType.Sqlite));
}