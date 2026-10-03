using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Onion;

public class AttributionDataServiceTests
{
    private readonly FailureOnionService _failureOnionService = new(new Mock<IFailureMessageSerializer>().Object);
    private readonly AttributionDataService _service;

    public AttributionDataServiceTests()
    {
        _service = new AttributionDataService(_failureOnionService);
    }

    #region fulfillment_payload_tlvs padding

    [Theory]
    [InlineData(0, 512, 508)] // no record takes exactly 255 or 256 bytes (254 + 2, then 253 + 4), so 512
    [InlineData(9, 256, 245)] // the spec trace: type 65537 = 070809
    [InlineData(253, 256, 1)]
    [InlineData(254, 256, 0)]
    [InlineData(257, 768, 507)] // 255 left in the second block cannot be filled either
    [InlineData(300, 512, 210)]
    public void Given_ContentLength_When_SerializingFulfillmentPayloadTlvs_Then_PaddedToMultipleOf256(
        int contentLength, int expectedTotal, int expectedPadding)
    {
        // Arrange: one odd record whose serialized size is contentLength
        BaseTlv[] records = contentLength == 0 ? [] : [RecordOfSerializedLength(contentLength)];

        // Act
        var serialized = AttributionDataService.SerializeFulfillmentPayloadTlvs(records);

        // Assert
        Assert.Equal(expectedTotal, serialized.Length);
        Assert.True(AttributionDataService.TryParseFulfillmentPayloadTlvs(serialized, out var parsed));
        Assert.Equal(records.Length, parsed.Count);
        var paddingValueLength = serialized.Length - contentLength - 1
                               - (expectedPadding < 0xFD ? 1 : 3);
        Assert.Equal(expectedPadding, paddingValueLength);
    }

    [Fact]
    public void Given_RecordsBelowAndAbovePadding_When_Serializing_Then_TypesAreSorted()
    {
        // Arrange
        var records = new[] { new BaseTlv(7UL, [0x07]), new BaseTlv(0UL, [0x00]) };

        // Act
        var serialized = AttributionDataService.SerializeFulfillmentPayloadTlvs(records);

        // Assert: 00 01 00 | 01 len pad | 07 01 07
        Assert.Equal(256, serialized.Length);
        Assert.Equal([0x00, 0x01, 0x00, 0x01], serialized[..4]);
        Assert.Equal([0x07, 0x01, 0x07], serialized[^3..]);
    }

    [Fact]
    public void Given_PaddingRecord_When_Serializing_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => AttributionDataService.SerializeFulfillmentPayloadTlvs(
                                             [new BaseTlv(FulfillmentPayloadTlvTypes.Padding, [0x00])]));
    }

    [Fact]
    public void Given_DuplicateRecordTypes_When_Serializing_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => AttributionDataService.SerializeFulfillmentPayloadTlvs(
                                             [new BaseTlv(3UL, [0x01]), new BaseTlv(3UL, [0x02])]));
    }

    [Fact]
    public void Given_RecordsTooLargeFor32KiB_When_CreatingFulfillment_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => _service.CreateFulfillment(NewSecret(), 0,
                                                                          [new BaseTlv(3UL, new byte[32760])]));
    }

    [Theory]
    [InlineData("0100", true)] // padding only
    [InlineData("01000301ff", true)] // padding, odd 3
    [InlineData("0201ff", false)] // unknown even type
    [InlineData("0301ff0100", false)] // unordered
    [InlineData("0100 0100", false)] // duplicate
    [InlineData("0105ff", false)] // length past the end
    [InlineData("fd000101ff", false)] // non-canonical type
    public void Given_Stream_When_ParsingFulfillmentPayloadTlvs_Then_StrictRulesApply(string hex, bool valid)
    {
        // Act
        var result = AttributionDataService.TryParseFulfillmentPayloadTlvs(Convert.FromHexString(hex.Replace(" ", "")),
                                                                           out var records);

        // Assert
        Assert.Equal(valid, result);
        if (!valid)
            Assert.Empty(records);
    }

    #endregion

    #region attribution_data edge cases

    [Theory]
    [InlineData(0)]
    [InlineData(919)]
    [InlineData(921)]
    public void Given_DownstreamAttributionOfWrongLength_When_Wrapping_Then_TreatedAsAllZero(int length)
    {
        // Arrange
        var secret = NewSecret();
        var packet = RandomNumberGenerator.GetBytes(292);

        // Act
        var withBadData = _service.WrapErrorPacket(secret, packet, new byte[length], 3);
        var withZeroBlock = _service.WrapErrorPacket(secret, packet, new byte[OnionConstants.AttributionDataLength], 3);

        // Assert
        Assert.Equal(withZeroBlock.AttributionData, withBadData.AttributionData);
        Assert.Equal(withZeroBlock.Reason, withBadData.Reason);
    }

    [Fact]
    public void Given_ReturnPacketOver32KiB_When_Wrapping_Then_TruncatedBeforeBothTransforms()
    {
        // Arrange
        var secret = NewSecret();
        var packet = RandomNumberGenerator.GetBytes(OnionConstants.MaxErrorPacketLength + 10);

        // Act
        var longResult = _service.WrapErrorPacket(secret, packet, ReadOnlySpan<byte>.Empty, 1);
        var truncatedResult = _service.WrapErrorPacket(secret, packet.AsSpan(0, OnionConstants.MaxErrorPacketLength),
                                                       ReadOnlySpan<byte>.Empty, 1);

        // Assert
        Assert.Equal(OnionConstants.MaxErrorPacketLength, longResult.Reason.Length);
        Assert.Equal(truncatedResult.Reason, longResult.Reason);
        Assert.Equal(truncatedResult.AttributionData, longResult.AttributionData);
    }

    [Fact]
    public void Given_RouteLongerThan20Hops_When_Decrypting_Then_AttributionAbsent()
    {
        // Arrange
        var route = Enumerable.Range(0, 21).Select(_ => NewSecret()).ToList();

        // Act
        var result = _service.DecryptErrorPacket(route, RandomNumberGenerator.GetBytes(292),
                                                 new byte[OnionConstants.AttributionDataLength]);

        // Assert
        Assert.Null(result.Failure);
        Assert.False(result.Attribution.IsPresent);
        Assert.Null(result.BlamedHopIndex);
    }

    [Fact]
    public void Given_TwentyHopFulfillment_When_Verifying_Then_EveryHoldTimeRecovered()
    {
        // Arrange: the longest route attribution_data covers
        var route = Enumerable.Range(0, OnionConstants.AttributionMaxHops).Select(_ => NewSecret()).ToList();
        var result = _service.CreateFulfillment(route[^1], 1000, [new BaseTlv(5UL, [0xab, 0xcd])]);
        for (var i = route.Count - 2; i >= 0; i--)
            result = _service.WrapFulfillment(route[i], result.AttributionData, result.FulfillmentPayload!, (uint)i);

        // Act
        var verified = _service.VerifyFulfillment(route, result.AttributionData, result.FulfillmentPayload!);

        // Assert
        Assert.Null(verified.Attribution.InvalidHopIndex);
        Assert.Equal(Enumerable.Range(0, 19).Select(i => (uint)i).Append(1000u), verified.Attribution.HoldTimes);
        Assert.Equal(FulfillmentPayloadStatus.Valid, verified.PayloadStatus);
        var record = Assert.Single(verified.PayloadRecords);
        Assert.Equal(new byte[] { 0xab, 0xcd }, record.Value);
    }

    [Fact]
    public void Given_TwentyHopFailure_When_Decrypting_Then_EveryHoldTimeRecovered()
    {
        // Arrange: the erring node is the last of 20; its HMAC for position 19 is the only one the origin checks
        var route = Enumerable.Range(0, OnionConstants.AttributionMaxHops).Select(_ => NewSecret()).ToList();
        var attribution = AttributionDataService.UpdateAttributionData(route[^1], [0x01], ReadOnlySpan<byte>.Empty,
                                                                       77);
        var packet = _failureOnionService.WrapErrorPacket(route[^1], new byte[] { 0x01 });
        for (var i = route.Count - 2; i >= 0; i--)
        {
            var wrapped = _service.WrapErrorPacket(route[i], packet, attribution, (uint)i);
            packet = wrapped.Reason;
            attribution = wrapped.AttributionData;
        }

        // Act
        var result = _service.DecryptErrorPacket(route, packet, attribution);

        // Assert: the 1-byte packet has no valid legacy HMAC, so every hop is checked
        Assert.Null(result.Failure);
        Assert.Null(result.Attribution.InvalidHopIndex);
        Assert.Equal(Enumerable.Range(0, 19).Select(i => (uint)i).Append(77u), result.Attribution.HoldTimes);
    }

    [Fact]
    public void Given_BlindedHop_When_WrappingOnlyThePayload_Then_OriginStillDecryptsIt()
    {
        // Arrange: hop 1 has a path_key, so it relays the payload and drops attribution_data
        var route = Enumerable.Range(0, 3).Select(_ => NewSecret()).ToList();
        var result = _service.CreateFulfillment(route[2], 1, [new BaseTlv(9UL, [0x09])]);
        var payload = _service.WrapFulfillmentPayload(route[1], result.FulfillmentPayload!);
        result = _service.WrapFulfillment(route[0], ReadOnlySpan<byte>.Empty, payload, 3);

        // Act
        var verified = _service.VerifyFulfillment(route, result.AttributionData, result.FulfillmentPayload!);

        // Assert
        Assert.Equal(1, verified.Attribution.InvalidHopIndex);
        Assert.Equal([3u], verified.Attribution.HoldTimes);
        Assert.Equal(FulfillmentPayloadStatus.Valid, verified.PayloadStatus);
        Assert.Equal(new byte[] { 0x09 }, Assert.Single(verified.PayloadRecords).Value);
    }

    [Fact]
    public void Given_PayloadWithoutAttribution_When_Verifying_Then_PayloadDecryptedAndAttributionAbsent()
    {
        // Arrange
        var route = Enumerable.Range(0, 2).Select(_ => NewSecret()).ToList();
        var created = _service.CreateFulfillment(route[1], 1, []);
        var payload = _service.WrapFulfillmentPayload(route[0], created.FulfillmentPayload!);

        // Act
        var verified = _service.VerifyFulfillment(route, ReadOnlySpan<byte>.Empty, payload);

        // Assert
        Assert.False(verified.Attribution.IsPresent);
        Assert.Equal(FulfillmentPayloadStatus.Valid, verified.PayloadStatus);
    }

    [Fact]
    public void Given_PayloadEncryptedForAnotherHop_When_Verifying_Then_PayloadInvalid()
    {
        // Arrange: the final node used a key the origin does not expect
        var route = Enumerable.Range(0, 2).Select(_ => NewSecret()).ToList();
        var created = _service.CreateFulfillment(NewSecret(), 1, []);
        var payload = _service.WrapFulfillmentPayload(route[0], created.FulfillmentPayload!);

        // Act
        var verified = _service.VerifyFulfillment(route, ReadOnlySpan<byte>.Empty, payload);

        // Assert
        Assert.Equal(FulfillmentPayloadStatus.Invalid, verified.PayloadStatus);
        Assert.Empty(verified.PayloadRecords);
    }

    [Fact]
    public void Given_PayloadOver32KiB_When_Wrapping_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => _service.WrapFulfillment(NewSecret(), ReadOnlySpan<byte>.Empty,
                                                                        new byte[32769], 1));
        Assert.Throws<ArgumentException>(() => _service.WrapFulfillmentPayload(NewSecret(), new byte[32769]));
    }

    [Fact]
    public void Given_EmptyRoute_When_VerifyingFulfillment_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => _service.VerifyFulfillment([], ReadOnlySpan<byte>.Empty,
                                                                          ReadOnlySpan<byte>.Empty));
    }

    #endregion

    private static Secret NewSecret() => new(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// An odd-typed (3) record whose serialized size (type + length + value) is exactly <paramref name="length"/>.
    /// </summary>
    private static BaseTlv RecordOfSerializedLength(int length)
    {
        var valueLength = length - 2;
        if (valueLength >= 0xFD)
            valueLength = length - 4;

        return new BaseTlv(3UL, new byte[valueLength]);
    }
}