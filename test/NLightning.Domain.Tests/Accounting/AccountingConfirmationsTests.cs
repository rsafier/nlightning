namespace NLightning.Domain.Tests.Accounting;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;

public class AccountingConfirmationsTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TxId s_txId = new(Enumerable.Repeat((byte)0x5a, 32).ToArray());
    private static readonly string s_baseKey = AccountingEventKeys.WalletReceived(s_txId, 1);

    [Fact]
    public void Given_NothingRecorded_When_AskingForTheKey_Then_ItIsTheBaseKey()
    {
        // Act
        var key = AccountingConfirmations.NextConfirmationKey(s_baseKey, []);

        // Assert
        Assert.Equal(s_baseKey, key);
    }

    [Fact]
    public void Given_AStandingConfirmation_When_AskingForTheKey_Then_ThereIsNone()
    {
        // Arrange: the block was processed again
        var recorded = Event(s_baseKey, 101);

        // Act
        var key = AccountingConfirmations.NextConfirmationKey(s_baseKey, [recorded]);

        // Assert
        Assert.Null(key);
    }

    [Fact]
    public void Given_AReversedConfirmation_When_AskingForTheKey_Then_ItIsTheSecondConfirmationKey()
    {
        // Arrange
        var recorded = Event(s_baseKey, 101);
        var reversal = AccountingConfirmations.CreateReversal(recorded, s_at, 100);

        // Act
        var key = AccountingConfirmations.NextConfirmationKey(s_baseKey, [recorded, reversal]);

        // Assert
        Assert.Equal(AccountingEventKeys.Reconfirmed(s_baseKey, 2), key);
        Assert.Equal(s_baseKey + ":c2", key);
    }

    [Fact]
    public void Given_TwoReversedConfirmations_When_AskingForTheKey_Then_ItIsTheThirdAndAStandingSecondBlocksIt()
    {
        // Arrange: confirmed at 101, reorged, confirmed again at the same height, reorged again
        var first = Event(s_baseKey, 101);
        var second = Event(AccountingEventKeys.Reconfirmed(s_baseKey, 2), 101);
        var events = new List<AccountingEventModel>
        {
            first, AccountingConfirmations.CreateReversal(first, s_at, 100), second
        };

        // Act
        var whileSecondStands = AccountingConfirmations.NextConfirmationKey(s_baseKey, events);
        events.Add(AccountingConfirmations.CreateReversal(second, s_at, 100));
        var afterSecondReversed = AccountingConfirmations.NextConfirmationKey(s_baseKey, events);

        // Assert
        Assert.Null(whileSecondStands);
        Assert.Equal(AccountingEventKeys.Reconfirmed(s_baseKey, 3), afterSecondReversed);
    }

    [Fact]
    public void Given_AnUnrecordedReversalAtTheSameHeight_When_AskingForTheKey_Then_TheLaterConfirmationStillStands()
    {
        // Arrange: a fact from before the feed was reversed at 101, then recorded at 101 once more
        var standIn = Event(s_baseKey, 101);
        var unrecorded = AccountingConfirmations.CreateReversal(standIn, s_at, 100, unrecorded: true);
        var recorded = Event(s_baseKey, 101);

        // Act
        var beforeRecorded = AccountingConfirmations.NextConfirmationKey(s_baseKey, [unrecorded]);
        var afterRecorded = AccountingConfirmations.NextConfirmationKey(s_baseKey, [unrecorded, recorded]);

        // Assert
        Assert.Equal(s_baseKey, beforeRecorded);
        Assert.Null(afterRecorded);
        Assert.False(AccountingConfirmations.IsReversed(recorded, new HashSet<string> { unrecorded.EventKey }));
    }

    [Fact]
    public void Given_AnOnchainEvent_When_Reversed_Then_TheReversalNegatesItUnderItsReversalKey()
    {
        // Arrange
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var original = new AccountingEventModel
        {
            EventKey = s_baseKey,
            Kind = AccountingEventKind.WalletSent,
            OccurredAt = s_at,
            BlockHeight = 812_345,
            ChannelId = channelId,
            TxId = s_txId,
            OutputIndex = 1,
            AmountMsat = -60_000_000,
            FeeMsat = 1_000_000,
            Finality = AccountingFinality.Confirmed
        };

        // Act
        var reversal = AccountingConfirmations.CreateReversal(original, s_at.AddMinutes(5), 812_340);

        // Assert
        Assert.Equal(AccountingEventKeys.Reversal(s_baseKey, 812_345), reversal.EventKey);
        Assert.Equal(AccountingEventKind.Reversal, reversal.Kind);
        Assert.Equal(812_345u, reversal.BlockHeight);
        Assert.Equal(60_000_000, reversal.AmountMsat);
        Assert.Equal(-1_000_000, reversal.FeeMsat);
        Assert.Equal(channelId, reversal.ChannelId);
        Assert.Equal(s_txId, reversal.TxId);
        Assert.Equal(1u, reversal.OutputIndex);
        Assert.Equal(s_at.AddMinutes(5), reversal.OccurredAt);
        Assert.Equal(s_baseKey, reversal.Details[AccountingConfirmations.ReversesDetail]);
        Assert.Equal(nameof(AccountingEventKind.WalletSent),
                     reversal.Details[AccountingConfirmations.OriginalKindDetail]);
        Assert.Equal("812340", reversal.Details[AccountingConfirmations.ForkHeightDetail]);
        Assert.False(reversal.Details.ContainsKey(AccountingConfirmations.UnrecordedDetail));
        Assert.True(AccountingConfirmations.IsReversed(original, new HashSet<string> { reversal.EventKey }));
    }

    [Fact]
    public void Given_AnEventWithoutABlockHeight_When_Reversed_Then_ItThrows()
    {
        // Arrange
        var offChain = new AccountingEventModel
        {
            EventKey = "inv:x:settled",
            Kind = AccountingEventKind.InvoiceSettled,
            OccurredAt = s_at
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => AccountingConfirmations.CreateReversal(offChain, s_at, 1));
    }

    [Fact]
    public void Given_ConfirmationsAndReversals_When_FindingTheStandingOne_Then_ItIsTheLatestNotReversed()
    {
        // Arrange (NL-613): confirmed at 101, reorged, confirmed again at 103
        var first = Event(s_baseKey, 101);
        var second = Event(AccountingEventKeys.Reconfirmed(s_baseKey, 2), 103);
        var events = new List<AccountingEventModel> { first, AccountingConfirmations.CreateReversal(first, s_at, 100) };

        // Act
        var none = AccountingConfirmations.FindStanding(s_baseKey, events);
        events.Add(second);
        var standing = AccountingConfirmations.FindStanding(s_baseKey, events);
        var atFirstHeight = AccountingConfirmations.FindAt(s_baseKey, 101, events);
        var atOtherHeight = AccountingConfirmations.FindAt(s_baseKey, 107, events);

        // Assert
        Assert.Null(none);
        Assert.Same(second, standing);
        Assert.Same(first, atFirstHeight);
        Assert.Same(second, atOtherHeight);
    }

    [Fact]
    public void Given_AFormerReemissionThatStands_When_AskingForTheKey_Then_ThereIsNoneAndItIsTheStandingOne()
    {
        // Arrange (NL-613): a resolution writer's re-emission from before the generation scheme (`{key}:re:{height}`)
        var first = Event(s_baseKey, 101);
        var reemitted = Event(s_baseKey + ":re:103", 103);
        var events = new List<AccountingEventModel>
        {
            first, AccountingConfirmations.CreateReversal(first, s_at, 100), reemitted
        };

        // Act
        var key = AccountingConfirmations.NextConfirmationKey(s_baseKey, events);
        var standing = AccountingConfirmations.FindStanding(s_baseKey, events);
        events.Add(AccountingConfirmations.CreateReversal(reemitted, s_at, 102));
        var afterReversal = AccountingConfirmations.NextConfirmationKey(s_baseKey, events);

        // Assert
        Assert.Null(key);
        Assert.Same(reemitted, standing);
        Assert.Equal(AccountingEventKeys.Reconfirmed(s_baseKey, 2), afterReversal);
        Assert.False(AccountingConfirmations.IsConfirmationKey(s_baseKey, s_baseKey + ":rev:101"));
        Assert.False(AccountingConfirmations.IsConfirmationKey(s_baseKey, s_baseKey + ":c"));
        Assert.True(AccountingConfirmations.IsConfirmationKey(s_baseKey, s_baseKey + ":c12"));
    }

    private static AccountingEventModel Event(string key, uint height) => new()
    {
        EventKey = key,
        Kind = AccountingEventKind.WalletReceived,
        OccurredAt = s_at,
        BlockHeight = height,
        TxId = s_txId,
        OutputIndex = 1,
        AmountMsat = 50_000_000,
        Finality = AccountingFinality.Confirmed
    };
}