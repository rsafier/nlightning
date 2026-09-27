using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Handlers;

using Application.Channels.Services;
using Daemon.Handlers;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class OpenChannelClientHandlerTests
{
    private readonly Mock<IBlockchainMonitor> _blockchainMonitorMock;
    private readonly Mock<IChannelFactory> _channelFactoryMock;
    private readonly Mock<IChannelManager> _channelManagerMock;
    private readonly Mock<IChannelMemoryRepository> _channelMemoryRepositoryMock;
    private readonly Mock<IMessageFactory> _messageFactoryMock;
    private readonly Mock<IPeerManager> _peerManagerMock;
    private readonly Mock<IUtxoMemoryRepository> _utxoMemoryRepositoryMock;
    private readonly OpenChannelClientHandler _handler;

    public OpenChannelClientHandlerTests()
    {
        _blockchainMonitorMock = new Mock<IBlockchainMonitor>();
        _channelFactoryMock = new Mock<IChannelFactory>();
        _channelManagerMock = new Mock<IChannelManager>();
        _channelManagerMock.Setup(x => x.StartOpeningChannelAsync(It.IsAny<CompactPubKey>(), It.IsAny<ChannelModel>(),
                                                                  It.IsAny<IChannelMessage>()))
                           .Returns(Task.CompletedTask);
        _channelMemoryRepositoryMock = new Mock<IChannelMemoryRepository>();
        var loggerMock = new Mock<ILogger<OpenChannelClientHandler>>();
        _messageFactoryMock = new Mock<IMessageFactory>();
        _peerManagerMock = new Mock<IPeerManager>();
        _utxoMemoryRepositoryMock = new Mock<IUtxoMemoryRepository>();

        _handler = new OpenChannelClientHandler(
            _blockchainMonitorMock.Object,
            _channelFactoryMock.Object,
            _channelManagerMock.Object,
            _channelMemoryRepositoryMock.Object,
            loggerMock.Object,
            _messageFactoryMock.Object,
            _peerManagerMock.Object,
            _utxoMemoryRepositoryMock.Object
        );
    }

    [Fact]
    public async Task GivenValidRequest_WhenHandleAsync_ThenFollowsCompleteFlow()
    {
        // Arrange
        var peerId = CreateDummyPubKey();
        var nodeInfo = $"{peerId}@127.0.0.1:9735";
        var fundingAmount = LightningMoney.Satoshis(100000);
        var request = new OpenChannelClientRequest(nodeInfo, fundingAmount);

        var peerModel = new PeerModel(peerId, "127.0.0.1", 9735, "ipv4");
        var peerServiceMock = new Mock<IPeerService>();
        peerServiceMock.Setup(x => x.Features).Returns(new FeatureOptions());
        peerModel.SetPeerService(peerServiceMock.Object);

        _peerManagerMock.Setup(x => x.GetPeer(peerId)).Returns(peerModel);
        _blockchainMonitorMock.Setup(x => x.LastProcessedBlockHeight).Returns(100u);
        _utxoMemoryRepositoryMock.Setup(x => x.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(200000));

        var channelConfig = new ChannelParams();
        var localKeySet = new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId);
        var channelModel = new ChannelModel(channelConfig, CreateRandomChannelId(), null, null, true, null, null,
                                            fundingAmount, localKeySet, 0, 0, LightningMoney.Zero, null, 0, peerId, 0,
                                            ChannelState.V1Opening, ChannelVersion.V1);
        var tempChannelId = channelModel.ChannelId;

        _channelFactoryMock.Setup(x => x.CreateChannelV1AsInitiatorAsync(request, It.IsAny<FeatureOptions>(), peerId))
                           .ReturnsAsync(channelModel);

        var openChannel1Message = CreateDummyOpenChannel1Message(tempChannelId, fundingAmount, peerId);
        _messageFactoryMock.Setup(x => x.CreateOpenChannel1Message(It.IsAny<ChannelId>(), It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<ChannelParty>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<ChannelFlags>(),
                                                                   It.IsAny<ChannelTypeTlv>(),
                                                                   It.IsAny<UpfrontShutdownScriptTlv>()))
                           .Returns(openChannel1Message);

        peerServiceMock.Setup(x => x.SendMessageAsync(It.IsAny<IChannelMessage>())).Returns(Task.CompletedTask);

        var finalChannelId = CreateRandomChannelId();

        // Act
        var handleTask = _handler.HandleAsync(request, CancellationToken.None);

        // Wait a bit to ensure it reached the `await tsc.Task`
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _channelMemoryRepositoryMock.Raise(x => x.OnChannelUpgraded += null, null!,
                                           new ChannelUpgradedEventArgs(tempChannelId, finalChannelId));

        var response = await handleTask;

        // Assert
        Assert.Equal(finalChannelId, response.ChannelId);
        _peerManagerMock.Verify(x => x.GetPeer(peerId), Times.Once);
        _utxoMemoryRepositoryMock.Verify(x => x.LockUtxosToSpendOnChannel(fundingAmount, tempChannelId), Times.Once);
        // open_channel goes through the channel manager (temporary channel lock + the peer's outbox), never straight
        // to the peer service
        _channelManagerMock.Verify(x => x.StartOpeningChannelAsync(peerId, channelModel, openChannel1Message),
                                   Times.Once);
        peerServiceMock.Verify(x => x.SendMessageAsync(It.IsAny<IChannelMessage>()), Times.Never);
    }

    [Fact]
    public async Task GivenValidRequest_WhenHandleAsync_ThenChannelTypeTlvIsBigEndianStaticRemoteKey()
    {
        // Arrange
        var peerId = CreateDummyPubKey();
        var nodeInfo = $"{peerId}@127.0.0.1:9735";
        var fundingAmount = LightningMoney.Satoshis(100000);
        var request = new OpenChannelClientRequest(nodeInfo, fundingAmount);

        var peerModel = new PeerModel(peerId, "127.0.0.1", 9735, "ipv4");
        var peerServiceMock = new Mock<IPeerService>();
        peerServiceMock.Setup(x => x.Features).Returns(new FeatureOptions());
        peerModel.SetPeerService(peerServiceMock.Object);

        _peerManagerMock.Setup(x => x.GetPeer(peerId)).Returns(peerModel);
        _blockchainMonitorMock.Setup(x => x.LastProcessedBlockHeight).Returns(100u);
        _utxoMemoryRepositoryMock.Setup(x => x.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(200000));

        var localKeySet = new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId);
        var channelModel = new ChannelModel(new ChannelParams(), CreateRandomChannelId(), null, null, true, null, null,
                                            fundingAmount, localKeySet, 0, 0, LightningMoney.Zero, null, 0, peerId, 0,
                                            ChannelState.V1Opening, ChannelVersion.V1);
        var tempChannelId = channelModel.ChannelId;

        _channelFactoryMock.Setup(x => x.CreateChannelV1AsInitiatorAsync(request, It.IsAny<FeatureOptions>(), peerId))
                           .ReturnsAsync(channelModel);

        ChannelTypeTlv? capturedChannelType = null;
        var openChannel1Message = CreateDummyOpenChannel1Message(tempChannelId, fundingAmount, peerId);
        _messageFactoryMock.Setup(x => x.CreateOpenChannel1Message(It.IsAny<ChannelId>(), It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<ChannelParty>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<ChannelFlags>(),
                                                                   It.IsAny<ChannelTypeTlv>(),
                                                                   It.IsAny<UpfrontShutdownScriptTlv>()))
                           .Callback(new InvocationAction(invocation =>
                                                              capturedChannelType =
                                                                  invocation.Arguments.OfType<ChannelTypeTlv>()
                                                                            .Single()))
                           .Returns(openChannel1Message);

        peerServiceMock.Setup(x => x.SendMessageAsync(It.IsAny<IChannelMessage>())).Returns(Task.CompletedTask);

        // Act
        var handleTask = _handler.HandleAsync(request, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _channelMemoryRepositoryMock.Raise(x => x.OnChannelUpgraded += null, null!,
                                           new ChannelUpgradedEventArgs(tempChannelId, CreateRandomChannelId()));
        await handleTask;

        // Assert
        Assert.NotNull(capturedChannelType);
        // Big-endian: static_remotekey (bit 12) lives in the last two bytes, whatever else the channel type sets
        Assert.Equal(new byte[] { 0x10, 0x00 }, capturedChannelType.Value[^2..]);
        Assert.True(capturedChannelType.Features.IsFeatureSet(Feature.OptionStaticRemoteKey, true));
        Assert.False(capturedChannelType.Features.HasFeature(Feature.OptionUpfrontShutdownScript));
    }

    [Fact]
    public async Task Given_PushAmount_When_HandleAsync_Then_OpenChannelCarriesTheWholeFundingAmount()
    {
        // Arrange
        var peerId = CreateDummyPubKey();
        var nodeInfo = $"{peerId}@127.0.0.1:9735";
        var fundingAmount = LightningMoney.Satoshis(1_000_000);
        var pushAmount = LightningMoney.Satoshis(300_000);
        var request = new OpenChannelClientRequest(nodeInfo, fundingAmount) { PushAmount = pushAmount };

        var peerModel = new PeerModel(peerId, "127.0.0.1", 9735, "ipv4");
        var peerServiceMock = new Mock<IPeerService>();
        peerServiceMock.Setup(x => x.Features).Returns(new FeatureOptions());
        peerModel.SetPeerService(peerServiceMock.Object);

        _peerManagerMock.Setup(x => x.GetPeer(peerId)).Returns(peerModel);
        _blockchainMonitorMock.Setup(x => x.LastProcessedBlockHeight).Returns(100u);
        _utxoMemoryRepositoryMock.Setup(x => x.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(2_000_000));

        // The factory splits the funding into our opening balance and the pushed amount
        var localKeySet = new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId);
        var channelModel = new ChannelModel(new ChannelParams(), CreateRandomChannelId(), null, null, true, null, null,
                                            fundingAmount - pushAmount, localKeySet, 0, 0, pushAmount, null, 0,
                                            peerId, 0, ChannelState.V1Opening, ChannelVersion.V1);
        var tempChannelId = channelModel.ChannelId;
        _channelFactoryMock.Setup(x => x.CreateChannelV1AsInitiatorAsync(request, It.IsAny<FeatureOptions>(), peerId))
                           .ReturnsAsync(channelModel);

        LightningMoney? sentFunding = null;
        LightningMoney? sentPush = null;
        _messageFactoryMock.Setup(x => x.CreateOpenChannel1Message(It.IsAny<ChannelId>(), It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<ChannelParty>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<ChannelFlags>(),
                                                                   It.IsAny<ChannelTypeTlv>(),
                                                                   It.IsAny<UpfrontShutdownScriptTlv>()))
                           .Callback(new InvocationAction(invocation =>
                            {
                                sentFunding = (LightningMoney)invocation.Arguments[1];
                                sentPush = (LightningMoney)invocation.Arguments[3];
                            }))
                           .Returns(CreateDummyOpenChannel1Message(tempChannelId, fundingAmount, peerId));
        peerServiceMock.Setup(x => x.SendMessageAsync(It.IsAny<IChannelMessage>())).Returns(Task.CompletedTask);

        // Act
        var handleTask = _handler.HandleAsync(request, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _channelMemoryRepositoryMock.Raise(x => x.OnChannelUpgraded += null, null!,
                                           new ChannelUpgradedEventArgs(tempChannelId, CreateRandomChannelId()));
        await handleTask;

        // Assert: BOLT 2 funding_satoshis is the channel capacity, push_msat is taken out of it
        Assert.Equal(fundingAmount, sentFunding);
        Assert.Equal(pushAmount, sentPush);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AnnounceFlag_When_HandleAsync_Then_OpenChannelCarriesIt(bool announce)
    {
        // Arrange (NL-341, G1-T1: the factory stores the flag, open_channel.channel_flags carries it)
        var peerId = CreateDummyPubKey();
        var nodeInfo = $"{peerId}@127.0.0.1:9735";
        var fundingAmount = LightningMoney.Satoshis(1_000_000);
        var request = new OpenChannelClientRequest(nodeInfo, fundingAmount);

        var peerModel = new PeerModel(peerId, "127.0.0.1", 9735, "ipv4");
        var peerServiceMock = new Mock<IPeerService>();
        peerServiceMock.Setup(x => x.Features).Returns(new FeatureOptions());
        peerModel.SetPeerService(peerServiceMock.Object);

        _peerManagerMock.Setup(x => x.GetPeer(peerId)).Returns(peerModel);
        _blockchainMonitorMock.Setup(x => x.LastProcessedBlockHeight).Returns(100u);
        _utxoMemoryRepositoryMock.Setup(x => x.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(2_000_000));

        var localKeySet = new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId);
        var channelModel = new ChannelModel(new ChannelParams { AnnounceChannel = announce }, CreateRandomChannelId(),
                                            null, null, true, null, null, fundingAmount, localKeySet, 0, 0,
                                            LightningMoney.Zero, null, 0, peerId, 0, ChannelState.V1Opening,
                                            ChannelVersion.V1);
        var tempChannelId = channelModel.ChannelId;
        _channelFactoryMock.Setup(x => x.CreateChannelV1AsInitiatorAsync(request, It.IsAny<FeatureOptions>(), peerId))
                           .ReturnsAsync(channelModel);

        ChannelFlags? sentFlags = null;
        _messageFactoryMock.Setup(x => x.CreateOpenChannel1Message(It.IsAny<ChannelId>(), It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<ChannelParty>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<ChannelFlags>(),
                                                                   It.IsAny<ChannelTypeTlv>(),
                                                                   It.IsAny<UpfrontShutdownScriptTlv>()))
                           .Callback(new InvocationAction(invocation =>
                                                              sentFlags = (ChannelFlags)invocation.Arguments[11]))
                           .Returns(CreateDummyOpenChannel1Message(tempChannelId, fundingAmount, peerId));

        // Act
        var handleTask = _handler.HandleAsync(request, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _channelMemoryRepositoryMock.Raise(x => x.OnChannelUpgraded += null, null!,
                                           new ChannelUpgradedEventArgs(tempChannelId, CreateRandomChannelId()));
        await handleTask;

        // Assert
        Assert.NotNull(sentFlags);
        Assert.Equal(announce, sentFlags.Value.AnnounceChannel);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Given_PublicRequest_When_OnMainnetOrZeroConf_Then_RefusedBeforeConnecting(bool allowMainnet,
        bool zeroConf)
    {
        // Arrange (BOLT 7 plan D12: public channels stay off on mainnet until Proof G1; zero-conf has nothing to
        // announce)
        var handler = new OpenChannelClientHandler(_blockchainMonitorMock.Object, _channelFactoryMock.Object,
                                                   _channelManagerMock.Object, _channelMemoryRepositoryMock.Object,
                                                   new Mock<ILogger<OpenChannelClientHandler>>().Object,
                                                   _messageFactoryMock.Object, _peerManagerMock.Object,
                                                   _utxoMemoryRepositoryMock.Object,
                                                   Options.Create(new GossipOptions
                                                   {
                                                       AllowPublicChannelsOnMainnet = allowMainnet
                                                   }),
                                                   Options.Create(new NodeOptions
                                                   {
                                                       BitcoinNetwork = BitcoinNetwork.Mainnet
                                                   }));
        var request = new OpenChannelClientRequest($"{CreateDummyPubKey()}@127.0.0.1:9735",
                                                   LightningMoney.Satoshis(1_000_000))
        {
            IsPublic = true,
            IsZeroConfChannel = zeroConf
        };

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => handler.HandleAsync(request, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        _peerManagerMock.Verify(x => x.GetPeer(It.IsAny<CompactPubKey>()), Times.Never);
        _channelFactoryMock.Verify(x => x.CreateChannelV1AsInitiatorAsync(It.IsAny<OpenChannelClientRequest>(),
                                                                          It.IsAny<FeatureOptions>(),
                                                                          It.IsAny<CompactPubKey>()), Times.Never);
    }

    [Fact]
    public async Task GivenPeerNotConnected_WhenHandleAsync_ThenConnectsToPeer()
    {
        // Arrange
        var peerId = CreateDummyPubKey();
        var nodeInfo = $"{peerId}@127.0.0.1:9735";
        var fundingAmount = LightningMoney.Satoshis(100000);
        var request = new OpenChannelClientRequest(nodeInfo, fundingAmount);

        var peerModel = new PeerModel(peerId, "127.0.0.1", 9735, "ipv4");
        var peerServiceMock = new Mock<IPeerService>();
        peerServiceMock.Setup(x => x.Features).Returns(new FeatureOptions());
        peerModel.SetPeerService(peerServiceMock.Object);

        // Peer is not found initially
        _peerManagerMock.Setup(x => x.GetPeer(peerId)).Returns((PeerModel?)null);
        _peerManagerMock.Setup(x => x.ConnectToPeerAsync(It.IsAny<PeerAddressInfo>())).ReturnsAsync(peerModel);

        _blockchainMonitorMock.Setup(x => x.LastProcessedBlockHeight).Returns(100u);
        _utxoMemoryRepositoryMock.Setup(x => x.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(200000));

        var channelModel = new ChannelModel(new ChannelParams(), CreateRandomChannelId(), null, null, true, null, null,
                                            fundingAmount,
                                            new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId),
                                            0, 0, LightningMoney.Zero, null, 0, peerId, 0, ChannelState.V1Opening,
                                            ChannelVersion.V1);
        var tempChannelId = channelModel.ChannelId;
        _channelFactoryMock.Setup(x => x.CreateChannelV1AsInitiatorAsync(request, It.IsAny<FeatureOptions>(), peerId))
                           .ReturnsAsync(channelModel);
        _messageFactoryMock.Setup(x => x.CreateOpenChannel1Message(It.IsAny<ChannelId>(), It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<ChannelParty>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<ChannelFlags>(),
                                                                   It.IsAny<ChannelTypeTlv>(),
                                                                   It.IsAny<UpfrontShutdownScriptTlv>()))
                           .Returns(CreateDummyOpenChannel1Message(tempChannelId, fundingAmount, peerId));
        peerServiceMock.Setup(x => x.SendMessageAsync(It.IsAny<IChannelMessage>())).Returns(Task.CompletedTask);

        var finalChannelId = CreateRandomChannelId();

        // Act
        var handleTask = _handler.HandleAsync(request, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _channelMemoryRepositoryMock.Raise(x => x.OnChannelUpgraded += null, null!,
                                           new ChannelUpgradedEventArgs(tempChannelId, finalChannelId));
        await handleTask;

        // Assert
        _peerManagerMock.Verify(x => x.ConnectToPeerAsync(It.Is<PeerAddressInfo>(p => p.Address == nodeInfo)),
                                Times.Once);
    }

    [Fact]
    public async Task GivenInsufficientBalance_WhenHandleAsync_ThenThrowsClientException()
    {
        // Arrange
        var peerId = CreateDummyPubKey();
        var nodeInfo = $"{peerId}@127.0.0.1:9735";
        var fundingAmount = LightningMoney.Satoshis(100000);
        var request = new OpenChannelClientRequest(nodeInfo, fundingAmount);

        var peerModel = new PeerModel(peerId, "127.0.0.1", 9735, "ipv4");
        _peerManagerMock.Setup(x => x.GetPeer(peerId)).Returns(peerModel);
        _blockchainMonitorMock.Setup(x => x.LastProcessedBlockHeight).Returns(100u);
        _utxoMemoryRepositoryMock.Setup(x => x.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(50000));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ClientException>(() => _handler.HandleAsync(request, CancellationToken.None));
        Assert.Equal(ErrorCodes.NotEnoughBalance, ex.ErrorCode);
    }

    [Fact]
    public async Task GivenInvalidNodeInfo_WhenHandleAsync_ThenThrowsClientException()
    {
        // Arrange
        var request = new OpenChannelClientRequest("", LightningMoney.Satoshis(100000));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ClientException>(() => _handler.HandleAsync(request, CancellationToken.None));
        Assert.Equal(ErrorCodes.InvalidAddress, ex.ErrorCode);
    }

    [Fact]
    public async Task GivenPeerDisconnection_WhenHandleAsync_ThenThrowsConnectionException()
    {
        // Arrange
        var peerId = CreateDummyPubKey();
        var nodeInfo = $"{peerId}@127.0.0.1:9735";
        var fundingAmount = LightningMoney.Satoshis(100000);
        var request = new OpenChannelClientRequest(nodeInfo, fundingAmount);

        var peerModel = new PeerModel(peerId, "127.0.0.1", 9735, "ipv4");
        var peerServiceMock = new Mock<IPeerService>();
        peerServiceMock.Setup(x => x.Features).Returns(new FeatureOptions());
        peerModel.SetPeerService(peerServiceMock.Object);

        _peerManagerMock.Setup(x => x.GetPeer(peerId)).Returns(peerModel);
        _blockchainMonitorMock.Setup(x => x.LastProcessedBlockHeight).Returns(100u);
        _utxoMemoryRepositoryMock.Setup(x => x.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(200000));

        var channelModel = new ChannelModel(new ChannelParams(), CreateRandomChannelId(), null, null, true, null, null,
                                            fundingAmount,
                                            new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId),
                                            0, 0, LightningMoney.Zero, null, 0, peerId, 0, ChannelState.V1Opening,
                                            ChannelVersion.V1);
        _channelFactoryMock.Setup(x => x.CreateChannelV1AsInitiatorAsync(request, It.IsAny<FeatureOptions>(), peerId))
                           .ReturnsAsync(channelModel);
        _messageFactoryMock.Setup(x => x.CreateOpenChannel1Message(It.IsAny<ChannelId>(), It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<ChannelParty>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<ChannelFlags>(),
                                                                   It.IsAny<ChannelTypeTlv>(),
                                                                   It.IsAny<UpfrontShutdownScriptTlv>()))
                           .Returns(CreateDummyOpenChannel1Message(channelModel.ChannelId, fundingAmount, peerId));

        // Act
        var handleTask = _handler.HandleAsync(request, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        peerServiceMock.Raise(x => x.OnDisconnect += null, null!, new PeerDisconnectedEventArgs(peerId));

        // Assert
        await Assert.ThrowsAsync<ConnectionException>(() => handleTask);
        _utxoMemoryRepositoryMock.Verify(x => x.ReturnUtxosNotSpentOnChannel(channelModel.ChannelId), Times.Once);
    }

    [Fact]
    public async Task GivenAttentionMessage_WhenHandleAsync_ThenThrowsChannelErrorException()
    {
        // Arrange
        var peerId = CreateDummyPubKey();
        var nodeInfo = $"{peerId}@127.0.0.1:9735";
        var fundingAmount = LightningMoney.Satoshis(100000);
        var request = new OpenChannelClientRequest(nodeInfo, fundingAmount);

        var peerModel = new PeerModel(peerId, "127.0.0.1", 9735, "ipv4");
        var peerServiceMock = new Mock<IPeerService>();
        peerServiceMock.Setup(x => x.Features).Returns(new FeatureOptions());
        peerModel.SetPeerService(peerServiceMock.Object);

        _peerManagerMock.Setup(x => x.GetPeer(peerId)).Returns(peerModel);
        _blockchainMonitorMock.Setup(x => x.LastProcessedBlockHeight).Returns(100u);
        _utxoMemoryRepositoryMock.Setup(x => x.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(200000));

        var channelModel = new ChannelModel(new ChannelParams(), CreateRandomChannelId(), null, null, true, null, null,
                                            fundingAmount,
                                            new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId),
                                            0, 0, LightningMoney.Zero, null, 0, peerId, 0, ChannelState.V1Opening,
                                            ChannelVersion.V1);
        _channelFactoryMock.Setup(x => x.CreateChannelV1AsInitiatorAsync(request, It.IsAny<FeatureOptions>(), peerId))
                           .ReturnsAsync(channelModel);
        _messageFactoryMock.Setup(x => x.CreateOpenChannel1Message(It.IsAny<ChannelId>(), It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<ChannelParty>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<ChannelFlags>(),
                                                                   It.IsAny<ChannelTypeTlv>(),
                                                                   It.IsAny<UpfrontShutdownScriptTlv>()))
                           .Returns(CreateDummyOpenChannel1Message(channelModel.ChannelId, fundingAmount, peerId));

        // Act
        var handleTask = _handler.HandleAsync(request, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        peerServiceMock.Raise(x => x.OnAttentionMessageReceived += null, null!,
                              new AttentionMessageEventArgs("Error Message", peerId,
                                                            channelModel.ChannelId));

        // Assert
        await Assert.ThrowsAsync<ChannelErrorException>(() => handleTask);
        _utxoMemoryRepositoryMock.Verify(x => x.ReturnUtxosNotSpentOnChannel(channelModel.ChannelId), Times.Once);
    }

    [Fact]
    public async Task GivenExceptionRaised_WhenHandleAsync_ThenThrowsException()
    {
        // Arrange
        var peerId = CreateDummyPubKey();
        var nodeInfo = $"{peerId}@127.0.0.1:9735";
        var fundingAmount = LightningMoney.Satoshis(100000);
        var request = new OpenChannelClientRequest(nodeInfo, fundingAmount);

        var peerModel = new PeerModel(peerId, "127.0.0.1", 9735, "ipv4");
        var peerServiceMock = new Mock<IPeerService>();
        peerServiceMock.Setup(x => x.Features).Returns(new FeatureOptions());
        peerModel.SetPeerService(peerServiceMock.Object);

        _peerManagerMock.Setup(x => x.GetPeer(peerId)).Returns(peerModel);
        _blockchainMonitorMock.Setup(x => x.LastProcessedBlockHeight).Returns(100u);
        _utxoMemoryRepositoryMock.Setup(x => x.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(200000));

        var channelModel = new ChannelModel(new ChannelParams(), CreateRandomChannelId(), null, null, true, null, null,
                                            fundingAmount,
                                            new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId),
                                            0, 0, LightningMoney.Zero, null, 0, peerId, 0, ChannelState.V1Opening,
                                            ChannelVersion.V1);
        _channelFactoryMock.Setup(x => x.CreateChannelV1AsInitiatorAsync(request, It.IsAny<FeatureOptions>(), peerId))
                           .ReturnsAsync(channelModel);
        _messageFactoryMock.Setup(x => x.CreateOpenChannel1Message(It.IsAny<ChannelId>(), It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<ChannelParty>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<ChannelFlags>(),
                                                                   It.IsAny<ChannelTypeTlv>(),
                                                                   It.IsAny<UpfrontShutdownScriptTlv>()))
                           .Returns(CreateDummyOpenChannel1Message(channelModel.ChannelId, fundingAmount, peerId));

        // Act
        var handleTask = _handler.HandleAsync(request, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var expectedException = new ChannelErrorException("Critical error", channelModel.ChannelId);
        peerServiceMock.Raise(x => x.OnExceptionRaised += null, null!, expectedException);

        // Assert
        var ex = await Assert.ThrowsAsync<ChannelErrorException>(() => handleTask);
        Assert.Same(expectedException, ex);
        _utxoMemoryRepositoryMock.Verify(x => x.ReturnUtxosNotSpentOnChannel(channelModel.ChannelId), Times.Once);
    }

    [Fact]
    public async Task GivenAnAnchorsChannelThatWouldBreakTheReserve_WhenHandleAsync_ThenRefusedBeforeAnythingIsLocked()
    {
        // Arrange (NL-379 as opener)
        var peerId = CreateDummyPubKey();
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var request = new OpenChannelClientRequest($"{peerId}@127.0.0.1:9735", fundingAmount);
        var reserveMock = new Mock<IAnchorReserveService>();
        var channelModel = SetUpOpen(peerId, request, fundingAmount, anchors: true);
        reserveMock.Setup(x => x.EnsureCanFundAsync(fundingAmount, channelModel, It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new AnchorReserveException("Funding would leave the wallet below the anchors reserve",
                                                           fundingAmount + LightningMoney.Satoshis(10_000),
                                                           LightningMoney.Satoshis(105_000),
                                                           LightningMoney.Satoshis(10_000)));
        var handler = CreateHandlerWithReserve(reserveMock.Object);

        // Act
        var ex = await Assert.ThrowsAsync<ClientException>(() => handler.HandleAsync(request, CancellationToken.None));

        // Assert
        Assert.Equal(ErrorCodes.NotEnoughBalance, ex.ErrorCode);
        Assert.Contains("anchors reserve", ex.Message);
        reserveMock.Verify(x => x.LockFundingUtxosAsync(It.IsAny<LightningMoney>(), It.IsAny<ChannelModel>(),
                                                        It.IsAny<CancellationToken>()),
                           Times.Never);
        _utxoMemoryRepositoryMock.Verify(x => x.LockUtxosToSpendOnChannel(It.IsAny<LightningMoney>(),
                                                                          It.IsAny<ChannelId>()), Times.Never);
        _channelManagerMock.Verify(x => x.StartOpeningChannelAsync(peerId, channelModel, It.IsAny<IChannelMessage>()),
                                   Times.Never);
        reserveMock.Verify(x => x.ReleasePendingChannel(channelModel.ChannelId), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GivenTheReserveService_WhenHandleAsync_ThenTheFundingIsLockedThroughItWithTheAnchorsFlag()
    {
        // Arrange (NL-379, NL-385: the funding keeps the reserve and skips outputs of pending broadcasts)
        var peerId = CreateDummyPubKey();
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var request = new OpenChannelClientRequest($"{peerId}@127.0.0.1:9735", fundingAmount);
        var reserveMock = new Mock<IAnchorReserveService>();
        var channelModel = SetUpOpen(peerId, request, fundingAmount, anchors: true);
        var temporaryChannelId = channelModel.ChannelId;
        reserveMock.Setup(x => x.LockFundingUtxosAsync(fundingAmount, channelModel,
                                                       It.IsAny<CancellationToken>()))
                   .ReturnsAsync([]);
        var handler = CreateHandlerWithReserve(reserveMock.Object);
        var finalChannelId = CreateRandomChannelId();

        // Act
        var handleTask = handler.HandleAsync(request, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _channelMemoryRepositoryMock.Raise(x => x.OnChannelUpgraded += null, null!,
                                           new ChannelUpgradedEventArgs(channelModel.ChannelId, finalChannelId));
        var response = await handleTask;

        // Assert
        Assert.Equal(finalChannelId, response.ChannelId);
        reserveMock.Verify(x => x.EnsureCanFundAsync(fundingAmount, channelModel, It.IsAny<CancellationToken>()),
                           Times.Once);
        reserveMock.Verify(x => x.LockFundingUtxosAsync(fundingAmount, channelModel,
                                                        It.IsAny<CancellationToken>()), Times.Once);

        // The funded channel counts as a channel now, so its pending open is released (NL-379)
        reserveMock.Verify(x => x.ReleasePendingChannel(temporaryChannelId), Times.Once);
        _utxoMemoryRepositoryMock.Verify(x => x.LockUtxosToSpendOnChannel(It.IsAny<LightningMoney>(),
                                                                          It.IsAny<ChannelId>()), Times.Never);
    }

    [Fact]
    public async Task Given_TheFundingLockFindsTooFewUtxos_When_HandleAsync_Then_NotEnoughBalance()
    {
        // Arrange (NL-393: the selection throws InvalidOperationException, e.g. after a concurrent spend)
        var peerId = CreateDummyPubKey();
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var request = new OpenChannelClientRequest($"{peerId}@127.0.0.1:9735", fundingAmount);
        var channelModel = SetUpOpen(peerId, request, fundingAmount, anchors: false);
        _utxoMemoryRepositoryMock.Setup(x => x.LockUtxosToSpendOnChannel(fundingAmount, channelModel.ChannelId))
                                 .Throws(new InvalidOperationException("Insufficient funds"));

        // Act
        var ex = await Assert.ThrowsAsync<ClientException>(() => _handler.HandleAsync(request, CancellationToken.None));

        // Assert
        Assert.Equal(ErrorCodes.NotEnoughBalance, ex.ErrorCode);
        Assert.Contains("Insufficient funds", ex.Message);
        _channelManagerMock.Verify(x => x.StartOpeningChannelAsync(peerId, channelModel, It.IsAny<IChannelMessage>()),
                                   Times.Never);
    }

    [Fact]
    public async Task Given_TheReserveLockFindsNoUtxos_When_HandleAsync_Then_NotEnoughBalanceAndPendingOpenReleased()
    {
        // Arrange (NL-393 through the anchors reserve service)
        var peerId = CreateDummyPubKey();
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var request = new OpenChannelClientRequest($"{peerId}@127.0.0.1:9735", fundingAmount);
        var reserveMock = new Mock<IAnchorReserveService>();
        var channelModel = SetUpOpen(peerId, request, fundingAmount, anchors: true);
        reserveMock.Setup(x => x.LockFundingUtxosAsync(fundingAmount, channelModel, It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("No available UTXOs"));
        var handler = CreateHandlerWithReserve(reserveMock.Object);

        // Act
        var ex = await Assert.ThrowsAsync<ClientException>(() => handler.HandleAsync(request, CancellationToken.None));

        // Assert
        Assert.Equal(ErrorCodes.NotEnoughBalance, ex.ErrorCode);
        Assert.Contains("No available UTXOs", ex.Message);
        reserveMock.Verify(x => x.ReleasePendingChannel(channelModel.ChannelId), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Given_APeerThatNeverAnswers_When_TheOpenTimesOut_Then_ItFailsAndForgetsTheTemporaryChannel()
    {
        // Arrange (NL-392)
        var peerId = CreateDummyPubKey();
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var request = new OpenChannelClientRequest($"{peerId}@127.0.0.1:9735", fundingAmount);
        var channelModel = SetUpOpen(peerId, request, fundingAmount, anchors: false);
        _handler.OpenTimeout = TimeSpan.FromMilliseconds(100);

        // Act
        var ex = await Assert.ThrowsAsync<ClientException>(() => _handler.HandleAsync(request, CancellationToken.None));

        // Assert
        Assert.Equal(ErrorCodes.ConnectionError, ex.ErrorCode);
        Assert.Contains("did not answer open_channel", ex.Message);
        _channelMemoryRepositoryMock.Verify(x => x.TryRemoveTemporaryChannel(peerId, channelModel.ChannelId),
                                            Times.Once);
        _utxoMemoryRepositoryMock.Verify(x => x.ReturnUtxosNotSpentOnChannel(channelModel.ChannelId), Times.Once);
    }

    [Fact]
    public async Task Given_TheRequestIsCancelled_When_WaitingForAcceptChannel_Then_ItFailsAndForgetsTheTemporaryChannel()
    {
        // Arrange (NL-392: the IPC connection went away)
        var peerId = CreateDummyPubKey();
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var request = new OpenChannelClientRequest($"{peerId}@127.0.0.1:9735", fundingAmount);
        var channelModel = SetUpOpen(peerId, request, fundingAmount, anchors: false);
        using var cts = new CancellationTokenSource();

        // Act
        var handleTask = _handler.HandleAsync(request, cts.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handleTask);
        _channelMemoryRepositoryMock.Verify(x => x.TryRemoveTemporaryChannel(peerId, channelModel.ChannelId),
                                            Times.Once);
    }

    [Fact]
    public async Task Given_ThePeerRefusesTheOpen_When_HandleAsync_Then_TheTemporaryChannelIsForgotten()
    {
        // Arrange (NL-392: an error for the temporary channel)
        var peerId = CreateDummyPubKey();
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var request = new OpenChannelClientRequest($"{peerId}@127.0.0.1:9735", fundingAmount);
        var channelModel = SetUpOpen(peerId, request, fundingAmount, anchors: false);
        Assert.True(_peerManagerMock.Object.GetPeer(peerId)!.TryGetPeerService(out var peerService));
        var peerServiceMock = Mock.Get(peerService);

        // Act
        var handleTask = _handler.HandleAsync(request, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        peerServiceMock.Raise(x => x.OnAttentionMessageReceived += null, null!,
                              new AttentionMessageEventArgs("channel refused", peerId, channelModel.ChannelId));

        // Assert
        await Assert.ThrowsAsync<ChannelErrorException>(() => handleTask);
        _channelMemoryRepositoryMock.Verify(x => x.TryRemoveTemporaryChannel(peerId, channelModel.ChannelId),
                                            Times.Once);
    }

    [Fact]
    public async Task Given_AcceptChannelHoldsTheLock_When_TheOpenTimesOutAndTheHandlerUpgrades_Then_OpenSucceeds()
    {
        // Arrange (NL-392 review: the timeout fires while accept_channel's handler, under the temporary channel's
        // lock, already read our locked UTXOs; the cleanup must wait and must not return them)
        var peerId = CreateDummyPubKey();
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var request = new OpenChannelClientRequest($"{peerId}@127.0.0.1:9735", fundingAmount);
        var channelModel = SetUpOpen(peerId, request, fundingAmount, anchors: false);
        var lockProvider = new ChannelLockProvider();
        var handler = CreateHandlerWithLock(lockProvider);
        handler.OpenTimeout = TimeSpan.FromMilliseconds(100);
        var newChannelId = CreateRandomChannelId();
        var acceptLock = await lockProvider.AcquireAsync(channelModel.ChannelId,
                                                         TestContext.Current.CancellationToken);

        // Act
        var handleTask = handler.HandleAsync(request, CancellationToken.None);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var returnedWhileLocked = _utxoMemoryRepositoryMock.Invocations
                                                           .Any(i => i.Method.Name
                                                                  == nameof(IUtxoMemoryRepository
                                                                               .ReturnUtxosNotSpentOnChannel));
        _channelMemoryRepositoryMock.Raise(x => x.OnChannelUpgraded += null, null!,
                                           new ChannelUpgradedEventArgs(channelModel.ChannelId, newChannelId));
        acceptLock.Dispose();
        var response = await handleTask;

        // Assert
        Assert.False(returnedWhileLocked);
        Assert.Equal(newChannelId, response.ChannelId);
        _utxoMemoryRepositoryMock.Verify(x => x.ReturnUtxosNotSpentOnChannel(It.IsAny<ChannelId>()), Times.Never);
        _channelMemoryRepositoryMock.Verify(x => x.TryRemoveTemporaryChannel(It.IsAny<CompactPubKey>(),
                                                                             It.IsAny<ChannelId>()), Times.Never);
    }

    [Fact]
    public async Task Given_AcceptChannelHoldsTheLockAndFails_When_TheOpenTimesOut_Then_CleanupRunsAfterTheLock()
    {
        // Arrange (the accept handler releases the lock without upgrading the channel)
        var peerId = CreateDummyPubKey();
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var request = new OpenChannelClientRequest($"{peerId}@127.0.0.1:9735", fundingAmount);
        var channelModel = SetUpOpen(peerId, request, fundingAmount, anchors: false);
        var lockProvider = new ChannelLockProvider();
        var handler = CreateHandlerWithLock(lockProvider);
        handler.OpenTimeout = TimeSpan.FromMilliseconds(100);
        var acceptLock = await lockProvider.AcquireAsync(channelModel.ChannelId,
                                                         TestContext.Current.CancellationToken);

        // Act
        var handleTask = handler.HandleAsync(request, CancellationToken.None);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var completedWhileLocked = handleTask.IsCompleted;
        acceptLock.Dispose();
        var ex = await Assert.ThrowsAsync<ClientException>(() => handleTask);

        // Assert
        Assert.False(completedWhileLocked);
        Assert.Equal(ErrorCodes.ConnectionError, ex.ErrorCode);
        _utxoMemoryRepositoryMock.Verify(x => x.ReturnUtxosNotSpentOnChannel(channelModel.ChannelId), Times.Once);
        _channelMemoryRepositoryMock.Verify(x => x.TryRemoveTemporaryChannel(peerId, channelModel.ChannelId),
                                            Times.Once);
    }

    [Fact]
    public async Task Given_ADisposedWalletDuringTheFundingLock_When_HandleAsync_Then_NotReportedAsBalance()
    {
        // Arrange (only the selection's own InvalidOperationException means too few UTXOs, NL-393 review)
        var peerId = CreateDummyPubKey();
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var request = new OpenChannelClientRequest($"{peerId}@127.0.0.1:9735", fundingAmount);
        var channelModel = SetUpOpen(peerId, request, fundingAmount, anchors: true);
        var reserveMock = new Mock<IAnchorReserveService>();
        reserveMock.Setup(x => x.LockFundingUtxosAsync(fundingAmount, channelModel, It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new ObjectDisposedException("wallet"));
        var handler = CreateHandlerWithReserve(reserveMock.Object);

        // Act / Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handler.HandleAsync(request, CancellationToken.None));
        reserveMock.Verify(x => x.ReleasePendingChannel(channelModel.ChannelId), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Given_TheReserveCheckThrowsInvalidOperation_When_HandleAsync_Then_NotReportedAsBalance()
    {
        // Arrange (EnsureCanFundAsync is outside the NL-393 mapping)
        var peerId = CreateDummyPubKey();
        var fundingAmount = LightningMoney.Satoshis(100_000);
        var request = new OpenChannelClientRequest($"{peerId}@127.0.0.1:9735", fundingAmount);
        var channelModel = SetUpOpen(peerId, request, fundingAmount, anchors: true);
        var reserveMock = new Mock<IAnchorReserveService>();
        reserveMock.Setup(x => x.EnsureCanFundAsync(fundingAmount, channelModel, It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("duplicate pending channel"));
        var handler = CreateHandlerWithReserve(reserveMock.Object);

        // Act / Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(request,
                                                                             CancellationToken.None));
        Assert.Equal("duplicate pending channel", ex.Message);
        reserveMock.Verify(x => x.LockFundingUtxosAsync(It.IsAny<LightningMoney>(), It.IsAny<ChannelModel>(),
                                                        It.IsAny<CancellationToken>()), Times.Never);
    }

    private OpenChannelClientHandler CreateHandlerWithLock(IChannelLockProvider lockProvider) =>
        new(_blockchainMonitorMock.Object, _channelFactoryMock.Object, _channelManagerMock.Object,
            _channelMemoryRepositoryMock.Object, new Mock<ILogger<OpenChannelClientHandler>>().Object,
            _messageFactoryMock.Object, _peerManagerMock.Object, _utxoMemoryRepositoryMock.Object,
            channelLockProvider: lockProvider);

    private OpenChannelClientHandler CreateHandlerWithReserve(IAnchorReserveService reserveService) =>
        new(_blockchainMonitorMock.Object, _channelFactoryMock.Object, _channelManagerMock.Object,
            _channelMemoryRepositoryMock.Object, new Mock<ILogger<OpenChannelClientHandler>>().Object,
            _messageFactoryMock.Object, _peerManagerMock.Object, _utxoMemoryRepositoryMock.Object,
            anchorReserveService: reserveService);

    private ChannelModel SetUpOpen(CompactPubKey peerId, OpenChannelClientRequest request, LightningMoney fundingAmount,
                                   bool anchors)
    {
        var peerModel = new PeerModel(peerId, "127.0.0.1", 9735, "ipv4");
        var peerServiceMock = new Mock<IPeerService>();
        peerServiceMock.Setup(x => x.Features).Returns(new FeatureOptions());
        peerModel.SetPeerService(peerServiceMock.Object);
        _peerManagerMock.Setup(x => x.GetPeer(peerId)).Returns(peerModel);
        _blockchainMonitorMock.Setup(x => x.LastProcessedBlockHeight).Returns(100u);
        _utxoMemoryRepositoryMock.Setup(x => x.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(200_000));

        var party = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(1_000),
                                     LightningMoney.Satoshis(1), 30, fundingAmount, 144, null);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, anchors,
                                              FeatureSupport.No);
        var localKeySet = new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId);
        var channelModel = new ChannelModel(channelParams, CreateRandomChannelId(), null, null, true, null, null,
                                            fundingAmount, localKeySet, 0, 0, LightningMoney.Zero, null, 0, peerId, 0,
                                            ChannelState.V1Opening, ChannelVersion.V1);
        _channelFactoryMock.Setup(x => x.CreateChannelV1AsInitiatorAsync(request, It.IsAny<FeatureOptions>(), peerId))
                           .ReturnsAsync(channelModel);
        _messageFactoryMock.Setup(x => x.CreateOpenChannel1Message(It.IsAny<ChannelId>(), It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<ChannelParty>(),
                                                                   It.IsAny<LightningMoney>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                                   It.IsAny<CompactPubKey>(), It.IsAny<ChannelFlags>(),
                                                                   It.IsAny<ChannelTypeTlv>(),
                                                                   It.IsAny<UpfrontShutdownScriptTlv>()))
                           .Returns(CreateDummyOpenChannel1Message(channelModel.ChannelId, fundingAmount, peerId));
        return channelModel;
    }

    private static ChannelId CreateRandomChannelId()
    {
        var bytes = new byte[32];
        Random.Shared.NextBytes(bytes);
        return new ChannelId(bytes);
    }

    private static CompactPubKey CreateDummyPubKey()
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        return new CompactPubKey(bytes);
    }

    private static OpenChannel1Message CreateDummyOpenChannel1Message(ChannelId tempChannelId,
                                                                      LightningMoney fundingAmount,
                                                                      CompactPubKey peerId)
    {
        var payload = new OpenChannel1Payload(new ChainHash(new byte[32]), new ChannelFlags(ChannelFlag.None),
                                              tempChannelId, LightningMoney.Zero, peerId, LightningMoney.Zero,
                                              LightningMoney.Zero, peerId, fundingAmount, peerId, peerId,
                                              LightningMoney.Zero, 483, LightningMoney.Zero, peerId,
                                              LightningMoney.Zero, peerId, 144);
        var channelTypeTlv = new ChannelTypeTlv([]);
        return new OpenChannel1Message(payload, channelTypeTlv);
    }
}