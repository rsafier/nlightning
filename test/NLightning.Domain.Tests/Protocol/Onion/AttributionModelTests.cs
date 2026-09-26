namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;

public class AttributionModelTests
{
    [Theory]
    [InlineData(0, 0u)]
    [InlineData(-5, 0u)]
    [InlineData(99, 0u)]
    [InlineData(100, 1u)]
    [InlineData(350, 3u)]
    [InlineData(60_000, 600u)]
    public void Given_HeldDuration_When_ConvertingToHoldTime_Then_RoundedDownTo100MsUnits(int milliseconds,
                                                                                          uint expected)
    {
        // Act
        var holdTime = AttributionHoldTime.FromDuration(TimeSpan.FromMilliseconds(milliseconds));

        // Assert
        Assert.Equal(expected, holdTime);
    }

    [Fact]
    public void Given_HugeDuration_When_ConvertingToHoldTime_Then_Saturates()
    {
        // Act / Assert
        Assert.Equal(uint.MaxValue, AttributionHoldTime.FromDuration(TimeSpan.FromDays(10_000)));
    }

    [Fact]
    public void Given_HoldTime_When_ConvertingToDuration_Then_Multiplied()
    {
        // Act / Assert
        Assert.Equal(TimeSpan.FromMilliseconds(300), AttributionHoldTime.ToDuration(3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(919)]
    [InlineData(921)]
    public void Given_WrongLength_When_CreatingAttributionDataTlv_Then_Throws(int length)
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => new AttributionDataTlv(new byte[length]));
    }

    [Fact]
    public void Given_920Bytes_When_CreatingAttributionDataTlv_Then_TypeOneAndValueKept()
    {
        // Arrange
        var value = new byte[OnionConstants.AttributionDataLength];
        value[0] = 0x42;

        // Act
        var tlv = new AttributionDataTlv(value);

        // Assert
        Assert.Equal(1UL, tlv.Type.Value);
        Assert.Equal(920UL, tlv.Length.Value);
        Assert.Same(value, tlv.AttributionData);
    }

    [Theory]
    [InlineData(32768, false)]
    [InlineData(32769, true)]
    public void Given_PayloadLength_When_CreatingFulfillmentPayloadTlv_Then_IsTooLongFollowsBolt2Limit(int length,
        bool tooLong)
    {
        // Act
        var tlv = new FulfillmentPayloadTlv(new byte[length]);

        // Assert
        Assert.Equal(3UL, tlv.Type.Value);
        Assert.Equal(tooLong, tlv.IsTooLong);
    }

    [Fact]
    public void Given_NoTlvs_When_CreatingFailAndFulfillMessages_Then_NoExtension()
    {
        // Act
        var fail = new UpdateFailHtlcMessage(new UpdateFailHtlcPayload(ChannelId.Zero, 1, new byte[4]));
        var fulfill = new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(ChannelId.Zero, 1, new byte[32]));

        // Assert
        Assert.Null(fail.Extension);
        Assert.Null(fail.AttributionDataTlv);
        Assert.Null(fulfill.Extension);
        Assert.Null(fulfill.AttributionDataTlv);
        Assert.Null(fulfill.FulfillmentPayloadTlv);
    }

    [Fact]
    public void Given_Tlvs_When_CreatingFailAndFulfillMessages_Then_ExtensionCarriesThem()
    {
        // Arrange
        var attribution = new AttributionDataTlv(new byte[OnionConstants.AttributionDataLength]);
        var payload = new FulfillmentPayloadTlv([0x01, 0x02]);

        // Act
        var fail = new UpdateFailHtlcMessage(new UpdateFailHtlcPayload(ChannelId.Zero, 1, new byte[4]), attribution);
        var fulfill = new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(ChannelId.Zero, 1, new byte[32]),
                                                   null, payload);

        // Assert
        Assert.NotNull(fail.Extension);
        Assert.True(fail.Extension.TryGetTlv(1, out _));
        Assert.NotNull(fulfill.Extension);
        Assert.False(fulfill.Extension.TryGetTlv(1, out _));
        Assert.True(fulfill.Extension.TryGetTlv(3, out _));
    }

    [Fact]
    public void Given_NoLegacyFailure_When_AttributionFailedAtHop_Then_ThatHopIsBlamed()
    {
        // Arrange
        var verification = new AttributionVerification(true, [3u, 4u], 2);

        // Act
        var failure = new AttributedFailure(null, verification);

        // Assert
        Assert.Equal(2, failure.BlamedHopIndex);
        Assert.Equal(2, verification.VerifiedHopCount);
        Assert.Equal(TimeSpan.FromMilliseconds(400), verification.GetHoldTime(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => verification.GetHoldTime(2));
    }

    [Fact]
    public void Given_LegacyFailure_When_Attributed_Then_ErringHopWinsOverAttribution()
    {
        // Act
        var failure = new AttributedFailure(new DecryptedFailure(1, ReadOnlyMemory<byte>.Empty, null),
                                            new AttributionVerification(true, [], 0));

        // Assert
        Assert.Equal(1, failure.BlamedHopIndex);
    }

    [Fact]
    public void Given_NothingIdentified_When_Attributed_Then_NobodyBlamed()
    {
        // Act
        var failure = new AttributedFailure(null, AttributionVerification.Absent);

        // Assert
        Assert.Null(failure.BlamedHopIndex);
        Assert.False(failure.Attribution.IsPresent);
    }

    [Fact]
    public void Given_WrongLengthAttribution_When_CreatingResults_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => new AttributedErrorPacket([0x01], new byte[10]));
        Assert.Throws<ArgumentException>(() => new AttributedFulfillment(new byte[10], null));
    }
}