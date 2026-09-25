namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;
using Helpers;
using Serialization.Messages.Types;

public class FundingCreatedMessageTests
{
    // BOLT 2 funding_created (type 34): temporary_channel_id, funding_txid, funding_output_index, signature.
    private const string TemporaryChannelIdHex = "0101010101010101010101010101010101010101010101010101010101010101";

    // BOLT 3 Appendix B funding txid 8984484a...7ef6be, in wire (internal) byte order.
    private const string FundingTxIdHex = "BEF67E4E2FB9DDEEB3461973CD4C62ABB35050B1ADD772995B820B584A488489";
    private const string FundingOutputIndexHex = "0001";

    // BOLT 3 Appendix C "simple commitment tx with no HTLCs" remote signature, as compact r || s.
    private const string SignatureHex = "F51D2E566A70BA740FC5D8C0F07B9B93D2ED741C3C0860C613173DE7D39E7968"
                                      + "41376D520E9C0E1AD52248DDF4B22E12BE8763007DF977253EF45A4CA3BDB7C0";

    private const string MessageHex = TemporaryChannelIdHex + FundingTxIdHex + FundingOutputIndexHex + SignatureHex;

    private readonly FundingCreatedMessageTypeSerializer _serializer =
        new(SerializerHelper.PayloadSerializerFactory);

    [Fact]
    public async Task Given_SpecShapedBytes_When_DeserializeAsync_Then_AllFieldsAreDecoded()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(MessageHex));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Equal(MessageTypes.FundingCreated, message.Type);
        Assert.Equal(new ChannelId(Convert.FromHexString(TemporaryChannelIdHex)), message.Payload.ChannelId);
        Assert.Equal(Convert.FromHexString(FundingTxIdHex), (byte[])message.Payload.FundingTxId);
        Assert.Equal(1, message.Payload.FundingOutputIndex);
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

    private static FundingCreatedMessage CreateMessage()
    {
        return new FundingCreatedMessage(new FundingCreatedPayload(Convert.FromHexString(TemporaryChannelIdHex),
                                                                   new TxId(Convert.FromHexString(FundingTxIdHex)),
                                                                   1,
                                                                   new CompactSignature(
                                                                       Convert.FromHexString(SignatureHex))));
    }
}