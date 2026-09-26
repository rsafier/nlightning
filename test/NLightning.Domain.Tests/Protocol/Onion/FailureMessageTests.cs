namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Money;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

public class FailureMessageTests
{
    private static readonly byte[] s_sha256OfOnion = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] s_channelUpdate = [0xaa, 0xbb, 0xcc];

    public static TheoryData<string, FailureCode, string> FactoryCases => new()
    {
        { "temporary_node_failure", FailureCode.TemporaryNodeFailure, "" },
        { "permanent_node_failure", FailureCode.PermanentNodeFailure, "" },
        { "required_node_feature_missing", FailureCode.RequiredNodeFeatureMissing, "" },
        { "invalid_onion_version", FailureCode.InvalidOnionVersion, Convert.ToHexStringLower(s_sha256OfOnion) },
        { "invalid_onion_hmac", FailureCode.InvalidOnionHmac, Convert.ToHexStringLower(s_sha256OfOnion) },
        { "invalid_onion_key", FailureCode.InvalidOnionKey, Convert.ToHexStringLower(s_sha256OfOnion) },
        { "temporary_channel_failure", FailureCode.TemporaryChannelFailure, "0003aabbcc" },
        { "permanent_channel_failure", FailureCode.PermanentChannelFailure, "" },
        { "required_channel_feature_missing", FailureCode.RequiredChannelFeatureMissing, "" },
        { "unknown_next_peer", FailureCode.UnknownNextPeer, "" },
        { "amount_below_minimum", FailureCode.AmountBelowMinimum, "00000000000003e80003aabbcc" },
        { "fee_insufficient", FailureCode.FeeInsufficient, "00000000000007d00000" },
        { "incorrect_cltv_expiry", FailureCode.IncorrectCltvExpiry, "000c35000003aabbcc" },
        { "expiry_too_soon", FailureCode.ExpiryTooSoon, "0000" },
        { "incorrect_or_unknown_payment_details", FailureCode.IncorrectOrUnknownPaymentDetails, "0000000000000064000c3500" },
        { "incorrect_payment_amount", FailureCode.IncorrectPaymentAmount, "" },
        { "final_expiry_too_soon", FailureCode.FinalExpiryTooSoon, "" },
        { "final_incorrect_cltv_expiry", FailureCode.FinalIncorrectCltvExpiry, "00000090" },
        { "final_incorrect_htlc_amount", FailureCode.FinalIncorrectHtlcAmount, "0000000000002710" },
        { "channel_disabled", FailureCode.ChannelDisabled, "00000003aabbcc" },
        { "expiry_too_far", FailureCode.ExpiryTooFar, "" },
        { "invalid_onion_payload", FailureCode.InvalidOnionPayload, "fd012d0015" },
        { "mpp_timeout", FailureCode.MppTimeout, "" },
        { "invalid_onion_blinding", FailureCode.InvalidOnionBlinding, Convert.ToHexStringLower(s_sha256OfOnion) }
    };

    private static FailureMessage Create(string name)
    {
        return name switch
        {
            "temporary_node_failure" => FailureMessage.TemporaryNodeFailure(),
            "permanent_node_failure" => FailureMessage.PermanentNodeFailure(),
            "required_node_feature_missing" => FailureMessage.RequiredNodeFeatureMissing(),
            "invalid_onion_version" => FailureMessage.InvalidOnionVersion(s_sha256OfOnion),
            "invalid_onion_hmac" => FailureMessage.InvalidOnionHmac(s_sha256OfOnion),
            "invalid_onion_key" => FailureMessage.InvalidOnionKey(s_sha256OfOnion),
            "temporary_channel_failure" => FailureMessage.TemporaryChannelFailure(s_channelUpdate),
            "permanent_channel_failure" => FailureMessage.PermanentChannelFailure(),
            "required_channel_feature_missing" => FailureMessage.RequiredChannelFeatureMissing(),
            "unknown_next_peer" => FailureMessage.UnknownNextPeer(),
            "amount_below_minimum" => FailureMessage.AmountBelowMinimum(LightningMoney.MilliSatoshis(1000UL),
                                                                        s_channelUpdate),
            "fee_insufficient" => FailureMessage.FeeInsufficient(LightningMoney.MilliSatoshis(2000UL)),
            "incorrect_cltv_expiry" => FailureMessage.IncorrectCltvExpiry(800_000, s_channelUpdate),
            "expiry_too_soon" => FailureMessage.ExpiryTooSoon(),
            "incorrect_or_unknown_payment_details" =>
                FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(100UL), 800_000),
            "incorrect_payment_amount" => FailureMessage.IncorrectPaymentAmount(),
            "final_expiry_too_soon" => FailureMessage.FinalExpiryTooSoon(),
            "final_incorrect_cltv_expiry" => FailureMessage.FinalIncorrectCltvExpiry(144),
            "final_incorrect_htlc_amount" => FailureMessage.FinalIncorrectHtlcAmount(LightningMoney.Satoshis(10UL)),
            "channel_disabled" => FailureMessage.ChannelDisabled(0, s_channelUpdate),
            "expiry_too_far" => FailureMessage.ExpiryTooFar(),
            "invalid_onion_payload" => FailureMessage.InvalidOnionPayload(new BigSize(301), 21),
            "mpp_timeout" => FailureMessage.MppTimeout(),
            "invalid_onion_blinding" => FailureMessage.InvalidOnionBlinding(s_sha256OfOnion),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
        };
    }

    [Theory]
    [MemberData(nameof(FactoryCases))]
    public void Given_Factory_When_Creating_Then_CodeAndDataMatchBolt4Layout(string name, FailureCode expectedCode,
                                                                           string expectedDataHex)
    {
        // Act
        var message = Create(name);

        // Assert
        Assert.Equal(expectedCode, message.Code);
        Assert.Equal(expectedDataHex, Convert.ToHexStringLower(message.Data.Span));
        Assert.True(message.IsKnownCode);
        Assert.Null(message.Extension);
    }

    [Fact]
    public void Given_FactoryCases_When_Enumerating_Then_EveryDefinedCodeIsCovered()
    {
        // Arrange
        var covered = FactoryCases.Select(row => row.Data.Item2).ToHashSet();

        // Act / Assert
        Assert.Equal(Enum.GetValues<FailureCode>().ToHashSet(), covered);
    }

    [Fact]
    public void Given_UpdateCodes_When_ReadingAccessors_Then_FieldsAndChannelUpdateAreDecoded()
    {
        // Act
        var amountBelowMinimum = FailureMessage.AmountBelowMinimum(LightningMoney.MilliSatoshis(1000UL),
                                                                    s_channelUpdate);
        var incorrectCltv = FailureMessage.IncorrectCltvExpiry(800_000);
        var disabled = FailureMessage.ChannelDisabled(7, s_channelUpdate);
        var temporary = FailureMessage.TemporaryChannelFailure();

        // Assert
        Assert.Equal(LightningMoney.MilliSatoshis(1000UL), amountBelowMinimum.HtlcAmount);
        Assert.Equal(s_channelUpdate, amountBelowMinimum.ChannelUpdate!.Value.ToArray());
        Assert.Equal(800_000u, incorrectCltv.CltvExpiry);
        Assert.True(incorrectCltv.ChannelUpdate!.Value.IsEmpty);
        Assert.Equal((ushort)7, disabled.DisabledFlags);
        Assert.Equal(s_channelUpdate, disabled.ChannelUpdate!.Value.ToArray());
        Assert.True(temporary.ChannelUpdate!.Value.IsEmpty);
        Assert.Null(temporary.HtlcAmount);
        Assert.Null(temporary.Sha256OfOnion);
    }

    [Fact]
    public void Given_FinalAndBadOnionCodes_When_ReadingAccessors_Then_FieldsAreDecoded()
    {
        // Act
        var details = FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(100UL), 800_000);
        var finalAmount = FailureMessage.FinalIncorrectHtlcAmount(LightningMoney.MilliSatoshis(42UL));
        var finalCltv = FailureMessage.FinalIncorrectCltvExpiry(144);
        var badOnion = FailureMessage.InvalidOnionHmac(s_sha256OfOnion);
        var invalidPayload = FailureMessage.InvalidOnionPayload(new BigSize(301), 21);

        // Assert
        Assert.Equal(LightningMoney.MilliSatoshis(100UL), details.HtlcAmount);
        Assert.Equal(800_000u, details.Height);
        Assert.Null(details.ChannelUpdate);
        Assert.Equal(LightningMoney.MilliSatoshis(42UL), finalAmount.HtlcAmount);
        Assert.Equal(144u, finalCltv.CltvExpiry);
        Assert.Null(finalCltv.Height);
        Assert.Equal(s_sha256OfOnion, badOnion.Sha256OfOnion!.Value.ToArray());
        Assert.Equal(new BigSize(301), invalidPayload.InvalidPayloadType);
        Assert.Equal((ushort)21, invalidPayload.InvalidPayloadOffset);
        Assert.Null(badOnion.InvalidPayloadType);
    }

    [Theory]
    [InlineData(FailureCode.TemporaryNodeFailure, "00")]
    [InlineData(FailureCode.InvalidOnionHmac, "0011")]
    [InlineData(FailureCode.TemporaryChannelFailure, "00")]
    [InlineData(FailureCode.TemporaryChannelFailure, "0002aa")]
    [InlineData(FailureCode.TemporaryChannelFailure, "0001aabb")]
    [InlineData(FailureCode.AmountBelowMinimum, "00000000000003e8")]
    [InlineData(FailureCode.IncorrectOrUnknownPaymentDetails, "0000000000000064")]
    [InlineData(FailureCode.FinalIncorrectCltvExpiry, "000090")]
    [InlineData(FailureCode.InvalidOnionPayload, "fd00010015")]
    [InlineData(FailureCode.InvalidOnionPayload, "0600")]
    public void Given_DataNotMatchingKnownCodeLayout_When_Constructing_Then_Throws(FailureCode code, string dataHex)
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => new FailureMessage(code, Convert.FromHexString(dataHex)));
    }

    [Fact]
    public void Given_UnknownCode_When_Constructing_Then_AnyDataIsKept()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3 };

        // Act
        var message = new FailureMessage((FailureCode)0x1234, data);

        // Assert
        Assert.False(message.IsKnownCode);
        Assert.Equal(data, message.Data.ToArray());
        Assert.Null(message.HtlcAmount);
        Assert.Null(message.ChannelUpdate);
    }

    [Fact]
    public void Given_Extension_When_WithExtension_Then_CopyCarriesItAndEmptyStreamIsNull()
    {
        // Arrange
        var extension = new TlvStream();
        extension.Add(new BaseTlv(new BigSize(34001), [0x80]));
        var original = FailureMessage.TemporaryNodeFailure();

        // Act
        var withExtension = original.WithExtension(extension);
        var withEmpty = original.WithExtension(new TlvStream());

        // Assert
        Assert.Same(extension, withExtension.Extension);
        Assert.Null(original.Extension);
        Assert.Null(withEmpty.Extension);
        Assert.Equal(original.Code, withExtension.Code);
    }

    [Fact]
    public void Given_CallerBuffer_When_Constructing_Then_DataIsCopied()
    {
        // Arrange
        var data = Convert.FromHexString("00000090");

        // Act
        var message = new FailureMessage(FailureCode.FinalIncorrectCltvExpiry, data);
        data[3] = 0xff;

        // Assert
        Assert.Equal(144u, message.CltvExpiry);
    }

    [Fact]
    public void Given_WrongSha256Length_When_CreatingBadOnion_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => FailureMessage.InvalidOnionKey(new byte[31]));
    }

    [Fact]
    public void Given_OversizedChannelUpdate_When_CreatingUpdateFailure_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => FailureMessage.ExpiryTooSoon(new byte[ushort.MaxValue + 1]));
    }

    [Theory]
    [InlineData(FailureCode.TemporaryNodeFailure, "0102", 0)]
    [InlineData(FailureCode.FeeInsufficient, "00000000000007d00001aa01020304", 11)]
    [InlineData(FailureCode.InvalidOnionPayload, "fd012d0015ffff", 5)]
    [InlineData((FailureCode)0x7777, "01020304", 4)]
    public void Given_DataFollowedByTail_When_GettingDataLength_Then_OnlyTheDataIsCounted(FailureCode code,
        string hex, int expectedLength)
    {
        // Act
        var ok = FailureMessage.TryGetDataLength(code, Convert.FromHexString(hex), out var length);

        // Assert
        Assert.True(ok);
        Assert.Equal(expectedLength, length);
    }
}