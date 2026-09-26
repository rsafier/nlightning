namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Exceptions;
using Helpers;
using Serialization.Messages.Types;

public class UpdateAddHtlcMessageTests
{
    private const string PayloadHeaderHex =
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001567CBDADB00B825448B2E414487D73A97F657F0634166D3AB3F3A2CC1042EDA500000003";

    private const string OnionHex =
        "567CBDADB00B825448B2E414487D73A97F657F0634166D3AB3F3A2CC1042EDA518885F08987B365412FDFFA239917499B5B45557C6312852B36C62B5BD0C3F6837BCD5F6757B564CC44090EE1C156621EF432E9F0FFACB77DFCA219D514312AE02F6C0D865CEA05074183C6C6300C8FFBB3FDACC9E01847D32567E9D189AB01AA4A66F4F12BA54202F10D11604B00CDA31C259D24A14B8816940D3B6CE20B955687EE834F07E35CBFFADBE725588F1D64985FF329A1860AFB0CBE1C81B4028209A6FCCF0C44F18A0D1E7D2E10B800AEE3DDFCAE4395CB363A840F8958EC860D51903A89D60FFABA46256D68A15920544CA989469E18E6DC252C2EDBD153292AF3DBE51C8D9064360588DC7316A8E682D56EA99F5EF6DA6E8F566E715A3320396B556CC0BFAD6AD22F25635A893CD493734C7667834005FF5AD2A437A0EADA662823382CEC1164661E691845A81EA063DC6DBD84C30BCFB8DE272A14BC46601C1C0D4216D96685F6272D60313B3034AEC74B11AD0639B885F22DE2A476A989C7198F1AA556F50ABD3D8D60B9DA0AE2A31BD7E744723B33A44A68B19B60BAD95C90D79DB77FADF8852AFFF7CB44882BA7B06741FD68BDD25F27C120C9B42FFA544CCA0A4350CB1FE43030A8BBD06B584114C4E8101386C0603E90F657C8E105DA42BF8BB3C9DBAE55E008FF25CA141F630F6B230E863C61A4D9AFFACFECD2580ABD42087D26DE8A6371AF07F6F532D482A7026E4CC1F2295BE284E5018655FCB7AE272B33A0F209F80B1A77EB8102AEB98EB85B77047E8A6DDFCD48B31175F4177CCA1D3B0942FEA2FC6D8F71492F3A260ABB7EEBEDAC4CEF600B6D65A156DF8E194B86461B752CD6F289FEED9AB53D8A977BEC9DAB73774C7BB7C60E02F728A61598DF8FEDBC851CDD4E6B97641FB3D11A15320957D9E2FC2732C210D463B880FB1A5B5CA18E6AD456B8ACD1D9DFE43824C0B12A925EFEA55BF74380CA6A4DFBB267A5C61299EEF9663FFAF40D2A8C078BE6D95BF5CD3D8FDDF3AC76A76B55AC6D3EF964EE5B7E1434CADD1A66C33B4B75BABE907FCC3FE97EAD7D6D7FAA05B9184C2E54AA8F8366E6FDC6D9D0FCA8DAFB5ADF3F31505B15C89A12063CD7492F392CA575404FF9C93C7935A9F1C5D28B88E63D0DF7DB36DFB2C498F8FF665B2BC01718EEBFA47ADBF34AAC7C34834824AB3B101ACA7A09E3210C4ACA6AB0B7D214CF7E69D992EC0231EF1D2AFC19B09F035BACA8F04FCDC6ADAAB392732AC1C223EF3AF0D6020E3D41A1C7766590EE88E04B8161C5AE21F7E94F7DC3A2085D4FB54D2ECD1772DB2F6CAC66354D7E522E99574B0E952E3F4EE06B4D5047F2D2149AB03DDE085DACA6771044A15C6D956096D9C2DD5CDA17E230195C676CCE9ECE97A83957F2520D844350D1C72766E80B421E6D9FBEF30F8F8223AC5B1E7B9E49F0083B90FD42AF4B0CD60633DE954A04101BBA9A87F3B7BFF60B3D5806828FDE024437B7B7C97DB35B93F8CFC98B19A15E92A65EC7C918C913C1E02593B080D8700EC65948D954B49D1D9099E9BCF50EA201E8D09B1F92245BCABCC55226EC8599E4530FE8D3C645C88CDD1DD483086341C89D36B94DB6964A6ACF029DD200565EB7EED8570168C7E3E8FE2E49DD1E706D38E3ECD3693D41B279A1ED6E090BE23BF359DB63A5B2ACEF5432B482AF1F5AD2C38E288A0AE5FA49093789509E7AA167C53C6E6FAA60E009EDF15B8A0E5263822CBCC32177EE28D6320EAD4C60D30B67E75FAE676FC9F38A784DB7612104A20AABA108457DBB09E403F120B1019D23B7C3B095F50007C388F74EEC12E7CB45CC45769939A88026F2F3BDD29F47DD480B54AA6606B42FDC191D69D107E0C94F39F5F760534B448B006D7E07FAF92D70D2CDBFAFE799A76E4F19A73F00BB0D06B50CF09955659";

    private const string BlindedPathTlvHex = "002102C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A75";

    private static readonly byte[] s_paymentHash =
        Convert.FromHexString("567cbdadb00b825448b2e414487d73a97f657f0634166d3ab3f3a2cc1042eda5");

    private readonly UpdateAddHtlcMessageTypeSerializer _updateAddHtlcMessageTypeSerializer;

    public UpdateAddHtlcMessageTests()
    {
        _updateAddHtlcMessageTypeSerializer =
            new UpdateAddHtlcMessageTypeSerializer(SerializerHelper.PayloadSerializerFactory,
                                                   SerializerHelper.TlvConverterFactory,
                                                   SerializerHelper.TlvStreamSerializer);
    }

    #region Deserialize

    [Fact]
    public async Task Given_ValidStream_When_DeserializeAsync_Then_ReturnsUpdateAddHtlcMessageWithRoutingPacket()
    {
        // Arrange
        var expectedChannelId = ChannelId.Zero;
        const ulong expectedId = 0UL;
        var expectedAmountMsat = LightningMoney.MilliSatoshis(1);
        const uint expectedCltvExpiry = 3u;
        var expectedOnionRoutingPacket = Convert.FromHexString(OnionHex);
        var stream = new MemoryStream(Convert.FromHexString(PayloadHeaderHex + OnionHex));

        // Act
        var message = await _updateAddHtlcMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(expectedChannelId, message.Payload.ChannelId);
        Assert.Equal(expectedId, message.Payload.Id);
        Assert.Equal(expectedAmountMsat, message.Payload.Amount);
        Assert.Equal(s_paymentHash, message.Payload.PaymentHash);
        Assert.Equal(expectedCltvExpiry, message.Payload.CltvExpiry);
        Assert.Equal(1366, message.Payload.OnionRoutingPacket.Length);
        Assert.Equal(expectedOnionRoutingPacket, message.Payload.OnionRoutingPacket.ToArray());
        Assert.Null(message.Extension);
        Assert.Null(message.BlindedPathTlv);
    }

    [Fact]
    public async Task Given_StreamWithBlindedPathTlv_When_DeserializeAsync_Then_ReturnsMessageWithBlindedPath()
    {
        // Arrange
        var expectedPathKey =
            Convert.FromHexString("02c93ca7dca44d2e45e3cc5419d92750f7fb3a0f180852b73a621f4051c0193a75");
        var expectedOnionRoutingPacket = Convert.FromHexString(OnionHex);
        var stream = new MemoryStream(Convert.FromHexString(PayloadHeaderHex + OnionHex + BlindedPathTlvHex));

        // Act
        var message = await _updateAddHtlcMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(s_paymentHash, message.Payload.PaymentHash);
        Assert.Equal(expectedOnionRoutingPacket, message.Payload.OnionRoutingPacket.ToArray());
        Assert.NotNull(message.Extension);
        Assert.NotNull(message.BlindedPathTlv);
        Assert.Equal(expectedPathKey, message.BlindedPathTlv.PathKey);
    }

    [Fact]
    public async Task Given_StreamWithoutOnionPacket_When_DeserializeAsync_Then_ThrowsPayloadSerializationException()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHeaderHex));

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(() => _updateAddHtlcMessageTypeSerializer
                                                                   .DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_StreamWithTruncatedOnionPacket_When_DeserializeAsync_Then_ThrowsPayloadSerializationException()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHeaderHex + OnionHex[..^2]));

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(() => _updateAddHtlcMessageTypeSerializer
                                                                   .DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_InvalidStreamContent_When_DeserializeAsync_Then_ThrowsMessageSerializationException()
    {
        // Arrange
        var invalidStream = new MemoryStream(Convert.FromHexString(PayloadHeaderHex + OnionHex + "0021"));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _updateAddHtlcMessageTypeSerializer
                                                                   .DeserializeAsync(invalidStream));
    }

    [Fact]
    public async Task Given_UnknownEvenExtensionTlv_When_DeserializeAsync_Then_ThrowsMessageSerializationException()
    {
        // Arrange: BOLT 1 - an unknown even type MUST fail to parse the tlv_stream.
        var stream = new MemoryStream(Convert.FromHexString(PayloadHeaderHex + OnionHex + "0200"));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _updateAddHtlcMessageTypeSerializer
                                                                   .DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_UnknownOddExtensionTlv_When_DeserializeAsync_Then_IgnoresIt()
    {
        // Arrange: BOLT 1 - an unknown odd type MUST be ignored.
        var stream = new MemoryStream(Convert.FromHexString(PayloadHeaderHex + OnionHex + "0300"));

        // Act
        var message = await _updateAddHtlcMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Null(message.BlindedPathTlv);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData("000102")]
    [InlineData("002002C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A")]
    [InlineData("002104C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A75")]
    public async Task Given_MalformedBlindedPathTlv_When_DeserializeAsync_Then_ThrowsMessageSerializationException(
        string tlvHex)
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHeaderHex + OnionHex + tlvHex));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _updateAddHtlcMessageTypeSerializer
                                                                   .DeserializeAsync(stream));
    }

    #endregion

    #region Serialize

    [Fact]
    public async Task Given_ValidPayloadWithOnionPacket_When_SerializeAsync_Then_WritesCorrectDataToStream()
    {
        // Arrange
        var channelId = ChannelId.Zero;
        const ulong id = 0UL;
        var amountMsat = LightningMoney.MilliSatoshis(1);
        const uint cltvExpiry = 3u;
        var onionRoutingPacket = Convert.FromHexString(OnionHex);
        var message =
            new UpdateAddHtlcMessage(
                new UpdateAddHtlcPayload(amountMsat, channelId, cltvExpiry, id, s_paymentHash, onionRoutingPacket));
        var stream = new MemoryStream();
        var expectedBytes = Convert.FromHexString(PayloadHeaderHex + OnionHex);

        // Act
        await _updateAddHtlcMessageTypeSerializer.SerializeAsync(message, stream);
        stream.Position = 0;
        var result = new byte[stream.Length];
        _ = await stream.ReadAsync(result, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedBytes, result);
    }

    [Fact]
    public async Task Given_ValidPayloadAndExtensions_When_SerializeAsync_Then_WritesCorrectDataToStream()
    {
        // Arrange
        var channelId = ChannelId.Zero;
        const ulong id = 0UL;
        var amountMsat = LightningMoney.MilliSatoshis(1);
        const uint cltvExpiry = 3u;
        var pathKey = Convert.FromHexString("02c93ca7dca44d2e45e3cc5419d92750f7fb3a0f180852b73a621f4051c0193a75");
        var blindedPathTlv = new BlindedPathTlv(pathKey);
        var onionRoutingPacket = Convert.FromHexString(OnionHex);
        var message =
            new UpdateAddHtlcMessage(
                new UpdateAddHtlcPayload(amountMsat, channelId, cltvExpiry, id, s_paymentHash, onionRoutingPacket),
                blindedPathTlv);
        var stream = new MemoryStream();
        var expectedBytes = Convert.FromHexString(PayloadHeaderHex + OnionHex + BlindedPathTlvHex);

        // Act
        await _updateAddHtlcMessageTypeSerializer.SerializeAsync(message, stream);
        stream.Position = 0;
        var result = new byte[stream.Length];
        _ = await stream.ReadAsync(result, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedBytes, result);
    }

    [Fact]
    public async Task Given_MessageRoundTrip_When_SerializeThenDeserialize_Then_OnionPacketIsPreserved()
    {
        // Arrange
        var onionRoutingPacket = Convert.FromHexString(OnionHex);
        var message =
            new UpdateAddHtlcMessage(
                new UpdateAddHtlcPayload(LightningMoney.MilliSatoshis(1), ChannelId.Zero, 3u, 0UL, s_paymentHash,
                                         onionRoutingPacket));
        var stream = new MemoryStream();

        // Act
        await _updateAddHtlcMessageTypeSerializer.SerializeAsync(message, stream);
        stream.Position = 0;
        var result = await _updateAddHtlcMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(onionRoutingPacket, result.Payload.OnionRoutingPacket.ToArray());
        Assert.Equal(stream.Length, stream.Position);
    }

    #endregion

    #region Payload validation

    [Theory]
    [InlineData(0)]
    [InlineData(1365)]
    [InlineData(1367)]
    public void Given_OnionPacketOfWrongLength_When_CreatingPayload_Then_ThrowsArgumentException(int length)
    {
        // Arrange
        var onionRoutingPacket = new byte[length];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new UpdateAddHtlcPayload(LightningMoney.MilliSatoshis(1),
                                                                         ChannelId.Zero, 3u, 0UL, s_paymentHash,
                                                                         onionRoutingPacket));
    }

    #endregion
}