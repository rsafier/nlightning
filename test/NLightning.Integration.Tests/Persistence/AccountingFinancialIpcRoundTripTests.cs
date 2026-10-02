using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Integration.Tests.Persistence;

using Application.Accounting;
using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Reports;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
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
/// The financial book's commands (ClientCommand 43/44 with <c>--book financial</c>, NL-602 A3-T6) end to end on a real
/// SQLite schema with the production registrations (<c>AddAccountingServices</c>, <c>AddAccountingIpcServices</c>): a
/// financial book stored as the projector (A3-T4) will store it answers the balance sheet, the income statement, the
/// realized gains and the unrealized gains at the stored price, an export paged by one entry concatenates to the whole
/// journal, and the operational book's reports are not touched by it.
/// </summary>
public class AccountingFinancialIpcRoundTripTests : IAsyncLifetime
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_at = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

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
    public async Task Given_AStoredFinancialBook_When_ReportedAndExportedOverIpc_Then_TheBookAnswers()
    {
        // Arrange: a deposit of 0.01 BTC at 50,000 USD (500), then a payment of 0.001 BTC at 60,000 (60) whose lot part
        // cost 50: a realized gain of 10; 0.009 BTC left at 60,000 is 540 against a cost of 450
        await using var provider = BuildNode();
        await SeedAsync(provider);

        // Act
        var sheet = await ReportAsync(provider, new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.BalanceSheet,
            Book = (int)AccountingBook.Financial
        });
        var income = await ReportAsync(provider, new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.IncomeStatement,
            Book = (int)AccountingBook.Financial,
            SinceUnixSeconds = s_at.AddDays(1).ToUnixTimeSeconds()
        });
        var gains = await ReportAsync(provider, new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.RealizedGains,
            Grouping = (int)AccountingGainsGrouping.Year
        });
        var unrealized = await ReportAsync(provider, new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.UnrealizedGains
        });
        var operational = await ReportAsync(provider, new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.BalanceSheet
        });
        var journal = new StringWriter();
        long after = 0;
        int? afterAdjustment = null;
        while (true)
        {
            var page = await ExportAsync(provider, new AccountingExportIpcRequest
            {
                Format = (int)AccountingExportFormat.Hledger,
                Book = (int)AccountingBook.Financial,
                AfterLedgerSeq = after,
                AfterAdjustment = afterAdjustment,
                Limit = 1
            });
            await journal.WriteAsync(page.Text);
            if (!page.HasMore)
                break;

            (after, afterAdjustment) = (page.NextAfter, page.NextAfterAdjustment);
        }

        var whole = await ExportAsync(provider, new AccountingExportIpcRequest
        {
            Format = (int)AccountingExportFormat.Hledger,
            Book = (int)AccountingBook.Financial,
            Limit = 1_000
        });

        // Assert
        var balance = sheet.Financial!.BalanceSheet!;
        Assert.Equal(2, sheet.ProjectedLedgerSeq);
        Assert.Equal(900_000_000, balance.TotalAssetsMsat);
        Assert.Equal("450", balance.TotalAssetsFiat);
        Assert.Equal("500", balance.TotalEquityFiat);
        Assert.Equal("-50", balance.RetainedEarningsFiat);
        Assert.True(balance.IsBalanced);
        Assert.True(balance.IsFiatBalanced);
        Assert.Equal(["income:gains:realized"], income.Financial!.IncomeStatement!.Income.Select(l => l.Name));
        Assert.Equal("-50", income.Financial.IncomeStatement.NetIncomeFiat);
        var year = Assert.Single(gains.Financial!.RealizedGains!.Periods);
        Assert.Equal(("2026", "10", "50", "60"), (year.Period, year.Gain, year.CostBasis, year.Proceeds));
        Assert.Equal("60000", unrealized.Financial!.Lots!.Price!.PricePerBitcoin);
        Assert.Equal("540", unrealized.Financial.Lots.MarketValue);
        Assert.Equal("90", unrealized.Financial.Lots.UnrealizedGain);
        Assert.Empty(operational.BalanceSheet!.Assets);
        Assert.Null(operational.Financial);
        Assert.Equal(whole.Text, journal.ToString());
        Assert.Contains("-100000000 msat @@ 50 USD", whole.Text);
        Assert.Contains("P 2026-10-02 08:00:00 msat 0.0000006 USD", whole.Text);
    }

    private async Task SeedAsync(IServiceProvider provider)
    {
        await using (var scope = provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.AccountingPriceDbRepository.TryAddAsync(
                new AccountingPrice(0, "USD", s_at, 50_000m, AccountingPriceSource.Import, s_at));
            await unitOfWork.AccountingPriceDbRepository.TryAddAsync(
                new AccountingPrice(0, "USD", s_at.AddDays(1), 60_000m, AccountingPriceSource.Http, s_at));
            await unitOfWork.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var prices = await unitOfWork.AccountingPriceDbRepository.ListAsync("USD", null, null, 10);
            var books = unitOfWork.AccountingBooksDbRepository;
            await books.AddEntryAsync(Entry(1, AccountingEventKind.WalletReceived, s_at,
                                            Line(AccountRole.Wallet, "assets:onchain:wallet", 1_000_000_000, 500m,
                                                 prices[0].Id),
                                            Line(AccountRole.TransfersIn, "equity:transfers:in", -1_000_000_000, -500m,
                                                 prices[0].Id)));
            await books.AddEntryAsync(Entry(2, AccountingEventKind.WalletSent, s_at.AddDays(1),
                                            Line(AccountRole.Sent, "expenses:payments", 100_000_000, 60m,
                                                 prices[1].Id),
                                            Line(AccountRole.Wallet, "assets:onchain:wallet", -100_000_000, -50m,
                                                 null),
                                            Line(AccountRole.Wallet, "income:gains:realized", 0, -10m, null)));
            await books.SetCursorAsync(AccountingBook.Financial, 2);
            var lot = await unitOfWork.AccountingLotDbRepository.AddLotAsync(
                          new AccountingLot(0, s_at, AccountingLotOrigin.Acquisition, 1, 0, null, null, 1_000_000_000,
                                            900_000_000, 500m, "USD", prices[0].Id, false, null));
            unitOfWork.AccountingLotDbRepository.AddRelief(new AccountingLotRelief(0, lot, 2, 0, s_at.AddDays(1),
                                                                                   100_000_000, 50m, 60m, null));
            await unitOfWork.SaveChangesAsync();
        }
    }

    private static AccountingEntry Entry(long seq, AccountingEventKind kind, DateTimeOffset at,
                                         params AccountingPosting[] postings) =>
        new(seq, $"e:{seq}", kind, at, null, null, postings) { Book = AccountingBook.Financial };

    private static AccountingPosting Line(AccountRole role, string name, long msat, decimal fiat, long? priceId) =>
        new(role, msat) { AccountName = name, FiatAmount = fiat, FiatCurrency = "USD", PriceId = priceId };

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
                                                                       AccountingExportIpcRequest request)
    {
        var response = await Handler(provider, ClientCommand.AccountingExport)
                          .HandleAsync(Envelope(ClientCommand.AccountingExport, request),
                                       TestContext.Current.CancellationToken);
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<AccountingExportIpcResponse>(response.Payload, s_options,
                                                                             TestContext.Current.CancellationToken);
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
        services.AddAccountingServices();
        services.AddAccountingIpcServices();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private NLightningDbContext CreateContext() => new(_dbOptions, new DatabaseTypeProvider(DatabaseType.Sqlite));
}