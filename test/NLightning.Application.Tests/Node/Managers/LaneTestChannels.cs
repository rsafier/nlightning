using NBitcoin;

namespace NLightning.Application.Tests.Node.Managers;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;

/// <summary>
/// Registered channels for the per-channel lane tests: a channel id the memory repository knows (with its peer) gets a
/// lane of its own at its first sight on a connection.
/// </summary>
internal static class LaneTestChannels
{
    public static ChannelModel Create(ChannelId channelId, CompactPubKey peer)
    {
        var pubKey = new CompactPubKey(new Key().PubKey.ToBytes());
        var party = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(1_000),
                                     LightningMoney.Satoshis(1), 30, LightningMoney.Satoshis(100_000), 144, null);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, false, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, pubKey, pubKey, pubKey, pubKey, pubKey, pubKey);
        return new ChannelModel(channelParams, channelId, null, null, true, null, null,
                                LightningMoney.Satoshis(100_000), keySet, 0, 0, LightningMoney.Zero, null, 0, peer, 0,
                                ChannelState.Open, ChannelVersion.V1);
    }

    /// <summary>Makes the memory repository mock know <paramref name="channelIds"/> as channels of the peer.</summary>
    public static void Register(Mock<IChannelMemoryRepository> repository, CompactPubKey peer,
                                params ChannelId[] channelIds)
    {
        foreach (var channelId in channelIds)
        {
            var channel = Create(channelId, peer);
            repository.Setup(r => r.TryGetChannel(channelId, out channel)).Returns(true);
        }
    }
}