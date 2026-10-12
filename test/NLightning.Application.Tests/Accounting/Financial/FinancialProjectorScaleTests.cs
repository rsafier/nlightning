using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Accounting.Financial;

using Application.Accounting.Financial;
using Domain.Accounting.Books;
using Domain.Accounting.Models;
using Domain.Accounting.Prices;
using Domain.Persistence.Interfaces;

/// <summary>
/// The financial projector's scale (NL-658) on SQLite with the production rules: the lot pool is kept across rounds
/// while the saved lots keep its fingerprint (read again after a rebuild or any other write), and the background
/// rounds wait with the replays the back-valuation asks for while it catches up, at most
/// <see cref="FinancialBooksProjector.MaxReplayDeferral"/>; the books stay those of a
/// rebuild.
/// </summary>
public sealed class FinancialProjectorScaleTests
{
    private const string Usd = FinancialProjectorTestKit.Usd;

    private static readonly DateTimeOffset s_t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t1 = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t2 = new(2026, 1, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t3 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_now = new(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly (DateTimeOffset, decimal)[] s_prices =
        [(s_t0, 40_000m), (s_t1, 50_000m), (s_t2, 60_000m), (s_t3, 30_000m)];

    [Fact]
    public async Task Given_RoundsOfNewEntries_When_Projected_Then_TheOpenLotsAreReadOnceUntilSomethingElseWritesThem()
    {
        // Arrange
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddPricesAsync(s_prices);

        // Act: three rounds with new entries, a rebuild, one more round, then a lot changed behind its back
        foreach (var chunk in Story(kit).Chunk(3))
        {
            await kit.AddAsync(chunk);
            await kit.ProjectAsync();
        }

        var afterRounds = kit.Projector.OpenLotLoads;
        await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);
        await kit.AddAsync(kit.Invoice(10_000_000, s_t3));
        await kit.ProjectAsync();
        var afterRebuild = kit.Projector.OpenLotLoads;
        using (var scope = kit.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var lot = (await unitOfWork.AccountingLotDbRepository.ListOpenLotsAsync(
                           cancellationToken: TestContext.Current.CancellationToken))[0];
            await unitOfWork.AccountingLotDbRepository.UpdateLotAsync(lot with { RemainingMsat = lot.RemainingMsat - 1 },
                                                                      TestContext.Current.CancellationToken);
            await unitOfWork.SaveChangesAsync();
        }

        await kit.AddAsync(kit.Invoice(10_000_000, s_t3));
        await kit.ProjectAsync();

        // Assert: one read for the three rounds; one more after the rebuild; one more after the write behind its back
        Assert.Equal(1, afterRounds);
        Assert.Equal(2, afterRebuild);
        Assert.Equal(3, kit.Projector.OpenLotLoads);
    }

    [Fact]
    public async Task Given_TheBackValuationCatchingUp_When_ItLowersTheCursor_Then_TheBackgroundRoundsWaitAndReplayOnce()
    {
        // Arrange: the story projected without prices
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddAsync(Story(kit));
        await kit.ProjectAsync();
        kit.CatchingUp = true;

        // Act: the prices arrive (the back-valuation lowers the cursor); the background rounds wait while it catches up
        await kit.Valuation.ImportAsync(Usd, s_prices.Select(p => new AccountingPricePoint(p.Item1, p.Item2)).ToList(),
                                        TestContext.Current.CancellationToken);
        var waited = await kit.Projector.RunRoundAsync(TestContext.Current.CancellationToken);
        var cursorWhileWaiting = await CursorAsync(kit);
        kit.Clock.Now = s_now + FinancialBooksProjector.MaxReplayDeferral;
        var replayed = await kit.Projector.RunRoundAsync(TestContext.Current.CancellationToken);

        // Assert: nothing while waiting, the whole replay once the wait is over; the book equals one valued from the start
        Assert.Equal(0, waited);
        Assert.Equal(0, cursorWhileWaiting);
        Assert.Equal(Story(kit).Length, replayed);
        await using var early = await FinancialProjectorTestKit.CreateAsync(s_now);
        await early.AddPricesAsync(s_prices);
        await early.AddAsync(Story(early));
        await early.ProjectAsync();
        Assert.Equal(await early.SnapshotFinancialAsync(), await kit.SnapshotFinancialAsync());
    }

    [Fact]
    public async Task Given_TheBackValuationCatchingUp_When_AnExplicitProjectionRuns_Then_ItReplaysAtOnce()
    {
        // Arrange
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_now);
        await kit.AddAsync(Story(kit));
        await kit.ProjectAsync();
        kit.CatchingUp = true;
        await kit.Valuation.ImportAsync(Usd, s_prices.Select(p => new AccountingPricePoint(p.Item1, p.Item2)).ToList(),
                                        TestContext.Current.CancellationToken);

        // Act: a close, a rebuild or a report projects explicitly
        var projected = await kit.Projector.ProjectAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(Story(kit).Length, projected);
        Assert.Equal(Story(kit).Length, await CursorAsync(kit));
    }

    private static Task<long> CursorAsync(FinancialProjectorTestKit kit) =>
        kit.ReadAsync(u => u.AccountingBooksDbRepository.GetCursorAsync(AccountingBook.Financial,
                                                                         TestContext.Current.CancellationToken));

    // 1-2 the cutover (wallet 1e9); 3 a deposit; 4-6 a channel open with change; 7 an invoice; 8 a payment
    private static AccountingEventModel[] Story(FinancialProjectorTestKit kit) =>
    [
        FinancialProjectorTestKit.Opening("wallet", 1_000_000_000, s_t0),
        FinancialProjectorTestKit.Cutover(s_t0),
        kit.Deposit(1_000_000_000, s_t1),
        kit.WalletSpent(1_000_000_000, s_t2),
        kit.Funded(899_000_000, 1_000_000, s_t2),
        kit.WalletIn(100_000_000, s_t2),
        kit.Invoice(200_000_000, s_t3),
        kit.Payment(300_000_000, 100_000, s_t3)
    ];
}