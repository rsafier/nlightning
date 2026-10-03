using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.LiquidityAds;

using Application.LiquidityAds;
using Domain.Bitcoin.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;

public class LiquidityAdsServiceTests
{
    private static readonly FundingRate s_small = new(10_000, 100_000, 500, 100, 1_000, 500);
    private static readonly FundingRate s_large = new(50_000, 1_000_000, 500, 50, 2_000, 500);
    private static readonly CompactPubKey s_buyer =
        new(Convert.FromHexString("031b84c5567b126440995d3ed5aaba0565d71e1834604819ff9c17f5e9d5dd078f"));
    private static readonly CompactPubKey s_otherBuyer =
        new(Convert.FromHexString("024d4b6cd1361032ca9bd2aeb9d900aa4d45d9ead80ac9423374c451a7254d0766"));

    [Fact]
    public void Given_NoRatesConfigured_When_ARequestArrives_Then_ItIsRefused()
    {
        // Arrange
        var service = CreateService(new NodeOptions());

        // Act
        var refusal = service.TryStartSale(s_buyer, Request(50_000, s_small), out var sale);

        // Assert
        Assert.Equal("we do not sell liquidity", refusal);
        Assert.Null(sale);
    }

    [Fact]
    public void Given_RatesAndCaps_When_SalesStartAndEnd_Then_TheCapsHoldAndSlotsComeBack()
    {
        // Arrange
        var options = SellingOptions(maxConcurrent: 2, maxPerPeer: 1);
        var service = CreateService(options);

        // Act
        var first = service.TryStartSale(s_buyer, Request(50_000, s_small), out var firstSale);
        var samePeer = service.TryStartSale(s_buyer, Request(50_000, s_small), out _);
        var other = service.TryStartSale(s_otherBuyer, Request(60_000, s_large), out var otherSale);
        var third = service.TryStartSale(new CompactPubKey(Convert.FromHexString(
                                                               "02531fe6068134503d2723133227c867ac8fa6c83c537e9a44c3c5bdbdcb1fe337")),
                                         Request(60_000, s_large), out _);
        firstSale!.Dispose();
        firstSale.Dispose();
        var again = service.TryStartSale(s_buyer, Request(50_000, s_small), out var againSale);

        // Assert
        Assert.Null(first);
        Assert.Contains("with this peer", samePeer);
        Assert.Null(other);
        Assert.Equal("too many liquidity sales in progress", third);
        Assert.Null(again);
        Assert.Equal(2, service.SalesInProgress);
        otherSale!.Dispose();
        againSale!.Dispose();
        Assert.Equal(0, service.SalesInProgress);
    }

    [Theory]
    [InlineData(5_000ul)]
    [InlineData(200_000ul)]
    public void Given_AnAmountOutsideTheRate_When_ARequestArrives_Then_ItIsRefused(ulong amount)
    {
        // Arrange
        var service = CreateService(SellingOptions());

        // Act
        var refusal = service.TryStartSale(s_buyer, Request(amount, s_small), out _);

        // Assert
        Assert.Contains(nameof(LiquidityAdsRefusal.AmountOutOfRange), refusal);
    }

    [Fact]
    public void Given_ARateWeDoNotSell_When_ARequestArrives_Then_ItIsRefused()
    {
        // Arrange
        var service = CreateService(SellingOptions());

        // Act
        var refusal = service.TryStartSale(s_buyer, Request(50_000, s_small with { FeeBasis = 1 }), out _);

        // Assert
        Assert.Contains(nameof(LiquidityAdsRefusal.UnknownRate), refusal);
    }

    [Fact]
    public void Given_SellerRatesInItsInit_When_CreatingARequest_Then_TheCheapestCompatibleRateIsPicked()
    {
        // Arrange
        var rates = WillFundRates.Create([s_small, s_large], [LiquidityPaymentType.FromChannelBalance]);
        var service = CreateService(new NodeOptions(), sellerRates: rates);

        // Act
        var cheap = service.CreateRequest(s_buyer, new LiquidityRequest(80_000), 2_500, true);
        var onlyLarge = service.CreateRequest(s_buyer, new LiquidityRequest(500_000), 2_500, true);
        var pinned = service.CreateRequest(s_buyer, new LiquidityRequest(80_000, s_large), 2_500, true);

        // Assert: at 80,000 sat the small rate costs 1,000 + 500 + 800 = 2,300 sat, the large one 2,000 + 500 + 400
        Assert.Equal(s_small, cheap.Rate);
        Assert.Equal(s_large, onlyLarge.Rate);
        Assert.Equal(s_large, pinned.Rate);
        Assert.Equal(80_000ul, cheap.RequestedSat);
        Assert.Throws<InvalidOperationException>(() => service.CreateRequest(s_buyer, new LiquidityRequest(5_000_000),
                                                                             2_500, true));
        Assert.Throws<InvalidOperationException>(
            () => service.CreateRequest(s_buyer, new LiquidityRequest(80_000, s_small with { FeeBaseSat = 1 }), 2_500,
                                        true));
    }

    [Fact]
    public void Given_ASellerWithoutRates_When_CreatingARequest_Then_ItThrows()
    {
        // Arrange
        var service = CreateService(new NodeOptions());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => service.CreateRequest(s_buyer, new LiquidityRequest(80_000),
                                                                             2_500, true));
    }

    [Fact]
    public void Given_OurWillFund_When_TheBuyerValidatesIt_Then_TheFeesAreComputed()
    {
        // Arrange: the signer signs and verifies a fixed signature
        var signature = new CompactSignature(Enumerable.Repeat((byte)7, 64).ToArray());
        var signer = new Mock<ILightningSigner>();
        signer.Setup(s => s.SignNodeMessage(It.IsAny<Hash>())).Returns(signature);
        signer.Setup(s => s.VerifyNodeMessage(It.IsAny<Hash>(), signature, s_buyer)).Returns(true);
        var service = CreateService(SellingOptions(), signer: signer.Object);
        var request = Request(50_000, s_small);
        var script = new byte[] { 0x00, 0x20, 1, 2, 3 };

        // Act
        var willFund = service.CreateWillFund(s_small, script);
        var refusal = service.ValidateWillFund(s_buyer, request, willFund, script, 50_000, 2_500, true, null,
                                               out var fees);
        var tooDear = service.ValidateWillFund(s_buyer, request, willFund, script, 50_000, 2_500, true, 10, out _);

        // Assert: mining 2,500 x 500 / 1,000 = 1,250; service 1,000 + 500 + 50,000 x 1 % = 2,000
        Assert.Equal(LiquidityAdsRefusal.None, refusal);
        Assert.Equal(new LiquidityFees(1_250, 2_000), fees);
        Assert.Equal(LiquidityAdsRefusal.FeeTooHigh, tooDear);
        signer.Verify(s => s.SignNodeMessage(LiquidityAdsRules.SignedData(s_small, script)));
    }

    private static RequestFunding Request(ulong amount, FundingRate rate) =>
        new(amount, rate, LiquidityPaymentDetails.FromChannelBalance);

    private static NodeOptions SellingOptions(int maxConcurrent = 4, int maxPerPeer = 1)
    {
        var options = new NodeOptions();
        options.LiquidityAds.MaxConcurrentSales = maxConcurrent;
        options.LiquidityAds.MaxSalesPerPeer = maxPerPeer;
        foreach (var rate in new[] { s_small, s_large })
            options.LiquidityAds.FundingRates.Add(new FundingRateOptions
            {
                MinAmountSat = rate.MinAmountSat,
                MaxAmountSat = rate.MaxAmountSat,
                FundingWeight = rate.FundingWeight,
                FeeBasis = rate.FeeBasis,
                FeeBaseSat = rate.FeeBaseSat,
                ChannelCreationFeeSat = rate.ChannelCreationFeeSat
            });
        return options;
    }

    private static LiquidityAdsService CreateService(NodeOptions options, WillFundRates? sellerRates = null,
                                                     ILightningSigner? signer = null)
    {
        var provider = new FakeServiceProvider();
        if (sellerRates is not null)
        {
            var peerService = new Mock<IPeerService>();
            peerService.SetupGet(p => p.LiquidityRates).Returns(sellerRates);
            var peer = new PeerModel(s_buyer, "127.0.0.1", 9735, "IPv4");
            peer.SetPeerService(peerService.Object);
            var peerManager = new Mock<IPeerManager>();
            peerManager.Setup(m => m.GetPeer(s_buyer)).Returns(peer);
            provider.AddService(typeof(IPeerManager), peerManager.Object);
        }

        return new LiquidityAdsService(signer ?? new Mock<ILightningSigner>().Object, provider,
                                       NullLogger<LiquidityAdsService>.Instance, Options.Create(options));
    }
}