using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Factories;
using Helpers;
using Serialization.Messages;

/// <summary>
/// Liquidity ads (BOLT PR #1153 as Eclair 0.14.3 speaks it, NL-850 LA2): the TLV 1339 records on <c>init</c>,
/// <c>open_channel2</c>, <c>accept_channel2</c>, <c>tx_init_rbf</c>, <c>tx_ack_rbf</c>, <c>splice_init</c> and
/// <c>splice_ack</c>, against the message vectors of Eclair's <c>LightningMessageCodecsSpec</c>.
/// </summary>
public class LiquidityAdsMessageTests
{
    // Eclair's fundingRate of the RBF and splice vectors: FundingRate(25_000, 250_000, 750, 150, 50, 500) and
    // FundingRate(100_000, 100_000, 400, 150, 0, 0)
    private static readonly FundingRate s_rbfRate = new(25_000, 250_000, 750, 150, 50, 500);
    private static readonly FundingRate s_spliceRate = new(100_000, 100_000, 400, 150, 0, 0);
    private static readonly FundingRate s_rate1 = new(100_000, 500_000, 550, 100, 5_000, 1_000);
    private static readonly FundingRate s_rate2 = new(500_000, 5_000_000, 1_100, 75, 0, 1_500);

    private const string ZeroSignature =
        "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    private const string TxInitRbfHex =
        "0048" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "00000000" + "00000FA0"
      + "00080000000000000000" + "FD053B1E000000000000C350000061A80003D09002EE009600000032000001F40000";

    private const string TxAckRbfHex =
        "0049" + "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB" + "0008000000000000C350" + "0200"
      + "FD053B5A000061A80003D09002EE009600000032000001F40004DEADBEEF" + ZeroSignature;

    private const string SpliceInitHex =
        "0050" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "00000000000186A0" + "000009C4"
      + "00000064" + "0279BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798"
      + "FD053B1E00000000000186A0000186A0000186A00190009600000000000000000000";

    private const string SpliceAckHex =
        "0051" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "00000000000061A8"
      + "0279BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798"
      + "FD053B5A000186A0000186A00190009600000000000000000004DEADBEEF" + ZeroSignature;

    private const string OpenChannel2Hex =
        "0040" + "6FE28C0AB6F1B372C1A6A246AE63F74F931E8365E15A089C68D6190000000000"
      + "0100000000000000000000000000000000000000000000000000000000000000" + "00001388" + "00000FA0"
      + "000000000003D090" + "00000000000001F4" + "000000000000C350" + "000000000000000F" + "0090" + "01E3"
      + "0009EB10" + "031B84C5567B126440995D3ED5AABA0565D71E1834604819FF9C17F5E9D5DD078F"
      + "024D4B6CD1361032CA9BD2AEB9D900AA4D45D9EAD80AC9423374C451A7254D0766"
      + "02531FE6068134503D2723133227C867AC8FA6C83C537E9A44C3C5BDBDCB1FE337"
      + "03462779AD4AAD39514614751A71085F2F10E1C7A593E4E030EFB5B8721CE55B0B"
      + "0362C0A046DACCE86DDD0343C6D3C7C79C2208BA0D9C9CF24A6D046D21D21F90F7"
      + "03F006A18D5653C4EDF5391FF23A61F03FF83D237E880EE61187FA9F379A028E0A"
      + "02989C0B76CB563971FDC9BEF31EC06C3560F3249D6EE9E5D83C57625596E05F6F" + "01"
      + "FD053B1E" + "00000000000B71B0" + "0007A120004C4B40044C004B00000000000005DC" + "0000";

    private const string AcceptChannel2Hex =
        "0041" + "0100000000000000000000000000000000000000000000000000000000000000" + "00000000000AAE60"
      + "00000000000001D9" + "0000000005F5E100" + "0000000000000001" + "00000006" + "0090" + "0032"
      + "031B84C5567B126440995D3ED5AABA0565D71E1834604819FF9C17F5E9D5DD078F"
      + "024D4B6CD1361032CA9BD2AEB9D900AA4D45D9EAD80AC9423374C451A7254D0766"
      + "02531FE6068134503D2723133227C867AC8FA6C83C537E9A44C3C5BDBDCB1FE337"
      + "03462779AD4AAD39514614751A71085F2F10E1C7A593E4E030EFB5B8721CE55B0B"
      + "0362C0A046DACCE86DDD0343C6D3C7C79C2208BA0D9C9CF24A6D046D21D21F90F7"
      + "03F006A18D5653C4EDF5391FF23A61F03FF83D237E880EE61187FA9F379A028E0A"
      + "02989C0B76CB563971FDC9BEF31EC06C3560F3249D6EE9E5D83C57625596E05F6F"
      + "FD053B78" + "0007A120004C4B40044C004B00000000000005DC"
      + "002200202EC38203F4CF37A3B377D9A55C7AE0153C643046DBDBE2FFCCFB11B74420103C"
      + "C57CF393F6BD534472EC08CBFBBC7268501B32F563A21CDF02A99127C4F25168249ACD6509F96B2E93843C3B838EE4808C75D0A15FF71BA886FDA980B8CA954F";

    private const string InitOneRateHex =
        "0010" + "0000" + "0002088A" + "FD053B19" + "0001" + "000186A00007A1200226006400001388000003E8" + "0001" + "01";

    private const string InitTwoRatesHex =
        "0010" + "0000" + "0002088A" + "FD053B47" + "0002" + "000186A00007A1200226006400001388000003E8"
      + "0007A120004C4B40044C004B00000000000005DC" + "001B"
      + "080000000000000000000700000000000000000000000000000001";

    private readonly MessageSerializer _messageSerializer =
        new(NullLogger<MessageSerializer>.Instance,
            new MessageTypeSerializerFactory(SerializerHelper.PayloadSerializerFactory,
                                             SerializerHelper.TlvStreamSerializer));

    [Fact]
    public async Task Given_EclairTxInitRbfWithRequestFunding_When_RoundTripped_Then_ItIsByteExact()
    {
        // Act
        var message = await DeserializeAsync<TxInitRbfMessage>(TxInitRbfHex);
        var bytes = await SerializeAsync(message);

        // Assert
        Assert.NotNull(message.RequestFundingTlv);
        Assert.Equal(new RequestFunding(50_000, s_rbfRate, LiquidityPaymentDetails.FromChannelBalance),
                     message.RequestFundingTlv.Request);
        Assert.Equal(TxInitRbfHex, Convert.ToHexString(bytes));
    }

    [Fact]
    public async Task Given_EclairTxAckRbfWithProvideFunding_When_RoundTripped_Then_ItIsByteExact()
    {
        // Act
        var message = await DeserializeAsync<TxAckRbfMessage>(TxAckRbfHex);
        var bytes = await SerializeAsync(message);

        // Assert
        Assert.NotNull(message.RequireConfirmedInputsTlv);
        Assert.NotNull(message.ProvideFundingTlv);
        Assert.Equal(s_rbfRate, message.ProvideFundingTlv.WillFund.Rate);
        Assert.Equal("DEADBEEF", Convert.ToHexString(message.ProvideFundingTlv.WillFund.FundingScript));
        Assert.Equal(TxAckRbfHex, Convert.ToHexString(bytes));
    }

    [Fact]
    public async Task Given_EclairSpliceInitWithRequestFunding_When_RoundTripped_Then_ItIsByteExact()
    {
        // Act
        var message = await DeserializeAsync<SpliceInitMessage>(SpliceInitHex);
        var bytes = await SerializeAsync(message);

        // Assert
        Assert.Null(message.RequireConfirmedInputsTlv);
        Assert.NotNull(message.RequestFundingTlv);
        Assert.Equal(new RequestFunding(100_000, s_spliceRate, LiquidityPaymentDetails.FromChannelBalance),
                     message.RequestFundingTlv.Request);
        Assert.Equal(100_000, message.Payload.FundingContributionSatoshis);
        Assert.Equal(SpliceInitHex, Convert.ToHexString(bytes));
    }

    [Fact]
    public async Task Given_EclairSpliceAckWithProvideFunding_When_RoundTripped_Then_ItIsByteExact()
    {
        // Act
        var message = await DeserializeAsync<SpliceAckMessage>(SpliceAckHex);
        var bytes = await SerializeAsync(message);

        // Assert
        Assert.Null(message.RequireConfirmedInputsTlv);
        Assert.NotNull(message.ProvideFundingTlv);
        Assert.Equal(s_spliceRate, message.ProvideFundingTlv.WillFund.Rate);
        Assert.Equal(SpliceAckHex, Convert.ToHexString(bytes));
    }

    [Fact]
    public async Task Given_EclairOpenChannel2WithRequestFunding_When_RoundTripped_Then_ItIsByteExact()
    {
        // Act
        var message = await DeserializeAsync<OpenChannel2Message>(OpenChannel2Hex);
        var bytes = await SerializeAsync(message);

        // Assert
        Assert.NotNull(message.RequestFundingTlv);
        Assert.Equal(new RequestFunding(750_000, s_rate2, LiquidityPaymentDetails.FromChannelBalance),
                     message.RequestFundingTlv.Request);
        Assert.Equal(OpenChannel2Hex, Convert.ToHexString(bytes));
    }

    [Fact]
    public async Task Given_EclairAcceptChannel2WithProvideFunding_When_RoundTripped_Then_ItIsByteExact()
    {
        // Act
        var message = await DeserializeAsync<AcceptChannel2Message>(AcceptChannel2Hex);
        var bytes = await SerializeAsync(message);

        // Assert: the funding script is the P2WSH of the 2-of-2 of both funding keys
        Assert.NotNull(message.ProvideFundingTlv);
        Assert.Equal(s_rate2, message.ProvideFundingTlv.WillFund.Rate);
        Assert.Equal("00202EC38203F4CF37A3B377D9A55C7AE0153C643046DBDBE2FFCCFB11B74420103C",
                     Convert.ToHexString(message.ProvideFundingTlv.WillFund.FundingScript));
        Assert.Equal(AcceptChannel2Hex, Convert.ToHexString(bytes));
    }

    [Fact]
    public async Task Given_EclairInitWithOneRate_When_Deserialized_Then_RatesAreReadAndWrittenBack()
    {
        // Act
        var message = await DeserializeAsync<InitMessage>(InitOneRateHex);
        var bytes = await SerializeAsync(message);

        // Assert
        Assert.NotNull(message.WillFundRatesTlv);
        Assert.Equal([s_rate1], message.WillFundRatesTlv.Rates.Rates);
        Assert.True(message.WillFundRatesTlv.Rates.Supports(LiquidityPaymentType.FromChannelBalance));
        Assert.Null(message.UndecodableWillFundRates);
        Assert.EndsWith(InitOneRateHex[16..], Convert.ToHexString(bytes));
    }

    [Fact]
    public async Task Given_EclairInitWithTwoRatesAndOnTheFlyTypes_When_Deserialized_Then_EveryTypeIsKept()
    {
        // Act
        var message = await DeserializeAsync<InitMessage>(InitTwoRatesHex);
        var bytes = await SerializeAsync(message);

        // Assert
        Assert.NotNull(message.WillFundRatesTlv);
        var rates = message.WillFundRatesTlv.Rates;
        Assert.Equal([s_rate1, s_rate2], rates.Rates);
        Assert.True(rates.Supports(LiquidityPaymentType.FromChannelBalance));
        Assert.True(rates.Supports(LiquidityPaymentType.FromFutureHtlc));
        Assert.True(rates.Supports(LiquidityPaymentType.FromFutureHtlcWithPreimage));
        Assert.True(rates.Supports(LiquidityPaymentType.FromChannelBalanceForFutureHtlc));
        Assert.True(rates.SupportsBit(211));
        Assert.EndsWith(InitTwoRatesHex[16..], Convert.ToHexString(bytes));
    }

    [Fact]
    public async Task Given_InitWithMalformedRates_When_Deserialized_Then_TheInitIsKeptWithoutRates()
    {
        // Arrange: a count of two with only one rate
        const string hex = "0010" + "0000" + "0002088A" + "FD053B19" + "0002"
                         + "000186A00007A1200226006400001388000003E8" + "0001" + "01";

        // Act
        var message = await DeserializeAsync<InitMessage>(hex);

        // Assert: the raw value is kept for the receiver's log
        Assert.Null(message.WillFundRatesTlv);
        Assert.Equal("0002000186A00007A1200226006400001388000003E8000101",
                     Convert.ToHexString(message.UndecodableWillFundRates!));
    }

    [Fact]
    public async Task Given_TxInitRbfWithMalformedRequest_When_Deserialized_Then_ItThrows()
    {
        // Arrange: the payment details cut short
        var hex = TxInitRbfHex[..^4].Replace("FD053B1E", "FD053B1C");

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => DeserializeAsync<TxInitRbfMessage>(hex));
    }

    [Fact]
    public async Task Given_OurMessagesWithLiquidityRecords_When_RoundTripped_Then_TheRecordsAreLast()
    {
        // Arrange
        var request = new RequestFundingTlv(new RequestFunding(50_000, s_rbfRate,
                                                               LiquidityPaymentDetails.FromChannelBalance));
        var message = new TxInitRbfMessage(new TxInitRbfPayload(new byte[32], 4_000, 0),
                                           new FundingOutputContributionTlv(0L), new RequireConfirmedInputsTlv(),
                                           request);

        // Act
        var bytes = await SerializeAsync(message);
        var parsed = await DeserializeAsync<TxInitRbfMessage>(Convert.ToHexString(bytes));

        // Assert
        Assert.EndsWith("0200" + "FD053B1E000000000000C350000061A80003D09002EE009600000032000001F40000",
                        Convert.ToHexString(bytes));
        Assert.Equal(request.Request, parsed.RequestFundingTlv?.Request);
    }

    private async Task<T> DeserializeAsync<T>(string hex) where T : class, IMessage
    {
        using var stream = new MemoryStream(Convert.FromHexString(hex));
        var message = await _messageSerializer.DeserializeMessageAsync(stream);
        return Assert.IsType<T>(message);
    }

    private async Task<byte[]> SerializeAsync(IMessage message)
    {
        using var stream = new MemoryStream();
        await _messageSerializer.SerializeAsync(message, stream);
        return stream.ToArray();
    }
}