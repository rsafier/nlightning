namespace NLightning.Domain.Tests.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial.Classification;

/// <summary>
/// The move of a closed fact's classifiable lines (NL-660, D-A8) and the review state after it (NL-667).
/// </summary>
public class AccountingReclassificationTests
{
    private static readonly DateTimeOffset s_at = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Given_AClosedFact_When_MovedToAnotherAccount_Then_OnlyTheClassifiableLinesMoveAtTheirValues()
    {
        // Arrange: a received payment valued at 50, a late price of a zero-msat line (an adjustment of the close)
        var entries = new List<AccountingEntry> { Fact(), Price() };

        // Act
        var moves = AccountingReclassification.PlanMove(entries, _ => "income:consulting");

        // Assert: the asset line stays; the sales line and the price's value move to the new account
        Assert.Equal([
                         ("income:sales", 100L, (decimal?)50m, (long?)7L),
                         ("income:sales", 0L, 1.5m, 8L),
                         ("income:consulting", -100L, -50m, 7L),
                         ("income:consulting", 0L, -1.5m, 8L)
                     ],
                     moves.Select(m => (m.AccountName!, m.AmountMsat, m.FiatAmount, m.PriceId)).ToArray());
        Assert.Equal(0, moves.Sum(m => m.AmountMsat));
        Assert.Equal(0m, moves.Sum(m => m.FiatAmount ?? 0m));
    }

    [Fact]
    public void Given_AMoveAlreadyPosted_When_PlannedAgainOrBack_Then_NothingOrTheExactUndo()
    {
        // Arrange
        var fact = Fact();
        var first = Reclass(1, AccountingReclassification.PlanMove([fact], _ => "income:consulting"));
        var entries = new List<AccountingEntry> { fact, first };

        // Act
        var again = AccountingReclassification.PlanMove(entries, _ => "income:consulting");
        var back = AccountingReclassification.PlanMove(entries, _ => "income:sales");

        // Assert
        Assert.Empty(again);
        Assert.Equal(first.Postings.Select(p => (p.AccountName, -p.AmountMsat, -p.FiatAmount)).OrderBy(p => p.AccountName),
                     back.Select(p => (p.AccountName, p.AmountMsat, p.FiatAmount)).OrderBy(p => p.AccountName));
        Assert.Equal("reclass:2", AccountingReclassification.NextDedupeKey(entries));
    }

    [Fact]
    public void Given_AnUnclassifiedFact_When_ReclassifiedOutAndBack_Then_ItIsStillUnclassifiedOnlyWhileItSitsThere()
    {
        // Arrange
        var chart = FinancialChart.Default;
        var fact = Fact("income:unclassified");
        var moveOut = Reclass(1, AccountingReclassification.PlanMove([fact], _ => "income:gifts"));
        var moveBack = Reclass(2, AccountingReclassification.PlanMove([fact, moveOut], _ => "income:unclassified"));

        // Act / Assert
        Assert.True(AccountingReclassification.IsStillUnclassified([fact], chart));
        Assert.False(AccountingReclassification.IsStillUnclassified([fact, moveOut], chart));
        Assert.True(AccountingReclassification.IsStillUnclassified([fact, moveOut, moveBack], chart));
    }

    [Fact]
    public void Given_ALateFactWithoutAProjection_When_Moved_Then_ItsAdjustmentIsTheBase()
    {
        // Arrange
        var lateFact = Fact() with
        {
            Adjustment = 1,
            Flags = AccountingEntryFlags.Adjustment | AccountingEntryFlags.LateFact
        };

        // Act
        var baseEntry = AccountingReclassification.BaseEntry([lateFact]);
        var moves = AccountingReclassification.PlanMove([lateFact], _ => "income:consulting");

        // Assert
        Assert.Same(lateFact, baseEntry);
        Assert.Equal(2, moves.Count);
        Assert.Null(AccountingReclassification.BaseEntry([]));
    }

    [Fact]
    public void Given_AnUnvaluedClosedLine_When_MovedTwice_Then_TheMovesCarryZeroAndTheLineIsWhereTheLastMovePutIt()
    {
        // Arrange - NL-681: a fact left unvalued by a forced close
        var fact = Fact() with
        {
            Postings = Fact().Postings.Select(p => p with { FiatAmount = null, FiatCurrency = null, PriceId = null })
                             .ToList()
        };

        // Act
        var first = Reclass(1, AccountingReclassification.PlanMove([fact], _ => "income:consulting", "USD"));
        var second = Reclass(2, AccountingReclassification.PlanMove([fact, first], _ => "income:licences", "USD"));

        // Assert: valued at 0 in the book's currency (never unvalued, never valued later); the line is now in licences
        Assert.All(first.Postings.Concat(second.Postings), p => Assert.Equal((0m, "USD"), (p.FiatAmount!.Value,
                                                                                         p.FiatCurrency!)));
        Assert.Equal("income:consulting", AccountingReclassification.CurrentAccountOf([fact, first], fact.Postings[1]));
        Assert.Equal("income:licences",
                     AccountingReclassification.CurrentAccountOf([fact, first, second], fact.Postings[1]));
        Assert.Equal("income:sales", AccountingReclassification.CurrentAccountOf([fact], fact.Postings[1]));
    }

    private static AccountingEntry Fact(string account = "income:sales") =>
        new(1, "inv:1:settled", AccountingEventKind.InvoiceSettled, s_at, null, null,
        [
            new AccountingPosting(AccountRole.Channels, 100)
            {
                AccountName = "assets:lightning:channels",
                FiatAmount = 50m,
                FiatCurrency = "USD",
                PriceId = 7
            },
            new AccountingPosting(AccountRole.Received, -100)
            {
                AccountName = account,
                FiatAmount = -50m,
                FiatCurrency = "USD",
                PriceId = 7
            }
        ])
        {
            Book = AccountingBook.Financial,
            ClosedPeriodId = "2026-01"
        };

    private static AccountingEntry Price() =>
        new(1, "inv:1:settled", AccountingEventKind.InvoiceSettled, s_at.AddMonths(2), null, null,
        [
            new AccountingPosting(AccountRole.Received, 0)
            {
                AccountName = "income:sales",
                FiatAmount = -1.5m,
                FiatCurrency = "USD",
                PriceId = 8
            },
            new AccountingPosting(AccountRole.Channels, 0)
            {
                AccountName = "assets:lightning:channels",
                FiatAmount = 1.5m,
                FiatCurrency = "USD",
                PriceId = 8
            }
        ], "[price:1:0:1] adjusts 2026-01: Price of 2026-01-10")
        {
            Book = AccountingBook.Financial,
            Adjustment = 1,
            Flags = AccountingEntryFlags.Adjustment
        };

    private static AccountingEntry Reclass(int adjustment, IReadOnlyList<AccountingPosting> moves) =>
        new(1, "inv:1:settled", AccountingEventKind.InvoiceSettled, s_at.AddMonths(3), null, null, moves,
            $"[reclass:{adjustment}] adjusts 2026-01: Override of 2026-01-10")
        {
            Book = AccountingBook.Financial,
            Adjustment = adjustment,
            Flags = AccountingEntryFlags.Adjustment
        };
}