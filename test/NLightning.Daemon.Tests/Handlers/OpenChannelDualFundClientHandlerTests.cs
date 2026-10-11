using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Handlers;

using Daemon.Handlers;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.DualFunding.Interfaces;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
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
using Domain.Node.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Transport.Ipc.Responses;

/// <summary>
/// <c>openchannel --dual-fund</c> (wave sp1 lane SP1-F, NL-037): the client handler hands the open to
/// <see cref="IDualFundedOpenService"/> and never builds a v1 channel. A plain <c>openchannel</c> does the same when
/// <c>option_dual_fund</c> is negotiated, unless it needs v1 (NL-551).
/// </summary>
public class OpenChannelDualFundClientHandlerTests
{
    private static readonly CompactPubKey s_peer =
        Convert.FromHexString("02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27");

    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IChannelFactory> _channelFactory = new();
    private readonly Mock<IChannelManager> _channelManager = new();
    private readonly Mock<IPeerManager> _peerManager = new();
    private readonly Mock<IUtxoMemoryRepository> _utxos = new();
    private readonly Mock<IDualFundedOpenService> _dualFund = new();

    public OpenChannelDualFundClientHandlerTests()
    {
        _peerManager.Setup(m => m.GetPeer(s_peer)).Returns(new PeerModel(s_peer, "127.0.0.1", 9735, "ipv4"));
        _monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(100u);
        _utxos.Setup(u => u.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(1_000_000));
    }

    [Fact]
    public async Task Given_ADualFundRequest_When_Handled_Then_TheDualFundedOpenServiceOpensIt()
    {
        // Arrange
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x5C, 32).ToArray());
        var txId = new TxId(Enumerable.Repeat((byte)0x5D, 32).ToArray());
        _dualFund.Setup(s => s.OpenAsync(It.Is<DualFundedOpenRequest>(r => r.PeerNodeId == s_peer
                                                                         && r.LocalFundingAmount
                                                                         == LightningMoney.Satoshis(400_000)
                                                                         && r.FundingFeeratePerKw == 2_500
                                                                         && r.IsPublic),
                                         It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new DualFundedOpenResult(channelId, txId));
        var request = new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(400_000))
        {
            IsDualFunded = true,
            IsPublic = true,
            FeeRatePerKw = LightningMoney.Satoshis(2_500)
        };

        // Act
        var response = await CreateHandler().HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: the v2 channel id, and no v1 channel was built
        Assert.Equal(channelId, response.ChannelId);
        _channelFactory.VerifyNoOtherCalls();
        _channelManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ADualFundedOpen_When_Handled_Then_TheResponseCarriesThePublishedFunding()
    {
        // Arrange (NL-535): the open returns once both tx_signatures went out and the funding was published, long
        // before it confirms; the client prints the txid at once so the operator can bumpopen it
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x6C, 32).ToArray());
        var txId = new TxId(Enumerable.Repeat((byte)0x6D, 32).ToArray());
        _dualFund.Setup(s => s.OpenAsync(It.IsAny<DualFundedOpenRequest>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new DualFundedOpenResult(channelId, txId));
        var channel = new ChannelModel(new ChannelParams(), channelId, null,
                                       new FundingOutputInfo(LightningMoney.Satoshis(800_000), s_peer, s_peer, txId, 1),
                                       true, null, null, LightningMoney.Satoshis(400_000),
                                       new ChannelKeySetModel(0, s_peer, s_peer, s_peer, s_peer, s_peer, s_peer), 0, 0,
                                       LightningMoney.Satoshis(400_000), null, 0, s_peer, 0,
                                       ChannelState.V1FundingSigned, ChannelVersion.V2);
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        var handler = new OpenChannelClientHandler(_monitor.Object, _channelFactory.Object, _channelManager.Object,
                                                   memory.Object, new Mock<ILogger<OpenChannelClientHandler>>().Object,
                                                   new Mock<IMessageFactory>().Object, _peerManager.Object,
                                                   _utxos.Object,
                                                   nodeOptions: Options.Create(new NodeOptions
                                                   {
                                                       BitcoinNetwork = BitcoinNetwork.Regtest
                                                   }),
                                                   dualFundedOpenService: _dualFund.Object);
        var request = new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(400_000))
        {
            IsDualFunded = true
        };

        // Act
        var response = await handler.HandleAsync(request, TestContext.Current.CancellationToken);
        var ipc = OpenChannelIpcResponse.FromClientResponse(response);

        // Assert
        Assert.Equal(channelId, response.ChannelId);
        Assert.Equal(txId, response.FundingTxId);
        Assert.Equal(1u, response.FundingOutputIndex);
        Assert.Equal(txId, ipc.FundingTxId);
        Assert.Equal(1u, ipc.FundingOutputIndex);
    }

    [Fact]
    public async Task Given_AFailedDualFundedOpen_When_Handled_Then_InvalidOperation()
    {
        // Arrange
        _dualFund.Setup(s => s.OpenAsync(It.IsAny<DualFundedOpenRequest>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new DualFundedOpenResult(ChannelId.Zero, null, "peer error: no"));
        var request = new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(400_000))
        {
            IsDualFunded = true
        };

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => CreateHandler().HandleAsync(request, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        Assert.Contains("peer error: no", exception.Message);
    }

    [Fact]
    public async Task Given_APushAmount_When_ADualFundRequestIsHandled_Then_Refused()
    {
        // Arrange
        var request = new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(400_000))
        {
            IsDualFunded = true,
            PushAmount = LightningMoney.Satoshis(1_000)
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => CreateHandler().HandleAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        _dualFund.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_NoDualFundedOpenService_When_ADualFundRequestIsHandled_Then_Refused()
    {
        // Arrange
        var handler = new OpenChannelClientHandler(_monitor.Object, _channelFactory.Object, _channelManager.Object,
                                                   new Mock<IChannelMemoryRepository>().Object,
                                                   new Mock<ILogger<OpenChannelClientHandler>>().Object,
                                                   new Mock<IMessageFactory>().Object, _peerManager.Object,
                                                   _utxos.Object);
        var request = new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(400_000))
        {
            IsDualFunded = true
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => handler.HandleAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
    }

    [Fact]
    public async Task Given_APeerWithDualFund_When_APlainOpenIsHandled_Then_ItOpensDualFundedAndReturnsTheFunding()
    {
        // Arrange (NL-551): no --dual-fund, but option_dual_fund is negotiated with the peer
        SetPeerFeatures(new FeatureOptions { DualFund = FeatureSupport.Optional });
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x7C, 32).ToArray());
        var txId = new TxId(Enumerable.Repeat((byte)0x7D, 32).ToArray());
        _dualFund.Setup(s => s.OpenAsync(It.Is<DualFundedOpenRequest>(r => r.PeerNodeId == s_peer
                                                                         && r.LocalFundingAmount
                                                                         == LightningMoney.Satoshis(400_000)
                                                                         && r.IsPublic),
                                         It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new DualFundedOpenResult(channelId, txId));
        var request = new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(400_000))
        {
            IsPublic = true
        };

        // Act
        var response = await CreateHandler().HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: the v2 channel and its published funding (NL-535: the client prints it at once), no v1 channel
        Assert.Equal(channelId, response.ChannelId);
        Assert.Equal(txId, response.FundingTxId);
        _channelFactory.VerifyNoOtherCalls();
        _channelManager.VerifyNoOtherCalls();
    }

    public static TheoryData<string, FeatureSupport, long, bool> V1Cases => new()
    {
        { "the peer lacks option_dual_fund", FeatureSupport.No, 0, false },
        { "a push amount (v2 has none)", FeatureSupport.Optional, 1_000, false },
        { "--v1", FeatureSupport.Optional, 0, true }
    };

    [Theory]
    [MemberData(nameof(V1Cases))]
    public async Task Given_ARequestThatNeedsV1_When_APlainOpenIsHandled_Then_ItOpensV1(string reason,
        FeatureSupport peerDualFund, long pushSat, bool forceV1)
    {
        // Arrange (NL-551): the v1 factory is reached, then stopped by a sentinel
        SetPeerFeatures(new FeatureOptions { DualFund = peerDualFund });
        _channelFactory.Setup(f => f.CreateChannelV1AsInitiatorAsync(It.IsAny<OpenChannelClientRequest>(),
                                                                     It.IsAny<FeatureOptions>(), s_peer))
                       .ThrowsAsync(new InvalidOperationException("v1 sentinel"));
        var request = new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(400_000))
        {
            PushAmount = pushSat == 0 ? null : LightningMoney.Satoshis(pushSat),
            ForceV1 = forceV1
        };

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => CreateHandler().HandleAsync(request, TestContext.Current.CancellationToken));

        // Assert
        Assert.True(exception.Message == "v1 sentinel", reason);
        _channelFactory.Verify(f => f.CreateChannelV1AsInitiatorAsync(request, It.IsAny<FeatureOptions>(), s_peer),
                               Times.Once);
        _dualFund.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_DualFundAndForceV1_When_Handled_Then_Refused()
    {
        // Arrange
        var request = new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(400_000))
        {
            IsDualFunded = true,
            ForceV1 = true
        };

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => CreateHandler().HandleAsync(request, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        _dualFund.VerifyNoOtherCalls();
        _channelFactory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ALabelAndTags_When_ADualFundRequestIsHandled_Then_TheOpenRequestCarriesThem()
    {
        // Arrange (NL-602 A3-T1): the channel built at accept_channel2 takes them from the open request
        DualFundedOpenRequest? open = null;
        _dualFund.Setup(s => s.OpenAsync(It.IsAny<DualFundedOpenRequest>(), It.IsAny<CancellationToken>()))
                 .Callback<DualFundedOpenRequest, CancellationToken>((r, _) => open = r)
                 .ReturnsAsync(new DualFundedOpenResult(new ChannelId(new byte[32]), new TxId(new byte[32])));
        var request = new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(400_000))
        {
            IsDualFunded = true,
            Label = "liquidity",
            Tags = ["peer=acme", "purpose=routing"]
        };

        // Act
        await CreateHandler().HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(open);
        Assert.Equal("liquidity", open.Labels.Label);
        Assert.Equal("peer=acme\npurpose=routing", open.Labels.CanonicalTags);
    }

    [Fact]
    public async Task Given_ABrokenLabel_When_AnOpenIsHandled_Then_RefusedBeforeAnything()
    {
        // Arrange (NL-602 A3-T1): a control character in the label
        var request = new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(400_000))
        {
            IsDualFunded = true,
            Label = "line\nbreak"
        };

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => CreateHandler().HandleAsync(request, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        Assert.Contains("control characters", exception.Message);
        _dualFund.VerifyNoOtherCalls();
        _channelFactory.VerifyNoOtherCalls();
        _peerManager.Verify(m => m.ConnectToPeerAsync(It.IsAny<PeerAddressInfo>()), Times.Never);
    }

    private void SetPeerFeatures(FeatureOptions features)
    {
        var peer = new PeerModel(s_peer, "127.0.0.1", 9735, "ipv4");
        var peerService = new Mock<IPeerService>();
        peerService.Setup(p => p.Features).Returns(features);
        peer.SetPeerService(peerService.Object);
        _peerManager.Setup(m => m.GetPeer(s_peer)).Returns(peer);
    }

    private OpenChannelClientHandler CreateHandler() =>
        new(_monitor.Object, _channelFactory.Object, _channelManager.Object,
            new Mock<IChannelMemoryRepository>().Object, new Mock<ILogger<OpenChannelClientHandler>>().Object,
            new Mock<IMessageFactory>().Object, _peerManager.Object, _utxos.Object,
            nodeOptions: Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
            dualFundedOpenService: _dualFund.Object);
}