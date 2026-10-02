namespace NLightning.Domain.Tests.Accounting.Financial.Lots;

using Domain.Accounting.Books;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Financial.Lots;

/// <summary>
/// The cost-basis rules of one financial entry (NL-602 A3-T4, NL-657, NL-674, NL-675, D-A9, D-A12), every amount computed
/// by hand: lots kept per bucket, a transfer that moves its lots at cost, the disposals and gains, a loss at zero
/// proceeds, a rebalance where only the fee disposes (paid first or received first), a withdrawal classified to the
/// operator's cold storage and its deposit back, the clearing account spent before the wallet's event (a debt settled
/// later), an unvalued entry and an unvalued lot, the opening balance and the imported lots, the corrections.
/// </summary>
/// <remarks>Prices are USD per BTC: at 70,000, 1e8 msat (100,000 sat) is worth 70 USD. The three lots: L1 Jan 1, 1e8
/// msat for 40; L2 Feb 1, 1e8 for 60; L3 Mar 1, 1e8 for 50.</remarks>
public class FinancialEntryPlannerTests
{
    private const string Usd = "USD";
    private static readonly FinancialChart s_chart = FinancialChart.Default;
    private static readonly DateTimeOffset s_april = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly AccountingPrice s_price70K = new(7, Usd, s_april, 70_000m, AccountingPriceSource.Csv,
                                                             s_april);
    private static readonly AccountingPrice s_price60K = new(6, Usd, s_april, 60_000m, AccountingPriceSource.Csv,
                                                             s_april);

    public static TheoryData<AccountingCostBasisMethod, decimal, decimal> PaymentCases => new()
    {
        // 1.5e8 msat sent at 70,000 (proceeds 105): FIFO cost 70 (gain 35), LIFO 80 (25), HIFO 85 (20)
        { AccountingCostBasisMethod.Fifo, 70m, 35m },
        { AccountingCostBasisMethod.Lifo, 80m, 25m },
        { AccountingCostBasisMethod.Hifo, 85m, 20m }
    };

    [Theory]
    [MemberData(nameof(PaymentCases))]
    public void Given_APayment_When_Planned_Then_TheChannelsLineCarriesTheLotsCostAndTheGainIsTheHandComputedOne(
        AccountingCostBasisMethod method, decimal cost, decimal gain)
    {
        // Arrange
        var pool = ThreeLots(method, AccountingLotBucket.Channels);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -150_000_000),
            Line(AccountRole.Sent, "expenses:payments", 150_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: channels at the lots' cost, the payment at market (105), the realized gain on its own line
        Assert.Equal(AccountingEntryFlags.None, plan.Flags);
        Assert.Equal(gain, plan.RealizedGain);
        Assert.Equal(0m, plan.FiatSum);
        Assert.Equal([-cost, 105m, -gain], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal(["assets:lightning:channels", "expenses:payments", "income:gains:realized"],
                     plan.Postings.Select(p => p.AccountName!).ToArray());
        Assert.All(plan.Reliefs, r => Assert.Equal(AccountingLotReliefKind.Disposal, r.Kind));
        Assert.Equal(cost, plan.Reliefs.Sum(r => r.Cost));
        Assert.Equal([70m, 35m], plan.Reliefs.Select(r => r.Proceeds!.Value).ToArray());
        Assert.Empty(plan.NewLots);
    }

    [Fact]
    public void Given_APaymentFromTheChannels_When_TheWalletHoldsCheaperLots_Then_OnlyTheChannelsLotsAreRelieved()
    {
        // Arrange - NL-657: L1 (the cheapest, oldest) is in the wallet, L2 and L3 in the channels
        var pool = new FinancialLotPool([
            Lot(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 40m, AccountingLotBucket.Wallet),
            Lot(2, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), 60m, AccountingLotBucket.Channels),
            Lot(3, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), 50m, AccountingLotBucket.Channels)
        ], AccountingCostBasisMethod.Fifo, Usd);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -150_000_000),
            Line(AccountRole.Sent, "expenses:payments", 150_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: FIFO within the channels: L2 1e8 (60) and L3 5e7 (25): cost 85, gain 20
        Assert.Equal([(2L, 100_000_000L), (3L, 50_000_000L)], plan.Reliefs.Select(r => (r.LotId, r.Msat)).ToArray());
        Assert.Equal(20m, plan.RealizedGain);
    }

    [Fact]
    public void Given_AnInvoiceSettled_When_Planned_Then_ALotOpensInTheChannelsAtFairValueAndNothingIsRelieved()
    {
        // Arrange
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo, AccountingLotBucket.Channels);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", 200_000_000),
            Line(AccountRole.Received, "income:sales", -200_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: 2e8 msat at 70,000 = 140
        Assert.Empty(plan.Reliefs);
        Assert.Equal(new FinancialLotSpec(200_000_000, 140m, Usd, 7, AccountingLotOrigin.Acquisition, false)
        {
            Bucket = AccountingLotBucket.Channels
        }, Assert.Single(plan.NewLots));
        Assert.Equal([140m, -140m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Null(plan.RealizedGain);
    }

    [Fact]
    public void Given_AChannelOpen_When_Planned_Then_TheFeeDisposesAndTheRestMovesToTheChannelsAtCost()
    {
        // Arrange: Dr Channels 1e8; Dr FeeFunding 1e6; Cr Clearing 1.01e8, the lots in the clearing account
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo, AccountingLotBucket.Clearing);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", 100_000_000),
            Line(AccountRole.FeeFunding, "expenses:fees:funding", 1_000_000),
            Line(AccountRole.Clearing, "assets:onchain:clearing", -101_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: the fee 1e6 relieves L1 (cost 0.4, proceeds 0.7: gain 0.3); the channel takes L1's other 9.9e7
        // (39.6) and 1e6 of L2 (0.6) at cost: channels 40.2, clearing -40.6
        Assert.Equal([(1L, 1_000_000L, AccountingLotReliefKind.Disposal, (decimal?)0.4m),
                      (1L, 99_000_000L, AccountingLotReliefKind.Move, 39.6m),
                      (2L, 1_000_000L, AccountingLotReliefKind.Move, 0.6m)],
                     plan.Reliefs.Select(r => (r.LotId, r.Msat, r.Kind, r.Cost)).ToArray());
        Assert.Equal(0.3m, plan.RealizedGain);
        Assert.Equal([40.2m, 0.7m, -40.6m, -0.3m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal([(1L, 99_000_000L, (decimal?)39.6m, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                      (2L, 1_000_000L, 0.6m, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero))],
                     plan.Moved.Select(l => (l.ParentLotId!.Value, l.Msat, l.Cost, l.HeldSince!.Value)).ToArray());
        Assert.All(plan.Moved, l => Assert.Equal(AccountingLotBucket.Channels, l.Bucket));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_AWalletTransfer_When_Planned_Then_ItsLotsMoveAtCostWithOrWithoutAPrice(bool priced)
    {
        // Arrange: Cr Wallet 2.5e8; Dr Clearing 2.5e8
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo, AccountingLotBucket.Wallet);
        var lines = new[]
        {
            Line(AccountRole.Wallet, "assets:onchain:wallet", -250_000_000),
            Line(AccountRole.Clearing, "assets:onchain:clearing", 250_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, priced ? s_price70K : null, s_april),
                                              pool, s_chart);

        // Assert: L1 40 + L2 60 + L3 half 25; a transfer needs no price
        Assert.Equal(AccountingEntryFlags.None, plan.Flags);
        Assert.Equal([-125m, 125m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.All(plan.Reliefs, r => Assert.Equal(AccountingLotReliefKind.Move, r.Kind));
        Assert.Equal(3, plan.Moved.Count());
        Assert.Null(plan.RealizedGain);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_ALossOnChain_When_Planned_Then_ItDisposesAtZeroProceeds(bool priced)
    {
        // Arrange: a force close loses 1e6 msat (Dr LossOnchain), L1's cost of it 0.4
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo, AccountingLotBucket.Channels);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -1_000_000),
            Line(AccountRole.LossOnchain, "expenses:losses", 1_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, priced ? s_price70K : null, s_april),
                                              pool, s_chart);

        // Assert: channels -0.4 at cost, the loss 0 (zero proceeds), the realized loss +0.4
        Assert.Equal(AccountingEntryFlags.None, plan.Flags);
        Assert.Equal(-0.4m, plan.RealizedGain);
        Assert.Equal((decimal?)0m, Assert.Single(plan.Reliefs).Proceeds);
        Assert.Equal([-0.4m, 0m, 0.4m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal("expenses:losses:realized", plan.Postings[^1].AccountName);
    }

    [Fact]
    public void Given_ARebalancePaidFirst_When_BothHalvesArePlanned_Then_OnlyTheRouteFeeDisposesAndTheLotsComeBack()
    {
        // Arrange: the paying side, split by the classification (NL-609): Cr Channels 1.001e8, Dr Rebalance 1e8, Dr
        // RoutingFees 1e5
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo, AccountingLotBucket.Channels);
        var paying = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -100_100_000),
            Line(AccountRole.Rebalance, "equity:transfers:rebalance", 100_000_000),
            Line(AccountRole.RoutingFees, "expenses:fees:routing", 100_000)
        };

        // Act
        var paid = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(paying, s_price70K, s_april), pool, s_chart);
        Apply(pool, paid, 20);
        var received = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(
                                                      [
                                                          Line(AccountRole.Channels, "assets:lightning:channels",
                                                               100_000_000),
                                                          Line(AccountRole.Rebalance, "equity:transfers:rebalance",
                                                               -100_000_000)
                                                      ], s_price70K, s_april), pool, s_chart);

        // Assert: the fee 1e5 relieves L1 (0.04; proceeds 0.07, gain 0.03); 1e8 moves into the rebalance bucket at
        // cost (L1 9.99e7 for 39.96, L2 1e5 for 0.06: 40.02) and the receiving half moves it back at the same cost
        var fee = Assert.Single(paid.Disposals);
        Assert.Equal((100_000L, (decimal?)0.04m), (fee.Msat, fee.Cost));
        Assert.Equal(0.03m, paid.RealizedGain);
        Assert.Equal([-40.06m, 40.02m, 0.07m, -0.03m], paid.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.All(paid.Moved, l => Assert.Equal(AccountingLotBucket.Rebalance, l.Bucket));
        Assert.Equal([40.02m, -40.02m], received.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Empty(received.Disposals);
        Assert.All(received.Moved, l => Assert.Equal(AccountingLotBucket.Channels, l.Bucket));
        Assert.Equal([new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                      new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)],
                     received.Moved.Select(l => l.HeldSince!.Value).ToArray());
    }

    [Fact]
    public void Given_ARebalanceReceivedFirst_When_BothHalvesArePlanned_Then_ADebtIsSettledAndOnlyTheFeeDisposes()
    {
        // Arrange: the invoice of our own rebalance settles before the payment succeeds
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo, AccountingLotBucket.Channels);
        var receiving = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", 100_000_000),
            Line(AccountRole.Rebalance, "equity:transfers:rebalance", -100_000_000)
        };

        // Act
        var received = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(receiving, s_price70K, s_april), pool,
                                                  s_chart);
        Apply(pool, received, 20);
        var paid = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(
                                                  [
                                                      Line(AccountRole.Channels, "assets:lightning:channels",
                                                           -100_100_000),
                                                      Line(AccountRole.Rebalance, "equity:transfers:rebalance",
                                                           100_000_000),
                                                      Line(AccountRole.RoutingFees, "expenses:fees:routing", 100_000)
                                                  ], s_price70K, s_april), pool, s_chart);

        // Assert: the receiving half moves nothing: the rebalance bucket owes the channels 1e8, valued at the
        // channels' lots (L1, 40); the paying half settles that debt at 40 and relieves only the fee (L1 1e5, 0.04)
        Assert.Empty(received.Reliefs);
        var debt = Assert.Single(received.NewLots);
        Assert.Equal((AccountingLotOrigin.Debt, AccountingLotBucket.Rebalance, AccountingLotBucket.Channels,
                      100_000_000L, (decimal?)40m),
                     (debt.Origin, debt.Bucket!.Value, debt.Lender!.Value, debt.Msat, debt.Cost));
        Assert.Equal([40m, -40m], received.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal([(20L, AccountingLotReliefKind.Settlement, 100_000_000L), (1L, AccountingLotReliefKind.Disposal,
                      100_000L)], paid.Reliefs.Select(r => (r.LotId, r.Kind, r.Msat)).OrderBy(r => r.Kind == AccountingLotReliefKind.Disposal).ToArray());
        Assert.Equal([-40.04m, 40m, 0.07m, -0.03m], paid.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Empty(paid.NewLots);
    }

    [Fact]
    public void Given_AWithdrawalClassifiedToColdStorage_When_Planned_Then_ItsLotsAreHeldOutsideAtCostAndOnlyTheFeeDisposes()
    {
        // Arrange - NL-674: Dr TransfersOut 1e8 (an override to equity:transfers:cold-storage); Dr FeeWithdraw 1e6;
        // Cr Clearing 1.01e8
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo, AccountingLotBucket.Clearing);
        var lines = new[]
        {
            Line(AccountRole.TransfersOut, "equity:transfers:cold-storage", 100_000_000),
            Line(AccountRole.FeeWithdraw, "expenses:fees:withdraw", 1_000_000),
            Line(AccountRole.Clearing, "assets:onchain:clearing", -101_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: the fee relieves L1 1e6 (0.4, proceeds 0.7: gain 0.3); the 1e8 moves to the held-outside bucket at
        // cost (L1 9.9e7 for 39.6, L2 1e6 for 0.6), no gain on it
        Assert.Equal(FinancialLineKind.HeldOutside, FinancialLotRules.KindOf(lines[0], s_chart));
        Assert.Equal(0.3m, plan.RealizedGain);
        Assert.Equal([40.2m, 0.7m, -40.6m, -0.3m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal(1_000_000, plan.Disposals.Sum(r => r.Msat));
        Assert.All(plan.Moved, l => Assert.Equal(AccountingLotBucket.HeldOutside, l.Bucket));
        Assert.Equal(100_000_000, plan.Moved.Sum(l => l.Msat));
    }

    [Fact]
    public void Given_AnUnclassifiedWithdrawal_When_Planned_Then_ItIsADisposalAsBefore()
    {
        // Arrange: the default equity:transfers:out says nothing of where the sats went
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo, AccountingLotBucket.Clearing);
        var lines = new[]
        {
            Line(AccountRole.TransfersOut, "equity:transfers:out", 100_000_000),
            Line(AccountRole.FeeWithdraw, "expenses:fees:withdraw", 1_000_000),
            Line(AccountRole.Clearing, "assets:onchain:clearing", -101_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: the withdrawal relieves L1 (40, proceeds 70: 30), the fee L2 1e6 (0.6, proceeds 0.7: 0.1)
        Assert.Equal(FinancialLineKind.Disposal, FinancialLotRules.KindOf(lines[0], s_chart));
        Assert.Equal(30.1m, plan.RealizedGain);
        Assert.Equal([70m, 0.7m, -40.6m, -30.1m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Empty(plan.NewLots);
    }

    [Fact]
    public void Given_ADepositClassifiedAsATransferBack_When_Planned_Then_TheHeldLotsReturnAtTheirCostAndTimes()
    {
        // Arrange - NL-674: the two parts the withdrawal moved out (L1's 9.9e7 for 39.6, L2's 1e6 for 0.6)
        var pool = new FinancialLotPool([
            Held(11, 1, 99_000_000, 39.6m, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Held(12, 2, 1_000_000, 0.6m, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero))
        ], AccountingCostBasisMethod.Fifo, Usd);
        var lines = new[]
        {
            Line(AccountRole.Wallet, "assets:onchain:wallet", 100_000_000),
            Line(AccountRole.TransfersIn, "equity:transfers:cold-storage", -100_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: both parts move into the wallet at 40.2 with their original acquisition times; nothing is acquired
        Assert.Equal([40.2m, -40.2m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Empty(plan.Acquired);
        Assert.Equal([(11L, 99_000_000L, (decimal?)39.6m, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                      (12L, 1_000_000L, 0.6m, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero))],
                     plan.Moved.Select(l => (l.ParentLotId!.Value, l.Msat, l.Cost, l.HeldSince!.Value)).ToArray());
        Assert.All(plan.Moved, l => Assert.Equal(AccountingLotBucket.Wallet, l.Bucket));
        Assert.Null(plan.RealizedGain);
    }

    [Fact]
    public void Given_ADepositBackBeyondWhatIsHeldOutside_When_Planned_Then_TheRestIsAcquiredAtMarket()
    {
        // Arrange
        var pool = new FinancialLotPool([
            Held(11, 1, 100_000_000, 40m, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
        ], AccountingCostBasisMethod.Fifo, Usd);
        var lines = new[]
        {
            Line(AccountRole.Wallet, "assets:onchain:wallet", 150_000_000),
            Line(AccountRole.TransfersIn, "equity:transfers:cold-storage", -150_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: 1e8 back at 40, 5e7 acquired at 35
        Assert.Equal([75m, -75m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal(new FinancialLotSpec(50_000_000, 35m, Usd, 7, AccountingLotOrigin.Acquisition, false)
        {
            Bucket = AccountingLotBucket.Wallet
        }, Assert.Single(plan.Acquired));
        Assert.Contains("beyond what is held outside", plan.Note);
    }

    [Fact]
    public void Given_ARebalanceLineReclassifiedToAnExpense_When_Planned_Then_ItDisposes()
    {
        // Arrange - NL-674's converse: a self-payment the operator calls marketing
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo, AccountingLotBucket.Channels);
        var paying = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -100_000_000),
            Line(AccountRole.Rebalance, "expenses:marketing", 100_000_000)
        };
        var receiving = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", 100_000_000),
            Line(AccountRole.Rebalance, "income:sales", -100_000_000)
        };

        // Act
        var paid = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(paying, s_price70K, s_april), pool, s_chart);
        var received = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(receiving, s_price70K, s_april), pool,
                                                  s_chart);

        // Assert: the paying half disposes of L1 (40) at 70; the receiving half acquires a lot at 70
        Assert.Equal(30m, paid.RealizedGain);
        Assert.Equal([-40m, 70m, -30m], paid.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal(new FinancialLotSpec(100_000_000, 70m, Usd, 7, AccountingLotOrigin.Acquisition, false)
        {
            Bucket = AccountingLotBucket.Channels
        }, Assert.Single(received.NewLots));
    }

    [Fact]
    public void Given_TheClearingAccountSpentBeforeTheWalletsEvent_When_ThenSpent_Then_TheDebtIsSettledAndTheLotsLandRight()
    {
        // Arrange: the wallet holds W (1e9 for 500); a channel funding of 8.99e8 + fee 1e6 comes before the wallet's
        // spend of W, at 60,000
        var pool = new FinancialLotPool([
            Lot(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 500m, AccountingLotBucket.Wallet) with
            {
                OriginalMsat = 1_000_000_000, RemainingMsat = 1_000_000_000
            }
        ], AccountingCostBasisMethod.Fifo, Usd);
        var funding = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", 899_000_000),
            Line(AccountRole.FeeFunding, "expenses:fees:funding", 1_000_000),
            Line(AccountRole.Clearing, "assets:onchain:clearing", -900_000_000)
        };

        // Act
        var funded = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(funding, s_price60K, s_april), pool,
                                                s_chart);
        Apply(pool, funded, 20);
        var spent = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(
                                                   [
                                                       Line(AccountRole.Wallet, "assets:onchain:wallet",
                                                            -1_000_000_000),
                                                       Line(AccountRole.Clearing, "assets:onchain:clearing",
                                                            1_000_000_000)
                                                   ], s_price60K, s_april), pool, s_chart);

        // Assert: the funding borrows W: the fee disposes of 1e6 (0.5, proceeds 0.6), 8.99e8 move to the channels
        // (449.5) and the clearing account owes the wallet 9e8 (450)
        Assert.Equal([449.5m, 0.6m, -450m, -0.1m], funded.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        var debt = Assert.Single(funded.Debts);
        Assert.Equal((AccountingLotBucket.Clearing, AccountingLotBucket.Wallet, 900_000_000L, (decimal?)450m),
                     (debt.Bucket!.Value, debt.Lender!.Value, debt.Msat, debt.Cost));
        Assert.Equal((AccountingLotBucket.Channels, 899_000_000L, (decimal?)449.5m),
                     (funded.Moved.Single().Bucket!.Value, funded.Moved.Single().Msat, funded.Moved.Single().Cost));

        // The wallet's spend settles the debt (no lot moves) and moves only W's last 1e8 (50) into the clearing
        Assert.Equal([(AccountingLotReliefKind.Settlement, 900_000_000L, (decimal?)450m),
                      (AccountingLotReliefKind.Move, 100_000_000L, 50m)],
                     spent.Reliefs.Select(r => (r.Kind, r.Msat, r.Cost)).ToArray());
        Assert.Equal([-500m, 500m], spent.Postings.Select(p => p.FiatAmount!.Value).ToArray());
    }

    [Fact]
    public void Given_TheClearingAccountSpentFirstOnANodeWithAChannel_When_ThenSpent_Then_TheWalletLotsReachTheChannels()
    {
        // Arrange (NL-739): as above, but the node already has a channel (C, 1e9 for 700 in the channels bucket). The
        // short clearing account claimed C (a debt to the channels nobody pays back), so W's lots were stranded in the
        // clearing bucket and the channels' later disposals relieved C before the older W
        var pool = new FinancialLotPool([
            Lot(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 500m, AccountingLotBucket.Wallet) with
            {
                OriginalMsat = 1_000_000_000, RemainingMsat = 1_000_000_000
            },
            Lot(2, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), 700m, AccountingLotBucket.Channels) with
            {
                OriginalMsat = 1_000_000_000, RemainingMsat = 1_000_000_000
            }
        ], AccountingCostBasisMethod.Fifo, Usd);
        var funding = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", 899_000_000),
            Line(AccountRole.FeeFunding, "expenses:fees:funding", 1_000_000),
            Line(AccountRole.Clearing, "assets:onchain:clearing", -900_000_000)
        };

        // Act
        var funded = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(funding, s_price60K, s_april), pool,
                                                s_chart);
        Apply(pool, funded, 20);
        var spent = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(
                                                   [
                                                       Line(AccountRole.Wallet, "assets:onchain:wallet",
                                                            -1_000_000_000),
                                                       Line(AccountRole.Clearing, "assets:onchain:clearing",
                                                            1_000_000_000)
                                                   ], s_price60K, s_april), pool, s_chart);

        // Assert: the funding borrows W from the wallet (C untouched), exactly as on a node without a channel
        Assert.Equal([449.5m, 0.6m, -450m, -0.1m], funded.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        var debt = Assert.Single(funded.Debts);
        Assert.Equal((AccountingLotBucket.Clearing, AccountingLotBucket.Wallet, 900_000_000L, (decimal?)450m),
                     (debt.Bucket!.Value, debt.Lender!.Value, debt.Msat, debt.Cost));
        var moved = Assert.Single(funded.Moved);
        Assert.Equal((AccountingLotBucket.Channels, 899_000_000L, (decimal?)449.5m, (long?)1),
                     (moved.Bucket!.Value, moved.Msat, moved.Cost, moved.ParentLotId));
        Assert.Equal([(AccountingLotReliefKind.Settlement, 900_000_000L, (decimal?)450m),
                      (AccountingLotReliefKind.Move, 100_000_000L, 50m)],
                     spent.Reliefs.Select(r => (r.Kind, r.Msat, r.Cost)).ToArray());
    }

    [Fact]
    public void Given_NoUsablePrice_When_APaymentIsPlanned_Then_TheEntryIsUnvaluedAndWaitsForItsPrice()
    {
        // Arrange
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo, AccountingLotBucket.Channels);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -150_000_000),
            Line(AccountRole.Sent, "expenses:payments", 150_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, null, s_april), pool, s_chart);

        // Assert: no value anywhere, the lots still relieved (by msat), the gain pending
        Assert.Equal(AccountingEntryFlags.Unvalued | AccountingEntryFlags.PendingValuation
                   | AccountingEntryFlags.GainPending, plan.Flags);
        Assert.All(plan.Postings, p => Assert.Null(p.FiatAmount));
        Assert.Equal(2, plan.Postings.Count);
        Assert.Equal(150_000_000, plan.Reliefs.Sum(r => r.Msat));
        Assert.All(plan.Reliefs, r => Assert.Null(r.Proceeds));
        Assert.Null(plan.RealizedGain);
    }

    [Fact]
    public void Given_ALotWithoutCost_When_ItIsRelieved_Then_TheGainIsPendingNotZero()
    {
        // Arrange
        var unvalued = new AccountingLot(4, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                                         AccountingLotOrigin.Acquisition, 4, 0, AccountingLotBucket.Channels, null,
                                         100_000_000, 100_000_000, null, null, null, false, null);
        var pool = new FinancialLotPool([unvalued], AccountingCostBasisMethod.Fifo, Usd);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -50_000_000),
            Line(AccountRole.Sent, "expenses:payments", 50_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: the lines at market, no gain line
        Assert.Equal(AccountingEntryFlags.GainPending, plan.Flags);
        Assert.Null(plan.RealizedGain);
        Assert.DoesNotContain(plan.Postings, p => p.AccountName == "income:gains:realized");
        Assert.Equal([-35m, 35m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal((decimal?)35m, Assert.Single(plan.Reliefs).Proceeds);
        Assert.Null(plan.Reliefs[0].Cost);
        Assert.Equal(0m, plan.FiatSum);
    }

    [Fact]
    public void Given_AnOpeningBalance_When_Planned_Then_ItsLotHasAnEstimatedBasisOrTakesTheImportedLots()
    {
        // Arrange: an imported lot of 1e8 for 30 (no bucket yet)
        var imported = new AccountingLot(5, new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
                                         AccountingLotOrigin.Import, null, 0, null, null, 100_000_000, 100_000_000,
                                         30m, Usd, null, false, null);
        var lines = new[]
        {
            Line(AccountRole.Wallet, "assets:onchain:wallet", 100_000_000),
            Line(AccountRole.Opening, "equity:opening-balances", -100_000_000)
        };

        // Act
        var estimated = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april)
        {
            IsOpeningBalance = true
        }, new FinancialLotPool([], AccountingCostBasisMethod.Fifo, Usd), s_chart);
        var fromImport = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april)
        {
            IsOpeningBalance = true,
            OpeningLotsImported = true
        }, new FinancialLotPool([imported], AccountingCostBasisMethod.Fifo, Usd), s_chart);

        // Assert: the estimated lot at the cutover price; after an import the wallet takes the imported lot at its cost
        Assert.Equal(new FinancialLotSpec(100_000_000, 70m, Usd, 7, AccountingLotOrigin.Opening, true)
        {
            Bucket = AccountingLotBucket.Wallet
        }, Assert.Single(estimated.NewLots));
        Assert.Equal([70m, -70m], estimated.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        var moved = Assert.Single(fromImport.NewLots);
        Assert.Equal((5L, AccountingLotOrigin.Import, AccountingLotBucket.Wallet, (decimal?)30m),
                     (moved.ParentLotId!.Value, moved.Origin, moved.Bucket!.Value, moved.Cost));
        Assert.Equal([30m, -30m], fromImport.Postings.Select(p => p.FiatAmount!.Value).ToArray());
    }

    [Fact]
    public void Given_ACancelledFact_When_Planned_Then_ItIsValuedButTouchesNoLot()
    {
        // Arrange: a deposit that a reorg reverses in the open period
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo, AccountingLotBucket.Wallet);
        var lines = new[]
        {
            Line(AccountRole.Wallet, "assets:onchain:wallet", 100_000_000),
            Line(AccountRole.TransfersIn, "equity:transfers:in", -100_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april)
        {
            Cancelled = true
        }, pool, s_chart);

        // Assert
        Assert.Empty(plan.NewLots);
        Assert.Empty(plan.Reliefs);
        Assert.Equal([70m, -70m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
    }

    [Fact]
    public void Given_NoLotLeft_When_APaymentIsPlanned_Then_TheShortfallHasNoGainAndIsNoted()
    {
        // Arrange
        var pool = new FinancialLotPool([], AccountingCostBasisMethod.Fifo, Usd);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -1_000_000),
            Line(AccountRole.Sent, "expenses:payments", 1_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert
        Assert.Equal(1_000_000, plan.ShortfallMsat);
        Assert.Equal(0m, plan.RealizedGain);
        Assert.Contains("lot shortfall", plan.Note);
        Assert.Equal([-0.7m, 0.7m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
    }

    [Theory]
    [InlineData(true, 2L, 60)]
    [InlineData(false, 1L, 40)]
    public void Given_TheReversalOfAClosedDeposit_When_Planned_Then_ItTakesTheDepositsLotBackAtCost(
        bool depositLotOpen, long lotId, int cost)
    {
        // Arrange - NL-675: the negation of a closed period's deposit (ledger sequence 2, its lot L2 for 60) at its
        // original value; FIFO would take L1 (40) and realize 20
        var lots = new List<AccountingLot>
        {
            Lot(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 40m, AccountingLotBucket.Wallet)
        };
        if (depositLotOpen)
            lots.Add(Lot(2, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), 60m, AccountingLotBucket.Wallet));
        var pool = new FinancialLotPool(lots, AccountingCostBasisMethod.Fifo, Usd);
        var lines = new[]
        {
            Line(AccountRole.Wallet, "assets:onchain:wallet", -100_000_000) with { FiatAmount = -60m, FiatCurrency = Usd },
            Line(AccountRole.TransfersIn, "equity:transfers:in", 100_000_000) with { FiatAmount = 60m, FiatCurrency = Usd }
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, null, s_april)
        {
            CorrectionOf = 2
        }, pool, s_chart);

        // Assert: the deposit's own lot when it is still there, else the method's; at cost, nothing realized
        var relief = Assert.Single(plan.Reliefs);
        Assert.Equal((lotId, (decimal?)cost, (decimal?)cost), (relief.LotId, relief.Cost, relief.Proceeds));
        Assert.Equal([-(decimal)cost, cost], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal(0m, plan.RealizedGain);
    }

    [Fact]
    public void Given_ADebitOfTheOpeningBalances_When_Planned_Then_ItRelievesTheOpeningLotAtItsCostAndRealizesNothing()
    {
        // Arrange - NL-673: the reversal of a wallet receive from before the feed; LIFO would take the newest lot (L3)
        var opening = Lot(4, new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero), 30m,
                          AccountingLotBucket.Wallet) with
        {
            Origin = AccountingLotOrigin.Opening,
            BasisEstimated = true
        };
        var pool = new FinancialLotPool([
            .. ThreeLots(AccountingCostBasisMethod.Lifo, AccountingLotBucket.Wallet).OpenLots, opening
        ], AccountingCostBasisMethod.Lifo, Usd);
        var lines = new[]
        {
            Line(AccountRole.Wallet, "assets:onchain:wallet", -150_000_000),
            Line(AccountRole.Opening, "equity:opening-balances", 150_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: the opening lot (1e8 for 30) first, then L3 (5e7 of 1e8 for 50: 25), each at its cost; both lines
        // carry the cost relieved (55); no gain
        Assert.Equal([(4L, 100_000_000L, (decimal?)30m, (decimal?)30m), (3L, 50_000_000L, 25m, 25m)],
                     plan.Reliefs.Select(r => (r.LotId, r.Msat, r.Cost, r.Proceeds)).ToArray());
        Assert.Equal([-55m, 55m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal(0m, plan.RealizedGain);
        Assert.Equal(0m, plan.FiatSum);
    }

    [Fact]
    public void Given_LotsOfTheNodeWidePool_When_AChannelPays_Then_TheyAreTakenOverWithoutADebt()
    {
        // Arrange: a book projected before lots were kept per bucket (no bucket)
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo, null);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -150_000_000),
            Line(AccountRole.Sent, "expenses:payments", 150_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: FIFO over the pool as before (cost 70, gain 35), no debt
        Assert.Equal(35m, plan.RealizedGain);
        Assert.Empty(plan.NewLots);
    }

    private static void Apply(FinancialLotPool pool, FinancialEntryPlan plan, long firstId)
    {
        foreach (var relief in plan.Reliefs)
            pool.Relieve(relief.LotId, relief.Msat);

        var id = firstId;
        foreach (var spec in plan.NewLots)
            pool.Add(new AccountingLot(id++, s_april, spec.Origin, 99, 0, spec.Bucket, spec.ParentLotId, spec.Msat,
                                       spec.Msat, spec.Cost, spec.Currency, spec.PriceId, spec.BasisEstimated, null)
            {
                HeldSince = spec.HeldSince,
                Lender = spec.Lender
            });
    }

    private static FinancialLotPool ThreeLots(AccountingCostBasisMethod method, AccountingLotBucket? bucket) =>
        new([
            Lot(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 40m, bucket),
            Lot(2, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), 60m, bucket),
            Lot(3, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), 50m, bucket)
        ], method, Usd);

    private static AccountingLot Lot(long id, DateTimeOffset at, decimal cost, AccountingLotBucket? bucket) =>
        new(id, at, AccountingLotOrigin.Acquisition, id, 0, bucket, null, 100_000_000, 100_000_000, cost, Usd, null,
            false, null);

    // A part held outside the node, moved there on Mar 15 from lot parent acquired at heldSince
    private static AccountingLot Held(long id, long parent, long msat, decimal cost, DateTimeOffset heldSince) =>
        new AccountingLot(id, new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero), AccountingLotOrigin.Acquisition,
                          10, 0, AccountingLotBucket.HeldOutside, parent, msat, msat, cost, Usd, null, false, null)
        {
            HeldSince = heldSince
        };

    private static AccountingPosting Line(AccountRole role, string name, long msat) =>
        new(role, msat) { AccountName = name };
}