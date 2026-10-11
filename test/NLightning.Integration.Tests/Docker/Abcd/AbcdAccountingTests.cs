using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Abcd;

using Domain.Accounting.Books;
using Domain.Accounting.Financial.Reports;
using Domain.Accounting.Prices;
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// The A3 Docker smoke (NL-676, ACCOUNTING_PLAN A3-T7): Bob and Carol run <c>Accounting:Profile=Financial</c> for the
/// whole ABCD suite (<see cref="AbcdNetwork"/>), so their books hold the opening balances, the channel opens and every
/// payment the suite forwarded before this test. After one more forward, each node's operational books reconcile with
/// the live node without drift, and once fixture prices are imported (the test node keeps
/// <c>Accounting:Prices:Source=None</c>) every posting of the financial book has a fiat value and its balance sheet
/// balances in msat and in fiat.
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class AbcdAccountingTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    : AbcdTestBase(fixture, output)
{
    private const int MaxValuationRounds = 20;
    private static readonly TimeSpan s_reconcileTimeout = TimeSpan.FromSeconds(30);

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_PaymentForwardedByBobAndCarol_When_BooksValued_Then_ReconciledAndFinancialBookBalanced()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        const long amountMsat = 30_000_789;
        var fees = AbcdPathFees.ToDavid(amountMsat);
        var before = await Network.SnapshotAsync(ct);
        var invoice = await LndTestHelpers.AddInvoiceAsync(Network.David, amountMsat, [Network.HintThroughBobAndCarol()],
                                                           ct, "abcd accounting smoke");
        var payment = await Network.PayFromAliceAsync(invoice.PaymentRequest, ct);
        await AssertPaidThroughBobAndCarolAsync(payment, invoice.RHash.ToByteArray(), fees, before, ct);

        foreach (var node in Network.Nodes)
        {
            // Act
            var reconcile = await ReconcileCleanAsync(node, ct);
            var imported = await ValueEveryPostingAsync(node, ct);
            var reports = node.Services.GetRequiredService<IAccountingFinancialReports>();
            var balance = await reports.GetBalanceSheetAsync(null, null, null, cancellationToken: ct);
            var income = await reports.GetIncomeStatementAsync(null, null, null, ct);

            // Assert
            Console.WriteLine($"[{node.Name}] reconcile at seq {reconcile.LedgerSeq}: "
                            + string.Join("; ", reconcile.Lines.Select(l => $"{l.Account} books {l.BooksMsat} node "
                                                                            + $"{l.NodeMsat} outstanding "
                                                                            + $"{l.OutstandingMsat}")));
            Console.WriteLine($"[{node.Name}] {imported} prices imported; financial balance sheet ({balance.Currency}): "
                            + $"assets {balance.TotalAssetsMsat} msat / {balance.TotalAssetsFiat}, liabilities "
                            + $"{balance.TotalLiabilitiesMsat} / {balance.TotalLiabilitiesFiat}, equity "
                            + $"{balance.TotalEquityMsat} / {balance.TotalEquityFiat}, earnings "
                            + $"{balance.RetainedEarningsMsat} / {balance.RetainedEarningsFiat}, unvalued "
                            + $"{balance.UnvaluedPostings}");
            Console.WriteLine($"[{node.Name}] financial income: "
                            + string.Join("; ", income.Income.Select(l => $"{l.Name} {l.AmountMsat} msat / "
                                                                         + $"{l.FiatAmount}"))
                            + " | expenses: "
                            + string.Join("; ", income.Expenses.Select(l => $"{l.Name} {l.AmountMsat} msat / "
                                                                           + $"{l.FiatAmount}")));

            Assert.True(reconcile.IsClean, $"{node.Name}'s operational books drift from the node");
            Assert.NotEmpty(balance.Assets);
            Assert.Equal(0, balance.UnvaluedPostings);
            Assert.True(balance.IsBalanced, $"{node.Name}'s financial book does not balance in msat");
            Assert.True(balance.IsFiatBalanced, $"{node.Name}'s financial book does not balance in fiat");
            Assert.Equal(0, income.UnvaluedPostings);

            // Both forwarded this payment (and the suite's earlier ones) for a fee: routing income in msat and in fiat
            var routing = Assert.Single(income.Income, l => l.Account == AccountRole.Routing);
            var feeMsat = node == Network.Bob ? fees.FeeBobMsat : fees.FeeCarolMsat;
            Assert.True(routing.AmountMsat >= feeMsat,
                        $"{node.Name}'s routing income {routing.AmountMsat} msat is below this forward's {feeMsat}");
            Assert.True(routing.FiatAmount > 0, $"{node.Name}'s routing income has no fiat value");
        }
    }

    /// <summary>
    /// The node's operational books against its live state. Reconcile seals and projects first, so the events of the
    /// forward are in; the poll only covers a background round that holds the books' gate at that moment.
    /// </summary>
    private static async Task<AccountingReconcileResult> ReconcileCleanAsync(NLightningTestNode node,
                                                                             CancellationToken ct)
    {
        var books = node.Services.GetRequiredService<IAccountingBooks>();
        Assert.True(books.IsEnabled, $"{node.Name}'s books are off");
        AccountingReconcileResult? last = null;
        try
        {
            return await Poll.ForAsync(async () =>
            {
                last = await books.ReconcileAsync(ct);
                return last.IsClean ? last : null;
            }, s_reconcileTimeout, $"{node.Name}'s books reconcile without drift", ct, TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException) when (last is not null)
        {
            return last;
        }
    }

    /// <summary>
    /// Imports an hourly price (a fixture series, one hour apart) for every hour that holds an unvalued posting of the
    /// node's financial book, until the back-valuation leaves none. Returns how many prices it imported.
    /// </summary>
    private static async Task<int> ValueEveryPostingAsync(NLightningTestNode node, CancellationToken ct)
    {
        var reports = node.Services.GetRequiredService<IAccountingFinancialReports>();
        var prices = node.Services.GetRequiredService<IAccountingPrices>();
        var imported = 0;
        for (var round = 0; round < MaxValuationRounds; round++)
        {
            var unvalued = await reports.GetUnvaluedAsync(500, ct);
            if (unvalued.Postings.Count == 0)
                return imported;

            var hours = unvalued.Postings
                                .Select(p => TruncateToHour(p.OccurredAt))
                                .Append(TruncateToHour(DateTimeOffset.UtcNow))
                                .Distinct()
                                .Order()
                                .Select(h => new AccountingPricePoint(h, FixturePrice(h)))
                                .ToList();
            var result = await prices.ImportAsync(null, hours, ct);
            imported += result.Added;
            Console.WriteLine($"[{node.Name}] round {round}: {unvalued.Postings.Count} unvalued postings, imported "
                            + $"{result.Added} {result.Currency} prices ({result.AlreadyStored} kept), valued "
                            + $"{result.Valuation?.Valued}");
            await prices.ValueNowAsync(ct);
        }

        var left = await reports.GetUnvaluedAsync(10, ct);
        Assert.Fail($"{node.Name}: {left.Postings.Count} postings still unvalued after {MaxValuationRounds} rounds, "
                  + $"the first at {left.Postings.FirstOrDefault()?.OccurredAt:O}");
        return imported;
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset time)
    {
        var utc = time.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>60,000 plus 125 per hour of the day: different hours carry different prices, so lots relieved later
    /// than they were acquired realize a gain or a loss.</summary>
    private static decimal FixturePrice(DateTimeOffset hour) => 60_000m + 125m * hour.Hour;
}