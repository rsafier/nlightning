namespace NLightning.Daemon.Tests.Client;

using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Enums;
using Domain.Client.Responses;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Ipc.Handlers;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of liquidity ads (NL-771): <c>liquidityads rates|sellers|purchases</c> (ClientCommand 46), the
/// <c>--request-inbound</c>/<c>--max-liquidity-fee</c> options of <c>openchannel</c>, <c>splicein</c> and
/// <c>bumpopen</c>, <c>closechannel --force</c>, and the printed purchases.
/// </summary>
public class LiquidityAdsCommandsTests
{
    private const string ChannelIdHex = "0101010101010101010101010101010101010101010101010101010101010101";
    private const string NodeHex = "02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27";

    private static readonly ChannelId s_channelId = new(Convert.FromHexString(ChannelIdHex));

    #region liquidityads

    [Theory]
    [InlineData(new[] { "rates" }, LiquidityAdsAction.Rates)]
    [InlineData(new[] { "SELLERS" }, LiquidityAdsAction.Sellers)]
    [InlineData(new[] { "purchases" }, LiquidityAdsAction.Purchases)]
    public void Given_AnAction_When_Parsed_Then_TheRequestNamesIt(string[] args, LiquidityAdsAction action)
    {
        // Act
        var request = LiquidityAdsCommands.Parse(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(action, request.Action);
        Assert.Equal(LiquidityAdsCommands.DefaultLimit, request.Take);
        Assert.Null(ClientApp.ValidateArguments("liquidityads", args));
        Assert.Null(ClientApp.ValidateArguments("liquidity-ads", args));
    }

    [Fact]
    public void Given_PurchaseFilters_When_Parsed_Then_TheyReachTheRequest()
    {
        // Act
        var request = LiquidityAdsCommands.Parse(
                          ["purchases", "--role", "seller", "--status=active", "--skip", "10", "--limit=50"],
                          out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(LiquidityPurchaseRole.Seller, request.Role);
        Assert.Equal(LiquidityPurchaseStatus.Active, request.Status);
        Assert.Equal(10, request.Skip);
        Assert.Equal(50, request.Take);
    }

    [Theory]
    [InlineData(new string[0], "Missing the action")]
    [InlineData(new[] { "buy" }, "Unknown action 'buy'")]
    [InlineData(new[] { "rates", "--role", "buyer" }, "Unexpected argument '--role'")]
    [InlineData(new[] { "purchases", "--role", "lender" }, "Invalid role 'lender'")]
    [InlineData(new[] { "purchases", "--status", "open" }, "Invalid status 'open'")]
    [InlineData(new[] { "purchases", "--limit", "0" }, "Invalid limit '0'")]
    [InlineData(new[] { "purchases", "--limit", "1001" }, "Invalid limit '1001'")]
    [InlineData(new[] { "purchases", "--skip", "-1" }, "Invalid skip '-1'")]
    [InlineData(new[] { "purchases", "--skip" }, "Missing value for --skip")]
    [InlineData(new[] { "purchases", "--page", "1" }, "Unknown option '--page'")]
    public void Given_BadArguments_When_Validated_Then_UsageError(string[] args, string expected)
    {
        // Act
        var error = ClientApp.ValidateArguments("liquidityads", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expected, error);
        Assert.Contains(LiquidityAdsCommands.Usage, error);
    }

    [Fact]
    public void Given_NoRates_When_RatesArePrinted_Then_NotSelling()
    {
        // Arrange
        using var output = new StringWriter();

        // Act
        new LiquidityAdsPrinter(output).Print(new LiquidityAdsIpcResponse { Action = LiquidityAdsAction.Rates });

        // Assert
        Assert.StartsWith("Not selling liquidity", output.ToString());
    }

    [Fact]
    public void Given_OurRates_When_Printed_Then_EveryRateField()
    {
        // Arrange
        using var output = new StringWriter();
        var response = LiquidityAdsIpcResponse.FromClientResponse(
            new LiquidityAdsClientResponse(LiquidityAdsAction.Rates)
            {
                OurRates = WillFundRates.Create([LiquidityAdsTestData.Rate],
                                                [LiquidityPaymentType.FromChannelBalance]),
                LeaseBlocks = 4_032,
                SalesInProgress = 1
            });

        // Act
        new LiquidityAdsPrinter(output).Print(response);

        // Assert
        var printed = output.ToString();
        Assert.Contains("Selling liquidity:", printed);
        Assert.Contains("Lease:              4032 blocks", printed);
        Assert.Contains("Payment types:      from_channel_balance", printed);
        Assert.Contains("Sales in progress:  1", printed);
        Assert.Contains("Rate 1:  100000-1000000 sat, fee base 10 sat + 100 basis points, channel creation fee 1000 "
                      + "sat, funding weight 500", printed);
    }

    [Fact]
    public void Given_Sellers_When_Printed_Then_EachWithItsSourceAndRates()
    {
        // Arrange
        using var output = new StringWriter();
        var rates = WillFundRates.Create([LiquidityAdsTestData.Rate], [LiquidityPaymentType.FromChannelBalance]);
        var response = LiquidityAdsIpcResponse.FromClientResponse(
            new LiquidityAdsClientResponse(LiquidityAdsAction.Sellers)
            {
                Sellers =
                [
                    new LiquiditySellerInfo(LiquidityAdsTestData.Peer, LiquiditySellerSource.Init, rates, true,
                                            "nltg-eclair"),
                    new LiquiditySellerInfo(LiquidityAdsTestData.Peer, LiquiditySellerSource.NodeAnnouncement, rates,
                                            false, null)
                ]
            });

        // Act
        new LiquidityAdsPrinter(output).Print(response);

        // Assert
        var printed = output.ToString();
        Assert.Contains("Sellers: 2", printed);
        Assert.Contains("Node ID:        " + LiquidityAdsTestData.PeerHex, printed);
        Assert.Contains("Alias:          nltg-eclair", printed);
        Assert.Contains("Source:         init (connected)", printed);
        Assert.Contains("Source:         node_announcement" + Environment.NewLine, printed);
        Assert.Contains("Rate 1:  100000-1000000 sat", printed);
    }

    [Theory]
    [InlineData(LiquidityPurchaseStatus.Active, 1_000U, "until block 4132 (3132 blocks left)")]
    [InlineData(LiquidityPurchaseStatus.Active, 5_000U, "ended at block 4132")]
    [InlineData(LiquidityPurchaseStatus.Closed, 5_000U, "ended early: the channel closed at block 2000, before the lease ended (block 4132)")]
    [InlineData(LiquidityPurchaseStatus.Pending, 1_000U, "4032 blocks from the funding's confirmation")]
    [InlineData(LiquidityPurchaseStatus.Replaced, 1_000U, "none (another attempt of the funding confirmed)")]
    public void Given_APurchase_When_Printed_Then_ItsLeaseStatus(LiquidityPurchaseStatus status, uint height,
                                                                 string lease)
    {
        // Arrange
        using var output = new StringWriter();
        var response = LiquidityAdsIpcResponse.FromClientResponse(
            new LiquidityAdsClientResponse(LiquidityAdsAction.Purchases)
            {
                Purchases = [LiquidityAdsTestData.Restored(s_channelId, status)],
                CurrentHeight = height
            });

        // Act
        new LiquidityAdsPrinter(output).Print(response);

        // Assert
        var printed = output.ToString();
        Assert.Contains($"Sold on channel {ChannelIdHex}", printed);
        Assert.Contains("Fee:            6260 sat (mining 1250 sat, service 5010 sat)", printed);
        Assert.Contains("Lease:          " + lease, printed);
    }

    #endregion

    #region --request-inbound

    [Theory]
    [InlineData(new[] { "--request-inbound", "400000" }, 400_000UL, null)]
    [InlineData(new[] { "--request-inbound=400000", "--max-liquidity-fee=5000" }, 400_000UL, 5_000UL)]
    [InlineData(new[] { "--max-liquidity-fee", "0", "--request-inbound", "1" }, 1UL, 0UL)]
    public void Given_LiquidityOptions_When_Extracted_Then_TheRestIsKept(string[] options, ulong inbound,
                                                                       ulong? maxFee)
    {
        // Act
        var rest = LiquidityOptions.Extract(["a", .. options, "b"], out var liquidity, out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(rest);
        Assert.Equal(["a", "b"], rest);
        Assert.Equal(new LiquidityArguments(inbound, maxFee), liquidity);
    }

    [Theory]
    [InlineData(new[] { "--request-inbound" }, "Missing value for --request-inbound")]
    [InlineData(new[] { "--request-inbound", "0" }, "Invalid --request-inbound '0'")]
    [InlineData(new[] { "--request-inbound", "-5" }, "Invalid --request-inbound '-5'")]
    [InlineData(new[] { "--request-inbound", "2100000000000001" }, "Invalid --request-inbound")]
    [InlineData(new[] { "--max-liquidity-fee", "10" }, "--max-liquidity-fee needs --request-inbound")]
    [InlineData(new[] { "--request-inbound", "1", "--request-inbound", "2" }, "--request-inbound given twice")]
    public void Given_BadLiquidityOptions_When_Extracted_Then_Error(string[] options, string expected)
    {
        // Act
        var rest = LiquidityOptions.Extract(options, out _, out var error);

        // Assert
        Assert.Null(rest);
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_AnOpenThatBuysLiquidity_When_Parsed_Then_ItIsValidAndCarriesThePurchase(bool equalsForm)
    {
        // Arrange
        string[] args = equalsForm
                            ? [NodeHex, "500000", "--dual-fund", "--request-inbound=400000", "--max-liquidity-fee=9000"]
                            : [NodeHex, "500000", "--request-inbound", "400000"];

        // Act
        var positional = OpenChannelMessageHandler.ParseArguments(args, out _, out _, out var forceV1, out _,
                                                                  out var liquidity, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal([NodeHex, "500000"], positional);
        Assert.False(forceV1);
        Assert.Equal(400_000UL, liquidity.RequestInboundSat);
        Assert.Null(ClientApp.ValidateArguments("openchannel", args));
    }

    [Theory]
    [InlineData(new[] { NodeHex, "500000", "--v1", "--request-inbound", "400000" }, "can't be used with --v1")]
    [InlineData(new[] { NodeHex, "500000", "1000", "--request-inbound", "400000" }, "no push amount")]
    [InlineData(new[] { NodeHex, "500000", "--max-liquidity-fee", "1" }, "needs --request-inbound")]
    public void Given_AnOpenThatCannotBuyLiquidity_When_Validated_Then_UsageError(string[] args, string expected)
    {
        // Act
        var error = ClientApp.ValidateArguments("openchannel", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Given_ASpliceInThatBuysLiquidity_When_Parsed_Then_ItCarriesThePurchase()
    {
        // Arrange
        string[] args = [ChannelIdHex, "100000", "--request-inbound", "300000", "--feerate", "2500"];

        // Act
        var parsed = SpliceCommands.Parse(args, false, out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(2_500U, parsed.FeeRatePerKw);
        Assert.Equal(new LiquidityArguments(300_000, null), parsed.Liquidity);
        Assert.Null(ClientApp.ValidateArguments("splicein", args));
    }

    [Fact]
    public void Given_ASpliceOutWithRequestInbound_When_Validated_Then_UnknownOption()
    {
        // Act
        var error = ClientApp.ValidateArguments("spliceout", [ChannelIdHex, "100000", "--request-inbound", "1"]);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("Unknown option '--request-inbound'", error);
    }

    [Fact]
    public void Given_ABumpOpenThatBuysLiquidity_When_Parsed_Then_ItCarriesThePurchase()
    {
        // Arrange
        string[] args = [ChannelIdHex, "3000", "--request-inbound=200000", "--max-liquidity-fee", "6000"];

        // Act
        var parsed = BumpOpenCommands.Parse(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(3_000U, parsed.FeeRatePerKw);
        Assert.Equal(new LiquidityArguments(200_000, 6_000), parsed.Liquidity);
        Assert.Null(ClientApp.ValidateArguments("bumpopen", args));
    }

    [Fact]
    public void Given_ResponsesWithAPurchase_When_Printed_Then_ThePurchaseFollows()
    {
        // Arrange
        var purchase = LiquidityPurchaseIpcInfo.From(LiquidityAdsTestData.Purchase(s_channelId));
        using var open = new StringWriter();
        using var splice = new StringWriter();
        using var bump = new StringWriter();

        // Act
        new OpenChannelPrinter(open).Print(new OpenChannelIpcResponse
        {
            ChannelId = s_channelId,
            FundingTxId = LiquidityAdsTestData.FundingTxId,
            FundingOutputIndex = 0,
            Purchase = purchase
        });
        new SplicePrinter(splice).Print(new SpliceIpcResponse
        {
            ChannelId = s_channelId,
            State = SpliceNegotiationState.Signed,
            Purchase = purchase
        });
        BumpOpenCommands.Print(new BumpOpenIpcResponse
        {
            ChannelId = s_channelId,
            FundingTxId = "c2",
            Purchase = purchase
        }, bump);

        // Assert
        foreach (var printed in new[] { open.ToString(), splice.ToString(), bump.ToString() })
        {
            Assert.Contains("  Liquidity bought:  400000 sat requested, 410000 sat contributed by the seller", printed);
            Assert.Contains("  Liquidity fee:     6260 sat (mining 1250 sat, service 5010 sat), paid from our balance",
                            printed);
            Assert.Contains("  Liquidity lease:   4032 blocks from the funding's confirmation", printed);
        }
    }

    #endregion

    #region closechannel --force

    [Theory]
    [InlineData(new[] { ChannelIdHex, "--force" }, true)]
    [InlineData(new[] { "--force", ChannelIdHex, "0", "30", "nofeerange" }, true)]
    [InlineData(new[] { ChannelIdHex, "0", "30" }, false)]
    public void Given_ACloseWithOrWithoutForce_When_Parsed_Then_TheRestIsTheUsualClose(string[] args, bool force)
    {
        // Act
        var rest = ClientApp.ExtractCloseForce(args, out var forced);

        // Assert
        Assert.Equal(force, forced);
        Assert.Equal(ChannelIdHex, rest[0]);
        Assert.DoesNotContain("--force", rest);
        Assert.Null(ClientApp.ValidateArguments("closechannel", args));
    }

    [Fact]
    public void Given_OnlyForce_When_ACloseIsValidated_Then_TheChannelIsMissing()
    {
        // Act
        var error = ClientApp.ValidateArguments("closechannel", ["--force"]);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(ClientApp.CloseChannelUsage, error);
    }

    #endregion
}