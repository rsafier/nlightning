using System.Diagnostics.CodeAnalysis;

namespace NLightning.Tests.Utils.Channels;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Money;

/// <summary>
/// Builds <see cref="ChannelParams"/> for tests that do not care about per-side values: both sides get the same
/// reserve, htlc minimum, max accepted HTLCs, max in flight and to_self_delay, and each side its own dust limit.
/// </summary>
[ExcludeFromCodeCoverage]
public static class TestChannelParams
{
    public static ChannelParams Create(LightningMoney channelReserveAmount, LightningMoney feeRateAmountPerKw,
                                       LightningMoney htlcMinimumAmount, LightningMoney localDustLimitAmount,
                                       ushort maxAcceptedHtlcs, LightningMoney maxHtlcAmountInFlight,
                                       uint minimumDepth, bool optionAnchorOutputs,
                                       LightningMoney remoteDustLimitAmount, ushort toSelfDelay,
                                       FeatureSupport useScidAlias, BitcoinScript? localUpfrontShutdownScript = null,
                                       BitcoinScript? remoteUpfrontShutdownScript = null)
    {
        var local = new ChannelParty(localDustLimitAmount, channelReserveAmount, htlcMinimumAmount, maxAcceptedHtlcs,
                                     maxHtlcAmountInFlight, toSelfDelay, localUpfrontShutdownScript);
        var remote = new ChannelParty(remoteDustLimitAmount, channelReserveAmount, htlcMinimumAmount,
                                      maxAcceptedHtlcs, maxHtlcAmountInFlight, toSelfDelay,
                                      remoteUpfrontShutdownScript);

        return new ChannelParams(local, remote, feeRateAmountPerKw, minimumDepth, optionAnchorOutputs, useScidAlias);
    }
}