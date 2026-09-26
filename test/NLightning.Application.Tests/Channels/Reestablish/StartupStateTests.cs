using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Reestablish;

using Application.Channels.Managers;
using Application.Channels.Reestablish;
using Application.Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Handlers;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// BOLT2 plan N7-T5: what a channel loaded at startup resumes as, for every state other than Open
/// (<see cref="ChannelManager.RegisterExistingChannelAsync"/>). The funder rules are BOLT 2 "Message Retransmission":
/// a funder that has not broadcast the funding transaction SHOULD NOT remember the channel (NL-048).
/// </summary>
public class StartupStateTests
{
    private static readonly CompactPubKey s_peer = NormalOperationTestContext.PeerNodeId;

    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<ILightningSigner> _signer = new();
    private readonly Mock<IChannelDbRepository> _channelDb = new();
    private readonly Mock<IWatchedTransactionDbRepository> _watchedDb = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly List<ChannelState> _persistedStates = [];
    private readonly List<(CompactPubKey Peer, IChannelMessage Message)> _raised = [];

    public StartupStateTests()
    {
        _unitOfWork.Setup(u => u.ChannelDbRepository).Returns(_channelDb.Object);
        _unitOfWork.Setup(u => u.WatchedTransactionDbRepository).Returns(_watchedDb.Object);
        _channelDb.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>()))
                  .Callback((ChannelModel c) => _persistedStates.Add(c.State))
                  .Returns(Task.CompletedTask);
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
    }

    [Fact]
    public async Task Given_FunderStoppedAfterWatchingTheFunding_When_Registered_Then_ItWaitsForTheConfirmation()
    {
        // Arrange - FundingSignedMessageHandler persisted the channel and the watch (so the tx may be out), then died
        var channel = CreateChannel(ChannelState.V1FundingCreated);
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(channel.FundingOutput!.TransactionId!.Value))
                  .ReturnsAsync(new WatchedTransactionModel(channel.ChannelId,
                                                            channel.FundingOutput.TransactionId.Value, 3));
        var manager = CreateManager();

        // Act
        await manager.RegisterExistingChannelAsync(channel);

        // Assert - remembered (it may have been broadcast), persisted as V1FundingSigned before it is registered
        Assert.Equal(ChannelState.V1FundingSigned, channel.State);
        Assert.Equal([ChannelState.V1FundingSigned], _persistedStates);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
        _memory.Verify(m => m.AddChannel(channel), Times.Once);
        _signer.Verify(s => s.RegisterChannel(channel.ChannelId, It.IsAny<ChannelSigningInfo>()), Times.Once);
    }

    [Fact]
    public async Task Given_FunderStoppedBeforeWatchingTheFunding_When_Registered_Then_TheChannelIsForgotten()
    {
        // Arrange - no watch: the funding transaction was never published
        var channel = CreateChannel(ChannelState.V1FundingCreated);
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((WatchedTransactionModel?)null);
        var manager = CreateManager();

        // Act
        await manager.RegisterExistingChannelAsync(channel);

        // Assert - BOLT 2: not remembered, persisted Stale so the next start skips it
        Assert.Equal(ChannelState.Stale, channel.State);
        Assert.Equal([ChannelState.Stale], _persistedStates);
        _memory.Verify(m => m.AddChannel(It.IsAny<ChannelModel>()), Times.Never);
        _signer.Verify(s => s.RegisterChannel(It.IsAny<ChannelId>(), It.IsAny<ChannelSigningInfo>()), Times.Never);
    }

    [Fact]
    public async Task Given_AWatchOfAnotherChannelOnTheFundingTxId_When_Registered_Then_TheChannelIsForgotten()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.V1FundingCreated);
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync(new WatchedTransactionModel(new ChannelId(Enumerable.Repeat((byte)0x99, 32).ToArray()),
                                                            channel.FundingOutput!.TransactionId!.Value, 3));
        var manager = CreateManager();

        // Act
        await manager.RegisterExistingChannelAsync(channel);

        // Assert
        Assert.Equal(ChannelState.Stale, channel.State);
        _memory.Verify(m => m.AddChannel(It.IsAny<ChannelModel>()), Times.Never);
    }

    [Theory]
    [InlineData(ChannelState.None)]
    [InlineData(ChannelState.V1Opening)]
    [InlineData(ChannelState.V2Opening)]
    [InlineData(ChannelState.Closed)]
    [InlineData(ChannelState.Stale)]
    public async Task Given_AStateThatIsNotRemembered_When_Registered_Then_NothingIsRegisteredOrPersisted(
        ChannelState state)
    {
        // Arrange
        var channel = CreateChannel(state);
        var manager = CreateManager();

        // Act
        await manager.RegisterExistingChannelAsync(channel);

        // Assert
        Assert.Equal(state, channel.State);
        Assert.Empty(_persistedStates);
        _memory.Verify(m => m.AddChannel(It.IsAny<ChannelModel>()), Times.Never);
        _signer.Verify(s => s.RegisterChannel(It.IsAny<ChannelId>(), It.IsAny<ChannelSigningInfo>()), Times.Never);
    }

    [Theory]
    [InlineData(ChannelState.V1FundingSigned)]
    [InlineData(ChannelState.ReadyForThem)]
    [InlineData(ChannelState.ReadyForUs)]
    [InlineData(ChannelState.Open)]
    [InlineData(ChannelState.Closing)]
    [InlineData(ChannelState.Failed)]
    public async Task Given_ARememberedState_When_Registered_Then_ItIsRegisteredUnchanged(ChannelState state)
    {
        // Arrange
        var channel = CreateChannel(state);
        var manager = CreateManager();

        // Act
        await manager.RegisterExistingChannelAsync(channel);

        // Assert
        Assert.Equal(state, channel.State);
        Assert.Empty(_persistedStates);
        _memory.Verify(m => m.AddChannel(channel), Times.Once);
        _signer.Verify(s => s.RegisterChannel(channel.ChannelId, It.IsAny<ChannelSigningInfo>()), Times.Once);
    }

    [Theory]
    [InlineData(ChannelState.V1FundingSigned)]
    [InlineData(ChannelState.ReadyForUs)]
    [InlineData(ChannelState.Closing)]
    public async Task Given_ARegisteredChannelNotOpenNorFailed_When_PeerConnects_Then_NothingIsSentFirst(
        ChannelState state)
    {
        // Arrange - pending channels answer the peer's reestablish; Closing waits for shutdown (N10)
        var channel = CreateChannel(state);
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => new[] { channel }.Where(predicate).ToList());
        var manager = CreateManager();
        await manager.RegisterExistingChannelAsync(channel);

        // Act
        var errors = await manager.OnPeerConnectedAsync(s_peer);

        // Assert
        Assert.Empty(errors);
        Assert.Empty(_raised);
    }

    private ChannelManager CreateManager()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ReestablishTracker());
        services.AddScoped<ChannelDomainEventQueue>();
        services.AddScoped(_ => _unitOfWork.Object);

        var manager = new ChannelManager(new Mock<IBlockchainMonitor>().Object, new ChannelLockProvider(),
                                         _memory.Object, NullLogger<ChannelManager>.Instance, _signer.Object,
                                         services.BuildServiceProvider());
        manager.OnResponseMessageReady += (_, args) => _raised.Add((args.PeerPubKey, args.ResponseMessage));
        return manager;
    }

    /// <summary>A funder's channel without a commitment snapshot, in <paramref name="state"/>.</summary>
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
                                LightningMoney.Zero, keySet, 0, s_peer, 0, state, ChannelVersion.V1);
    }
}