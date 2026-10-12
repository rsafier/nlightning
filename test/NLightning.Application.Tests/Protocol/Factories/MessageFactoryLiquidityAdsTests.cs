using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Protocol.Factories;

using Application.Protocol.Factories;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds.Constants;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Infrastructure.Serialization;

/// <summary>
/// Liquidity ads (NL-850, plan LA2) from the factory: <c>request_funding</c> on <c>open_channel2</c>,
/// <c>tx_init_rbf</c> and <c>splice_init</c>, <c>provide_funding</c> on <c>accept_channel2</c>, <c>tx_ack_rbf</c>
/// and <c>splice_ack</c>, our <c>option_will_fund</c> rates on <c>init</c> when we sell; byte-identical to the
/// messages without them when the argument is null.
/// </summary>
public class MessageFactoryLiquidityAdsTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x24, 32).ToArray());

    private static readonly CompactPubKey s_pubKey =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private static readonly FundingRate s_rate = new(100_000, 500_000, 550, 100, 5_000, 1_000);

    private static readonly RequestFunding s_request =
        new(250_000, s_rate, LiquidityPaymentDetails.FromChannelBalance);

    private static readonly WillFund s_willFund =
        new(s_rate, Convert.FromHexString("0020" + new string('a', 64)),
            new CompactSignature(Enumerable.Repeat((byte)0x11, 64).ToArray()));

    private static readonly ChannelParty s_localParams =
        new(LightningMoney.Satoshis(354), LightningMoney.Zero, LightningMoney.MilliSatoshis(1), 483,
            LightningMoney.Satoshis(500_000), 144);

    private readonly MessageFactory _messageFactory = new(Options.Create(new NodeOptions()));
    private readonly IMessageSerializer _serializer;

    public MessageFactoryLiquidityAdsTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSerializationInfrastructureServices();
        _serializer = services.BuildServiceProvider().GetRequiredService<IMessageSerializer>();
    }

    [Fact]
    public async Task Given_ARequest_When_CreatingOpenChannel2_Then_ItCarriesRequestFundingLast()
    {
        // Act
        var message = CreateOpenChannel2(s_request);
        var parsed = await RoundTripAsync<OpenChannel2Message>(message);

        // Assert
        Assert.NotNull(message.RequestFundingTlv);
        Assert.Equal(s_request, message.RequestFundingTlv.Request);
        Assert.Equal(LiquidityAdsConstants.TlvType, message.Extension!.GetTlvs().Last().Type.Value);
        Assert.Equal(s_request, parsed.RequestFundingTlv?.Request);
    }

    [Fact]
    public async Task Given_NoRequest_When_CreatingOpenChannel2_Then_ItIsTheMessageWithoutTheTlv()
    {
        // Act
        var message = CreateOpenChannel2(null);

        // Assert
        Assert.Null(message.RequestFundingTlv);
        Assert.Equal(await SerializeAsync(new OpenChannel2Message(message.Payload, null, message.ChannelTypeTlv)),
                     await SerializeAsync(message));
    }

    [Fact]
    public async Task Given_AnAnswer_When_CreatingAcceptChannel2_Then_ItCarriesProvideFunding()
    {
        // Act
        var message = CreateAcceptChannel2(s_willFund);
        var parsed = await RoundTripAsync<AcceptChannel2Message>(message);

        // Assert
        Assert.NotNull(message.ProvideFundingTlv);
        Assert.Equal(s_willFund, message.ProvideFundingTlv.WillFund);
        Assert.Equal(s_willFund, parsed.ProvideFundingTlv?.WillFund);
    }

    [Fact]
    public async Task Given_NoAnswer_When_CreatingAcceptChannel2_Then_ItIsTheMessageWithoutTheTlv()
    {
        // Act
        var message = CreateAcceptChannel2(null);

        // Assert
        Assert.Null(message.ProvideFundingTlv);
        Assert.Equal(await SerializeAsync(new AcceptChannel2Message(message.Payload, null, message.ChannelTypeTlv)),
                     await SerializeAsync(message));
    }

    [Fact]
    public async Task Given_ARequest_When_CreatingTxInitRbf_Then_ItCarriesRequestFunding()
    {
        // Act
        var message = _messageFactory.CreateTxInitRbfMessage(s_channelId, 120, 2_600, 50_000, true, s_request);
        var parsed = await RoundTripAsync<TxInitRbfMessage>(message);

        // Assert
        Assert.Equal(s_request, message.RequestFundingTlv?.Request);
        Assert.Equal(50_000, parsed.FundingOutputContributionTlv?.Satoshis);
        Assert.NotNull(parsed.RequireConfirmedInputsTlv);
        Assert.Equal(s_request, parsed.RequestFundingTlv?.Request);
    }

    [Fact]
    public async Task Given_NoRequest_When_CreatingTxInitRbf_Then_ItIsTheMessageWithoutTheTlv()
    {
        // Act
        var message = _messageFactory.CreateTxInitRbfMessage(s_channelId, 120, 2_600, 50_000, true);

        // Assert
        Assert.Null(message.RequestFundingTlv);
        Assert.Equal(await SerializeAsync(new TxInitRbfMessage(new TxInitRbfPayload(s_channelId, 2_600, 120),
                                                               new FundingOutputContributionTlv(50_000L),
                                                               new RequireConfirmedInputsTlv())),
                     await SerializeAsync(message));
    }

    [Fact]
    public async Task Given_AnAnswer_When_CreatingTxAckRbf_Then_ItCarriesProvideFunding()
    {
        // Act
        var message = _messageFactory.CreateTxAckRbfMessage(s_channelId, 0, false, s_willFund);
        var parsed = await RoundTripAsync<TxAckRbfMessage>(message);

        // Assert
        Assert.Equal(s_willFund, message.ProvideFundingTlv?.WillFund);
        Assert.Null(parsed.FundingOutputContributionTlv);
        Assert.Equal(s_willFund, parsed.ProvideFundingTlv?.WillFund);
    }

    [Fact]
    public async Task Given_NoAnswer_When_CreatingTxAckRbf_Then_ItIsTheMessageWithoutTheTlv()
    {
        // Act
        var message = _messageFactory.CreateTxAckRbfMessage(s_channelId, -1_000, false);

        // Assert
        Assert.Null(message.ProvideFundingTlv);
        Assert.Equal(await SerializeAsync(new TxAckRbfMessage(new TxAckRbfPayload(s_channelId),
                                                              new FundingOutputContributionTlv(-1_000L))),
                     await SerializeAsync(message));
    }

    [Fact]
    public async Task Given_ARequest_When_CreatingSpliceInit_Then_ItCarriesRequestFunding()
    {
        // Act
        var message = _messageFactory.CreateSpliceInitMessage(s_channelId, 0, 2_500, 120, s_pubKey,
                                                              requestFunding: s_request);
        var parsed = await RoundTripAsync<SpliceInitMessage>(message);

        // Assert
        Assert.Equal(s_request, message.RequestFundingTlv?.Request);
        Assert.Null(parsed.RequireConfirmedInputsTlv);
        Assert.Equal(s_request, parsed.RequestFundingTlv?.Request);
    }

    [Fact]
    public async Task Given_NoRequest_When_CreatingSpliceInit_Then_ItIsTheMessageWithoutTheTlv()
    {
        // Act
        var message = _messageFactory.CreateSpliceInitMessage(s_channelId, 10_000, 2_500, 120, s_pubKey, true);

        // Assert
        Assert.Null(message.RequestFundingTlv);
        Assert.Equal(await SerializeAsync(new SpliceInitMessage(message.Payload, new RequireConfirmedInputsTlv())),
                     await SerializeAsync(message));
    }

    [Fact]
    public async Task Given_AnAnswer_When_CreatingSpliceAck_Then_ItCarriesProvideFunding()
    {
        // Act
        var message = _messageFactory.CreateSpliceAckMessage(s_channelId, 250_000, s_pubKey, true, s_willFund);
        var parsed = await RoundTripAsync<SpliceAckMessage>(message);

        // Assert
        Assert.Equal(s_willFund, message.ProvideFundingTlv?.WillFund);
        Assert.NotNull(parsed.RequireConfirmedInputsTlv);
        Assert.Equal(s_willFund, parsed.ProvideFundingTlv?.WillFund);
    }

    [Fact]
    public async Task Given_NoAnswer_When_CreatingSpliceAck_Then_ItIsTheMessageWithoutTheTlv()
    {
        // Act
        var message = _messageFactory.CreateSpliceAckMessage(s_channelId, 0, s_pubKey);

        // Assert
        Assert.Null(message.ProvideFundingTlv);
        Assert.Null(message.Extension);
        Assert.Equal(await SerializeAsync(new SpliceAckMessage(message.Payload)), await SerializeAsync(message));
    }

    [Fact]
    public async Task Given_WeDoNotSell_When_CreatingInit_Then_ItHasNoRates()
    {
        // Act
        var message = _messageFactory.CreateInitMessage();
        var parsed = await RoundTripAsync<InitMessage>(message);

        // Assert (decision D-L3: not selling by default)
        Assert.Null(message.WillFundRatesTlv);
        Assert.Null(parsed.WillFundRatesTlv);
    }

    [Fact]
    public async Task Given_WeSell_When_CreatingInit_Then_ItCarriesOurRates()
    {
        // Arrange
        var options = new NodeOptions();
        options.LiquidityAds.FundingRates =
        [
            new FundingRateOptions
            {
                MinAmountSat = 100_000,
                MaxAmountSat = 500_000,
                FundingWeight = 550,
                FeeBasis = 100,
                FeeBaseSat = 5_000,
                ChannelCreationFeeSat = 1_000
            }
        ];
        var factory = new MessageFactory(Options.Create(options));

        // Act
        var message = factory.CreateInitMessage();
        var parsed = await RoundTripAsync<InitMessage>(message);

        // Assert: Eclair's one-rate init vector, from_channel_balance only (decision D-L2)
        Assert.NotNull(message.WillFundRatesTlv);
        Assert.Equal("0001000186a00007a1200226006400001388000003e8000101",
                     Convert.ToHexStringLower(message.WillFundRatesTlv.Value));
        Assert.NotNull(message.NetworksTlv);
        Assert.Equal(options.LiquidityAds.GetWillFundRates(), parsed.WillFundRatesTlv?.Rates);
    }

    private OpenChannel2Message CreateOpenChannel2(RequestFunding? request) =>
        _messageFactory.CreateOpenChannel2Message(s_channelId, 2_500, 2_500, LightningMoney.Satoshis(1_000_000),
                                                  s_localParams, 120, s_pubKey, s_pubKey, s_pubKey, s_pubKey,
                                                  s_pubKey, s_pubKey, s_pubKey, new ChannelFlags((byte)0),
                                                  new ChannelTypeTlv(new byte[] { 0x10, 0x00 }), null, false, request);

    private AcceptChannel2Message CreateAcceptChannel2(WillFund? willFund) =>
        _messageFactory.CreateAcceptChannel2Message(s_channelId, LightningMoney.Satoshis(250_000), s_localParams, 3,
                                                    s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey,
                                                    s_pubKey, new ChannelTypeTlv(new byte[] { 0x10, 0x00 }), null, false,
                                                    willFund);

    private async Task<byte[]> SerializeAsync(IMessage message)
    {
        using var stream = new MemoryStream();
        await _serializer.SerializeAsync(message, stream);
        return stream.ToArray();
    }

    private async Task<TMessage> RoundTripAsync<TMessage>(IMessage message) where TMessage : class, IMessage
    {
        using var stream = new MemoryStream(await SerializeAsync(message));
        return Assert.IsType<TMessage>(await _serializer.DeserializeMessageAsync(stream));
    }
}