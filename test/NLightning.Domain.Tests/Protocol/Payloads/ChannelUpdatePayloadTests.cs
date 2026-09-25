using System.Security.Cryptography;

namespace NLightning.Domain.Tests.Protocol.Payloads;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Payloads;

public class ChannelUpdatePayloadTests
{
    private static ChannelUpdatePayload CreatePayload(byte[]? extraData = null) =>
        new(ChannelUpdatePayload.EmptySignature, ChainConstants.Regtest, new ShortChannelId(0x0102, 0x0304, 0x0506),
            0x0708090A, ChannelUpdatePayload.MessageFlagMustBeOne, ChannelUpdatePayload.ChannelFlagDirection, 0x0B0C,
            0x0D0E0F1011121314, 0x15161718, 0x191A1B1C, 0x1D1E1F2021222324, extraData);

    [Fact]
    public void Given_Payload_When_GetSignedData_Then_FieldsAreBigEndianInSpecOrder()
    {
        // Arrange
        var payload = CreatePayload();
        var expected = Convert.ToHexString(ChainConstants.Regtest)
                     + "0001020003040506" // short_channel_id
                     + "0708090A" // timestamp
                     + "01" // message_flags
                     + "01" // channel_flags
                     + "0B0C" // cltv_expiry_delta
                     + "0D0E0F1011121314" // htlc_minimum_msat
                     + "15161718" // fee_base_msat
                     + "191A1B1C" // fee_proportional_millionths
                     + "1D1E1F2021222324"; // htlc_maximum_msat

        // Act
        var signedData = payload.GetSignedData();

        // Assert
        Assert.Equal(ChannelUpdatePayload.SignedFieldsLength, signedData.Length);
        Assert.Equal(expected, Convert.ToHexString(signedData));
        Assert.Equal(new byte[64].Concat(signedData).ToArray(), payload.GetBytes());
    }

    [Fact]
    public void Given_PayloadWithExtraData_When_GetSignatureHash_Then_IsDoubleSha256OfSignedDataIncludingExtra()
    {
        // Arrange
        var payload = CreatePayload([0xAA, 0xBB]);
        var signedData = payload.GetSignedData();

        // Act
        var hash = (byte[])payload.GetSignatureHash();

        // Assert
        Assert.Equal([0xAA, 0xBB], signedData[^2..]);
        Assert.Equal(SHA256.HashData(SHA256.HashData(signedData)), hash);
    }

    [Fact]
    public void Given_UnsignedPayload_When_WithSignature_Then_OnlySignatureChanges()
    {
        // Arrange
        var unsigned = CreatePayload([0x01]);
        var signature = Enumerable.Repeat((byte)0x42, 64).ToArray();

        // Act
        var signed = unsigned.WithSignature(signature);

        // Assert
        Assert.Equal(signature, signed.Signature.Value);
        Assert.Equal(unsigned.GetSignedData(), signed.GetSignedData());
        Assert.Equal(new byte[64], unsigned.Signature.Value);
    }

    [Fact]
    public void Given_Bytes_When_Parse_Then_RoundTripsWithFlagsDecoded()
    {
        // Arrange
        var bytes = CreatePayload([0x09]).GetBytes();

        // Act
        var parsed = ChannelUpdatePayload.Parse(bytes);

        // Assert
        Assert.Equal(bytes, parsed.GetBytes());
        Assert.True(parsed.Direction);
        Assert.False(parsed.IsDisabled);
        Assert.False(parsed.DontForward);
        Assert.Equal([0x09], parsed.ExtraData.ToArray());
    }

    [Fact]
    public void Given_TooShortBytes_When_ParseOrTryParse_Then_Fails()
    {
        // Arrange
        var bytes = new byte[ChannelUpdatePayload.MinLength - 1];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => ChannelUpdatePayload.Parse(bytes));
        Assert.False(ChannelUpdatePayload.TryParse(bytes, out var payload));
        Assert.Null(payload);
    }

    [Fact]
    public void Given_63ByteSignature_When_Constructing_Then_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new ChannelUpdatePayload(
                                             new byte[63], ChainConstants.Regtest, new ShortChannelId(1, 1, 1), 1, 1,
                                             0, 40, 1, 1, 1, 1));
    }
}