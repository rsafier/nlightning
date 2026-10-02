using System.Text;

namespace NLightning.Application.Tests.Accounting.Financial;

using Application.Accounting.Export.Financial;
using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Lots;
using Domain.Accounting.Models;
using Domain.Accounting.Prices;
using Domain.Client.Enums;
using Domain.Client.Requests;

/// <summary>
/// The financial projector (NL-602 A3-T4, plan §9 A3-T4, D-A7, D-A9, D-A12) on SQLite with the production operational
/// rules, every amount computed by hand: a story of an opening balance, a deposit, a channel open, an invoice, a
/// payment, a forward, a rebalance, a mutual close and a withdrawal under FIFO, LIFO and HIFO, where only the fees and
/// the payments dispose; the late valuation by the back-valuation; a reorg's reversal in the open period; a rebuild
/// equal to the incremental book; a close and its verification with a late fact; the operational book unchanged by the
/// financial one; the lot import; a reclassification in the open period; golden exports through A3-T6's formatters.
/// </summary>
public sealed class FinancialBooksProjectorTests
{
    private const string Usd = FinancialProjectorTestKit.Usd;

    // The story's times and prices (USD per BTC; 1e9 msat = 0.01 BTC)
    private static readonly DateTimeOffset s_t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t1 = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t2 = new(2026, 1, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t3 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t4 = new(2026, 2, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t5 = new(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t6 = new(2026, 2, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t7 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t8 = new(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_now = new(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly (DateTimeOffset, decimal)[] s_prices =
    [
        (s_t0, 40_000m), (s_t1, 50_000m), (s_t2, 60_000m), (s_t3, 30_000m), (s_t4, 70_000m), (s_t5, 70_000m),
        (s_t6, 80_000m), (s_t7, 90_000m), (s_t8, 100_000m)
    ];

    /// <summary>
    /// The realized gain of each disposing entry of the story, by ledger sequence (see <see cref="StoryAsync"/>):
    /// 5 the funding fee 1e6 at 60,000 (0.6), 8 the payment 3e8 + 1e5 at 70,000 (210.07), 10 the rebalance's fee 2e4 at
    /// 80,000 (0.016), 12 the close fee 3e5 at 90,000 (0.27), 15 the withdrawal 5e8 + 2e5 at 100,000 (500.2), each
    /// relieving the lots L1 (opening 1e9 for 400), L2 (deposit 1e9 for 500), L3 (invoice 2e8 for 60) and L4 (forward
    /// fee 5e4 for 0.035) in the method's order; the costs in the comments of the cases.
    /// </summary>
    public static TheoryData<AccountingCostBasisMethod, decimal[]> StoryCases => new()
    {
        // FIFO, L1 throughout: costs 0.4, 120.04, 0.008, 0.12, 200.08
        { AccountingCostBasisMethod.Fifo, [0.2m, 90.03m, 0.008m, 0.15m, 300.12m] },
        // LIFO: L2 0.5; L3 60 + L2 50.05; L4 0.014; L4 0.021 + L2 0.135; L2 250.1
        { AccountingCostBasisMethod.Lifo, [0.1m, 100.02m, 0.002m, 0.114m, 250.1m] },
        // HIFO (L4 7e-7 > L2 5e-7 > L1 4e-7 > L3 3e-7 per msat): L2 0.5; L2 150.05; L4 0.014; L4 0.021 + L2 0.135; L2
        // 250.1
        { AccountingCostBasisMethod.Hifo, [0.1m, 60.02m, 0.002m, 0.114m, 250.1m] }
    };

    [Theory]
    [MemberData(nameof(StoryCases))]
    public async Task Given_TheStory_When_Projected_Then_OnlyTheFeesAndThePaymentsDisposeAtTheHandComputedGains(
        AccountingCostBasisMethod method, decimal[] gains)
    {
        // Arrange
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now, method);
        await kit.AddPricesAsync(s_prices);
        await StoryAsync(kit);

        // Act
        var projected = await kit.ProjectAsync();

        // Assert: one financial entry per operational one
        var operational = await kit.ListEntriesAsync(AccountingBook.Operational);
        var financial = await kit.ListEntriesAsync(AccountingBook.Financial);
        Assert.Equal(16, projected);
        Assert.Equal(operational.Select(e => e.LedgerSeq), financial.Select(e => e.LedgerSeq));
        Assert.Null(kit.Projector.ProjectionError);

        // Only the funding fee, the payment, the rebalance's fee, the close fee and the withdrawal dispose; every other
        // relief moves a part of a lot between our buckets (NL-657)
        var lots = await kit.ListLotsAsync();
        var reliefs = lots.SelectMany(l => l.Reliefs).Where(r => r.IsDisposal).ToList();
        Assert.Equal([5L, 8L, 10L, 12L, 15L], reliefs.Select(r => r.LedgerSeq).Distinct().Order().ToArray());
        Assert.Equal([1_000_000L, 300_100_000L, 20_000L, 300_000L, 500_200_000L],
                     reliefs.GroupBy(r => r.LedgerSeq).OrderBy(g => g.Key).Select(g => g.Sum(r => r.Msat)).ToArray());
        var realized = reliefs.GroupBy(r => r.LedgerSeq).OrderBy(g => g.Key)
                              .Select(g => g.Sum(r => r.Proceeds!.Value - r.FiatCostRelieved!.Value)).ToArray();
        Assert.Equal(gains, realized);
        Assert.All(lots.SelectMany(l => l.Reliefs).Where(r => !r.IsDisposal),
                   r => Assert.Equal((AccountingLotReliefKind.Move, r.FiatCostRelieved), (r.Kind, r.Proceeds)));

        // The gain lines of the entries say the same (a credit to income:gains:realized)
        foreach (var (seq, gain) in new[] { 5L, 8L, 10L, 12L, 15L }.Zip(gains))
        {
            var entry = financial.Single(e => e.LedgerSeq == seq);
            Assert.Equal(-gain, entry.Postings.Single(p => p.AccountName == "income:gains:realized").FiatAmount);
            Assert.Equal(0m, entry.Postings.Sum(p => p.FiatAmount!.Value));
        }

        // Four acquisitions: the opening balance (estimated basis), the deposit, the invoice, the forward fee; the
        // rebalance's incoming half and the transfers acquire nothing (their lots are moved parts), and no bucket owes
        // another
        var acquired = lots.Where(l => l.Lot.ParentLotId is null).ToList();
        Assert.Equal([(AccountingLotOrigin.Opening, 1_000_000_000L, (decimal?)400m, true),
                      (AccountingLotOrigin.Acquisition, 1_000_000_000L, 500m, false),
                      (AccountingLotOrigin.Acquisition, 200_000_000L, 60m, false),
                      (AccountingLotOrigin.Acquisition, 50_000L, 0.035m, false)],
                     acquired.Select(l => (l.Lot.Origin, l.Lot.OriginalMsat, l.Lot.FiatCost, l.Lot.BasisEstimated))
                             .ToArray());
        Assert.DoesNotContain(lots, l => l.Lot.IsDebt);
        Assert.All(lots.Where(l => l.Lot.ParentLotId is not null),
                   l => Assert.NotEqual(l.Lot.AcquiredAt, l.Lot.HeldSinceOrAcquired));

        // The lots hold what the assets hold, and each asset account carries the cost of its own bucket's open lots
        var balances = await kit.ReadAsync(u => u.AccountingBooksDbRepository.GetAccountBalancesAsync(
                                                    AccountingBook.Financial, TestContext.Current.CancellationToken));
        var assets = balances.Where(b => b.AccountName!.StartsWith("assets:", StringComparison.Ordinal)).ToList();
        Assert.Equal(1_398_430_000, lots.Sum(l => l.Lot.RemainingMsat));
        Assert.Equal(1_398_430_000, assets.Sum(b => b.BalanceMsat));
        Assert.DoesNotContain(assets, b => b.AccountName == "assets:cost-basis" && b.FiatAmount != 0m);
        foreach (var account in assets.Where(b => FinancialLotRules.IsAsset(b.Account)))
        {
            var bucket = (AccountingLotBucket)(int)account.Account;
            var held = lots.Where(l => l.Lot.Bucket == bucket).ToList();
            Assert.Equal(held.Sum(l => l.Lot.RemainingMsat), account.BalanceMsat);
            Assert.Equal(held.Sum(l => l.Lot.FiatCost!.Value - l.Reliefs.Sum(r => r.FiatCostRelieved!.Value)),
                         account.FiatAmount);
        }

        // The rebalance is a transfer: its halves net to zero in equity:transfers:rebalance, in msat and in fiat
        Assert.Equal((0L, 0m), (balances.Where(b => b.AccountName == "equity:transfers:rebalance").Sum(b => b.BalanceMsat),
                                balances.Where(b => b.AccountName == "equity:transfers:rebalance")
                                        .Sum(b => b.FiatAmount)));
        Assert.Equal(0, balances.Where(b => b.AccountName == "assets:onchain:clearing").Sum(b => b.BalanceMsat));
    }

    [Fact]
    public async Task Given_TheFifoStory_When_Exported_Then_TheGoldenJournalsBalanceAndMatchTheBook()
    {
        // Arrange
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync(s_prices);
        await StoryAsync(kit);
        await kit.ProjectAsync();
        var entries = await kit.ListEntriesAsync(AccountingBook.Financial);
        var events = await kit.EventsBySeqAsync();
        var prices = await kit.ListPricesAsync();

        // Act
        var journal = AccountingFinancialExportFormatter.WriteDocument(AccountingExportFormat.Hledger, Usd, entries,
                                                                       events, prices);
        var beancount = AccountingFinancialExportFormatter.WriteDocument(AccountingExportFormat.Beancount, Usd,
                                                                         entries, events, prices);
        var csv = AccountingFinancialExportFormatter.WriteDocument(AccountingExportFormat.Csv, Usd, entries, events,
                                                                   prices);

        // Assert: the golden files, which A3-T6's balance checker accepts, with the book's totals
        await AssertGoldenAsync("projector-fifo.journal", journal);
        await AssertGoldenAsync("projector-fifo.beancount", beancount);
        await AssertGoldenAsync("projector-fifo.csv", csv);
        var hledger = JournalBalanceChecker.CheckHledger(journal, Usd);
        var bean = JournalBalanceChecker.CheckBeancount(beancount, Usd);
        Assert.True(hledger.IsValid, string.Join("\n", hledger.Errors));
        Assert.True(bean.IsValid, string.Join("\n", bean.Errors));
        Assert.DoesNotContain("fiat-rounding", journal);
        var balances = await kit.ReadAsync(u => u.AccountingBooksDbRepository.GetAccountBalancesAsync(
                                                    AccountingBook.Financial, TestContext.Current.CancellationToken));
        foreach (var account in balances.GroupBy(b => b.AccountName!))
        {
            var (msat, fiat) = hledger.Accounts[account.Key];
            Assert.Equal(account.Sum(b => b.BalanceMsat), msat);
            Assert.Equal(account.Sum(b => b.FiatAmount), fiat);
        }
    }

    [Fact]
    public async Task Given_NoPriceYet_When_TheBackValuationFindsThemLater_Then_TheBookEqualsOneValuedFromTheStart()
    {
        // Arrange: one book projected before any price is stored, one with the prices from the start
        await using var late = await FinancialProjectorTestKit.CreateAsync(s_now);
        await StoryAsync(late);
        await late.ProjectAsync();
        await using var early = await FinancialProjectorTestKit.CreateAsync(s_now);
        await early.AddPricesAsync(s_prices);
        await StoryAsync(early);
        await early.ProjectAsync();

        // Before: every entry with lines waits for its price, its lots have no cost and its gain is pending
        var pending = await late.ListEntriesAsync(AccountingBook.Financial);
        Assert.All(pending.Where(e => e.Postings.Count > 0),
                   e => Assert.True(e.Flags.HasFlag(AccountingEntryFlags.PendingValuation), e.EventKey));
        var lotsBefore = await late.ListLotsAsync();
        Assert.All(lotsBefore, l => Assert.Null(l.Lot.FiatCost));
        Assert.All(lotsBefore.SelectMany(l => l.Reliefs), r => Assert.Null(r.Proceeds));

        // Act: the operator imports the prices; the back-valuation values the projector's postings and lowers its
        // cursor, and the projector projects them again with their lots and gains
        var import = await late.Valuation.ImportAsync(Usd, s_prices.Select(p => new AccountingPricePoint(p.Item1,
                                                                                                         p.Item2))
                                                                   .ToList(), TestContext.Current.CancellationToken);
        var cursorAfterValuation = await late.ReadAsync(u => u.AccountingBooksDbRepository.GetCursorAsync(
                                                            AccountingBook.Financial,
                                                            TestContext.Current.CancellationToken));
        await late.ProjectAsync();

        // Assert
        Assert.True(import.Valuation!.Valued > 0);
        Assert.Equal(0, cursorAfterValuation);
        Assert.Equal(await early.SnapshotFinancialAsync(), await late.SnapshotFinancialAsync());
        Assert.All(await late.ListLotsAsync(), l => Assert.NotNull(l.Lot.FiatCost));
    }

    [Fact]
    public async Task Given_ADepositReversedInTheOpenPeriod_When_Projected_Then_TheLotsAreRebuiltWithoutIt()
    {
        // Arrange: LIFO, so the withdrawal relieves the deposit's lot until the reorg removes the deposit
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now, AccountingCostBasisMethod.Lifo);
        await kit.AddPricesAsync(s_prices);
        var deposit = kit.Deposit(1_000_000_000, s_t1);
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_t0),
                           FinancialProjectorTestKit.Cutover(s_t0), deposit);
        await kit.AddAsync(kit.Withdrawal(100_000_000, 0, s_t2));
        await kit.ProjectAsync();
        var before = await kit.ListLotsAsync();
        Assert.Equal(4L, Assert.Single(before.Single(l => l.Lot.SourceLedgerSeq == 3).Reliefs).LedgerSeq);

        // Act: the reorg reverses the deposit
        await kit.AddAsync(FinancialProjectorTestKit.Reversal(deposit, s_t3, 799_999));
        await kit.ProjectAsync();

        // Assert: no lot of the deposit; the withdrawal relieves the opening lot (cost 40, proceeds 60: gain 20),
        // borrowed by the clearing account, which owes the wallet for it (NL-657)
        var lots = await kit.ListLotsAsync();
        var (openingLot, openingReliefs) = Assert.Single(lots, l => !l.Lot.IsDebt);
        Assert.Equal((AccountingLotBucket.Clearing, AccountingLotBucket.Wallet, 100_000_000L),
                     Assert.Single(lots, l => l.Lot.IsDebt).Lot is var debt
                         ? (debt.Bucket!.Value, debt.Lender!.Value, debt.RemainingMsat)
                         : default);
        Assert.Equal(AccountingLotOrigin.Opening, openingLot.Origin);
        var relief = Assert.Single(openingReliefs);
        Assert.Equal((4L, 100_000_000L, (decimal?)40m, (decimal?)60m),
                     (relief.LedgerSeq, relief.Msat, relief.FiatCostRelieved, relief.Proceeds));

        // The deposit and its reversal cancel out in msat and fiat
        var entries = await kit.ListEntriesAsync(AccountingBook.Financial);
        var depositEntry = entries.Single(e => e.LedgerSeq == 3);
        var reversalEntry = entries.Single(e => e.LedgerSeq == 5);
        Assert.Contains("reversed in the open period by", depositEntry.Note);
        Assert.Equal(depositEntry.Postings.Select(p => (p.AccountName, -p.AmountMsat, -p.FiatAmount)),
                     reversalEntry.Postings.Select(p => (p.AccountName, p.AmountMsat, p.FiatAmount)));

        // The same book as one projected with the reversal known from the start (a cancelled pair, no rollback)
        await using var fresh = await FinancialProjectorTestKit.CreateAsync(s_now, AccountingCostBasisMethod.Lifo);
        await fresh.AddPricesAsync(s_prices);
        var freshDeposit = fresh.Deposit(1_000_000_000, s_t1);
        await fresh.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_t0),
                             FinancialProjectorTestKit.Cutover(s_t0), freshDeposit, fresh.Withdrawal(100_000_000, 0, s_t2),
                             FinancialProjectorTestKit.Reversal(freshDeposit, s_t3, 799_999));
        await fresh.ProjectAsync();
        Assert.Equal(await fresh.SnapshotFinancialAsync(), await kit.SnapshotFinancialAsync());
    }

    [Theory]
    [InlineData(AccountingCostBasisMethod.Fifo)]
    [InlineData(AccountingCostBasisMethod.Hifo)]
    public async Task Given_TheStoryProjectedInSteps_When_Rebuilt_Then_TheRebuildEqualsTheIncrementalBook(
        AccountingCostBasisMethod method)
    {
        // Arrange: three rounds, two of them on a page size of 3 (several saves per round)
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now, method, pageSize: 3);
        await kit.AddPricesAsync(s_prices);
        var events = Story(kit);
        foreach (var chunk in events.Chunk(6))
        {
            await kit.AddAsync(chunk);
            await kit.ProjectAsync();
        }

        var incremental = await kit.SnapshotFinancialAsync();

        // Act
        await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(incremental, await kit.SnapshotFinancialAsync());
    }

    [Fact]
    public async Task Given_ProjectedMonths_When_ClosedWithALateFactAfter_Then_VerifyHoldsAndTheFactIsAnAdjustment()
    {
        // Arrange
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync(s_prices);
        await StoryAsync(kit);
        await kit.ProjectAsync();

        // Act: close January and February, then an invoice of January sealed late
        var january = await kit.Periods.CloseAsync("2026-01", false, TestContext.Current.CancellationToken);
        var february = await kit.Periods.CloseAsync("2026-02", false, TestContext.Current.CancellationToken);
        await kit.AddPricesAsync((new DateTimeOffset(2026, 1, 25, 0, 0, 0, TimeSpan.Zero), 45_000m));
        await kit.AddAsync(kit.Invoice(100_000_000, new DateTimeOffset(2026, 1, 25, 6, 0, 0, TimeSpan.Zero)));
        await kit.ProjectAsync();
        var verified = await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken);

        // Assert: both closes still verify
        Assert.False(january.Period.Forced);
        Assert.False(february.Period.Forced);
        Assert.Equal(2, verified.Count);
        Assert.All(verified, v => Assert.True(v.IsIntact, v.Problem));

        // The late invoice is an adjustment of the open period at January's price, its lot dated as the adjustment
        var late = (await kit.ListEntriesAsync(AccountingBook.Financial)).Single(e => e.LedgerSeq == 17);
        Assert.Equal(1, late.Adjustment);
        Assert.True(late.Flags.HasFlag(AccountingEntryFlags.Adjustment));
        Assert.Equal(s_now, late.OccurredAt);
        Assert.Equal([45m, -45m], late.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        var lot = (await kit.ListLotsAsync()).Single(l => l.Lot.SourceLedgerSeq == 17).Lot;
        Assert.Equal((1, s_now, (decimal?)45m), (lot.SourceAdjustment, lot.AcquiredAt, lot.FiatCost));

        // A rebuild from the last close equals the incremental book
        var incremental = await kit.SnapshotFinancialAsync();
        await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);
        Assert.Equal(incremental, await kit.SnapshotFinancialAsync());
    }

    [Fact]
    public async Task Given_TheSameFeed_When_ProjectedWithAndWithoutTheFinancialBook_Then_TheOperationalBookIsIdentical()
    {
        // Arrange
        await using var operationalOnly =
            await FinancialProjectorTestKit.CreateAsync(s_now, profile: AccountingProfile.Operational);
        await using var financial = await FinancialProjectorTestKit.CreateAsync(s_now);
        await financial.AddPricesAsync(s_prices);
        await StoryAsync(operationalOnly);
        await StoryAsync(financial);

        // Act
        var nothing = await operationalOnly.ProjectAsync();
        var projected = await financial.ProjectAsync();

        // Assert
        Assert.Equal(0, nothing);
        Assert.Equal(16, projected);
        Assert.Empty(await operationalOnly.ListEntriesAsync(AccountingBook.Financial));
        Assert.Equal(await operationalOnly.SnapshotOperationalAsync(), await financial.SnapshotOperationalAsync());
    }

    [Fact]
    public async Task Given_ImportedLots_When_ThePaymentIsProjected_Then_ItRelievesThemAndTheAssetsCarryTheirCost()
    {
        // Arrange: opening balances of 1e9 (wallet) and 5e8 (a channel) at 40,000 (600), a payment of 1e8 at 50,000
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync(s_prices);
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_t0),
                           FinancialProjectorTestKit.Opening("channel:aa", 500_000_000, s_t0),
                           FinancialProjectorTestKit.Cutover(s_t0), kit.Payment(100_000_000, 0, s_t1));
        await kit.ProjectAsync();

        // Act: lots of 1e6 sats for 300 and 5e5 sats (less 400 msat) for 250
        var result = await kit.Projector.ImportAsync(null,
        [
            new AccountingLotPoint(new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), 1_000_000_000, 300m),
            new AccountingLotPoint(new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero), 499_999_600, 250m)
        ], TestContext.Current.CancellationToken);

        // Assert: the opening lots replaced, the last lot made up to the msat
        Assert.Equal((2, 1_500_000_000L, 550m, 400L, 4), (result.Imported, result.ImportedMsat, result.ImportedCost,
                                                         result.AdjustedMsat, result.ProjectedEntries));
        var lots = await kit.ListLotsAsync();
        var imported = lots.Where(l => l.Lot.ParentLotId is null).ToList();
        Assert.Equal([AccountingLotOrigin.Import, AccountingLotOrigin.Import],
                     imported.Select(l => l.Lot.Origin).ToArray());
        Assert.Equal(500_000_000, imported[1].Lot.OriginalMsat);

        // Each opening balance takes the imported lots in order at their cost (NL-657): the wallet the first (1e9 for
        // 300), the channel the second (5e8 for 250); the moved parts keep the imported acquisition times
        var wallet = lots.Single(l => l.Lot.Bucket == AccountingLotBucket.Wallet).Lot;
        var (channelLot, channelReliefs) = lots.Single(l => l.Lot.Bucket == AccountingLotBucket.Channels);
        Assert.Equal((imported[0].Lot.Id, (decimal?)300m, imported[0].Lot.AcquiredAt),
                     (wallet.ParentLotId!.Value, wallet.FiatCost, wallet.HeldSinceOrAcquired));
        Assert.Equal((imported[1].Lot.Id, (decimal?)250m), (channelLot.ParentLotId!.Value, channelLot.FiatCost));

        // FIFO in the channel: the payment relieves the channel's part, 1e8 of 5e8 for 250 = 50, proceeds 50, no gain
        var relief = Assert.Single(channelReliefs);
        Assert.Equal((100_000_000L, (decimal?)50m, (decimal?)50m), (relief.Msat, relief.FiatCostRelieved, relief.Proceeds));

        // The opening entries carry the imported cost; the cutover marker posts nothing
        var entries = await kit.ListEntriesAsync(AccountingBook.Financial);
        Assert.Empty(entries.Single(e => e.EventKey == "open:cutover").Postings);
        Assert.Equal([300m, -300m], entries.Single(e => e.LedgerSeq == 1).Postings.Select(p => p.FiatAmount!.Value));
        var balances = await kit.ReadAsync(u => u.AccountingBooksDbRepository.GetAccountBalancesAsync(
                                                    AccountingBook.Financial, TestContext.Current.CancellationToken));
        Assert.Equal(500m, balances.Where(b => b.AccountName!.StartsWith("assets:", StringComparison.Ordinal))
                                   .Sum(b => b.FiatAmount));
    }

    [Fact]
    public async Task Given_LotsThatDoNotHoldTheOpeningBalances_When_Imported_Then_TheImportIsRefused()
    {
        // Arrange
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync(s_prices);
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_t0),
                           FinancialProjectorTestKit.Cutover(s_t0));
        await kit.ProjectAsync();

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => kit.Projector.ImportAsync(
            null, [new AccountingLotPoint(s_t0, 999_999_000, 300m)], TestContext.Current.CancellationToken));

        // Assert: one sat short; the opening lot stays
        Assert.Contains("must agree to the sat", exception.Message);
        Assert.Equal(AccountingLotOrigin.Opening, Assert.Single(await kit.ListLotsAsync()).Lot.Origin);
    }

    [Fact]
    public async Task Given_AClosedPeriod_When_LotsAreImported_Then_TheImportIsRefused()
    {
        // Arrange
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync(s_prices);
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_t0),
                           FinancialProjectorTestKit.Cutover(s_t0));
        await kit.ProjectAsync();
        await kit.Periods.CloseAsync("2026-01", false, TestContext.Current.CancellationToken);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Projector.ImportAsync(
            null, [new AccountingLotPoint(s_t0, 1_000_000_000, 300m)], TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("only through an adjustment", exception.Message);
    }

    [Fact]
    public async Task Given_AnOverrideOfAnOpenPeriodEntry_When_Set_Then_TheFinancialBookProjectsItAgainReclassified()
    {
        // Arrange
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync(s_prices);
        var invoice = kit.Invoice(200_000_000, s_t3);
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_t0),
                           FinancialProjectorTestKit.Cutover(s_t0), invoice, kit.Payment(50_000_000, 0, s_t4));
        await kit.ProjectAsync();
        var before = await kit.SnapshotFinancialAsync();

        // Act
        await kit.Classification.HandleAsync(new AccountingClassifyClientRequest
        {
            Action = AccountingClassifyAction.Set,
            EventKey = invoice.EventKey,
            Account = "income:consulting"
        }, TestContext.Current.CancellationToken);
        await kit.ProjectAsync();

        // Assert: the invoice now goes to the override's account; the payment after it was projected again too
        var entry = (await kit.ListEntriesAsync(AccountingBook.Financial)).Single(e => e.EventKey == invoice.EventKey);
        Assert.Equal(AccountingClassificationSource.Override, entry.Classification);
        Assert.Contains(entry.Postings, p => p.AccountName == "income:consulting");
        Assert.Equal(before.Replace("income:sales", "income:consulting").Replace(" Default  ", " Override  "),
                     (await kit.SnapshotFinancialAsync()).Replace(" Default  ", " Override  "));
    }

    [Fact]
    public async Task Given_ARuleAddedAfterAClose_When_Projected_Then_OnlyTheOpenPeriodIsReclassified()
    {
        // Arrange: an invoice labelled consulting in January (closed) and one in February (open)
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync(s_prices);
        var january = kit.Invoice(100_000_000, s_t1, label: "consulting");
        var february = kit.Invoice(100_000_000, s_t3, label: "consulting");
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_t0),
                           FinancialProjectorTestKit.Cutover(s_t0), january, february);
        await kit.ProjectAsync();
        await kit.Periods.CloseAsync("2026-01", false, TestContext.Current.CancellationToken);

        // Act
        await kit.Classification.HandleAsync(new AccountingClassifyClientRequest
        {
            Action = AccountingClassifyAction.RuleAdd,
            Rule = new AccountingRule(0, 1, null, "^consult", null, null, null, null, null, "income:consulting", true,
                                      s_now)
        }, TestContext.Current.CancellationToken);
        await kit.ProjectAsync();

        // Assert: February's invoice follows the rule; January's closed entry is left alone and an adjustment in the
        // open period moves it (NL-660)
        var entries = await kit.ListEntriesAsync(AccountingBook.Financial);
        var feb = entries.Single(e => e.EventKey == february.EventKey);
        var jan = entries.Single(e => e.EventKey == january.EventKey && e.Adjustment == 0);
        var moved = entries.Single(e => e.EventKey == january.EventKey && e.Adjustment == 1);
        Assert.Equal(AccountingClassificationSource.Rule, feb.Classification);
        Assert.Contains(feb.Postings, p => p.AccountName == "income:consulting");
        Assert.Contains(jan.Postings, p => p.AccountName == "income:sales");
        Assert.Contains("RuleChange", moved.Note);
        Assert.Equal(["income:sales", "income:consulting"], moved.Postings.Select(p => p.AccountName));
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
    }

    [Fact]
    public async Task Given_AFactOfAClosedPeriodReversed_When_Projected_Then_ItsNegationPostsInTheOpenPeriod()
    {
        // Arrange: a deposit of January, January closed, then a reorg reverses it
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync(s_prices);
        var deposit = kit.Deposit(1_000_000_000, s_t1);
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_t0),
                           FinancialProjectorTestKit.Cutover(s_t0), deposit);
        await kit.ProjectAsync();
        await kit.Periods.CloseAsync("2026-01", false, TestContext.Current.CancellationToken);

        // Act
        await kit.AddAsync(FinancialProjectorTestKit.Reversal(deposit, s_t4, 799_999));
        await kit.ProjectAsync();

        // Assert: the deposit's lines negated at their January value (500), flagged as an adjustment; the deposit's
        // own lot (1e9 for 500) is taken back at its cost: nothing is realized (NL-675)
        var reversal = (await kit.ListEntriesAsync(AccountingBook.Financial)).Single(e => e.LedgerSeq == 4);
        Assert.True(reversal.Flags.HasFlag(AccountingEntryFlags.Adjustment));
        Assert.Equal([("assets:onchain:wallet", -1_000_000_000L, (decimal?)-500m),
                      ("equity:transfers:in", 1_000_000_000L, 500m)],
                     reversal.Postings.Select(p => (p.AccountName!, p.AmountMsat, p.FiatAmount)).ToArray());
        var lots = await kit.ListLotsAsync();
        var relief = Assert.Single(lots.Single(l => l.Lot.SourceLedgerSeq == 3).Reliefs);
        Assert.Equal((4L, (decimal?)500m, (decimal?)500m), (relief.LedgerSeq, relief.FiatCostRelieved, relief.Proceeds));
        Assert.Empty(lots.Single(l => l.Lot.Origin == AccountingLotOrigin.Opening).Reliefs);
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
    }

    /// <summary>Writes the story's events (ledger sequences 1 to 16).</summary>
    private static Task StoryAsync(FinancialProjectorTestKit kit) => kit.AddAsync(Story(kit));

    /// <summary>
    /// 1-2 the cutover: wallet 1e9 (lot L1 at 40,000 = 400); 3 a deposit of 1e9 (L2 at 50,000 = 500); 4-6 a channel
    /// open spending it: 8.99e8 to the channel, fee 1e6, change 1e8; 7 an invoice of 2e8 (L3 at 30,000 = 60); 8 a
    /// payment of 3e8 + fee 1e5; 9 a forward fee of 5e4 (L4 at 70,000 = 0.035); 10-11 a rebalance of 1e7 with a fee of
    /// 2e4; 12-13 a mutual close of the channel's 7.9893e8, fee 3e5; 14-16 a withdrawal of 5e8, fee 2e5, spending the
    /// opening output with 4.998e8 of change.
    /// </summary>
    private static AccountingEventModel[] Story(FinancialProjectorTestKit kit) =>
    [
        FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_t0),
        FinancialProjectorTestKit.Cutover(s_t0),
        kit.Deposit(1_000_000_000, s_t1),
        kit.WalletSpent(1_000_000_000, s_t2),
        kit.Funded(899_000_000, 1_000_000, s_t2),
        kit.WalletIn(100_000_000, s_t2),
        kit.Invoice(200_000_000, s_t3, label: "consulting"),
        kit.Payment(300_000_000, 100_000, s_t4),
        kit.Forward(50_000, s_t5),
        kit.Payment(10_000_000, 20_000, s_t6, selfPayment: true),
        kit.Invoice(10_000_000, s_t6, selfPayment: true),
        kit.MutualClose(798_930_000, 300_000, s_t7),
        kit.WalletIn(798_630_000, s_t7, "channel"),
        kit.WalletSpent(1_000_000_000, s_t8),
        kit.Withdrawal(500_000_000, 200_000, s_t8),
        kit.WalletIn(499_800_000, s_t8)
    ];

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