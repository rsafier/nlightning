using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Close;

using Application.Channels.Close;
using Application.Channels.Managers;
using Application.Channels.Reestablish;
using Application.Channels.Services;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Handlers;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The close outside the negotiation (BOLT2 plan N10-T3, NL-036): the closing transaction's confirmation makes the
/// channel Closed and forgets it in memory; a restart in Closing rebroadcasts the stored transaction; ShuttingDown and
/// Negotiating channels are registered and send channel_reestablish when the peer connects.
/// </summary>
public class ClosingLifecycleTests
{
    private static readonly SignedTransaction s_closingTx =
        new(new TxId(Enumerable.Repeat((byte)0x3c, 32).ToArray()), CreateRawTx());

    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChannelDbRepository> _channelDb = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IBitcoinChainService> _chain = new();
    private readonly ClosingNegotiationRegistry _registry = new();
    private readonly List<ChannelState> _persisted = [];

    public ClosingLifecycleTests()
    {
        _unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_channelDb.Object);
        _channelDb.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>()))
                  .Callback((ChannelModel c) => _persisted.Add(c.State))
                  .Returns(Task.CompletedTask);
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
    }

    [Fact]
    public async Task Given_Closing_When_ClosingTransactionConfirmed_Then_ClosedAndForgotten()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.Closing);
        channel.SetClosingTransaction(s_closingTx);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        _registry.Get(channelId);
        CreateManager();

        // Act
        _monitor.Raise(m => m.OnTransactionConfirmed += null, _monitor.Object, Confirmed(channelId, s_closingTx.TxId));

        // Assert
        await WaitUntilAsync(() => channel.State == ChannelState.Closed);
        Assert.Equal([ChannelState.Closed], _persisted);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
        _memory.Verify(m => m.TryRemoveChannel(channelId), Times.Once);
        Assert.False(_registry.TryGet(channelId, out _));
    }

    [Fact]
    public async Task Given_Closing_When_AnotherTransactionConfirmed_Then_StillClosing()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.Closing);
        channel.SetClosingTransaction(s_closingTx);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        CreateManager();

        // Act
        _monitor.Raise(m => m.OnTransactionConfirmed += null, _monitor.Object,
                       Confirmed(channelId, new TxId(Enumerable.Repeat((byte)0x55, 32).ToArray())));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelState.Closing, channel.State);
        Assert.Empty(_persisted);
    }

    [Fact]
    public async Task Given_ClosingAtStartup_When_Registered_Then_ClosingTransactionRebroadcast()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.Closing);
        channel.SetClosingTransaction(s_closingTx);
        var manager = CreateManager();

        // Act
        await manager.RegisterExistingChannelAsync(channel);

        // Assert
        _memory.Verify(m => m.AddChannel(channel), Times.Once);
        _chain.Verify(c => c.SendTransactionAsync(It.Is<Transaction>(t => t.ToBytes().SequenceEqual(s_closingTx.RawTxBytes))),
                      Times.Once);
        Assert.Empty(_persisted);
    }

    [Theory]
    [InlineData(ChannelState.ShuttingDown)]
    [InlineData(ChannelState.Negotiating)]
    public async Task Given_ClosingNegotiationAtStartup_When_PeerConnects_Then_ReestablishSent(ChannelState state)
    {
        // Arrange (NL-036): registered as it is, then the reestablish starts the close again
        var channel = CreateChannel(state);
        channel.SetLocalShutdownScript(Convert.FromHexString("0014" + new string('1', 40)));
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => new[] { channel }.Where(predicate).ToList());
        var reestablishRequests = 0;
        var manager = CreateManager(services => services.AddScoped<ReestablishService>(_ =>
        {
            // The real service needs a whole node; being asked for it proves the reestablish path was taken
            reestablishRequests++;
            throw new InvalidOperationException("not built in this test");
        }));
        var raised = new List<Domain.Protocol.Interfaces.IChannelMessage>();
        manager.OnResponseMessageReady += (_, args) => raised.Add(args.ResponseMessage);
        await manager.RegisterExistingChannelAsync(channel);
        var registryEntry = _registry.Get(channel.ChannelId);
        registryEntry.ShutdownReceivedOnConnection = true;

        // Act
        await manager.OnPeerConnectedAsync(NormalOperationTestContext.PeerNodeId);

        // Assert: registered unchanged, and the connection state of the close was reset (B2-RE-29)
        _memory.Verify(m => m.AddChannel(channel), Times.Once);
        Assert.Equal(state, channel.State);
        Assert.False(registryEntry.ShutdownReceivedOnConnection);
        Assert.Empty(raised);
        Assert.Equal(1, reestablishRequests);
    }

    private ChannelManager CreateManager(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ReestablishTracker());
        services.AddSingleton(_registry);
        services.AddScoped<ChannelDomainEventQueue>();
        services.AddScoped(_ => _unitOfWork.Object);
        services.AddScoped(_ => _chain.Object);
        configure?.Invoke(services);

        return new ChannelManager(_monitor.Object, new ChannelLockProvider(), _memory.Object,
                                  NullLogger<ChannelManager>.Instance, new Mock<ILightningSigner>().Object,
                                  services.BuildServiceProvider());
    }

    private static TransactionConfirmedEventArgs Confirmed(ChannelId channelId, TxId txId)
    {
        var watched = new WatchedTransactionModel(channelId, txId, 6);
        watched.SetHeightAndIndex(600, 1);
        watched.MarkAsCompleted();
        return new TransactionConfirmedEventArgs(watched, 605);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.True(condition());
    }

    private static byte[] CreateRawTx()
    {
        var tx = Transaction.Create(Network.RegTest);
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(new TxOut(Money.Satoshis(10_000), new Key().PubKey.WitHash));
        return tx.ToBytes();
    }

    private static ChannelModel CreateChannel(ChannelState state)
    {
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(1_000_000), 144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false,
                                              FeatureSupport.No);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000),
                                                  NormalOperationTestContext.Point(0x01),
                                                  NormalOperationTestContext.Point(0x02))
        {
            TransactionId = new TxId(Enumerable.Repeat((byte)0x0f, 32).ToArray()),
            Index = 0
        };
        var keySet = new ChannelKeySetModel(0, NormalOperationTestContext.Point(0x01),
                                            NormalOperationTestContext.Point(0x03),
                                            NormalOperationTestContext.Point(0x04),
                                            NormalOperationTestContext.Point(0x05),
                                            NormalOperationTestContext.Point(0x06),
                                            NormalOperationTestContext.Point(0x07));
        return new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat((byte)0x0e, 32).ToArray()), null,
                                fundingOutput, true, null, null, LightningMoney.Satoshis(1_000_000), keySet, 0, 0,
                                LightningMoney.Zero, keySet, 0, NormalOperationTestContext.PeerNodeId, 0, state,
                                ChannelVersion.V1);
    }
}