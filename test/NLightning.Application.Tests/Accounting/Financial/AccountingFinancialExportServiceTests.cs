using System.Text;

namespace NLightning.Application.Tests.Accounting.Financial;

using Application.Accounting.Export.Financial;
using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial.Export;
using Domain.Accounting.Financial.Reports;
using static FinancialBooksFixture;

/// <summary>
/// The financial book's exports (NL-602 A3-T6) of the hand-computed book of <see cref="FinancialBooksFixture"/>:
/// hledger with <c>@@</c> costs and <c>P</c> directives, beancount with <c>{{ }}</c> costs and <c>price</c> directives,
/// CSV with fiat columns, against golden files; every journal transaction balances in msat and fiat
/// (<see cref="JournalBalanceChecker"/>, the stand-in for <c>hledger check</c> and <c>bean-check</c>), the journals'
/// account totals equal the books, and pages of any size (one splitting a sequence's adjustments) concatenate to the
/// whole document.
/// </summary>
/// <remarks>
/// Set <c>NLTG_UPDATE_GOLDEN</c> to a directory to write the current output there instead of comparing (then review it,
/// check it with <c>hledger check --strict</c> and <c>bean-check</c> when they are installed, and copy the files to
/// <c>Accounting/Financial/Golden/</c>).
/// </remarks>
public sealed class AccountingFinancialExportServiceTests : IAsyncLifetime
{
    private FinancialBooksFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await CreateAsync();

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Theory]
    [InlineData(AccountingExportFormat.Hledger, "financial.journal")]
    [InlineData(AccountingExportFormat.Beancount, "financial.beancount")]
    [InlineData(AccountingExportFormat.Csv, "financial.csv")]
    public async Task Given_TheBook_When_Exported_Then_TheOutputEqualsTheGoldenFile(AccountingExportFormat format,
                                                                                   string file)
    {
        // Act
        var chunk = await _fixture.CreateExports().ExportAsync(new AccountingFinancialExportQuery(format, 0, 1_000),
                                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.False(chunk.HasMore);
        Assert.Equal(11, chunk.EntryCount);
        Assert.Equal((10L, (int?)0), (chunk.NextAfter, chunk.NextAfterAdjustment));
        await AssertGoldenAsync(file, chunk.Text);
    }

    [Theory]
    [InlineData(AccountingExportFormat.Hledger)]
    [InlineData(AccountingExportFormat.Beancount)]
    public async Task Given_TheBook_When_ExportedAsAJournal_Then_ItBalancesAndItsTotalsEqualTheBooks(
        AccountingExportFormat format)
    {
        // Arrange
        var sums = await _fixture.ReadAsync(u => u.AccountingBooksDbRepository.SumAccountPostingsAsync(
                                               AccountingBook.Financial, null, null, Usd,
                                               TestContext.Current.CancellationToken));

        // Act
        var chunk = await _fixture.CreateExports().ExportAsync(new AccountingFinancialExportQuery(format, 0, 1_000),
                                                               TestContext.Current.CancellationToken);
        var check = format == AccountingExportFormat.Hledger
                        ? JournalBalanceChecker.CheckHledger(chunk.Text, Usd)
                        : JournalBalanceChecker.CheckBeancount(chunk.Text, Usd);

        // Assert: ten transactions (the failed payment posts nothing), the six prices referenced
        Assert.True(check.IsValid, string.Join("\n", check.Errors));
        Assert.Equal(10, check.Transactions);
        Assert.Equal(6, check.Prices);
        var rounding = format == AccountingExportFormat.Hledger ? "equity:fiat-rounding" : "Equity:Fiat-rounding";
        Assert.Equal((0L, 0.00000001m), check.Accounts[rounding]);
        foreach (var sum in sums.GroupBy(s => s.AccountName!))
        {
            var name = format == AccountingExportFormat.Hledger ? sum.Key : BeancountName(sum.Key);
            var (msat, fiat) = check.Accounts[name];
            Assert.Equal(sum.Sum(s => s.AmountMsat), msat);

            // The journals carry the fiat of the valued entries only (the unvalued ones have none in this book)
            Assert.Equal(sum.Sum(s => s.FiatAmount), fiat);
        }

        Assert.Equal(sums.Select(s => s.AccountName).Distinct().Count() + 1, check.Accounts.Count);
    }

    [Theory]
    [InlineData(AccountingExportFormat.Hledger, 1)]
    [InlineData(AccountingExportFormat.Hledger, 7)]
    [InlineData(AccountingExportFormat.Beancount, 3)]
    [InlineData(AccountingExportFormat.Csv, 2)]
    public async Task Given_SmallPages_When_Exported_Then_ThePagesConcatenateToTheWholeDocument(
        AccountingExportFormat format, int take)
    {
        // Arrange
        var exports = _fixture.CreateExports();
        var whole = await exports.ExportAsync(new AccountingFinancialExportQuery(format, 0, 1_000),
                                              TestContext.Current.CancellationToken);

        // Act: 7 ends a page inside sequence 7's adjustments
        var text = new StringBuilder();
        long after = 0;
        int? afterAdjustment = null;
        var entries = 0;
        while (true)
        {
            var page = await exports.ExportAsync(
                           new AccountingFinancialExportQuery(format, after, take, AfterAdjustment: afterAdjustment),
                           TestContext.Current.CancellationToken);
            text.Append(page.Text);
            entries += page.EntryCount;
            if (!page.HasMore)
                break;

            (after, afterAdjustment) = (page.NextAfter, page.NextAfterAdjustment);
        }

        // Assert
        Assert.Equal(11, entries);
        Assert.Equal(whole.Text, text.ToString());
    }

    [Fact]
    public async Task Given_AWindow_When_Exported_Then_OnlyItsEntriesAndTheirPricesAreWritten()
    {
        // Act: the first quarter of 2026
        var chunk = await _fixture.CreateExports().ExportAsync(
                        new AccountingFinancialExportQuery(AccountingExportFormat.Hledger, 0, 1_000, At(2026, 1, 1),
                                                           At(2026, 4, 1)), TestContext.Current.CancellationToken);

        // Assert: entries 2 to 7 and the adjustment (dated 2026-03-20); prices P1 to P3
        var check = JournalBalanceChecker.CheckHledger(chunk.Text, Usd);
        Assert.True(check.IsValid, string.Join("\n", check.Errors));
        Assert.Equal(7, chunk.EntryCount);
        Assert.Equal(7, check.Transactions);
        Assert.Equal(3, check.Prices);
        Assert.DoesNotContain("equity:fiat-rounding", chunk.Text);
        Assert.Contains("P 2026-01-01 00:00:00 msat 0.0000004 USD", chunk.Text);
    }

    [Fact]
    public async Task Given_AnotherCurrency_When_Exported_Then_EveryEntryIsWrittenInMsatOnlyAndStillBalances()
    {
        // Act
        var chunk = await _fixture.CreateExports().ExportAsync(
                        new AccountingFinancialExportQuery(AccountingExportFormat.Beancount, 0, 1_000, Currency: "eur"),
                        TestContext.Current.CancellationToken);

        // Assert
        var check = JournalBalanceChecker.CheckBeancount(chunk.Text, "EUR");
        Assert.True(check.IsValid, string.Join("\n", check.Errors));
        Assert.Equal(0, check.Prices);
        Assert.DoesNotContain("{{", chunk.Text);
        Assert.Contains("option \"operating_currency\" \"EUR\"", chunk.Text);
        Assert.All(check.Accounts.Values, a => Assert.Equal(0m, a.Fiat));
    }

    [Fact]
    public async Task Given_TheFinancialBookOff_When_Exported_Then_ItIsRefused()
    {
        // Arrange
        _fixture.Projection.SetupGet(p => p.IsEnabled).Returns(false);

        // Act / Assert
        await Assert.ThrowsAsync<AccountingFinancialBooksDisabledException>(
            () => _fixture.CreateExports().ExportAsync(
                new AccountingFinancialExportQuery(AccountingExportFormat.Csv, 0, 10),
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 1_001)]
    public async Task Given_ABadPage_When_Exported_Then_ItIsRefused(long after, int take)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _fixture.CreateExports().ExportAsync(
                new AccountingFinancialExportQuery(AccountingExportFormat.Csv, after, take),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_TheEntriesInMemory_When_WrittenAsADocument_Then_ItEqualsTheServicesExport()
    {
        // Arrange: what A3-T4's golden files do with the formatter alone
        var entries = await _fixture.ReadEntriesAsync();
        var prices = await _fixture.ReadAsync(u => u.AccountingPriceDbRepository.ListAsync(
                                                 Usd, null, null, 100, TestContext.Current.CancellationToken));
        var events = await _fixture.ReadAsync(u => u.AccountingEventDbRepository.GetSealedRangeAsync(
                                                 1, 100, TestContext.Current.CancellationToken));

        // Act
        var document = AccountingFinancialExportFormatter.WriteDocument(
            AccountingExportFormat.Hledger, Usd, entries, events.ToDictionary(e => e.LedgerSeq!.Value), prices);
        var exported = await _fixture.CreateExports().ExportAsync(
                           new AccountingFinancialExportQuery(AccountingExportFormat.Hledger, 0, 1_000),
                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(exported.Text, document);
    }

    [Fact]
    public void Given_AnUnbalancedJournal_When_Checked_Then_TheCheckerReportsIt()
    {
        // Arrange: a gain line one hundred-millionth short
        var entry = new AccountingEntry(1, "k", AccountingEventKind.PaymentSucceeded, At(2026, 1, 1), null, null,
                                        [
                                            Line(AccountRole.Sent, "expenses:payments", 1_000, 2m, 1),
                                            Line(AccountRole.Channels, "assets:lightning:channels", -1_000, -1m),
                                            Line(AccountRole.Channels, "income:gains:realized", 0, -1m)
                                        ])
        { Book = AccountingBook.Financial };
        var hledger = AccountingFinancialExportFormatter.WriteDocument(AccountingExportFormat.Hledger, Usd, [entry]);

        // Act
        var good = JournalBalanceChecker.CheckHledger(hledger, Usd);
        var bad = JournalBalanceChecker.CheckHledger(hledger.Replace("-1 USD", "-0.99999999 USD"), Usd);

        // Assert
        Assert.True(good.IsValid, string.Join("\n", good.Errors));
        Assert.False(bad.IsValid);
        Assert.Contains(bad.Errors, e => e.Contains("does not balance"));
    }

    [Fact]
    public void Given_ALineWithAValueOfTheOtherSign_When_Written_Then_TheEntryIsWrittenUnvaluedInMsat()
    {
        // Arrange
        var entry = new AccountingEntry(1, "k", AccountingEventKind.PaymentSucceeded, At(2026, 1, 1), null, null,
                                        [
                                            Line(AccountRole.Sent, "expenses:payments", 1_000, -2m, 1),
                                            Line(AccountRole.Channels, "assets:lightning:channels", -1_000, 2m)
                                        ])
        { Book = AccountingBook.Financial };

        // Act
        var valuation = AccountingFinancialExportFormatter.Valuate(entry, Usd);
        var beancount = AccountingFinancialExportFormatter.WriteDocument(AccountingExportFormat.Beancount, Usd,
                                                                         [entry]);

        // Assert
        Assert.False(valuation.IsValued);
        Assert.Contains("2026-01-01 ! \"PaymentSucceeded\"", beancount);
        Assert.True(JournalBalanceChecker.CheckBeancount(beancount, Usd).IsValid);
    }

    /// <summary>
    /// The golden journals through the real tools (<c>Explicit</c>: neither is installed in CI): <c>NLTG_HLEDGER</c> and
    /// <c>NLTG_BEAN_CHECK</c> name the binaries (else <c>hledger</c> and <c>bean-check</c> on the PATH). Run with
    /// <c>-- xUnit.Explicit=only</c> or the xunit runner's <c>-explicit only</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Given_TheRealTools_When_TheGoldenJournalsAreChecked_Then_TheyAccept()
    {
        // Arrange
        var golden = Path.Combine(AppContext.BaseDirectory, "Accounting", "Financial", "Golden");
        var hledger = Environment.GetEnvironmentVariable("NLTG_HLEDGER") is { Length: > 0 } h ? h : "hledger";
        var beanCheck = Environment.GetEnvironmentVariable("NLTG_BEAN_CHECK") is { Length: > 0 } b ? b : "bean-check";

        // Act
        var hledgerExit = await RunAsync(hledger, "-f", Path.Combine(golden, "financial.journal"), "check", "--strict");
        var beanExit = await RunAsync(beanCheck, Path.Combine(golden, "financial.beancount"));

        // Assert
        Assert.Equal(0, hledgerExit);
        Assert.Equal(0, beanExit);
    }

    private static async Task<int> RunAsync(string program, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(program) { RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = System.Diagnostics.Process.Start(start)!;
        var errors = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(errors);
        return process.ExitCode;
    }

    private static string BeancountName(string name)
    {
        var parts = name.Split(':');
        var category = AccountingFiat.CategoryOf(name, AccountRole.Opening);
        var root = category switch
        {
            AccountingAccountCategory.Assets => "Assets",
            AccountingAccountCategory.Income => "Income",
            AccountingAccountCategory.Expenses => "Expenses",
            AccountingAccountCategory.Liabilities => "Liabilities",
            _ => "Equity"
        };
        return root + string.Concat(parts.Skip(1).Select(p => ":" + char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static async Task AssertGoldenAsync(string file, string text)
    {
        var update = Environment.GetEnvironmentVariable("NLTG_UPDATE_GOLDEN");
        if (!string.IsNullOrEmpty(update))
        {
            await File.WriteAllTextAsync(Path.Combine(update, file), text, new UTF8Encoding(false),
                                         TestContext.Current.CancellationToken);
            return;
        }

        var expected = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Accounting", "Financial",
                                                                "Golden", file),
                                                   TestContext.Current.CancellationToken);
        Assert.Equal(expected.Replace("\r\n", "\n"), text);
    }
}