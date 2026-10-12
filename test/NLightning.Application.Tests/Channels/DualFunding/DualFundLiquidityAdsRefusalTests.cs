using System.Text;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Domain.Channels.DualFunding.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using static LiquidityAdsKit;

/// <summary>
/// The refusals of liquidity ads in the dual-funded open (NL-850; Eclair 0.14.3's <c>validateRequest</c> and
/// <c>validateRemoteFunding</c>) on <see cref="DualFundHarness"/>. Seller: no rates, a rate it does not sell, an amount
/// outside the rate, a wallet that cannot fund the amount (never accepted without our funds), the griefing caps (D-L5)
/// — each an <c>error</c> for the open, the sale slot given back. Buyer: no answer, a tampered signature, a short
/// contribution — each an <c>error</c> before anything is funded, the result naming the refusal — and a fee above our
/// limit, refused before anything is sent. RBF: a <c>tx_init_rbf</c> that drops the purchase gets <c>tx_abort</c>, and
/// the seller cannot bump the attempt it sold in.
/// </summary>
public class DualFundLiquidityAdsRefusalTests
{
    private static readonly TimeSpan s_shortTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Given_BobDoesNotSell_When_AliceAsksForLiquidity_Then_BobRefusesTheOpen()
    {
        // Arrange: Alice believes Bob sells (a stale init), Bob has no rate
        await using var harness = await CreateAsync(openTimeout: s_shortTimeout, bobSells: false);
        AdvertiseToAlice(harness, Rate().ToFundingRate());

        // Act
        var result = await OpenAsync(harness);

        // Assert
        AssertSellerRefused(harness, result, "we do not sell liquidity");
    }

    [Fact]
    public async Task Given_ARateBobDoesNotSell_When_AliceAsksForLiquidity_Then_BobRefusesTheOpen()
    {
        // Arrange: Alice is told about a rate Bob does not have
        await using var harness = await CreateAsync(openTimeout: s_shortTimeout);
        var other = Rate();
        other.FeeBasis = 50;
        AdvertiseToAlice(harness, other.ToFundingRate());

        // Act
        var result = await OpenAsync(harness);

        // Assert
        AssertSellerRefused(harness, result, nameof(LiquidityAdsRefusal.UnknownRate));
    }

    [Fact]
    public async Task Given_AnAmountOutsideBobsRate_When_TheRequestArrives_Then_BobRefusesTheOpen()
    {
        // Arrange: Alice's open_channel2 asks for 2,000,000 sat (the rate sells at most 1,000,000)
        await using var harness = await CreateAsync(openTimeout: s_shortTimeout);
        var open = StartOpen(harness);
        var message = await TakeAsync<OpenChannel2Message>(harness, harness.Alice);
        var request = message.RequestFundingTlv!.Request with { RequestedSat = 2_000_000 };

        // Act
        await harness.DeliverAsync(harness.Alice,
                                   new OpenChannel2Message(message.Payload, message.UpfrontShutdownScriptTlv,
                                                           message.ChannelTypeTlv, message.RequireConfirmedInputsTlv,
                                                           new RequestFundingTlv(request)));
        var result = await harness.RunAsync(open);

        // Assert
        AssertSellerRefused(harness, result, nameof(LiquidityAdsRefusal.AmountOutOfRange));
    }

    [Fact]
    public async Task Given_APaymentTypeOtherThanTheChannelBalance_When_TheRequestArrives_Then_BobRefusesTheOpen()
    {
        // Arrange: Alice's open_channel2 asks to pay from future HTLCs (Eclair's on-the-fly funding, never accepted)
        await using var harness = await CreateAsync(openTimeout: s_shortTimeout);
        var open = StartOpen(harness);
        var message = await TakeAsync<OpenChannel2Message>(harness, harness.Alice);
        var request = message.RequestFundingTlv!.Request with
        {
            PaymentDetails = LiquidityPaymentDetails.Create((ulong)LiquidityPaymentType.FromFutureHtlc, [])
        };

        // Act
        await harness.DeliverAsync(harness.Alice,
                                   new OpenChannel2Message(message.Payload, message.UpfrontShutdownScriptTlv,
                                                           message.ChannelTypeTlv, message.RequireConfirmedInputsTlv,
                                                           new RequestFundingTlv(request)));
        var result = await harness.RunAsync(open);

        // Assert
        AssertSellerRefused(harness, result, nameof(LiquidityAdsRefusal.UnsupportedPaymentType));
    }

    [Fact]
    public async Task Given_BobsWalletCannotFundTheAmount_When_AliceAsksForLiquidity_Then_BobRefusesInsteadOfGivingNothing()
    {
        // Arrange: Bob sells but his wallet is empty
        await using var harness = await CreateAsync(bobWalletSat: 0, openTimeout: s_shortTimeout);

        // Act
        var result = await OpenAsync(harness);

        // Assert: refused (a plain accepter would have gone on without its funds)
        AssertSellerRefused(harness, result, "Cannot fund the requested liquidity");
        Assert.DoesNotContain(harness.Transcript, t => t.Message is AcceptChannel2Message);
    }

    [Fact]
    public async Task Given_ASaleInProgressWithAlice_When_HerSecondOpenAsksForLiquidity_Then_BobRefusesIt()
    {
        // Arrange: Bob sells to one negotiation per peer at a time (the default MaxSalesPerPeer); Alice opens twice
        await using var harness = await CreateAsync(openTimeout: TimeSpan.FromSeconds(5));
        harness.Alice.Wallet.Utxos.Add(InteractiveTx.TestDoubles.WalletUtxo.Create(1_000_000));
        var first = StartOpen(harness);
        var second = StartOpen(harness);

        // Act
        var firstResult = await harness.RunAsync(first);
        var secondResult = await harness.RunAsync(second);

        // Assert: the first one bought, the second was refused while the first held the slot, and no slot is held
        Assert.True(firstResult.FailureReason is null, $"{firstResult.FailureReason}\n{harness.Describe()}");
        Assert.NotNull(firstResult.Purchase);
        Assert.NotNull(secondResult.FailureReason);
        var error = Assert.IsType<ChannelErrorException>(Assert.Single(harness.Bob.Errors));
        Assert.Contains("too many liquidity sales in progress with this peer", error.Message);
        Assert.Single(harness.Bob.Memory.FindChannels(_ => true));
        Assert.Equal(0, LiquidityAds(harness.Bob).SalesInProgress);
    }

    [Fact]
    public async Task Given_BobsAnswerWithoutProvideFunding_When_AliceReadsIt_Then_TheOpenFailsBeforeAnythingIsFunded()
    {
        // Arrange
        await using var harness = await CreateAsync(openTimeout: s_shortTimeout);
        var open = StartOpen(harness);
        var accept = await TakeAsync<AcceptChannel2Message>(harness, harness.Bob);

        // Act
        await harness.DeliverAsync(harness.Bob, WithAnswer(accept, accept.Payload, null));
        var result = await harness.RunAsync(open);

        // Assert
        AssertBuyerRefused(harness, result, nameof(LiquidityAdsRefusal.Missing));

        // Bob's accepter watchdog ends his half of the open and gives his sale slot back
        await WaitForSlotsAsync(harness);
    }

    [Fact]
    public async Task Given_ATamperedWillFundSignature_When_AliceReadsIt_Then_TheOpenFails()
    {
        // Arrange
        await using var harness = await CreateAsync(openTimeout: s_shortTimeout);
        var open = StartOpen(harness);
        var accept = await TakeAsync<AcceptChannel2Message>(harness, harness.Bob);
        var willFund = accept.ProvideFundingTlv!.WillFund;
        var signature = ((byte[])willFund.Signature).ToArray();
        signature[10] ^= 0x01;

        // Act
        await harness.DeliverAsync(harness.Bob, WithAnswer(accept, accept.Payload,
                                                           new WillFund(willFund.Rate, willFund.FundingScript,
                                                                        new CompactSignature(signature))));
        var result = await harness.RunAsync(open);

        // Assert
        AssertBuyerRefused(harness, result, nameof(LiquidityAdsRefusal.BadSignature));
    }

    [Fact]
    public async Task Given_BobContributesLessThanRequested_When_AliceReadsIt_Then_TheOpenFails()
    {
        // Arrange
        await using var harness = await CreateAsync(openTimeout: s_shortTimeout);
        var open = StartOpen(harness);
        var accept = await TakeAsync<AcceptChannel2Message>(harness, harness.Bob);
        var p = accept.Payload;
        var shortPayload = new AcceptChannel2Payload(p.DelayedPaymentCompactBasepoint, p.DustLimitAmount,
                                                     p.FirstPerCommitmentCompactPoint,
                                                     LightningMoney.Satoshis(RequestedSat - 1), p.FundingCompactPubKey,
                                                     p.HtlcCompactBasepoint, p.HtlcMinimumAmount, p.MaxAcceptedHtlcs,
                                                     p.MaxHtlcValueInFlightAmount, p.MinimumDepth,
                                                     p.PaymentCompactBasepoint, p.RevocationCompactBasepoint,
                                                     p.ChannelId, p.ToSelfDelay, p.SecondPerCommitmentCompactPoint);

        // Act
        await harness.DeliverAsync(harness.Bob, WithAnswer(accept, shortPayload, accept.ProvideFundingTlv!.WillFund));
        var result = await harness.RunAsync(open);

        // Assert
        AssertBuyerRefused(harness, result, nameof(LiquidityAdsRefusal.AmountTooLow));
    }

    [Fact]
    public async Task Given_AFeeAboveTheLimit_When_AliceOpens_Then_NothingIsSent()
    {
        // Arrange: the fee at 2,500 sat/kw is 6,500 sat
        await using var harness = await CreateAsync(openTimeout: s_shortTimeout);
        Assert.Equal(6_500UL, Fees(2_500).TotalSat);

        // Act: the request's own limit, then the node's default limit
        var byRequest = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => StartOpen(harness, new LiquidityRequest(RequestedSat, MaxFeeSat: 6_499)));
        harness.Alice.Options.LiquidityAds.MaxFeeSat = 1_000;
        var byNode = await Assert.ThrowsAsync<InvalidOperationException>(() => StartOpen(harness));

        // Assert
        Assert.Contains("above the limit of 6499 sat", byRequest.Message);
        Assert.Contains("above the limit of 1000 sat", byNode.Message);
        await harness.PumpAsync();
        Assert.Empty(harness.Transcript);
    }

    [Fact]
    public async Task Given_AnOpenWithAPurchase_When_ATxInitRbfDropsTheRequest_Then_BobAnswersTxAbort()
    {
        // Arrange
        await using var harness = await CreateAsync(openTimeout: TimeSpan.FromSeconds(5));
        var first = await OpenAsync(harness);
        Assert.True(first.FailureReason is null, $"{first.FailureReason}\n{harness.Describe()}");
        var channelId = first.ChannelId;
        var bump = harness.Alice.DualFund.BumpAsync(channelId, 5_000, TestContext.Current.CancellationToken);
        var initRbf = await TakeAsync<TxInitRbfMessage>(harness, harness.Alice);

        // Act: the same tx_init_rbf without request_funding
        await harness.DeliverAsync(harness.Alice,
                                   new TxInitRbfMessage(initRbf.Payload, initRbf.FundingOutputContributionTlv,
                                                        initRbf.RequireConfirmedInputsTlv));
        var result = await harness.RunAsync(bump);

        // Assert: tx_abort naming the missing purchase, the bump failed, both nodes still on the first attempt's fee
        var abort = (TxAbortMessage)harness.Transcript.Single(t => t is { From: "Bob", Message: TxAbortMessage })
                                           .Message;
        Assert.Contains("InvalidRbfMissingLiquidityPurchase", Encoding.ASCII.GetString(abort.Payload.Data));
        Assert.NotNull(result.FailureReason);
        await AssertBalancesAsync(harness, channelId, 600_000, RequestedSat, Fees(2_500).TotalMsat);
        Assert.Equal(first.FundingTxId, harness.Bob.Channel(channelId).FundingOutput!.TransactionId);
        Assert.Equal(0, LiquidityAds(harness.Bob).SalesInProgress);
    }

    [Fact]
    public async Task Given_BobSoldInTheOpen_When_BobBumpsIt_Then_Refused()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var first = await OpenAsync(harness);

        // Act
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                          () => harness.Bob.DualFund.BumpAsync(first.ChannelId, 5_000,
                                                               TestContext.Current.CancellationToken));

        // Assert: only the buyer can carry its purchase into an RBF
        Assert.Contains("only the buyer can bump it", refused.Message);
    }

    /// <summary>Alice's peer service reports <paramref name="rate"/> as Bob's (his <c>init</c>).</summary>
    private static void AdvertiseToAlice(DualFundHarness harness, FundingRate rate) =>
        harness.Alice.PeerService.SetupGet(p => p.LiquidityRates)
               .Returns(WillFundRates.Create([rate], [LiquidityPaymentType.FromChannelBalance]));

    /// <summary>Pumps until <paramref name="from"/> queued a <typeparamref name="T"/>, then takes it undelivered.</summary>
    private static async Task<T> TakeAsync<T>(DualFundHarness harness, DualFundNode from) where T : IChannelMessage
    {
        for (var i = 0; i < 500; i++)
        {
            await harness.PumpAsync((sender, message) => sender == from.Name && message is T);
            if (harness.TakeNext(from) is { } next)
                return Assert.IsType<T>(next);

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"{from.Name} sent no {typeof(T).Name}\n{harness.Describe()}");
    }

    private static AcceptChannel2Message WithAnswer(AcceptChannel2Message accept, AcceptChannel2Payload payload,
                                                    WillFund? willFund) =>
        new(payload, accept.UpfrontShutdownScriptTlv, accept.ChannelTypeTlv, accept.RequireConfirmedInputsTlv,
            willFund is null ? null : new ProvideFundingTlv(willFund));

    /// <summary>Bob answered the open with an error naming the refusal, kept nothing, and holds no sale slot.</summary>
    private static void AssertSellerRefused(DualFundHarness harness, DualFundedOpenResult result, string reason)
    {
        var error = Assert.IsType<ChannelErrorException>(Assert.Single(harness.Bob.Errors));
        Assert.Contains(reason, error.Message);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(harness.Bob.Memory.FindChannels(_ => true));
        Assert.Equal(0, LiquidityAds(harness.Bob).SalesInProgress);
        Assert.False(harness.Alice.DualFund.IsOpening(result.ChannelId));
    }

    /// <summary>Alice failed the open with an error naming the refusal, funded nothing and recorded nothing.</summary>
    private static void AssertBuyerRefused(DualFundHarness harness, DualFundedOpenResult result, string reason)
    {
        var error = Assert.IsType<ChannelErrorException>(Assert.Single(harness.Alice.Errors));
        Assert.Contains(reason, error.Message);
        Assert.Contains(reason, result.FailureReason);
        Assert.DoesNotContain(harness.Transcript, t => t is { From: "Alice", Message: TxAddInputMessage });
        Assert.Empty(harness.Alice.Memory.FindChannels(_ => true));
        Assert.False(harness.Alice.DualFund.IsOpening(result.ChannelId));
    }

    /// <summary>Lets Bob's accepter watchdog fire and waits until his sale slot is given back.</summary>
    private static async Task WaitForSlotsAsync(DualFundHarness harness)
    {
        // The watchdog's delay starts on a background task: keep stepping the clock until it has fired, so a loaded run
        // that schedules it after the first step still sees it expire
        for (var i = 0; i < 1_000 && LiquidityAds(harness.Bob).SalesInProgress > 0; i++)
        {
            harness.Clock.Advance(harness.OpenTimeout + TimeSpan.FromSeconds(1));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, LiquidityAds(harness.Bob).SalesInProgress);
    }
}