using NBitcoin;

namespace NLightning.Application.Tests.Channels.Harness;

using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Protocol.Models;
using Infrastructure.Crypto.Hashes;
using NLightning.Tests.Utils.Channels;

/// <summary>
/// NL-491: the harness's in-memory channel repository raises its events the way the real repository does (the
/// <c>ChannelUpdatedEventArgs</c> carrying the channel), so harness tests can observe subscribers such as the
/// <c>ChannelUpdateService</c>.
/// </summary>
public class InMemoryChannelRepositoryTests
{
    [Fact]
    public void Given_AChannel_When_ItIsUpdated_Then_TheEventCarriesIt()
    {
        // Arrange
        var repository = new InMemoryChannelRepository();
        var channel = CreateChannel(ChannelState.Open);
        repository.AddChannel(channel);
        ChannelUpdatedEventArgs? args = null;
        repository.OnChannelUpdated += (_, eventArgs) => args = eventArgs;

        // Act
        repository.UpdateChannel(channel);

        // Assert
        Assert.NotNull(args);
        Assert.Same(channel, args!.Channel);
    }

    [Fact]
    public void Given_AChannelUpgrade_When_TheRepositoryRefusesIt_Then_TheUpgradeEventCarriesBothChannelIds()
    {
        // Arrange: the repository supports no upgrades, but the event goes out first, as the real one raises it
        var repository = new InMemoryChannelRepository();
        var oldChannelId = new ChannelId(Enumerable.Repeat((byte)0x71, 32).ToArray());
        var channel = CreateChannel(ChannelState.Open);
        ChannelUpgradedEventArgs? args = null;
        repository.OnChannelUpgraded += (_, eventArgs) => args = eventArgs;

        // Act
        Assert.Throws<NotSupportedException>(() => repository.UpgradeChannel(oldChannelId, channel));

        // Assert
        Assert.NotNull(args);
        Assert.Equal(oldChannelId, args!.OldChannelId);
        Assert.Equal(channel.ChannelId, args.NewChannelId);
    }

    /// <summary>An open channel, built as <c>PeerManagerInboundRestartTests</c> builds the one it stores.</summary>
    private static ChannelModel CreateChannel(ChannelState state)
    {
        var local = new ChannelKeySetModel(0, Pub(), Pub(), Pub(), Pub(), Pub(), Pub());
        var remote = new ChannelKeySetModel(0, Pub(), Pub(), Pub(), Pub(), Pub(), Pub());
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), local.FundingCompactPubKey,
                                                  remote.FundingCompactPubKey,
                                                  new TxId(Enumerable.Repeat((byte)0x72, 32).ToArray()), 0);
        var channelParams = TestChannelParams.Create(LightningMoney.Satoshis(10_000), LightningMoney.Satoshis(2_500),
                                                     LightningMoney.MilliSatoshis(1_000),
                                                     LightningMoney.Satoshis(546), 483,
                                                     LightningMoney.Satoshis(500_000), 3, false,
                                                     LightningMoney.Satoshis(546), 144, FeatureSupport.No);
        return new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat((byte)0x73, 32).ToArray()),
                                new CommitmentNumber(remote.PaymentCompactBasepoint, local.PaymentCompactBasepoint,
                                                     new Sha256()),
                                fundingOutput, false, null, null, LightningMoney.Zero, local, 0, 0,
                                LightningMoney.Satoshis(1_000_000), remote, 0,
                                new CompactPubKey(new Key().PubKey.ToBytes()), 0, state, ChannelVersion.V1);
    }

    private static CompactPubKey Pub() => new Key().PubKey.ToBytes();
}