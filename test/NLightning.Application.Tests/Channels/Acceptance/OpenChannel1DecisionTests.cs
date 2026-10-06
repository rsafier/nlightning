using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Acceptance;

using Application.Channels.Acceptance;
using Application.Channels.Handlers;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Acceptance;
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
/// NL-1180 on the v1 accepter: an external decider answers before anything is created for the open; a rejection is the
/// <c>error</c> the opener gets, an acceptance's values are what <c>accept_channel</c> announces.
/// </summary>
public class OpenChannel1DecisionTests
{
    private static readonly CompactPubKey s_pubKey = new byte[]
    {
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    private readonly Mock<IChannelFactory> _channelFactory = new();
    private readonly Mock<IChannelMemoryRepository> _channelMemory = new();
    private readonly Mock<IMessageFactory> _messageFactory = new();
    private readonly ChannelOpenDecisionGate _gate = new(NullLogger<ChannelOpenDecisionGate>.Instance);
    private ChannelParty? _announced;
    private uint? _announcedDepth;

    [Fact]
    public async Task Given_ADeciderThatRejects_When_Opened_Then_TheOpenerGetsItsErrorAndNothingIsCreated()
    {
        // Arrange
        var handler = CreateHandler();
        using var registration = _gate.Register(
            new ChannelOpenDecisionGateTests.FixedDecider(ChannelOpenDecision.Rejected("private channels only")));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
            () => handler.HandleAsync(CreateMessage(), ChannelState.None, new FeatureOptions(), s_pubKey));

        // Assert
        Assert.Equal("private channels only", exception.PeerMessage);
        _channelFactory.Verify(f => f.CreateChannelV1AsNonInitiatorAsync(It.IsAny<OpenChannel1Message>(),
                                                                         It.IsAny<FeatureOptions>(),
                                                                         It.IsAny<CompactPubKey>()), Times.Never);
        _channelMemory.Verify(m => m.AddTemporaryChannel(It.IsAny<CompactPubKey>(), It.IsAny<ChannelModel>()),
                              Times.Never);
    }

    [Fact]
    public async Task Given_ADeciderThatAcceptsWithValues_When_Opened_Then_AcceptChannelAnnouncesThem()
    {
        // Arrange
        var handler = CreateHandler();
        using var registration = _gate.Register(new ChannelOpenDecisionGateTests.FixedDecider(new ChannelOpenDecision
        {
            Accept = true,
            ToSelfDelay = 500,
            MaxAcceptedHtlcs = 20,
            HtlcMinimum = LightningMoney.MilliSatoshis(2_000),
            MinimumDepth = 6
        }));

        // Act
        var replies = await handler.HandleAsync(CreateMessage(), ChannelState.None, new FeatureOptions(), s_pubKey);

        // Assert
        Assert.IsType<AcceptChannel1Message>(Assert.Single(replies));
        Assert.NotNull(_announced);
        Assert.Equal((ushort)500, _announced.Value.ToSelfDelay);
        Assert.Equal((ushort)20, _announced.Value.MaxAcceptedHtlcs);
        Assert.Equal(LightningMoney.MilliSatoshis(2_000), _announced.Value.HtlcMinimumAmount);
        Assert.Equal(6U, _announcedDepth);
    }

    [Fact]
    public async Task Given_ADeciderThatAsksForZeroConf_When_Opened_Then_TheOpenIsRefused()
    {
        // Arrange
        var handler = CreateHandler();
        using var registration = _gate.Register(
            new ChannelOpenDecisionGateTests.FixedDecider(new ChannelOpenDecision { Accept = true, ZeroConf = true }));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
            () => handler.HandleAsync(CreateMessage(), ChannelState.None, new FeatureOptions(), s_pubKey));

        // Assert
        Assert.Equal(ChannelOpenDecision.GenericRejection, exception.PeerMessage);
        _channelMemory.Verify(m => m.AddTemporaryChannel(It.IsAny<CompactPubKey>(), It.IsAny<ChannelModel>()),
                              Times.Never);
    }

    [Fact]
    public async Task Given_AnUpfrontScriptWithoutTheFeature_When_Opened_Then_TheOpenIsRefused()
    {
        // Arrange: LND fails the open when the acceptor sets upfront_shutdown for a peer without the feature
        var handler = CreateHandler();
        var script = new BitcoinScript([0x00, 0x14, .. Enumerable.Repeat((byte)0x01, 20)]);
        using var registration = _gate.Register(new ChannelOpenDecisionGateTests.FixedDecider(
                                                    new ChannelOpenDecision { Accept = true, UpfrontShutdownScript = script }));

        // Act / Assert
        await Assert.ThrowsAsync<ChannelErrorException>(
            () => handler.HandleAsync(CreateMessage(), ChannelState.None, new FeatureOptions(), s_pubKey));
    }

    [Fact]
    public async Task Given_NoDecider_When_Opened_Then_TheNodesOwnValuesAreAnnounced()
    {
        // Arrange
        var handler = CreateHandler();

        // Act
        await handler.HandleAsync(CreateMessage(), ChannelState.None, new FeatureOptions(), s_pubKey);

        // Assert
        Assert.Equal((ushort)144, _announced!.Value.ToSelfDelay);
        Assert.Equal(3U, _announcedDepth);
    }

    private OpenChannel1MessageHandler CreateHandler()
    {
        var dust = LightningMoney.Satoshis(354);
        var reserve = LightningMoney.Satoshis(1_000);
        var funding = LightningMoney.Satoshis(100_000);
        var channelParams = TestChannelParams.Create(reserve, LightningMoney.Satoshis(253), LightningMoney.Satoshis(1),
                                                     dust, 10, funding, 3, false, dust, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey);
        _channelFactory.Setup(f => f.CreateChannelV1AsNonInitiatorAsync(It.IsAny<OpenChannel1Message>(),
                                                                        It.IsAny<FeatureOptions>(),
                                                                        It.IsAny<CompactPubKey>()))
                       .ReturnsAsync(() => new ChannelModel(channelParams, ChannelId.Zero,
                                                            new CommitmentNumber(s_pubKey, s_pubKey, new FakeSha256()),
                                                            new FundingOutputInfo(funding, s_pubKey, s_pubKey), false,
                                                            null, null, LightningMoney.Zero, keySet, 1, 0, funding,
                                                            keySet, 1, s_pubKey, 0, ChannelState.V1Opening,
                                                            ChannelVersion.V1));
        _messageFactory
           .Setup(x => x.CreateAcceptChannel1Message(It.IsAny<ChannelParty>(), It.IsAny<ChannelTypeTlv>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<uint>(), It.IsAny<CompactPubKey>(),
                                                     It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                     It.IsAny<UpfrontShutdownScriptTlv>()))
           .Callback((ChannelParty local, ChannelTypeTlv _, CompactPubKey _, CompactPubKey _, CompactPubKey _,
                      CompactPubKey _, uint depth, CompactPubKey _, CompactPubKey _, ChannelId _,
                      UpfrontShutdownScriptTlv _) =>
            {
                _announced = local;
                _announcedDepth = depth;
            })
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
                                              openDecisionGate: _gate);
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