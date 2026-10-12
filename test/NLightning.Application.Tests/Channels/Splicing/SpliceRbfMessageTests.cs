namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Splicing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;

/// <summary>
/// The splice RBF's <c>tx_init_rbf</c>/<c>tx_ack_rbf</c> rewritten with our signed <c>funding_output_contribution</c>
/// (<c>SpliceService.WithContribution</c>, NL-481/NL-503) keep every other record, the liquidity ads request and
/// answer included (NL-850, plan LA2: an RBF of a purchase keeps requesting).
/// </summary>
public class SpliceRbfMessageTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x31, 32).ToArray());
    private static readonly FundingRate s_rate = new(10_000, 500_000, 550, 100, 5_000, 1_000);

    [Fact]
    public void Given_ATxInitRbfWithARequest_When_TheContributionIsSet_Then_TheRequestIsKept()
    {
        // Arrange
        var request = new RequestFunding(100_000, s_rate, LiquidityPaymentDetails.FromChannelBalance);
        var message = new TxInitRbfMessage(new TxInitRbfPayload(s_channelId, 3_000, 120), null,
                                           new RequireConfirmedInputsTlv(), new RequestFundingTlv(request));

        // Act
        var rewritten = Assert.IsType<TxInitRbfMessage>(
            Assert.Single(SpliceService.WithContribution([message], -20_000)));

        // Assert
        Assert.Equal(-20_000, rewritten.FundingOutputContributionTlv?.Satoshis);
        Assert.NotNull(rewritten.RequireConfirmedInputsTlv);
        Assert.Equal(request, rewritten.RequestFundingTlv?.Request);
        Assert.Same(message.Payload, rewritten.Payload);
    }

    [Fact]
    public void Given_ATxAckRbfWithAnAnswer_When_TheContributionIsSet_Then_TheAnswerIsKept()
    {
        // Arrange
        var willFund = new WillFund(s_rate, Convert.FromHexString("0020" + new string('c', 64)),
                                    new CompactSignature(Enumerable.Repeat((byte)0x05, 64).ToArray()));
        var message = new TxAckRbfMessage(new TxAckRbfPayload(s_channelId), null, null,
                                          new ProvideFundingTlv(willFund));

        // Act
        var rewritten = Assert.IsType<TxAckRbfMessage>(Assert.Single(SpliceService.WithContribution([message], 0)));

        // Assert: the TLV is sent with 0 too (NL-503)
        Assert.Equal(0, rewritten.FundingOutputContributionTlv?.Satoshis);
        Assert.Null(rewritten.RequireConfirmedInputsTlv);
        Assert.Equal(willFund, rewritten.ProvideFundingTlv?.WillFund);
    }

    [Fact]
    public void Given_RbfMessagesWithoutLiquidityAds_When_TheContributionIsSet_Then_NoneIsAdded()
    {
        // Arrange
        IChannelMessage other = new TxAbortMessage(new TxAbortPayload(s_channelId, []));
        var message = new TxInitRbfMessage(new TxInitRbfPayload(s_channelId, 3_000, 120));

        // Act
        var rewritten = SpliceService.WithContribution([message, other], 5_000);

        // Assert
        var init = Assert.IsType<TxInitRbfMessage>(rewritten[0]);
        Assert.Null(init.RequestFundingTlv);
        Assert.Equal(5_000, init.FundingOutputContributionTlv?.Satoshis);
        Assert.Same(other, rewritten[1]);
    }
}