using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Managers;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Services;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Constants;
using Domain.Channels.Enums;
using Domain.Channels.Factories;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Validators;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
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
    public void Given_UnconfirmedChannelWithoutCreationHeight_When_NewBlockDetected_Then_CreationHeightIsSetAndPersisted()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.V1FundingSigned, false, 1, 0);
        _channels.Add(channel);
        CreateChannelManager();

        // Act
        RaiseNewBlock(900_000);

        // Assert
        Assert.Equal(ChannelState.V1FundingSigned, channel.State);
        Assert.Equal(900_000u, channel.FundingCreatedAtBlockHeight);
        _mockChannelDbRepository.Verify(r => r.UpdateAsync(channel), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public void Given_UnconfirmedChannelWithoutCreationHeight_When_TimeoutPassesAfterFirstSeen_Then_ChannelIsMarkedStale()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.ReadyForThem, false, 1, 0);
        _channels.Add(channel);
        CreateChannelManager();
        RaiseNewBlock(900_000);

        // Act
        RaiseNewBlock(900_000 + ChannelConstants.MaxUnconfirmedChannelAge);

        // Assert
        Assert.Equal(ChannelState.Stale, channel.State);
    }

    [Fact]
    public void Given_InitiatorChannelWithoutCreationHeight_When_NewBlockDetected_Then_CreationHeightIsNotChanged()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.V1FundingSigned, true, 1, 0);
        _channels.Add(channel);
        CreateChannelManager();

        // Act
        RaiseNewBlock(900_000);

        // Assert
        Assert.Equal(0u, channel.FundingCreatedAtBlockHeight);
        _mockChannelDbRepository.Verify(r => r.UpdateAsync(It.IsAny<ChannelModel>()), Times.Never);
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
        var channelManager = CreateChannelManager();
        var sentMessages = 0;
        channelManager.OnResponseMessageReady += (_, _) => sentMessages++;

        // Act
        RaiseNewBlock(103);
        RaiseNewBlock(104);

        // Assert
        Assert.Equal(ChannelState.ReadyForUs, channel.State);
        Assert.Equal(0UL, channel.LocalCommitmentNumber);
        Assert.Equal(0, sentMessages);
    }

    [Fact]
    public void Given_ReadyForThemChannelWithCompletedFunding_When_SeveralBlocksDetected_Then_ConfirmedOnlyOnce()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.ReadyForThem, false, 1, 100);
        _channels.Add(channel);
        SetupCompletedWatchedTransaction(channel, 100);
        var channelManager = CreateChannelManager();
        var sentMessages = 0;
        channelManager.OnResponseMessageReady += (_, _) => sentMessages++;

        // Act
        RaiseNewBlock(103);
        RaiseNewBlock(104);
        RaiseNewBlock(105);

        // Assert
        Assert.Equal(ChannelState.Open, channel.State);
        Assert.Equal(0UL, channel.LocalCommitmentNumber);
        Assert.Equal(0UL, channel.RemoteCommitmentNumber);
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

    [Fact]
    public async Task Given_OpenChannelWithoutChannelType_When_Handled_Then_ChannelErrorCarriesTemporaryChannelId()
    {
        // Arrange (NL-027: open_channel without channel_type now reaches the handler; BOLT 2 fails that channel,
        // and the error must name the temporary_channel_id, not all-zero, which would fail every channel)
        var nodeOptions = new NodeOptions();
        var feeServiceMock = new Mock<IFeeService>();
        feeServiceMock.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(LightningMoney.Satoshis(2_500));
        var channelFactory = new ChannelFactory(new Mock<IChannelIdFactory>().Object,
                                                new ChannelOpenValidator(nodeOptions), feeServiceMock.Object,
                                                new Mock<ILightningSigner>().Object, nodeOptions, new FakeSha256());
        var handler = new OpenChannel1MessageHandler(channelFactory, _mockChannelMemoryRepository.Object,
                                                     new Mock<ILogger<OpenChannel1MessageHandler>>().Object,
                                                     _mockMessageFactory.Object);
        var channelManager = CreateChannelManager((typeof(IChannelMessageHandler<OpenChannel1Message>), handler));
        var temporaryChannelId = CreateChannelId(0x42);
        var payload = new OpenChannel1Payload(nodeOptions.BitcoinNetwork.ChainHash, new ChannelFlags((byte)0),
                                              temporaryChannelId, LightningMoney.Satoshis(1_000), s_emptyPubKey,
                                              LightningMoney.Satoshis(354), LightningMoney.Satoshis(2_500),
                                              s_emptyPubKey, LightningMoney.Satoshis(100_000), s_emptyPubKey,
                                              s_emptyPubKey, LightningMoney.Satoshis(1), 30,
                                              LightningMoney.Satoshis(100_000), s_emptyPubKey, LightningMoney.Zero,
                                              s_emptyPubKey, 144);
        var message = new OpenChannel1Message(payload, null);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => channelManager.HandleChannelMessageAsync(message, new FeatureOptions(),
                                                                           s_emptyPubKey));

        // Assert
        Assert.Contains("ChannelTypeTlv", exception.Message);
        Assert.Equal(temporaryChannelId, exception.ChannelId);
    }

    [Fact]
    public async Task Given_HandlerFailsWithoutChannelId_When_Handled_Then_ChannelIdOfTheMessageIsAttached()
    {
        // Arrange
        var handlerMock = new Mock<IChannelMessageHandler<ChannelReadyMessage>>();
        handlerMock.Setup(h => h.HandleAsync(It.IsAny<ChannelReadyMessage>(), It.IsAny<ChannelState>(),
                                             It.IsAny<FeatureOptions>(), It.IsAny<CompactPubKey>()))
                   .ThrowsAsync(new ChannelErrorException("internal", "peer text"));
        var channelManager =
            CreateChannelManager((typeof(IChannelMessageHandler<ChannelReadyMessage>), handlerMock.Object));
        var channelId = CreateChannelId(0x43);
        var message = new ChannelReadyMessage(new ChannelReadyPayload(channelId, s_emptyPubKey));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => channelManager.HandleChannelMessageAsync(message, new FeatureOptions(),
                                                                           s_emptyPubKey));

        // Assert
        Assert.Equal(channelId, exception.ChannelId);
        Assert.Equal("peer text", exception.PeerMessage);
    }

    [Theory]
    [InlineData(MessageTypes.TxAddInput)]
    [InlineData(MessageTypes.TxComplete)]
    public async Task Given_NotImplementedChannelMessage_When_Handled_Then_ChannelScopedWarningIsRaised(
        MessageTypes messageType)
    {
        // Arrange (interim for the dual-funding messages: never fail a known channel, or all channels, for these;
        // shutdown and closing_signed have handlers since N10)
        var channelManager = CreateChannelManager();
        var channelId = CreateChannelId(0x44);
        MarkChannelKnownInMemory(channelId);
        var payloadMock = new Mock<IChannelMessagePayload>();
        payloadMock.SetupGet(p => p.ChannelId).Returns(channelId);
        var messageMock = new Mock<IChannelMessage>();
        messageMock.SetupGet(m => m.Type).Returns(messageType);
        messageMock.SetupGet(m => m.Payload).Returns(payloadMock.Object);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => channelManager.HandleChannelMessageAsync(messageMock.Object, new FeatureOptions(),
                                                                           s_emptyPubKey));

        // Assert
        Assert.Equal(channelId, exception.ChannelId);
        Assert.Contains("not supported yet", exception.PeerMessage);
    }

    [Theory]
    [InlineData(MessageTypes.ChannelReestablish)]
    [InlineData(MessageTypes.UpdateAddHtlc)]
    [InlineData(MessageTypes.UpdateFailMalformedHtlc)]
    [InlineData(MessageTypes.Shutdown)]
    public async Task Given_MessageForUnknownChannel_When_Handled_Then_ErrorIsScopedToTheUnknownChannel(
        MessageTypes messageType)
    {
        // Arrange (BOLT 1: SHOULD send `error` with the unknown channel_id, so a peer holding a channel we lost fails
        // it instead of waiting forever)
        var channelManager = CreateChannelManager();
        var channelId = CreateChannelId(0x47);
        var messageMock = CreateChannelMessageMock(messageType, channelId);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => channelManager.HandleChannelMessageAsync(messageMock.Object, new FeatureOptions(),
                                                                           s_emptyPubKey));

        // Assert
        Assert.Equal(channelId, exception.ChannelId);
        Assert.Equal("unknown channel", exception.PeerMessage);
    }

    [Fact]
    public async Task Given_MessageForChannelOnlyInDatabase_When_Handled_Then_OnlyAWarningIsRaised()
    {
        // Arrange (not every channel is in memory, e.g. stale ones: a channel in the DB is known, never failed)
        var channel = CreateChannel(ChannelState.Stale, false, 0x48, 100);
        _channels.Add(channel);
        var channelManager = CreateChannelManager();
        var messageMock = CreateChannelMessageMock(MessageTypes.TxAddInput, channel.ChannelId);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => channelManager.HandleChannelMessageAsync(messageMock.Object, new FeatureOptions(),
                                                                           s_emptyPubKey));

        // Assert
        Assert.Equal(channel.ChannelId, exception.ChannelId);
    }

    [Fact]
    public async Task Given_MessageForTemporaryChannelOfThisPeer_When_Handled_Then_OnlyAWarningIsRaised()
    {
        // Arrange (a channel being opened with this peer is known under its temporary id)
        var channelManager = CreateChannelManager();
        var temporaryChannelId = CreateChannelId(0x49);
        _mockChannelMemoryRepository
           .Setup(r => r.TryGetTemporaryChannelState(s_emptyPubKey, temporaryChannelId,
                                                     out It.Ref<ChannelState>.IsAny))
           .Returns(true);
        var messageMock = CreateChannelMessageMock(MessageTypes.TxAddInput, temporaryChannelId);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => channelManager.HandleChannelMessageAsync(messageMock.Object, new FeatureOptions(),
                                                                           s_emptyPubKey));

        // Assert
        Assert.Equal(temporaryChannelId, exception.ChannelId);
    }

    private static Mock<IChannelMessage> CreateChannelMessageMock(MessageTypes messageType, ChannelId channelId)
    {
        var payloadMock = new Mock<IChannelMessagePayload>();
        payloadMock.SetupGet(p => p.ChannelId).Returns(channelId);
        var messageMock = new Mock<IChannelMessage>();
        messageMock.SetupGet(m => m.Type).Returns(messageType);
        messageMock.SetupGet(m => m.Payload).Returns(payloadMock.Object);
        return messageMock;
    }

    private void MarkChannelKnownInMemory(ChannelId channelId)
    {
        _mockChannelMemoryRepository.Setup(r => r.TryGetChannelState(channelId, out It.Ref<ChannelState>.IsAny))
                                    .Returns(true);
    }

    private static ChannelId CreateChannelId(byte fill)
    {
        return new ChannelId(Enumerable.Repeat(fill, 32).ToArray());
    }

    private ChannelManager CreateChannelManager(params (Type Type, object Service)[] extraServices)
    {
        var mockSigner = new Mock<ILightningSigner>();
        var serviceProvider = new FakeServiceProvider();
        foreach (var (type, service) in extraServices)
            serviceProvider.AddService(type, service);
        serviceProvider.AddService(typeof(IUnitOfWork), _mockUnitOfWork.Object);
        serviceProvider.AddService(typeof(ChannelDomainEventQueue), new ChannelDomainEventQueue());
        serviceProvider.AddService(typeof(FundingConfirmedMessageHandler),
                                   new FundingConfirmedMessageHandler(_mockChannelMemoryRepository.Object,
                                                                      mockSigner.Object,
                                                                      new Mock<ILogger<FundingConfirmedMessageHandler>>()
                                                                         .Object,
                                                                      _mockMessageFactory.Object,
                                                                      _mockUnitOfWork.Object));

        return new ChannelManager(_mockBlockchainMonitor.Object, new ChannelLockProvider(),
                                  _mockChannelMemoryRepository.Object, new Mock<ILogger<ChannelManager>>().Object,
                                  mockSigner.Object, serviceProvider);
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
        var channelConfig = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
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