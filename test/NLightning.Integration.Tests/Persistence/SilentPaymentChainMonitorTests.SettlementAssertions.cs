namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Books;
using Domain.Accounting.Services;
using Domain.Accounting.Enums;
using Infrastructure.Repositories.Database.Accounting;

public sealed partial class SilentPaymentChainMonitorTests
{
    private static async Task AssertSimpleSpendSettlementAsync(ChainMonitorHarness harness, int reversalCount, long walletMsat)
    {
        var events = await EventsAsync(harness);
        Assert.Equal(3 + reversalCount, events.Count);
        Assert.Equal(events.Count, events.Select(fact => fact.EventKey).Distinct().Count());
        Assert.Single(events, fact => fact.Kind == AccountingEventKind.WalletReceived);
        var debit = Assert.Single(events, fact => fact.Kind == AccountingEventKind.WalletOutputSpent);
        Assert.Equal(-AmountSat * 1_000, debit.AmountMsat);
        var settlement = Assert.Single(events, fact => fact.Kind == AccountingEventKind.WalletSent);
        Assert.Equal(-(AmountSat - 500) * 1_000, settlement.AmountMsat);
        Assert.Equal(500_000, settlement.FeeMsat);
        Assert.Equal("true", settlement.Details["recovered"]);
        var reversals = events.Where(fact => fact.Kind == AccountingEventKind.Reversal).ToArray();
        Assert.Equal(reversalCount, reversals.Length);
        foreach (var reversal in reversals)
        {
            var original = Assert.Single(events, fact => fact.EventKey == reversal.Details[AccountingConfirmations.ReversesDetail]);
            Assert.Equal(-original.AmountMsat, reversal.AmountMsat);
            Assert.Equal(-original.FeeMsat, reversal.FeeMsat);
        }
        if (reversalCount > 0)
        {
            Assert.Single(reversals, fact => fact.Details[AccountingConfirmations.ReversesDetail] == debit.EventKey);
            Assert.Single(reversals, fact => fact.Details[AccountingConfirmations.ReversesDetail] == settlement.EventKey);
        }
        Assert.Equal(walletMsat, await WalletBalanceAsync(harness));
        await using (var context = harness.Context())
        {
            var balances = await new AccountingBooksDbRepository(context).GetBalancesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, balances.GetValueOrDefault(AccountRole.Clearing));
            Assert.Equal(reversalCount == 0 ? 500_000 : 0, balances.GetValueOrDefault(AccountRole.FeeWithdraw));
            Assert.Equal(reversalCount == 0 ? (AmountSat - 500) * 1_000 : 0, balances.GetValueOrDefault(AccountRole.TransfersOut));
        }
        // A replay cannot create another settlement or another reversal of either accounting leg.
        await harness.DeliverTipAsync();
        Assert.Equal(events.Select(fact => fact.EventKey).Order(), (await EventsAsync(harness)).Select(fact => fact.EventKey).Order());
    }
}