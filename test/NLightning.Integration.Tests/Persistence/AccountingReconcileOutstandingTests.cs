using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Application.Accounting;
using Application.Accounting.Books;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Memory;

/// <summary>
/// The reconcile's outstanding clearing (NL-621) on the real SQLite schema with the production books, sealer and
/// posting rules: a splice-in whose wallet side is booked while it is below its depth (its watch pending), then past
/// its depth while the <c>splice_locked</c> exchange is not finished (its pending funding row), is outstanding, not
/// drift; once it locks it nets to zero.
/// </summary>
public sealed class AccountingReconcileOutstandingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

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
    public async Task Given_ASpliceBelowItsDepthThenAwaitingItsLock_When_Reconciled_Then_ItIsOutstandingUntilItLocks()
    {
        // Arrange: an open channel we fund; a splice-in of 160,500 sat paid from a 200,000 sat wallet output with
        // 39,000 sat of change (500 sat of fee), confirmed at 101, watched for its depth
        var channel = SqliteDbTestContext.CreateChannel(true, channelTag: 9);
        channel.ShortChannelId = new ShortChannelId(90, 1, 1);
        var deposit = new TxId(Enumerable.Repeat((byte)0xd1, 32).ToArray());
        var splice = Network.RegTest.CreateTransaction();
        splice.Inputs.Add(new OutPoint(new uint256(channel.FundingOutput!.TransactionId!.Value), 1));
        splice.Inputs.Add(new OutPoint(new uint256(deposit), 0));
        splice.Outputs.Add(Money.Satoshis(1_160_500), new Key().PubKey.WitHash.ScriptPubKey);
        splice.Outputs.Add(Money.Satoshis(39_000), new Key().PubKey.WitHash.ScriptPubKey);
        var spliceTxId = new TxId(splice.GetHash().ToBytes());

        using (var uow = CreateUnitOfWork())
        {
            await uow.ChannelDbRepository.AddAsync(channel);
            uow.BroadcastTransactionDbRepository.Add(new BroadcastTransactionModel(
                new SignedTransaction(spliceTxId, splice.ToBytes()), BroadcastPurpose.Splice, channel.ChannelId, 100));
            var watch = new WatchedTransactionModel(channel.ChannelId, spliceTxId, 6);
            watch.SetHeightAndIndex(101, 1);
            uow.WatchedTransactionDbRepository.Add(watch);
            await uow.SaveChangesAsync();
            await uow.BroadcastTransactionDbRepository.MarkConfirmedAsync(spliceTxId, 101, new Hash(new byte[32]));
            await uow.SaveChangesAsync();
        }

        await AddAsync(
            Wallet(AccountingEventKind.WalletReceived, AccountingEventKeys.WalletReceived(deposit, 0), 200_000_000, 90,
                   deposit, 0, AccountingDetailKeys.ExternalSource),
            Wallet(AccountingEventKind.WalletOutputSpent, AccountingEventKeys.WalletOutputSpent(deposit, 0),
                   -200_000_000, 101, deposit, 0, AccountingDetailKeys.BroadcastSource, spliceTxId),
            Wallet(AccountingEventKind.WalletReceived, AccountingEventKeys.WalletReceived(spliceTxId, 1), 39_000_000,
                   101, spliceTxId, 1, AccountingDetailKeys.BroadcastSource));
        var bucket = new ChannelBalanceBucket(channel.ChannelId, channel.ShortChannelId, ChannelState.Open,
                                              channel.RemoteNodeId, 1_000_000_000, 0, 0, 0, 0, 0, 0, 0, true);
        var snapshot = new AccountingSnapshot(s_at, 102, [bucket], new WalletBalanceBucket(0, 39_000_000, 0));
        var source = new Mock<INodeSnapshotSource>();
        source.Setup(s => s.TakeSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => snapshot);
        var scopes = Provider.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new AccountingOptions
        {
            SealInterval = TimeSpan.FromHours(1),
            SnapshotInterval = TimeSpan.FromHours(1)
        });
        await using var sealer = new AccountingEventSealerService(scopes,
                                                                  NullLogger<AccountingEventSealerService>.Instance,
                                                                  options);
        await using var books = new AccountingBooksService(scopes, NullLogger<AccountingBooksService>.Instance, options,
                                                           sealer, source.Object);

        // Act: below its depth
        var belowDepth = await books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: the whole clearing balance is the splice's, outstanding
        Assert.True(belowDepth.IsClean);
        Assert.Equal((161_000_000L, 161_000_000L, 0L), Clearing(belowDepth));

        // Act: past its depth (the watch completed) while splice_locked is not exchanged yet (its funding pending)
        using (var uow = CreateUnitOfWork())
        {
            await uow.WatchedTransactionDbRepository.DeleteByTransactionIdAsync(spliceTxId);
            await uow.ChannelFundingDbRepository.UpsertAsync(
                channel.ChannelId,
                new ChannelFunding(spliceTxId, 0, 1_160_500, SqliteDbTestContext.LocalFundingPubKey,
                                   SqliteDbTestContext.RemoteFundingPubKey, 1, 160_500_000, 0, ChannelFundingKind.Splice,
                                   ChannelFundingStatus.Pending, ConfirmedHeight: 101));
            await uow.SaveChangesAsync();
        }

        var awaitingLock = await books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: still outstanding, through the pending funding
        Assert.True(awaitingLock.IsClean);
        Assert.Equal((161_000_000L, 161_000_000L, 0L), Clearing(awaitingLock));

        // Act: the splice locks (its channel side is booked: the delta into the channel and our fee)
        await AddAsync(new AccountingEventModel
        {
            EventKey = AccountingEventKeys.SpliceLocked(channel.ChannelId, spliceTxId),
            Kind = AccountingEventKind.SpliceLocked,
            OccurredAt = s_at,
            BlockHeight = 107,
            ChannelId = channel.ChannelId,
            TxId = spliceTxId,
            AmountMsat = 160_500_000,
            FeeMsat = 500_000
        });
        snapshot = snapshot with { Channels = [bucket with { LocalBalanceMsat = 160_500_000 }] };
        var locked = await books.ReconcileAsync(TestContext.Current.CancellationToken);

        // Assert: nets to zero, nothing outstanding (the funding row still pending changes nothing)
        Assert.True(locked.IsClean);
        Assert.Equal((0L, 0L, 0L), Clearing(locked));
    }

    private static (long Books, long Outstanding, long Drift) Clearing(AccountingReconcileResult result)
    {
        var line = result.Lines.Single(l => l.Account == AccountRole.Clearing);
        return (line.BooksMsat, line.OutstandingMsat, line.DriftMsat);
    }

    private static AccountingEventModel Wallet(AccountingEventKind kind, string key, long amountMsat, uint height,
                                               TxId txId, uint vout, string source, TxId? spentBy = null) =>
        new()
        {
            EventKey = key,
            Kind = kind,
            OccurredAt = s_at,
            BlockHeight = height,
            TxId = txId,
            OutputIndex = vout,
            AmountMsat = amountMsat,
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create((AccountingDetailKeys.Source, source),
                                                    ("spentBy", spentBy?.ToString()))
        };

    private async Task AddAsync(params AccountingEventModel[] events)
    {
        using var uow = CreateUnitOfWork();
        foreach (var accountingEvent in events)
            uow.AccountingEventDbRepository.Add(accountingEvent);
        await uow.SaveChangesAsync();
    }

    private UnitOfWork CreateUnitOfWork() =>
        new(Db.CreateDbContext(), NullLogger<UnitOfWork>.Instance, Db.Sha256, new UtxoMemoryRepository(),
            TimeProvider.System);
}