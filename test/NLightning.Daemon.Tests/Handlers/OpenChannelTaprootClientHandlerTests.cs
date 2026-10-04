using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Handlers;

using Daemon.Handlers;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.DualFunding.Interfaces;
using Domain.Channels.DualFunding.Models;
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
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using NLightning.Client.Handlers;
using Transport.Ipc.Requests;

/// <summary>
/// <c>openchannel --channel-type taproot</c> (NL-877 T5): the IPC key, the client option, the daemon's refusals
/// (public, not advertised, the peer without option_simple_taproot or option_simple_close), the taproot
/// <c>open_channel</c> with our commitment 0 verification nonce, and the v2 open by the NL-551 rules handing the type
/// to the dual-funded open (<c>DualFundedOpenRequest.SimpleTaproot</c>), a liquidity purchase included (NL-971).
/// </summary>
public class OpenChannelTaprootClientHandlerTests
{
    private static readonly MusigPublicNonce s_nonce = new(Enumerable.Repeat((byte)0x02, 66).ToArray());

    private readonly Mock<IBlockchainMonitor> _blockchainMonitor = new();
    private readonly Mock<IChannelFactory> _channelFactory = new();
    private readonly Mock<IChannelManager> _channelManager = new();
    private readonly Mock<IChannelMemoryRepository> _channelMemory = new();
    private readonly Mock<IMessageFactory> _messageFactory = new();
    private readonly Mock<IPeerManager> _peerManager = new();
    private readonly Mock<IUtxoMemoryRepository> _utxos = new();
    private readonly Mock<ILightningSigner> _signer = new();

    [Fact]
    public async Task Given_APublicTaprootRequest_When_Handled_Then_RefusedBeforeConnecting()
    {
        // Arrange
        var request = Request();
        request.IsPublic = true;

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => CreateHandler(Advertising()).HandleAsync(request,
                                                                           TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        _peerManager.Verify(x => x.GetPeer(It.IsAny<CompactPubKey>()), Times.Never);
    }

    [Theory]
    [InlineData(false, true, true, false, "Features:AllowExperimentalFeatures")]
    [InlineData(true, false, true, false, "does not support simple taproot")]
    [InlineData(true, true, false, false, "option_simple_close")]
    [InlineData(true, true, false, true, "option_simple_close")]
    public async Task Given_ATaprootRequest_When_TheNodeOrPeerCannotRunIt_Then_RefusedBeforeTheFactory(
        bool advertised, bool peerTaproot, bool peerSimpleClose, bool peerDualFund, string reason)
    {
        // Arrange
        var peerId = PeerId();
        SetUpPeer(peerId, new FeatureOptions
        {
            OptionSimpleTaproot = peerTaproot ? FeatureSupport.Optional : FeatureSupport.No,
            OptionSimpleClose = peerSimpleClose ? FeatureSupport.Optional : FeatureSupport.No,
            DualFund = peerDualFund ? FeatureSupport.Optional : FeatureSupport.No,
            AllowExperimentalFeatures = true
        });
        var options = advertised
                          ? Advertising()
                          : new NodeOptions { Features = new FeatureOptions { OptionSimpleTaproot = FeatureSupport.Optional } };

        var dualFunded = new Mock<IDualFundedOpenService>();

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => CreateHandler(options, dualFunded.Object)
                                 .HandleAsync(Request(peerId), TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains(reason, exception.Message);
        dualFunded.Verify(x => x.OpenAsync(It.IsAny<DualFundedOpenRequest>(), It.IsAny<CancellationToken>()),
                          Times.Never);
        _channelFactory.Verify(x => x.CreateChannelV1AsInitiatorAsync(It.IsAny<OpenChannelClientRequest>(),
                                                                      It.IsAny<FeatureOptions>(),
                                                                      It.IsAny<CompactPubKey>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Given_ATaprootOpenToADualFundPeer_When_ItGoesV2_Then_TheDualFundedOpenGetsTheTaprootType(
        bool explicitDualFund, bool requestInbound)
    {
        // Arrange - NL-551: option_dual_fund negotiated and no push opens v2 (or --dual-fund asks for it)
        var peerId = PeerId();
        SetUpPeer(peerId, new FeatureOptions
        {
            OptionSimpleTaproot = FeatureSupport.Optional,
            OptionSimpleClose = FeatureSupport.Optional,
            DualFund = FeatureSupport.Optional,
            AllowExperimentalFeatures = true
        });
        var request = Request(peerId);
        request.IsDualFunded = explicitDualFund;
        request.RequestInboundSat = requestInbound ? 50_000UL : null;
        var channelId = new ChannelId(Enumerable.Repeat((byte)9, 32).ToArray());
        DualFundedOpenRequest? sent = null;
        var dualFunded = new Mock<IDualFundedOpenService>();
        dualFunded.Setup(x => x.OpenAsync(It.IsAny<DualFundedOpenRequest>(), It.IsAny<CancellationToken>()))
                  .Callback<DualFundedOpenRequest, CancellationToken>((r, _) => sent = r)
                  .ReturnsAsync(new DualFundedOpenResult(channelId));

        // Act
        var response = await CreateHandler(Advertising(), dualFunded.Object)
                          .HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert - a private taproot v2 open (buying liquidity when asked, NL-971), nothing through the v1 factory
        Assert.Equal(channelId, response.ChannelId);
        Assert.NotNull(sent);
        Assert.True(sent.SimpleTaproot);
        Assert.False(sent.IsPublic);
        Assert.Equal(requestInbound ? 50_000UL : null, sent.Liquidity?.AmountSat);
        _channelFactory.Verify(x => x.CreateChannelV1AsInitiatorAsync(It.IsAny<OpenChannelClientRequest>(),
                                                                      It.IsAny<FeatureOptions>(),
                                                                      It.IsAny<CompactPubKey>()), Times.Never);
    }

    [Fact]
    public async Task Given_ATaprootV1Open_When_Handled_Then_OpenChannelCarriesOurCommitmentZeroNonce()
    {
        // Arrange
        var peerId = PeerId();
        SetUpPeer(peerId, new FeatureOptions
        {
            OptionSimpleTaproot = FeatureSupport.Optional,
            OptionSimpleClose = FeatureSupport.Optional,
            DualFund = FeatureSupport.Optional,
            AllowExperimentalFeatures = true
        });
        var request = Request(peerId);
        request.ForceV1 = true;
        var party = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(1_000),
                                     LightningMoney.Satoshis(1), 30, request.FundingAmount, 144);
        var channel = new ChannelModel(new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, true,
                                                         FeatureSupport.No)
        { OptionSimpleTaproot = true },
                                       new ChannelId(Enumerable.Repeat((byte)7, 32).ToArray()), null, null, true, null,
                                       null, request.FundingAmount,
                                       new ChannelKeySetModel(5, peerId, peerId, peerId, peerId, peerId, peerId), 0, 0,
                                       LightningMoney.Zero, null, 0, peerId, 0, ChannelState.V1Opening,
                                       ChannelVersion.V1);
        _channelFactory.Setup(x => x.CreateChannelV1AsInitiatorAsync(request, It.IsAny<FeatureOptions>(), peerId))
                       .ReturnsAsync(channel);
        _signer.Setup(s => s.GetLocalVerificationNonce(5u, null, 0UL)).Returns(s_nonce);
        var open = new OpenChannel1Message(
            new OpenChannel1Payload(new ChainHash(new byte[32]), new ChannelFlags(ChannelFlag.None), channel.ChannelId,
                                    LightningMoney.Zero, peerId, LightningMoney.Zero, LightningMoney.Zero, peerId,
                                    request.FundingAmount, peerId, peerId, LightningMoney.Zero, 483,
                                    LightningMoney.Zero, peerId, LightningMoney.Zero, peerId, 144),
            null);
        _messageFactory.Setup(x => x.CreateOpenChannel1Message(It.IsAny<ChannelId>(), It.IsAny<LightningMoney>(),
                                                               It.IsAny<CompactPubKey>(), It.IsAny<LightningMoney>(),
                                                               It.IsAny<ChannelParty>(), It.IsAny<LightningMoney>(),
                                                               It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                               It.IsAny<CompactPubKey>(), It.IsAny<CompactPubKey>(),
                                                               It.IsAny<CompactPubKey>(), It.IsAny<ChannelFlags>(),
                                                               It.IsAny<ChannelTypeTlv>(),
                                                               It.IsAny<UpfrontShutdownScriptTlv>(), s_nonce))
                       .Returns(open);
        var handler = CreateHandler(Advertising(), new Mock<IDualFundedOpenService>().Object);

        // Act
        var task = handler.HandleAsync(request, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _channelMemory.Raise(x => x.OnChannelUpgraded += null, null!,
                             new ChannelUpgradedEventArgs(channel.ChannelId,
                                                          new ChannelId(Enumerable.Repeat((byte)8, 32).ToArray())));
        await task;

        // Assert - the taproot overload (with the nonce) built the open_channel that went out
        _channelManager.Verify(x => x.StartOpeningChannelAsync(peerId, channel, open), Times.Once);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("anchors", false)]
    [InlineData("taproot", true)]
    [InlineData("TAPROOT", true)]
    public void Given_TheIpcChannelType_When_Mapped_Then_TheClientRequestSaysTaproot(string? channelType,
                                                                                    bool expected)
    {
        var ipc = new OpenChannelIpcRequest
        {
            NodeInfo = "node",
            Amount = LightningMoney.Satoshis(100_000),
            ChannelType = channelType
        };

        Assert.Equal(expected, ipc.ToClientRequest().IsSimpleTaproot);
    }

    [Fact]
    public void Given_AnUnknownIpcChannelType_When_Mapped_Then_Refused()
    {
        var ipc = new OpenChannelIpcRequest
        {
            NodeInfo = "node",
            Amount = LightningMoney.Satoshis(100_000),
            ChannelType = "legacy"
        };

        var exception = Assert.Throws<ClientException>(() => ipc.ToClientRequest());
        Assert.Contains("Unknown channel type", exception.Message);
    }

    [Theory]
    [InlineData(new[] { "node", "100000", "--channel-type", "taproot", "--v1" }, "taproot", null)]
    [InlineData(new[] { "node", "100000", "--channel-type", "anchors" }, "anchors", null)]
    [InlineData(new[] { "node", "100000", "--channel-type", "taproot", "--dual-fund" }, "taproot", null)]
    [InlineData(new[] { "node", "100000", "--channel-type", "taproot", "--public" }, "taproot", "private channel")]
    [InlineData(new[] { "node", "100000", "--channel-type", "taproot", "--request-inbound", "50000" }, "taproot",
                null)]
    [InlineData(new[] { "node", "100000", "--channel-type" }, null, "expects taproot or anchors")]
    [InlineData(new[] { "node", "100000", "--channel-type", "legacy" }, null, "expects taproot or anchors")]
    public void Given_ChannelTypeOption_When_Parsed_Then_TypeOrError(string[] args, string? type, string? error)
    {
        var positional = OpenChannelMessageHandler.ParseArguments(args, out _, out _, out _, out _, out _,
                                                                  out var channelType, out var parseError);

        Assert.Equal(type, channelType);
        if (error is null)
        {
            Assert.Null(parseError);
            Assert.Equal(["node", "100000"], positional);
        }
        else
        {
            Assert.Contains(error, parseError);
        }
    }

    private OpenChannelClientHandler CreateHandler(NodeOptions nodeOptions,
                                                   IDualFundedOpenService? dualFundedOpenService = null) =>
        new(_blockchainMonitor.Object, _channelFactory.Object, _channelManager.Object, _channelMemory.Object,
            new Mock<ILogger<OpenChannelClientHandler>>().Object, _messageFactory.Object, _peerManager.Object,
            _utxos.Object, nodeOptions: Options.Create(nodeOptions), dualFundedOpenService: dualFundedOpenService,
            lightningSigner: _signer.Object);

    private void SetUpPeer(CompactPubKey peerId, FeatureOptions negotiated)
    {
        var peer = new PeerModel(peerId, "127.0.0.1", 9735, "ipv4");
        var peerService = new Mock<IPeerService>();
        peerService.Setup(x => x.Features).Returns(negotiated);
        peer.SetPeerService(peerService.Object);
        _peerManager.Setup(x => x.GetPeer(peerId)).Returns(peer);
        _blockchainMonitor.Setup(x => x.LastProcessedBlockHeight).Returns(100u);
        _utxos.Setup(x => x.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(2_000_000));
    }

    private static NodeOptions Advertising() => new()
    {
        Features = new FeatureOptions
        {
            OptionSimpleTaproot = FeatureSupport.Optional,
            AllowExperimentalFeatures = true
        }
    };

    private static CompactPubKey PeerId()
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = 0x01;
        return new CompactPubKey(bytes);
    }

    private static OpenChannelClientRequest Request(CompactPubKey? peerId = null) =>
        new(peerId is { } id ? id.ToString() : $"{PeerId()}@127.0.0.1:9735", LightningMoney.Satoshis(1_000_000))
        {
            IsSimpleTaproot = true
        };
}