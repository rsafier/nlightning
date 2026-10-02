using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Accounting.Financial;

using Application.Accounting;
using Application.Accounting.Reports.Financial;
using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Models;
using Domain.Accounting.Prices;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;

/// <summary>
/// The reclassification of a closed period's entry (NL-660, plan D-A8): <c>classify set|unset</c> and rule changes
/// never rewrite a closed entry; each posts one adjustment in the open period, dated now, that moves the entry's
/// classifiable lines (msat and fiat at the original value) from the account the book holds them in to the new one,
/// moves no lot, keeps the closes intact and survives a rebuild. The unclassified listing leaves out a closed entry
/// reclassified out of the unclassified accounts (NL-667).
/// </summary>
public sealed class FinancialReclassificationTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t1 = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t3 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_now = new(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly (DateTimeOffset, decimal)[] s_prices =
    [
        (s_t0, 40_000m), (s_t1, 50_000m), (s_t3, 30_000m)
    ];

    [Fact]
    public async Task Given_AClosedEntry_When_AnOverrideIsSetAndUnset_Then_EachPostsOneAdjustmentInTheOpenPeriod()
    {
        // Arrange: a January invoice of 1e8 msat at 50,000 (50 USD), January closed, a February invoice (open)
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        var (january, february) = await ArrangeClosedJanuaryAsync(kit);
        var closedBefore = await EntriesAsync(kit, january.EventKey);
        var februaryBefore = await EntriesAsync(kit, february.EventKey);
        var lotsBefore = await LotsAsync(kit);

        // Act
        var set = await ClassifyAsync(kit, AccountingClassifyAction.Set, january.EventKey, "income:consulting");
        await kit.ProjectAsync();
        var again = await ClassifyAsync(kit, AccountingClassifyAction.Set, january.EventKey, "income:consulting");
        await kit.ProjectAsync();
        var afterSet = await EntriesAsync(kit, january.EventKey);
        var unset = await ClassifyAsync(kit, AccountingClassifyAction.Unset, january.EventKey);
        await kit.ProjectAsync();
        var afterUnset = await EntriesAsync(kit, january.EventKey);

        // Assert: the closed entry is untouched; one adjustment per change, dated now, in the open period
        Assert.Contains(set.Warnings, w => w.Contains("closed period 2026-01", StringComparison.Ordinal));
        Assert.DoesNotContain(again.Warnings, w => w.Contains("closed period", StringComparison.Ordinal));
        Assert.True(unset.Changed);
        Assert.Equal(FinancialProjectorTestKit.Describe(closedBefore[0]),
                     FinancialProjectorTestKit.Describe(afterUnset[0]));
        Assert.Equal(2, afterSet.Count);
        var moved = afterSet[1];
        Assert.Equal(1, moved.Adjustment);
        Assert.Equal(AccountingEntryFlags.Adjustment, moved.Flags);
        Assert.Equal(AccountingClassificationSource.Override, moved.Classification);
        Assert.Null(moved.ClosedPeriodId);
        Assert.Equal(s_now, moved.OccurredAt);
        Assert.Contains("[reclass:1] adjusts 2026-01: Override", moved.Note);
        Assert.Equal([("income:sales", 100_000_000L, (decimal?)50m), ("income:consulting", -100_000_000L, -50m)],
                     Lines(moved));

        Assert.Equal(3, afterUnset.Count);
        Assert.Equal(AccountingClassificationSource.Default, afterUnset[2].Classification);
        Assert.Equal([("income:consulting", 100_000_000L, (decimal?)50m), ("income:sales", -100_000_000L, -50m)],
                     Lines(afterUnset[2]));

        // No lot moved, the open period's entry is unchanged, the closes verify
        Assert.Equal(lotsBefore, await LotsAsync(kit));
        Assert.Equal(februaryBefore.Select(FinancialProjectorTestKit.Describe),
                     (await EntriesAsync(kit, february.EventKey)).Select(FinancialProjectorTestKit.Describe));
        await AssertClosesIntactAsync(kit);
    }

    [Fact]
    public async Task Given_AClosedEntry_When_ARuleIsAddedDisabledEnabledAndAddedAgain_Then_EachChangeMovesItOnce()
    {
        // Arrange
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        var (january, february) = await ArrangeClosedJanuaryAsync(kit);
        var lotsBefore = await LotsAsync(kit);

        // Act: add (moves to consulting), add a later rule of the same priority (no change), disable the first (moves
        // to the second's account), enable it again (back to consulting)
        var first = (await ClassifyAsync(kit, new AccountingClassifyClientRequest
        {
            Action = AccountingClassifyAction.RuleAdd,
            Rule = Rule("income:consulting")
        })).Rules!.Single();
        await kit.ProjectAsync();
        await ClassifyAsync(kit, new AccountingClassifyClientRequest
        {
            Action = AccountingClassifyAction.RuleAdd,
            Rule = Rule("income:advice")
        });
        await kit.ProjectAsync();
        var afterAdds = await EntriesAsync(kit, january.EventKey);
        await ClassifyAsync(kit, new AccountingClassifyClientRequest
        {
            Action = AccountingClassifyAction.RuleDisable,
            RuleId = first.Id
        });
        await kit.ProjectAsync();
        await ClassifyAsync(kit, new AccountingClassifyClientRequest
        {
            Action = AccountingClassifyAction.RuleEnable,
            RuleId = first.Id
        });
        await kit.ProjectAsync();

        // Assert
        Assert.Equal(2, afterAdds.Count);
        var entries = await EntriesAsync(kit, january.EventKey);
        Assert.Equal(4, entries.Count);
        Assert.All(entries.Skip(1), e => Assert.Contains("RuleChange", e.Note));
        Assert.All(entries.Skip(1), e => Assert.Equal(AccountingClassificationSource.Rule, e.Classification));
        Assert.Null(entries[1].RuleId); // the rule had no id before its save
        Assert.Equal(first.Id + 1, entries[2].RuleId);
        Assert.Equal(first.Id, entries[3].RuleId);
        Assert.Equal([("income:sales", 100_000_000L, (decimal?)50m), ("income:consulting", -100_000_000L, -50m)],
                     Lines(entries[1]));
        Assert.Equal([("income:consulting", 100_000_000L, (decimal?)50m), ("income:advice", -100_000_000L, -50m)],
                     Lines(entries[2]));
        Assert.Equal([("income:advice", 100_000_000L, (decimal?)50m), ("income:consulting", -100_000_000L, -50m)],
                     Lines(entries[3]));

        // The open period follows the rules by projection, not by adjustment
        var feb = Assert.Single(await EntriesAsync(kit, february.EventKey));
        Assert.Contains(feb.Postings, p => p.AccountName == "income:consulting");
        Assert.Equal(lotsBefore, await LotsAsync(kit));
        await AssertClosesIntactAsync(kit);
    }

    [Fact]
    public async Task Given_ReclassificationsOfAClosedEntry_When_TheFinancialBookIsRebuilt_Then_TheBooksAreTheSame()
    {
        // Arrange
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        var (january, _) = await ArrangeClosedJanuaryAsync(kit);
        await ClassifyAsync(kit, AccountingClassifyAction.Set, january.EventKey, "income:consulting");
        await ClassifyAsync(kit, new AccountingClassifyClientRequest
        {
            Action = AccountingClassifyAction.RuleAdd,
            Rule = Rule("income:advice")
        });
        await kit.ProjectAsync();
        await ClassifyAsync(kit, AccountingClassifyAction.Unset, january.EventKey);
        await kit.ProjectAsync();
        var incremental = await kit.SnapshotFinancialAsync();

        // Act
        await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);

        // Assert: the override's two adjustments are kept (the rule moved nothing while the override won)
        Assert.Equal(incremental, await kit.SnapshotFinancialAsync());
        var entries = await EntriesAsync(kit, january.EventKey);
        Assert.Equal(3, entries.Count);
        Assert.Equal([("income:consulting", 100_000_000L, (decimal?)50m), ("income:advice", -100_000_000L, -50m)],
                     Lines(entries[2]));
        await AssertClosesIntactAsync(kit);
    }

    [Fact]
    public async Task Given_AnUnclassifiedClosedEntry_When_ReclassifiedLater_Then_TheUnclassifiedListingLeavesItOut()
    {
        // Arrange: a January push (unclassified by default), January closed by force
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync(s_prices);
        var push = FinancialProjectorTestKit.Event("push:1", AccountingEventKind.PushReceived, s_t1, 2_000_000);
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_t0),
                           FinancialProjectorTestKit.Cutover(s_t0), push);
        await kit.ProjectAsync();
        await kit.Periods.CloseAsync("2026-01", true, TestContext.Current.CancellationToken);
        var reports = CreateReports(kit);
        var listedBefore = await ListUnclassifiedAsync(reports);

        // Act
        await ClassifyAsync(kit, AccountingClassifyAction.Set, push.EventKey, "income:gifts");
        var listedAfter = await ListUnclassifiedAsync(reports);
        await ClassifyAsync(kit, AccountingClassifyAction.Unset, push.EventKey);
        var listedAfterUnset = await ListUnclassifiedAsync(reports);

        // Assert
        Assert.Equal([push.EventKey], listedBefore.Entries.Select(e => e.EventKey));
        Assert.Empty(listedAfter.Entries);
        Assert.False(listedAfter.HasMore);
        Assert.Equal([push.EventKey], listedAfterUnset.Entries.Select(e => e.EventKey));
        Assert.Equal(3, (await EntriesAsync(kit, push.EventKey)).Count);
        await AssertClosesIntactAsync(kit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AClosedEntryRepricedAndReclassified_When_ReclassifiedBack_Then_ThePriceCorrectionFollowsIt(
        bool repricedFirst)
    {
        // Arrange: the January invoice valued at 50,000 (-50 USD on income:sales), January closed
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        var (january, _) = await ArrangeClosedJanuaryAsync(kit);
        var replacement = new AccountingPriceReplacement(null, s_t1, 45_000m, null, "wrong price");

        // Act: the price replaced (+5 income:sales, -5 cost basis) before or after the move to income:consulting
        if (repricedFirst)
            await kit.Valuation.ReplaceAsync(replacement, TestContext.Current.CancellationToken);
        await ClassifyAsync(kit, AccountingClassifyAction.Set, january.EventKey, "income:consulting");
        if (!repricedFirst)
            await kit.Valuation.ReplaceAsync(replacement, TestContext.Current.CancellationToken);
        await kit.ProjectAsync();
        var moved = await FactBalancesAsync(kit, january.EventKey);
        await ClassifyAsync(kit, AccountingClassifyAction.Unset, january.EventKey);
        await kit.ProjectAsync();
        var back = await FactBalancesAsync(kit, january.EventKey);

        // Assert: the whole fact, its price correction included, sits on the account its classification names
        Assert.Equal((0L, 0m), moved.GetValueOrDefault("income:sales"));
        Assert.Equal((-100_000_000L, -45m), moved["income:consulting"]);
        Assert.Equal((-100_000_000L, -45m), back["income:sales"]);
        Assert.Equal((0L, 0m), back.GetValueOrDefault("income:consulting"));
        Assert.Equal(-5m, back["assets:cost-basis"].Fiat);
        await AssertClosesIntactAsync(kit);
        var incremental = await kit.SnapshotFinancialAsync();
        await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);
        Assert.Equal(incremental, await kit.SnapshotFinancialAsync());
    }

    private static async Task<(AccountingEventModel January, AccountingEventModel February)> ArrangeClosedJanuaryAsync(
        FinancialProjectorTestKit kit)
    {
        await kit.AddPricesAsync(s_prices);
        var january = kit.Invoice(100_000_000, s_t1, label: "consulting");
        var february = kit.Invoice(100_000_000, s_t3, label: "consulting");
        await kit.AddAsync(FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_t0),
                           FinancialProjectorTestKit.Cutover(s_t0), january, february);
        await kit.ProjectAsync();
        await kit.Periods.CloseAsync("2026-01", false, TestContext.Current.CancellationToken);
        var closed = Assert.Single(await EntriesAsync(kit, january.EventKey));
        Assert.Equal("2026-01", closed.ClosedPeriodId);
        Assert.Contains(closed.Postings, p => p.AccountName == "income:sales");
        return (january, february);
    }

    private static AccountingRule Rule(string target) =>
        new(0, 1, null, "^consult", null, null, null, null, null, target, true, s_now);

    private static Task<AccountingClassifyClientResponse> ClassifyAsync(FinancialProjectorTestKit kit,
                                                                       AccountingClassifyAction action, string key,
                                                                       string? account = null) =>
        ClassifyAsync(kit, new AccountingClassifyClientRequest { Action = action, EventKey = key, Account = account });

    private static Task<AccountingClassifyClientResponse> ClassifyAsync(FinancialProjectorTestKit kit,
                                                                       AccountingClassifyClientRequest request) =>
        kit.Classification.HandleAsync(request, TestContext.Current.CancellationToken);

    private static async Task<IReadOnlyList<AccountingEntry>> EntriesAsync(FinancialProjectorTestKit kit, string key) =>
        (await kit.ListEntriesAsync(AccountingBook.Financial)).Where(e => e.EventKey == key)
                                                              .OrderBy(e => e.Adjustment).ToList();

    // The fact's lines summed per account over all its entries
    private static async Task<Dictionary<string, (long Msat, decimal Fiat)>> FactBalancesAsync(
        FinancialProjectorTestKit kit, string key) =>
        (await EntriesAsync(kit, key)).SelectMany(e => e.Postings)
                                      .GroupBy(p => p.AccountName!)
                                      .ToDictionary(g => g.Key, g => (g.Sum(p => p.AmountMsat),
                                                                       g.Sum(p => p.FiatAmount ?? 0m)));

    private static async Task<IReadOnlyList<string>> LotsAsync(FinancialProjectorTestKit kit) =>
        (await kit.ListLotsAsync()).Select(l => $"{l.Lot.Id} {l.Lot.RemainingMsat} {l.Lot.FiatCost} {l.Reliefs.Count}")
                                   .ToList();

    private static (string, long, decimal?)[] Lines(AccountingEntry entry) =>
        entry.Postings.Select(p => (p.AccountName!, p.AmountMsat, p.FiatAmount)).ToArray();

    private static async Task AssertClosesIntactAsync(FinancialProjectorTestKit kit) =>
        Assert.All(await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken),
                   v => Assert.True(v.IsIntact, v.Problem));

    private static AccountingFinancialReportService CreateReports(FinancialProjectorTestKit kit)
    {
        using var scope = kit.CreateScope();
        return new AccountingFinancialReportService(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
                                                    kit.Books,
                                                    NullLogger<AccountingFinancialReportService>.Instance,
                                                    Options.Create(new AccountingOptions
                                                    {
                                                        Profile = AccountingProfile.Financial
                                                    }), kit.Sealer, kit.Projector);
    }

    private static Task<Domain.Accounting.Financial.Reports.AccountingFinancialRegister> ListUnclassifiedAsync(
        AccountingFinancialReportService reports) =>
        reports.GetRegisterAsync(new AccountingEntryQuery(0, 100) { WithFlags = AccountingEntryFlags.Unclassified },
                                 TestContext.Current.CancellationToken);
}