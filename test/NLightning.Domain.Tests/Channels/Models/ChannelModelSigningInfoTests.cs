using NLightning.Tests.Utils.Channels;

namespace NLightning.Domain.Tests.Channels.Models;

using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;

/// <summary>
/// NL-495: <see cref="ChannelModel.GetSigningInfo"/> reports the current funding (outpoint, capacity, both keys and our
/// key index), which a splice lock rotates, never the key sets' original funding keys.
/// </summary>
public class ChannelModelSigningInfoTests
{
    private static readonly CompactPubKey s_localKey = Key(0x01);
    private static readonly CompactPubKey s_remoteKey = Key(0x02);
    private static readonly CompactPubKey s_splicedLocalKey = Key(0x03);
    private static readonly CompactPubKey s_splicedRemoteKey = Key(0x04);
    private static readonly TxId s_fundingTxId = new(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static readonly TxId s_spliceTxId = new(Enumerable.Repeat((byte)0x22, 32).ToArray());

    [Fact]
    public void Given_ANeverSplicedChannel_When_GetSigningInfo_Then_TheKeySetsFundingKeysAndIndexZero()
    {
        // Arrange
        var channel = CreateChannel();

        // Act
        var info = channel.GetSigningInfo();

        // Assert
        Assert.Equal(s_fundingTxId, info.FundingTxId);
        Assert.Equal((ushort)1, info.FundingOutputIndex);
        Assert.Equal(s_localKey, info.LocalFundingPubKey);
        Assert.Equal(s_remoteKey, info.RemoteFundingPubKey);
        Assert.Equal(0U, info.LocalFundingKeyIndex);
        Assert.Equal(0U, channel.LocalFundingKeyIndex);
    }

    [Fact]
    public void Given_ALockedSplice_When_GetSigningInfo_Then_TheSplicedFundingsOutpointKeysAndIndex()
    {
        // Arrange: the lock replaces the funding output and sets the rotated key's index
        var channel = CreateChannel();
        channel.ReplaceFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(1_500_000), s_splicedLocalKey,
                                                           s_splicedRemoteKey, s_spliceTxId, 0));
        channel.SetLocalFundingKeyIndex(2);

        // Act
        var info = channel.GetSigningInfo();

        // Assert
        Assert.Equal(s_spliceTxId, info.FundingTxId);
        Assert.Equal((ushort)0, info.FundingOutputIndex);
        Assert.Equal(LightningMoney.Satoshis(1_500_000).MilliSatoshi, info.FundingSatoshis);
        Assert.Equal(s_splicedLocalKey, info.LocalFundingPubKey);
        Assert.Equal(s_splicedRemoteKey, info.RemoteFundingPubKey);
        Assert.Equal(2U, info.LocalFundingKeyIndex);
        Assert.Equal(7U, info.ChannelKeyIndex);
        Assert.Equal(s_remoteKey, info.RemoteHtlcBasepoint);
    }

    private static ChannelModel CreateChannel()
    {
        var channelParams = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Satoshis(2_500),
                                                     LightningMoney.Zero, LightningMoney.Zero, 0, LightningMoney.Zero,
                                                     3, false, LightningMoney.Zero, 144, FeatureSupport.No);
        var local = new ChannelKeySetModel(7, s_localKey, s_localKey, s_localKey, s_localKey, s_localKey, s_localKey);
        var remote = new ChannelKeySetModel(0, s_remoteKey, s_remoteKey, s_remoteKey, s_remoteKey, s_remoteKey,
                                            s_remoteKey);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), s_localKey, s_remoteKey,
                                                  s_fundingTxId, 1);
        return new ChannelModel(channelParams, ChannelId.Zero, null, fundingOutput, true, null, null,
                                LightningMoney.Satoshis(1_000_000), local, 0, 0, LightningMoney.Zero, remote, 0,
                                s_remoteKey, 0, ChannelState.Open, ChannelVersion.V1);
    }

    private static CompactPubKey Key(byte last)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = last;
        return new CompactPubKey(bytes);
    }
}