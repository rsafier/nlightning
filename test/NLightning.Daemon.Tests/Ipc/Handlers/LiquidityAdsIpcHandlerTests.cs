using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Application.LiquidityAds;
using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Responses;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Node.Options;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// Liquidity ads (NL-850) over IPC: <c>liquidityads</c> (ClientCommand 46) through the envelope, and the keys added to
/// <c>openchannel</c> (9/10), <c>splicein</c> (3/4), <c>bumpopen</c> (3/4), <c>closechannel</c> (4) and to their
/// responses (the purchase), which an older client or daemon simply leaves out.
/// </summary>
public class LiquidityAdsIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x31, 32).ToArray());

    public LiquidityAdsIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task Given_ConfiguredRates_When_LiquidityAdsRatesGoOverIpc_Then_TheRatesComeBack()
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
        var handler = GetHandler(options);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(new LiquidityAdsIpcRequest
        {
            Action = LiquidityAdsAction.Rates
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<LiquidityAdsIpcResponse>(response.Payload, s_options,
                                                                                 TestContext.Current
                                                                                    .CancellationToken);
        Assert.Equal(LiquidityAdsAction.Rates, payload.Action);
        Assert.NotNull(payload.OurRates);
        Assert.Equal(LiquidityAdsTestData.Rate, Assert.Single(payload.OurRates).ToFundingRate());
        Assert.Equal(4_032U, payload.LeaseBlocks);
    }

    [Fact]
    public async Task Given_APageOutOfBounds_When_PurchasesGoOverIpc_Then_InvalidOperation()
    {
        // Act
        var response = await GetHandler(new NodeOptions()).HandleAsync(CreateEnvelope(new LiquidityAdsIpcRequest
        {
            Action = LiquidityAdsAction.Purchases,
            Take = 0
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
    }

    [Fact]
    public void Given_AResponseWithSellersAndPurchases_When_RoundTripped_Then_EveryFieldIsKept()
    {
        // Arrange
        var rates = WillFundRates.Create([LiquidityAdsTestData.Rate], [LiquidityPaymentType.FromChannelBalance]);
        var response = LiquidityAdsIpcResponse.FromClientResponse(
            new LiquidityAdsClientResponse(LiquidityAdsAction.Purchases)
            {
                Sellers = [new LiquiditySellerInfo(LiquidityAdsTestData.Peer, LiquiditySellerSource.Init, rates, true,
                                                   "eclair")],
                Purchases = [LiquidityAdsTestData.Restored(s_channelId, LiquidityPurchaseStatus.Closed)],
                CurrentHeight = 2_500,
                LeaseBlocks = 4_032
            });

        // Act
        var bytes = MessagePackSerializer.Serialize(response, s_options, TestContext.Current.CancellationToken);
        var read = MessagePackSerializer.Deserialize<LiquidityAdsIpcResponse>(bytes, s_options,
                                                                              TestContext.Current.CancellationToken);

        // Assert
        var seller = Assert.Single(read.Sellers);
        Assert.Equal(LiquidityAdsTestData.Peer, seller.NodeId);
        Assert.Equal(LiquiditySellerSource.Init, seller.Source);
        Assert.Equal("eclair", seller.Alias);
        Assert.Equal(LiquidityAdsTestData.Rate, Assert.Single(seller.Rates).ToFundingRate());
        var purchase = Assert.Single(read.Purchases);
        Assert.Equal(s_channelId, purchase.ChannelId);
        Assert.Equal(LiquidityPurchaseRole.Seller, purchase.Role);
        Assert.Equal(LiquidityPurchaseKind.Splice, purchase.Kind);
        Assert.Equal(LiquidityPurchaseStatus.Closed, purchase.Status);
        Assert.Equal(LiquidityAdsTestData.Peer, purchase.PeerNodeId);
        Assert.Equal(400_000UL, purchase.RequestedSat);
        Assert.Equal(410_000UL, purchase.ContributedSat);
        Assert.Equal(1_250UL, purchase.MiningFeeSat);
        Assert.Equal(5_010UL, purchase.ServiceFeeSat);
        Assert.Equal(100U, purchase.LeaseStartHeight);
        Assert.Equal(4_132U, purchase.LeaseEndHeight);
        Assert.Equal(2_000U, purchase.ClosedAtHeight);
        Assert.True(purchase.ClosedEarly);
        Assert.Equal(2_500U, read.CurrentHeight);
    }

    [Fact]
    public void Given_TheLiquidityKeys_When_RequestsAreRoundTripped_Then_TheyReachTheClientRequests()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var open = new OpenChannelIpcRequest
        {
            NodeInfo = "02abc@127.0.0.1:9735",
            Amount = LightningMoney.Satoshis(500_000),
            RequestInboundSat = 400_000,
            MaxLiquidityFeeSat = 9_000
        };
        var splice = new SpliceInIpcRequest
        {
            ChannelId = s_channelId,
            AmountSat = 100_000,
            RequestInboundSat = 300_000,
            MaxLiquidityFeeSat = 7_000
        };
        var bump = new BumpOpenIpcRequest
        {
            ChannelId = s_channelId,
            FeeRatePerKw = 3_000,
            RequestInboundSat = 200_000,
            MaxLiquidityFeeSat = 6_000
        };
        var close = new CloseChannelIpcRequest { ChannelId = s_channelId, Force = true };

        // Act
        var openRead = RoundTrip(open, ct).ToClientRequest();
        var spliceRead = RoundTrip(splice, ct).ToClientRequest();
        var bumpRead = RoundTrip(bump, ct).ToClientRequest();
        var closeRead = RoundTrip(close, ct).ToClientRequest();

        // Assert
        Assert.Equal(400_000UL, openRead.RequestInboundSat);
        Assert.Equal(9_000UL, openRead.MaxLiquidityFeeSat);
        Assert.Equal(new LiquidityRequest(300_000, null, 7_000), spliceRead.ToSpliceRequest().Liquidity);
        Assert.Equal(new LiquidityRequest(200_000, null, 6_000), bumpRead.ToLiquidityRequest());
        Assert.True(closeRead.Force);
    }

    [Fact]
    public void Given_RequestsFromAnOlderClient_When_Deserialized_Then_NothingIsBoughtOrForced()
    {
        // Arrange: each request cut after its last key from before NL-850
        var ct = TestContext.Current.CancellationToken;
        var splice = MessagePackSerializer.Serialize(new SpliceInIpcRequest { ChannelId = s_channelId, AmountSat = 1 },
                                                     s_options, ct);
        var bump = MessagePackSerializer.Serialize(new BumpOpenIpcRequest
        {
            ChannelId = s_channelId,
            FeeRatePerKw = 3_000
        }, s_options, ct);
        var close = MessagePackSerializer.Serialize(new CloseChannelIpcRequest { ChannelId = s_channelId }, s_options,
                                                    ct);
        Assert.Equal(0x95, splice[0]); // keys 0-4
        Assert.Equal(0x95, bump[0]); // keys 0-4
        Assert.Equal(0x95, close[0]); // keys 0-4
        byte[] olderSplice = [0x93, .. splice[1..^2]];
        byte[] olderBump = [0x93, .. bump[1..^2]];
        byte[] olderClose = [0x94, .. close[1..^1]];

        // Act
        var spliceRead = MessagePackSerializer.Deserialize<SpliceInIpcRequest>(olderSplice, s_options, ct);
        var bumpRead = MessagePackSerializer.Deserialize<BumpOpenIpcRequest>(olderBump, s_options, ct);
        var closeRead = MessagePackSerializer.Deserialize<CloseChannelIpcRequest>(olderClose, s_options, ct);

        // Assert
        Assert.Null(spliceRead.ToClientRequest().ToSpliceRequest().Liquidity);
        Assert.Null(bumpRead.ToClientRequest().ToLiquidityRequest());
        Assert.False(closeRead.ToClientRequest().Force);
    }

    [Fact]
    public void Given_ResponsesWithAPurchase_When_RoundTripped_Then_ThePurchaseIsKept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var purchase = LiquidityAdsTestData.Purchase(s_channelId);
        var open = OpenChannelIpcResponse.FromClientResponse(new OpenChannelClientResponse(s_channelId)
        {
            FundingTxId = LiquidityAdsTestData.FundingTxId,
            FundingOutputIndex = 0,
            Purchase = purchase
        });
        var splice = SpliceIpcResponse.FromClientResponse(new SpliceClientResponse(s_channelId,
                                                                                   SpliceNegotiationState.Signed)
        {
            Purchase = purchase
        });
        var bump = BumpOpenIpcResponse.FromClientResponse(new BumpOpenClientResponse(s_channelId,
                                                                                     LiquidityAdsTestData.FundingTxId)
        {
            Purchase = purchase
        });

        // Act
        var openRead = RoundTrip(open, ct);
        var spliceRead = RoundTrip(splice, ct);
        var bumpRead = RoundTrip(bump, ct);

        // Assert
        foreach (var read in new[] { openRead.Purchase, spliceRead.Purchase, bumpRead.Purchase })
        {
            Assert.NotNull(read);
            Assert.Equal(6_260UL, read.TotalFeeSat);
            Assert.Equal(LiquidityPurchaseStatus.Pending, read.Status);
            Assert.Null(read.LeaseEndHeight);
        }
    }

    private static T RoundTrip<T>(T value, CancellationToken ct) =>
        MessagePackSerializer.Deserialize<T>(MessagePackSerializer.Serialize(value, s_options, ct), s_options, ct);

    private static IIpcCommandHandler GetHandler(NodeOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new LiquidityAdsService(new Mock<ILightningSigner>().Object,
                                                      new Mock<IServiceProvider>().Object,
                                                      NullLogger<LiquidityAdsService>.Instance,
                                                      Options.Create(options)));
        services.AddLiquidityAdsIpcServices();
        return services.BuildServiceProvider().GetServices<IIpcCommandHandler>()
                       .Single(h => h.Command == ClientCommand.LiquidityAds);
    }

    private static IpcEnvelope CreateEnvelope(LiquidityAdsIpcRequest request) =>
        new()
        {
            Version = 1,
            Command = ClientCommand.LiquidityAds,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options)
        };
}