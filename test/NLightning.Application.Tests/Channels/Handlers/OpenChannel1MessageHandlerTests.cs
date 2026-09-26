using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NLightning.Application.Channels.Handlers;
using NLightning.Domain.Bitcoin.Transactions.Outputs;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Interfaces;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Enums;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Money;
using NLightning.Domain.Node;
using NLightning.Domain.Node.Options;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.Messages;
using NLightning.Domain.Protocol.Models;
using NLightning.Domain.Protocol.Payloads;
using NLightning.Domain.Protocol.Tlv;
using NLightning.Domain.Protocol.ValueObjects;
using NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Handlers;

public class OpenChannel1MessageHandlerTests
{
    private readonly Mock<IChannelFactory> _mockChannelFactory;
    private readonly Mock<IChannelMemoryRepository> _mockChannelMemoryRepository;
    private readonly Mock<IMessageFactory> _mockMessageFactory;
    private readonly OpenChannel1MessageHandler _handler;
    private readonly CompactPubKey _peerPubKey;
    private readonly OpenChannel1Message _validMessage;
    private readonly FeatureOptions _negotiatedFeatures;
    private readonly ChannelModel _channel;

    private static readonly IOptions<NodeOptions> s_regtestOptions =
        Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest });

    public OpenChannel1MessageHandlerTests()
    {
        _mockChannelFactory = new Mock<IChannelFactory>();
        _mockChannelMemoryRepository = new Mock<IChannelMemoryRepository>();
        _mockMessageFactory = new Mock<IMessageFactory>();

        _handler = new OpenChannel1MessageHandler(_mockChannelFactory.Object, _mockChannelMemoryRepository.Object,
                                                  new Mock<ILogger<OpenChannel1MessageHandler>>().Object,
                                                  _mockMessageFactory.Object, nodeOptions: s_regtestOptions);

        // Setup test data
        CompactPubKey emptyPubKey = new byte[]
        {
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00
        };
        _peerPubKey = emptyPubKey;
        _negotiatedFeatures = new FeatureOptions();

        var channelReserveAmount = LightningMoney.Satoshis(1_000);
        var dustLimitAmount = LightningMoney.Satoshis(354);
        var feeRateAmountPerKw = LightningMoney.Zero;
        var htlcMinimumAmount = LightningMoney.Satoshis(1);
        const ushort maxAcceptedHtlcs = 10;
        var maxHtlcAmountInFlight = LightningMoney.Satoshis(10_000);
        const ushort toSelfDelay = 144;
        var fundingAmount = LightningMoney.Satoshis(10_000);

        // Create a valid OpenChannel1Message
        var channelId = ChannelId.Zero;
        var payload =
            new OpenChannel1Payload(BitcoinNetwork.Mainnet.ChainHash, new ChannelFlags(ChannelFlag.AnnounceChannel),
                                    channelId, channelReserveAmount, emptyPubKey, dustLimitAmount, feeRateAmountPerKw,
                                    emptyPubKey, fundingAmount, emptyPubKey, emptyPubKey, htlcMinimumAmount,
                                    maxAcceptedHtlcs, maxHtlcAmountInFlight, emptyPubKey, LightningMoney.Zero,
                                    emptyPubKey, toSelfDelay);
        _validMessage =
            new OpenChannel1Message(payload, new ChannelTypeTlv(FeatureSet.NewBasicChannelType()));

        // Setup ChannelConfig
        var channelConfig = TestChannelParams.Create(channelReserveAmount, feeRateAmountPerKw, htlcMinimumAmount,
                                              dustLimitAmount, maxAcceptedHtlcs, maxHtlcAmountInFlight, 3, false,
                                              dustLimitAmount, toSelfDelay, FeatureSupport.No);

        // Create a real ChannelKeySetModel instance instead of mocking it
        var keySet = new ChannelKeySetModel(0, emptyPubKey, emptyPubKey, emptyPubKey, emptyPubKey, emptyPubKey,
                                            emptyPubKey);

        // Setup channel and related objects
        _channel = new ChannelModel(channelConfig, channelId,
                                    new CommitmentNumber(emptyPubKey, emptyPubKey, new FakeSha256()),
                                    new FundingOutputInfo(fundingAmount, emptyPubKey, emptyPubKey), false, null,
                                    null, LightningMoney.Zero, keySet, 1, 0, fundingAmount, keySet, 1,
                                    _peerPubKey, 0, ChannelState.V1Opening, ChannelVersion.V1);

        // Setup factory to return our mocked channel
        _mockChannelFactory
           .Setup(x => x.CreateChannelV1AsNonInitiatorAsync(It.IsAny<OpenChannel1Message>(), It.IsAny<FeatureOptions>(),
                                                            It.IsAny<CompactPubKey>()))
           .ReturnsAsync(_channel);

        // Setup message factory
        _mockMessageFactory
           .Setup(x => x.CreateAcceptChannel1Message(It.IsAny<ChannelParty>(), It.IsAny<ChannelTypeTlv>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<uint>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                     It.IsAny<UpfrontShutdownScriptTlv>()))
           .Returns(new AcceptChannel1Message(
                        new AcceptChannel1Payload(channelId, channelReserveAmount, emptyPubKey, dustLimitAmount,
                                                  emptyPubKey, emptyPubKey, emptyPubKey, htlcMinimumAmount,
                                                  maxAcceptedHtlcs, maxHtlcAmountInFlight, 3, emptyPubKey,
                                                  emptyPubKey, toSelfDelay),
                        new ChannelTypeTlv(FeatureSet.NewBasicChannelType())));
    }

    [Fact]
    public async Task HandleAsync_ValidMessage_CreatesChannelAndReturnsAcceptChannelMessage()
    {
        // Arrange
        _mockChannelMemoryRepository
           .Setup(x => x.TryGetTemporaryChannelState(It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                     out It.Ref<ChannelState>.IsAny))
           .Returns(false);

        // Act
        var result = await _handler.HandleAsync(_validMessage, ChannelState.None, _negotiatedFeatures, _peerPubKey);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<AcceptChannel1Message>(Assert.Single(result));

        _mockChannelFactory.Verify(
            x => x.CreateChannelV1AsNonInitiatorAsync(_validMessage, _negotiatedFeatures, _peerPubKey),
            Times.Once);

        _mockChannelMemoryRepository.Verify(x => x.AddTemporaryChannel(_peerPubKey, _channel), Times.Once);

        _mockMessageFactory.Verify(
            x => x.CreateAcceptChannel1Message(
                _channel.ChannelParams.Local,
                It.IsAny<ChannelTypeTlv>(),
                _channel.LocalKeySet.DelayedPaymentCompactBasepoint,
                _channel.LocalKeySet.CurrentPerCommitmentCompactPoint,
                _channel.LocalKeySet.FundingCompactPubKey, _channel.LocalKeySet.HtlcCompactBasepoint,
                _channel.ChannelParams.MinimumDepth, _channel.LocalKeySet.PaymentCompactBasepoint,
                _channel.LocalKeySet.RevocationCompactBasepoint, _channel.ChannelId,
                It.IsAny<UpfrontShutdownScriptTlv>()),
            Times.Once);
    }

    [Fact]
    public async Task Given_ValidMessage_When_HandleAsync_Then_ChannelTypeTlvIsBigEndianStaticRemoteKey()
    {
        // Arrange
        _mockChannelMemoryRepository
           .Setup(x => x.TryGetTemporaryChannelState(It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                     out It.Ref<ChannelState>.IsAny))
           .Returns(false);

        ChannelTypeTlv? capturedChannelType = null;
        _mockMessageFactory
           .Setup(x => x.CreateAcceptChannel1Message(It.IsAny<ChannelParty>(), It.IsAny<ChannelTypeTlv>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<uint>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                     It.IsAny<UpfrontShutdownScriptTlv>()))
           .Callback(new InvocationAction(invocation => capturedChannelType = (ChannelTypeTlv)invocation.Arguments[1]))
           .Returns((AcceptChannel1Message)null!);

        // Act
        await _handler.HandleAsync(_validMessage, ChannelState.None, _negotiatedFeatures, _peerPubKey);

        // Assert
        Assert.NotNull(capturedChannelType);
        Assert.Equal(new byte[] { 0x10, 0x00 }, capturedChannelType.Value);
        Assert.True(capturedChannelType.Features.IsFeatureSet(Feature.OptionStaticRemoteKey, true));
        Assert.False(capturedChannelType.Features.HasFeature(Feature.OptionUpfrontShutdownScript));
    }

    [Fact]
    public async Task HandleAsync_WithUpfrontShutdownScript_IncludesItInResponse()
    {
        // Arrange
        _mockChannelMemoryRepository
           .Setup(x => x.TryGetTemporaryChannelState(It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                     out It.Ref<ChannelState>.IsAny))
           .Returns(false);

        // Setup channel with upfront shutdown script
        var channelParams = _channel.ChannelParams;
        var local = channelParams.Local;
        var localWithScript = new ChannelParty(local.DustLimitAmount, local.ChannelReserveAmount,
                                               local.HtlcMinimumAmount, local.MaxAcceptedHtlcs,
                                               local.MaxHtlcValueInFlight, local.ToSelfDelay,
                                               new BitcoinScript([1, 2, 3]));
        var channel = new ChannelModel(new ChannelParams(localWithScript, channelParams.Remote,
                                                         channelParams.FeeRateAmountPerKw, channelParams.MinimumDepth,
                                                         channelParams.OptionAnchorOutputs, channelParams.UseScidAlias),
                                       _channel.ChannelId,
                                       _channel.CommitmentNumber, _channel.FundingOutput, _channel.IsInitiator,
                                       _channel.LastSentSignature, _channel.LastReceivedSignature,
                                       _channel.LocalBalance, _channel.LocalKeySet, _channel.LocalNextHtlcId,
                                       _channel.LocalRevocationNumber, _channel.RemoteBalance,
                                       _channel.RemoteKeySet, _channel.RemoteNextHtlcId, _peerPubKey,
                                       _channel.RemoteRevocationNumber, ChannelState.V1Opening,
                                       ChannelVersion.V1);

        _mockChannelFactory
           .Setup(x => x.CreateChannelV1AsNonInitiatorAsync(It.IsAny<OpenChannel1Message>(), It.IsAny<FeatureOptions>(),
                                                            It.IsAny<CompactPubKey>()))
           .ReturnsAsync(channel);

        // Act
        var result = await _handler.HandleAsync(_validMessage, ChannelState.None, _negotiatedFeatures, _peerPubKey);

        // Assert
        Assert.Single(result);

        _mockMessageFactory.Verify(
            x => x.CreateAcceptChannel1Message(It.IsAny<ChannelParty>(), It.IsAny<ChannelTypeTlv>(),
                                               It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                               It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                               It.IsAny<uint>(), It.IsAny<CompactPubKey>(),
                                               It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                               It.Is<UpfrontShutdownScriptTlv>(t => t.Value.Length == 3)),
            Times.Once);
    }

    [Fact]
    public async Task Given_RemoteParams_When_HandleAsync_Then_AcceptCarriesOurValues()
    {
        // Arrange: the opener's values differ from ours on every field (NL-194)
        var ourParams = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(2_000),
                                         LightningMoney.MilliSatoshis(1), 30, LightningMoney.Satoshis(8_000), 720);
        var theirParams = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(1_000),
                                           LightningMoney.Satoshis(1), 10, LightningMoney.Satoshis(10_000), 144);
        var channel = new ChannelModel(new ChannelParams(ourParams, theirParams, LightningMoney.Zero, 3, false,
                                                         FeatureSupport.No), _channel.ChannelId,
                                       _channel.CommitmentNumber, _channel.FundingOutput, false, null, null,
                                       LightningMoney.Zero, _channel.LocalKeySet, 0, 0, _channel.RemoteBalance,
                                       _channel.RemoteKeySet, 0, _peerPubKey, 0, ChannelState.V1Opening,
                                       ChannelVersion.V1);
        _mockChannelFactory
           .Setup(x => x.CreateChannelV1AsNonInitiatorAsync(It.IsAny<OpenChannel1Message>(), It.IsAny<FeatureOptions>(),
                                                            It.IsAny<CompactPubKey>()))
           .ReturnsAsync(channel);

        ChannelParty? sentParams = null;
        _mockMessageFactory
           .Setup(x => x.CreateAcceptChannel1Message(It.IsAny<ChannelParty>(), It.IsAny<ChannelTypeTlv>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<uint>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                     It.IsAny<UpfrontShutdownScriptTlv>()))
           .Callback(new InvocationAction(invocation => sentParams = (ChannelParty)invocation.Arguments[0]))
           .Returns((AcceptChannel1Message)null!);

        // Act
        await _handler.HandleAsync(_validMessage, ChannelState.None, _negotiatedFeatures, _peerPubKey);

        // Assert
        Assert.Equal(ourParams, sentParams);
    }

    [Fact]
    public async Task Given_OpenerChannelTypeWithScidAlias_When_HandleAsync_Then_AcceptEchoesTheOpenerChannelType()
    {
        // Arrange: BOLT 2 accept_channel MUST set channel_type to the one from open_channel (NL-218)
        var openerChannelType = FeatureSet.NewBasicChannelType();
        openerChannelType.SetFeature(Feature.OptionScidAlias, true);
        var message = new OpenChannel1Message(_validMessage.Payload, new ChannelTypeTlv(openerChannelType));

        ChannelTypeTlv? capturedChannelType = null;
        _mockMessageFactory
           .Setup(x => x.CreateAcceptChannel1Message(It.IsAny<ChannelParty>(), It.IsAny<ChannelTypeTlv>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<uint>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                     It.IsAny<UpfrontShutdownScriptTlv>()))
           .Callback(new InvocationAction(invocation => capturedChannelType = (ChannelTypeTlv)invocation.Arguments[1]))
           .Returns((AcceptChannel1Message)null!);

        // Act
        await _handler.HandleAsync(message, ChannelState.None, _negotiatedFeatures, _peerPubKey);

        // Assert
        Assert.NotNull(capturedChannelType);
        Assert.Equal(message.ChannelTypeTlv!.ChannelType, capturedChannelType.ChannelType);
    }

    [Fact]
    public async Task HandleAsync_WhenChannelStateIsNotNone_ThrowsChannelErrorException()
    {
        // Act & Assert
        var exception =
            await Assert.ThrowsAsync<ChannelErrorException>(() => _handler.HandleAsync(
                                                                _validMessage, ChannelState.Open, _negotiatedFeatures,
                                                                _peerPubKey));

        Assert.Equal("A channel with this id already exists", exception.Message);
        Assert.Equal(_validMessage.Payload.ChannelId, exception.ChannelId);
    }

    [Fact]
    public async Task HandleAsync_WithExistingTemporaryChannelInWrongState_ThrowsChannelErrorException()
    {
        // Arrange
        var outState = ChannelState.None;
        _mockChannelMemoryRepository
           .Setup(x => x.TryGetTemporaryChannelState(It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(), out outState))
           .Callback((CompactPubKey _, ChannelId _, out ChannelState state) =>
            {
                state = ChannelState.Open; // Wrong state
                outState = state;
            })
           .Returns(true);

        // Act & Assert
        var exception =
            await Assert.ThrowsAsync<ChannelErrorException>(() => _handler.HandleAsync(
                                                                _validMessage, ChannelState.None, _negotiatedFeatures,
                                                                _peerPubKey));

        Assert.Equal("Channel had the wrong state", exception.Message);
        Assert.Equal("This channel is already being negotiated with peer", exception.PeerMessage);
    }

    [Fact]
    public async Task HandleAsync_WithExistingTemporaryChannelInCorrectState_ProcessesNormally()
    {
        // Arrange
        var outState = ChannelState.None;
        _mockChannelMemoryRepository
           .Setup(x => x.TryGetTemporaryChannelState(It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(), out outState))
           .Callback((CompactPubKey _, ChannelId _, out ChannelState state) =>
            {
                state = ChannelState.V1Opening; // Correct state
                outState = state;
            })
           .Returns(true);

        // Act
        var result = await _handler.HandleAsync(_validMessage, ChannelState.None, _negotiatedFeatures, _peerPubKey);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<AcceptChannel1Message>(Assert.Single(result));
    }

    [Fact]
    public async Task Given_ChainProcessingHalted_When_OpenChannelReceived_Then_RefusedWithoutAChannel()
    {
        // Arrange - NL-216: a node blind to the chain could not see the funding confirm nor be cheated on it
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.IsChainProcessingHalted).Returns(true);
        var handler = new OpenChannel1MessageHandler(_mockChannelFactory.Object, _mockChannelMemoryRepository.Object,
                                                     new Mock<ILogger<OpenChannel1MessageHandler>>().Object,
                                                     _mockMessageFactory.Object, monitor.Object);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => handler.HandleAsync(_validMessage, ChannelState.None, _negotiatedFeatures,
                                                      _peerPubKey));

        // Assert
        Assert.Contains("chain processing is halted", exception.Message);
        Assert.Equal(_validMessage.Payload.ChannelId, exception.ChannelId);
        _mockChannelFactory.Verify(x => x.CreateChannelV1AsNonInitiatorAsync(It.IsAny<OpenChannel1Message>(),
                                                                             It.IsAny<FeatureOptions>(),
                                                                             It.IsAny<CompactPubKey>()), Times.Never);
        _mockChannelMemoryRepository.Verify(x => x.AddTemporaryChannel(It.IsAny<CompactPubKey>(),
                                                                        It.IsAny<ChannelModel>()), Times.Never);
    }

    [Fact]
    public async Task Given_PublicChannelsRefused_When_APublicOpenChannelArrives_Then_ErrorWithoutAChannel()
    {
        // Arrange - NL-341: BOLT 2 lets the receiver fail a channel whose announce_channel it does not want
        var handler = new OpenChannel1MessageHandler(_mockChannelFactory.Object, _mockChannelMemoryRepository.Object,
                                                     new Mock<ILogger<OpenChannel1MessageHandler>>().Object,
                                                     _mockMessageFactory.Object,
                                                     gossipOptions: Options.Create(
                                                         new GossipOptions { AcceptPublicChannels = false }));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => handler.HandleAsync(_validMessage, ChannelState.None, _negotiatedFeatures,
                                                      _peerPubKey));

        // Assert
        Assert.True(_validMessage.Payload.ChannelFlags.AnnounceChannel);
        Assert.Equal(_validMessage.Payload.ChannelId, exception.ChannelId);
        Assert.Equal("We don't accept public channels", exception.PeerMessage);
        _mockChannelFactory.Verify(x => x.CreateChannelV1AsNonInitiatorAsync(It.IsAny<OpenChannel1Message>(),
                                                                             It.IsAny<FeatureOptions>(),
                                                                             It.IsAny<CompactPubKey>()), Times.Never);
    }

    [Fact]
    public async Task Given_PublicChannelsAccepted_When_APublicOpenChannelArrives_Then_ItIsAccepted()
    {
        // Arrange (the default)
        var handler = new OpenChannel1MessageHandler(_mockChannelFactory.Object, _mockChannelMemoryRepository.Object,
                                                     new Mock<ILogger<OpenChannel1MessageHandler>>().Object,
                                                     _mockMessageFactory.Object,
                                                     gossipOptions: Options.Create(new GossipOptions()),
                                                     nodeOptions: s_regtestOptions);

        // Act
        var result = await handler.HandleAsync(_validMessage, ChannelState.None, _negotiatedFeatures, _peerPubKey);

        // Assert
        Assert.IsType<AcceptChannel1Message>(Assert.Single(result));
    }

    [Fact]
    public async Task Given_MainnetWithDefaultGossipOptions_When_APublicOpenChannelArrives_Then_ErrorWithoutAChannel()
    {
        // Arrange - BOLT 7: the fundee MUST send announcement_signatures for a public channel, which the D12 gate
        // forbids on mainnet by default, so the channel is refused instead of silently left unannounced
        var handler = new OpenChannel1MessageHandler(_mockChannelFactory.Object, _mockChannelMemoryRepository.Object,
                                                     new Mock<ILogger<OpenChannel1MessageHandler>>().Object,
                                                     _mockMessageFactory.Object,
                                                     gossipOptions: Options.Create(new GossipOptions()),
                                                     nodeOptions: Options.Create(
                                                         new NodeOptions { BitcoinNetwork = BitcoinNetwork.Mainnet }));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => handler.HandleAsync(_validMessage, ChannelState.None, _negotiatedFeatures,
                                                      _peerPubKey));

        // Assert
        Assert.True(_validMessage.Payload.ChannelFlags.AnnounceChannel);
        Assert.Equal(_validMessage.Payload.ChannelId, exception.ChannelId);
        Assert.Equal("We don't accept public channels", exception.PeerMessage);
        _mockChannelFactory.Verify(x => x.CreateChannelV1AsNonInitiatorAsync(It.IsAny<OpenChannel1Message>(),
                                                                             It.IsAny<FeatureOptions>(),
                                                                             It.IsAny<CompactPubKey>()), Times.Never);
    }

    [Fact]
    public async Task Given_MainnetWithPublicChannelsAllowed_When_APublicOpenChannelArrives_Then_ItIsAccepted()
    {
        // Arrange
        var handler = new OpenChannel1MessageHandler(_mockChannelFactory.Object, _mockChannelMemoryRepository.Object,
                                                     new Mock<ILogger<OpenChannel1MessageHandler>>().Object,
                                                     _mockMessageFactory.Object,
                                                     gossipOptions: Options.Create(
                                                         new GossipOptions { AllowPublicChannelsOnMainnet = true }),
                                                     nodeOptions: Options.Create(
                                                         new NodeOptions { BitcoinNetwork = BitcoinNetwork.Mainnet }));

        // Act
        var result = await handler.HandleAsync(_validMessage, ChannelState.None, _negotiatedFeatures, _peerPubKey);

        // Assert
        Assert.IsType<AcceptChannel1Message>(Assert.Single(result));
    }
}