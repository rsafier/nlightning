namespace NLightning.Domain.Tests.Accounting.Financial.Lots;

using Domain.Accounting.Books;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Financial.Lots;

/// <summary>
/// Each asset account holds its own bucket's cost basis after every entry (NL-749, NL-657), whatever order the wallet's,
/// the clearing account's and the pending account's events come in and when the clearing account does not net to zero.
/// </summary>
/// <remarks>
/// <para>The invariant, per bucket <c>b</c> (an asset account) after every entry: the msat of <c>b</c>'s open lots, plus
/// the debts owed to <c>b</c>, less the debts <c>b</c> owes, equal the account's balance, in msat and in fiat (the lots'
/// and the debts' cost). A bucket that owes holds no lot: only a bucket whose balance went below what it holds (the
/// clearing account spent before the wallet's own event, a sweep received before its resolution, a clearing account left
/// negative by a fee nobody booked) owes, and the lots it would need stay with its lender until msat come back to it, from
/// any bucket. So a bucket that neither owes nor is owed holds exactly its balance in lots, and no lot is ever missing
/// (no shortfall) while the node's lots cover its balance.</para>
/// </remarks>
public class FinancialLotBucketInvariantTests
{
    private const string Usd = "USD";
    private static readonly FinancialChart s_chart = FinancialChart.Default;
    private static readonly AccountRole[] s_assets =
        [AccountRole.Channels, AccountRole.Pending, AccountRole.Wallet, AccountRole.Clearing];

    [Theory]
    [InlineData(AccountingCostBasisMethod.Fifo)]
    [InlineData(AccountingCostBasisMethod.Lifo)]
    [InlineData(AccountingCostBasisMethod.Hifo)]
    public void Given_TheFafo2Shape_When_EveryEntryIsPlanned_Then_EachBucketHoldsItsBalance(
        AccountingCostBasisMethod method)
    {
        // Arrange: FAFO2's feed in small (NL-749): opening balances in the wallet and a channel; a splice-in whose
        // change reaches the wallet before the wallet's spend; a force close whose sweep reaches the wallet before its
        // resolution; an anchor sweep received with nothing booked against the clearing account (NL-611: -514 sat);
        // an anchors HTLC-timeout whose change reaches the wallet before the spend of its 500,000 sat wallet input,
        // whose 268 sat fee is not booked (NL-748); then a payment and an invoice
        var book = new BucketBook(method);

        // Act / Assert: the invariant after every entry
        book.Plan(40_000m, true, (AccountRole.Wallet, 1_000_000_000), (AccountRole.Opening, -1_000_000_000));
        book.Plan(40_000m, true, (AccountRole.Channels, 500_000_000), (AccountRole.Opening, -500_000_000));
        book.Plan(50_000m, false, (AccountRole.Channels, 2_000_000), (AccountRole.Received, -2_000_000));
        book.Plan(60_000m, false, (AccountRole.Wallet, 290_000_000), (AccountRole.Clearing, -290_000_000));
        book.Plan(60_000m, false, (AccountRole.Wallet, -300_000_000), (AccountRole.Clearing, 300_000_000));
        book.Plan(60_000m, false, (AccountRole.Channels, 9_500_000), (AccountRole.FeeSplice, 500_000),
                  (AccountRole.Clearing, -10_000_000));
        book.Plan(70_000m, false, (AccountRole.Channels, -200_000_000), (AccountRole.Pending, 200_000_000));
        book.Plan(70_000m, false, (AccountRole.Wallet, 199_000_000), (AccountRole.Clearing, -199_000_000));
        book.Plan(70_000m, false, (AccountRole.Pending, -200_000_000), (AccountRole.Clearing, 199_000_000),
                  (AccountRole.FeeSweep, 1_000_000));
        book.Plan(80_000m, false, (AccountRole.Wallet, 514_000), (AccountRole.Clearing, -514_000));
        book.Plan(80_000m, false, (AccountRole.Wallet, 499_732_000), (AccountRole.Clearing, -499_732_000));
        book.Plan(80_000m, false, (AccountRole.Wallet, -500_000_000), (AccountRole.Clearing, 500_000_000));
        book.Plan(90_000m, false, (AccountRole.Channels, -10_001_000), (AccountRole.Sent, 10_000_000),
                  (AccountRole.RoutingFees, 1_000));
        book.Plan(90_000m, false, (AccountRole.Channels, 3_000_000), (AccountRole.Received, -3_000_000));

        // The clearing account ends at -246 sat (-514 + 268): it owes the wallet just that, and holds nothing; the
        // wallet holds its balance less those 246 sat; the channels hold their balance; nothing went without a lot
        Assert.Equal(-246_000, book.Balance(AccountRole.Clearing));
        Assert.Equal(0, book.Held(AccountingLotBucket.Clearing));
        Assert.Equal(246_000, book.Owed(AccountingLotBucket.Clearing, AccountingLotBucket.Wallet));
        Assert.Equal(book.Balance(AccountRole.Wallet) - 246_000, book.Held(AccountingLotBucket.Wallet));
        Assert.Equal(book.Balance(AccountRole.Channels), book.Held(AccountingLotBucket.Channels));
        Assert.Equal(0, book.Held(AccountingLotBucket.Pending));
        Assert.Single(book.Pool.Debts);
        Assert.Equal(0, book.Shortfall);
    }

    [Theory]
    [InlineData(AccountingCostBasisMethod.Fifo, 1)]
    [InlineData(AccountingCostBasisMethod.Lifo, 2)]
    [InlineData(AccountingCostBasisMethod.Hifo, 3)]
    [InlineData(AccountingCostBasisMethod.Fifo, 4)]
    [InlineData(AccountingCostBasisMethod.Lifo, 5)]
    [InlineData(AccountingCostBasisMethod.Hifo, 6)]
    public void Given_RandomTransfersInAnyOrder_When_EveryEntryIsPlanned_Then_EachBucketHoldsItsBalance(
        AccountingCostBasisMethod method, int seed)
    {
        // Arrange: income, payments, and on-chain transfers through the clearing account (funding, splices, closes,
        // wallet spends and their change) or through the pending account (force closes and their sweeps), each half in
        // a random order, some with a fee nobody books on the clearing account
        var random = new Random(seed);
        var book = new BucketBook(method);
        book.Plan(40_000m, true, (AccountRole.Wallet, 2_000_000_000), (AccountRole.Opening, -2_000_000_000));
        book.Plan(40_000m, true, (AccountRole.Channels, 1_000_000_000), (AccountRole.Opening, -1_000_000_000));

        // Act / Assert: the invariant after every entry (BucketBook.Plan)
        for (var step = 0; step < 300; step++)
        {
            var price = 20_000m + random.Next(0, 80_000);
            switch (random.Next(0, 5))
            {
                case 0:
                    var income = random.NextInt64(1_000, 50_000_000);
                    var into = random.Next(0, 2) == 0 ? AccountRole.Channels : AccountRole.Wallet;
                    book.Plan(price, false, (into, income),
                              (into == AccountRole.Channels ? AccountRole.Received : AccountRole.TransfersIn, -income));
                    break;
                case 1:
                    var from = random.Next(0, 2) == 0 ? AccountRole.Channels : AccountRole.Wallet;
                    var paid = Math.Min(book.Balance(from) / 4, random.NextInt64(1_000, 50_000_000));
                    if (paid > 0)
                        book.Plan(price, false, (from, -paid),
                                  (from == AccountRole.Channels ? AccountRole.Sent : AccountRole.TransfersOut, paid));
                    break;
                case 2:
                case 3:
                    // Through the clearing account: wallet to channels or back, or the wallet spending into its change
                    var (giver, taker) = random.Next(0, 3) switch
                    {
                        0 => (AccountRole.Wallet, AccountRole.Channels),
                        1 => (AccountRole.Channels, AccountRole.Wallet),
                        _ => (AccountRole.Wallet, AccountRole.Wallet)
                    };
                    var amount = Math.Min(book.Balance(giver) / 3, random.NextInt64(10_000, 400_000_000));
                    if (amount <= 10_000)
                        break;

                    var fee = random.NextInt64(1_000, 10_000);
                    var unbooked = random.Next(0, 4) == 0 ? random.NextInt64(1, 1_000) : 0;
                    (AccountRole, long)[] spend = [(giver, -amount), (AccountRole.Clearing, amount)];
                    (AccountRole, long)[] receive =
                    [
                        (taker, amount - fee - unbooked), (AccountRole.FeeFunding, fee),
                        (AccountRole.Clearing, -(amount - unbooked))
                    ];
                    var receiveFirst = random.Next(0, 2) == 0;
                    book.Plan(price, false, receiveFirst ? receive : spend);
                    book.Plan(price, false, receiveFirst ? spend : receive);
                    break;
                default:
                    // A force close through the pending account, its sweep to the wallet before or after the resolution
                    var closed = Math.Min(book.Balance(AccountRole.Channels) / 3, random.NextInt64(10_000, 300_000_000));
                    if (closed <= 10_000)
                        break;

                    var sweepFee = random.NextInt64(1_000, 10_000);
                    (AccountRole, long)[] resolved =
                    [
                        (AccountRole.Pending, -closed), (AccountRole.Clearing, closed - sweepFee),
                        (AccountRole.FeeSweep, sweepFee)
                    ];
                    (AccountRole, long)[] swept =
                        [(AccountRole.Wallet, closed - sweepFee), (AccountRole.Clearing, -(closed - sweepFee))];
                    var sweptFirst = random.Next(0, 2) == 0;
                    var closeLast = random.Next(0, 3) == 0;
                    if (!closeLast)
                        book.Plan(price, false, (AccountRole.Channels, -closed), (AccountRole.Pending, closed));
                    book.Plan(price, false, sweptFirst ? swept : resolved);
                    book.Plan(price, false, sweptFirst ? resolved : swept);
                    if (closeLast)
                        book.Plan(price, false, (AccountRole.Channels, -closed), (AccountRole.Pending, closed));
                    break;
            }
        }

        Assert.Equal(0, book.Shortfall);
    }

    /// <summary>The asset accounts' balances and the lot pool of a book, entry by entry.</summary>
    private sealed class BucketBook(AccountingCostBasisMethod method)
    {
        private readonly Dictionary<AccountRole, (long Msat, decimal Fiat)> _balances = [];
        private long _nextId = 1;
        private long _seq;
        private DateTimeOffset _at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public FinancialLotPool Pool { get; } = new([], method, Usd);
        public long Shortfall { get; private set; }

        public long Balance(AccountRole role) => _balances.GetValueOrDefault(role).Msat;

        public long Held(AccountingLotBucket bucket) => Pool.HeldMsat(bucket);

        public long Owed(AccountingLotBucket debtor, AccountingLotBucket lender) =>
            Pool.DebtsOf(debtor, lender).Sum(d => d.RemainingMsat);

        /// <summary>Plans one entry at <paramref name="usdPerBtc"/>, applies it and checks the invariant.</summary>
        public void Plan(decimal usdPerBtc, bool opening, params (AccountRole Role, long Msat)[] lines)
        {
            _at = _at.AddHours(1);
            _seq++;
            var price = new AccountingPrice(_seq, Usd, _at, usdPerBtc, AccountingPriceSource.Csv, _at);
            var postings = lines.Select(l => new AccountingPosting(l.Role, l.Msat)
            {
                AccountName = s_chart[FinancialChart.DefaultAccountOf(l.Role)]
            }).ToList();
            var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(postings, price, _at)
            {
                IsOpeningBalance = opening
            }, Pool, s_chart);

            Assert.Equal(0m, plan.FiatSum);
            Shortfall += plan.ShortfallMsat;
            foreach (var posting in plan.Postings.Where(p => FinancialLotRules.IsAsset(p.Account)
                                                          && p.AccountName == s_chart[
                                                                 FinancialChart.DefaultAccountOf(p.Account)]))
            {
                var (msat, fiat) = _balances.GetValueOrDefault(posting.Account);
                _balances[posting.Account] = (msat + posting.AmountMsat, fiat + posting.FiatAmount!.Value);
            }

            foreach (var relief in plan.Reliefs)
                Pool.Relieve(relief.LotId, relief.Msat);
            foreach (var spec in plan.NewLots)
                Pool.Add(new AccountingLot(_nextId++, _at, spec.Origin, _seq, 0, spec.Bucket, spec.ParentLotId,
                                           spec.Msat, spec.Msat, spec.Cost, spec.Currency, spec.PriceId,
                                           spec.BasisEstimated, null)
                {
                    HeldSince = spec.HeldSince,
                    Lender = spec.Lender
                });

            AssertInvariant();
        }

        private void AssertInvariant()
        {
            foreach (var role in s_assets)
            {
                var bucket = (AccountingLotBucket)(int)role;
                var lots = Pool.LotsOf(bucket);
                var owedTo = Pool.Debts.Where(d => d.Lender == bucket).ToList();
                var owedBy = Pool.Debts.Where(d => d.Bucket == bucket).ToList();
                var (msat, fiat) = _balances.GetValueOrDefault(role);
                Assert.Equal(msat, lots.Sum(l => l.RemainingMsat) + owedTo.Sum(d => d.RemainingMsat)
                                 - owedBy.Sum(d => d.RemainingMsat));
                Assert.Equal(fiat, lots.Sum(Cost) + owedTo.Sum(Cost) - owedBy.Sum(Cost));
                if (owedBy.Count > 0)
                    Assert.Empty(lots);
            }

            Assert.Empty(Pool.LotsOf(null));
            Assert.Equal(_balances.Values.Sum(b => b.Msat), Pool.RemainingMsat);
        }

        private static decimal Cost(AccountingLot lot) => FinancialLotPool.CostOf(lot, lot.RemainingMsat, Usd)!.Value;
    }
}