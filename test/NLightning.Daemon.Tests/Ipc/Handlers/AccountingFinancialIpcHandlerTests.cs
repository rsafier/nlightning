using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Export;
using Domain.Accounting.Financial.Reports;
using Domain.Accounting.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Persistence.Interfaces;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The financial book over IPC 43/44 (NL-602 A3-T6): <c>--book financial</c> and the financial kinds go to the financial
/// reports with their currency, price, grouping and cursor; every report crosses the envelope with exact fiat strings;
/// the operational-only views, a bad price or book, and the financial book off are <c>invalid_operation</c>; a financial
/// export page carries its adjustment cursor both ways; the operational paths are untouched.
/// </summary>
public class AccountingFinancialIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private readonly Mock<IAccountingReports> _reports = new(MockBehavior.Strict);
    private readonly Mock<IAccountingExports> _exports = new(MockBehavior.Strict);
    private readonly Mock<IAccountingFinancialReports> _financial = new(MockBehavior.Strict);
    private readonly Mock<IAccountingFinancialExports> _financialExports = new(MockBehavior.Strict);
    private readonly Mock<IAccountingBooks> _books = new();

    public AccountingFinancialIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _books.SetupGet(b => b.IsEnabled).Returns(true);
    }

    [Fact]
    public async Task Given_AFinancialBalanceSheet_When_Reported_Then_ItsFiatAndPriceCrossTheEnvelopeExactly()
    {
        // Arrange
        var sheet = new AccountingFinancialBalanceSheet(
            s_at, 42, "EUR",
            [
                new AccountingFinancialAccountLine(AccountRole.Wallet, "assets:onchain:wallet",
                                                   AccountingAccountCategory.Assets, 1_000, 0.00000086m, 1)
                {
                    MarketValue = 0.0000007m
                }
            ], [],
            [
                new AccountingFinancialAccountLine(AccountRole.TransfersIn, "equity:transfers:in",
                                                   AccountingAccountCategory.Equity, 1_000, 0.00000086m, 0)
            ], 0, 0m)
        {
            Price = new AccountingReportPrice(70_000.5m, "EUR", null, null),
            UnvaluedPostings = 1
        };
        _financial.Setup(f => f.GetBalanceSheetAsync(s_at, "EUR", 70_000.5m, true, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(sheet);

        // Act
        var report = await ReportAsync(new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.BalanceSheet,
            UntilUnixSeconds = 1_800_000_000,
            Book = (int)AccountingBook.Financial,
            Currency = "eur",
            Price = "70000.5"
        });

        // Assert
        Assert.Null(report.BalanceSheet);
        Assert.Equal(42, report.ProjectedLedgerSeq);
        Assert.Equal(1_800_000_000_000, report.UntilUnixMilliseconds);
        var financial = report.Financial!;
        Assert.Equal(("EUR", 2), (financial.Currency, financial.MinorUnits));
        var wallet = Assert.Single(financial.BalanceSheet!.Assets);
        Assert.Equal(("assets:onchain:wallet", "Assets", 1_000L), (wallet.Name, wallet.Category, wallet.AmountMsat));
        Assert.Equal(("0.00000086", "0.0000007", 1), (wallet.FiatAmount, wallet.MarketValue, wallet.UnvaluedPostings));
        Assert.Equal("70000.5", financial.BalanceSheet.Price!.PricePerBitcoin);
        Assert.Null(financial.BalanceSheet.Price.PriceId);
        Assert.True(financial.BalanceSheet.IsBalanced);
    }

    [Fact]
    public async Task Given_TheFinancialKinds_When_Reported_Then_EachGoesToItsReportWithItsArguments()
    {
        // Arrange
        var gains = new AccountingRealizedGainsReport(null, s_at, "USD", AccountingGainsGrouping.Quarter,
                                                      [
                                                          new AccountingRealizedGainsLine("2026-Q1", s_at, s_at)
                                                          {
                                                              Reliefs = 2, CostBasis = 1.5m, Proceeds = 2m,
                                                              ShortTermGain = 0.5m
                                                          }
                                                      ], new AccountingRealizedGainsLine("total", null, s_at));
        var lot = new AccountingLot(3, s_at, AccountingLotOrigin.Opening, null, 0, null, null, 10, 4, 5m, "USD", 1,
                                    true, null);
        var lots = new AccountingLotsReport("USD", new AccountingReportPrice(1m, "USD", s_at, 9),
                                            [new AccountingLotLine(lot, 2m, 0.0000004m)], 3, true,
                                            new AccountingLotTotals { OpenLots = 7 });
        var entry = new AccountingEntry(8, "k", AccountingEventKind.PaymentSucceeded, s_at, null, null,
                                        [
                                            new AccountingPosting(AccountRole.Sent, 5)
                                            {
                                                AccountName = "expenses:unclassified", FiatAmount = 1.25m,
                                                FiatCurrency = "USD", PriceId = 9
                                            },
                                            new AccountingPosting(AccountRole.Channels, -5)
                                            {
                                                AccountName = "assets:lightning:channels", FiatAmount = -1.25m,
                                                FiatCurrency = "USD"
                                            }
                                        ])
        {
            Book = AccountingBook.Financial,
            Adjustment = 2,
            Flags = AccountingEntryFlags.Unclassified,
            Classification = AccountingClassificationSource.Default
        };
        var register = new AccountingFinancialRegister([entry], 8, 2, true, 50);
        var unvalued = new AccountingUnvaluedReport(
            [
                new AccountingUnvaluedPosting(new AccountingPostingKey(AccountingBook.Financial, 6, 0, 1), s_at,
                                              AccountRole.Routing, "income:routing", -5_000, "2026-03")
            ], true, 50);
        _financial.Setup(f => f.GetRealizedGainsAsync(null, s_at, AccountingGainsGrouping.Quarter, "USD",
                                                      It.IsAny<CancellationToken>()))
                  .ReturnsAsync(gains);
        _financial.Setup(f => f.GetLotsAsync(2, 1, "USD", null, true, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(lots);
        AccountingEntryQuery? asked = null;
        _financial.Setup(f => f.GetRegisterAsync(It.IsAny<AccountingEntryQuery>(), It.IsAny<CancellationToken>()))
                  .Callback<AccountingEntryQuery, CancellationToken>((q, _) => asked = q)
                  .ReturnsAsync(register);
        _financial.Setup(f => f.GetUnvaluedAsync(25, It.IsAny<CancellationToken>())).ReturnsAsync(unvalued);

        // Act
        var gainsReport = await ReportAsync(new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.RealizedGains,
            UntilUnixSeconds = 1_800_000_000,
            Grouping = (int)AccountingGainsGrouping.Quarter
        });
        var lotsReport = await ReportAsync(new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.UnrealizedGains,
            AfterLedgerSeq = 2,
            Limit = 1
        });
        var unclassified = await ReportAsync(new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.Unclassified,
            AfterLedgerSeq = 7,
            AfterAdjustment = 0,
            Limit = 10
        });
        var unvaluedReport = await ReportAsync(new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.Unvalued,
            Limit = 25
        });

        // Assert
        var period = Assert.Single(gainsReport.Financial!.RealizedGains!.Periods);
        Assert.Equal(("2026-Q1", 2, "1.5", "2", "0.5"), (period.Period, period.Reliefs, period.CostBasis,
                                                         period.Proceeds, period.Gain));
        Assert.Equal("Quarter", gainsReport.Financial.RealizedGains.GroupingName);
        var lotLine = Assert.Single(lotsReport.Financial!.Lots!.Lots);
        Assert.Equal((3L, "Opening", true, "2", "0.0000004", "-1.9999996"),
                     (lotLine.Id, lotLine.Origin, lotLine.BasisEstimated, lotLine.RemainingCostBasis,
                      lotLine.MarketValue, lotLine.UnrealizedGain));
        Assert.Equal((7, 3L, true), (lotsReport.Financial.Lots.OpenLots, lotsReport.Financial.Lots.NextAfterLotId,
                                     lotsReport.Financial.Lots.HasMore));
        Assert.Equal(9, lotsReport.Financial.Lots.Price!.PriceId);
        Assert.NotNull(asked);
        Assert.Equal((AccountingBook.Financial, 7L, 0, 10, AccountingEntryFlags.Unclassified),
                     (asked.Book, asked.AfterLedgerSeq, asked.AfterAdjustment, asked.Take, asked.WithFlags));
        var listed = Assert.Single(unclassified.Financial!.Register!.Entries);
        Assert.Equal((8L, 2, "Unclassified", "Default"), (listed.LedgerSeq, listed.Adjustment, listed.FlagNames,
                                                          listed.Classification));
        Assert.Equal(("expenses:unclassified", "1.25", "USD", (long?)9),
                     (listed.Postings[0].Name, listed.Postings[0].FiatAmount, listed.Postings[0].FiatCurrency,
                      listed.Postings[0].PriceId));
        Assert.Equal((8L, 2, true), (unclassified.Financial.Register.NextAfter,
                                     unclassified.Financial.Register.NextAfterAdjustment,
                                     unclassified.Financial.Register.HasMore));
        Assert.Equal(50, unclassified.ProjectedLedgerSeq);
        var posting = Assert.Single(unvaluedReport.Financial!.Unvalued!.Postings);
        Assert.Equal((6L, 1, "income:routing", -5_000L, "2026-03"),
                     (posting.LedgerSeq, posting.Index, posting.Name, posting.AmountMsat, posting.ClosedPeriodId));
    }

    [Fact]
    public async Task Given_TheRiskCapital_When_Reported_Then_ItsStatesAndChannelsCrossTheEnvelope()
    {
        // Arrange
        var bucket = new ChannelBalanceBucket(new ChannelId(Enumerable.Repeat((byte)0x21, 32).ToArray()), null,
                                              ChannelState.Open, null, 10_000, 6_000, 4_000, 1_000, 0, 0, 0, 0, true);
        var risk = AccountingRiskCapital.Build(new AccountingSnapshot(s_at, 7, [bucket],
                                                                      new WalletBalanceBucket(2_000, 0, 0)),
                                               AccountingRiskWeights.Default)
                   with
        {
            Price = new AccountingReportPrice(100_000m, "USD", null, null)
        };
        _financial.Setup(f => f.GetRiskCapitalAsync("USD", 100_000m, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(risk);

        // Act
        var report = await ReportAsync(new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.RiskCapital,
            Price = "100000"
        });

        // Assert: 2,000 wallet + 5,000 settled + 1,000 x 0.5 in flight
        var capital = report.Financial!.RiskCapital!;
        Assert.Equal(7_500, capital.WeightedMsat);
        Assert.Equal("0.0075", capital.WeightedFiat);
        Assert.Equal(4_000, capital.ExcludedRemoteMsat);
        Assert.Equal(["wallet-confirmed", "channel-settled", "outgoing-in-flight"], capital.Lines.Select(l => l.State));
        Assert.Equal("0.5", capital.Lines[2].Weight);
        Assert.Equal(5_500, Assert.Single(capital.Channels).WeightedMsat);
    }

    [Theory]
    [InlineData(AccountingReportKind.Channels, null, null, "not available for the financial book")]
    [InlineData(AccountingReportKind.BalanceSheet, "-5", null, "Invalid price")]
    [InlineData(AccountingReportKind.BalanceSheet, "1e5", null, "Invalid price")]
    [InlineData(AccountingReportKind.IncomeStatement, null, "EURO", "is not a currency code")]
    public async Task Given_ABadFinancialRequest_When_Reported_Then_InvalidOperation(
        AccountingReportKind kind, string? price, string? currency, string expected)
    {
        // Act
        var response = await HandleAsync(ClientCommand.AccountingReport, new AccountingReportIpcRequest
        {
            Kind = (int)kind,
            Book = (int)AccountingBook.Financial,
            Price = price,
            Currency = currency
        });

        // Assert
        var error = ReadError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(256)]
    public async Task Given_AnUnknownBook_When_Reported_Then_InvalidOperation(int book)
    {
        // Act
        var response = await HandleAsync(ClientCommand.AccountingReport, new AccountingReportIpcRequest
        {
            Book = book
        });

        // Assert
        Assert.Contains("Unknown accounting book", ReadError(response).Message);
    }

    [Fact]
    public async Task Given_TheFinancialBookOff_When_Reported_Then_TheReasonIsReturned()
    {
        // Arrange
        _financial.Setup(f => f.GetIncomeStatementAsync(null, null, "USD", It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new AccountingFinancialBooksDisabledException());

        // Act
        var response = await HandleAsync(ClientCommand.AccountingReport, new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.IncomeStatement,
            Book = (int)AccountingBook.Financial
        });

        // Assert
        var error = ReadError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("Accounting:Profile=Operational", error.Message);
    }

    [Fact]
    public async Task Given_NoFinancialReports_When_AFinancialKindIsAsked_Then_BooksDisabled()
    {
        // Act
        var response = await HandleAsync(ClientCommand.AccountingReport,
                                         new AccountingReportIpcRequest { Kind = (int)AccountingReportKind.Lots },
                                         withFinancial: false);

        // Assert
        Assert.Contains("books are disabled", ReadError(response).Message);
    }

    [Fact]
    public async Task Given_AFinancialExportPage_When_Asked_Then_TheAdjustmentCursorCrossesBothWays()
    {
        // Arrange
        _financialExports.Setup(e => e.ExportAsync(new AccountingFinancialExportQuery(
                                                        AccountingExportFormat.Hledger, 7, 2, null, null, "EUR", 0),
                                                    It.IsAny<CancellationToken>()))
                         .ReturnsAsync(new AccountingFinancialExportChunk("2026-03-20 x\n", 7, 1, true, 2));

        // Act
        var response = await HandleAsync(ClientCommand.AccountingExport, new AccountingExportIpcRequest
        {
            Format = (int)AccountingExportFormat.Hledger,
            AfterLedgerSeq = 7,
            AfterAdjustment = 0,
            Limit = 2,
            Book = (int)AccountingBook.Financial,
            Currency = "EUR"
        });

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var page = MessagePackSerializer.Deserialize<AccountingExportIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(("2026-03-20 x\n", 7L, (int?)1, true, 2),
                     (page.Text, page.NextAfter, page.NextAfterAdjustment, page.HasMore, page.EntryCount));
    }

    [Fact]
    public async Task Given_AnOperationalExport_When_Asked_Then_ItHasNoAdjustmentCursor()
    {
        // Arrange
        _exports.Setup(e => e.ExportAsync(new AccountingExportQuery(AccountingExportFormat.Csv, 0, 5),
                                          It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccountingExportChunk("a\n", 3, false, 1));

        // Act
        var response = await HandleAsync(ClientCommand.AccountingExport, new AccountingExportIpcRequest
        {
            Format = (int)AccountingExportFormat.Csv,
            Limit = 5
        });

        // Assert
        var page = MessagePackSerializer.Deserialize<AccountingExportIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Null(page.NextAfterAdjustment);
        Assert.Equal("a\n", page.Text);
    }

    [Fact]
    public async Task Given_AnOperationalBalanceSheet_When_Reported_Then_TheFinancialReportsAreNotAsked()
    {
        // Arrange
        _reports.Setup(r => r.GetBalanceSheetAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccountingBalanceSheet(null, 3, [], [], [], 0));

        // Act
        var report = await ReportAsync(new AccountingReportIpcRequest { Kind = (int)AccountingReportKind.BalanceSheet });

        // Assert
        Assert.NotNull(report.BalanceSheet);
        Assert.Null(report.Financial);
        _financial.VerifyNoOtherCalls();
    }

    private async Task<AccountingReportIpcResponse> ReportAsync(AccountingReportIpcRequest request)
    {
        var response = await HandleAsync(ClientCommand.AccountingReport, request);
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<AccountingReportIpcResponse>(response.Payload, s_options,
                                                                             TestContext.Current.CancellationToken);
    }

    private Task<IpcEnvelope> HandleAsync<T>(ClientCommand command, T request, bool withFinancial = true)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddSingleton(_reports.Object);
        services.AddSingleton(_exports.Object);
        services.AddSingleton(_books.Object);
        if (withFinancial)
        {
            services.AddSingleton(_financial.Object);
            services.AddSingleton(_financialExports.Object);
        }

        services.AddAccountingIpcServices();
        var handler = services.BuildServiceProvider().GetServices<IIpcCommandHandler>().Single(h => h.Command == command);
        return handler.HandleAsync(new IpcEnvelope
        {
            Version = 1,
            Command = command,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        }, TestContext.Current.CancellationToken);
    }

    private static IpcError ReadError(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }
}