namespace NLightning.Domain.Tests.Channels.Acceptance;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Acceptance;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Protocol.Constants;

/// <summary>
/// NL-1180/NL-1181: how channel acceptor answers combine (LND's merge) and apply to what we announce, as LND's funding
/// manager applies them.
/// </summary>
public class ChannelOpenDecisionRulesTests
{
    private static readonly ChannelParty s_local = new(LightningMoney.Satoshis(354), LightningMoney.Satoshis(10_000),
                                                       LightningMoney.MilliSatoshis(1), 483,
                                                       LightningMoney.Satoshis(800_000), 144);

    private static readonly BitcoinScript s_p2wpkh = new([0x00, 0x14, .. Enumerable.Repeat((byte)0x05, 20)]);

    private static ChannelOpenRequest Request(bool dualFunded = false, bool zeroConfType = false) =>
        new(new CompactPubKey(Convert.FromHexString("02" + new string('a', 64))), ChainConstants.Regtest,
            new ChannelId(new byte[32]), LightningMoney.Satoshis(1_000_000), LightningMoney.Zero,
            LightningMoney.Satoshis(546), LightningMoney.Satoshis(500_000), LightningMoney.Satoshis(10_000),
            LightningMoney.MilliSatoshis(1), 253, 144, 30, 0, zeroConfType ? ZeroConfChannelType() : null, dualFunded);

    private static FeatureSet ZeroConfChannelType()
    {
        var channelType = FeatureSet.NewBasicChannelType();
        channelType.SetFeature(Feature.OptionScidAlias, true);
        channelType.SetFeature(Feature.OptionZeroconf, true);
        return channelType;
    }

    [Fact]
    public void Given_TwoAcceptancesThatAgree_When_Merged_Then_TheirValuesCombine()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryMerge(new ChannelOpenDecision { Accept = true, ToSelfDelay = 200 },
                                                      new ChannelOpenDecision
                                                      {
                                                          Accept = true,
                                                          ToSelfDelay = 200,
                                                          MinimumDepth = 3
                                                      }, out var merged);

        // Assert
        Assert.Null(error);
        Assert.Equal((ushort)200, merged.ToSelfDelay);
        Assert.Equal(3U, merged.MinimumDepth);
    }

    [Fact]
    public void Given_TwoAcceptancesThatDisagree_When_Merged_Then_ItIsAnError()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryMerge(new ChannelOpenDecision { Accept = true, MaxAcceptedHtlcs = 10 },
                                                      new ChannelOpenDecision { Accept = true, MaxAcceptedHtlcs = 20 },
                                                      out _);

        // Assert
        Assert.NotNull(error);
    }

    [Fact]
    public void Given_ZeroConfAndADepth_When_Merged_Then_ItIsAnError()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryMerge(new ChannelOpenDecision { Accept = true, ZeroConf = true },
                                                      new ChannelOpenDecision { Accept = true, MinimumDepth = 3 },
                                                      out _);

        // Assert
        Assert.NotNull(error);
    }

    [Fact]
    public void Given_AnAcceptanceWithValues_When_Applied_Then_TheyReplaceWhatWeAnnounce()
    {
        // Arrange
        var script = new BitcoinScript(Enumerable.Repeat((byte)0x51, 22).ToArray());

        // Act
        var error = ChannelOpenDecisionRules.TryApply(new ChannelOpenDecision
        {
            Accept = true,
            ToSelfDelay = 288,
            ChannelReserve = LightningMoney.Satoshis(20_000),
            MaxHtlcValueInFlight = LightningMoney.Satoshis(100_000),
            MaxAcceptedHtlcs = 12,
            HtlcMinimum = LightningMoney.MilliSatoshis(5_000),
            MinimumDepth = 6,
            UpfrontShutdownScript = script
        }, Request(), s_local, 3, LightningMoney.Satoshis(1_000_000), out var local, out var depth);

        // Assert
        Assert.Null(error);
        Assert.Equal((ushort)288, local.ToSelfDelay);
        Assert.Equal(LightningMoney.Satoshis(20_000), local.ChannelReserveAmount);
        Assert.Equal(LightningMoney.Satoshis(100_000), local.MaxHtlcValueInFlight);
        Assert.Equal((ushort)12, local.MaxAcceptedHtlcs);
        Assert.Equal(LightningMoney.MilliSatoshis(5_000), local.HtlcMinimumAmount);
        Assert.Equal(s_local.DustLimitAmount, local.DustLimitAmount);
        Assert.Equal(script, local.UpfrontShutdownScript);
        Assert.Equal(6U, depth);
    }

    [Fact]
    public void Given_AnAcceptanceWithoutValues_When_Applied_Then_NothingChanges()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryApply(ChannelOpenDecision.Accepted, Request(), s_local, 3,
                                                      LightningMoney.Satoshis(1_000_000), out var local, out var depth);

        // Assert
        Assert.Null(error);
        Assert.Equal(s_local, local);
        Assert.Equal(3U, depth);
    }

    public static TheoryData<ChannelOpenDecision, bool> Unappliable => new()
    {
        { new ChannelOpenDecision { Accept = true, ZeroConf = true }, false },
        { new ChannelOpenDecision { Accept = true, MinimumDepth = 0 }, false },
        { new ChannelOpenDecision { Accept = true, MaxAcceptedHtlcs = 484 }, false },
        { new ChannelOpenDecision { Accept = true, ChannelReserve = LightningMoney.Satoshis(500) }, false },
        { new ChannelOpenDecision { Accept = true, ChannelReserve = LightningMoney.Satoshis(1_000_000) }, false },
        { new ChannelOpenDecision { Accept = true, HtlcMinimum = LightningMoney.Satoshis(1_000_000) }, false },
        { new ChannelOpenDecision { Accept = true, ChannelReserve = LightningMoney.Satoshis(20_000) }, true },
        { new ChannelOpenDecision { Accept = true, ToSelfDelay = 0 }, false },
        { new ChannelOpenDecision { Accept = true, MaxHtlcValueInFlight = LightningMoney.Zero }, false }
    };

    [Theory]
    [MemberData(nameof(Unappliable))]
    public void Given_AValueThatCannotApply_When_Applied_Then_TheOpenIsRefused(ChannelOpenDecision decision,
                                                                             bool dualFunded)
    {
        // Act
        var error = ChannelOpenDecisionRules.TryApply(decision, Request(dualFunded), s_local, 3,
                                                      LightningMoney.Satoshis(1_000_000), out var local, out _);

        // Assert
        Assert.NotNull(error);
        Assert.Equal(s_local, local);
    }

    [Fact]
    public void Given_AZeroConfOpenAndAZeroConfAcceptance_When_Applied_Then_WeAskForDepthZero()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryApply(new ChannelOpenDecision { Accept = true, ZeroConf = true },
                                                      Request(zeroConfType: true), s_local, 3,
                                                      LightningMoney.Satoshis(1_000_000), out _, out var depth);

        // Assert
        Assert.Null(error);
        Assert.Equal(0U, depth);
    }

    [Fact]
    public void Given_AZeroConfOpenAndADepthZeroAcceptanceWithoutZeroConf_When_Applied_Then_TheOpenIsRefused()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryApply(new ChannelOpenDecision { Accept = true, MinimumDepth = 0 },
                                                      Request(zeroConfType: true), s_local, 3,
                                                      LightningMoney.Satoshis(1_000_000), out var local,
                                                      out var depth);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("min_accept_depth 0", error);
        Assert.Equal(s_local, local);
        Assert.Equal(3U, depth);
    }

    [Fact]
    public void Given_AnOpenWithoutTheZeroConfTypeAndADepthZeroAcceptance_When_Applied_Then_ItIsNotAZeroConfAcceptance()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryApply(new ChannelOpenDecision { Accept = true, MinimumDepth = 0 },
                                                      Request(), s_local, 3, LightningMoney.Satoshis(1_000_000),
                                                      out _, out var depth);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("without zero_conf", error);
        Assert.Equal(3U, depth);
    }

    [Fact]
    public void Given_AZeroConfOpenAndZeroConfWithDepthZero_When_Applied_Then_WeAskForDepthZero()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryApply(
            new ChannelOpenDecision { Accept = true, ZeroConf = true, MinimumDepth = 0 }, Request(zeroConfType: true),
            s_local, 3, LightningMoney.Satoshis(1_000_000), out _, out var depth);

        // Assert
        Assert.Null(error);
        Assert.Equal(0U, depth);
    }

    [Fact]
    public void Given_AZeroConfOpenAndAnAcceptanceWithoutZeroConf_When_Applied_Then_TheAcceptorBlockedIt()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryApply(new ChannelOpenDecision { Accept = true, ToSelfDelay = 200 },
                                                      Request(zeroConfType: true), s_local, 0,
                                                      LightningMoney.Satoshis(1_000_000), out var local, out _);

        // Assert
        Assert.Equal("channel acceptor blocked zero-conf channel negotiation", error);
        Assert.Equal(s_local, local);
    }

    [Fact]
    public void Given_AZeroConfAcceptanceWithADepth_When_Applied_Then_TheOpenIsRefused()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryApply(
            new ChannelOpenDecision { Accept = true, ZeroConf = true, MinimumDepth = 2 }, Request(zeroConfType: true),
            s_local, 3, LightningMoney.Satoshis(1_000_000), out _, out _);

        // Assert
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(5_000, 10_000)]
    [InlineData(10_000, 10_000)]
    public void Given_ADualFundedOpenAndAReserveTheV2ReserveMeets_When_Applied_Then_TheV2ReserveStays(
        long wantedSat, long expectedSat)
    {
        // Act: s_local's 10,000 sat is the reserve BOLT 2 fixes for the dual-funded channel
        var error = ChannelOpenDecisionRules.TryApply(
            new ChannelOpenDecision { Accept = true, ChannelReserve = LightningMoney.Satoshis(wantedSat) },
            Request(dualFunded: true), s_local, 3, LightningMoney.Satoshis(1_000_000), out var local, out _);

        // Assert
        Assert.Null(error);
        Assert.Equal(LightningMoney.Satoshis(expectedSat), local.ChannelReserveAmount);
    }

    [Fact]
    public void Given_ADualFundedOpenAndAReserveBelowTheDustLimit_When_Applied_Then_TheOpenIsRefused()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryApply(
            new ChannelOpenDecision { Accept = true, ChannelReserve = LightningMoney.Satoshis(500) },
            Request(dualFunded: true), s_local, 3, LightningMoney.Satoshis(1_000_000), out _, out _);

        // Assert
        Assert.Equal("reserve lower than proposed dust limit", error);
    }

    [Fact]
    public void Given_ADualFundedOpenAndAnUpfrontScript_When_AppliedWithTheFeature_Then_ItIsAnnounced()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryApply(
            new ChannelOpenDecision { Accept = true, UpfrontShutdownScript = s_p2wpkh }, Request(dualFunded: true),
            s_local, 3, LightningMoney.Satoshis(1_000_000), out var local, out _,
            new FeatureOptions { UpfrontShutdownScript = FeatureSupport.Optional });

        // Assert
        Assert.Null(error);
        Assert.Equal(s_p2wpkh, local.UpfrontShutdownScript);
    }

    [Fact]
    public void Given_AnUpfrontScriptWithoutTheFeature_When_Applied_Then_TheOpenIsRefused()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryApply(
            new ChannelOpenDecision { Accept = true, UpfrontShutdownScript = s_p2wpkh }, Request(), s_local, 3,
            LightningMoney.Satoshis(1_000_000), out _, out _, new FeatureOptions());

        // Assert
        Assert.NotNull(error);
        Assert.Contains("does not support option upfront shutdown script", error);
    }

    [Fact]
    public void Given_AnUpfrontScriptThatIsNoShutdownForm_When_Applied_Then_TheOpenIsRefused()
    {
        // Act
        var error = ChannelOpenDecisionRules.TryApply(
            new ChannelOpenDecision { Accept = true, UpfrontShutdownScript = new BitcoinScript([0x51]) }, Request(),
            s_local, 3, LightningMoney.Satoshis(1_000_000), out _, out _,
            new FeatureOptions { UpfrontShutdownScript = FeatureSupport.Optional });

        // Assert
        Assert.Equal("upfront_shutdown is not a valid shutdown script", error);
    }

    [Fact]
    public void Given_ALongError_When_Truncated_Then_ItIs500Characters()
    {
        // Act / Assert
        Assert.Equal(500, ChannelOpenDecisionRules.Truncate(new string('x', 900)).Length);
        Assert.Equal(ChannelOpenDecision.GenericRejection, ChannelOpenDecisionRules.Truncate(null));
    }
}