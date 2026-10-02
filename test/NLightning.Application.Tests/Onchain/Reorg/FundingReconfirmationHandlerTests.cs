using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Accounting;

namespace NLightning.Application.Tests.Onchain.Reorg;

using Application.Channels.Services;
using Application.Onchain.Reorg;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-138: a funding transaction that confirms again after a reorg moves the channel's short channel id on the shared
/// model, and that move only reaches the subscribers (the channel update service's channel_update switch, the backup
/// monitor's SCB) when the handler publishes it with UpdateChannel.
/// </summary>
public sealed class FundingReconfirmationHandlerTests
{
    [Fact]
    public async Task Given_AnOpenChannelWhoseFundingConfirmedAgain_When_TheWatchIsHandled_Then_TheMoveIsPublished()
    {
        // Arrange: the guard is on, so an unpublished mutation would make the lookup at the end throw
        var memory = new ChannelMemoryRepository(NullLogger<ChannelMemoryRepository>.Instance)
        {
            DetectUnpublishedMutations = true
        };
        var fundingTxId = TxIdOf(0x01);
        var channelId = ChannelIdOf(0x02);
        var channel = CreateChannel(channelId, Peer(3), fundingTxId);
        memory.AddChannel(channel);
        var updates = new List<ChannelModel>();
        memory.OnChannelUpdated += (_, args) => updates.Add(args.Channel);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository)
                  .Returns(ChannelDbRepositoryOf(CreateChannel(channelId, Peer(3), fundingTxId)));
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IUnitOfWork))).Returns(unitOfWork.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(() =>
        {
            var scope = new Mock<IServiceScope>();
            scope.SetupGet(s => s.ServiceProvider).Returns(serviceProvider.Object);
            return scope.Object;
        });

        var handler = new FundingReconfirmationHandler(new ChannelLockProvider(), memory, NullLogger.Instance,
                                                       scopeFactory.Object);
        var watch = new WatchedTransactionModel(channelId, fundingTxId, 6);
        watch.SetHeightAndIndex(910, 2);

        // Act
        var moved = await handler.HandleAsync(watch, TestContext.Current.CancellationToken);

        // Assert: the short channel id moved on the shared model and the subscribers were told (once)
        Assert.True(moved);
        Assert.Equal(new ShortChannelId(910, 2, 0), channel.ShortChannelId);
        Assert.Equal(910u, channel.FundingCreatedAtBlockHeight);
        var update = Assert.Single(updates);
        Assert.Same(channel, update);
        Assert.True(memory.TryGetChannel(channelId, out _));
    }

    [Fact]
    public async Task Given_AChannelAtTheWatchedPosition_When_TheWatchIsHandled_Then_NothingMoves()
    {
        // Arrange: the first confirmation is the channel manager's; a confirmation at the recorded position changes
        // nothing (the handler's remarks)
        var memory = new ChannelMemoryRepository(NullLogger<ChannelMemoryRepository>.Instance);
        var fundingTxId = TxIdOf(0x01);
        var channelId = ChannelIdOf(0x02);
        var channel = CreateChannel(channelId, Peer(3), fundingTxId);
        memory.AddChannel(channel);
        var updates = new List<ChannelModel>();
        memory.OnChannelUpdated += (_, args) => updates.Add(args.Channel);

        var handler = new FundingReconfirmationHandler(new ChannelLockProvider(), memory, NullLogger.Instance,
                                                       NoScopeFactory());
        var watch = new WatchedTransactionModel(channelId, fundingTxId, 6);
        watch.SetHeightAndIndex(800, 1);

        // Act
        var moved = await handler.HandleAsync(watch, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(moved);
        Assert.Empty(updates);
    }

    [Fact]
    public async Task Given_TheFundingsAccountingEvents_When_TheFundingConfirmedAgainElsewhere_Then_ReversedAndRecordedAtTheNewBlock()
    {
        // Arrange (NL-607): ChannelFunded and the push were recorded at the first block (800); the funding confirmed
        // again at 910 after a reorg
        var memory = new ChannelMemoryRepository(NullLogger<ChannelMemoryRepository>.Instance);
        var fundingTxId = TxIdOf(0x01);
        var channelId = ChannelIdOf(0x02);
        memory.AddChannel(CreateChannel(channelId, Peer(3), fundingTxId));
        var funded = Event(AccountingEventKeys.ChannelFunded(channelId, fundingTxId), AccountingEventKind.ChannelFunded,
                           channelId, fundingTxId, 100_000_000, 800);
        var push = Event(AccountingEventKeys.Push(channelId), AccountingEventKind.PushSent, channelId, fundingTxId,
                         -5_000_000, 800);
        var feed = new List<AccountingEventModel> { funded, push };
        var saves = new List<int>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository)
                  .Returns(ChannelDbRepositoryOf(CreateChannel(channelId, Peer(3), fundingTxId)));
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(FeedOf(feed));
        unitOfWork.Setup(u => u.SaveChangesAsync()).Callback(() => saves.Add(feed.Count)).Returns(Task.CompletedTask);
        var handler = new FundingReconfirmationHandler(new ChannelLockProvider(), memory, NullLogger.Instance,
                                                       ScopeFactoryOf(unitOfWork.Object));
        var watch = new WatchedTransactionModel(channelId, fundingTxId, 6);
        watch.SetHeightAndIndex(910, 2);

        // Act
        await handler.HandleAsync(watch, TestContext.Current.CancellationToken);
        await handler.HandleAsync(watch, TestContext.Current.CancellationToken);

        // Assert: both reversed and recorded again at 910 with the new short channel id, in the move's one save; the
        // second handling (same position) writes nothing
        Assert.Equal([6], saves);
        foreach (var original in new[] { funded, push })
        {
            var reversal = Assert.Single(feed, e => e.EventKey == AccountingEventKeys.Reversal(original.EventKey, 800));
            Assert.Equal(-original.AmountMsat, reversal.AmountMsat);
            var again = Assert.Single(feed, e => e.EventKey == AccountingEventKeys.Reconfirmed(original.EventKey, 2));
            Assert.Equal(original.Kind, again.Kind);
            Assert.Equal(original.AmountMsat, again.AmountMsat);
            Assert.Equal(910u, again.BlockHeight);
            Assert.Equal(new ShortChannelId(910, 2, 0), again.ShortChannelId);
        }

        var books = BooksSimulator.Of(feed);
        Assert.Equal(95_000_000, books[AccountRole.Channels]);
    }

    [Fact]
    public async Task Given_TheFundingsAccountingEvents_When_TheFundingConfirmedAgainAtTheSameHeightElsewhere_Then_TheyMoveToo()
    {
        // Arrange (NL-738): recorded at 800x1; the funding confirmed again at the same height, index 5
        var memory = new ChannelMemoryRepository(NullLogger<ChannelMemoryRepository>.Instance);
        var fundingTxId = TxIdOf(0x01);
        var channelId = ChannelIdOf(0x02);
        memory.AddChannel(CreateChannel(channelId, Peer(3), fundingTxId));
        var funded = Event(AccountingEventKeys.ChannelFunded(channelId, fundingTxId), AccountingEventKind.ChannelFunded,
                           channelId, fundingTxId, 100_000_000, 800);
        var feed = new List<AccountingEventModel> { funded };
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository)
                  .Returns(ChannelDbRepositoryOf(CreateChannel(channelId, Peer(3), fundingTxId)));
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(FeedOf(feed));
        var handler = new FundingReconfirmationHandler(new ChannelLockProvider(), memory, NullLogger.Instance,
                                                       ScopeFactoryOf(unitOfWork.Object));
        var watch = new WatchedTransactionModel(channelId, fundingTxId, 6);
        watch.SetHeightAndIndex(800, 5);

        // Act
        await handler.HandleAsync(watch, TestContext.Current.CancellationToken);

        // Assert: reversed and recorded again with the new short channel id
        Assert.Single(feed, e => e.Kind == AccountingEventKind.Reversal);
        var again = Assert.Single(feed, e => e.EventKey == AccountingEventKeys.Reconfirmed(funded.EventKey, 2));
        Assert.Equal(new ShortChannelId(800, 5, 0), again.ShortChannelId);
        Assert.Equal(funded.AmountMsat, BooksSimulator.Of(feed)[AccountRole.Channels]);
    }

    private static AccountingEventModel Event(string key, AccountingEventKind kind, ChannelId channelId, TxId txId,
                                              long amountMsat, uint height) => new()
                                              {
                                                  EventKey = key,
                                                  Kind = kind,
                                                  OccurredAt = DateTimeOffset.UnixEpoch,
                                                  BlockHeight = height,
                                                  ChannelId = channelId,
                                                  ShortChannelId = new ShortChannelId(height, 1, 0),
                                                  TxId = txId,
                                                  OutputIndex = 0,
                                                  AmountMsat = amountMsat,
                                                  Finality = AccountingFinality.Confirmed
                                              };

    /// <summary>A feed over <paramref name="events"/>: staged rows are visible at once, as in a unit of work.</summary>
    internal static IAccountingEventDbRepository FeedOf(List<AccountingEventModel> events)
    {
        var feed = new Mock<IAccountingEventDbRepository>();
        feed.Setup(f => f.Add(It.IsAny<AccountingEventModel>())).Callback<AccountingEventModel>(events.Add);
        feed.Setup(f => f.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => events.Any(e => e.EventKey == key));
        feed.Setup(f => f.GetByKeyPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string prefix, CancellationToken _) =>
                              events.Where(e => e.EventKey.StartsWith(prefix, StringComparison.Ordinal)).ToList());
        return feed.Object;
    }

    private static IServiceScopeFactory ScopeFactoryOf(IUnitOfWork unitOfWork)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IUnitOfWork))).Returns(unitOfWork);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(() =>
        {
            var scope = new Mock<IServiceScope>();
            scope.SetupGet(s => s.ServiceProvider).Returns(serviceProvider.Object);
            return scope.Object;
        });
        return scopeFactory.Object;
    }

    private static IServiceScopeFactory NoScopeFactory()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(() =>
        {
            var scope = new Mock<IServiceScope>();
            scope.SetupGet(s => s.ServiceProvider).Returns(Mock.Of<IServiceProvider>());
            return scope.Object;
        });
        return scopeFactory.Object;
    }

    private static IChannelDbRepository ChannelDbRepositoryOf(ChannelModel stored)
    {
        var repository = new Mock<IChannelDbRepository>();
        repository.Setup(r => r.GetByIdAsync(stored.ChannelId)).ReturnsAsync(stored);
        return repository.Object;
    }

    private static ChannelModel CreateChannel(ChannelId channelId, CompactPubKey peer, TxId fundingTxId)
    {
        var key = Peer(9);
        var keySet = new ChannelKeySetModel(0, key, key, key, key, key, key);
        return new ChannelModel(new ChannelParams(), channelId, null,
                                new FundingOutputInfo(LightningMoney.Satoshis(100_000), key, key, fundingTxId, 0),
                                true, null, null, LightningMoney.Satoshis(60_000), keySet, 0, 0,
                                LightningMoney.Satoshis(40_000), keySet, 0, peer, 0, ChannelState.Open,
                                ChannelVersion.V1)
        {
            ShortChannelId = new ShortChannelId(800, 1, 0)
        };
    }

    private static ChannelId ChannelIdOf(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static TxId TxIdOf(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static CompactPubKey Peer(byte seed)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = seed;
        return new CompactPubKey(bytes);
    }
}