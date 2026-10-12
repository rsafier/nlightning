using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Tests.Utils.Channels;

namespace NLightning.Application.Tests.Gossip;

using Application.Channels.Services;
using Application.Gossip.Events;
using Application.Gossip.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin;

/// <summary>
/// Splicing plan SP2-B-T2 (D12): our <c>channel_update</c> follows the short channel id a splice lock gives the channel.
/// </summary>
public class ChannelUpdateServiceSpliceTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);
    private static readonly ShortChannelId s_oldScid = new(500, 3, 1);
    private static readonly ShortChannelId s_newScid = new(620, 7, 0);

    private readonly Key _ourKey = new(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private readonly Key _peerKey = new(Enumerable.Repeat((byte)0x22, 32).ToArray());
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly List<ChannelModel> _channels = [];

    public ChannelUpdateServiceSpliceTests()
    {
        _memory.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => _channels.Where(predicate).ToList());
        _memory.Setup(r => r.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId channelId, out ChannelModel? channel) =>
                {
                    channel = _channels.FirstOrDefault(c => c.ChannelId == channelId);
                    return channel is not null;
                }));
    }

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    private CompactPubKey OurNodeId => _ourKey.PubKey.ToBytes();
    private CompactPubKey PeerNodeId => _peerKey.PubKey.ToBytes();

    [Fact]
    public async Task Given_AnOpenChannelWithAnUpdate_When_ASpliceLockMovesItsScid_Then_ANewUpdateNamesTheNewScid()
    {
        // Arrange: the update made at the open, for the funding's short channel id
        using var service = CreateService();
        var channel = AddChannel();
        var raised = new List<ChannelUpdateReadyEventArgs>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnChannelUpdateReady += (_, args) =>
        {
            lock (raised)
            {
                raised.Add(args);
                if (raised.Count == 2)
                    second.TrySetResult();
            }
        };
        RaiseChannelUpdated(channel);
        await WaitForAsync(() => raised.Count == 1);

        // Act: the lock switches the channel to the splice's short channel id
        channel.ShortChannelId = s_newScid;
        RaiseChannelUpdated(channel);
        await second.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert: the new update names the new short channel id, private until the splice is announced, and is newer
        Assert.Equal(s_oldScid, raised[0].Message.Payload.ShortChannelId);
        var update = raised[1].Message.Payload;
        Assert.Equal(s_newScid, update.ShortChannelId);
        Assert.Equal(PeerNodeId, raised[1].PeerPubKey);
        Assert.NotEqual(0, update.MessageFlags & ChannelUpdatePayload.MessageFlagDontForward);
        Assert.False(update.IsDisabled);
        Assert.True(update.Timestamp > raised[0].Message.Payload.Timestamp);
        Assert.True(service.TryGetLocalChannelUpdate(channel.ChannelId, out var local));
        Assert.Equal(s_newScid, local!.Payload.ShortChannelId);
    }

    [Fact]
    public async Task Given_AnUnchangedScid_When_TheChannelIsUpdatedAgain_Then_NoOtherUpdateIsMade()
    {
        // Arrange
        using var service = CreateService();
        var channel = AddChannel();
        var raised = new List<ChannelUpdateReadyEventArgs>();
        service.OnChannelUpdateReady += (_, args) =>
        {
            lock (raised)
                raised.Add(args);
        };
        RaiseChannelUpdated(channel);
        await WaitForAsync(() => raised.Count == 1);

        // Act: any other change of the channel (an HTLC, a fee) keeps its short channel id
        RaiseChannelUpdated(channel);
        RaiseChannelUpdated(channel);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(raised);
    }

    private ChannelUpdateService CreateService()
    {
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetNodeKeyPair())
                  .Returns(() => new CryptoKeyPair(_ourKey.ToBytes(), _ourKey.PubKey.ToBytes()));
        keyManager.Setup(k => k.GetNodePubKey()).Returns(() => _ourKey.PubKey.ToBytes());

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(keyManager.Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddSingleton(new Mock<IChannelMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        var signer = services.BuildServiceProvider().GetRequiredService<ILightningSigner>();

        return new ChannelUpdateService(_memory.Object, new ChannelLockProvider(), signer, keyManager.Object,
                                        Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                        NullLogger<ChannelUpdateService>.Instance);
    }

    private ChannelModel AddChannel()
    {
        var channelParams = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Satoshis(2_500),
                                                     LightningMoney.MilliSatoshis(5_000),
                                                     LightningMoney.Satoshis(354), 30,
                                                     LightningMoney.MilliSatoshis(800_000_000), 3, false,
                                                     LightningMoney.Satoshis(354), 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, OurNodeId, OurNodeId, OurNodeId, OurNodeId, OurNodeId, OurNodeId);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), OurNodeId, PeerNodeId);
        var channel = new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat((byte)0x31, 32).ToArray()), null,
                                       fundingOutput, true, null, null, LightningMoney.Zero, keySet, 0, 0,
                                       LightningMoney.Zero, keySet, 0, PeerNodeId, 0, ChannelState.Open,
                                       ChannelVersion.V1)
        {
            ShortChannelId = s_oldScid
        };
        _channels.Add(channel);
        return channel;
    }

    private void RaiseChannelUpdated(ChannelModel channel) =>
        _memory.Raise(r => r.OnChannelUpdated += null, _memory.Object, new ChannelUpdatedEventArgs(channel));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}