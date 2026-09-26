using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Protocol.Factories;

using Application.Protocol.Factories;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Protocol.Tlv;

public class MessageFactoryTests
{
    private static readonly CompactPubKey s_pubKey =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    // Every value differs from the NodeOptions defaults, so a value read from the options instead shows up (NL-194)
    private static readonly ChannelParty s_localParams =
        new(LightningMoney.Satoshis(600), LightningMoney.Satoshis(2_500), LightningMoney.MilliSatoshis(1_234), 42,
            LightningMoney.Satoshis(77_000), 720);

    private readonly MessageFactory _messageFactory = new(Options.Create(new NodeOptions()));

    [Fact]
    public void Given_LocalParams_When_CreatingAcceptChannel1_Then_PayloadCarriesThem()
    {
        // Arrange
        var channelTypeTlv = new ChannelTypeTlv(FeatureSet.NewBasicChannelType());

        // Act
        var message = _messageFactory.CreateAcceptChannel1Message(s_localParams, channelTypeTlv, s_pubKey, s_pubKey,
                                                                  s_pubKey, s_pubKey, 3, s_pubKey, s_pubKey,
                                                                  ChannelId.Zero, null);

        // Assert
        var payload = message.Payload;
        Assert.Equal(s_localParams.DustLimitAmount, payload.DustLimitAmount);
        Assert.Equal(s_localParams.ChannelReserveAmount, payload.ChannelReserveAmount);
        Assert.Equal(s_localParams.HtlcMinimumAmount, payload.HtlcMinimumAmount);
        Assert.Equal(s_localParams.MaxAcceptedHtlcs, payload.MaxAcceptedHtlcs);
        Assert.Equal(s_localParams.MaxHtlcValueInFlight, payload.MaxHtlcValueInFlightAmount);
        Assert.Equal(s_localParams.ToSelfDelay, payload.ToSelfDelay);
        Assert.Equal(3U, payload.MinimumDepth);
        Assert.Same(channelTypeTlv, message.ChannelTypeTlv);
    }

    [Fact]
    public void Given_LocalParams_When_CreatingOpenChannel1_Then_PayloadCarriesThem()
    {
        // Act
        var message = _messageFactory.CreateOpenChannel1Message(ChannelId.Zero, LightningMoney.Satoshis(100_000),
                                                                s_pubKey, LightningMoney.Zero, s_localParams,
                                                                LightningMoney.Satoshis(253), s_pubKey, s_pubKey,
                                                                s_pubKey, s_pubKey, s_pubKey,
                                                                new ChannelFlags(ChannelFlag.None),
                                                                new ChannelTypeTlv(FeatureSet.NewBasicChannelType()),
                                                                null);

        // Assert
        var payload = message.Payload;
        Assert.Equal(s_localParams.DustLimitAmount, payload.DustLimitAmount);
        Assert.Equal(s_localParams.ChannelReserveAmount, payload.ChannelReserveAmount);
        Assert.Equal(s_localParams.HtlcMinimumAmount, payload.HtlcMinimumAmount);
        Assert.Equal(s_localParams.MaxAcceptedHtlcs, payload.MaxAcceptedHtlcs);
        Assert.Equal(s_localParams.MaxHtlcValueInFlight, payload.MaxHtlcValueInFlight);
        Assert.Equal(s_localParams.ToSelfDelay, payload.ToSelfDelay);
    }

    [Fact]
    public void Given_AttributionAndPayload_When_CreatingUpdateFulfillHtlc_Then_Tlv1And3AreSet()
    {
        // Arrange - BOLT 2 update_fulfill_htlc_tlvs (NL-022)
        var attribution = Enumerable.Repeat((byte)0x1A, 920).ToArray();
        var payload = Enumerable.Repeat((byte)0x3C, 272).ToArray();

        // Act
        var message = _messageFactory.CreateUpdateFulfillHtlcMessage(ChannelId.Zero, 7, new byte[32], attribution,
                                                                     payload);

        // Assert
        Assert.Equal(attribution, message.AttributionDataTlv!.AttributionData);
        Assert.Equal(payload, message.FulfillmentPayloadTlv!.FulfillmentPayload);
        Assert.NotNull(message.Extension);
    }

    [Fact]
    public void Given_NoAttribution_When_CreatingUpdateFailHtlc_Then_NoExtension()
    {
        // Act
        var message = _messageFactory.CreateUpdateFailHtlcMessage(ChannelId.Zero, 7, new byte[292]);

        // Assert
        Assert.Null(message.AttributionDataTlv);
        Assert.Null(message.Extension);
    }

    [Fact]
    public void Given_Attribution_When_CreatingUpdateFailHtlc_Then_Tlv1IsSet()
    {
        // Arrange
        var attribution = Enumerable.Repeat((byte)0x2B, 920).ToArray();

        // Act
        var message = _messageFactory.CreateUpdateFailHtlcMessage(ChannelId.Zero, 7, new byte[292], attribution);

        // Assert
        Assert.Equal(attribution, message.AttributionDataTlv!.AttributionData);
    }

    [Theory]
    [InlineData(919)]
    [InlineData(921)]
    public void Given_AttributionOfTheWrongLength_When_CreatingUpdateFailHtlc_Then_ArgumentException(int length)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(
            () => _messageFactory.CreateUpdateFailHtlcMessage(ChannelId.Zero, 7, new byte[292], new byte[length]));
    }
}