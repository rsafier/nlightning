namespace NLightning.Application.Tests.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Prices;
using Domain.Client.Enums;
using Domain.Client.Requests;

/// <summary>
/// Lines a forced close left unvalued, valued later (NL-680, NL-681), on SQLite with the production rules: a late fact
/// closed unvalued takes the price of its own time, never its adjustment date's; a reclassified unvalued line moves at
/// 0 and its value, once found, lands on the account the reclassifications hold it in, so every account is right after
/// any number of reclassifications.
/// </summary>
public sealed class FinancialForcedCloseValuationTests
{
    private const string Usd = FinancialProjectorTestKit.Usd;

    private static readonly DateTimeOffset s_jan1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_jan20 = new(2026, 1, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_jan25 = new(2026, 1, 25, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_apr2 = new(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_may2 = new(2026, 5, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_ALateFactClosedUnvalued_When_ThePriceOfItsTimeArrives_Then_ItsPriceAdjustmentsUseIt()
    {
        // Arrange - NL-680: January closed with only the Jan 1 price; an invoice of Jan 25 sealed late is staged
        // unvalued (dated Apr 2); February to April closed later, April by force
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_apr2);
        await kit.AddPricesAsync((s_jan1, 40_000m));
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_jan1),
                           FinancialProjectorTestKit.Cutover(s_jan1));
        await kit.ProjectAsync();
        await kit.Periods.CloseAsync("2026-01", false, TestContext.Current.CancellationToken);
        await kit.AddAsync(kit.Invoice(100_000_000, s_jan25));
        await kit.ProjectAsync();
        kit.Clock.Now = s_may2;
        foreach (var month in new[] { "2026-02", "2026-03" })
            await kit.Periods.CloseAsync(month, false, TestContext.Current.CancellationToken);
        var april = await kit.Periods.CloseAsync("2026-04", true, TestContext.Current.CancellationToken);
        Assert.True(april.Period.Forced);

        // Act: the prices of the fact's hour (45,000) and of the adjustment's date (90,000) arrive
        await kit.Valuation.ImportAsync(Usd,
                                        [
                                            new AccountingPricePoint(s_jan25.AddHours(-1), 45_000m),
                                            new AccountingPricePoint(s_apr2.AddHours(-1), 90_000m)
                                        ], TestContext.Current.CancellationToken);

        // Assert: one price adjustment per line, at the fact's price (45), in the open period (May)
        var entries = (await kit.ListEntriesAsync(AccountingBook.Financial)).Where(e => e.LedgerSeq == 3).ToList();
        var prices = entries.Where(e => e.Note?.Contains("Price", StringComparison.Ordinal) == true).ToList();
        Assert.Equal([45m, -45m], prices.SelectMany(e => e.Postings).Select(p => p.FiatAmount!.Value).Order().Reverse()
                                        .ToArray());
        Assert.All(prices, e => Assert.Equal(s_may2, e.OccurredAt));
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
    }

    [Fact]
    public async Task Given_AnUnvaluedClosedLineReclassified_When_ItsPriceArrives_Then_ItsValueLandsWhereTheLineIs()
    {
        // Arrange - NL-681: an invoice of Jan 20 with no usable price, January closed by force
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_apr2);
        await kit.AddPricesAsync((s_jan1, 40_000m));
        var invoice = kit.Invoice(100_000_000, s_jan20);
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_jan1),
                           FinancialProjectorTestKit.Cutover(s_jan1), invoice);
        await kit.ProjectAsync();
        await kit.Periods.CloseAsync("2026-01", true, TestContext.Current.CancellationToken);

        // Act: reclassified to consulting, then its price (50,000) arrives, then reclassified to licences
        await ClassifyAsync(kit, invoice.EventKey, "income:consulting");
        var moved = (await EntriesAsync(kit, invoice.EventKey)).Single(e => e.Adjustment == 1);
        await kit.Valuation.ImportAsync(Usd, [new AccountingPricePoint(s_jan20.AddMinutes(-30), 50_000m)],
                                        TestContext.Current.CancellationToken);
        var afterPrice = await BalancesAsync(kit);
        await ClassifyAsync(kit, invoice.EventKey, "income:licences");
        await kit.Valuation.ValueNowAsync(TestContext.Current.CancellationToken);
        var afterSecond = await BalancesAsync(kit);

        // Assert: the move of the unvalued line carries 0 (never valued later, never unvalued)
        Assert.Equal([("income:sales", 100_000_000L, (decimal?)0m, (string?)Usd),
                      ("income:consulting", -100_000_000L, 0m, Usd)],
                     moved.Postings.Select(p => (p.AccountName!, p.AmountMsat, p.FiatAmount, p.FiatCurrency)).ToArray());

        // The income line's value (50) lands on consulting, where the line is; nothing is left on sales
        Assert.Equal((-100_000_000L, -50m), afterPrice["income:consulting"]);
        Assert.Equal((0L, 0m), afterPrice.GetValueOrDefault("income:sales"));
        Assert.Equal((100_000_000L, 50m), afterPrice["assets:lightning:channels"]);

        // After the second reclassification the value follows the line to licences; consulting and sales are empty
        Assert.Equal((-100_000_000L, -50m), afterSecond["income:licences"]);
        Assert.Equal((0L, 0m), afterSecond.GetValueOrDefault("income:consulting"));
        Assert.Equal((0L, 0m), afterSecond.GetValueOrDefault("income:sales"));
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
    }

    private static Task ClassifyAsync(FinancialProjectorTestKit kit, string key, string account) =>
        kit.Classification.HandleAsync(new AccountingClassifyClientRequest
        {
            Action = AccountingClassifyAction.Set,
            EventKey = key,
            Account = account
        }, TestContext.Current.CancellationToken);

    private static async Task<IReadOnlyList<AccountingEntry>> EntriesAsync(FinancialProjectorTestKit kit, string key) =>
        (await kit.ListEntriesAsync(AccountingBook.Financial)).Where(e => e.EventKey == key)
                                                              .OrderBy(e => e.Adjustment).ToList();

    private static async Task<Dictionary<string, (long Msat, decimal Fiat)>> BalancesAsync(
        FinancialProjectorTestKit kit) =>
        (await kit.ReadAsync(u => u.AccountingBooksDbRepository.GetAccountBalancesAsync(
                                 AccountingBook.Financial, TestContext.Current.CancellationToken)))
       .GroupBy(b => b.AccountName!)
       .ToDictionary(g => g.Key, g => (g.Sum(b => b.BalanceMsat), g.Sum(b => b.FiatAmount)));
}