namespace NLightning.Daemon.Tests.Client;

using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Client.Enums;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI's <c>accounting</c> verb family (NL-602 A2): <c>report</c> kinds and their options, <c>export</c> with the
/// pages fetched and written in order, the admin subcommands, usage errors and the printers.
/// </summary>
public class AccountingBooksCommandsTests
{
    [Theory]
    [InlineData("balance", AccountingReportKind.BalanceSheet)]
    [InlineData("balance-sheet", AccountingReportKind.BalanceSheet)]
    [InlineData("Income", AccountingReportKind.IncomeStatement)]
    [InlineData("channels", AccountingReportKind.Channels)]
    [InlineData("peers", AccountingReportKind.Peers)]
    [InlineData("fees", AccountingReportKind.Fees)]
    [InlineData("register", AccountingReportKind.Register)]
    public void Given_AReportKind_When_Parsed_Then_TheRequestNamesIt(string kind, AccountingReportKind expected)
    {
        // Act
        var arguments = AccountingBooksCommands.Parse(["report", kind], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal((int)expected, arguments!.Report!.Kind);
        Assert.Null(ClientApp.ValidateArguments("accounting", ["report", kind]));
    }

    [Fact]
    public void Given_EveryRegisterOption_When_Parsed_Then_TheRequestCarriesThem()
    {
        // Arrange
        string[] args =
        [
            "report", "register", "--since", "2026-09-01", "--until=1790000000", "--channel", "800000x1x0",
            "--account", "income:lightning:routing", "--kind", "forwardsettled,InvoiceSettled", "--kind", "31",
            "--after", "12", "--limit=50"
        ];

        // Act
        var request = AccountingBooksCommands.Parse(args, out var error)!.Report!;

        // Assert
        Assert.Null(error);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                     request.SinceUnixSeconds);
        Assert.Equal(1_790_000_000, request.UntilUnixSeconds);
        Assert.Equal("800000x1x0", request.Channel);
        Assert.Equal("income:lightning:routing", request.Account);
        Assert.Equal([
                         (int)AccountingEventKind.ForwardSettled, (int)AccountingEventKind.InvoiceSettled,
                         (int)AccountingEventKind.WalletSent
                     ], request.EventKinds!);
        Assert.Equal(12, request.AfterLedgerSeq);
        Assert.Equal(50, request.Limit);
    }

    [Fact]
    public void Given_ABalanceSheetAtATime_When_Parsed_Then_TheTimeIsTheEndOfThePeriod()
    {
        // Act
        var request = AccountingBooksCommands.Parse(["report", "balance", "--at", "1790000000"], out _)!.Report!;

        // Assert
        Assert.Equal(1_790_000_000, request.UntilUnixSeconds);
        Assert.Null(request.SinceUnixSeconds);
    }

    [Fact]
    public void Given_AnExportToAFile_When_Parsed_Then_TheFormatPeriodAndPathAreKept()
    {
        // Act
        var arguments = AccountingBooksCommands.Parse(
                            ["export", "--format", "Beancount", "--since", "1000", "--until", "2000", "--output",
                             "books.beancount"], out var error)!;

        // Assert
        Assert.Null(error);
        Assert.Equal((int)AccountingExportFormat.Beancount, arguments.Export!.Format);
        Assert.Equal(1_000, arguments.Export.SinceUnixSeconds);
        Assert.Equal(2_000, arguments.Export.UntilUnixSeconds);
        Assert.Equal(AccountingBooksCommands.ExportPageSize, arguments.Export.Limit);
        Assert.Equal("books.beancount", arguments.OutputPath);
    }

    [Theory]
    [InlineData("reconcile", AccountingAdminAction.Reconcile)]
    [InlineData("rebuild", AccountingAdminAction.Rebuild)]
    [InlineData("VERIFY", AccountingAdminAction.Verify)]
    public void Given_AnAdminSubcommand_When_Parsed_Then_ItsActionIsSent(string subcommand,
                                                                         AccountingAdminAction expected)
    {
        // Act
        var arguments = AccountingBooksCommands.Parse([subcommand], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal((int)expected, arguments!.Admin!.Action);
    }

    [Theory]
    [InlineData(new string[0], "Missing accounting subcommand")]
    [InlineData(new[] { "audit" }, "Unknown accounting subcommand")]
    [InlineData(new[] { "verify", "now" }, "Unexpected argument 'now'")]
    [InlineData(new[] { "report" }, "Missing report kind")]
    [InlineData(new[] { "report", "cashflow" }, "Unknown report 'cashflow'")]
    [InlineData(new[] { "report", "balance", "--since", "1000" }, "Unknown option '--since'")]
    [InlineData(new[] { "report", "fees", "--channel", "800000x1x0" }, "Unknown option '--channel'")]
    [InlineData(new[] { "report", "income", "--since", "2000", "--until", "1000" }, "--until must be after")]
    [InlineData(new[] { "report", "income", "--since", "2000", "--since", "3000" }, "--since given twice")]
    [InlineData(new[] { "report", "register", "--limit", "0" }, "Invalid limit")]
    [InlineData(new[] { "report", "register", "--kind", "Coffee" }, "Unknown kind 'Coffee'")]
    [InlineData(new[] { "report", "register", "--after" }, "Missing value for --after")]
    [InlineData(new[] { "report", "income", "--until", "tomorrow" }, "Invalid until")]
    [InlineData(new[] { "export" }, "Missing --format")]
    [InlineData(new[] { "export", "--format", "xlsx" }, "Unknown format 'xlsx'")]
    [InlineData(new[] { "export", "--format", "csv", "books.csv" }, "Unexpected argument 'books.csv'")]
    public void Given_ABadArgument_When_Validated_Then_UsageError(string[] args, string expected)
    {
        // Act
        var error = ClientApp.ValidateArguments("accounting", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expected, error);
        Assert.Contains("Usage: accounting report", error);
    }

    [Fact]
    public async Task Given_ThreePages_When_Exported_Then_EachPageIsAskedAfterThePreviousAndWrittenInOrder()
    {
        // Arrange
        var asked = new List<AccountingExportIpcRequest>();
        var pages = new Dictionary<long, AccountingExportIpcResponse>
        {
            [0] = Page("header\nentry 1\n", 5, true, 1),
            [5] = Page("entry 2\n", 9, true, 1),
            [9] = Page(string.Empty, 9, false, 0)
        };
        using var output = new StringWriter();

        // Act
        var entries = await AccountingBooksCommands.ExportAsync(
                          (request, _) =>
                          {
                              asked.Add(request);
                              return Task.FromResult(pages[request.AfterLedgerSeq]);
                          },
                          new AccountingExportIpcRequest
                          {
                              Format = (int)AccountingExportFormat.Csv,
                              SinceUnixSeconds = 7,
                              Limit = 1
                          }, output, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("header\nentry 1\nentry 2\n", output.ToString());
        Assert.Equal(2, entries);
        Assert.Equal([0L, 5L, 9L], asked.Select(r => r.AfterLedgerSeq));
        Assert.All(asked, r =>
        {
            Assert.Equal((int)AccountingExportFormat.Csv, r.Format);
            Assert.Equal(7, r.SinceUnixSeconds);
            Assert.Equal(1, r.Limit);
        });
    }

    [Fact]
    public void Given_ABalanceSheet_When_Printed_Then_AmountsAreInMsatAndSat()
    {
        // Arrange
        var report = new AccountingReportIpcResponse
        {
            Kind = (int)AccountingReportKind.BalanceSheet,
            KindName = "BalanceSheet",
            ProjectedLedgerSeq = 12,
            BalanceSheet = new AccountingBalanceSheetIpcResponse
            {
                Assets = [Account("assets:lightning:channels", 1_234_567)],
                Liabilities = [],
                Equity = [Account("equity:transfers:in", 1_000_000)],
                RetainedEarningsMsat = 234_567,
                TotalAssetsMsat = 1_234_567,
                TotalLiabilitiesMsat = 0,
                TotalEquityMsat = 1_000_000,
                IsBalanced = true
            }
        };
        using var output = new StringWriter();

        // Act
        new AccountingReportPrinter(output).Print(report);

        // Assert
        var printed = output.ToString();
        Assert.Contains("Balance sheet at now (books through #12)", printed);
        Assert.Contains("assets:lightning:channels", printed);
        Assert.Contains("1234567 msat (1234.567 sat)", printed);
        Assert.Contains("234567 msat (234.567 sat)", printed);
        Assert.Contains("Assets = liabilities + equity + earnings: yes", printed);
    }

    [Fact]
    public void Given_ARegisterAndAdminResults_When_Printed_Then_EntriesAndTheChainStateAreShown()
    {
        // Arrange
        var register = new AccountingReportIpcResponse
        {
            Kind = (int)AccountingReportKind.Register,
            KindName = "Register",
            ProjectedLedgerSeq = 3,
            Register = new AccountingRegisterIpcResponse
            {
                Entries =
                [
                    new AccountingEntryIpcResponse
                    {
                        LedgerSeq = 3,
                        EventKey = "fwd:x:1:settled",
                        Kind = (int)AccountingEventKind.ForwardSettled,
                        KindName = "ForwardSettled",
                        OccurredAtUnixMilliseconds = 0,
                        Postings = [Account("assets:lightning:channels", 100), Account("income:lightning:routing", -100)]
                    }
                ],
                NextAfter = 3,
                HasMore = false
            }
        };
        var broken = new AccountingAdminIpcResponse
        {
            Action = (int)AccountingAdminAction.Verify,
            Verification = new AccountingVerificationIpcResponse
            {
                IsIntact = false,
                VerifiedCount = 2,
                TipLedgerSeq = 2,
                TipHash = new string('a', 64),
                BreakLedgerSeq = 3,
                BreakReason = "The stored hash does not match the event"
            }
        };
        using var output = new StringWriter();

        // Act
        new AccountingReportPrinter(output).Print(register);
        new AccountingAdminPrinter(output).Print(broken);
        new AccountingAdminPrinter(output).Print(new AccountingAdminIpcResponse
        {
            Action = (int)AccountingAdminAction.Rebuild,
            RebuiltEntries = 7
        });

        // Assert
        var printed = output.ToString();
        Assert.Contains("#3  1970-01-01 00:00:00 UTC  ForwardSettled  fwd:x:1:settled", printed);
        Assert.Contains("-100 msat (-0.1 sat)", printed);
        Assert.Contains("Next page: --after 3 (nothing more yet)", printed);
        Assert.Contains("Hash chain BROKEN at #3: The stored hash does not match the event", printed);
        Assert.Contains("Rebuilt the books: 7 entries", printed);
    }

    [Fact]
    public void Given_AReconcileWithAnOutstandingClearing_When_Printed_Then_ItIsCleanWithTheOutstandingAmountNotDrift()
    {
        // Arrange (NL-621)
        var reconcile = new AccountingAdminIpcResponse
        {
            Action = (int)AccountingAdminAction.Reconcile,
            Reconcile = new AccountingReconcileIpcResponse
            {
                TakenAtUnixMilliseconds = 0,
                BlockHeight = 3_469_200,
                LedgerSeq = 156,
                IsClean = true,
                Lines =
                [
                    new AccountingReconcileLineIpcResponse
                    {
                        Account = 4,
                        Name = "assets:onchain:clearing",
                        BooksMsat = 10_252_000,
                        NodeMsat = 0,
                        DriftMsat = 0,
                        OutstandingMsat = 10_252_000
                    },
                    new AccountingReconcileLineIpcResponse
                    {
                        Account = 3,
                        Name = "assets:onchain:wallet",
                        BooksMsat = 5_000,
                        NodeMsat = 5_000,
                        DriftMsat = 0
                    }
                ]
            }
        };
        using var output = new StringWriter();

        // Act
        new AccountingAdminPrinter(output).Print(reconcile);

        // Assert
        var printed = output.ToString();
        Assert.DoesNotContain("DRIFT", printed);
        Assert.Contains("books through #156: clean, 10252000 msat outstanding (in flight)", printed);
        Assert.Contains("books 10252000 msat, node 0 msat, outstanding 10252000 msat, drift 0 msat", printed);
        Assert.Contains("books 5000 msat, node 5000 msat, drift 0 msat", printed);
    }

    private static AccountingExportIpcResponse Page(string text, long nextAfter, bool hasMore, int entries) => new()
    {
        Format = (int)AccountingExportFormat.Csv,
        Text = text,
        NextAfter = nextAfter,
        HasMore = hasMore,
        EntryCount = entries
    };

    private static AccountingAccountIpcResponse Account(string name, long msat) => new()
    {
        Account = 1,
        Role = "Channels",
        Name = name,
        AmountMsat = msat
    };
}