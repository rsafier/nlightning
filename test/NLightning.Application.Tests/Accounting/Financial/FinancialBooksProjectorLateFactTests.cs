namespace NLightning.Application.Tests.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Lots;
using Domain.Accounting.Prices;
using Domain.Accounting.Services;

/// <summary>
/// The A3-T4 review's findings on the financial projector (NL-671, NL-672, NL-673), on SQLite with the production
/// rules: a late fact's reliefs survive a replay of the open period (rollback and rebuild), a late fact projected
/// without a price is staged again at the price of its own time, and the reversal of a wallet fact from before the feed
/// corrects the opening lots at their cost instead of selling them.
/// </summary>
public sealed class FinancialBooksProjectorLateFactTests
{
    private const string Usd = FinancialProjectorTestKit.Usd;

    private static readonly DateTimeOffset s_jan1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_jan10 = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_jan20 = new(2026, 1, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_jan25 = new(2026, 1, 25, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_mar5 = new(2026, 3, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_now = new(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_ALateFactRelievingAnUnvaluedLot_When_ThePriceArrivesLate_Then_TheLotsStillHoldTheAssets()
    {
        // Arrange: LIFO; January closed; a deposit of March projected without a price, then a withdrawal of January
        // sealed late, whose adjustment relieves the deposit's lot (the newest)
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now, AccountingCostBasisMethod.Lifo);
        await kit.AddPricesAsync((s_jan1, 40_000m), (s_jan20, 60_000m));
        await LateWithdrawalStoryAsync(kit);
        var (depositLot, depositReliefs) = (await kit.ListLotsAsync())
                                          .Single(l => l.Lot.Origin == AccountingLotOrigin.Acquisition);
        Assert.Null(depositLot.FiatCost);
        Assert.Equal((4L, 1, 100_000_000L),
                     Assert.Single(depositReliefs, r => r.Adjustment > 0) is var r
                         ? (r.LedgerSeq, r.Adjustment, r.Msat)
                         : default);

        // Act: the price of the deposit arrives; the back-valuation lowers the cursor and the projector replays
        await kit.Valuation.ImportAsync(Usd, [new AccountingPricePoint(s_mar5, 80_000m)],
                                        TestContext.Current.CancellationToken);
        await kit.ProjectAsync();

        // Assert: the late withdrawal still relieves the (re-opened) deposit lot, so the lots hold the assets
        Assert.Equal(1_400_000_000L, await AssetMsatAsync(kit));
        Assert.Equal(1_400_000_000L, (await kit.ListLotsAsync()).Sum(l => l.Lot.RemainingMsat));
        var (replayedLot, replayedReliefs) = (await kit.ListLotsAsync())
                                            .Single(l => l.Lot.Origin == AccountingLotOrigin.Acquisition);
        Assert.Equal(400m, replayedLot.FiatCost);
        var relief = Assert.Single(replayedReliefs);
        Assert.Equal((4L, 1, 100_000_000L, (decimal?)80m), (relief.LedgerSeq, relief.Adjustment, relief.Msat,
                                                            relief.FiatCostRelieved));

        // The same book as one that knew the price from the start
        await using var fresh = await FinancialProjectorTestKit.CreateAsync(s_now, AccountingCostBasisMethod.Lifo);
        await fresh.AddPricesAsync((s_jan1, 40_000m), (s_jan20, 60_000m), (s_mar5, 80_000m));
        await LateWithdrawalStoryAsync(fresh);
        Assert.Equal(await fresh.SnapshotFinancialAsync(), await kit.SnapshotFinancialAsync());
    }

    [Fact]
    public async Task Given_ALateFactRelievingAnOpenLot_When_Rebuilt_Then_TheRebuildEqualsTheIncrementalBook()
    {
        // Arrange: as above, with every price known
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now, AccountingCostBasisMethod.Lifo);
        await kit.AddPricesAsync((s_jan1, 40_000m), (s_jan20, 60_000m), (s_mar5, 80_000m));
        await LateWithdrawalStoryAsync(kit);
        var incremental = await kit.SnapshotFinancialAsync();

        // Act
        await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(incremental, await kit.SnapshotFinancialAsync());
        Assert.Equal(1_400_000_000L, (await kit.ListLotsAsync()).Sum(l => l.Lot.RemainingMsat));
        Assert.Equal(1_400_000_000L, await AssetMsatAsync(kit));
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
    }

    [Fact]
    public async Task Given_AnUnvaluedLateFact_When_APriceOfItsTimeArrives_Then_ItIsStagedAgainAtThatPrice()
    {
        // Arrange: January closed with its only price on Jan 1; an invoice of Jan 25 sealed late finds no usable price
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync((s_jan1, 40_000m));
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_jan1),
                           FinancialProjectorTestKit.Cutover(s_jan1));
        await kit.ProjectAsync();
        await kit.Periods.CloseAsync("2026-01", false, TestContext.Current.CancellationToken);
        await kit.AddAsync(kit.Invoice(100_000_000, s_jan25));
        await kit.ProjectAsync();
        var pending = (await kit.ListEntriesAsync(AccountingBook.Financial)).Single(e => e.LedgerSeq == 3);
        Assert.True(pending.Flags.HasFlag(AccountingEntryFlags.LateFact | AccountingEntryFlags.PendingValuation));
        Assert.All(pending.Postings, p => Assert.Null(p.FiatAmount));

        // Act: the prices of the fact's hour and of the adjustment's date arrive
        await kit.Valuation.ImportAsync(Usd,
                                        [
                                            new AccountingPricePoint(s_jan25.AddHours(-1), 45_000m),
                                            new AccountingPricePoint(s_now.AddHours(-1), 90_000m)
                                        ], TestContext.Current.CancellationToken);
        await kit.ProjectAsync();

        // Assert: valued at the fact's price (45), never the adjustment date's (90); its lot carries that cost
        var late = (await kit.ListEntriesAsync(AccountingBook.Financial)).Single(e => e.LedgerSeq == 3);
        Assert.Equal(1, late.Adjustment);
        Assert.False(late.Flags.HasFlag(AccountingEntryFlags.PendingValuation));
        Assert.True(late.Flags.HasFlag(AccountingEntryFlags.LateFact));
        Assert.Equal([45m, -45m], late.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        var lot = (await kit.ListLotsAsync()).Single(l => l.Lot.SourceLedgerSeq == 3).Lot;
        Assert.Equal((1, (decimal?)45m), (lot.SourceAdjustment, lot.FiatCost));
        Assert.Equal(1_100_000_000L, (await kit.ListLotsAsync()).Sum(l => l.Lot.RemainingMsat));
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
    }

    [Fact]
    public async Task Given_TheReversalOfAWalletReceiveFromBeforeTheFeed_When_Projected_Then_ItRelievesTheOpeningLotAtCost()
    {
        // Arrange: LIFO, so without the correction rule the newest lot (the deposit's) would be sold at market
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now, AccountingCostBasisMethod.Lifo);
        await kit.AddPricesAsync((s_jan1, 40_000m), (s_jan10, 50_000m), (s_jan20, 60_000m));
        var preFeed = kit.Deposit(500_000_000, s_jan1.AddDays(-1));
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_jan1),
                           FinancialProjectorTestKit.Cutover(s_jan1), kit.Deposit(1_000_000_000, s_jan10),
                           AccountingConfirmations.CreateReversal(preFeed, s_jan20, 799_999, unrecorded: true));

        // Act
        await kit.ProjectAsync();

        // Assert: the opening lot (1e9 for 400) gives up 5e8 at its cost of 200, nothing is realized, and the cost-basis
        // line brings the assets from their market value (300) to the cost relieved
        var reversal = (await kit.ListEntriesAsync(AccountingBook.Financial)).Single(e => e.LedgerSeq == 4);
        Assert.Equal([("assets:onchain:wallet", -500_000_000L, (decimal?)-300m),
                      ("equity:opening-balances", 500_000_000L, 200m),
                      ("assets:cost-basis", 0L, 100m)],
                     reversal.Postings.Select(p => (p.AccountName!, p.AmountMsat, p.FiatAmount)).ToArray());
        var lots = await kit.ListLotsAsync();
        var (_, openingReliefs) = lots.Single(l => l.Lot.Origin == AccountingLotOrigin.Opening);
        var relief = Assert.Single(openingReliefs);
        Assert.Equal((500_000_000L, (decimal?)200m, (decimal?)200m),
                     (relief.Msat, relief.FiatCostRelieved, relief.Proceeds));
        Assert.Empty(lots.Single(l => l.Lot.Origin == AccountingLotOrigin.Acquisition).Reliefs);

        // The assets' fiat is the open lots' cost (200 + 500)
        var assetFiat = (await kit.ReadAsync(u => u.AccountingBooksDbRepository.GetAccountBalancesAsync(
                                                 AccountingBook.Financial, TestContext.Current.CancellationToken)))
                       .Where(b => FinancialLotRules.IsAsset(b.Account)).Sum(b => b.FiatAmount);
        Assert.Equal(700m, assetFiat);
    }

    /// <summary>
    /// 1-2 the cutover (wallet 1e9 on Jan 1), January closed; 3 a deposit of 5e8 on Mar 5; 4 a withdrawal of 1e8 dated
    /// Jan 20, sealed after the close (a late fact).
    /// </summary>
    private static async Task LateWithdrawalStoryAsync(FinancialProjectorTestKit kit)
    {
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_jan1),
                           FinancialProjectorTestKit.Cutover(s_jan1));
        await kit.ProjectAsync();
        await kit.Periods.CloseAsync("2026-01", false, TestContext.Current.CancellationToken);
        await kit.AddAsync(kit.Deposit(500_000_000, s_mar5));
        await kit.ProjectAsync();
        await kit.AddAsync(kit.Withdrawal(100_000_000, 0, s_jan20));
        await kit.ProjectAsync();
    }

    private static async Task<long> AssetMsatAsync(FinancialProjectorTestKit kit) =>
        (await kit.ReadAsync(u => u.AccountingBooksDbRepository.GetAccountBalancesAsync(
                                 AccountingBook.Financial, TestContext.Current.CancellationToken)))
       .Where(b => FinancialLotRules.IsAsset(b.Account)).Sum(b => b.BalanceMsat);
}