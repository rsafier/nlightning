namespace NLightning.Domain.Tests.Accounting.Financial.Lots;

using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Lots;

/// <summary>
/// The cost-basis lot pool (NL-602 A3-T4, D-A12): the relief order of FIFO, LIFO and HIFO computed by hand, the cost
/// of a take that adds up to the lot's cost, the lots acquired after a disposal kept for last, and the shortfall.
/// </summary>
public class FinancialLotPoolTests
{
    private const string Usd = "USD";

    // L1 Jan 1, 1e8 msat for 40; L2 Feb 1, 1e8 for 60; L3 Mar 1, 1e8 for 50
    private static readonly AccountingLot s_l1 = Lot(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 40m);
    private static readonly AccountingLot s_l2 = Lot(2, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), 60m);
    private static readonly AccountingLot s_l3 = Lot(3, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), 50m);
    private static readonly DateTimeOffset s_april = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<AccountingCostBasisMethod, long, long, decimal, decimal> MethodCases => new()
    {
        // 1.5e8 msat: FIFO L1 1e8 (40) + L2 5e7 (30); LIFO L3 1e8 (50) + L2 5e7 (30); HIFO L2 1e8 (60) + L3 5e7 (25)
        { AccountingCostBasisMethod.Fifo, 1, 2, 40m, 30m },
        { AccountingCostBasisMethod.Lifo, 3, 2, 50m, 30m },
        { AccountingCostBasisMethod.Hifo, 2, 3, 60m, 25m }
    };

    [Theory]
    [MemberData(nameof(MethodCases))]
    public void Given_ThreeLots_When_1Point5e8MsatAreDisposed_Then_TheMethodPicksTheLotsComputedByHand(
        AccountingCostBasisMethod method, long firstLot, long secondLot, decimal firstCost, decimal secondCost)
    {
        // Arrange
        var pool = new FinancialLotPool([s_l1, s_l2, s_l3], method, Usd);

        // Act
        var takes = pool.PlanRelief(150_000_000, s_april, out var shortfall);

        // Assert
        Assert.Equal(0, shortfall);
        Assert.Equal([(firstLot, 100_000_000L, (decimal?)firstCost), (secondLot, 50_000_000L, secondCost)],
                     takes.Select(t => (t.LotId, t.Msat, t.Cost)).ToArray());
        Assert.Equal(300_000_000, pool.RemainingMsat);
    }

    [Fact]
    public void Given_ALotRelievedInThirds_When_EachTakeIsCosted_Then_TheTakesAddUpToTheLotsCostExactly()
    {
        // Arrange: 3 msat for 10 USD
        var pool = new FinancialLotPool([s_l1 with { OriginalMsat = 3, RemainingMsat = 3, FiatCost = 10m }],
                                        AccountingCostBasisMethod.Fifo, Usd);
        var costs = new List<decimal?>();

        // Act
        for (var i = 0; i < 3; i++)
        {
            var take = Assert.Single(pool.PlanRelief(1, s_april, out _));
            costs.Add(take.Cost);
            pool.Relieve(take.LotId, take.Msat);
        }

        // Assert: 3.33333333 + 3.33333334 + 3.33333333
        Assert.Equal([3.33333333m, 3.33333334m, 3.33333333m], costs);
        Assert.Equal(0, pool.RemainingMsat);
        Assert.Empty(pool.OpenLots);
    }

    [Fact]
    public void Given_ALotAcquiredAfterTheDisposal_When_FifoRelieves_Then_ItIsUsedOnlyForWhatTheOthersDoNotCover()
    {
        // Arrange: L3 is acquired after the disposal's time (a late fact's adjustment)
        var pool = new FinancialLotPool([s_l1, s_l3], AccountingCostBasisMethod.Lifo, Usd);

        // Act: LIFO would take L3 first, but it comes after the disposal
        var takes = pool.PlanRelief(150_000_000, new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero),
                                    out var shortfall);

        // Assert
        Assert.Equal(0, shortfall);
        Assert.Equal([1L, 3L], takes.Select(t => t.LotId).ToArray());
        Assert.Equal([100_000_000L, 50_000_000L], takes.Select(t => t.Msat).ToArray());
    }

    [Fact]
    public void Given_LessInThePoolThanDisposed_When_Relieved_Then_TheRestIsAShortfall()
    {
        // Arrange
        var pool = new FinancialLotPool([s_l1], AccountingCostBasisMethod.Fifo, Usd);

        // Act
        var takes = pool.PlanRelief(120_000_000, s_april, out var shortfall);

        // Assert
        Assert.Equal(20_000_000, shortfall);
        Assert.Equal(100_000_000, Assert.Single(takes).Msat);
    }

    [Fact]
    public void Given_HifoWithAnUnvaluedLot_When_Relieved_Then_TheValuedLotsComeFirstAndTheUnvaluedHasNoCost()
    {
        // Arrange: L0 has no cost; HIFO puts it after every valued lot
        var unvalued = s_l1 with { Id = 9, FiatCost = null, FiatCurrency = null };
        var pool = new FinancialLotPool([unvalued, s_l3], AccountingCostBasisMethod.Hifo, Usd);

        // Act
        var takes = pool.PlanRelief(150_000_000, s_april, out _);

        // Assert
        Assert.Equal([(3L, (decimal?)50m), (9L, null)], takes.Select(t => (t.LotId, t.Cost)).ToArray());
    }

    [Fact]
    public void Given_ALotInAnotherCurrency_When_Costed_Then_ItHasNoCostInTheBooksCurrency()
    {
        // Arrange
        var euro = s_l1 with { FiatCurrency = "EUR" };

        // Act
        var cost = FinancialLotPool.CostOf(euro, 1_000, Usd);

        // Assert
        Assert.Null(cost);
    }

    private static AccountingLot Lot(long id, DateTimeOffset at, decimal cost) =>
        new(id, at, AccountingLotOrigin.Acquisition, id, 0, null, null, 100_000_000, 100_000_000, cost, Usd, null,
            false, null);
}