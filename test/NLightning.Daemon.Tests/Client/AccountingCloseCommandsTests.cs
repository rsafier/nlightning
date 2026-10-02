namespace NLightning.Daemon.Tests.Client;

using Domain.Accounting.Books;
using Domain.Client.Enums;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI's period close verbs (NL-602 A3-T5): <c>accounting close &lt;period&gt; [--force]</c>, <c>close list</c>,
/// <c>close show &lt;period&gt;</c> and <c>rebuild --book</c>, their usage errors and the printer.
/// </summary>
public class AccountingCloseCommandsTests
{
    [Theory]
    [InlineData(new[] { "close", "2026-09" }, AccountingAdminAction.Close, "2026-09", false)]
    [InlineData(new[] { "close", "--force", "2026-09" }, AccountingAdminAction.Close, "2026-09", true)]
    [InlineData(new[] { "close", "2026-07-01..2026-09-30", "--FORCE" }, AccountingAdminAction.Close,
                "2026-07-01..2026-09-30", true)]
    [InlineData(new[] { "close", "show", "2026-09" }, AccountingAdminAction.CloseShow, "2026-09", false)]
    [InlineData(new[] { "close", "list" }, AccountingAdminAction.CloseList, null, false)]
    public void Given_ACloseCommand_When_Parsed_Then_TheRequestCarriesIt(string[] args, AccountingAdminAction action,
                                                                        string? period, bool force)
    {
        // Act
        var arguments = AccountingBooksCommands.Parse(args, out var error);

        // Assert
        Assert.Null(error);
        var request = arguments!.Admin!;
        Assert.Equal((int)action, request.Action);
        Assert.Equal(period, request.Period);
        Assert.Equal(force, request.Force);
        Assert.Null(ClientApp.ValidateArguments("accounting", args));
    }

    [Theory]
    [InlineData(new string[] { }, AccountingBook.Operational, false)]
    [InlineData(new[] { "--book", "financial" }, AccountingBook.Financial, true)]
    [InlineData(new[] { "--book=Operational" }, AccountingBook.Operational, true)]
    public void Given_ARebuild_When_Parsed_Then_TheBookIsSetOnlyWhenNamed(string[] options, AccountingBook book,
                                                                         bool named)
    {
        // Act
        var arguments = AccountingBooksCommands.Parse(["rebuild", .. options], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal((int)AccountingAdminAction.Rebuild, arguments!.Admin!.Action);
        Assert.Equal(named ? (int)book : null, arguments.Admin.Book);
    }

    [Theory]
    [InlineData(new[] { "close" }, "Missing period")]
    [InlineData(new[] { "close", "September" }, "not a period")]
    [InlineData(new[] { "close", "2026-09", "2026-10" }, "Unexpected argument '2026-10'")]
    [InlineData(new[] { "close", "show", "2026-09", "--force" }, "Unexpected argument '--force'")]
    [InlineData(new[] { "close", "list", "2026-09" }, "Unexpected argument")]
    [InlineData(new[] { "close", "show" }, "A period is required")]
    [InlineData(new[] { "rebuild", "--book", "fiat" }, "Unknown book 'fiat'")]
    [InlineData(new[] { "rebuild", "--since", "1" }, "Unknown option")]
    public void Given_ABadCloseOrRebuild_When_Validated_Then_TheErrorSaysWhy(string[] args, string expected)
    {
        // Act
        var error = AccountingBooksCommands.Validate(args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Given_ClosesListedShownAndVerified_When_Printed_Then_EachLineSaysWhatItIs()
    {
        // Arrange
        var period = new AccountingPeriodIpcResponse
        {
            PeriodId = "2026-09",
            StartUnixSeconds = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            EndUnixSeconds = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            State = 1,
            ClosedAtUnixMilliseconds = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero)
               .ToUnixTimeMilliseconds(),
            LastLedgerSeq = 17,
            ChainHash = new string('a', 64),
            Digest = new string('b', 64),
            Signature = new string('c', 128),
            NodeId = "02" + new string('d', 64),
            Forced = true,
            ReplayAfterLedgerSeq = 15,
            Balances =
            [
                new AccountingPeriodBalanceIpcResponse
                {
                    Account = 1,
                    AccountName = "assets:channels",
                    BalanceMsat = 5_000,
                    FiatAmount = "1.25"
                }
            ],
            EntryCount = 4,
            ReliefCount = 2,
            OpenLotCount = 3,
            UnvaluedPostings = 1,
            UnclassifiedEntries = 0
        };
        var output = new StringWriter();
        var printer = new AccountingAdminPrinter(output);

        // Act
        printer.Print(new AccountingAdminIpcResponse { Action = 21, Periods = [period] });
        printer.Print(new AccountingAdminIpcResponse { Action = 22, Period = period });
        printer.Print(new AccountingAdminIpcResponse
        {
            Action = 3,
            PeriodVerifications =
            [
                new AccountingPeriodVerificationIpcResponse
                {
                    PeriodId = "2026-09",
                    IsIntact = true,
                    DigestMatches = true,
                    SignatureValid = true,
                    ChainHashMatches = true,
                    ClosingStateMatches = true,
                    Contiguous = true,
                    EntryCount = 4,
                    ReliefCount = 2,
                    OpenLotCount = 3
                },
                new AccountingPeriodVerificationIpcResponse
                {
                    PeriodId = "2026-10",
                    IsIntact = false,
                    DigestMatches = false,
                    SignatureValid = true,
                    ChainHashMatches = true,
                    ClosingStateMatches = true,
                    Contiguous = true,
                    EntryCount = 1,
                    ReliefCount = 0,
                    OpenLotCount = 3,
                    Problem = "the digest does not match"
                }
            ]
        });
        printer.Print(new AccountingAdminIpcResponse { Action = 21, Periods = [] });

        // Assert
        var text = output.ToString();
        Assert.Contains("2026-09                 closed  2026-09-01 to 2026-09-30  closed 2026-10-02 12:00:00 UTC  "
                      + "through #17  digest " + new string('b', 64) + "  FORCED", text);
        Assert.Contains("Period 2026-09 (closed): 2026-09-01 to 2026-09-30, closed with --force", text);
        Assert.Contains("  signature      " + new string('c', 128), text);
        Assert.Contains("  covers         4 entries, 2 reliefs, 3 lots open at its end", text);
        Assert.Contains("  left open      1 unvalued postings, 0 unclassified entries", text);
        Assert.Contains("  rebuild from   after #15", text);
        Assert.Contains("assets:channels", text);
        Assert.Contains("Close 2026-09 OK: digest, signature and chain hash verified (4 entries, 2 reliefs, 3 open "
                      + "lots)", text);
        Assert.Contains("Close 2026-10 BROKEN: the digest does not match", text);
        Assert.Contains("No accounting periods closed yet", text);
    }
}