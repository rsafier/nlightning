using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Handlers;

using Daemon.Handlers;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.DualFunding.Interfaces;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// <c>openchannel --dual-fund</c> (wave sp1 lane SP1-F, NL-037): the client handler hands the open to
/// <see cref="IDualFundedOpenService"/> and never builds a v1 channel.
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

    private OpenChannelClientHandler CreateHandler() =>
        new(_monitor.Object, _channelFactory.Object, _channelManager.Object,
            new Mock<IChannelMemoryRepository>().Object, new Mock<ILogger<OpenChannelClientHandler>>().Object,
            new Mock<IMessageFactory>().Object, _peerManager.Object, _utxos.Object,
            nodeOptions: Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
            dualFundedOpenService: _dualFund.Object);
}