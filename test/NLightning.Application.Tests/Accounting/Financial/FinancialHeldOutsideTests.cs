namespace NLightning.Application.Tests.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Models;
using Domain.Client.Enums;
using Domain.Client.Requests;

/// <summary>
/// NL-674 (D-A12 as amended 2026-10-02) on SQLite with the production rules: a withdrawal a rule classifies to the
/// operator's cold storage (<c>equity:transfers:cold-storage</c>) moves its lots out of the node at cost, realizing only
/// the fee; the deposit a rule classifies as the transfer back brings the same lots in at their original cost and
/// acquisition time; an unclassified withdrawal and deposit dispose and acquire as before. Every amount by hand.
/// </summary>
/// <remarks>
/// Prices (USD per BTC): Jan 1 40,000; Jan 10 50,000; Mar 1 80,000. The story: 1-2 the cutover (wallet 1e9: lot O for
/// 400); 3 the wallet's output spent (O moves to the clearing account); 4 a withdrawal of 5e8 + fee 1e5 tagged
/// <c>dest=cold</c>; 5 its change 4.999e8; 6 a deposit of 5e8 labelled <c>from cold</c> on Mar 1.
/// </remarks>
public sealed class FinancialHeldOutsideTests
{
    private const string ColdStorage = "equity:transfers:cold-storage";

    private static readonly DateTimeOffset s_jan1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_jan10 = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_mar1 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_now = new(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_AWithdrawalToColdStorageAndTheDepositBack_When_Classified_Then_NoGainAndTheOriginalLotsReturn()
    {
        // Arrange: the rules first, then the story
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync((s_jan1, 40_000m), (s_jan10, 50_000m), (s_mar1, 80_000m));
        await AddRuleAsync(kit, new AccountingRule(0, 1, null, null, "dest", "cold", null, null, null, ColdStorage,
                                                   true, s_now));
        await AddRuleAsync(kit, new AccountingRule(0, 2, null, "^from cold", null, null, null, null, null,
                                                   ColdStorage, true, s_now));
        await kit.AddAsync(Story(kit));

        // Act
        await kit.ProjectAsync();

        // Assert: the withdrawal realizes only its fee (1e5 of O: cost 0.04, proceeds 0.05) and moves 5e8 of O out at
        // cost (200)
        var entries = await kit.ListEntriesAsync(AccountingBook.Financial);
        var withdrawal = entries.Single(e => e.LedgerSeq == 4);
        Assert.Equal([(ColdStorage, 500_000_000L, (decimal?)200m), ("expenses:fees:withdraw", 100_000L, 0.05m),
                      ("assets:onchain:clearing", -500_100_000L, -200.04m), ("income:gains:realized", 0L, -0.01m)],
                     withdrawal.Postings.Select(p => (p.AccountName!, p.AmountMsat, p.FiatAmount)).ToArray());

        // The deposit back takes the held lot in at its cost (200), not the market (400), and acquires nothing
        var deposit = entries.Single(e => e.LedgerSeq == 6);
        Assert.Equal([("assets:onchain:wallet", 500_000_000L, (decimal?)200m), (ColdStorage, -500_000_000L, -200m)],
                     deposit.Postings.Select(p => (p.AccountName!, p.AmountMsat, p.FiatAmount)).ToArray());
        var lots = await kit.ListLotsAsync();
        Assert.Single(lots, l => l.Lot.ParentLotId is null);
        var back = lots.Single(l => l.Lot.SourceLedgerSeq == 6).Lot;
        Assert.Equal((AccountingLotBucket.Wallet, AccountingLotOrigin.Opening, 500_000_000L, (decimal?)200m, s_jan1,
                      true),
                     (back.Bucket!.Value, back.Origin, back.RemainingMsat, back.FiatCost, back.HeldSinceOrAcquired,
                      back.BasisEstimated));

        // Only the fee was disposed of; the cold storage account is back to zero; nothing is left outside
        var disposals = lots.SelectMany(l => l.Reliefs).Where(r => r.IsDisposal).ToList();
        Assert.Equal((100_000L, 0.01m), (Assert.Single(disposals).Msat,
                                        disposals[0].Proceeds!.Value - disposals[0].FiatCostRelieved!.Value));
        var balances = await kit.ReadAsync(u => u.AccountingBooksDbRepository.GetAccountBalancesAsync(
                                                    AccountingBook.Financial, TestContext.Current.CancellationToken));
        Assert.Equal((0L, 0m), (balances.Where(b => b.AccountName == ColdStorage).Sum(b => b.BalanceMsat),
                                balances.Where(b => b.AccountName == ColdStorage).Sum(b => b.FiatAmount)));
        Assert.DoesNotContain(lots, l => l.Lot.Bucket == AccountingLotBucket.HeldOutside && l.Lot.RemainingMsat > 0);

        // The wallet holds O's change part (4.999e8 for 199.96) and the part back (5e8 for 200)
        Assert.Equal(399.96m, balances.Where(b => b.AccountName == "assets:onchain:wallet").Sum(b => b.FiatAmount));
    }

    [Fact]
    public async Task Given_TheSameStoryUnclassified_When_Projected_Then_TheWithdrawalDisposesAndTheDepositAcquiresAsBefore()
    {
        // Arrange: no rule
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync((s_jan1, 40_000m), (s_jan10, 50_000m), (s_mar1, 80_000m));
        await kit.AddAsync(Story(kit));

        // Act
        await kit.ProjectAsync();

        // Assert: the withdrawal sells 5e8 of O (cost 200, proceeds 250: 50) and the fee (0.01); the deposit opens a lot
        // at market (400)
        var entries = await kit.ListEntriesAsync(AccountingBook.Financial);
        Assert.Equal([("equity:transfers:out", 500_000_000L, (decimal?)250m), ("expenses:fees:withdraw", 100_000L, 0.05m),
                      ("assets:onchain:clearing", -500_100_000L, -200.04m), ("income:gains:realized", 0L, -50.01m)],
                     entries.Single(e => e.LedgerSeq == 4).Postings
                            .Select(p => (p.AccountName!, p.AmountMsat, p.FiatAmount)).ToArray());
        var acquired = (await kit.ListLotsAsync()).Single(l => l.Lot.SourceLedgerSeq == 6).Lot;
        Assert.Equal((AccountingLotOrigin.Acquisition, (long?)null, (decimal?)400m),
                     (acquired.Origin, acquired.ParentLotId, acquired.FiatCost));
    }

    [Fact]
    public async Task Given_TheColdStorageStory_When_RebuiltAndClosed_Then_TheRebuildEqualsTheIncrementalBookAndVerifies()
    {
        // Arrange: projected in three rounds
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now, pageSize: 2);
        await kit.AddPricesAsync((s_jan1, 40_000m), (s_jan10, 50_000m), (s_mar1, 80_000m));
        await AddRuleAsync(kit, new AccountingRule(0, 1, null, null, "dest", "cold", null, null, null, ColdStorage,
                                                   true, s_now));
        await AddRuleAsync(kit, new AccountingRule(0, 2, null, "^from cold", null, null, null, null, null,
                                                   ColdStorage, true, s_now));
        foreach (var chunk in Story(kit).Chunk(2))
        {
            await kit.AddAsync(chunk);
            await kit.ProjectAsync();
        }

        var incremental = await kit.SnapshotFinancialAsync();

        // Act: January closed (the withdrawal and its held lot), then the whole open period rebuilt
        await kit.Periods.CloseAsync("2026-01", false, TestContext.Current.CancellationToken);
        var closed = await kit.SnapshotFinancialAsync();
        await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(incremental.Split('\n').Length, closed.Split('\n').Length);
        Assert.Equal(closed, await kit.SnapshotFinancialAsync());
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));
        var lots = await kit.ListLotsAsync();
        Assert.Equal((decimal?)200m, lots.Single(l => l.Lot.SourceLedgerSeq == 6).Lot.FiatCost);
    }

    private static AccountingEventModel[] Story(FinancialProjectorTestKit kit) =>
    [
        FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_jan1),
        FinancialProjectorTestKit.Cutover(s_jan1),
        kit.WalletSpent(1_000_000_000, s_jan10),
        FinancialProjectorTestKit.Event("wsend:cold", AccountingEventKind.WalletSent, s_jan10, -500_000_000, 100_000,
                                        (AccountingDetailKeys.TagPrefix + "dest", "cold")),
        kit.WalletIn(499_900_000, s_jan10),
        kit.Deposit(500_000_000, s_mar1, "from cold")
    ];

    private static Task AddRuleAsync(FinancialProjectorTestKit kit, AccountingRule rule) =>
        kit.Classification.HandleAsync(new AccountingClassifyClientRequest
        {
            Action = AccountingClassifyAction.RuleAdd,
            Rule = rule
        }, TestContext.Current.CancellationToken);
}