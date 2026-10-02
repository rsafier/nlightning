namespace NLightning.Domain.Tests.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;

/// <summary>
/// The period close's pieces (NL-602 A3-T5, D-A13): the period ids, the closing state's canonical text and the digest,
/// which covers every field a reader relies on and none that a later legitimate write changes.
/// </summary>
public class AccountingCloseDigestTests
{
    private static readonly DateTimeOffset s_at = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2026-09", "2026-09", "2026-09-01", "2026-10-01")]
    [InlineData(" 2026-12 ", "2026-12", "2026-12-01", "2027-01-01")]
    [InlineData("2026-07-01..2026-09-30", "2026-07-01..2026-09-30", "2026-07-01", "2026-10-01")]
    [InlineData("2026-09-15..2026-09-15", "2026-09-15..2026-09-15", "2026-09-15", "2026-09-16")]
    public void Given_APeriodText_When_Parsed_Then_ItHasItsCanonicalIdAndUtcBounds(string text, string id,
                                                                                   string start, string end)
    {
        // Act
        var range = AccountingPeriodRange.Parse(text);

        // Assert
        Assert.Equal(id, range.PeriodId);
        Assert.Equal(DateTimeOffset.Parse(start + "T00:00:00Z"), range.Start);
        Assert.Equal(DateTimeOffset.Parse(end + "T00:00:00Z"), range.End);
        Assert.Equal(TimeSpan.Zero, range.Start.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("2026-13")]
    [InlineData("September")]
    [InlineData("2026-09-30..2026-09-01")]
    [InlineData("2026-09-01..")]
    [InlineData("2026-09-01..2026-09-31")]
    public void Given_NotAPeriod_When_Parsed_Then_ItIsRefusedWithAReason(string? text)
    {
        // Act
        var parsed = AccountingPeriodRange.TryParse(text, out var range, out var error);

        // Assert
        Assert.False(parsed);
        Assert.Null(range);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<ArgumentException>(() => AccountingPeriodRange.Parse(text));
    }

    [Fact]
    public void Given_AClosingState_When_EncodedAndDecoded_Then_ItRoundTripsCanonically()
    {
        // Arrange: out of order, a padded fiat scale
        var state = new AccountingClosingState(42,
        [
            new AccountingAccountBalance(AccountingBook.Financial, AccountRole.Received, "income:sales", -5_000,
                                         -1.50000000m),
            new AccountingAccountBalance(AccountingBook.Financial, AccountRole.Channels, "assets:channels", 5_000,
                                         1.5m)
        ]);

        // Act
        var text = state.Encode();
        var decoded = AccountingClosingState.Decode(text);

        // Assert
        Assert.Equal("{\"version\":1,\"replayAfter\":42,\"balances\":[{\"account\":1,\"name\":\"assets:channels\","
                   + "\"msat\":5000,\"fiat\":\"1.5\"},{\"account\":10,\"name\":\"income:sales\",\"msat\":-5000,"
                   + "\"fiat\":\"-1.5\"}]}", text);
        Assert.Equal(42, decoded.ReplayAfterLedgerSeq);
        Assert.Equal(text, decoded.Encode());
        Assert.Throws<FormatException>(() => AccountingClosingState.Decode("{\"version\":2}"));
        Assert.Throws<FormatException>(() => AccountingClosingState.Decode("not json"));
        Assert.Throws<FormatException>(() => AccountingClosingState.Decode(null));
    }

    [Fact]
    public void Given_TheSameClose_When_DigestedTwice_Then_TheDigestIsTheSameAnd32Bytes()
    {
        // Act
        var first = Digest(Header(), [Entry()], [Relief()], [(Lot(), 700L)], "{}");
        var second = Digest(Header(), [Entry()], [Relief()], [(Lot(), 700L)], "{}");

        // Assert
        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Given_ACoveredFieldChanged_When_Digested_Then_TheDigestChanges()
    {
        // Arrange
        var baseline = Digest(Header(), [Entry()], [Relief()], [(Lot(), 700L)], "{}");
        var variants = new List<byte[]>
        {
            Digest(Header() with { PeriodId = "2026-10" }, [Entry()], [Relief()], [(Lot(), 700L)], "{}"),
            Digest(Header() with { LastLedgerSeq = 8 }, [Entry()], [Relief()], [(Lot(), 700L)], "{}"),
            Digest(Header() with { ChainHash = Filled(9) }, [Entry()], [Relief()], [(Lot(), 700L)], "{}"),
            Digest(Header() with { PreviousDigest = Filled(1) }, [Entry()], [Relief()], [(Lot(), 700L)], "{}"),
            Digest(Header() with { NodeId = Filled(3, 33) }, [Entry()], [Relief()], [(Lot(), 700L)], "{}"),
            Digest(Header() with { Forced = true }, [Entry()], [Relief()], [(Lot(), 700L)], "{}"),
            Digest(Header() with { ClosedAt = s_at.AddTicks(1) }, [Entry()], [Relief()], [(Lot(), 700L)], "{}"),
            Digest(Header(), [Entry() with { Note = "x" }], [Relief()], [(Lot(), 700L)], "{}"),
            Digest(Header(), [Entry() with { Flags = AccountingEntryFlags.Unvalued }], [Relief()], [(Lot(), 700L)],
                   "{}"),
            Digest(Header(), [Entry(fiat: 1.21m)], [Relief()], [(Lot(), 700L)], "{}"),
            Digest(Header(), [Entry(account: "income:other")], [Relief()], [(Lot(), 700L)], "{}"),
            Digest(Header(), [], [Relief()], [(Lot(), 700L)], "{}"),
            Digest(Header(), [Entry()], [Relief() with { Msat = 301 }], [(Lot(), 700L)], "{}"),
            Digest(Header(), [Entry()], [Relief() with { Proceeds = 2m }], [(Lot(), 700L)], "{}"),
            Digest(Header(), [Entry()], [Relief()], [(Lot() with { FiatCost = 9m }, 700L)], "{}"),
            Digest(Header(), [Entry()], [Relief()], [(Lot() with { BasisEstimated = true }, 700L)], "{}"),
            Digest(Header(), [Entry()], [Relief()], [(Lot(), 699L)], "{}"),
            Digest(Header(), [Entry()], [Relief()], [], "{}"),
            Digest(Header(), [Entry()], [Relief()], [(Lot(), 700L)], "{ }")
        };

        // Assert
        Assert.All(variants, v => Assert.NotEqual(baseline, v));
        Assert.Equal(variants.Count, variants.Select(Convert.ToHexString).Distinct().Count());
    }

    [Fact]
    public void Given_AFieldALaterWriteMayChange_When_Digested_Then_TheDigestStays()
    {
        // Arrange: the closed period's mark, a lot's current remaining amount and account, a padded decimal scale
        var baseline = Digest(Header(), [Entry()], [Relief()], [(Lot(), 700L)], "{}");

        // Act
        var marked = Digest(Header(), [Entry() with { ClosedPeriodId = "2026-09" }],
                            [Relief() with { ClosedPeriodId = "2026-09" }],
                            [(Lot() with { ClosedPeriodId = "2026-09", RemainingMsat = 1, Account = AccountRole.Wallet },
                              700L)], "{}");
        var padded = Digest(Header(), [Entry(fiat: 1.20000000m)], [Relief()], [(Lot(), 700L)], "{}");

        // Assert
        Assert.Equal(baseline, marked);
        Assert.Equal(baseline, padded);
    }

    [Fact]
    public void Given_AFinishedDigest_When_MoreIsAdded_Then_ItThrows()
    {
        // Arrange
        using var digest = new AccountingCloseDigest(Header());
        digest.Finish("{}");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => digest.AddEntry(Entry()));
        Assert.Throws<ArgumentException>(() => new AccountingCloseDigest(Header() with { ChainHash = new byte[31] }));
    }

    private static byte[] Digest(AccountingCloseHeader header, AccountingEntry[] entries, AccountingLotRelief[] reliefs,
                                 (AccountingLot Lot, long Remaining)[] lots, string state)
    {
        using var digest = new AccountingCloseDigest(header);
        foreach (var entry in entries)
            digest.AddEntry(entry);
        foreach (var relief in reliefs)
            digest.AddRelief(relief);
        foreach (var (lot, remaining) in lots)
            digest.AddOpenLot(lot, remaining);

        return digest.Finish(state);
    }

    private static AccountingCloseHeader Header() =>
        new("2026-09", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), 7, Filled(7), null, Filled(2, 33), false, s_at);

    private static AccountingEntry Entry(decimal fiat = 1.2m, string account = "income:sales") =>
        new(3, "inv:aa:settled", AccountingEventKind.InvoiceSettled, s_at, null, null,
        [
            new AccountingPosting(AccountRole.Channels, 2_000)
            {
                AccountName = "assets:channels",
                FiatAmount = fiat,
                FiatCurrency = "USD"
            },
            new AccountingPosting(AccountRole.Received, -2_000)
            {
                AccountName = account,
                FiatAmount = -fiat,
                FiatCurrency = "USD"
            }
        ])
        {
            Book = AccountingBook.Financial,
            Classification = AccountingClassificationSource.Default
        };

    private static AccountingLotRelief Relief() => new(4, 1, 3, 0, s_at, 300, 0.18m, 0.2m, null);

    private static AccountingLot Lot() =>
        new(1, s_at, AccountingLotOrigin.Acquisition, 3, 0, null, null, 1_000, 700, 0.6m, "USD", null, false, null);

    private static byte[] Filled(byte value, int length = 32) => Enumerable.Repeat(value, length).ToArray();
}