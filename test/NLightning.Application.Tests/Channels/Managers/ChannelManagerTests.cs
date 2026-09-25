using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Managers;

using Application.Channels.Handlers;
using Application.Channels.Managers;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Constants;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class ChannelManagerTests
{
    private static readonly CompactPubKey s_emptyPubKey = new byte[]
    {
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    private readonly Mock<IBlockchainMonitor> _mockBlockchainMonitor = new();
    private readonly Mock<IChannelMemoryRepository> _mockChannelMemoryRepository = new();
    private readonly Mock<IChannelDbRepository> _mockChannelDbRepository = new();
    private readonly Mock<IMessageFactory> _mockMessageFactory = new();
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly Mock<IWatchedTransactionDbRepository> _mockWatchedTransactionDbRepository = new();
    private readonly List<ChannelModel> _channels = [];

    public ChannelManagerTests()
    {
        _mockChannelMemoryRepository
           .Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
           .Returns((Func<ChannelModel, bool> predicate) => _channels.Where(predicate).ToList());
        _mockChannelMemoryRepository
           .Setup(r => r.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel>.IsAny!))
           .Returns(new TryGetChannelDelegate((ChannelId id, out ChannelModel channel) =>
            {
                channel = _channels.FirstOrDefault(c => c.ChannelId == id)!;
                return channel is not null;
            }));

        _mockUnitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_mockChannelDbRepository.Object);
        _mockUnitOfWork.SetupGet(u => u.WatchedTransactionDbRepository)
                       .Returns(_mockWatchedTransactionDbRepository.Object);
        _mockChannelDbRepository.Setup(r => r.GetByIdAsync(It.IsAny<ChannelId>()))
                                .ReturnsAsync((ChannelId id) => _channels.FirstOrDefault(c => c.ChannelId == id));

        _mockMessageFactory
           .Setup(f => f.CreateChannelReadyMessage(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                                   It.IsAny<ShortChannelId?>()))
           .Returns((ChannelId id, CompactPubKey point, ShortChannelId? _) =>
                        new ChannelReadyMessage(new ChannelReadyPayload(id, point)));
    }

    private delegate bool TryGetChannelDelegate(ChannelId channelId, out ChannelModel channel);

    [Fact]
    public void Given_OpenChannelConfirmedLongAgo_When_NewBlockDetected_Then_ChannelIsNotMarkedStale()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.Open, false, 1, 100);
        _channels.Add(channel);
        CreateChannelManager();

        // Act
        RaiseNewBlock(100 + ChannelConstants.MaxUnconfirmedChannelAge + 5000);

        // Assert
        Assert.Equal(ChannelState.Open, channel.State);
        _mockChannelDbRepository.Verify(r => r.UpdateAsync(It.IsAny<ChannelModel>()), Times.Never);
    }

    [Fact]
    public void Given_UnconfirmedChannelWithoutCreationHeight_When_NewBlockDetected_Then_ChannelIsNotMarkedStale()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.V1FundingSigned, false, 1, 0);
        _channels.Add(channel);
        CreateChannelManager();

        // Act
        RaiseNewBlock(900_000);

        // Assert
        Assert.Equal(ChannelState.V1FundingSigned, channel.State);
    }

    [Fact]
    public void Given_InitiatorChannelAwaitingFunding_When_TimeoutPasses_Then_ChannelIsNotMarkedStale()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.V1FundingSigned, true, 1, 100);
        _channels.Add(channel);
        CreateChannelManager();

        // Act
        RaiseNewBlock(100 + ChannelConstants.MaxUnconfirmedChannelAge);

        // Assert
        Assert.Equal(ChannelState.V1FundingSigned, channel.State);
    }

    [Fact]
    public void Given_FundeeChannelAwaitingFunding_When_TimeoutPasses_Then_ChannelIsMarkedStale()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.V1FundingSigned, false, 1, 100);
        var youngChannel = CreateChannel(ChannelState.V1FundingSigned, false, 2, 101);
        _channels.Add(channel);
        _channels.Add(youngChannel);
        CreateChannelManager();

        // Act
        RaiseNewBlock(100 + ChannelConstants.MaxUnconfirmedChannelAge);

        // Assert
        Assert.Equal(ChannelState.Stale, channel.State);
        Assert.Equal(ChannelState.V1FundingSigned, youngChannel.State);
    }

    [Fact]
    public void Given_ReadyForUsChannel_When_SeveralBlocksDetected_Then_ChannelReadyIsNotResent()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.ReadyForUs, false, 1, 100);
        _channels.Add(channel);
        SetupCompletedWatchedTransaction(channel, 100);
        var commitmentNumberBefore = channel.CommitmentNumber!.Value;
        var channelManager = CreateChannelManager();
        var sentMessages = 0;
        channelManager.OnResponseMessageReady += (_, _) => sentMessages++;

        // Act
        RaiseNewBlock(103);
        RaiseNewBlock(104);

        // Assert
        Assert.Equal(ChannelState.ReadyForUs, channel.State);
        Assert.Equal(commitmentNumberBefore, channel.CommitmentNumber.Value);
        Assert.Equal(0, sentMessages);
    }

    [Fact]
    public void Given_ReadyForThemChannelWithCompletedFunding_When_SeveralBlocksDetected_Then_ConfirmedOnlyOnce()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.ReadyForThem, false, 1, 100);
        _channels.Add(channel);
        SetupCompletedWatchedTransaction(channel, 100);
        var commitmentNumberBefore = channel.CommitmentNumber!.Value;
        var channelManager = CreateChannelManager();
        var sentMessages = 0;
        channelManager.OnResponseMessageReady += (_, _) => sentMessages++;

        // Act
        RaiseNewBlock(103);
        RaiseNewBlock(104);
        RaiseNewBlock(105);

        // Assert
        Assert.Equal(ChannelState.Open, channel.State);
        Assert.Equal(commitmentNumberBefore + 1, channel.CommitmentNumber.Value);
        Assert.Equal(1, sentMessages);
    }

    [Fact]
    public void Given_ChannelWithPendingFunding_When_NewBlockDetected_Then_ChannelIsNotConfirmed()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.ReadyForThem, false, 1, 100);
        _channels.Add(channel);
        var watchedTransaction = new WatchedTransactionModel(channel.ChannelId,
                                                             channel.FundingOutput!.TransactionId!.Value, 3);
        watchedTransaction.SetHeightAndIndex(101, 0);
        _mockWatchedTransactionDbRepository.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                                           .ReturnsAsync(watchedTransaction);
        CreateChannelManager();

        // Act
        RaiseNewBlock(102);

        // Assert
        Assert.Equal(ChannelState.ReadyForThem, channel.State);
    }

    private ChannelManager CreateChannelManager()
    {
        var mockSigner = new Mock<ILightningSigner>();
        var serviceProvider = new FakeServiceProvider();
        serviceProvider.AddService(typeof(IUnitOfWork), _mockUnitOfWork.Object);
        serviceProvider.AddService(typeof(FundingConfirmedMessageHandler),
                                   new FundingConfirmedMessageHandler(_mockChannelMemoryRepository.Object,
                                                                      mockSigner.Object,
                                                                      new Mock<ILogger<FundingConfirmedMessageHandler>>()
                                                                         .Object,
                                                                      _mockMessageFactory.Object,
                                                                      _mockUnitOfWork.Object));

        return new ChannelManager(_mockBlockchainMonitor.Object, _mockChannelMemoryRepository.Object,
                                  new Mock<ILogger<ChannelManager>>().Object, mockSigner.Object, serviceProvider);
    }

    private void RaiseNewBlock(int height)
    {
        _mockBlockchainMonitor.Raise(m => m.OnNewBlockDetected += null,
                                     new NewBlockEventArgs((uint)height, new byte[32]));
    }

    private void SetupCompletedWatchedTransaction(ChannelModel channel, uint firstSeenAtHeight)
    {
        var watchedTransaction = new WatchedTransactionModel(channel.ChannelId,
                                                             channel.FundingOutput!.TransactionId!.Value, 3);
        watchedTransaction.SetHeightAndIndex(firstSeenAtHeight, 0);
        watchedTransaction.MarkAsCompleted();
        _mockWatchedTransactionDbRepository.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                                           .ReturnsAsync(watchedTransaction);
    }

    private static ChannelModel CreateChannel(ChannelState state, bool isInitiator, byte id,
                                              uint fundingCreatedAtBlockHeight)
    {
        var channelIdBytes = new byte[32];
        channelIdBytes[0] = id;
        var txIdBytes = new byte[32];
        txIdBytes[0] = id;

        var fundingAmount = LightningMoney.Satoshis(10_000);
        var fundingOutput = new FundingOutputInfo(fundingAmount, s_emptyPubKey, s_emptyPubKey)
        {
            TransactionId = new TxId(txIdBytes),
            Index = 0
        };
        var channelConfig = new ChannelConfig(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                              LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                              LightningMoney.Zero, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_emptyPubKey, s_emptyPubKey, s_emptyPubKey, s_emptyPubKey,
                                            s_emptyPubKey, s_emptyPubKey);
        var commitmentNumber = new CommitmentNumber(s_emptyPubKey, s_emptyPubKey, new FakeSha256());

        return new ChannelModel(channelConfig, new ChannelId(channelIdBytes), commitmentNumber, fundingOutput,
                                isInitiator, null, null, LightningMoney.Zero, keySet, 0, 0, fundingAmount, keySet, 0,
                                s_emptyPubKey, 0, state, ChannelVersion.V1)
        {
            FundingCreatedAtBlockHeight = fundingCreatedAtBlockHeight
        };
    }
}