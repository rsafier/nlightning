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

public class AcceptChannel1MessageTests
{
    // BOLT 2 accept_channel (type 33) body, field by field in spec order.
    private const string TemporaryChannelIdHex = "0101010101010101010101010101010101010101010101010101010101010101";
    private const string DustLimitSatoshisHex = "000000000000014A"; // 330 sat
    private const string MaxHtlcValueInFlightMsatHex = "0000000005F5E100"; // 100_000_000 msat
    private const string ChannelReserveSatoshisHex = "00000000000007D0"; // 2_000 sat
    private const string HtlcMinimumMsatHex = "0000000000000001"; // 1 msat
    private const string MinimumDepthHex = "00000003"; // 3
    private const string ToSelfDelayHex = "0090"; // 144
    private const string MaxAcceptedHtlcsHex = "01E3"; // 483
    private const string FundingPubkeyHex = "030E9F7B623D2CCC7C9BD44D66D5CE21CE504C0ACF6385A132CEC6D3C39FA711C1";
    private const string RevocationBasepointHex = "036D6CAAC248AF96F6AFA7F904F550253A0F3EF3F5AA2FE6838A95B216691468E2";
    private const string PaymentBasepointHex = "032C0B7CF95324A07D05398B240174DC0C2BE444D96B159AA6C7F7B1E668680991";
    private const string DelayedPaymentBasepointHex =
        "03FD5960528DC152014952EFDB702A88F71E3C1653B2314431701EC77E57FDE83C";
    private const string HtlcBasepointHex = "034F355BDCB7CC0AF728EF3CCEB9615D90684BB5B2CA5F859AB0F0B704075871AA";
    private const string FirstPerCommitmentPointHex =
        "025F7117A78150FE2EF97DB7CFC83BD57B2E2C0D0DD25EAF467A4A1C2A45CE1486";

    // TLV type 0 (upfront_shutdown_script): a 34-byte P2WSH script.
    private const string ShutdownScriptHex = "00200101010101010101010101010101010101010101010101010101010101010101";
    private const string UpfrontShutdownScriptTlvHex = "0022" + ShutdownScriptHex;

    // TLV type 1 (channel_type): single-byte and multi-byte spec channel types (big-endian on the wire).
    private const string SingleByteChannelTypeHex = "10";

    private const string PayloadHex = TemporaryChannelIdHex + DustLimitSatoshisHex + MaxHtlcValueInFlightMsatHex
                                    + ChannelReserveSatoshisHex + HtlcMinimumMsatHex + MinimumDepthHex
                                    + ToSelfDelayHex + MaxAcceptedHtlcsHex + FundingPubkeyHex
                                    + RevocationBasepointHex + PaymentBasepointHex + DelayedPaymentBasepointHex
                                    + HtlcBasepointHex + FirstPerCommitmentPointHex;

    private readonly AcceptChannel1MessageTypeSerializer _serializer =
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
        Assert.Equal(MessageTypes.AcceptChannel, message.Type);
        Assert.Equal(new ChannelId(Convert.FromHexString(TemporaryChannelIdHex)), payload.ChannelId);
        Assert.Equal(LightningMoney.Satoshis(330), payload.DustLimitAmount);
        Assert.Equal(LightningMoney.MilliSatoshis(100_000_000), payload.MaxHtlcValueInFlightAmount);
        Assert.Equal(LightningMoney.Satoshis(2_000), payload.ChannelReserveAmount);
        Assert.Equal(LightningMoney.MilliSatoshis(1), payload.HtlcMinimumAmount);
        Assert.Equal(3U, payload.MinimumDepth);
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
        Assert.NotNull(message.ChannelTypeTlv);
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
    public async Task Given_PayloadWithoutExtension_When_DeserializeAsync_Then_MessageIsDecodedWithoutChannelType()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Null(message.ChannelTypeTlv);
        Assert.Null(message.UpfrontShutdownScriptTlv);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Given_TruncatedPayload_When_DeserializeAsync_Then_Throws()
    {
        // Arrange - drop the last byte of first_per_commitment_point
        var bytes = Convert.FromHexString(PayloadHex);
        var stream = new MemoryStream(bytes[..^1]);

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => _serializer.DeserializeAsync(stream));
    }

    private static string ChannelTypeTlv(string channelTypeHex)
    {
        return "01" + (channelTypeHex.Length / 2).ToString("X2") + channelTypeHex;
    }

    private static AcceptChannel1Message CreateMessage(string channelTypeHex)
    {
        var payload = new AcceptChannel1Payload(Convert.FromHexString(TemporaryChannelIdHex),
                                                LightningMoney.Satoshis(2_000),
                                                Convert.FromHexString(DelayedPaymentBasepointHex),
                                                LightningMoney.Satoshis(330),
                                                Convert.FromHexString(FirstPerCommitmentPointHex),
                                                Convert.FromHexString(FundingPubkeyHex),
                                                Convert.FromHexString(HtlcBasepointHex),
                                                LightningMoney.MilliSatoshis(1), 483,
                                                LightningMoney.MilliSatoshis(100_000_000), 3,
                                                Convert.FromHexString(PaymentBasepointHex),
                                                Convert.FromHexString(RevocationBasepointHex), 144);

        return new AcceptChannel1Message(payload, new ChannelTypeTlv(Convert.FromHexString(channelTypeHex)),
                                         new UpfrontShutdownScriptTlv(Convert.FromHexString(ShutdownScriptHex)));
    }
}