namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;
using Helpers;
using Serialization.Messages.Types;

public class FundingSignedMessageTests
{
    // BOLT 2 funding_signed (type 35): channel_id, signature.
    // channel_id = BOLT 3 Appendix B funding txid (wire byte order) XOR funding_output_index 1.
    private const string ChannelIdHex = "BEF67E4E2FB9DDEEB3461973CD4C62ABB35050B1ADD772995B820B584A488488";

    // BOLT 3 Appendix C "simple commitment tx with no HTLCs" local signature, as compact r || s.
    private const string SignatureHex = "51B75C73198C6DEEE1A875871C3961832909ACD297C6B908D59E3319E5185A46"
                                      + "55C419379C5051A78D00DBBCE11B5B664A0C22815FBCC6FCEF6B1937C3836939";

    private const string MessageHex = ChannelIdHex + SignatureHex;

    private readonly FundingSignedMessageTypeSerializer _serializer = new(SerializerHelper.PayloadSerializerFactory);

    [Fact]
    public async Task Given_SpecShapedBytes_When_DeserializeAsync_Then_AllFieldsAreDecoded()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(MessageHex));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(MessageTypes.FundingSigned, message.Type);
        Assert.Equal(new ChannelId(Convert.FromHexString(ChannelIdHex)), message.Payload.ChannelId);
        Assert.Equal(Convert.FromHexString(SignatureHex), message.Payload.Signature.Value);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Given_Message_When_SerializeAsync_Then_WritesSpecShapedBytes()
    {
        // Arrange
        var message = CreateMessage();
        var stream = new MemoryStream();

        // Act
        await _serializer.SerializeAsync(message, stream);

        // Assert
        Assert.Equal(Convert.FromHexString(MessageHex), stream.ToArray());
    }

    [Fact]
    public async Task Given_Message_When_RoundTripped_Then_BytesAreStable()
    {
        // Arrange
        var first = new MemoryStream();
        await _serializer.SerializeAsync(CreateMessage(), first);
        first.Position = 0;

        // Act
        var decoded = await _serializer.DeserializeAsync(first);
        var second = new MemoryStream();
        await _serializer.SerializeAsync(decoded, second);

        // Assert
        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public async Task Given_TruncatedSignature_When_DeserializeAsync_Then_ThrowsPayloadSerializationException()
    {
        // Arrange
        var bytes = Convert.FromHexString(MessageHex);
        var stream = new MemoryStream(bytes[..^1]);

        // Act & Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(() => _serializer.DeserializeAsync(stream));
    }

    private static FundingSignedMessage CreateMessage()
    {
        return new FundingSignedMessage(new FundingSignedPayload(Convert.FromHexString(ChannelIdHex),
                                                                 new CompactSignature(
                                                                     Convert.FromHexString(SignatureHex))));
    }
}