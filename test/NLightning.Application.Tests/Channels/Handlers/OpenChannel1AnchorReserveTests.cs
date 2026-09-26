using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// NL-379 as fundee: an <c>option_anchors</c> channel is refused with an <c>error</c> when the wallet cannot keep the
/// anchors reserve with it; a channel without anchors needs no reserve.
/// </summary>
public class OpenChannel1AnchorReserveTests
{
    private static readonly CompactPubKey s_pubKey = new byte[]
    {
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    private readonly Mock<IAnchorReserveService> _reserve = new();
    private readonly Mock<IChannelFactory> _channelFactory = new();
    private readonly Mock<IChannelMemoryRepository> _channelMemory = new();
    private readonly Mock<IMessageFactory> _messageFactory = new();

    [Fact]
    public async Task Given_AnAnchorsChannelAndTooLittleForTheReserve_When_Opened_Then_ItIsRefusedWithAnError()
    {
        // Arrange
        var handler = CreateHandler(anchors: true);
        _reserve.Setup(r => r.EnsureCanAcceptAnchorsChannelAsync(It.IsAny<ChannelModel>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AnchorReserveException("Not enough confirmed on-chain funds for the anchors reserve",
                                                        LightningMoney.Satoshis(10_000), LightningMoney.Zero,
                                                        LightningMoney.Satoshis(10_000)));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
            () => handler.HandleAsync(CreateMessage(), ChannelState.None, new FeatureOptions(), s_pubKey));

        // Assert: the peer gets an error for the temporary channel, and the channel is never stored
        Assert.Equal(ChannelId.Zero, exception.ChannelId);
        Assert.Contains("anchors reserve", exception.PeerMessage);
        _channelMemory.Verify(m => m.AddTemporaryChannel(It.IsAny<CompactPubKey>(), It.IsAny<ChannelModel>()),
                              Times.Never);
    }

    [Fact]
    public async Task Given_AnAnchorsChannelAndEnoughForTheReserve_When_Opened_Then_ItIsAccepted()
    {
        // Arrange
        var handler = CreateHandler(anchors: true);
        _reserve.Setup(r => r.EnsureCanAcceptAnchorsChannelAsync(It.IsAny<ChannelModel>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        // Act
        var replies = await handler.HandleAsync(CreateMessage(), ChannelState.None, new FeatureOptions(), s_pubKey);

        // Assert
        Assert.IsType<AcceptChannel1Message>(Assert.Single(replies));
        _reserve.Verify(r => r.EnsureCanAcceptAnchorsChannelAsync(It.IsAny<ChannelModel>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_AChannelWithoutAnchors_When_Opened_Then_TheReserveIsNotChecked()
    {
        // Arrange
        var handler = CreateHandler(anchors: false);

        // Act
        var replies = await handler.HandleAsync(CreateMessage(), ChannelState.None, new FeatureOptions(), s_pubKey);

        // Assert
        Assert.Single(replies);
        _reserve.Verify(r => r.EnsureCanAcceptAnchorsChannelAsync(It.IsAny<ChannelModel>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private OpenChannel1MessageHandler CreateHandler(bool anchors)
    {
        var dust = LightningMoney.Satoshis(354);
        var reserve = LightningMoney.Satoshis(1_000);
        var funding = LightningMoney.Satoshis(100_000);
        var channelParams = TestChannelParams.Create(reserve, LightningMoney.Satoshis(253), LightningMoney.Satoshis(1),
                                                     dust, 10, funding, 3, anchors, dust, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey);
        var channel = new ChannelModel(channelParams, ChannelId.Zero,
                                       new CommitmentNumber(s_pubKey, s_pubKey, new FakeSha256()),
                                       new FundingOutputInfo(funding, s_pubKey, s_pubKey), false, null, null,
                                       LightningMoney.Zero, keySet, 1, 0, funding, keySet, 1, s_pubKey, 0,
                                       ChannelState.V1Opening, ChannelVersion.V1);
        _channelFactory.Setup(f => f.CreateChannelV1AsNonInitiatorAsync(It.IsAny<OpenChannel1Message>(),
                                                                        It.IsAny<FeatureOptions>(),
                                                                        It.IsAny<CompactPubKey>()))
                       .ReturnsAsync(channel);
        _messageFactory
           .Setup(x => x.CreateAcceptChannel1Message(It.IsAny<ChannelParty>(), It.IsAny<ChannelTypeTlv>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<uint>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                     It.IsAny<UpfrontShutdownScriptTlv>()))
           .Returns(new AcceptChannel1Message(
                        new AcceptChannel1Payload(ChannelId.Zero, reserve, s_pubKey, dust, s_pubKey, s_pubKey,
                                                  s_pubKey, LightningMoney.Satoshis(1), 10, funding, 3, s_pubKey,
                                                  s_pubKey, 144),
                        new ChannelTypeTlv(FeatureSet.NewBasicChannelType())));

        return new OpenChannel1MessageHandler(_channelFactory.Object, _channelMemory.Object,
                                              NullLogger<OpenChannel1MessageHandler>.Instance,
                                              _messageFactory.Object,
                                              nodeOptions: Options.Create(
                                                  new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                              anchorReserveService: _reserve.Object);
    }

    private static OpenChannel1Message CreateMessage()
    {
        var payload = new OpenChannel1Payload(BitcoinNetwork.Regtest.ChainHash, new ChannelFlags(ChannelFlag.None),
                                              ChannelId.Zero, LightningMoney.Satoshis(1_000), s_pubKey,
                                              LightningMoney.Satoshis(354), LightningMoney.Satoshis(253), s_pubKey,
                                              LightningMoney.Satoshis(100_000), s_pubKey, s_pubKey,
                                              LightningMoney.Satoshis(1), 10, LightningMoney.Satoshis(100_000),
                                              s_pubKey, LightningMoney.Zero, s_pubKey, 144);
        return new OpenChannel1Message(payload, new ChannelTypeTlv(FeatureSet.NewBasicChannelType()));
    }
}