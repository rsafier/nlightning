namespace NLightning.Application.Tests.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Prices;

/// <summary>
/// The operator's correction of a wrong stored price (NL-693, <c>accounting prices replace</c>), on SQLite with the
/// production rules: in the open period the entries it valued are projected again (the book equals one valued with the
/// right price from the start, lots and gains included); in a closed period they keep their lines (D-A8) and get a
/// price adjustment each in the open period with the change of their value, the closes still verify, a rebuild equals
/// the incremental book, and a second replacement adds up.
/// </summary>
public sealed class FinancialPriceReplaceTests
{
    private const string Usd = FinancialProjectorTestKit.Usd;

    private static readonly DateTimeOffset s_jan1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_jan10 = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_jan20 = new(2026, 1, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_feb2 = new(2026, 2, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_AWrongPriceInTheOpenPeriod_When_Replaced_Then_TheBookEqualsOneValuedRightFromTheStart()
    {
        // Arrange: the Jan 10 price stored as 50,000 instead of 45,000; an invoice and a payment valued with it
        await using var wrong = await StoryAsync(50_000m);
        await using var right = await StoryAsync(45_000m);
        var payment = (await wrong.ListEntriesAsync(AccountingBook.Financial)).Single(e => e.EventKey.StartsWith(
                          "pay:", StringComparison.Ordinal));
        var invoiceSeq = (await wrong.ListEntriesAsync(AccountingBook.Financial))
                        .Single(e => e.EventKey.StartsWith("inv:", StringComparison.Ordinal)).LedgerSeq;
        Assert.NotEqual(await right.SnapshotFinancialAsync(), await wrong.SnapshotFinancialAsync());

        // Act
        var result = await wrong.Valuation.ReplaceAsync(
                         new AccountingPriceReplacement(null, s_jan10, 45_000m, "exchange statement", "decimal slip"),
                         TestContext.Current.CancellationToken);
        await wrong.ProjectAsync();

        // Assert: the two open entries projected again from the invoice, nothing closed
        Assert.True(result.Changed);
        Assert.Equal(50_000m, result.OldPrice);
        Assert.Equal(AccountingPriceSource.Csv, result.OldSource);
        Assert.Equal(45_000m, result.Price.Price);
        Assert.Equal(AccountingPriceSource.Manual, result.Price.Source);
        Assert.Equal(s_feb2, result.Price.FetchedAt);
        Assert.Equal(2, result.OpenEntries);
        Assert.Equal(invoiceSeq, result.ReplayFromLedgerSeq);
        Assert.Equal(0, result.ClosedEntries);
        Assert.Equal(0, result.Adjustments);
        Assert.True(payment.LedgerSeq > invoiceSeq);

        // The stored row keeps its id and time, with the operator's price, source and time of the replacement
        var stored = (await wrong.ListPricesAsync()).Single(p => p.Time == s_jan10);
        Assert.Equal(result.Price, stored);

        // The whole financial book (values, lots, reliefs, gains, balances, cursor) as if valued right from the start
        Assert.Equal(await right.SnapshotFinancialAsync(), await wrong.SnapshotFinancialAsync());
    }

    [Fact]
    public async Task Given_AWrongPriceOfAClosedPeriod_When_Replaced_Then_PriceAdjustmentsInTheOpenPeriod()
    {
        // Arrange: January (the invoice and the payment valued at the wrong 50,000) closed
        await using var kit = await StoryAsync(50_000m);
        await kit.Periods.CloseAsync("2026-01", false, TestContext.Current.CancellationToken);
        var before = await BalancesAsync(kit);
        var closedBefore = (await kit.ListEntriesAsync(AccountingBook.Financial)).Select(FinancialProjectorTestKit.Describe)
                                                                                 .ToList();

        // Act
        var result = await kit.Valuation.ReplaceAsync(
                         new AccountingPriceReplacement(Usd, s_jan10.AddSeconds(0.4), 45_000m, null, "wrong source"),
                         TestContext.Current.CancellationToken);
        await kit.ProjectAsync();

        // Assert: no open entry; one price adjustment for each of the two closed entries, dated now
        Assert.Null(result.ReplayFromLedgerSeq);
        Assert.Equal(0, result.OpenEntries);
        Assert.Equal(2, result.ClosedEntries);
        Assert.Equal(2, result.Adjustments);
        Assert.Equal(3, result.LinesRepriced);
        var entries = await kit.ListEntriesAsync(AccountingBook.Financial);
        var adjustments = entries.Where(e => e.Adjustment > 0).ToList();
        Assert.Equal(2, adjustments.Count);
        Assert.All(adjustments, a =>
        {
            Assert.Equal(s_feb2, a.OccurredAt);
            Assert.Null(a.ClosedPeriodId);
            Assert.True(a.Flags.HasFlag(AccountingEntryFlags.Adjustment));
            Assert.Contains("Price", a.Note, StringComparison.Ordinal);
            Assert.Contains("50000 (Csv) -> 45000", a.Note, StringComparison.Ordinal);
            Assert.Contains("wrong source", a.Note, StringComparison.Ordinal);
            Assert.All(a.Postings, p => Assert.Equal(0, p.AmountMsat));
            Assert.Equal(0m, a.Postings.Sum(p => p.FiatAmount ?? 0m));
        });

        // The closed entries are untouched
        Assert.Equal(closedBefore, entries.Where(e => e.Adjustment == 0).Select(FinancialProjectorTestKit.Describe));

        // The invoice (0.001 BTC): 5 less income, carried on the cost-basis line (its lot keeps its closed cost); the
        // payment (0.0005 BTC + 0.0000001 BTC fee): 2.5005 less proceeds, so 2.5005 less realized gain
        var after = await BalancesAsync(kit);
        Assert.Equal(before["income:sales"].Fiat + 5m, after["income:sales"].Fiat);
        Assert.Equal(-45m, after["income:sales"].Fiat);
        Assert.Equal(22.5m, after["expenses:payments"].Fiat);
        Assert.Equal(before["expenses:fees:routing"].Fiat - 0.0005m, after["expenses:fees:routing"].Fiat);
        Assert.Equal(before.GetValueOrDefault("assets:cost-basis").Fiat - 5m,
                     after.GetValueOrDefault("assets:cost-basis").Fiat);
        Assert.Equal(GainOf(before) - 2.5005m, GainOf(after));
        Assert.Equal(0m, after.Values.Sum(b => b.Fiat));

        // The closes still verify, and a rebuild equals the incremental book
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
        var incremental = await kit.SnapshotFinancialAsync();
        await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);
        Assert.Equal(incremental, await kit.SnapshotFinancialAsync());

        // Act: corrected again (48,000)
        kit.Clock.Now = s_feb2.AddHours(1);
        var second = await kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10, 48_000m),
                                                      TestContext.Current.CancellationToken);

        // Assert: the corrections add up to the lines valued at the last price
        Assert.Equal(45_000m, second.OldPrice);
        Assert.Equal(AccountingPriceSource.Manual, second.OldSource);
        Assert.Equal(2, second.Adjustments);
        var last = await BalancesAsync(kit);
        Assert.Equal(-48m, last["income:sales"].Fiat);
        Assert.Equal(24m, last["expenses:payments"].Fiat);
        Assert.Equal(GainOf(before) - 1.0002m, GainOf(last)); // proceeds 25.005 -> 24.0048
        Assert.Equal(0m, last.Values.Sum(b => b.Fiat));
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
    }

    [Fact]
    public async Task Given_TheSamePriceOrNoStoredPrice_When_Replaced_Then_NothingChangesOrArgumentException()
    {
        // Arrange
        await using var kit = await StoryAsync(50_000m);
        var snapshot = await kit.SnapshotFinancialAsync();

        // Act
        var same = await kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10, 50_000m),
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.False(same.Changed);
        Assert.Equal(AccountingPriceSource.Csv, same.Price.Source);
        Assert.Equal(snapshot, await kit.SnapshotFinancialAsync());
        await Assert.ThrowsAsync<ArgumentException>(
            () => kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10.AddSeconds(1), 45_000m),
                                             TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10, -1m),
                                             TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10, 45_000m, "a\nb"),
                                             TestContext.Current.CancellationToken));
        Assert.Equal(50_000m, (await kit.ListPricesAsync()).Single(p => p.Time == s_jan10).Price);
    }

    [Fact]
    public async Task Given_LateValuationsOfAForcedClose_When_TheirPriceIsReplaced_Then_TheirValuesFollow()
    {
        // Arrange: an invoice of Jan 20 without a usable price, January closed by force, then its price (50,000)
        // carried into the open period by two late valuations (NL-680), and that price found wrong
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_feb2);
        await kit.AddPricesAsync((s_jan1, 40_000m));
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_jan1),
                           FinancialProjectorTestKit.Cutover(s_jan1), kit.Invoice(100_000_000, s_jan20));
        await kit.ProjectAsync();
        await kit.Periods.CloseAsync("2026-01", true, TestContext.Current.CancellationToken);
        await kit.Valuation.ImportAsync(Usd, [new AccountingPricePoint(s_jan20.AddMinutes(-30), 50_000m)],
                                        TestContext.Current.CancellationToken);
        Assert.Equal(-50m, (await BalancesAsync(kit))["income:sales"].Fiat);

        // Act
        var result = await kit.Valuation.ReplaceAsync(
                         new AccountingPriceReplacement(Usd, s_jan20.AddMinutes(-30), 40_000m),
                         TestContext.Current.CancellationToken);

        // Assert: the two late valuations revalued from the msat of the postings they valued
        Assert.Equal(2, result.ClosedEntries);
        Assert.Equal(2, result.Adjustments);
        var balances = await BalancesAsync(kit);
        Assert.Equal(-40m, balances["income:sales"].Fiat);
        Assert.Equal(40m, balances["assets:lightning:channels"].Fiat);
        Assert.Equal(0m, balances.GetValueOrDefault("assets:cost-basis").Fiat);
        Assert.Equal(0m, balances.Values.Sum(b => b.Fiat));
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AClosedFactReversedInTheOpenPeriod_When_ItsPriceIsReplaced_Then_TheReversalNegatesTheNewValue(
        bool reversedFirst)
    {
        // Arrange: a deposit of Jan 10 valued at the wrong 50,000, January closed; the deposit is reorged out in
        // February (its reversal takes the closed values back in the open period), before or after the replacement
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_feb2);
        await kit.AddPricesAsync((s_jan1, 40_000m), (s_jan10, 50_000m), (s_feb2.AddHours(-1), 70_000m));
        var deposit = kit.Deposit(100_000_000, s_jan10.AddMinutes(30));
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_jan1.AddMinutes(30)),
                           FinancialProjectorTestKit.Cutover(s_jan1.AddMinutes(30)), deposit);
        await kit.ProjectAsync();
        await kit.Periods.CloseAsync("2026-01", false, TestContext.Current.CancellationToken);
        var reversal = FinancialProjectorTestKit.Reversal(deposit, s_feb2.AddMinutes(-30), 799_999);
        if (reversedFirst)
        {
            await kit.AddAsync(reversal);
            await kit.ProjectAsync();
            Assert.Equal((0L, 0m), (await BalancesAsync(kit))["equity:transfers:in"]);
        }

        // Act
        var result = await kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10, 45_000m),
                                                      TestContext.Current.CancellationToken);
        if (!reversedFirst)
            await kit.AddAsync(reversal);
        await kit.ProjectAsync();

        // Assert: the closed deposit adjusted (+5 on the transfer in, -5 on the cost basis), and the reversal (projected
        // again, or projected later) takes that correction back with the deposit: nothing is left of the deposit
        Assert.Equal(1, result.Adjustments);
        Assert.Equal(reversedFirst, result.ReplayFromLedgerSeq is not null);
        var after = await BalancesAsync(kit);
        Assert.Equal((0L, 0m), after["equity:transfers:in"]);
        Assert.Equal(0m, after.GetValueOrDefault("assets:cost-basis").Fiat);
        Assert.Equal(0m, after.Values.Sum(b => b.Fiat));
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
        var incremental = await kit.SnapshotFinancialAsync();
        await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);
        Assert.Equal(incremental, await kit.SnapshotFinancialAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_AClosedFactReversedLateInAClosedPeriod_When_ItsPriceIsReplaced_Then_NothingIsLeftOfIt(
        bool reversalClosed)
    {
        // Arrange: a deposit of Jan 10 valued at the wrong 50,000, January closed; its reorg dated Jan 20 arrives after
        // the close, so its reversal is a late fact of the open period; that period closed too, or not
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_feb2);
        await kit.AddPricesAsync((s_jan1, 40_000m), (s_jan10, 50_000m), (s_feb2.AddHours(-1), 70_000m));
        var deposit = kit.Deposit(100_000_000, s_jan10.AddMinutes(30));
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_jan1.AddMinutes(30)),
                           FinancialProjectorTestKit.Cutover(s_jan1.AddMinutes(30)), deposit);
        await kit.ProjectAsync();
        await kit.Periods.CloseAsync("2026-01", false, TestContext.Current.CancellationToken);
        await kit.AddAsync(FinancialProjectorTestKit.Reversal(deposit, s_jan20, 799_999));
        await kit.ProjectAsync();
        var reversal = (await kit.ListEntriesAsync(AccountingBook.Financial))
                      .Single(e => e.Kind == AccountingEventKind.Reversal);
        Assert.True(reversal.Flags.HasFlag(AccountingEntryFlags.LateFact));
        Assert.Contains("of a closed period", reversal.Note, StringComparison.Ordinal);
        if (reversalClosed)
        {
            kit.Clock.Now = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
            await kit.Periods.CloseAsync("2026-02", false, TestContext.Current.CancellationToken);
        }

        // Act
        var result = await kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10, 45_000m),
                                                      TestContext.Current.CancellationToken);
        await kit.ProjectAsync();

        // Assert: a closed late reversal leaves the closed deposit alone (the pair nets); an open one is projected
        // again and takes the deposit's correction back with it
        Assert.Equal(reversalClosed ? 0 : 1, result.Adjustments);
        Assert.Equal(reversalClosed ? null : reversal.LedgerSeq, result.ReplayFromLedgerSeq);
        var after = await BalancesAsync(kit);
        Assert.Equal((0L, 0m), after["equity:transfers:in"]);
        Assert.Equal(0m, after.GetValueOrDefault("assets:cost-basis").Fiat);
        Assert.Equal(0m, after.Values.Sum(b => b.Fiat));
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
        var incremental = await kit.SnapshotFinancialAsync();
        await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);
        Assert.Equal(incremental, await kit.SnapshotFinancialAsync());
    }

    /// <summary>Opening balances of 0.01 BTC in a channel on Jan 1 (40,000), an invoice of 0.001 BTC and a payment of
    /// 0.0005 BTC (fee 10,000 msat) on Jan 10 (at <paramref name="jan10Price"/>), projected; the clock on Feb 2.</summary>
    private static async Task<FinancialProjectorTestKit> StoryAsync(decimal jan10Price)
    {
        var kit = await FinancialProjectorTestKit.CreateAsync(s_feb2);
        await kit.AddPricesAsync((s_jan1, 40_000m), (s_jan10, jan10Price));
        await kit.AddAsync(FinancialProjectorTestKit.Opening("channel", 1_000_000_000, s_jan1.AddMinutes(30)),
                           FinancialProjectorTestKit.Cutover(s_jan1.AddMinutes(30)),
                           kit.Invoice(100_000_000, s_jan10.AddMinutes(30)),
                           kit.Payment(50_000_000, 10_000, s_jan10.AddHours(1)));
        await kit.ProjectAsync();
        return kit;
    }

    private static decimal GainOf(Dictionary<string, (long Msat, decimal Fiat)> balances) =>
        -(balances.GetValueOrDefault("income:gains:realized").Fiat
        + balances.GetValueOrDefault("expenses:losses:realized").Fiat);

    private static async Task<Dictionary<string, (long Msat, decimal Fiat)>> BalancesAsync(
        FinancialProjectorTestKit kit) =>
        (await kit.ReadAsync(u => u.AccountingBooksDbRepository.GetAccountBalancesAsync(
                                 AccountingBook.Financial, TestContext.Current.CancellationToken)))
       .GroupBy(b => b.AccountName!)
       .ToDictionary(g => g.Key, g => (g.Sum(b => b.BalanceMsat), g.Sum(b => b.FiatAmount)));
}