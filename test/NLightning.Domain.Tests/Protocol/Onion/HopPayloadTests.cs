namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

public class HopPayloadTests
{
    private static readonly byte[] s_pathKey =
        Convert.FromHexString("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619");

    [Fact]
    public void Given_AllKnownTlvs_When_CreatingHopPayload_Then_TypedAccessorsAreSet()
    {
        // Arrange
        var secret = new Secret(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var scid = new ShortChannelId(1, 2, 3);

        // Act
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1000)),
                                     new OutgoingCltvValueTlv(144),
                                     new OnionShortChannelIdTlv(scid),
                                     new PaymentDataTlv(secret, LightningMoney.MilliSatoshis(5000)),
                                     new EncryptedRecipientDataTlv([0xaa, 0xbb]),
                                     new CurrentPathKeyTlv(new CompactPubKey(s_pathKey)),
                                     new PaymentMetadataTlv([0x01]),
                                     new TotalAmountMsatTlv(LightningMoney.MilliSatoshis(7000)));

        // Assert
        Assert.Equal(1000UL, payload.AmtToForward!.MilliSatoshi);
        Assert.Equal(144U, payload.OutgoingCltvValue);
        Assert.Equal(scid, payload.ShortChannelId);
        Assert.NotNull(payload.PaymentData);
        Assert.Equal(5000UL, payload.PaymentData.TotalMsat.MilliSatoshi);
        Assert.Equal(new byte[] { 0xaa, 0xbb }, payload.EncryptedRecipientData!.Value.ToArray());
        Assert.Equal(s_pathKey, (byte[])payload.CurrentPathKey!.Value);
        Assert.Equal(new byte[] { 0x01 }, payload.PaymentMetadata!.Value.ToArray());
        Assert.Equal(7000UL, payload.TotalAmountMsat!.MilliSatoshi);
        Assert.True(payload.IsBlinded);
        Assert.Empty(payload.UnknownTlvs);
    }

    [Fact]
    public void Given_NoTlvs_When_CreatingHopPayload_Then_AllAccessorsAreNull()
    {
        // Act
        var payload = new HopPayload();

        // Assert
        Assert.Null(payload.AmtToForward);
        Assert.Null(payload.OutgoingCltvValue);
        Assert.Null(payload.ShortChannelId);
        Assert.Null(payload.PaymentData);
        Assert.Null(payload.EncryptedRecipientData);
        Assert.Null(payload.CurrentPathKey);
        Assert.Null(payload.PaymentMetadata);
        Assert.Null(payload.TotalAmountMsat);
        Assert.False(payload.IsBlinded);
        Assert.Empty(payload.Tlvs);
    }

    [Fact]
    public void Given_UnknownOddTlv_When_CreatingHopPayload_Then_ItIsKeptVerbatim()
    {
        // Arrange
        var unknown = new BaseTlv(new BigSize(513), [0x01, 0x02, 0x03]);

        // Act
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1)), unknown);

        // Assert
        var kept = Assert.Single(payload.UnknownTlvs);
        Assert.Same(unknown, kept);
        Assert.Equal(2, payload.Tlvs.Count());
    }

    [Fact]
    public void Given_KnownTypeAsRawBaseTlv_When_CreatingHopPayload_Then_ThrowsArgumentException()
    {
        // Arrange
        var raw = new BaseTlv(OnionPayloadTlvTypes.AmtToForward, [0x01]);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new HopPayload(raw));
    }

    [Fact]
    public void Given_UnknownTypeAsTypedTlvOfAnotherNamespace_When_CreatingHopPayload_Then_ThrowsArgumentException()
    {
        // Arrange (channel_ready's short_channel_id TLV, type 1)
        var foreign = new ShortChannelIdTlv(new ShortChannelId(1, 2, 3));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new HopPayload(foreign));
    }

    [Fact]
    public void Given_DuplicateType_When_CreatingHopPayload_Then_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new HopPayload(new OutgoingCltvValueTlv(1),
                                                              new OutgoingCltvValueTlv(2)));
    }

    [Fact]
    public void Given_TlvStreamAndOffsets_When_CreatingHopPayload_Then_OffsetsAreReturnedForPresentRecords()
    {
        // Arrange
        var tlvStream = new TlvStream();
        tlvStream.Add(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1)));
        var offsets = new Dictionary<BigSize, int>
        {
            [OnionPayloadTlvTypes.AmtToForward] = 3,
            [OnionPayloadTlvTypes.ShortChannelId] = 9
        };

        // Act
        var payload = new HopPayload(tlvStream, offsets);

        // Assert
        Assert.True(payload.TryGetRecordOffset(OnionPayloadTlvTypes.AmtToForward, out var offset));
        Assert.Equal(3, offset);
        Assert.False(payload.TryGetRecordOffset(OnionPayloadTlvTypes.ShortChannelId, out _));
    }

    [Fact]
    public void Given_SourceTlvStream_When_ModifiedAfterCreation_Then_HopPayloadIsUnaffected()
    {
        // Arrange
        var tlvStream = new TlvStream();
        tlvStream.Add(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1)));
        var payload = new HopPayload(tlvStream);

        // Act
        tlvStream.Add(new OutgoingCltvValueTlv(10));
        payload.ToTlvStream().Add(new OutgoingCltvValueTlv(10));

        // Assert
        Assert.Single(payload.Tlvs);
        Assert.False(payload.TryGetTlv(OnionPayloadTlvTypes.OutgoingCltvValue, out _));
    }

    [Fact]
    public void Given_NegativeOffset_When_CreatingHopPayload_Then_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var tlvStream = new TlvStream();
        var offsets = new Dictionary<BigSize, int> { [OnionPayloadTlvTypes.AmtToForward] = -1 };

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new HopPayload(tlvStream, offsets));
    }
}