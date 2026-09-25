namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Models;

public class FailureChannelUpdateFactoryTests
{
    /// <summary>
    /// A 136-byte channel_update payload: signature 0x11.., chain_hash 0x22.., scid 700000x42x1, timestamp 0x5f5e1000,
    /// then zeros.
    /// </summary>
    private static byte[] CreatePayload(byte signatureFirstByte = 0x11, byte signatureSecondByte = 0x11)
    {
        var payload = new byte[FailureChannelUpdateFactory.MinPayloadLength];
        payload.AsSpan(0, 64).Fill(0x11);
        payload[0] = signatureFirstByte;
        payload[1] = signatureSecondByte;
        payload.AsSpan(64, 32).Fill(0x22);
        ((ReadOnlySpan<byte>)new ShortChannelId(700_000, 42, 1)).CopyTo(payload.AsSpan(96));
        payload[104] = 0x5f;
        payload[105] = 0x5e;
        payload[106] = 0x10;
        payload[107] = 0x00;
        return payload;
    }

    [Fact]
    public void Given_Payload_When_Encoding_Then_Type258IsPrefixed()
    {
        // Arrange
        var payload = CreatePayload();

        // Act
        var field = FailureChannelUpdateFactory.Encode(payload);

        // Assert
        Assert.Equal(2 + payload.Length, field.Length);
        Assert.Equal("0102", Convert.ToHexStringLower(field.AsSpan(0, 2)));
        Assert.Equal(payload, field[2..]);
    }

    [Fact]
    public void Given_EmptyPayload_When_Encoding_Then_FieldIsEmpty()
    {
        // Act
        var field = FailureChannelUpdateFactory.Encode(ReadOnlySpan<byte>.Empty);

        // Assert
        Assert.Empty(field);
    }

    [Fact]
    public void Given_ShortPayload_When_Encoding_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => FailureChannelUpdateFactory.Encode(new byte[135]));
    }

    [Fact]
    public void Given_EncodedField_When_EmbeddedInFailure_Then_WireBytesAreLengthPrefixedAndRoundTrip()
    {
        // Arrange
        var payload = CreatePayload();

        // Act
        var message = FailureMessage.TemporaryChannelFailure(FailureChannelUpdateFactory.Encode(payload));
        var found = FailureChannelUpdateFactory.TryGetPayload(message.ChannelUpdate!.Value, out var decoded);

        // Assert: u16 len (138) || 0102 || payload
        Assert.Equal("008a0102", Convert.ToHexStringLower(message.Data.Span[..4]));
        Assert.True(found);
        Assert.Equal(payload, decoded.ToArray());
    }

    [Fact]
    public void Given_FieldWithoutTypePrefix_When_GettingPayload_Then_FieldIsThePayload()
    {
        // Arrange
        var payload = CreatePayload();

        // Act
        var found = FailureChannelUpdateFactory.TryGetPayload(payload, out var decoded);

        // Assert
        Assert.True(found);
        Assert.Equal(payload, decoded.ToArray());
    }

    [Fact]
    public void Given_UnprefixedPayloadWhoseSignatureStartsWith0102_When_GettingPayload_Then_ItIsNotStripped()
    {
        // Arrange: exactly 136 bytes cannot hold a type prefix plus a full payload
        var payload = CreatePayload(0x01, 0x02);

        // Act
        var found = FailureChannelUpdateFactory.TryGetPayload(payload, out var decoded);

        // Assert
        Assert.True(found);
        Assert.Equal(payload, decoded.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(135)]
    public void Given_EmptyOrShortField_When_GettingPayload_Then_ReturnsFalse(int length)
    {
        // Act
        var found = FailureChannelUpdateFactory.TryGetPayload(new byte[length], out var decoded);

        // Assert
        Assert.False(found);
        Assert.True(decoded.IsEmpty);
    }

    [Fact]
    public void Given_Payload_When_ReadingHeader_Then_ShortChannelIdAndTimestampAreRead()
    {
        // Act
        var found = FailureChannelUpdateFactory.TryReadHeader(CreatePayload(), out var shortChannelId,
                                                              out var timestamp);

        // Assert
        Assert.True(found);
        Assert.Equal(new ShortChannelId(700_000, 42, 1), shortChannelId);
        Assert.Equal(0x5f5e1000u, timestamp);
    }

    [Fact]
    public void Given_ShortPayload_When_ReadingHeader_Then_ReturnsFalse()
    {
        // Act / Assert
        Assert.False(FailureChannelUpdateFactory.TryReadHeader(new byte[135], out _, out _));
    }

    [Fact]
    public void Given_AmountBelowMinimumWithUpdate_When_Embedding_Then_UpdateFollowsHtlcMsat()
    {
        // Arrange
        var field = FailureChannelUpdateFactory.Encode(CreatePayload());

        // Act
        var message = FailureMessage.AmountBelowMinimum(LightningMoney.MilliSatoshis(1000UL), field);

        // Assert
        Assert.Equal("00000000000003e8008a", Convert.ToHexStringLower(message.Data.Span[..10]));
        Assert.Equal(field, message.ChannelUpdate!.Value.ToArray());
    }
}