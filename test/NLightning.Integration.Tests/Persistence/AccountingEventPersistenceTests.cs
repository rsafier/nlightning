namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Infrastructure.Repositories.Database.Accounting;

/// <summary>
/// Migration <c>AddAccountingEvents</c> (NL-602) on the real SQLite schema: events round trip with every field, the
/// sealer's decisions (dense ledger sequence, hash chain, duplicates) are stored, and readers page sealed rows in
/// ledger order.
/// </summary>
public class AccountingEventPersistenceTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 30, 15, 123, TimeSpan.Zero);

    [Fact]
    public async Task Given_AnEventWithEveryField_When_SavedSealedAndReloaded_Then_EveryFieldIsEqual()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var original = new AccountingEventModel
        {
            EventKey = AccountingEventKeys.InvoiceSettled(Hash(0x11)),
            Kind = AccountingEventKind.InvoiceSettled,
            OccurredAt = s_at.AddTicks(7),
            BlockHeight = 812_345,
            ChannelId = ChannelId.Zero,
            ShortChannelId = new ShortChannelId(812_000, 12, 1),
            PaymentHash = Hash(0x11),
            TxId = new TxId(Enumerable.Repeat((byte)0x22, 32).ToArray()),
            OutputIndex = 3,
            Counterparty = new CompactPubKey(Convert.FromHexString(
                                                 "02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619")),
            AmountMsat = 123_456_789,
            FeeMsat = 42,
            Finality = AccountingFinality.Confirmed,
            Flags = AccountingEventFlags.Backfilled,
            Details = AccountingDetailsCodec.Create(("description", "coffee"), ("kind", "bolt11"))
        };
        await using (var context = database.CreateContext())
        {
            new AccountingEventDbRepository(context).Add(original);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await SealAllAsync(database);
        IReadOnlyList<AccountingEventModel> listed;
        await using (var context = database.CreateContext())
            listed = await new AccountingEventDbRepository(context).ListAsync(new AccountingEventQuery(0, 10),
                                                                              TestContext.Current.CancellationToken);

        // Assert
        var reloaded = Assert.Single(listed);
        Assert.Equal(original.EventKey, reloaded.EventKey);
        Assert.Equal(original.Kind, reloaded.Kind);
        Assert.Equal(original.OccurredAt, reloaded.OccurredAt);
        Assert.Equal(original.BlockHeight, reloaded.BlockHeight);
        Assert.Equal(original.ChannelId, reloaded.ChannelId);
        Assert.Equal(original.ShortChannelId, reloaded.ShortChannelId);
        Assert.Equal(original.PaymentHash, reloaded.PaymentHash);
        Assert.Equal(original.TxId, reloaded.TxId);
        Assert.Equal(original.OutputIndex, reloaded.OutputIndex);
        Assert.Equal(original.Counterparty, reloaded.Counterparty);
        Assert.Equal(original.AmountMsat, reloaded.AmountMsat);
        Assert.Equal(original.FeeMsat, reloaded.FeeMsat);
        Assert.Equal(original.Finality, reloaded.Finality);
        Assert.Equal(original.Flags, reloaded.Flags);
        Assert.Equal(original.Details, reloaded.Details);
        Assert.Equal(1, reloaded.LedgerSeq);
        Assert.Equal(AccountingEventHasher.ComputeHash(new byte[32], 1, reloaded), reloaded.Hash);
    }

    [Fact]
    public async Task Given_RowsSavedInSeveralRounds_When_Sealed_Then_TheChainIsDenseAndDuplicatesAreMarked()
    {
        // Arrange: a first round sealed, then a second round with a repeated key
        using var database = new SqliteTestDatabase();
        await AddAsync(database, Event("a"), Event("b"));
        await SealAllAsync(database);
        await AddAsync(database, Event("a"), Event("c"), Event("c"));

        // Act
        await SealAllAsync(database);

        // Assert
        await using var context = database.CreateContext();
        var repository = new AccountingEventDbRepository(context);
        var sealedEvents = await repository.GetSealedRangeAsync(1, 100, TestContext.Current.CancellationToken);
        Assert.Equal(["a", "b", "c"], sealedEvents.Select(e => e.EventKey));
        Assert.Equal([1L, 2L, 3L], sealedEvents.Select(e => e.LedgerSeq!.Value));
        var previous = new byte[32];
        foreach (var sealedEvent in sealedEvents)
        {
            Assert.Equal(AccountingEventHasher.ComputeHash(previous, sealedEvent.LedgerSeq!.Value, sealedEvent),
                         sealedEvent.Hash);
            previous = sealedEvent.Hash!;
        }

        Assert.Empty(await repository.GetUnsealedAsync(10, TestContext.Current.CancellationToken));
        var tip = await repository.GetChainTipAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, tip.LedgerSeq);
        Assert.Equal(previous, tip.Hash);
        Assert.Equal(2, context.AccountingEvents.Count(e => e.LedgerSeq == null
                                                         && (e.Flags & (int)AccountingEventFlags.Duplicate) != 0));
    }

    [Fact]
    public async Task Given_AStagedEvent_When_ExistsIsAsked_Then_ItIsSeenBeforeAndAfterTheSave()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        await using var context = database.CreateContext();
        var repository = new AccountingEventDbRepository(context);

        // Act
        repository.Add(Event("staged"));
        var beforeSave = await repository.ExistsAsync("staged", TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using var otherContext = database.CreateContext();
        var afterSave = await new AccountingEventDbRepository(otherContext)
                           .ExistsAsync("staged", TestContext.Current.CancellationToken);
        var other = await repository.ExistsAsync("other", TestContext.Current.CancellationToken);

        // Assert
        Assert.True(beforeSave);
        Assert.True(afterSave);
        Assert.False(other);
    }

    [Fact]
    public async Task Given_EventsByKey_When_ReadBack_Then_StagedSavedAndFirstOfDuplicatesAreFound()
    {
        // Arrange: a key written twice (the second sealed as a duplicate), and a staged row
        using var database = new SqliteTestDatabase();
        var first = Event("dup");
        await AddAsync(database, first, new AccountingEventModel
        {
            EventKey = "dup",
            Kind = AccountingEventKind.InvoiceSettled,
            OccurredAt = s_at,
            AmountMsat = 2_000
        });
        await SealAllAsync(database);
        await using var context = database.CreateContext();
        var repository = new AccountingEventDbRepository(context);
        var ct = TestContext.Current.CancellationToken;

        // Act
        repository.Add(Event("staged"));
        var staged = await repository.GetByKeyAsync("staged", ct);
        var duplicate = await repository.GetByKeyAsync("dup", ct);
        var unknown = await repository.GetByKeyAsync("unknown", ct);

        // Assert
        Assert.Equal("staged", staged?.EventKey);
        Assert.Null(staged?.LedgerSeq);
        Assert.Equal(first.AmountMsat, duplicate?.AmountMsat);
        Assert.Equal(1, duplicate?.LedgerSeq);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task Given_SealedEvents_When_Listed_Then_TheQueryFiltersAndPagesInLedgerOrder()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var channel = new ChannelId(Enumerable.Repeat((byte)0x33, 32).ToArray());
        await AddAsync(database,
                       Event("p1", AccountingEventKind.PaymentSucceeded, s_at),
                       Event("f1", AccountingEventKind.ForwardSettled, s_at.AddMinutes(1), channel),
                       Event("p2", AccountingEventKind.PaymentSucceeded, s_at.AddMinutes(2)),
                       Event("f2", AccountingEventKind.ForwardSettled, s_at.AddMinutes(3), channel));
        await SealAllAsync(database);
        await using var context = database.CreateContext();
        var repository = new AccountingEventDbRepository(context);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var page = await repository.ListAsync(new AccountingEventQuery(1, 2), ct);
        var payments = await repository.ListAsync(
                           new AccountingEventQuery(0, 10, [AccountingEventKind.PaymentSucceeded]), ct);
        var ofChannel = await repository.ListAsync(new AccountingEventQuery(0, 10, ChannelId: channel), ct);
        var window = await repository.ListAsync(
                         new AccountingEventQuery(0, 10, Since: s_at.AddMinutes(1), Until: s_at.AddMinutes(3)), ct);

        // Assert
        Assert.Equal(["f1", "p2"], page.Select(e => e.EventKey));
        Assert.Equal(["p1", "p2"], payments.Select(e => e.EventKey));
        Assert.Equal(["f1", "f2"], ofChannel.Select(e => e.EventKey));
        Assert.Equal(["f1", "p2"], window.Select(e => e.EventKey));
    }

    [Fact]
    public async Task Given_WalletHistory_When_Queried_Then_OnlyStandingSealedEventsInRangeArePaged()
    {
        using var database = new SqliteTestDatabase();
        var txId = new TxId(Enumerable.Repeat((byte)0x44, 32).ToArray());
        AccountingEventModel Wallet(string key, uint height, long amount = 1_000) => new()
        {
            EventKey = key,
            Kind = AccountingEventKind.WalletReceived,
            OccurredAt = s_at,
            BlockHeight = height,
            TxId = txId,
            AmountMsat = amount
        };
        var reversed = Wallet("wallet:old", 100);
        await AddAsync(database,
                       Wallet("before", 99), reversed, Wallet("wallet:old:c2", 110, 2_000),
                       Wallet("other", 120), Wallet("after", 121),
                       Event("unrelated", AccountingEventKind.InvoiceSettled),
                       new AccountingEventModel
                       {
                           EventKey = "wallet:old:rev:100",
                           Kind = AccountingEventKind.Reversal,
                           OccurredAt = s_at,
                           BlockHeight = 999,
                           AmountMsat = -1_000,
                           Details = AccountingDetailsCodec.Create((AccountingConfirmations.ReversesDetail, reversed.EventKey))
                       });
        await SealAllAsync(database);
        // Neither an unsealed reversal nor an unsealed movement changes the sealed history.
        await AddAsync(database, Wallet("unsealed", 115), new AccountingEventModel
        {
            EventKey = "other:rev:120",
            Kind = AccountingEventKind.Reversal,
            OccurredAt = s_at,
            AmountMsat = -1_000
        });
        await using var context = database.CreateContext();
        var repository = new AccountingEventDbRepository(context);
        var ct = TestContext.Current.CancellationToken;

        var first = Assert.Single(await repository.GetWalletHistoryAsync(100, 120, 0, 1, ct));
        var second = Assert.Single(await repository.GetWalletHistoryAsync(100, 120, first.LedgerSeq!.Value, 1, ct));
        var done = await repository.GetWalletHistoryAsync(100, 120, second.LedgerSeq!.Value, 1, ct);

        Assert.Equal("wallet:old:c2", first.EventKey);
        Assert.Equal(2_000, first.AmountMsat);
        Assert.Equal("other", second.EventKey);
        Assert.Empty(done);
        Assert.Empty(await repository.GetWalletHistoryAsync(121, 100, 0, 10, ct));
    }

    [Fact]
    public async Task Given_LegacyAndExplicitReversals_When_WalletHistoryQueried_Then_ReferencesAreIndexed()
    {
        using var database = new SqliteTestDatabase();
        var events = new[] { "legacy", "explicit", "prefix", "prefix:c2" }.Select(key => new AccountingEventModel
        {
            EventKey = key,
            Kind = AccountingEventKind.WalletOutputSpent,
            OccurredAt = s_at,
            BlockHeight = 100,
            AmountMsat = -1_000
        }).ToArray();
        await AddAsync(database, events);
        await AddAsync(database,
                       new AccountingEventModel
                       {
                           EventKey = "legacy:rev:100",
                           Kind = AccountingEventKind.Reversal,
                           OccurredAt = s_at,
                           AmountMsat = 1_000
                       },
                       new AccountingEventModel
                       {
                           EventKey = "different:rev:100",
                           Kind = AccountingEventKind.Reversal,
                           OccurredAt = s_at,
                           AmountMsat = 1_000,
                           Details = AccountingDetailsCodec.Create((AccountingConfirmations.ReversesDetail, "explicit"))
                       },
                       new AccountingEventModel
                       {
                           EventKey = "prefix:rev:100",
                           Kind = AccountingEventKind.Reversal,
                           OccurredAt = s_at,
                           AmountMsat = 1_000
                       });
        await SealAllAsync(database);
        await using var context = database.CreateContext();
        var repository = new AccountingEventDbRepository(context);

        var standing = await repository.GetWalletHistoryAsync(100, 100, 0, 100, TestContext.Current.CancellationToken);

        Assert.Equal("prefix:c2", Assert.Single(standing).EventKey);
        Assert.Equal("legacy", context.AccountingEvents.Single(e => e.EventKey == "legacy:rev:100").ReversesEventKey);
        Assert.Equal("explicit", context.AccountingEvents.Single(e => e.EventKey == "different:rev:100").ReversesEventKey);
    }

    [Fact]
    public async Task Given_AHistoricalFeed_When_Upgraded_Then_ReversalReferencesAreBackfilledWithoutChangingSeals()
    {
        using var database = new SqliteTestDatabase();
        await AccountingHistoryUpgradeRoundTrip.AssertAsync(() => database.CreateContext(),
            Infrastructure.Persistence.Enums.DatabaseType.Sqlite, TestContext.Current.CancellationToken);
    }

    private static async Task AddAsync(SqliteTestDatabase database, params AccountingEventModel[] events)
    {
        await using var context = database.CreateContext();
        var repository = new AccountingEventDbRepository(context);
        foreach (var accountingEvent in events)
            repository.Add(accountingEvent);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task SealAllAsync(SqliteTestDatabase database)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = database.CreateContext();
        var repository = new AccountingEventDbRepository(context);
        var batch = await repository.GetUnsealedAsync(100, ct);
        var sealedKeys = await repository.GetSealedKeysAsync(batch.Select(e => e.EventKey).ToList(), ct);
        var seals = AccountingEventSealer.Seal(await repository.GetChainTipAsync(ct), batch, sealedKeys, out _);
        await repository.ApplySealsAsync(seals, ct);
        await context.SaveChangesAsync(ct);
    }

    private static AccountingEventModel Event(string key,
                                              AccountingEventKind kind = AccountingEventKind.InvoiceSettled,
                                              DateTimeOffset? at = null, ChannelId? channelId = null) =>
        new()
        {
            EventKey = key,
            Kind = kind,
            OccurredAt = at ?? s_at,
            ChannelId = channelId,
            AmountMsat = 1_000
        };

    private static Hash Hash(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());
}