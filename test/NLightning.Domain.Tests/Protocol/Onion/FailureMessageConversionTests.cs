namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Money;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

public class FailureMessageConversionTests
{
    private static readonly byte[] s_sha256OfOnion = Enumerable.Range(0, 32).Select(i => (byte)(0xa0 + i)).ToArray();
    private static readonly byte[] s_channelUpdate = [0x01, 0x02, 0xaa, 0xbb];

    [Theory]
    [InlineData(FailureCode.InvalidOnionVersion)]
    [InlineData(FailureCode.InvalidOnionHmac)]
    [InlineData(FailureCode.InvalidOnionKey)]
    [InlineData(FailureCode.InvalidOnionBlinding)]
    public void Given_BadOnionCode_When_ConvertingMalformed_Then_DataIsSha256OfOnion(FailureCode code)
    {
        // Act
        var message = FailureMessage.FromMalformed(code, s_sha256OfOnion);

        // Assert
        Assert.Equal(code, message.Code);
        Assert.Equal(s_sha256OfOnion, message.Data.ToArray());
        Assert.Equal(s_sha256OfOnion, message.Sha256OfOnion!.Value.ToArray());
        Assert.Null(message.Extension);
    }

    [Fact]
    public void Given_UnknownBadOnionCode_When_ConvertingMalformed_Then_CodeIsKeptAndDataIsSha256OfOnion()
    {
        // Arrange: BOLT 2 says to use the failure_code given, even one this node does not know
        var code = (FailureCode)0xC0FF;

        // Act
        var message = FailureMessage.FromMalformed(code, s_sha256OfOnion);

        // Assert
        Assert.Equal(code, message.Code);
        Assert.False(message.IsKnownCode);
        Assert.Equal(s_sha256OfOnion, message.Data.ToArray());
    }

    [Theory]
    [InlineData(FailureCode.TemporaryNodeFailure)]
    [InlineData(FailureCode.PermanentChannelFailure)]
    [InlineData(FailureCode.InvalidOnionPayload)]
    [InlineData((FailureCode)0x4005)]
    public void Given_CodeWithoutBadOnionBit_When_ConvertingMalformed_Then_Throws(FailureCode code)
    {
        // Act / Assert
        var exception = Assert.Throws<ArgumentException>(() => FailureMessage.FromMalformed(code, s_sha256OfOnion));
        Assert.Equal("failureCode", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Given_WrongSha256Length_When_ConvertingMalformed_Then_Throws(int length)
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => FailureMessage.FromMalformed(FailureCode.InvalidOnionHmac,
                                                                            new byte[length]));
    }

    [Fact]
    public void Given_UpdateFailures_When_ReplacingChannelUpdate_Then_FixedFieldsAndExtensionAreKept()
    {
        // Arrange
        var extension = new TlvStream();
        extension.Add(new BaseTlv(new BigSize(34001), [0x01, 0x02]));
        var feeInsufficient = FailureMessage.FeeInsufficient(LightningMoney.MilliSatoshis(2000UL))
                                            .WithExtension(extension);
        var disabled = FailureMessage.ChannelDisabled(3, s_channelUpdate);

        // Act
        var withUpdate = feeInsufficient.WithChannelUpdate(s_channelUpdate);
        var withoutUpdate = disabled.WithChannelUpdate(ReadOnlySpan<byte>.Empty);

        // Assert
        Assert.Equal("00000000000007d000040102aabb", Convert.ToHexStringLower(withUpdate.Data.Span));
        Assert.Equal(LightningMoney.MilliSatoshis(2000UL), withUpdate.HtlcAmount);
        Assert.Same(extension, withUpdate.Extension);
        Assert.Equal("00030000", Convert.ToHexStringLower(withoutUpdate.Data.Span));
        Assert.Equal((ushort)3, withoutUpdate.DisabledFlags);
        Assert.True(withoutUpdate.ChannelUpdate!.Value.IsEmpty);
    }

    [Theory]
    [InlineData(FailureCode.TemporaryChannelFailure, true)]
    [InlineData(FailureCode.AmountBelowMinimum, true)]
    [InlineData(FailureCode.FeeInsufficient, true)]
    [InlineData(FailureCode.IncorrectCltvExpiry, true)]
    [InlineData(FailureCode.ExpiryTooSoon, true)]
    [InlineData(FailureCode.ChannelDisabled, true)]
    [InlineData(FailureCode.TemporaryNodeFailure, false)]
    [InlineData(FailureCode.UnknownNextPeer, false)]
    public void Given_Code_When_CheckingChannelUpdateField_Then_OnlyUpdateCodesHaveIt(FailureCode code,
                                                                                     bool expected)
    {
        // Arrange
        var message = code switch
        {
            FailureCode.TemporaryChannelFailure => FailureMessage.TemporaryChannelFailure(),
            FailureCode.AmountBelowMinimum => FailureMessage.AmountBelowMinimum(LightningMoney.Zero),
            FailureCode.FeeInsufficient => FailureMessage.FeeInsufficient(LightningMoney.Zero),
            FailureCode.IncorrectCltvExpiry => FailureMessage.IncorrectCltvExpiry(1),
            FailureCode.ExpiryTooSoon => FailureMessage.ExpiryTooSoon(),
            FailureCode.ChannelDisabled => FailureMessage.ChannelDisabled(),
            FailureCode.TemporaryNodeFailure => FailureMessage.TemporaryNodeFailure(),
            _ => FailureMessage.UnknownNextPeer()
        };

        // Act / Assert
        Assert.Equal(expected, message.HasChannelUpdateField);
    }

    [Fact]
    public void Given_NonUpdateFailure_When_ReplacingChannelUpdate_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => FailureMessage.PermanentChannelFailure()
                                                                     .WithChannelUpdate(s_channelUpdate));
    }

    [Fact]
    public void Given_ChannelUpdateOver65535Bytes_When_Replacing_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => FailureMessage.ExpiryTooSoon()
                                                             .WithChannelUpdate(new byte[ushort.MaxValue + 1]));
    }
}