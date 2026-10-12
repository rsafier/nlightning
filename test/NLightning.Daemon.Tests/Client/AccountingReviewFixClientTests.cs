namespace NLightning.Daemon.Tests.Client;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Responses;
using NLightning.Client;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of the accounting live review's fixes (NL-602, 2026-10-02): a past balance names its last entry
/// (NL-627), the channel report's open time (NL-623) and yields (NL-625), and a short usage error (NL-628).
/// </summary>
public class AccountingReviewFixClientTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 8, 13, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(146L, "Balance sheet at 2026-10-02 08:13:00 UTC (last entry #146; books through #156)")]
    [InlineData(0L, "Balance sheet at 2026-10-02 08:13:00 UTC (no entries before it; books through #156)")]
    public void Given_APastBalanceSheet_When_Printed_Then_TheHeaderNamesTheLastEntryItCounts(long last,
        string expected)
    {
        // Arrange (NL-627)
        var sheet = new AccountingBalanceSheet(s_at, 156, [], [], [], 0) { LastLedgerSeqAt = last };
        var report = AccountingReportIpcResponse.FromClientResponse(
            new AccountingReportClientResponse(AccountingReportKind.BalanceSheet, new AccountNames())
            {
                BalanceSheet = sheet
            });
        using var output = new StringWriter();

        // Act
        new AccountingReportPrinter(output).Print(report);

        // Assert
        Assert.Equal(last, report.BalanceSheet!.LastLedgerSeqAt);
        Assert.StartsWith(expected, output.ToString());
        Assert.DoesNotContain("(books through #156)", output.ToString());
    }

    [Fact]
    public void Given_ChannelLines_When_ConvertedAndPrinted_Then_TheOpenTimeAndBothYieldsAreLabelled()
    {
        // Arrange (NL-623, NL-625): one channel dated by its funding block, one known only since the cutover
        var funded = AccountingChannelIpcResponse.From(new AccountingChannelLine
        {
            ChannelId = new ChannelId(Enumerable.Repeat((byte)0xA1, 32).ToArray()),
            ShortChannelId = "3463271x11x0",
            CapacityMsat = 1_000_000_000,
            OpenedAt = s_at.AddDays(-30),
            OpenedAtBlockHeight = 3_463_271,
            TrackedSince = s_at,
            RoutingOutMsat = 2_005,
            FundingFeeMsat = 155_000,
            YieldOnCapacity = 0.000002005,
            AnnualizedYield = 0.0000244,
            NetYieldOnCapacity = -0.000152995,
            NetAnnualizedYield = -0.00186
        });
        var cutoverOnly = AccountingChannelIpcResponse.From(new AccountingChannelLine
        {
            ChannelId = new ChannelId(Enumerable.Repeat((byte)0x76, 32).ToArray()),
            ShortChannelId = "3469150x25x1",
            OpenedAtBlockHeight = 3_469_150,
            TrackedSince = s_at
        });
        var report = new AccountingReportIpcResponse
        {
            Kind = (int)AccountingReportKind.Channels,
            KindName = "Channels",
            ProjectedLedgerSeq = 156,
            AsOfUnixMilliseconds = s_at.ToUnixTimeMilliseconds(),
            Channels = [funded, cutoverOnly]
        };
        using var output = new StringWriter();

        // Act
        new AccountingReportPrinter(output).Print(report);

        // Assert
        var printed = output.ToString();
        Assert.Equal(3_463_271u, funded.OpenedAtBlockHeight);
        Assert.Equal(s_at.ToUnixTimeMilliseconds(), funded.TrackedSinceUnixMilliseconds);
        Assert.Equal(-0.000152995, funded.NetYieldOnCapacity);
        Assert.Equal(-0.00186, funded.NetAnnualizedYield);
        Assert.Contains("Open:            2026-09-02 08:13:00 UTC (block 3463271) to now", printed);
        Assert.Contains("Open:            before the feed began (2026-10-02 08:13:00 UTC, funded at block 3469150) "
                      + "to now", printed);
        Assert.Contains("Net:             -152995 msat (-152.995 sat); net yield -0.0153 %, annualized -0.186 %",
                        printed);
        Assert.Contains("Routing yield:   0.0002 %, annualized 0.0024 % (routing out only, before costs)", printed);
        Assert.Contains("Net:             0 msat (0 sat); net yield -, annualized -", printed);
    }

    [Fact]
    public void Given_AnErrorWithTheCommandsUsage_When_Written_Then_TheFullHelpIsNotPrinted()
    {
        // Arrange (NL-628)
        var error = ClientApp.ValidateArguments("listaccountingevents", ["--kind", "NotAKind"]);
        using var output = new StringWriter();
        var fullHelp = false;

        // Act
        ClientApp.WriteUsageError(error!, output, () => fullHelp = true);

        // Assert
        Assert.False(fullHelp);
        Assert.Contains("Unknown kind 'NotAKind'", output.ToString());
        Assert.Contains("Usage: listaccountingevents", output.ToString());
        Assert.Contains("Run 'nltg --help' for every command.", output.ToString());
    }

    [Fact]
    public void Given_AnErrorWithoutAUsage_When_Written_Then_TheFullHelpFollows()
    {
        // Arrange (NL-628): an unknown command has no usage of its own
        var error = ClientApp.ValidateArguments("frobnicate", []);
        using var output = new StringWriter();
        var fullHelp = false;

        // Act
        ClientApp.WriteUsageError(error!, output, () => fullHelp = true);

        // Assert
        Assert.True(fullHelp);
        Assert.DoesNotContain("Run 'nltg --help'", output.ToString());
    }

    [Fact]
    public void Given_AnUnknownRegisterKind_When_Validated_Then_TheErrorListsTheKinds()
    {
        // Act (NL-628)
        var error = ClientApp.ValidateArguments("accounting", ["report", "register", "--kind", "NotAKind"]);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("Unknown kind 'NotAKind': expected one of ", error);
        Assert.Contains(nameof(AccountingEventKind.ForwardSettled), error);
        Assert.Contains("Usage: accounting report", error);
    }
}