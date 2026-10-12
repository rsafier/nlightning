using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Integration.Tests.Persistence;

using Application.Accounting;
using Application.Accounting.Books;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Memory;

/// <summary>
/// The reconcile's channels line while a settle is in flight (NL-886) on the real SQLite schema with the production
/// books, sealer, posting rules and snapshot source: the settle is booked (an invoice in our fulfill's save, a payment or
/// a forward when the peer's fulfill is handled) before the commitment dance folds the HTLC into the channel's gross
/// balance, so the HTLC's amount is outstanding, never drift, per channel and in both directions; once folded it nets
/// to zero; what the HTLCs in flight do not explain is still drift (and logged).
/// </summary>
public sealed class AccountingReconcileHtlcSettleTests : IAsyncLifetime
{
    private const long OpeningMsat = 600_000_000;
    private const ulong NextHtlcId = 10;

    private static readonly DateTimeOffset s_at = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Secret s_preimage = new(Enumerable.Repeat((byte)0x3c, 32).ToArray());
    private static readonly Hash s_hash = new(SHA256.HashData((byte[])s_preimage));

    private readonly List<ChannelModel> _loaded = [];
    private readonly RecordingLogger _logger = new();
    private SqliteDbTestContext? _db;
    private ServiceProvider? _provider;

    private SqliteDbTestContext Db => _db ?? throw new InvalidOperationException("Not initialized");

    private ServiceProvider Provider => _provider ?? throw new InvalidOperationException("Not initialized");

    public async ValueTask InitializeAsync()
    {
        _db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddScoped<IUnitOfWork>(_ => CreateUnitOfWork());
        _provider = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        if (_db is not null)
            await _db.DisposeAsync();
    }

    [Fact]
    public async Task Given_AnInvoiceSettledWhileItsFulfillIsNotCommitted_When_Reconciled_Then_ItIsOutstandingUntilFolded()
    {
        // Arrange: 20,000 sat of our invoice locked in on channel 1; our fulfill sent and the invoice settled in its
        // save (the live case of 2026-10-03: a reconcile a second later showed the channels drifting by 20,000,000)
        var channel = await AddChannelAsync(1, Incoming(0, 20_000_000));
        await AddEventsAsync(Opening(channel), Settle(AccountingEventKind.InvoiceSettled,
                                                      AccountingEventKeys.InvoiceSettled(s_hash), 20_000_000));
        Load(channel, OpeningMsat, Incoming(0, 20_000_000, HtlcState.SentRemoveHtlc, HtlcRemoval.Fulfill(s_preimage)));
        await using var harness = await CreateBooksAsync();

        // Act: the fulfill in flight
        var inFlight = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: books 620M, node 600M, the 20M outstanding; clean and no warning
        Assert.True(inFlight.IsClean);
        Assert.Equal((620_000_000L, 600_000_000L, 20_000_000L, 0L), Channels(inFlight));
        Assert.Empty(_logger.Warnings);

        // Act: the dance folds the HTLC into our balance
        Load(channel, OpeningMsat + 20_000_000);
        var folded = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: nothing outstanding any more
        Assert.True(folded.IsClean);
        Assert.Equal((620_000_000L, 620_000_000L, 0L, 0L), Channels(folded));
        Assert.Empty(_logger.Warnings);
    }

    [Fact]
    public async Task Given_AnHtlcTheInterceptorSettledNotCommittedYet_When_Reconciled_Then_ItIsOutstandingUntilFolded()
    {
        // Arrange (NL-1182): a held forward of 20,001,000 msat on channel 1 settled by the HTLC interceptor: our
        // fulfill sent with its InterceptedHtlcSettled in the same save, no outgoing leg, no invoice of ours
        var channel = await AddChannelAsync(1, Incoming(4, 20_001_000));
        await AddEventsAsync(Opening(channel),
                             Settle(AccountingEventKind.InterceptedHtlcSettled,
                                    AccountingEventKeys.InterceptedHtlcSettled(channel.ChannelId, 4), 20_001_000));
        Load(channel, OpeningMsat, Incoming(4, 20_001_000, HtlcState.SentRemoveHtlc, HtlcRemoval.Fulfill(s_preimage)));
        await using var harness = await CreateBooksAsync();

        // Act: the fulfill in flight
        var inFlight = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: the books hold the HTLC as received, the node not yet: outstanding, no drift
        Assert.True(inFlight.IsClean);
        Assert.Equal((620_001_000L, 600_000_000L, 20_001_000L, 0L), Channels(inFlight));
        Assert.Empty(_logger.Warnings);

        // Act: the dance folds the HTLC into our balance
        Load(channel, OpeningMsat + 20_001_000);
        var folded = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: the books and the node agree, nothing outstanding
        Assert.True(folded.IsClean);
        Assert.Equal((620_001_000L, 620_001_000L, 0L, 0L), Channels(folded));
        Assert.Empty(_logger.Warnings);
    }

    [Fact]
    public async Task Given_AnInterceptedSettleWithoutItsEvent_When_Folded_Then_TheReconcileReportsTheDrift()
    {
        // Arrange (NL-1182, the gap before the fix): the interceptor's fulfill folded with no event in the feed
        var channel = await AddChannelAsync(1, Incoming(4, 20_001_000));
        await AddEventsAsync(Opening(channel));
        Load(channel, OpeningMsat + 20_001_000);
        await using var harness = await CreateBooksAsync();

        // Act
        var result = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: the unexplained balance move is drift
        Assert.False(result.IsClean);
        Assert.Equal((600_000_000L, 620_001_000L, 0L, -20_001_000L), Channels(result));
    }

    [Fact]
    public async Task Given_TheSettleFoldedAfterItsEventCommittedButBeforeItWasSealed_When_Reconciled_Then_NothingDrifts()
    {
        // Arrange: the settle's save and the whole commitment dance land while the reconcile runs, before its snapshot
        // (the books had not sealed the event yet: the reconcile must seal and project after its snapshot)
        var channel = await AddChannelAsync(1, Incoming(0, 20_000_000));
        await AddEventsAsync(Opening(channel));
        Load(channel, OpeningMsat, Incoming(0, 20_000_000));
        await using var harness = await CreateBooksAsync(beforeSnapshot: async () =>
        {
            await AddEventsAsync(Settle(AccountingEventKind.InvoiceSettled, AccountingEventKeys.InvoiceSettled(s_hash),
                                        20_000_000));
            Load(channel, OpeningMsat + 20_000_000);
        });

        // Act
        var result = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.IsClean);
        Assert.Equal((620_000_000L, 620_000_000L, 0L, 0L), Channels(result));
        Assert.Empty(_logger.Warnings);
    }

    [Fact]
    public async Task Given_TheSettleSavedRightAfterTheSnapshot_When_Reconciled_Then_TheHtlcAsSnapshottedIsOutstanding()
    {
        // Arrange: the snapshot sees the HTLC locked in without its fulfill; the settle's save commits before the
        // reconcile seals (the marker on the record is not required)
        var channel = await AddChannelAsync(1, Incoming(0, 20_000_000));
        await AddEventsAsync(Opening(channel));
        Load(channel, OpeningMsat, Incoming(0, 20_000_000));
        await using var harness = await CreateBooksAsync(afterSnapshot: async () =>
        {
            await AddEventsAsync(Settle(AccountingEventKind.InvoiceSettled, AccountingEventKeys.InvoiceSettled(s_hash),
                                        20_000_000));
            Load(channel, OpeningMsat,
                 Incoming(0, 20_000_000, HtlcState.SentRemoveHtlc, HtlcRemoval.Fulfill(s_preimage)));
        });

        // Act
        var result = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.IsClean);
        Assert.Equal((620_000_000L, 600_000_000L, 20_000_000L, 0L), Channels(result));
    }

    [Fact]
    public async Task Given_OurSplitPaymentSucceededWithAPartNotFulfilledYet_When_Reconciled_Then_EveryPartIsOutstanding()
    {
        // Arrange: we pay 20,000 sat plus a 10 sat fee in two parts on channel 1; the payee fulfilled the first part
        // (the payment is booked as succeeded), the second is still waiting for its fulfill
        var channel = await AddChannelAsync(1, Outgoing(0, 12_000_000), Outgoing(1, 8_010_000));
        await SetOriginAsync(channel, 0, HtlcOrigin.Local(s_hash));
        await SetOriginAsync(channel, 1, HtlcOrigin.Local(s_hash));
        await AddEventsAsync(Opening(channel),
                             Settle(AccountingEventKind.PaymentSucceeded, AccountingEventKeys.PaymentSucceeded(s_hash),
                                    -20_010_000, 10_000));
        Load(channel, OpeningMsat, Outgoing(0, 12_000_000, HtlcState.RcvdRemoveHtlc, HtlcRemoval.Fulfill(s_preimage)),
             Outgoing(1, 8_010_000));
        await using var harness = await CreateBooksAsync();

        // Act
        var inFlight = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: the node still counts both parts in our gross balance; the books took them out
        Assert.True(inFlight.IsClean);
        Assert.Equal((579_990_000L, 600_000_000L, -20_010_000L, 0L), Channels(inFlight));
        Assert.Empty(_logger.Warnings);

        // Act: the first part is folded, the second fulfilled
        Load(channel, OpeningMsat - 12_000_000,
             Outgoing(1, 8_010_000, HtlcState.RcvdRemoveHtlc, HtlcRemoval.Fulfill(s_preimage)));
        var oneFolded = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(oneFolded.IsClean);
        Assert.Equal((579_990_000L, 588_000_000L, -8_010_000L, 0L), Channels(oneFolded));
    }

    [Fact]
    public async Task Given_AForwardSettledDownstreamBeforeUpstream_When_Reconciled_Then_EachChannelCountsItsOwnHtlc()
    {
        // Arrange: 20,001,000 msat in on channel 1, 20,000,000 out on channel 2 (fee 1,000)
        var incoming = await AddChannelAsync(1, Incoming(4, 20_001_000));
        var outgoing = await AddChannelAsync(2, Outgoing(7, 20_000_000));
        await SetOriginAsync(outgoing, 7, HtlcOrigin.Forwarded(incoming.ChannelId, 4));
        await AddEventsAsync(Opening(incoming), Opening(outgoing));
        Load(incoming, OpeningMsat, Incoming(4, 20_001_000));
        Load(outgoing, OpeningMsat, Outgoing(7, 20_000_000, HtlcState.RcvdRemoveHtlc, HtlcRemoval.Fulfill(s_preimage)));
        await using var harness = await CreateBooksAsync();

        // Act: the downstream fulfill received, the forward not booked yet
        var notBooked = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: nothing booked, nothing outstanding
        Assert.True(notBooked.IsClean);
        Assert.Equal((1_200_000_000L, 1_200_000_000L, 0L, 0L), Channels(notBooked));

        // Act: the forward is booked while the upstream fulfill could not be sent (the upstream link is down)
        await AddEventsAsync(Settle(AccountingEventKind.ForwardSettled,
                                    AccountingEventKeys.ForwardSettled(incoming.ChannelId, 4), 1_000));
        var downstreamOnly = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: +20,001,000 for the upstream HTLC on channel 1, -20,000,000 for the downstream one on channel 2
        Assert.True(downstreamOnly.IsClean);
        Assert.Equal((1_200_001_000L, 1_200_000_000L, 1_000L, 0L), Channels(downstreamOnly));

        // Act: the upstream is fulfilled and folded first, the downstream not folded yet
        Load(incoming, OpeningMsat + 20_001_000);
        var upstreamFolded = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: only the downstream HTLC, on its own channel
        Assert.True(upstreamFolded.IsClean);
        Assert.Equal((1_200_001_000L, 1_220_001_000L, -20_000_000L, 0L), Channels(upstreamFolded));

        // Act: both folded
        Load(outgoing, OpeningMsat - 20_000_000);
        var folded = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(folded.IsClean);
        Assert.Equal((1_200_001_000L, 1_200_001_000L, 0L, 0L), Channels(folded));
        Assert.Empty(_logger.Warnings);
    }

    [Fact]
    public async Task Given_AForwardFoldedDownstreamWhileTheUpstreamLinkStaysDown_When_Reconciled_Then_NothingDrifts()
    {
        // Arrange: 20,001,000 msat in on channel 1, 20,000,000 out on channel 2 (fee 1,000); the downstream fulfill is
        // folded past its revoke_and_ack, the upstream fulfill was never sent (the upstream peer is offline), so the
        // incoming HTLC has neither a known preimage nor a fulfill removal (review finding on NL-886)
        var incoming = await AddChannelAsync(1, Incoming(4, 20_001_000));
        var outgoing = await AddChannelAsync(2, Outgoing(7, 20_000_000));
        await SetOriginAsync(outgoing, 7, HtlcOrigin.Forwarded(incoming.ChannelId, 4));
        await AddEventsAsync(Opening(incoming), Opening(outgoing),
                             Settle(AccountingEventKind.ForwardSettled,
                                    AccountingEventKeys.ForwardSettled(incoming.ChannelId, 4), 1_000));
        Load(incoming, OpeningMsat, Incoming(4, 20_001_000));
        Load(outgoing, OpeningMsat - 20_000_000);
        await using var harness = await CreateBooksAsync();

        // Act: reconciled twice, as the periodic loop would while the upstream peer stays away
        var first = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);
        var second = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: the upstream HTLC holds the whole forward as outstanding, nothing drifts
        Assert.True(first.IsClean);
        Assert.Equal((1_200_001_000L, 1_180_000_000L, 20_001_000L, 0L), Channels(first));
        Assert.True(second.IsClean);
        Assert.Equal((1_200_001_000L, 1_180_000_000L, 20_001_000L, 0L), Channels(second));
        Assert.Empty(_logger.Warnings);
    }

    [Fact]
    public async Task Given_ATrampolineRelaySettledDownstreamFirst_When_Reconciled_Then_EachChannelCountsItsOwnHtlc()
    {
        // Arrange: a trampoline relay (NL-875): 20,001,000 msat in on channel 1, 20,000,000 out on channel 2 with the
        // trampoline origin; the relay's settle is booked, the downstream fulfill folded, the upstream one not yet
        var incoming = await AddChannelAsync(1, Incoming(4, 20_001_000));
        var outgoing = await AddChannelAsync(2, Outgoing(7, 20_000_000));
        await SetOriginAsync(outgoing, 7, HtlcOrigin.Trampoline(s_hash));
        await AddEventsAsync(Opening(incoming), Opening(outgoing),
                             Settle(AccountingEventKind.TrampolineRelaySettled,
                                    AccountingEventKeys.TrampolineRelaySettled(s_hash), 1_000));
        Load(incoming, OpeningMsat, Incoming(4, 20_001_000));
        Load(outgoing, OpeningMsat, Outgoing(7, 20_000_000, HtlcState.RcvdRemoveHtlc, HtlcRemoval.Fulfill(s_preimage)));
        await using var harness = await CreateBooksAsync();

        // Act
        var bothInFlight = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);
        Load(outgoing, OpeningMsat - 20_000_000);
        var downstreamFolded = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(bothInFlight.IsClean);
        Assert.Equal((1_200_001_000L, 1_200_000_000L, 1_000L, 0L), Channels(bothInFlight));
        Assert.True(downstreamFolded.IsClean);
        Assert.Equal((1_200_001_000L, 1_180_000_000L, 20_001_000L, 0L), Channels(downstreamFolded));
        Assert.Empty(_logger.Warnings);
    }

    [Fact]
    public async Task Given_ARealDriftBesideASettleInFlight_When_Reconciled_Then_OnlyTheDriftIsReportedAndLogged()
    {
        // Arrange: our invoice's fulfill in flight on channel 1, and 1,234 msat the books hold that the node never
        // had (a settle booked twice under another key)
        var channel = await AddChannelAsync(1, Incoming(0, 20_000_000));
        await AddEventsAsync(Opening(channel),
                             Settle(AccountingEventKind.InvoiceSettled, AccountingEventKeys.InvoiceSettled(s_hash),
                                    20_000_000),
                             Settle(AccountingEventKind.PushReceived, "test:unexplained", 1_234));
        Load(channel, OpeningMsat, Incoming(0, 20_000_000, HtlcState.SentRemoveHtlc, HtlcRemoval.Fulfill(s_preimage)));
        await using var harness = await CreateBooksAsync();

        // Act
        var result = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: the HTLC is outstanding, the rest is drift and is logged
        Assert.False(result.IsClean);
        Assert.Equal((620_001_234L, 600_000_000L, 20_000_000L, 1_234L), Channels(result));
        Assert.Contains(_logger.Warnings, w => w.Contains("Channels drifts by 1234 msat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Given_ABookedSettleWhoseHtlcIsBeingFailed_When_Reconciled_Then_ItIsDrift()
    {
        // Arrange: the books say our invoice was paid, but its only HTLC is being failed back
        var channel = await AddChannelAsync(1, Incoming(0, 20_000_000));
        await AddEventsAsync(Opening(channel), Settle(AccountingEventKind.InvoiceSettled,
                                                      AccountingEventKeys.InvoiceSettled(s_hash), 20_000_000));
        Load(channel, OpeningMsat, Incoming(0, 20_000_000, HtlcState.SentRemoveHtlc, HtlcRemoval.Fail(new byte[] { 1 })));
        await using var harness = await CreateBooksAsync();

        // Act
        var result = await harness.Books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.IsClean);
        Assert.Equal((620_000_000L, 600_000_000L, 0L, 20_000_000L), Channels(result));
        Assert.Single(_logger.Warnings);
    }

    private static (long Books, long Node, long Outstanding, long Drift) Channels(AccountingReconcileResult result)
    {
        var line = result.Lines.Single(l => l.Account == AccountRole.Channels);
        return (line.BooksMsat, line.NodeMsat, line.OutstandingMsat, line.DriftMsat);
    }

    private static HtlcRecord Incoming(ulong id, ulong amountMsat, HtlcState state = HtlcState.RcvdAddAckRevocation,
                                       HtlcRemoval? removal = null) =>
        new(HtlcDirection.Incoming, id, amountMsat, s_hash, 600, state, removal, new byte[1366]);

    private static HtlcRecord Outgoing(ulong id, ulong amountMsat, HtlcState state = HtlcState.SentAddAckRevocation,
                                       HtlcRemoval? removal = null) =>
        new(HtlcDirection.Outgoing, id, amountMsat, s_hash, 600, state, removal, new byte[1366]);

    private static ChannelCommitments State(ChannelModel channel, long localMsat, params HtlcRecord[] htlcs)
    {
        const ulong fundingSat = 1_000_000;
        var party = new CommitmentParty(546, 10_000, 1_000, 30, ulong.MaxValue);
        var @params = new CommitmentParams(channel.IsInitiator, fundingSat, false, party, party);
        var local = (ulong)localMsat;
        var remote = fundingSat * 1_000 - local;
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[1] = 0x20;
        CompactPubKey point = bytes;
        var localSpec = new CommitmentSpec(CommitmentSide.Local, 2_500, local, remote, []);
        var remoteSpec = new CommitmentSpec(CommitmentSide.Remote, 2_500, local, remote, []);
        var feeOwner = channel.IsInitiator ? HtlcState.SentAddAckRevocation : HtlcState.RcvdAddAckRevocation;
        return ChannelCommitments.Restore(channel.ChannelId, @params, local, remote, htlcs,
                                          [new FeeUpdate(0, 2_500, feeOwner)], NextHtlcId, NextHtlcId,
                                          new LocalCommit(1, localSpec, null),
                                          new RemoteCommit(1, remoteSpec, point), null, point);
    }

    /// <summary>The channel's state in memory, as the snapshot reads it.</summary>
    private void Load(ChannelModel channel, long localMsat, params HtlcRecord[] htlcs)
    {
        channel.UpdateCommitments(State(channel, localMsat, htlcs));
        if (!_loaded.Contains(channel))
            _loaded.Add(channel);
    }

    /// <summary>A channel row with its HTLC rows (the origins of outgoing HTLCs are stored on them).</summary>
    private async Task<ChannelModel> AddChannelAsync(byte tag, params HtlcRecord[] htlcs)
    {
        var channel = SqliteDbTestContext.CreateChannel(true, channelTag: tag);
        channel.ShortChannelId = new ShortChannelId(100, tag, 0);
        using var uow = CreateUnitOfWork();
        await uow.ChannelDbRepository.AddAsync(channel);
        await uow.ChannelStateDbRepository.InitializeAsync(State(channel, OpeningMsat, htlcs));
        await uow.SaveChangesAsync();
        return channel;
    }

    private async Task SetOriginAsync(ChannelModel channel, ulong htlcId, HtlcOrigin origin)
    {
        using var uow = CreateUnitOfWork();
        await uow.ChannelStateDbRepository.SetHtlcOriginAsync(channel.ChannelId,
                                                               new HtlcKey(HtlcDirection.Outgoing, htlcId), origin);
        await uow.SaveChangesAsync();
    }

    private static AccountingEventModel Opening(ChannelModel channel) =>
        Settle(AccountingEventKind.PushReceived, AccountingEventKeys.Push(channel.ChannelId), OpeningMsat);

    private static AccountingEventModel Settle(AccountingEventKind kind, string key, long amountMsat,
                                               long feeMsat = 0) =>
        new()
        {
            EventKey = key,
            Kind = kind,
            OccurredAt = s_at,
            PaymentHash = s_hash,
            AmountMsat = amountMsat,
            FeeMsat = feeMsat,
            Finality = AccountingFinality.Final
        };

    private async Task AddEventsAsync(params AccountingEventModel[] events)
    {
        using var uow = CreateUnitOfWork();
        foreach (var accountingEvent in events)
            uow.AccountingEventDbRepository.Add(accountingEvent);
        await uow.SaveChangesAsync();
    }

    private async Task<BooksHarness> CreateBooksAsync(Func<Task>? beforeSnapshot = null,
                                                      Func<Task>? afterSnapshot = null)
    {
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
              .Returns((Func<ChannelModel, bool> predicate) => _loaded.Where(predicate).ToList());
        var scopes = Provider.GetRequiredService<IServiceScopeFactory>();
        var source = new HookedSnapshotSource(new NodeSnapshotSource(memory.Object, new UtxoMemoryRepository(), scopes),
                                              beforeSnapshot, afterSnapshot);
        var options = Options.Create(new AccountingOptions
        {
            SealInterval = TimeSpan.FromHours(1),
            SnapshotInterval = TimeSpan.FromHours(1)
        });
        var sealer = new AccountingEventSealerService(scopes, NullLogger<AccountingEventSealerService>.Instance,
                                                      options);
        var books = new AccountingBooksService(scopes, _logger, options, sealer, source);

        // Everything committed so far is projected before the case starts (the background rounds)
        await books.ProjectNowAsync(TestContext.Current.CancellationToken);
        return new BooksHarness(sealer, books);
    }

    private UnitOfWork CreateUnitOfWork() =>
        new(Db.CreateDbContext(), NullLogger<UnitOfWork>.Instance, Db.Sha256, new UtxoMemoryRepository(),
            TimeProvider.System);

    private sealed class BooksHarness(AccountingEventSealerService sealer, AccountingBooksService books)
        : IAsyncDisposable
    {
        public AccountingBooksService Books => books;

        public async ValueTask DisposeAsync()
        {
            await books.DisposeAsync();
            await sealer.DisposeAsync();
        }
    }

    /// <summary>The production snapshot source with a hook on each side of the reading (a save landing then).</summary>
    private sealed class HookedSnapshotSource(INodeSnapshotSource inner, Func<Task>? before, Func<Task>? after)
        : INodeSnapshotSource
    {
        public async Task<AccountingSnapshot> TakeSnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (before is not null)
                await before();

            var snapshot = await inner.TakeSnapshotAsync(cancellationToken);
            if (after is not null)
                await after();

            return snapshot;
        }
    }

    private sealed class RecordingLogger : ILogger<AccountingBooksService>
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IReadOnlyList<string> Warnings => _warnings.ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                _warnings.Enqueue(formatter(state, exception));
        }
    }
}