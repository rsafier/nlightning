using NLightning.Domain.Protocol.ValueObjects;

namespace NLightning.Domain.Tests.Protocol.Models;

using Domain.Protocol.Models;
using Domain.Protocol.Tlv;

public class TlvStreamTests
{
    [Fact]
    public void Given_ValidTlv_When_AddedToStream_Then_ExistsInStream()
    {
        // Given
        var tlvStream = new TlvStream();
        var tlv = new BaseTlv(new BigSize(1), [0x01, 0x02]);

        // When
        tlvStream.Add(tlv);

        // Then
        Assert.True(tlvStream.TryGetTlv(tlv.Type, out var retrievedTlv));
        Assert.Equal(tlv, retrievedTlv);
    }

    [Fact]
    public void Given_DuplicateTlv_When_AddedToStream_Then_ThrowsArgumentException()
    {
        // Given
        var tlvStream = new TlvStream();
        var tlv = new BaseTlv(new BigSize(1), [0x01, 0x02]);

        // When
        tlvStream.Add(tlv);

        // Then
        Assert.Throws<ArgumentException>(() => tlvStream.Add(tlv));
    }

    [Fact]
    public void Given_MultipleTlvs_When_AddedToStream_Then_AllExistInStream()
    {
        // Given
        var tlvStream = new TlvStream();
        var tlv1 = new BaseTlv(new BigSize(1), [0x01]);
        var tlv2 = new BaseTlv(new BigSize(2), [0x02]);

        // When
        tlvStream.Add(tlv1, tlv2);

        // Then
        Assert.True(tlvStream.TryGetTlv(tlv1.Type, out var retrievedTlv1));
        Assert.True(tlvStream.TryGetTlv(tlv2.Type, out var retrievedTlv2));
        Assert.Equal(tlv1, retrievedTlv1);
        Assert.Equal(tlv2, retrievedTlv2);
    }

    [Fact]
    public void Given_TlvsAddedOutOfTypeOrder_When_Enumerated_Then_InsertionOrderIsPreserved()
    {
        // Given
        var tlvStream = new TlvStream();
        var tlv2 = new BaseTlv(new BigSize(2), [0x02]);
        var tlv1 = new BaseTlv(new BigSize(1), [0x01]);

        // When
        tlvStream.Add(tlv2);
        tlvStream.Add(tlv1);

        // Then: the stream keeps insertion order instead of silently re-sorting (NL-013)
        Assert.Equal([tlv2, tlv1], tlvStream.GetTlvs().ToList());
    }

    [Fact]
    public void Given_MultipleNullTlvs_When_AddedToStream_Then_NullsAreSkipped()
    {
        // Given
        var tlvStream = new TlvStream();
        var tlv = new BaseTlv(new BigSize(1), [0x01]);

        // When
        tlvStream.Add(null, tlv, null);

        // Then
        Assert.True(tlvStream.Any());
        Assert.True(tlvStream.TryGetTlv(tlv.Type, out _));
    }
}