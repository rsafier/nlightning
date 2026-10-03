using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;
using Factories;
using Helpers;
using Serialization.Messages;
using Serialization.Messages.Types;

/// <summary><c>closing_sig</c> (41) on the wire (BOLT 2 <c>option_simple_close</c>, B2-SC-W01).</summary>
public class ClosingSigMessageTests
{
    /// <summary>The fields of <see cref="ClosingCompleteMessageTests"/>, then closee_output_only (2).</summary>
    private const string ClosingSigHex = ClosingCompleteMessageTests.FixedHex + "0240"
                                                                              + ClosingCompleteMessageTests.Sig1Hex;

    private readonly ClosingSigMessageTypeSerializer _serializer =
        new(SerializerHelper.PayloadSerializerFactory, SerializerHelper.TlvStreamSerializer);

    private readonly MessageSerializer _messageSerializer =
        new(NullLogger<MessageSerializer>.Instance,
            new MessageTypeSerializerFactory(SerializerHelper.PayloadSerializerFactory,
                                             SerializerHelper.TlvConverterFactory,
                                             SerializerHelper.TlvStreamSerializer));

    [Fact]
    public async Task Given_ClosingSig_When_SerializeAsync_Then_BoltLayout()
    {
        // Arrange
        var complete = ClosingCompleteMessageTests.Payload();
        var message = new ClosingSigMessage(
            new ClosingSigPayload(complete.ChannelId, complete.CloserScriptPubKey, complete.CloseeScriptPubKey,
                                  complete.FeeSatoshis, complete.LockTime),
            ClosingSignatures.Single(ClosingSigKind.CloseeOutputOnly,
                                     new CompactSignature(Convert.FromHexString(ClosingCompleteMessageTests.Sig1Hex))));
        using var stream = new MemoryStream();

        // Act
        await _serializer.SerializeAsync(message, stream);

        // Assert
        Assert.Equal(ClosingSigHex, Convert.ToHexString(stream.ToArray()));
    }

    [Fact]
    public async Task Given_WireMessage_When_RoundTripThroughMessageSerializer_Then_SameBytesAndFields()
    {
        // Arrange
        var wire = Convert.FromHexString("0029" + ClosingSigHex);
        using var input = new MemoryStream(wire);

        // Act
        var message = Assert.IsType<ClosingSigMessage>(await _messageSerializer.DeserializeMessageAsync(input));
        using var output = new MemoryStream();
        await _messageSerializer.SerializeAsync(message, output);

        // Assert
        Assert.Equal(MessageTypes.ClosingSig, message.Type);
        Assert.Equal(1_000L, message.Payload.FeeSatoshis.Satoshi);
        Assert.Equal(500U, message.Payload.LockTime);
        Assert.Equal([ClosingSigKind.CloseeOutputOnly], message.Signatures.Kinds);
        Assert.Equal(wire, output.ToArray());
    }

    [Fact]
    public async Task Given_UnknownEvenTlv_When_DeserializeAsync_Then_Throws()
    {
        // Arrange
        using var stream = new MemoryStream(Convert.FromHexString(ClosingCompleteMessageTests.FixedHex + "0601FF"));

        // Act / Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _serializer.DeserializeAsync(stream));
    }
}