namespace NLightning.Daemon.Tests.Client;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Financial.Reports;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI's financial side of <c>accounting</c> (NL-602 A3-T6): the financial report kinds, <c>--book</c>,
/// <c>--currency</c>, <c>--price</c>, <c>--by</c> and the <c>seq:adjustment</c> cursor; the export's book and currency and
/// its paging through a sequence's adjustments; usage errors; and the printer's rounding to the currency's minor unit.
/// </summary>
public class AccountingFinancialCommandsTests
{
    [Theory]
    [InlineData("gains", AccountingReportKind.RealizedGains)]
    [InlineData("realized-gains", AccountingReportKind.RealizedGains)]
    [InlineData("unrealized", AccountingReportKind.UnrealizedGains)]
    [InlineData("lots", AccountingReportKind.Lots)]
    [InlineData("unvalued", AccountingReportKind.Unvalued)]
    [InlineData("risk", AccountingReportKind.RiskCapital)]
    public void Given_AFinancialReportKind_When_Parsed_Then_TheRequestNamesIt(string kind,
                                                                             AccountingReportKind expected)
    {
        // Act
        var arguments = AccountingBooksCommands.Parse(["report", kind], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal((int)expected, arguments!.Report!.Kind);
        Assert.Null(ClientApp.ValidateArguments("accounting", ["report", kind]));
    }

    [Fact]
    public void Given_TheGainsOptions_When_Parsed_Then_TheRequestCarriesThem()
    {
        // Act
        var report = AccountingBooksCommands.Parse(["report", "gains", "--by", "quarter", "--currency", "eur",
                                                    "--since", "1700000000"], out var error)!.Report!;

        // Assert
        Assert.Null(error);
        Assert.Equal((int)AccountingGainsGrouping.Quarter, report.Grouping);
        Assert.Equal("EUR", report.Currency);
        Assert.Equal(1_700_000_000, report.SinceUnixSeconds);
    }

    [Fact]
    public void Given_AFinancialBalanceSheetAtAPrice_When_Parsed_Then_TheBookCurrencyAndPriceAreSent()
    {
        // Act
        var report = AccountingBooksCommands.Parse(["report", "balance", "--book", "financial", "--currency", "usd",
                                                    "--price=86048.25"], out var error)!.Report!;

        // Assert
        Assert.Null(error);
        Assert.Equal((int)AccountingBook.Financial, report.Book);
        Assert.Equal("USD", report.Currency);
        Assert.Equal("86048.25", report.Price);
        Assert.Equal(AccountingReportKind.BalanceSheet,
                     report.ToClientRequest().Kind);
        Assert.Equal(86_048.25m, report.ToClientRequest().Price);
    }

    [Fact]
    public void Given_AFinancialRegisterCursor_When_Parsed_Then_TheSequenceAndAdjustmentAreSent()
    {
        // Act
        var report = AccountingBooksCommands.Parse(["report", "register", "--book", "fin", "--after", "7:1"],
                                                   out var error)!.Report!;
        var unclassified = AccountingBooksCommands.Parse(["report", "unclassified", "--after", "3"],
                                                         out _)!.Report!;

        // Assert
        Assert.Null(error);
        Assert.Equal((7L, (int?)1), (report.AfterLedgerSeq, report.AfterAdjustment));
        Assert.Equal((int)AccountingBook.Financial, report.Book);
        Assert.Equal((int)AccountingBook.Financial, unclassified.Book);
        Assert.Null(unclassified.AfterAdjustment);
    }

    [Theory]
    [InlineData(new[] { "report", "balance", "--currency", "usd" }, "need --book financial")]
    [InlineData(new[] { "report", "balance", "--book", "tax" }, "Unknown book 'tax'")]
    [InlineData(new[] { "report", "lots", "--price", "-1" }, "Invalid price")]
    [InlineData(new[] { "report", "risk", "--currency", "dollars" }, "Invalid currency")]
    [InlineData(new[] { "report", "gains", "--by", "week" }, "Unknown period 'week'")]
    [InlineData(new[] { "report", "register", "--after", "7:x" }, "Invalid after")]
    [InlineData(new[] { "report", "fees", "--book", "financial" }, "Unknown option '--book'")]
    [InlineData(new[] { "report", "coffee" }, "gains, unrealized, lots")]
    [InlineData(new[] { "export", "--format", "csv", "--currency", "usd" }, "--currency needs --book financial")]
    public void Given_ABadFinancialArgument_When_Validated_Then_UsageError(string[] args, string expected)
    {
        // Act
        var error = ClientApp.ValidateArguments("accounting", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Given_AFinancialExport_When_Parsed_Then_TheBookAndCurrencyAreSent()
    {
        // Act
        var export = AccountingBooksCommands.Parse(["export", "--format", "beancount", "--book", "financial",
                                                    "--currency", "chf"], out var error)!.Export!;

        // Assert
        Assert.Null(error);
        Assert.Equal((int)AccountingExportFormat.Beancount, export.Format);
        Assert.Equal((int)AccountingBook.Financial, export.Book);
        Assert.Equal("CHF", export.Currency);
        Assert.Equal(AccountingBook.Financial, export.ToClientRequest().Book);
    }

    [Fact]
    public async Task Given_PagesEndingInsideASequence_When_Exported_Then_TheAdjustmentCursorIsPassedBack()
    {
        // Arrange: a page ends at 7:0, the next at 7:1 (same sequence), the last is short
        var asked = new List<(long, int?)>();
        var pages = new Dictionary<(long, int?), AccountingExportIpcResponse>
        {
            [(0, null)] = Page("h\n7:0\n", 7, 0, true),
            [(7, 0)] = Page("7:1\n", 7, 1, true),
            [(7, 1)] = Page("8:0\n", 8, 0, false)
        };
        using var output = new StringWriter();

        // Act
        var entries = await AccountingBooksCommands.ExportAsync(
                          (request, _) =>
                          {
                              asked.Add((request.AfterLedgerSeq, request.AfterAdjustment));
                              Assert.Equal((int)AccountingBook.Financial, request.Book);
                              Assert.Equal("EUR", request.Currency);
                              return Task.FromResult(pages[(request.AfterLedgerSeq, request.AfterAdjustment)]);
                          },
                          new AccountingExportIpcRequest
                          {
                              Format = (int)AccountingExportFormat.Hledger,
                              Book = (int)AccountingBook.Financial,
                              Currency = "EUR",
                              Limit = 1
                          }, output, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("h\n7:0\n7:1\n8:0\n", output.ToString());
        Assert.Equal(3, entries);
        Assert.Equal([(0L, (int?)null), (7L, 0), (7L, 1)], asked);
    }

    [Fact]
    public void Given_AFinancialIncomeStatement_When_Printed_Then_FiatIsRoundedToTheMinorUnit()
    {
        // Arrange
        var report = new AccountingReportIpcResponse
        {
            Kind = (int)AccountingReportKind.IncomeStatement,
            KindName = "IncomeStatement",
            ProjectedLedgerSeq = 9,
            Financial = new AccountingFinancialReportIpcResponse
            {
                Currency = "JPY",
                MinorUnits = 0,
                IncomeStatement = new AccountingFinancialIncomeStatementIpcResponse
                {
                    Income = [Line("income:sales", 200_000_000, "13500.5")],
                    Expenses = [Line("expenses:payments", 1_000, "0.49")],
                    TotalIncomeMsat = 200_000_000,
                    TotalIncomeFiat = "13500.5",
                    TotalExpensesMsat = 1_000,
                    TotalExpensesFiat = "0.49",
                    NetIncomeMsat = 199_999_000,
                    NetIncomeFiat = "13500.01",
                    UnvaluedPostings = 2
                }
            }
        };
        using var output = new StringWriter();

        // Act
        new AccountingReportPrinter(output).Print(report);

        // Assert
        var printed = output.ToString();
        Assert.Contains("Financial income statement (all time) in JPY (financial book through #9)", printed);
        Assert.Contains("13501 JPY", printed);
        Assert.Contains("0 JPY", printed);
        Assert.Contains("13500 JPY", printed);
        Assert.Contains("2 posting(s) have no JPY value yet", printed);
    }

    [Fact]
    public void Given_RealizedGainsAndLots_When_Printed_Then_PeriodsPendingValuationAndLotsAreShown()
    {
        // Arrange
        var gains = new AccountingReportIpcResponse
        {
            Kind = (int)AccountingReportKind.RealizedGains,
            KindName = "RealizedGains",
            ProjectedLedgerSeq = 9,
            Financial = new AccountingFinancialReportIpcResponse
            {
                Currency = "USD",
                MinorUnits = 2,
                RealizedGains = new AccountingRealizedGainsIpcResponse
                {
                    Grouping = (int)AccountingGainsGrouping.Month,
                    GroupingName = "Month",
                    Periods = [Gains("2026-03", "25.002", 1)],
                    Total = Gains("total", "25.002", 1)
                }
            }
        };
        var lots = new AccountingReportIpcResponse
        {
            Kind = (int)AccountingReportKind.UnrealizedGains,
            KindName = "UnrealizedGains",
            ProjectedLedgerSeq = 9,
            Financial = new AccountingFinancialReportIpcResponse
            {
                Currency = "USD",
                MinorUnits = 2,
                Lots = new AccountingLotsIpcResponse
                {
                    Price = new AccountingReportPriceIpcResponse { PricePerBitcoin = "100000", Currency = "USD" },
                    Lots =
                    [
                        new AccountingLotIpcResponse
                        {
                            Id = 1, AcquiredAtUnixMilliseconds = 0, Origin = "Acquisition", OriginalMsat = 10,
                            RemainingMsat = 5, BasisEstimated = false, RemainingCostBasis = "1.234",
                            MarketValue = "2", UnrealizedGain = "0.766"
                        }
                    ],
                    NextAfterLotId = 1,
                    HasMore = true,
                    OpenLots = 2,
                    RemainingMsat = 5,
                    CostBasis = "1.234",
                    UnvaluedMsat = 0,
                    UnvaluedLots = 0,
                    BasisEstimatedMsat = 0,
                    MarketValue = "2",
                    UnrealizedGain = "0.766"
                }
            }
        };
        using var output = new StringWriter();

        // Act
        var printer = new AccountingReportPrinter(output);
        printer.Print(gains);
        printer.Print(lots);

        // Assert
        var printed = output.ToString();
        Assert.Contains("Realized gains (all time) by month in USD", printed);
        Assert.Contains("gain 25.00 USD", printed);
        Assert.Contains("pending valuation: 1 relief(s)", printed);
        Assert.Contains("market value 2.00 USD, unrealized gain 0.77 USD", printed);
        Assert.Contains("cost 1.23 USD", printed);
        Assert.Contains("More: --after 1", printed);
    }

    private static AccountingFinancialAccountIpcResponse Line(string name, long msat, string fiat) => new()
    {
        Account = 0,
        Role = "Received",
        Name = name,
        Category = "Income",
        AmountMsat = msat,
        FiatAmount = fiat,
        UnvaluedPostings = 1
    };

    private static AccountingRealizedGainsLineIpcResponse Gains(string period, string gain, int pending) => new()
    {
        Period = period,
        Reliefs = 3,
        DisposedMsat = 101_010_000,
        CostBasis = "35.004",
        Proceeds = "60.006",
        Gain = gain,
        ShortTermGain = gain,
        LongTermGain = "0",
        PendingValuation = pending,
        PendingValuationMsat = 1_000_000,
        BasisEstimatedMsat = 0
    };

    private static AccountingExportIpcResponse Page(string text, long next, int adjustment, bool more) => new()
    {
        Format = (int)AccountingExportFormat.Hledger,
        Text = text,
        NextAfter = next,
        NextAfterAdjustment = adjustment,
        HasMore = more,
        EntryCount = 1
    };
}