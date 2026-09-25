namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Helpers;
using Serialization.Messages.Types;

public class OpenChannel1MessageTests
{
    // BOLT 2 open_channel (type 32) body, field by field in spec order.
    private const string ChainHashHex = "06226E46111A0B59CAAF126043EB5BBF28C34F3A5E332A1FC7B2B73CF188910F";
    private const string TemporaryChannelIdHex = "0101010101010101010101010101010101010101010101010101010101010101";
    private const string FundingSatoshisHex = "0000000000030D40"; // 200_000 sat
    private const string PushMsatHex = "00000000000003E8"; // 1_000 msat
    private const string DustLimitSatoshisHex = "000000000000022A"; // 554 sat
    private const string MaxHtlcValueInFlightMsatHex = "0000000005F5E100"; // 100_000_000 msat
    private const string ChannelReserveSatoshisHex = "00000000000007D0"; // 2_000 sat
    private const string HtlcMinimumMsatHex = "00000000000003E8"; // 1_000 msat
    private const string FeeratePerKwHex = "000009C4"; // 2_500
    private const string ToSelfDelayHex = "0090"; // 144
    private const string MaxAcceptedHtlcsHex = "01E3"; // 483
    private const string FundingPubkeyHex = "023DA092F6980E58D2C037173180E9A465476026EE50F96695963E8EFE436F54EB";
    private const string RevocationBasepointHex = "036D6CAAC248AF96F6AFA7F904F550253A0F3EF3F5AA2FE6838A95B216691468E2";
    private const string PaymentBasepointHex = "034F355BDCB7CC0AF728EF3CCEB9615D90684BB5B2CA5F859AB0F0B704075871AA";
    private const string DelayedPaymentBasepointHex =
        "03FD5960528DC152014952EFDB702A88F71E3C1653B2314431701EC77E57FDE83C";
    private const string HtlcBasepointHex = "032C0B7CF95324A07D05398B240174DC0C2BE444D96B159AA6C7F7B1E668680991";
    private const string FirstPerCommitmentPointHex =
        "025F7117A78150FE2EF97DB7CFC83BD57B2E2C0D0DD25EAF467A4A1C2A45CE1486";
    private const string ChannelFlagsHex = "01"; // announce_channel

    // TLV type 0 (upfront_shutdown_script): a 22-byte P2WPKH script.
    private const string ShutdownScriptHex = "00140102030405060708090A0B0C0D0E0F1011121314";
    private const string UpfrontShutdownScriptTlvHex = "0016" + ShutdownScriptHex;

    // TLV type 1 (channel_type): single-byte and multi-byte spec channel types (big-endian on the wire).
    private const string SingleByteChannelTypeHex = "10";

    private const string PayloadHex = ChainHashHex + TemporaryChannelIdHex + FundingSatoshisHex + PushMsatHex
                                    + DustLimitSatoshisHex + MaxHtlcValueInFlightMsatHex + ChannelReserveSatoshisHex
                                    + HtlcMinimumMsatHex + FeeratePerKwHex + ToSelfDelayHex + MaxAcceptedHtlcsHex
                                    + FundingPubkeyHex + RevocationBasepointHex + PaymentBasepointHex
                                    + DelayedPaymentBasepointHex + HtlcBasepointHex + FirstPerCommitmentPointHex
                                    + ChannelFlagsHex;

    private const string Point = "02C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A75";
    private const string Zero32 = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly string s_openChannelPayloadHex =
        Zero32 + Zero32 + new string('0', 6 * 16) + "000003E8" + "0090" + "01E3"
      + string.Concat(Enumerable.Repeat(Point, 6)) + "00";

    private static readonly string s_acceptChannelPayloadHex =
        Zero32 + new string('0', 4 * 16) + "00000003" + "0090" + "01E3" + string.Concat(Enumerable.Repeat(Point, 6));

    private readonly OpenChannel1MessageTypeSerializer _openChannel1Serializer =
        new(SerializerHelper.PayloadSerializerFactory, SerializerHelper.TlvConverterFactory,
            SerializerHelper.TlvStreamSerializer);

    private readonly AcceptChannel1MessageTypeSerializer _acceptChannel1Serializer =
        new(SerializerHelper.PayloadSerializerFactory, SerializerHelper.TlvConverterFactory,
            SerializerHelper.TlvStreamSerializer);

    private readonly OpenChannel1MessageTypeSerializer _serializer =
        new(SerializerHelper.PayloadSerializerFactory, SerializerHelper.TlvConverterFactory,
            SerializerHelper.TlvStreamSerializer);

    [Fact]
    public async Task Given_SpecShapedBytes_When_DeserializeAsync_Then_AllFieldsAreDecoded()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex + UpfrontShutdownScriptTlvHex
                                                                       + ChannelTypeTlv(SingleByteChannelTypeHex)));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        var payload = message.Payload;
        Assert.Equal(MessageTypes.OpenChannel, message.Type);
        Assert.Equal(ChainConstants.Regtest, payload.ChainHash);
        Assert.Equal(new ChannelId(Convert.FromHexString(TemporaryChannelIdHex)), payload.ChannelId);
        Assert.Equal(LightningMoney.Satoshis(200_000), payload.FundingAmount);
        Assert.Equal(LightningMoney.MilliSatoshis(1_000), payload.PushAmount);
        Assert.Equal(LightningMoney.Satoshis(554), payload.DustLimitAmount);
        Assert.Equal(LightningMoney.MilliSatoshis(100_000_000), payload.MaxHtlcValueInFlight);
        Assert.Equal(LightningMoney.Satoshis(2_000), payload.ChannelReserveAmount);
        Assert.Equal(LightningMoney.MilliSatoshis(1_000), payload.HtlcMinimumAmount);
        Assert.Equal(LightningMoney.Satoshis(2_500), payload.FeeRatePerKw);
        Assert.Equal(144, payload.ToSelfDelay);
        Assert.Equal(483, payload.MaxAcceptedHtlcs);
        Assert.Equal(new CompactPubKey(Convert.FromHexString(FundingPubkeyHex)), payload.FundingPubKey);
        Assert.Equal(new CompactPubKey(Convert.FromHexString(RevocationBasepointHex)), payload.RevocationBasepoint);
        Assert.Equal(new CompactPubKey(Convert.FromHexString(PaymentBasepointHex)), payload.PaymentBasepoint);
        Assert.Equal(new CompactPubKey(Convert.FromHexString(DelayedPaymentBasepointHex)),
                     payload.DelayedPaymentBasepoint);
        Assert.Equal(new CompactPubKey(Convert.FromHexString(HtlcBasepointHex)), payload.HtlcBasepoint);
        Assert.Equal(new CompactPubKey(Convert.FromHexString(FirstPerCommitmentPointHex)),
                     payload.FirstPerCommitmentPoint);
        Assert.True(payload.ChannelFlags.AnnounceChannel);
        Assert.NotNull(message.UpfrontShutdownScriptTlv);
        Assert.Equal(Convert.FromHexString(ShutdownScriptHex),
                     (byte[])message.UpfrontShutdownScriptTlv.ShutdownScriptPubkey);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(SingleByteChannelTypeHex)]
    [InlineData("1000")] // option_static_remotekey (bit 12)
    [InlineData("401000")] // option_anchors (bit 22) + option_static_remotekey
    public async Task Given_ChannelTypeTlv_When_DeserializeAsync_Then_ChannelTypeBytesArePreserved(
        string channelTypeHex)
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex + ChannelTypeTlv(channelTypeHex)));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Null(message.UpfrontShutdownScriptTlv);
        Assert.Equal(Convert.FromHexString(channelTypeHex), message.ChannelTypeTlv?.ChannelType);
    }

    [Theory]
    [InlineData(SingleByteChannelTypeHex)]
    [InlineData("1000")]
    [InlineData("401000")]
    public async Task Given_Message_When_SerializeAsync_Then_WritesSpecShapedBytes(string channelTypeHex)
    {
        // Arrange
        var message = CreateMessage(channelTypeHex);
        var stream = new MemoryStream();
        var expected = Convert.FromHexString(PayloadHex + UpfrontShutdownScriptTlvHex
                                                        + ChannelTypeTlv(channelTypeHex));

        // Act
        await _serializer.SerializeAsync(message, stream);

        // Assert
        Assert.Equal(expected, stream.ToArray());
    }

    [Theory]
    [InlineData(SingleByteChannelTypeHex)]
    [InlineData("1000")]
    public async Task Given_Message_When_RoundTripped_Then_BytesAreStable(string channelTypeHex)
    {
        // Arrange
        var first = new MemoryStream();
        await _serializer.SerializeAsync(CreateMessage(channelTypeHex), first);
        first.Position = 0;

        // Act
        var decoded = await _serializer.DeserializeAsync(first);
        var second = new MemoryStream();
        await _serializer.SerializeAsync(decoded, second);

        // Assert
        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public async Task Given_TruncatedPayload_When_DeserializeAsync_Then_Throws()
    {
        // Arrange - drop the last byte (channel_flags)
        var bytes = Convert.FromHexString(PayloadHex);
        var stream = new MemoryStream(bytes[..^1]);

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => _serializer.DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_NoChannelTypeTlv_When_DeserializeAsync_Then_MessageIsDecoded()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex + UpfrontShutdownScriptTlvHex));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message.UpfrontShutdownScriptTlv);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0000")] // empty upfront_shutdown_script only
    public async Task Given_OpenChannelWithoutChannelType_When_DeserializeAsync_Then_ReturnsMessageWithNullChannelType(
        string extensionHex)
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(s_openChannelPayloadHex + extensionHex));

        // Act
        var message = await _openChannel1Serializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message);
        Assert.Null(message.ChannelTypeTlv);
    }

    [Fact]
    public async Task Given_OpenChannelWithChannelType_When_DeserializeAsync_Then_ReturnsMessageWithChannelType()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(s_openChannelPayloadHex + "01021000"));

        // Act
        var message = await _openChannel1Serializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message.ChannelTypeTlv);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0000")] // empty upfront_shutdown_script only
    public async Task
        Given_AcceptChannelWithoutChannelType_When_DeserializeAsync_Then_ReturnsMessageWithNullChannelType(
            string extensionHex)
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(s_acceptChannelPayloadHex + extensionHex));

        // Act
        var message = await _acceptChannel1Serializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message);
        Assert.Null(message.ChannelTypeTlv);
    }

    private static string ChannelTypeTlv(string channelTypeHex)
    {
        return "01" + (channelTypeHex.Length / 2).ToString("X2") + channelTypeHex;
    }

    private static OpenChannel1Message CreateMessage(string channelTypeHex)
    {
        var payload = new OpenChannel1Payload(ChainConstants.Regtest, new ChannelFlags(1),
                                              Convert.FromHexString(TemporaryChannelIdHex),
                                              LightningMoney.Satoshis(2_000),
                                              Convert.FromHexString(DelayedPaymentBasepointHex),
                                              LightningMoney.Satoshis(554), LightningMoney.Satoshis(2_500),
                                              Convert.FromHexString(FirstPerCommitmentPointHex),
                                              LightningMoney.Satoshis(200_000),
                                              Convert.FromHexString(FundingPubkeyHex),
                                              Convert.FromHexString(HtlcBasepointHex),
                                              LightningMoney.MilliSatoshis(1_000), 483,
                                              LightningMoney.MilliSatoshis(100_000_000),
                                              Convert.FromHexString(PaymentBasepointHex),
                                              LightningMoney.MilliSatoshis(1_000),
                                              Convert.FromHexString(RevocationBasepointHex), 144);

        return new OpenChannel1Message(payload, new ChannelTypeTlv(Convert.FromHexString(channelTypeHex)),
                                       new UpfrontShutdownScriptTlv(Convert.FromHexString(ShutdownScriptHex)));
    }
}