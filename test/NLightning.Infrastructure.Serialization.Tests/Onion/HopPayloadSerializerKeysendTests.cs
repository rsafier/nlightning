namespace NLightning.Infrastructure.Serialization.Tests.Onion;

using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Helpers;
using Serialization.Onion;

/// <summary>
/// Keysend and custom records in the hop payload (lane lh1-l3): types of 65536 and more are kept whatever their parity
/// (LND's "we always accept custom fields"), below that an unknown even type still fails.
/// </summary>
public class HopPayloadSerializerKeysendTests
{
    // amt_to_forward 1000, outgoing_cltv_value 500, 65536 = abcd (even), 7629169 = "podcast" (odd),
    // keysend_preimage 5482373484 = 01..20; encoded by hand (bigsize types fe/ff)
    private const string KeysendPayload =
        "020203e8040201f4fe0001000002abcdfe0074697107706f6463617374ff0000000146c6616c20"
      + "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

    private readonly HopPayloadSerializer _serializer = new(SerializerHelper.TlvSerializer,
                                                            SerializerHelper.TlvStreamSerializer,
                                                            SerializerHelper.ValueObjectSerializerFactory);

    [Fact]
    public async Task Given_KeysendPayloadWithEvenCustomRecord_When_Deserializing_Then_PreimageAndRecordsAreRead()
    {
        // Act
        var payload = await _serializer.DeserializeAsync(Convert.FromHexString(KeysendPayload));

        // Assert
        Assert.Equal(LightningMoney.MilliSatoshis(1000), payload.AmtToForward);
        Assert.Equal(500u, payload.OutgoingCltvValue);
        Assert.Null(payload.PaymentData);
        Assert.Equal(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), payload.KeysendPreimage!.Value.ToArray());
        Assert.Collection(payload.CustomRecords,
                          r => Assert.Equal((65536UL, "abcd"), (r.Type, Convert.ToHexStringLower(r.Value.Span))),
                          r => Assert.Equal((7629169UL, "706f6463617374"),
                                            (r.Type, Convert.ToHexStringLower(r.Value.Span))));
    }

    [Fact]
    public async Task Given_KeysendPayload_When_RoundTripping_Then_BytesAreIdentical()
    {
        // Arrange
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1000)),
                                     new OutgoingCltvValueTlv(500),
                                     new BaseTlv(OnionPayloadTlvTypes.KeysendPreimage,
                                                 Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
                                     new BaseTlv(new BigSize(7629169), "podcast"u8.ToArray()),
                                     new BaseTlv(OnionPayloadTlvTypes.CustomRecordTypeStart, [0xab, 0xcd]));
        using var stream = new MemoryStream();

        // Act
        await _serializer.SerializeAsync(payload, stream);
        var read = await _serializer.DeserializeAsync(stream.ToArray());

        // Assert
        Assert.Equal(KeysendPayload, Convert.ToHexStringLower(stream.ToArray()));
        Assert.Equal(payload.Tlvs, read.Tlvs);
    }

    [Fact]
    public async Task Given_UnknownEvenTypeBelowCustomRange_When_Deserializing_Then_InvalidOnionPayload()
    {
        // Arrange: 65534, the last even type below the custom range, after a 3-byte amt_to_forward (offsets count
        // the 1-byte length prefix)
        var bytes = Convert.FromHexString("020101fdfffe0100");

        // Act
        var exception = await Assert.ThrowsAsync<OnionException>(() => _serializer.DeserializeAsync(bytes));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
        Assert.True(InvalidOnionPayloadFailureFactory.TryDecodeData(exception.FailureData!.Value.Span, out var type,
                                                                    out var offset));
        Assert.Equal((65534UL, (ushort)4), (type.Value, offset));
    }
}