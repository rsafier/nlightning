namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Node;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Exceptions;
using Helpers;
using Serialization.Messages.Types;

public class InitMessageTests
{
    private readonly InitMessageTypeSerializer _initMessageTypeSerializer;

    public InitMessageTests()
    {
        _initMessageTypeSerializer =
            new InitMessageTypeSerializer(SerializerHelper.PayloadSerializerFactory,
                                          SerializerHelper.TlvConverterFactory,
                                          SerializerHelper.TlvStreamSerializer);
    }

    [Fact]
    public async Task
        Given_ValidStreamWithPayloadAndExtension_When_DeserializeAsync_Then_ReturnsInitMessageWithCorrectData()
    {
        // Arrange
        var expectedPayload = new InitPayload(new FeatureSet());
        var expectedExtension = new TlvStream();
        var expectedTlv = new NetworksTlv([ChainConstants.Main]);
        expectedExtension.Add(expectedTlv);
        var stream =
            new MemoryStream(
                Convert.FromHexString(
                    "00025101000610000000510101206fe28c0ab6f1b372c1a6a246ae63f74f931e8365e15a089c68d6190000000000"));

        // Act
        var initMessage = await _initMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        BaseTlv? tlv = null;
        Assert.NotNull(initMessage);
        Assert.Equal(expectedPayload.FeatureSet.ToString(), initMessage.Payload.FeatureSet.ToString());
        var hasTlv = initMessage.Extension?.TryGetTlv(TlvConstants.Networks, out tlv);
        Assert.True(hasTlv);
        Assert.Equal(expectedTlv.Value, tlv!.Value);
    }

    [Fact]
    public async Task Given_ValidStreamWithOnlyPayload_When_DeserializeAsync_Then_ReturnsInitMessageWithNullExtension()
    {
        // Arrange
        var expectedPayload = new InitPayload(new FeatureSet());
        var stream = new MemoryStream(Convert.FromHexString("000251010006100000005101"));

        // Act
        var initMessage = await _initMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(initMessage);
        Assert.Equal(expectedPayload.FeatureSet.ToString(), initMessage.Payload.FeatureSet.ToString());
        Assert.Null(initMessage.Extension);
    }

    [Fact]
    public async Task Given_InvalidStreamContent_When_DeserializeAsync_Then_ThrowsMessageSerializationException()
    {
        // Arrange
        var invalidStream = new MemoryStream(Convert.FromHexString("00020200000202000102"));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _initMessageTypeSerializer.DeserializeAsync(
                                                                    invalidStream));
    }

    [Theory]
    [InlineData("00000000")]
    [InlineData("00000000c9012acb0104")]
    public async Task Given_Bolt1AppendixCValidInit_When_DeserializeAsync_Then_ReturnsInitMessage(string hex)
    {
        // Arrange (BOLT 1 Appendix C, without the 0x0010 type prefix)
        var stream = new MemoryStream(Convert.FromHexString(hex));

        // Act
        var initMessage = await _initMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(initMessage);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData("0000000001")]
    [InlineData("00000000ca012a")]
    [InlineData("00000000c90101c90102")]
    public async Task Given_Bolt1AppendixCInvalidInit_When_DeserializeAsync_Then_ThrowsMessageSerializationException(
        string hex)
    {
        // Arrange (BOLT 1 Appendix C, without the 0x0010 type prefix)
        var stream = new MemoryStream(Convert.FromHexString(hex));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _initMessageTypeSerializer.DeserializeAsync(
                                                                    stream));
    }

    [Fact]
    public async Task Given_ValidPayloadAndExtension_When_SerializeAsync_Then_WritesCorrectDataToStream()
    {
        // Arrange
        var message = new InitMessage(new InitPayload(new FeatureSet()), new NetworksTlv([ChainConstants.Main]));
        var stream = new MemoryStream();
        var expectedBytes =
            Convert.FromHexString(
                "00025101000610000000510101206fe28c0ab6f1b372c1a6a246ae63f74f931e8365e15a089c68d6190000000000");

        // Act
        await _initMessageTypeSerializer.SerializeAsync(message, stream);
        stream.Position = 0;
        var result = new byte[stream.Length];
        _ = await stream.ReadAsync(result, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedBytes, result);
    }

    [Fact]
    public async Task Given_ValidPayloadOnly_When_SerializeAsync_Then_WritesCorrectDataToStream()
    {
        // Arrange
        var message = new InitMessage(new InitPayload(new FeatureSet()));
        var stream = new MemoryStream();
        var expectedBytes = Convert.FromHexString("000251010006100000005101");

        // Act
        await _initMessageTypeSerializer.SerializeAsync(message, stream);
        stream.Position = 0;
        var result = new byte[stream.Length];
        _ = await stream.ReadAsync(result, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedBytes, result);
    }

    [Fact]
    public async Task Given_InitWithUndecodableRemoteAddr_When_DeserializeAsync_Then_InitParsesAndKeepsTheRawValue()
    {
        // Arrange: NL-344, remote_addr (TLV 3) holding a truncated Tor v3 descriptor; the TLV is odd and advisory
        var stream = new MemoryStream(Convert.FromHexString("000251010006100000005101" + "030404010203"));

        // Act
        var initMessage = await _initMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.Null(initMessage.RemoteAddressTlv);
        Assert.Equal([4, 1, 2, 3], initMessage.UndecodableRemoteAddress);
    }

    [Fact]
    public async Task Given_InitWithIpv4RemoteAddr_When_DeserializeAsync_Then_TheDescriptorIsParsed()
    {
        // Arrange: 203.0.113.7:9735
        var stream = new MemoryStream(Convert.FromHexString("000251010006100000005101" + "030701cb0071072607"));

        // Act
        var initMessage = await _initMessageTypeSerializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(initMessage.RemoteAddressTlv);
        Assert.Equal("203.0.113.7", initMessage.RemoteAddressTlv.Address);
        Assert.Equal((ushort)9735, initMessage.RemoteAddressTlv.Port);
        Assert.Null(initMessage.UndecodableRemoteAddress);
    }
}