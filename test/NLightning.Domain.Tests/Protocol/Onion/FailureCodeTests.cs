namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Extensions;

public class FailureCodeTests
{
    private static readonly (FailureCode Code, ushort Value)[] s_expectedValues =
    [
        (FailureCode.TemporaryNodeFailure, 0x2002),
        (FailureCode.PermanentNodeFailure, 0x6002),
        (FailureCode.RequiredNodeFeatureMissing, 0x6003),
        (FailureCode.InvalidOnionVersion, 0xC004),
        (FailureCode.InvalidOnionHmac, 0xC005),
        (FailureCode.InvalidOnionKey, 0xC006),
        (FailureCode.TemporaryChannelFailure, 0x1007),
        (FailureCode.PermanentChannelFailure, 0x4008),
        (FailureCode.RequiredChannelFeatureMissing, 0x4009),
        (FailureCode.UnknownNextPeer, 0x400A),
        (FailureCode.AmountBelowMinimum, 0x100B),
        (FailureCode.FeeInsufficient, 0x100C),
        (FailureCode.IncorrectCltvExpiry, 0x100D),
        (FailureCode.ExpiryTooSoon, 0x100E),
        (FailureCode.IncorrectOrUnknownPaymentDetails, 0x400F),
        (FailureCode.IncorrectPaymentAmount, 0x4010),
        (FailureCode.FinalExpiryTooSoon, 0x0011),
        (FailureCode.FinalIncorrectCltvExpiry, 0x0012),
        (FailureCode.FinalIncorrectHtlcAmount, 0x0013),
        (FailureCode.ChannelDisabled, 0x1014),
        (FailureCode.ExpiryTooFar, 0x0015),
        (FailureCode.InvalidOnionPayload, 0x4016),
        (FailureCode.MppTimeout, 0x0017),
        (FailureCode.InvalidOnionBlinding, 0xC018)
    ];

    public static TheoryData<FailureCode, ushort> ExpectedValues
    {
        get
        {
            var data = new TheoryData<FailureCode, ushort>();
            foreach (var (code, value) in s_expectedValues)
                data.Add(code, value);

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ExpectedValues))]
    public void Given_FailureCode_When_CastToUshort_Then_MatchesBolt4Value(FailureCode code, ushort expected)
    {
        // Arrange & Act
        var value = (ushort)code;

        // Assert
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Given_AllFailureCodes_When_Enumerated_Then_EveryCodeHasAnExpectedValue()
    {
        // Arrange
        var expected = s_expectedValues.Select(e => e.Code).ToHashSet();

        // Act
        var all = Enum.GetValues<FailureCode>();

        // Assert
        Assert.Equal(24, all.Length);
        Assert.All(all, code => Assert.Contains(code, expected));
    }

    [Theory]
    [InlineData(FailureCode.InvalidOnionVersion)]
    [InlineData(FailureCode.InvalidOnionHmac)]
    [InlineData(FailureCode.InvalidOnionKey)]
    [InlineData(FailureCode.InvalidOnionBlinding)]
    public void Given_BadOnionCode_When_CheckingFlags_Then_IsBadOnionAndPerm(FailureCode code)
    {
        // Act & Assert
        Assert.True(code.IsBadOnion());
        Assert.True(code.IsPerm());
        Assert.False(code.IsNode());
        Assert.False(code.IsUpdate());
        Assert.Equal(FailureCodeFlags.BadOnion | FailureCodeFlags.Perm, code.GetFlags());
    }

    [Fact]
    public void Given_InvalidOnionHmac_When_CheckingIsBadOnion_Then_True()
    {
        // Act
        var result = FailureCode.InvalidOnionHmac.IsBadOnion();

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(FailureCode.TemporaryNodeFailure, false, false, true, false)]
    [InlineData(FailureCode.PermanentNodeFailure, false, true, true, false)]
    [InlineData(FailureCode.RequiredNodeFeatureMissing, false, true, true, false)]
    [InlineData(FailureCode.TemporaryChannelFailure, false, false, false, true)]
    [InlineData(FailureCode.PermanentChannelFailure, false, true, false, false)]
    [InlineData(FailureCode.UnknownNextPeer, false, true, false, false)]
    [InlineData(FailureCode.ChannelDisabled, false, false, false, true)]
    [InlineData(FailureCode.IncorrectOrUnknownPaymentDetails, false, true, false, false)]
    [InlineData(FailureCode.InvalidOnionPayload, false, true, false, false)]
    [InlineData(FailureCode.FinalIncorrectCltvExpiry, false, false, false, false)]
    [InlineData(FailureCode.MppTimeout, false, false, false, false)]
    [InlineData(FailureCode.IncorrectPaymentAmount, false, true, false, false)]
    [InlineData(FailureCode.FinalExpiryTooSoon, false, false, false, false)]
    public void Given_FailureCode_When_CheckingFlags_Then_FlagsMatchSpec(FailureCode code, bool badOnion, bool perm,
                                                                          bool node, bool update)
    {
        // Act & Assert
        Assert.Equal(badOnion, code.IsBadOnion());
        Assert.Equal(perm, code.IsPerm());
        Assert.Equal(node, code.IsNode());
        Assert.Equal(update, code.IsUpdate());
    }
}