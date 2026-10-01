using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Onchain.Reorg;

using Application.Channels.Services;
using Application.Onchain.Reorg;
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