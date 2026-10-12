using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Handlers;

using Application.Gossip.Graph.Interfaces;
using Application.LiquidityAds;
using Daemon.Extensions;
using Daemon.Handlers;
using Daemon.Interfaces;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.DualFunding.Interfaces;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.LiquidityAds;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Interfaces;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Ipc.Handlers;
using Transport.Ipc.Responses;

/// <summary>
/// Liquidity ads (NL-850) on the daemon's client handlers: <c>--request-inbound</c> on <c>openchannel</c> (a v2 open),
/// <c>splicein</c> and <c>bumpopen</c>, <c>closechannel --force</c>, and <c>liquidityads rates|sellers|purchases</c>.
/// </summary>
public class LiquidityAdsClientHandlersTests
{
    private static readonly CompactPubKey s_peer =
        Convert.FromHexString("02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27");

    private static readonly CompactPubKey s_other =
        Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c");

    private static readonly CompactPubKey s_us =
        Convert.FromHexString("03a5e1b8c8b6d5f1f0a9d3c2b8e1f4a7c6d9e2b5f8a1c4d7e0b3f6a9c2d5e8b1f4");

    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x5C, 32).ToArray());

    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IPeerManager> _peerManager = new();
    private readonly Mock<IUtxoMemoryRepository> _utxos = new();
    private readonly Mock<IDualFundedOpenService> _dualFund = new();

    public LiquidityAdsClientHandlersTests()
    {
        _peerManager.Setup(m => m.GetPeer(s_peer)).Returns(new PeerModel(s_peer, "127.0.0.1", 9735, "ipv4"));
        _monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(100u);
        _utxos.Setup(u => u.GetConfirmedBalance(100u)).Returns(LightningMoney.Satoshis(1_000_000));
    }

    #region openchannel --request-inbound

    [Fact]
    public async Task Given_RequestInbound_When_APlainOpenIsHandled_Then_ADualFundedOpenBuysTheLiquidity()
    {
        // Arrange: no --dual-fund, the purchase implies v2
        DualFundedOpenRequest? seen = null;
        var purchase = LiquidityAdsTestData.Purchase(s_channelId);
        _dualFund.Setup(s => s.OpenAsync(It.IsAny<DualFundedOpenRequest>(), It.IsAny<CancellationToken>()))
                 .Callback((DualFundedOpenRequest r, CancellationToken _) => seen = r)
                 .ReturnsAsync(new DualFundedOpenResult(s_channelId, LiquidityAdsTestData.FundingTxId)
                 {
                     Purchase = purchase
                 });
        var request = new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(500_000))
        {
            RequestInboundSat = 400_000,
            MaxLiquidityFeeSat = 10_000
        };

        // Act
        var response = await CreateOpenHandler().HandleAsync(request, TestContext.Current.CancellationToken);
        var ipc = OpenChannelIpcResponse.FromClientResponse(response);

        // Assert
        Assert.NotNull(seen);
        Assert.Equal(new LiquidityRequest(400_000, null, 10_000), seen.Liquidity);
        Assert.Same(purchase, response.Purchase);
        Assert.NotNull(ipc.Purchase);
        Assert.Equal(400_000UL, ipc.Purchase.RequestedSat);
        Assert.Equal(410_000UL, ipc.Purchase.ContributedSat);
        Assert.Equal(6_260UL, ipc.Purchase.TotalFeeSat);
        Assert.Equal(LiquidityPurchaseRole.Buyer, ipc.Purchase.Role);
    }

    [Fact]
    public async Task Given_NoRequestInbound_When_ADualFundedOpenIsHandled_Then_NothingIsBought()
    {
        // Arrange
        DualFundedOpenRequest? seen = null;
        _dualFund.Setup(s => s.OpenAsync(It.IsAny<DualFundedOpenRequest>(), It.IsAny<CancellationToken>()))
                 .Callback((DualFundedOpenRequest r, CancellationToken _) => seen = r)
                 .ReturnsAsync(new DualFundedOpenResult(s_channelId, LiquidityAdsTestData.FundingTxId));

        // Act
        var response = await CreateOpenHandler().HandleAsync(
                           new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(500_000))
                           {
                               IsDualFunded = true
                           }, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(seen);
        Assert.Null(seen.Liquidity);
        Assert.Null(OpenChannelIpcResponse.FromClientResponse(response).Purchase);
    }

    [Theory]
    [InlineData(400_000UL, null, true, 0UL, "--v1")]
    [InlineData(400_000UL, null, false, 10_000UL, "no push amount")]
    [InlineData(0UL, null, false, 0UL, "above 0")]
    [InlineData(null, 5_000UL, false, 0UL, "--max-liquidity-fee needs --request-inbound")]
    public async Task Given_ALiquidityRequestThatCannotBeBought_When_Handled_Then_RefusedBeforeAnything(
        ulong? inbound, ulong? maxFee, bool forceV1, ulong pushSat, string expected)
    {
        // Arrange
        var request = new OpenChannelClientRequest(s_peer.ToString(), LightningMoney.Satoshis(500_000))
        {
            RequestInboundSat = inbound,
            MaxLiquidityFeeSat = maxFee,
            ForceV1 = forceV1,
            PushAmount = pushSat == 0 ? null : LightningMoney.Satoshis(pushSat)
        };

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => CreateOpenHandler().HandleAsync(request, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        Assert.Contains(expected, exception.Message);
        _dualFund.VerifyNoOtherCalls();
        _peerManager.Verify(m => m.GetPeer(It.IsAny<CompactPubKey>()), Times.Never);
    }

    #endregion

    #region splicein and bumpopen

    [Fact]
    public async Task Given_RequestInbound_When_ASpliceInIsHandled_Then_TheSpliceBuysTheLiquidity()
    {
        // Arrange
        var splice = new Mock<ISpliceService>();
        SpliceRequest? seen = null;
        var purchase = LiquidityAdsTestData.Purchase(s_channelId, kind: LiquidityPurchaseKind.Splice);
        splice.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
              .Callback((SpliceRequest r, CancellationToken _) => seen = r)
              .ReturnsAsync(new SpliceResult(s_channelId, SpliceNegotiationState.Signed,
                                             LiquidityAdsTestData.FundingTxId, 1_910_000)
              {
                  Purchase = purchase
              });
        var handler = new SpliceInClientHandler(splice.Object, NullLogger<SpliceInClientHandler>.Instance);

        // Act
        var response = await handler.HandleAsync(new SpliceInClientRequest(s_channelId, 100_000)
        {
            RequestInboundSat = 400_000,
            MaxLiquidityFeeSat = 8_000
        }, TestContext.Current.CancellationToken);
        var ipc = SpliceIpcResponse.FromClientResponse(response);

        // Assert
        Assert.NotNull(seen);
        Assert.Equal(100_000L, seen.ContributionSatoshis);
        Assert.Equal(new LiquidityRequest(400_000, null, 8_000), seen.Liquidity);
        Assert.NotNull(ipc.Purchase);
        Assert.Equal(LiquidityPurchaseKind.Splice, ipc.Purchase.Kind);
    }

    [Theory]
    [InlineData(0UL, null)]
    [InlineData(null, 1UL)]
    public async Task Given_ABadLiquidityRequest_When_ASpliceInIsHandled_Then_RefusedBeforeTheService(
        ulong? inbound, ulong? maxFee)
    {
        // Arrange
        var splice = new Mock<ISpliceService>();
        var handler = new SpliceInClientHandler(splice.Object, NullLogger<SpliceInClientHandler>.Instance);

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => handler.HandleAsync(new SpliceInClientRequest(s_channelId, 100_000)
                            {
                                RequestInboundSat = inbound,
                                MaxLiquidityFeeSat = maxFee
                            }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        splice.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_RequestInbound_When_ABumpOpenIsHandled_Then_TheNewAttemptBuysTheLiquidity()
    {
        // Arrange
        LiquidityRequest? seen = null;
        var purchase = LiquidityAdsTestData.Purchase(s_channelId, kind: LiquidityPurchaseKind.OpenRbf);
        _dualFund.Setup(s => s.BumpAsync(s_channelId, 3_000, null, It.IsAny<LiquidityRequest?>(),
                                         It.IsAny<CancellationToken>()))
                 .Callback((ChannelId _, uint _, LightningMoney? _, LiquidityRequest? l, CancellationToken _) =>
                               seen = l)
                 .ReturnsAsync(new DualFundedOpenResult(s_channelId, LiquidityAdsTestData.FundingTxId)
                 {
                     Purchase = purchase
                 });
        var handler = new BumpOpenClientHandler(NullLogger<BumpOpenClientHandler>.Instance, _dualFund.Object);

        // Act
        var response = await handler.HandleAsync(new BumpOpenClientRequest(s_channelId, 3_000)
        {
            RequestInboundSat = 450_000
        }, TestContext.Current.CancellationToken);
        var ipc = BumpOpenIpcResponse.FromClientResponse(response);

        // Assert
        Assert.Equal(new LiquidityRequest(450_000), seen);
        Assert.NotNull(ipc.Purchase);
        Assert.Equal(LiquidityPurchaseKind.OpenRbf, ipc.Purchase.Kind);
    }

    [Fact]
    public async Task Given_NoRequestInbound_When_ABumpOpenIsHandled_Then_NullLetsTheServiceRepeatThePurchase()
    {
        // Arrange
        var called = false;
        _dualFund.Setup(s => s.BumpAsync(s_channelId, 3_000, null, null, It.IsAny<CancellationToken>()))
                 .Callback(() => called = true)
                 .ReturnsAsync(new DualFundedOpenResult(s_channelId, LiquidityAdsTestData.FundingTxId));
        var handler = new BumpOpenClientHandler(NullLogger<BumpOpenClientHandler>.Instance, _dualFund.Object);

        // Act
        var response = await handler.HandleAsync(new BumpOpenClientRequest(s_channelId, 3_000),
                                                  TestContext.Current.CancellationToken);

        // Assert
        Assert.True(called);
        Assert.Null(response.Purchase);
    }

    [Fact]
    public async Task Given_AMaxFeeWithoutRequestInbound_When_ABumpOpenIsHandled_Then_Refused()
    {
        // Arrange
        var handler = new BumpOpenClientHandler(NullLogger<BumpOpenClientHandler>.Instance, _dualFund.Object);

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => handler.HandleAsync(new BumpOpenClientRequest(s_channelId, 3_000)
                            {
                                MaxLiquidityFeeSat = 5_000
                            }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        _dualFund.VerifyNoOtherCalls();
    }

    #endregion

    #region closechannel --force

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_ForceOrNot_When_ACloseIsHandled_Then_TheServiceGetsIt(bool force)
    {
        // Arrange
        var service = new Mock<IChannelCloseService>();
        ChannelCloseRequest? seen = null;
        service.Setup(s => s.CloseChannelAsync(s_channelId, It.IsAny<ChannelCloseRequest>(),
                                               It.IsAny<CancellationToken>()))
               .Callback((ChannelId _, ChannelCloseRequest r, CancellationToken _) => seen = r)
               .ReturnsAsync(new ChannelCloseResult(s_channelId, ChannelState.ShuttingDown,
                                                    null));

        // Act
        await new CloseChannelClientHandler(service.Object).HandleAsync(new CloseChannelClientRequest(s_channelId)
        {
            Force = force
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(seen);
        Assert.Equal(force, seen.Force);
    }

    [Fact]
    public async Task Given_ALeaseRefusal_When_ACloseIsHandled_Then_InvalidOperationWithTheReason()
    {
        // Arrange
        var service = new Mock<IChannelCloseService>();
        service.Setup(s => s.CloseChannelAsync(s_channelId, It.IsAny<ChannelCloseRequest>(),
                                               It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("closing it now breaks the lease"));

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => new CloseChannelClientHandler(service.Object).HandleAsync(
                                new CloseChannelClientRequest(s_channelId), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        Assert.Contains("lease", exception.Message);
    }

    #endregion

    #region liquidityads

    [Fact]
    public async Task Given_ConfiguredRates_When_RatesAreAsked_Then_OursComeBack()
    {
        // Arrange
        var options = new NodeOptions();
        options.LiquidityAds.FundingRates.Add(new FundingRateOptions
        {
            MinAmountSat = 100_000,
            MaxAmountSat = 1_000_000,
            FundingWeight = 500,
            FeeBasis = 100,
            FeeBaseSat = 10,
            ChannelCreationFeeSat = 1_000
        });
        var handler = new LiquidityAdsClientHandler(CreateService(options));

        // Act
        var response = await handler.HandleAsync(new LiquidityAdsClientRequest(LiquidityAdsAction.Rates),
                                                 TestContext.Current.CancellationToken);
        var ipc = LiquidityAdsIpcResponse.FromClientResponse(response);

        // Assert
        Assert.NotNull(response.OurRates);
        Assert.Equal(LiquidityAdsTestData.Rate, Assert.Single(response.OurRates.Rates));
        Assert.Equal(4_032U, response.LeaseBlocks);
        Assert.NotNull(ipc.OurRates);
        Assert.Equal(LiquidityAdsTestData.Rate, Assert.Single(ipc.OurRates).ToFundingRate());
        Assert.Equal(["from_channel_balance"], ipc.OurPaymentTypes);
    }

    [Fact]
    public async Task Given_NoRates_When_RatesAreAsked_Then_NotSelling()
    {
        // Act
        var response = await new LiquidityAdsClientHandler(CreateService(new NodeOptions())).HandleAsync(
                           new LiquidityAdsClientRequest(LiquidityAdsAction.Rates),
                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(response.OurRates);
        Assert.Null(LiquidityAdsIpcResponse.FromClientResponse(response).OurRates);
    }

    [Fact]
    public async Task Given_NoLiquidityAdsService_When_Asked_Then_NotAvailable()
    {
        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => new LiquidityAdsClientHandler().HandleAsync(
                                new LiquidityAdsClientRequest(LiquidityAdsAction.Rates),
                                TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        Assert.Contains("not available", exception.Message);
    }

    [Fact]
    public async Task Given_SellersInInitAndInTheGraph_When_SellersAreAsked_Then_OnePerNodeWithInitPreferred()
    {
        // Arrange: s_peer connected with rates in its init and in its announcement (different rates), s_other only
        // announced, our own node announced, and a connected peer without rates
        var initRates = WillFundRates.Create([LiquidityAdsTestData.Rate], [LiquidityPaymentType.FromChannelBalance]);
        var announcedRate = new FundingRate(1, 2, 3, 4, 5, 6);
        var announcedRates = WillFundRates.Create([announcedRate], [LiquidityPaymentType.FromChannelBalance,
                                                                    LiquidityPaymentType.FromFutureHtlc]);
        var peer = new PeerModel(s_peer, "127.0.0.1", 9735, "ipv4");
        var peerService = new Mock<IPeerService>();
        peerService.SetupGet(s => s.LiquidityRates).Returns(initRates);
        peer.SetPeerService(peerService.Object);
        var silent = new PeerModel(CreateKey(0x11), "127.0.0.1", 9736, "ipv4");
        silent.SetPeerService(new Mock<IPeerService>().Object);
        var peerManager = new Mock<IPeerManager>();
        peerManager.Setup(m => m.ListPeers()).Returns([peer, silent]);
        peerManager.Setup(m => m.GetPeer(s_peer)).Returns(peer);
        var nodes = new[]
        {
            CreateNode(s_peer, "peer", announcedRates), CreateNode(s_other, "other", announcedRates),
            CreateNode(s_us, "us", announcedRates), CreateNode(CreateKey(0x22), "plain", null)
        };
        var view = new Mock<IGraphView>();
        view.SetupGet(v => v.Nodes).Returns(nodes);
        view.Setup(v => v.TryGetNode(It.IsAny<CompactPubKey>(), out It.Ref<GraphNode?>.IsAny))
            .Returns((CompactPubKey id, out GraphNode? node) =>
            {
                node = nodes.FirstOrDefault(n => n.NodeId == id);
                return node is not null;
            });
        var graph = new Mock<IGraphStore>();
        graph.Setup(g => g.GetSnapshot()).Returns(view.Object);
        var keys = new Mock<ISecureKeyManager>();
        keys.Setup(k => k.GetNodePubKey()).Returns(s_us);
        var handler = new LiquidityAdsClientHandler(CreateService(new NodeOptions()), peerManager.Object, graph.Object,
                                                    secureKeyManager: keys.Object);

        // Act
        var response = await handler.HandleAsync(new LiquidityAdsClientRequest(LiquidityAdsAction.Sellers),
                                                 TestContext.Current.CancellationToken);
        var ipc = LiquidityAdsIpcResponse.FromClientResponse(response);

        // Assert
        Assert.Equal(2, response.Sellers.Count);
        var first = response.Sellers[0];
        Assert.Equal(s_peer, first.NodeId);
        Assert.Equal(LiquiditySellerSource.Init, first.Source);
        Assert.True(first.IsConnected);
        Assert.Equal("peer", first.Alias);
        Assert.Equal(initRates, first.Rates);
        // NL-884: the connected seller's announcement rates come along with its init's
        Assert.Equal(announcedRates, first.AnnouncedRates);
        Assert.Equal(announcedRate, Assert.Single(ipc.Sellers[0].AnnouncedRates!).ToFundingRate());
        Assert.Equal(["from_channel_balance", "from_future_htlc"], ipc.Sellers[0].AnnouncedPaymentTypes);
        var second = response.Sellers[1];
        Assert.Equal(s_other, second.NodeId);
        Assert.Equal(LiquiditySellerSource.NodeAnnouncement, second.Source);
        Assert.False(second.IsConnected);
        Assert.Equal(announcedRate, Assert.Single(second.Rates.Rates));
        Assert.Equal(["from_channel_balance", "from_future_htlc"], ipc.Sellers[1].PaymentTypes);
        Assert.Null(second.AnnouncedRates);
        Assert.Null(ipc.Sellers[1].AnnouncedRates);
    }

    [Fact]
    public async Task Given_Purchases_When_PurchasesAreAsked_Then_ThePageAndTheHeightComeBack()
    {
        // Arrange
        var repository = new Mock<ILiquidityPurchaseDbRepository>();
        var rows = new List<LiquidityPurchaseModel>
        {
            LiquidityAdsTestData.Restored(s_channelId, LiquidityPurchaseStatus.Active)
        };
        repository.Setup(r => r.ListAsync(LiquidityPurchaseRole.Seller, LiquidityPurchaseStatus.Active, 5, 10))
                  .ReturnsAsync(rows);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.LiquidityPurchaseDbRepository).Returns(repository.Object);
        var handler = new LiquidityAdsClientHandler(CreateService(new NodeOptions()), unitOfWork: unitOfWork.Object,
                                                    blockchainMonitor: _monitor.Object);

        // Act
        var response = await handler.HandleAsync(new LiquidityAdsClientRequest(LiquidityAdsAction.Purchases)
        {
            Role = LiquidityPurchaseRole.Seller,
            Status = LiquidityPurchaseStatus.Active,
            Skip = 5,
            Take = 10
        }, TestContext.Current.CancellationToken);
        var ipc = LiquidityAdsIpcResponse.FromClientResponse(response);

        // Assert
        Assert.Equal(100U, response.CurrentHeight);
        var purchase = Assert.Single(ipc.Purchases);
        Assert.Equal(100U, purchase.LeaseStartHeight);
        Assert.Equal(4_132U, purchase.LeaseEndHeight);
        Assert.Equal("1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100", purchase.FundingTxId);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 1_001)]
    public async Task Given_ABadPage_When_PurchasesAreAsked_Then_InvalidOperation(int skip, int take)
    {
        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => new LiquidityAdsClientHandler(CreateService(new NodeOptions())).HandleAsync(
                                new LiquidityAdsClientRequest(LiquidityAdsAction.Purchases)
                                {
                                    Skip = skip,
                                    Take = take
                                }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
    }

    [Fact]
    public async Task Given_AUnitOfWorkWithoutPurchases_When_PurchasesAreAsked_Then_AnEmptyPage()
    {
        // Arrange: the interface default throws NotSupportedException
        var unitOfWork = new Mock<IUnitOfWork> { CallBase = true };

        // Act
        var response = await new LiquidityAdsClientHandler(CreateService(new NodeOptions()),
                                                           unitOfWork: unitOfWork.Object)
                          .HandleAsync(new LiquidityAdsClientRequest(LiquidityAdsAction.Purchases),
                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(response.Purchases);
    }

    [Fact]
    public void Given_TheIpcServices_When_Composed_Then_TheHandlerAndItsIpcHandlerResolve()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(CreateService(new NodeOptions()));
        services.AddLiquidityAdsIpcServices();
        services.AddLiquidityAdsIpcServices();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Act
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<LiquidityAdsClientRequest,
                                LiquidityAdsClientResponse>>();
        var ipcHandlers = provider.GetServices<IIpcCommandHandler>()
                                  .Where(h => h.Command == ClientCommand.LiquidityAds)
                                  .ToList();

        // Assert
        Assert.Equal(ClientCommand.LiquidityAds, handler.Command);
        Assert.IsType<Daemon.Ipc.Handlers.LiquidityAdsIpcHandler>(Assert.Single(ipcHandlers));
    }

    #endregion

    private static LiquidityAdsService CreateService(NodeOptions options) =>
        new(new Mock<ILightningSigner>().Object, new Mock<IServiceProvider>().Object,
            NullLogger<LiquidityAdsService>.Instance, Options.Create(options));

    private static CompactPubKey CreateKey(byte fill) =>
        new([0x02, .. Enumerable.Repeat(fill, 32)]);

    private static GraphNode CreateNode(CompactPubKey nodeId, string alias, WillFundRates? rates)
    {
        var aliasBytes = NodeAnnouncementPayload.EncodeAlias(alias);
        var payload = new NodeAnnouncementPayload(NodeAnnouncementPayload.EmptySignature, Array.Empty<byte>(), 1,
                                                  nodeId, new byte[] { 1, 2, 3 }, aliasBytes, Array.Empty<byte>(),
                                                  NodeAnnouncementRates.EncodeExtraData(rates));
        return new GraphNode(nodeId, 1, Array.Empty<byte>(), aliasBytes, new byte[] { 1, 2, 3 })
        {
            RawAnnouncement = payload.GetBytes()
        };
    }

    private OpenChannelClientHandler CreateOpenHandler() =>
        new(_monitor.Object, new Mock<IChannelFactory>().Object, new Mock<IChannelManager>().Object,
            new Mock<IChannelMemoryRepository>().Object, new Mock<ILogger<OpenChannelClientHandler>>().Object,
            new Mock<IMessageFactory>().Object, _peerManager.Object, _utxos.Object,
            nodeOptions: Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
            dualFundedOpenService: _dualFund.Object);
}