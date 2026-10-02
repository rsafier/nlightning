namespace NLightning.Domain.Tests.Accounting.Financial.Lots;

using Domain.Accounting.Books;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Financial.Lots;

/// <summary>
/// The cost-basis rules of one financial entry (NL-602 A3-T4, D-A9, D-A12), every amount computed by hand: the market
/// value of the lines, the lots relieved and opened, the realized gain or loss and the cost-basis line, transfers that
/// touch no lot, a loss at zero proceeds, a rebalance where only the fee disposes, an unvalued entry and an unvalued lot.
/// </summary>
/// <remarks>Prices are USD per BTC: at 70,000, 1e8 msat (100,000 sat) is worth 70 USD.</remarks>
public class FinancialEntryPlannerTests
{
    private const string Usd = "USD";
    private static readonly FinancialChart s_chart = FinancialChart.Default;
    private static readonly DateTimeOffset s_april = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly AccountingPrice s_price70K = new(7, Usd, s_april, 70_000m, AccountingPriceSource.Csv,
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
    public void Given_APayment_When_Planned_Then_TheGainAndTheCostBasisLineAreTheHandComputedOnes(
        AccountingCostBasisMethod method, decimal cost, decimal gain)
    {
        // Arrange
        var pool = ThreeLots(method);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -150_000_000),
            Line(AccountRole.Sent, "expenses:payments", 150_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: channels -105 at market, payment 105, cost-basis +gain, realized gain -gain (a credit)
        Assert.Equal(AccountingEntryFlags.None, plan.Flags);
        Assert.Equal(gain, plan.RealizedGain);
        Assert.Equal(0m, plan.FiatSum);
        Assert.Equal([-105m, 105m, gain, -gain], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal(["assets:lightning:channels", "expenses:payments", "assets:cost-basis", "income:gains:realized"],
                     plan.Postings.Select(p => p.AccountName!).ToArray());
        Assert.Equal(cost, plan.Reliefs.Sum(r => r.Cost));
        Assert.Equal([70m, 35m], plan.Reliefs.Select(r => r.Proceeds!.Value).ToArray());
        Assert.Null(plan.NewLot);
    }

    [Fact]
    public void Given_AnInvoiceSettled_When_Planned_Then_ALotOpensAtFairValueAndNothingIsRelieved()
    {
        // Arrange
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", 200_000_000),
            Line(AccountRole.Received, "income:sales", -200_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: 2e8 msat at 70,000 = 140
        Assert.Empty(plan.Reliefs);
        Assert.Equal(new FinancialLotSpec(200_000_000, 140m, Usd, 7, AccountingLotOrigin.Acquisition, false),
                     plan.NewLot);
        Assert.Equal([140m, -140m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Null(plan.RealizedGain);
    }

    [Fact]
    public void Given_AChannelOpen_When_Planned_Then_OnlyTheFeeDisposes()
    {
        // Arrange: Dr Channels 1e8; Dr FeeFunding 1e6; Cr Clearing 1.01e8
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", 100_000_000),
            Line(AccountRole.FeeFunding, "expenses:fees:funding", 1_000_000),
            Line(AccountRole.Clearing, "assets:onchain:clearing", -101_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: the fee 1e6 relieves L1 (cost 0.4), proceeds 0.7, gain 0.3; the 1e8 moved touches no lot
        var relief = Assert.Single(plan.Reliefs);
        Assert.Equal((1L, 1_000_000L, (decimal?)0.4m, (decimal?)0.7m),
                     (relief.LotId, relief.Msat, relief.Cost, relief.Proceeds));
        Assert.Equal(0.3m, plan.RealizedGain);
        Assert.Equal([70m, 0.7m, -70.7m, 0.3m, -0.3m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Null(plan.NewLot);
    }

    [Fact]
    public void Given_AWalletTransfer_When_Planned_Then_NoLotMoves()
    {
        // Arrange: Cr Wallet 5e8; Dr Clearing 5e8
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo);
        var lines = new[]
        {
            Line(AccountRole.Wallet, "assets:onchain:wallet", -500_000_000),
            Line(AccountRole.Clearing, "assets:onchain:clearing", 500_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert
        Assert.Empty(plan.Reliefs);
        Assert.Null(plan.NewLot);
        Assert.Equal(2, plan.Postings.Count);
        Assert.Equal(0m, plan.FiatSum);
    }

    [Fact]
    public void Given_ALossOnChain_When_Planned_Then_ItDisposesAtZeroProceeds()
    {
        // Arrange: a force close loses 1e6 msat (Dr LossOnchain), L1's cost of it 0.4
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -1_000_000),
            Line(AccountRole.LossOnchain, "expenses:losses", 1_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: channels -0.7 at market, the loss 0 (zero proceeds), cost-basis +0.3, realized loss +0.4
        Assert.Equal(-0.4m, plan.RealizedGain);
        Assert.Equal((decimal?)0m, Assert.Single(plan.Reliefs).Proceeds);
        Assert.Equal([-0.7m, 0m, 0.3m, 0.4m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal("expenses:losses:realized", plan.Postings[^1].AccountName);
    }

    [Fact]
    public void Given_ARebalance_When_Planned_Then_OnlyTheRouteFeeDisposes()
    {
        // Arrange: the paying side, split by the classification (NL-609): Cr Channels 1.001e8, Dr Rebalance 1e8, Dr
        // RoutingFees 1e5
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -100_100_000),
            Line(AccountRole.Rebalance, "equity:transfers:rebalance", 100_000_000),
            Line(AccountRole.RoutingFees, "expenses:fees:routing", 100_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: only the fee 1e5 relieves L1 (cost 0.04), proceeds 0.07, gain 0.03
        var relief = Assert.Single(plan.Reliefs);
        Assert.Equal((100_000L, (decimal?)0.04m), (relief.Msat, relief.Cost));
        Assert.Equal(0.03m, plan.RealizedGain);
        Assert.Equal([-70.07m, 70m, 0.07m, 0.03m, -0.03m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
    }

    [Fact]
    public void Given_NoUsablePrice_When_Planned_Then_TheEntryIsUnvaluedAndWaitsForItsPrice()
    {
        // Arrange
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo);
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
                                         AccountingLotOrigin.Acquisition, 4, 0, null, null, 100_000_000, 100_000_000,
                                         null, null, null, false, null);
        var pool = new FinancialLotPool([unvalued], AccountingCostBasisMethod.Fifo, Usd);
        var lines = new[]
        {
            Line(AccountRole.Channels, "assets:lightning:channels", -50_000_000),
            Line(AccountRole.Sent, "expenses:payments", 50_000_000)
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert
        Assert.Equal(AccountingEntryFlags.GainPending, plan.Flags);
        Assert.Null(plan.RealizedGain);
        Assert.DoesNotContain(plan.Postings, p => p.AccountName == "income:gains:realized");
        Assert.Equal((decimal?)35m, Assert.Single(plan.Reliefs).Proceeds);
        Assert.Null(plan.Reliefs[0].Cost);
        Assert.Equal(0m, plan.FiatSum);
    }

    [Fact]
    public void Given_AnOpeningBalance_When_Planned_Then_ItsLotHasAnEstimatedBasisOrNoneOnceLotsAreImported()
    {
        // Arrange
        var pool = new FinancialLotPool([], AccountingCostBasisMethod.Fifo, Usd);
        var lines = new[]
        {
            Line(AccountRole.Wallet, "assets:onchain:wallet", 100_000_000),
            Line(AccountRole.Opening, "equity:opening-balances", -100_000_000)
        };

        // Act
        var estimated = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april)
        {
            IsOpeningBalance = true
        }, pool, s_chart);
        var imported = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april)
        {
            IsOpeningBalance = true,
            OpeningLotsImported = true
        }, pool, s_chart);

        // Assert
        Assert.Equal(new FinancialLotSpec(100_000_000, 70m, Usd, 7, AccountingLotOrigin.Opening, true),
                     estimated.NewLot);
        Assert.Null(imported.NewLot);
        Assert.Equal([70m, -70m], imported.Postings.Select(p => p.FiatAmount!.Value).ToArray());
    }

    [Fact]
    public void Given_ACancelledFact_When_Planned_Then_ItIsValuedButTouchesNoLot()
    {
        // Arrange: a deposit that a reorg reverses in the open period
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo);
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
        Assert.Null(plan.NewLot);
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
        Assert.Equal(2, plan.Postings.Count);
    }

    [Fact]
    public void Given_ValuedLinesOfAReversal_When_Planned_Then_TheirValuesAreKept()
    {
        // Arrange: the negation of a closed period's deposit at its original 40 (price 40,000), reversed at 70,000
        var pool = ThreeLots(AccountingCostBasisMethod.Fifo);
        var lines = new[]
        {
            Line(AccountRole.Wallet, "assets:onchain:wallet", -100_000_000) with { FiatAmount = -40m, FiatCurrency = Usd },
            Line(AccountRole.TransfersIn, "equity:transfers:in", 100_000_000) with { FiatAmount = 40m, FiatCurrency = Usd }
        };

        // Act
        var plan = FinancialEntryPlanner.Plan(new FinancialEntryPlanInput(lines, s_price70K, s_april), pool, s_chart);

        // Assert: the deposit taken back disposes of L1 (cost 40) at its original value: no gain
        Assert.Equal([-40m, 40m], plan.Postings.Select(p => p.FiatAmount!.Value).ToArray());
        Assert.Equal(0m, plan.RealizedGain);
        Assert.Equal(1L, Assert.Single(plan.Reliefs).LotId);
    }

    private static FinancialLotPool ThreeLots(AccountingCostBasisMethod method) =>
        new([
            Lot(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 40m),
            Lot(2, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), 60m),
            Lot(3, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), 50m)
        ], method, Usd);

    private static AccountingLot Lot(long id, DateTimeOffset at, decimal cost) =>
        new(id, at, AccountingLotOrigin.Acquisition, id, 0, null, null, 100_000_000, 100_000_000, cost, Usd, null,
            false, null);

    private static AccountingPosting Line(AccountRole role, string name, long msat) =>
        new(role, msat) { AccountName = name };
}