namespace NLightning.Domain.Tests.Accounting;

using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;

public class AccountingEventSealerTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Given_AnEmptyFeed_When_ABatchIsSealed_Then_LedgerSequencesAreDenseAndChained()
    {
        // Arrange
        var batch = new[] { Event(10, "a"), Event(11, "b"), Event(12, "c") };

        // Act
        var seals = AccountingEventSealer.Seal(AccountingChainTip.Genesis, batch, new HashSet<string>(), out var tip);

        // Assert
        Assert.Equal([1L, 2L, 3L], seals.Select(s => s.LedgerSeq!.Value));
        Assert.Equal([10L, 11L, 12L], seals.Select(s => s.Id));
        Assert.Equal(AccountingEventHasher.ComputeHash(new byte[32], 1, batch[0]), seals[0].Hash);
        Assert.Equal(AccountingEventHasher.ComputeHash(seals[0].Hash, 2, batch[1]), seals[1].Hash);
        Assert.Equal(AccountingEventHasher.ComputeHash(seals[1].Hash, 3, batch[2]), seals[2].Hash);
        Assert.Equal(3, tip.LedgerSeq);
        Assert.Equal(seals[2].Hash, tip.Hash);
    }

    [Fact]
    public void Given_ATip_When_TheNextBatchIsSealed_Then_ItContinuesTheSequenceAndChain()
    {
        // Arrange
        var first = AccountingEventSealer.Seal(AccountingChainTip.Genesis, [Event(1, "a")], new HashSet<string>(),
                                               out var tip);
        var next = Event(2, "b");

        // Act
        var seals = AccountingEventSealer.Seal(tip, [next], new HashSet<string>(), out var newTip);

        // Assert
        Assert.Equal(2, seals[0].LedgerSeq);
        Assert.Equal(AccountingEventHasher.ComputeHash(first[0].Hash, 2, next), seals[0].Hash);
        Assert.Equal(2, newTip.LedgerSeq);
    }

    [Fact]
    public void Given_KeysAlreadySealedOrRepeated_When_Sealed_Then_TheyAreDuplicatesAndTakeNoSequence()
    {
        // Arrange: "a" is sealed already, "b" appears twice in the batch
        var batch = new[] { Event(5, "a"), Event(6, "b"), Event(7, "b"), Event(8, "c") };

        // Act
        var seals = AccountingEventSealer.Seal(new AccountingChainTip(4, new byte[32]), batch,
                                               new HashSet<string> { "a" }, out var tip);

        // Assert
        Assert.True(seals[0].IsDuplicate);
        Assert.Equal(5, seals[1].LedgerSeq);
        Assert.True(seals[2].IsDuplicate);
        Assert.Null(seals[2].Hash);
        Assert.Equal(6, seals[3].LedgerSeq);
        Assert.Equal(AccountingEventHasher.ComputeHash(seals[1].Hash, 6, batch[3]), seals[3].Hash);
        Assert.Equal(6, tip.LedgerSeq);
    }

    [Fact]
    public void Given_ASealedEvent_When_ItIsPassedAgain_Then_SealThrows()
    {
        // Arrange
        var sealedEvent = new AccountingEventModel
        {
            Id = 1,
            EventKey = "a",
            Kind = AccountingEventKind.InvoiceSettled,
            OccurredAt = s_at,
            LedgerSeq = 1,
            Hash = new byte[32]
        };

        // Act / Assert
        Assert.Throws<ArgumentException>(() => AccountingEventSealer.Seal(
                                             AccountingChainTip.Genesis, [sealedEvent], new HashSet<string>(),
                                             out _));
    }

    [Fact]
    public void Given_AnyFieldChanged_When_Hashed_Then_TheHashChanges()
    {
        // Arrange
        var original = Event(1, "a");
        var variants = new[]
        {
            Event(1, "a2"),
            Event(1, "a", amountMsat: 1001),
            Event(1, "a", feeMsat: 1),
            Event(1, "a", details: new SortedDictionary<string, string> { ["memo"] = "x" }),
            new AccountingEventModel
            {
                Id = 1, EventKey = "a", Kind = AccountingEventKind.PaymentSucceeded, OccurredAt = s_at,
                AmountMsat = 1000
            },
            new AccountingEventModel
            {
                Id = 1, EventKey = "a", Kind = AccountingEventKind.InvoiceSettled, OccurredAt = s_at.AddTicks(1),
                AmountMsat = 1000
            }
        };
        var originalHash = AccountingEventHasher.ComputeHash(new byte[32], 1, original);

        // Act / Assert
        foreach (var variant in variants)
            Assert.NotEqual(originalHash, AccountingEventHasher.ComputeHash(new byte[32], 1, variant));
        Assert.NotEqual(originalHash, AccountingEventHasher.ComputeHash(new byte[32], 2, original));
        Assert.Equal(originalHash, AccountingEventHasher.ComputeHash(new byte[32], 1, Event(99, "a")));
    }

    [Fact]
    public void Given_DetailsInDifferentInsertionOrder_When_Hashed_Then_TheHashIsTheSame()
    {
        // Arrange
        var first = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" };
        var second = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };

        // Act
        var firstHash = AccountingEventHasher.ComputeHash(new byte[32], 1, Event(1, "k", details: first));
        var secondHash = AccountingEventHasher.ComputeHash(new byte[32], 1, Event(1, "k", details: second));

        // Assert
        Assert.Equal(firstHash, secondHash);
    }

    private static AccountingEventModel Event(long id, string key, long amountMsat = 1000, long feeMsat = 0,
                                              IReadOnlyDictionary<string, string>? details = null) =>
        new()
        {
            Id = id,
            EventKey = key,
            Kind = AccountingEventKind.InvoiceSettled,
            OccurredAt = s_at,
            AmountMsat = amountMsat,
            FeeMsat = feeMsat,
            Details = details ?? new SortedDictionary<string, string>()
        };
}