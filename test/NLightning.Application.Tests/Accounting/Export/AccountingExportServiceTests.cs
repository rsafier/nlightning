using System.Text;

namespace NLightning.Application.Tests.Accounting.Export;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Accounting.Services;
using Reports;
using static Reports.AccountingBooksTestKit;

/// <summary>
/// The books' exports (NL-602 A2, plan §6.1): hledger, beancount and CSV against golden files (exact msat, one entry
/// per event in ledger order, escaped descriptions), paging that concatenates to the same document, and the header
/// only on the first page.
/// </summary>
/// <remarks>
/// Set <c>NLTG_UPDATE_GOLDEN</c> to a directory to write the current output there instead of comparing (then review
/// and copy the files to <c>Accounting/Export/Golden/</c>).
/// </remarks>
public class AccountingExportServiceTests
{
    private readonly AccountingBooksTestKit _kit = new();

    public AccountingExportServiceTests()
    {
        AddGoldenLedger(_kit);
    }

    [Theory]
    [InlineData(AccountingExportFormat.Hledger, "books.journal")]
    [InlineData(AccountingExportFormat.Beancount, "books.beancount")]
    [InlineData(AccountingExportFormat.Csv, "books.csv")]
    public async Task Given_TheGoldenLedger_When_Exported_Then_TheOutputEqualsTheGoldenFile(
        AccountingExportFormat format, string file)
    {
        // Act
        var chunk = await _kit.CreateExports().ExportAsync(new AccountingExportQuery(format, 0, 1_000),
                                                           TestContext.Current.CancellationToken);

        // Assert
        Assert.False(chunk.HasMore);
        Assert.Equal(7, chunk.EntryCount);
        Assert.Equal(7, chunk.NextAfter);
        var update = Environment.GetEnvironmentVariable("NLTG_UPDATE_GOLDEN");
        if (!string.IsNullOrEmpty(update))
        {
            await File.WriteAllTextAsync(Path.Combine(update, file), chunk.Text, new UTF8Encoding(false),
                                         TestContext.Current.CancellationToken);
            return;
        }

        var expected = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Accounting", "Export",
                                                                "Golden", file),
                                                   TestContext.Current.CancellationToken);
        Assert.Equal(expected.Replace("\r\n", "\n"), chunk.Text);
    }

    [Theory]
    [InlineData(AccountingExportFormat.Hledger)]
    [InlineData(AccountingExportFormat.Beancount)]
    [InlineData(AccountingExportFormat.Csv)]
    public async Task Given_SmallPages_When_Exported_Then_ThePagesConcatenateToTheWholeDocument(
        AccountingExportFormat format)
    {
        // Arrange
        var exports = _kit.CreateExports();
        var whole = await exports.ExportAsync(new AccountingExportQuery(format, 0, 1_000),
                                              TestContext.Current.CancellationToken);

        // Act
        var text = new StringBuilder();
        var after = 0L;
        var pages = 0;
        while (true)
        {
            var page = await exports.ExportAsync(new AccountingExportQuery(format, after, 2),
                                                 TestContext.Current.CancellationToken);
            text.Append(page.Text);
            pages++;
            if (!page.HasMore)
                break;
            after = page.NextAfter;
        }

        // Assert
        Assert.Equal(whole.Text, text.ToString());
        Assert.Equal(4, pages);
    }

    [Fact]
    public async Task Given_APeriod_When_Exported_Then_OnlyItsEntriesAndTheirFirstDateAreUsed()
    {
        // Act
        var chunk = await _kit.CreateExports()
                              .ExportAsync(new AccountingExportQuery(AccountingExportFormat.Beancount, 0, 1_000,
                                                                     T0.AddDays(2), T0.AddDays(4)),
                                           TestContext.Current.CancellationToken);

        // Assert: the invoice (day 2) and the forward (day 3); accounts opened at day 2
        Assert.Equal(2, chunk.EntryCount);
        Assert.Contains("2026-01-03 open Assets:Lightning:Channels MSAT", chunk.Text);
        Assert.Contains("2026-01-03 open Income:Lightning:Received MSAT", chunk.Text);
        Assert.DoesNotContain("Assets:Onchain:Wallet", chunk.Text);
        Assert.DoesNotContain("2026-01-01", chunk.Text);
    }

    [Fact]
    public async Task Given_RenamedAccounts_When_ExportedToBeancount_Then_TheNamesAreValidUnderTheirCategory()
    {
        // Arrange
        _kit.Options.AccountNames[AccountRole.Routing] = "revenue: routing fees!";
        _kit.Options.AccountNames[AccountRole.Channels] = "assets:ln;chan";

        // Act
        var beancount = await _kit.CreateExports()
                                  .ExportAsync(new AccountingExportQuery(AccountingExportFormat.Beancount, 0, 1_000),
                                               TestContext.Current.CancellationToken);
        var hledger = await _kit.CreateExports()
                                .ExportAsync(new AccountingExportQuery(AccountingExportFormat.Hledger, 0, 1_000),
                                             TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("open Income:Revenue:Routing-fees- MSAT", beancount.Text);
        Assert.Contains("open Assets:Ln-chan MSAT", beancount.Text);
        Assert.Contains("account revenue: routing fees!\n", hledger.Text);
        Assert.Contains("account assets:ln_chan\n", hledger.Text);
    }

    [Fact]
    public async Task Given_TheBooksOff_When_Exported_Then_BooksDisabled()
    {
        // Arrange
        _kit.Books.SetupGet(b => b.IsEnabled).Returns(false);

        // Act / Assert
        await Assert.ThrowsAsync<AccountingBooksDisabledException>(
            () => _kit.CreateExports().ExportAsync(new AccountingExportQuery(AccountingExportFormat.Csv, 0, 10),
                                                   TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 1_001)]
    public async Task Given_ABadPage_When_Exported_Then_ArgumentException(long after, int take)
    {
        // Act / Assert
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _kit.CreateExports().ExportAsync(new AccountingExportQuery(AccountingExportFormat.Csv, after, take),
                                                   TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Seven entries over five days: a deposit, a funding with its wallet spend, an invoice whose description carries
    /// a line break, a semicolon and quotes, a forward, a payment to a description starting with '=', and a failed
    /// payment that posts nothing. Amounts are odd msat on purpose (no rounding anywhere).
    /// </summary>
    internal static void AddGoldenLedger(AccountingBooksTestKit kit)
    {
        var at = T0.AddHours(9).AddMinutes(30).AddMilliseconds(123);
        kit.Add(AccountingEventKind.WalletReceived, at, 1_000_000_000, 0, key: "wallet:aa:0:in",
                postings: [(AccountRole.Wallet, 1_000_000_000), (AccountRole.TransfersIn, -1_000_000_000)]);
        kit.Add(AccountingEventKind.ChannelFunded, at.AddDays(1), 500_000_000, 1_234_567, Channel(1),
                key: "chan:01:funded",
                postings:
                [
                    (AccountRole.Channels, 500_000_000), (AccountRole.FeeFunding, 1_234_567),
                    (AccountRole.Clearing, -501_234_567)
                ]);
        kit.Add(AccountingEventKind.WalletOutputSpent, at.AddDays(1), -501_234_567, 0, key: "wallet:aa:0:spent",
                postings: [(AccountRole.Wallet, -501_234_567), (AccountRole.Clearing, 501_234_567)]);
        kit.Add(AccountingEventKind.InvoiceSettled, at.AddDays(2), 12_345_678, 0, Channel(1),
                details: AccountingDetailsCodec.Create(("description", "coffee;\n\"large\", \\ été")),
                key: "inv:01:settled",
                postings: [(AccountRole.Channels, 12_345_678), (AccountRole.Received, -12_345_678)]);
        kit.Add(AccountingEventKind.ForwardSettled, at.AddDays(3), 1_001, 0, Channel(1), key: "fwd:01:0:settled",
                postings: [(AccountRole.Channels, 1_001), (AccountRole.Routing, -1_001)]);
        kit.Add(AccountingEventKind.PaymentSucceeded, at.AddDays(4), -5_000_051, 51, Channel(1),
                details: AccountingDetailsCodec.Create(("description", "=HYPERLINK(\"x\")")),
                key: "pay:02:succeeded",
                postings:
                [
                    (AccountRole.Channels, -5_000_051), (AccountRole.Sent, 5_000_000), (AccountRole.RoutingFees, 51)
                ]);
        kit.Add(AccountingEventKind.PaymentFailed, at.AddDays(4), key: "pay:03:failed:1");
    }
}