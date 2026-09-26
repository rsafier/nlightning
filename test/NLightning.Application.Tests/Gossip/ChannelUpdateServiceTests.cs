using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Tests.Utils.Channels;

namespace NLightning.Application.Tests.Gossip;

using Announcements;

using Application.Channels.Services;
using Application.Gossip.Announcements;
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
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin;

public class ChannelUpdateServiceTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);
    private static readonly ShortChannelId s_shortChannelId = new(500, 3, 1);
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    private Key _ourKey = new(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private Key _peerKey = new(Enumerable.Repeat((byte)0x22, 32).ToArray());
    private readonly Mock<IChannelMemoryRepository> _channelMemoryRepository = new();
    private readonly ChannelLockProvider _channelLockProvider = new();
    private readonly FixedTimeProvider _timeProvider = new(s_now);
    private readonly List<ChannelModel> _channels = [];

    private readonly NodeOptions _nodeOptions = new()
    {
        BitcoinNetwork = BitcoinNetwork.Regtest,
        Routing = new RoutingOptions
        {
            FeeBaseMsat = 2_000,
            FeeProportionalMillionths = 500,
            CltvExpiryDelta = 40,
            HtlcMinimumMsat = 1_000
        }
    };

    public ChannelUpdateServiceTests()
    {
        _channelMemoryRepository.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                                .Returns((Func<ChannelModel, bool> predicate) => _channels.Where(predicate).ToList());
        _channelMemoryRepository
           .Setup(r => r.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
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
    public void Given_OpenChannel_When_CreatingUpdate_Then_FieldsFollowRoutingOptionsAndOurSignatureVerifies()
    {
        // Arrange
        var service = CreateService(out var ourSigner);
        var channel = AddChannel(ChannelState.Open);

        // Act
        var update = service.CreateChannelUpdate(channel).Payload;

        // Assert
        Assert.Equal(ChainConstants.Regtest, update.ChainHash);
        Assert.Equal(s_shortChannelId, update.ShortChannelId);
        Assert.Equal((uint)s_now.ToUnixTimeSeconds(), update.Timestamp);
        Assert.Equal(ChannelUpdatePayload.MessageFlagMustBeOne | ChannelUpdatePayload.MessageFlagDontForward,
                     update.MessageFlags);
        Assert.False(update.IsDisabled);
        Assert.Equal(40, update.CltvExpiryDelta);
        Assert.Equal(2_000u, update.FeeBaseMsat);
        Assert.Equal(500u, update.FeeProportionalMillionths);
        // The peer's htlc_minimum (5,000 msat) is above ours (1,000); its max in flight (800M msat) is below capacity
        Assert.Equal(5_000ul, update.HtlcMinimumMsat);
        Assert.Equal(800_000_000ul, update.HtlcMaximumMsat);
        Assert.True(update.ExtraData.IsEmpty);
        Assert.True(ourSigner.VerifyNodeMessage(update.GetSignatureHash(), update.Signature, OurNodeId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_NodeIdOrder_When_CreatingUpdate_Then_DirectionIsOneOnlyWhenWeAreNode2(bool swapKeys)
    {
        // Arrange - BOLT 7: node_id_1 is the lexicographically lesser key, direction = 0 for its updates
        if (swapKeys)
            (_ourKey, _peerKey) = (_peerKey, _ourKey);
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);
        var weAreNode2 = ((ReadOnlySpan<byte>)OurNodeId).SequenceCompareTo(PeerNodeId) > 0;

        // Act
        var update = service.CreateChannelUpdate(channel).Payload;

        // Assert
        Assert.Equal(weAreNode2, update.Direction);
    }

    [Fact]
    public void Given_SmallChannelAndConfiguredMaximum_When_CreatingUpdate_Then_HtlcMaximumIsTheSmallestLimit()
    {
        // Arrange
        _nodeOptions.Routing.HtlcMaximumMsat = 300_000_000;
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open, capacity: LightningMoney.Satoshis(200_000));

        // Act
        var update = service.CreateChannelUpdate(channel).Payload;

        // Assert: capacity 200M msat < configured 300M < peer in-flight 800M
        Assert.Equal(200_000_000ul, update.HtlcMaximumMsat);
    }

    [Fact]
    public void Given_TwoUpdatesInTheSameSecond_When_Creating_Then_TimestampStrictlyIncreases()
    {
        // Arrange
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);

        // Act
        var first = service.CreateChannelUpdate(channel).Payload;
        var second = service.CreateChannelUpdate(channel, disabled: true).Payload;

        // Assert
        Assert.True(second.Timestamp > first.Timestamp);
        Assert.True(second.IsDisabled);
        Assert.True(service.TryGetLocalChannelUpdate(channel.ChannelId, out var latest));
        Assert.Equal(second.Timestamp, latest!.Payload.Timestamp);
    }

    [Fact]
    public void Given_ChannelWithoutShortChannelId_When_CreatingUpdate_Then_Throws()
    {
        // Arrange
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);
        channel.ShortChannelId = default;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => service.CreateChannelUpdate(channel));
    }

    [Fact]
    public void Given_ScidAliasChannel_When_CreatingUpdate_Then_ItNamesThePeersAliasNotTheRealScid()
    {
        // Arrange - BOLT 2: no incoming HTLC may use the real scid of an option_scid_alias channel; BOLT 7: an
        // unannounced channel's update uses an alias received from the peer (or the real scid)
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open, useScidAlias: FeatureSupport.Optional);
        var peerAlias = new ShortChannelId(16_000_000, 7, 0);
        channel.RemoteAlias = peerAlias;
        channel.LocalAliases = [new ShortChannelId(16_000_000, 9, 0)];

        // Act
        var update = service.CreateChannelUpdate(channel).Payload;

        // Assert
        Assert.Equal(peerAlias, update.ShortChannelId);
        Assert.NotEqual(s_shortChannelId, update.ShortChannelId);
    }

    [Fact]
    public async Task Given_ScidAliasChannelWithoutPeerAlias_When_SendingUpdate_Then_NothingIsSent()
    {
        // Arrange
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open, useScidAlias: FeatureSupport.Compulsory);
        var raised = new List<ChannelUpdateReadyEventArgs>();
        service.OnChannelUpdateReady += (_, args) => raised.Add(args);

        // Act
        await service.SendChannelUpdateAsync(channel.ChannelId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(raised);
        Assert.False(service.TryGetLocalChannelUpdate(channel.ChannelId, out _));
        Assert.Throws<InvalidOperationException>(() => service.CreateChannelUpdate(channel));
    }

    [Fact]
    public async Task Given_HtlcMinimumAboveTheCapacity_When_SendingUpdate_Then_NothingIsSent()
    {
        // Arrange - the maximum used to be raised to the minimum, above the capacity (BOLT 7: MUST be <= capacity)
        _nodeOptions.Routing.HtlcMinimumMsat = 300_000_000;
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open, capacity: LightningMoney.Satoshis(200_000));
        var raised = new List<ChannelUpdateReadyEventArgs>();
        service.OnChannelUpdateReady += (_, args) => raised.Add(args);

        // Act
        await service.SendChannelUpdateAsync(channel.ChannelId, TestContext.Current.CancellationToken);
        await service.SendChannelUpdatesToPeerAsync(PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(raised);
        Assert.Throws<InvalidOperationException>(() => service.CreateChannelUpdate(channel));
    }

    [Fact]
    public void Given_HtlcMinimumAbovePeersMaxInFlight_When_CreatingUpdate_Then_Throws()
    {
        // Arrange - the peer's max_htlc_value_in_flight_msat is 800M msat, below the capacity
        _nodeOptions.Routing.HtlcMinimumMsat = 900_000_000;
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => service.CreateChannelUpdate(channel));
    }

    [Fact]
    public async Task Given_ChannelBecomesOpenUnderItsLock_When_Updated_Then_UpdateIsRaisedOnceAfterTheLockIsReleased()
    {
        // Arrange - the handler that opens the channel holds its lock and queues channel_ready after the memory update
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);
        var raised = new List<ChannelUpdateReadyEventArgs>();
        var firstRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnChannelUpdateReady += (_, args) =>
        {
            lock (raised)
                raised.Add(args);
            firstRaised.TrySetResult();
        };

        // Act
        using (await _channelLockProvider.AcquireAsync(channel.ChannelId, TestContext.Current.CancellationToken))
        {
            RaiseChannelUpdated(channel);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            lock (raised)
                Assert.Empty(raised);
        }

        await firstRaised.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        RaiseChannelUpdated(channel);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        lock (raised)
        {
            var args = Assert.Single(raised);
            Assert.Equal(PeerNodeId, args.PeerPubKey);
            Assert.Equal(s_shortChannelId, args.Message.Payload.ShortChannelId);
        }
    }

    [Theory]
    [InlineData(ChannelState.ReadyForThem)]
    [InlineData(ChannelState.ReadyForUs)]
    [InlineData(ChannelState.V1FundingSigned)]
    public async Task Given_ChannelNotOpen_When_Updated_Then_NoUpdateIsRaised(ChannelState state)
    {
        // Arrange
        var service = CreateService(out _);
        var channel = AddChannel(state);
        var raised = false;
        service.OnChannelUpdateReady += (_, _) => raised = true;

        // Act
        RaiseChannelUpdated(channel);
        await service.SendChannelUpdateAsync(channel.ChannelId, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(raised);
    }

    [Fact]
    public async Task Given_OpenAndOpeningChannels_When_SendingToPeer_Then_OnlyOpenChannelsGetAnUpdate()
    {
        // Arrange
        var service = CreateService(out _);
        var open = AddChannel(ChannelState.Open);
        _ = AddChannel(ChannelState.ReadyForThem);
        var raised = new List<ChannelUpdateReadyEventArgs>();
        service.OnChannelUpdateReady += (_, args) => raised.Add(args);

        // Act
        await service.SendChannelUpdatesToPeerAsync(PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        var args = Assert.Single(raised);
        Assert.Equal(PeerNodeId, args.PeerPubKey);
        Assert.True(service.TryGetLocalChannelUpdate(open.ChannelId, out var local));
        Assert.Same(local, args.Message);
    }

    [Fact]
    public async Task Given_UnchangedPolicy_When_PeerReconnects_Then_TheSameUpdateIsSentAgain()
    {
        // Arrange - LND ignores a same-policy "keep-alive" update younger than 24 h, and resends its own as is
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);
        var raised = new List<ChannelUpdateReadyEventArgs>();
        service.OnChannelUpdateReady += (_, args) => raised.Add(args);
        await service.SendChannelUpdateAsync(channel.ChannelId, TestContext.Current.CancellationToken);

        // Act
        await service.SendChannelUpdatesToPeerAsync(PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, raised.Count);
        Assert.Same(raised[0].Message, raised[1].Message);
    }

    [Fact]
    public async Task Given_PolicyChangedSinceLastUpdate_When_PeerReconnects_Then_ANewerUpdateWithTheNewPolicyIsSent()
    {
        // Arrange
        var service = CreateService(out var ourSigner);
        var channel = AddChannel(ChannelState.Open);
        var raised = new List<ChannelUpdateReadyEventArgs>();
        service.OnChannelUpdateReady += (_, args) => raised.Add(args);
        await service.SendChannelUpdateAsync(channel.ChannelId, TestContext.Current.CancellationToken);
        _nodeOptions.Routing.FeeBaseMsat = 9_999;

        // Act
        await service.SendChannelUpdatesToPeerAsync(PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, raised.Count);
        var resent = raised[1].Message.Payload;
        Assert.True(resent.Timestamp > raised[0].Message.Payload.Timestamp);
        Assert.Equal(9_999u, resent.FeeBaseMsat);
        Assert.True(ourSigner.VerifyNodeMessage(resent.GetSignatureHash(), resent.Signature, OurNodeId));
    }

    [Fact]
    public async Task Given_LastUpdateDisabled_When_PeerReconnects_Then_AnEnabledUpdateIsSent()
    {
        // Arrange
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);
        var disabled = service.CreateChannelUpdate(channel, disabled: true);
        var raised = new List<ChannelUpdateReadyEventArgs>();
        service.OnChannelUpdateReady += (_, args) => raised.Add(args);

        // Act
        await service.SendChannelUpdatesToPeerAsync(PeerNodeId, TestContext.Current.CancellationToken);

        // Assert
        var args = Assert.Single(raised);
        Assert.False(args.Message.Payload.IsDisabled);
        Assert.True(args.Message.Payload.Timestamp > disabled.Payload.Timestamp);
    }

    [Fact]
    public async Task Given_OtherPeersChannel_When_SendingToPeer_Then_NothingIsRaised()
    {
        // Arrange
        var service = CreateService(out _);
        _ = AddChannel(ChannelState.Open);
        var raised = false;
        service.OnChannelUpdateReady += (_, _) => raised = true;
        CompactPubKey otherPeer = new Key(Enumerable.Repeat((byte)0x33, 32).ToArray()).PubKey.ToBytes();

        // Act
        await service.SendChannelUpdatesToPeerAsync(otherPeer, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(raised);
    }

    [Fact]
    public async Task Given_ChannelAlreadyResentOnConnect_When_ItIsUpdatedAsOpen_Then_NoSecondUpdateIsRaised()
    {
        // Arrange - e.g. a restart: the reconnect already sent it, the memory repository then reports it Open
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);
        var count = 0;
        service.OnChannelUpdateReady += (_, _) => Interlocked.Increment(ref count);
        await service.SendChannelUpdatesToPeerAsync(PeerNodeId, TestContext.Current.CancellationToken);

        // Act
        RaiseChannelUpdated(channel);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Given_ValidUpdateFromPeer_When_Handled_Then_ItIsStored()
    {
        // Arrange
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);
        var update = CreatePeerUpdate(timestamp: 100);

        // Act
        var stored = service.HandleRemoteChannelUpdate(PeerNodeId, update);

        // Assert
        Assert.True(stored);
        Assert.True(service.TryGetRemoteChannelUpdate(channel.ChannelId, out var remote));
        Assert.Equal(update.Payload.GetBytes(), remote!.GetBytes());
    }

    [Fact]
    public void Given_UpdateNamingOurAliasForTheChannel_When_Handled_Then_ItIsStored()
    {
        // Arrange
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);
        var alias = new ShortChannelId(16_000_000, 1, 0);
        channel.LocalAliases = [alias];

        // Act
        var stored = service.HandleRemoteChannelUpdate(PeerNodeId, CreatePeerUpdate(timestamp: 100, scid: alias));

        // Assert
        Assert.True(stored);
        Assert.True(service.TryGetRemoteChannelUpdate(channel.ChannelId, out _));
    }

    [Fact]
    public void Given_NewerThenOlderUpdate_When_Handled_Then_OnlyTheNewerIsKept()
    {
        // Arrange
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);

        // Act
        var newer = service.HandleRemoteChannelUpdate(PeerNodeId, CreatePeerUpdate(timestamp: 200, feeBase: 7));
        var older = service.HandleRemoteChannelUpdate(PeerNodeId, CreatePeerUpdate(timestamp: 150, feeBase: 9));
        var same = service.HandleRemoteChannelUpdate(PeerNodeId, CreatePeerUpdate(timestamp: 200, feeBase: 9));

        // Assert
        Assert.True(newer);
        Assert.False(older);
        Assert.False(same);
        Assert.True(service.TryGetRemoteChannelUpdate(channel.ChannelId, out var remote));
        Assert.Equal(7u, remote!.FeeBaseMsat);
    }

    [Fact]
    public void Given_UpdateFarInTheFuture_When_Handled_Then_ItIsIgnoredAndLaterUpdatesStillCount()
    {
        // Arrange - one update near uint.MaxValue used to shadow every later real update until a restart
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);
        var tooFar = (uint)(s_now + ChannelUpdateService.MaxFutureTimestamp).ToUnixTimeSeconds() + 1;
        var justInTime = (uint)(s_now + ChannelUpdateService.MaxFutureTimestamp).ToUnixTimeSeconds();

        // Act
        var farFuture = service.HandleRemoteChannelUpdate(PeerNodeId, CreatePeerUpdate(uint.MaxValue, feeBase: 1));
        var pastLimit = service.HandleRemoteChannelUpdate(PeerNodeId, CreatePeerUpdate(tooFar, feeBase: 2));
        var real = service.HandleRemoteChannelUpdate(PeerNodeId,
                                                     CreatePeerUpdate((uint)s_now.ToUnixTimeSeconds(), feeBase: 3));
        var atLimit = service.HandleRemoteChannelUpdate(PeerNodeId, CreatePeerUpdate(justInTime, feeBase: 4));

        // Assert
        Assert.False(farFuture);
        Assert.False(pastLimit);
        Assert.True(real);
        Assert.True(atLimit);
        Assert.True(service.TryGetRemoteChannelUpdate(channel.ChannelId, out var remote));
        Assert.Equal(4u, remote!.FeeBaseMsat);
    }

    [Fact]
    public void Given_UpdateWithHtlcMaximumAboveTheCapacity_When_Handled_Then_ItIsIgnored()
    {
        // Arrange - BOLT 7: SHOULD ignore the channel for routing; it would end up in our route hints
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open, capacity: LightningMoney.Satoshis(500_000));

        // Act
        var above = service.HandleRemoteChannelUpdate(PeerNodeId,
                                                      CreatePeerUpdate(timestamp: 100, htlcMaximumMsat: 500_000_001));
        var atCapacity = service.HandleRemoteChannelUpdate(PeerNodeId,
                                                           CreatePeerUpdate(timestamp: 101,
                                                                            htlcMaximumMsat: 500_000_000));

        // Assert
        Assert.False(above);
        Assert.True(atCapacity);
        Assert.True(service.TryGetRemoteChannelUpdate(channel.ChannelId, out var remote));
        Assert.Equal(500_000_000ul, remote!.HtlcMaximumMsat);
    }

    [Fact]
    public void Given_UpdateWithBadSignature_When_Handled_Then_ItIsIgnored()
    {
        // Arrange
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);
        var valid = CreatePeerUpdate(timestamp: 100).Payload;
        var tampered = new ChannelUpdatePayload(valid.Signature, valid.ChainHash, valid.ShortChannelId,
                                                valid.Timestamp, valid.MessageFlags, valid.ChannelFlags,
                                                valid.CltvExpiryDelta, valid.HtlcMinimumMsat, valid.FeeBaseMsat + 1,
                                                valid.FeeProportionalMillionths, valid.HtlcMaximumMsat);

        // Act
        var stored = service.HandleRemoteChannelUpdate(PeerNodeId, new ChannelUpdateMessage(tampered));

        // Assert
        Assert.False(stored);
        Assert.False(service.TryGetRemoteChannelUpdate(channel.ChannelId, out _));
    }

    [Fact]
    public void Given_UpdateSignedByAnotherNode_When_Handled_Then_ItIsIgnored()
    {
        // Arrange
        var service = CreateService(out _);
        AddChannel(ChannelState.Open);
        var update = CreatePeerUpdate(timestamp: 100, signer: new Key());

        // Act & Assert
        Assert.False(service.HandleRemoteChannelUpdate(PeerNodeId, update));
    }

    [Fact]
    public void Given_UpdateForAnotherChain_When_Handled_Then_ItIsIgnored()
    {
        // Arrange
        var service = CreateService(out _);
        AddChannel(ChannelState.Open);

        // Act & Assert
        Assert.False(service.HandleRemoteChannelUpdate(PeerNodeId,
                                                       CreatePeerUpdate(timestamp: 100, chain: ChainConstants.Main)));
    }

    [Fact]
    public void Given_UpdateForAnUnknownChannel_When_Handled_Then_ItIsIgnored()
    {
        // Arrange
        var service = CreateService(out _);
        AddChannel(ChannelState.Open);

        // Act & Assert
        Assert.False(service.HandleRemoteChannelUpdate(PeerNodeId,
                                                       CreatePeerUpdate(timestamp: 100, scid: new(501, 1, 0))));
    }

    [Fact]
    public void Given_UpdateInOurDirection_When_Handled_Then_ItIsIgnored()
    {
        // Arrange - an update for our side of the channel would claim to be our policy
        var service = CreateService(out _);
        AddChannel(ChannelState.Open);

        // Act & Assert
        Assert.False(service.HandleRemoteChannelUpdate(PeerNodeId,
                                                       CreatePeerUpdate(timestamp: 100, flipDirection: true)));
    }

    [Fact]
    public void Given_UpdateFromAnotherPeer_When_Handled_Then_ItIsIgnored()
    {
        // Arrange - the channel is with the peer, but a third node relays the peer's valid update to us
        var service = CreateService(out _);
        AddChannel(ChannelState.Open);
        CompactPubKey otherPeer = new Key().PubKey.ToBytes();

        // Act & Assert
        Assert.False(service.HandleRemoteChannelUpdate(otherPeer, CreatePeerUpdate(timestamp: 100)));
    }

    [Fact]
    public void Given_AnAnnouncedChannel_When_CreatingUpdate_Then_DontForwardIsClearAndTheUpdateIsPublished()
    {
        // Arrange (BOLT 7: dont_forward only for an update not preceded by the channel's announcement)
        var sink = new RecordingOwnGossipSink();
        var relay = new RecordingRelayScheduler();
        var service = CreateService(out var ourSigner, new OwnGossipPublisher(sink, relay));
        var channel = AddAnnouncedChannel();

        // Act
        var update = service.CreateChannelUpdate(channel).Payload;

        // Assert
        Assert.Equal(ChannelUpdatePayload.MessageFlagMustBeOne, update.MessageFlags);
        Assert.False(update.DontForward);
        Assert.Equal(s_shortChannelId, update.ShortChannelId);
        Assert.True(ourSigner.VerifyNodeMessage(update.GetSignatureHash(), update.Signature, OurNodeId));
        Assert.Same(update, Assert.Single(sink.ChannelUpdates));
        Assert.Same(update, Assert.Single(relay.Queued));
    }

    [Fact]
    public void Given_AnAnnouncedChannelWithScidAliasNegotiated_When_CreatingUpdate_Then_ItNamesTheRealScid()
    {
        // Arrange (the announcement names the real short channel id, so must the public update)
        var service = CreateService(out _);
        var channel = AddAnnouncedChannel(useScidAlias: FeatureSupport.Optional);
        channel.RemoteAlias = new ShortChannelId(16_000_000, 7, 0);

        // Act
        var update = service.CreateChannelUpdate(channel).Payload;

        // Assert
        Assert.Equal(s_shortChannelId, update.ShortChannelId);
        Assert.False(update.DontForward);
    }

    [Fact]
    public void Given_APublicChannelNotAnnouncedYet_When_CreatingUpdate_Then_DontForwardStaysSetAndNothingIsPublished()
    {
        // Arrange (BOLT 7: an update sent before the peers exchanged announcement_signatures is for the peer only)
        var sink = new RecordingOwnGossipSink();
        var relay = new RecordingRelayScheduler();
        var service = CreateService(out _, new OwnGossipPublisher(sink, relay));
        var channel = AddAnnouncedChannel(exchanged: false);

        // Act
        var update = service.CreateChannelUpdate(channel).Payload;

        // Assert
        Assert.True(update.DontForward);
        Assert.Empty(sink.ChannelUpdates);
        Assert.Empty(relay.Queued);
    }

    [Fact]
    public void Given_AChannelJustAnnounced_When_OnChannelAnnounced_Then_ANewerPublicUpdateGoesToThePeer()
    {
        // Arrange: the private update of the open went out first
        var relay = new RecordingRelayScheduler();
        var service = CreateService(out _, new OwnGossipPublisher(new RecordingOwnGossipSink(), relay));
        var channel = AddAnnouncedChannel(exchanged: false);
        var atOpen = service.CreateChannelUpdate(channel).Payload;
        var signature = new CompactSignature(Enumerable.Repeat((byte)0x01, 64).ToArray());
        channel.SetRemoteAnnouncementSignatures(new ChannelAnnouncementSignatures(signature, signature));
        channel.MarkAnnouncementSignaturesSent(s_now);
        var raised = new List<ChannelUpdateReadyEventArgs>();
        service.OnChannelUpdateReady += (_, args) => raised.Add(args);

        // Act
        var announced = service.OnChannelAnnounced(channel);

        // Assert
        Assert.NotNull(announced);
        var args = Assert.Single(raised);
        Assert.Equal(PeerNodeId, args.PeerPubKey);
        Assert.Same(announced, args.Message);
        Assert.False(announced.Payload.DontForward);
        Assert.True(announced.Payload.Timestamp > atOpen.Timestamp);
        Assert.Same(announced.Payload, Assert.Single(relay.Queued));
    }

    [Fact]
    public void Given_APrivateChannel_When_OnChannelAnnounced_Then_Nothing()
    {
        // Arrange
        var service = CreateService(out _);
        var channel = AddChannel(ChannelState.Open);

        // Act / Assert
        Assert.Null(service.OnChannelAnnounced(channel));
        Assert.False(service.TryGetLocalChannelUpdate(channel.ChannelId, out _));
    }

    [Fact]
    public void Given_AnAnnouncedChannelShuttingDown_When_Updated_Then_ADisabledUpdateIsRelayedOnce()
    {
        // Arrange (BOLT 7: MAY send a disabled update prior to an on-chain settlement)
        var relay = new RecordingRelayScheduler();
        var service = CreateService(out _, new OwnGossipPublisher(new RecordingOwnGossipSink(), relay));
        var channel = AddAnnouncedChannel(ChannelState.ShuttingDown);
        var raised = new List<ChannelUpdateReadyEventArgs>();
        service.OnChannelUpdateReady += (_, args) => raised.Add(args);

        // Act
        RaiseChannelUpdated(channel);
        RaiseChannelUpdated(channel);

        // Assert: relayed, not sent to the peer
        var update = Assert.IsType<ChannelUpdatePayload>(Assert.Single(relay.Queued));
        Assert.True(update.IsDisabled);
        Assert.False(update.DontForward);
        Assert.Empty(raised);
    }

    [Fact]
    public void Given_APrivateChannelShuttingDown_When_Updated_Then_NothingIsRelayed()
    {
        // Arrange
        var relay = new RecordingRelayScheduler();
        var service = CreateService(out _, new OwnGossipPublisher(new RecordingOwnGossipSink(), relay));
        var channel = AddChannel(ChannelState.ShuttingDown);

        // Act
        RaiseChannelUpdated(channel);

        // Assert
        Assert.Empty(relay.Queued);
        Assert.False(service.TryGetLocalChannelUpdate(channel.ChannelId, out _));
    }

    private ChannelUpdateService CreateService(out ILightningSigner ourSigner, OwnGossipPublisher? publisher = null)
    {
        ourSigner = CreateSigner(_ourKey);
        var keyManager = CreateKeyManager(_ourKey);
        return new ChannelUpdateService(_channelMemoryRepository.Object, _channelLockProvider, ourSigner,
                                        keyManager.Object, Options.Create(_nodeOptions),
                                        NullLogger<ChannelUpdateService>.Instance, _timeProvider, publisher);
    }

    /// <summary>
    /// A public channel whose <c>announcement_signatures</c> were exchanged (both halves; the signatures' bytes do not
    /// matter to the update service).
    /// </summary>
    private ChannelModel AddAnnouncedChannel(ChannelState state = ChannelState.Open,
                                             FeatureSupport useScidAlias = FeatureSupport.No, bool exchanged = true)
    {
        var channel = AddChannel(state, useScidAlias: useScidAlias, announce: true);
        if (!exchanged)
            return channel;

        var signature = new CompactSignature(Enumerable.Repeat((byte)0x01, 64).ToArray());
        channel.SetRemoteAnnouncementSignatures(new ChannelAnnouncementSignatures(signature, signature));
        channel.MarkAnnouncementSignaturesSent(s_now);
        return channel;
    }

    private ChannelUpdateMessage CreatePeerUpdate(uint timestamp, ShortChannelId? scid = null, uint feeBase = 1_000,
                                                  ChainHash? chain = null, bool flipDirection = false,
                                                  Key? signer = null, ulong htlcMaximumMsat = 990_000_000)
    {
        var peerIsNode2 = ((ReadOnlySpan<byte>)PeerNodeId).SequenceCompareTo(OurNodeId) > 0;
        var direction = peerIsNode2 != flipDirection;
        var unsigned = new ChannelUpdatePayload(ChannelUpdatePayload.EmptySignature, chain ?? ChainConstants.Regtest,
                                                scid ?? s_shortChannelId, timestamp,
                                                ChannelUpdatePayload.MessageFlagMustBeOne
                                              | ChannelUpdatePayload.MessageFlagDontForward,
                                                direction ? ChannelUpdatePayload.ChannelFlagDirection : (byte)0, 80,
                                                1_000, feeBase, 1, htlcMaximumMsat);
        var signature = CreateSigner(signer ?? _peerKey).SignNodeMessage(unsigned.GetSignatureHash());
        return new ChannelUpdateMessage(unsigned.WithSignature(signature));
    }

    private ChannelModel AddChannel(ChannelState state, LightningMoney? capacity = null,
                                    FeatureSupport useScidAlias = FeatureSupport.No, bool announce = false)
    {
        var channelIdBytes = new byte[32];
        channelIdBytes[0] = (byte)(_channels.Count + 1);
        var channelParams = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Satoshis(2_500),
                                                     LightningMoney.MilliSatoshis(5_000),
                                                     LightningMoney.Satoshis(354), 30,
                                                     LightningMoney.MilliSatoshis(800_000_000), 3, false,
                                                     LightningMoney.Satoshis(354), 144, useScidAlias) with
        {
            AnnounceChannel = announce
        };
        var keySet = new ChannelKeySetModel(0, OurNodeId, OurNodeId, OurNodeId, OurNodeId, OurNodeId, OurNodeId);
        var fundingOutput = new FundingOutputInfo(capacity ?? LightningMoney.Satoshis(1_000_000), OurNodeId,
                                                  PeerNodeId);
        var channel = new ChannelModel(channelParams, new ChannelId(channelIdBytes), null, fundingOutput, true, null,
                                       null, LightningMoney.Zero, keySet, 0, 0, LightningMoney.Zero, keySet, 0,
                                       PeerNodeId, 0, state, ChannelVersion.V1)
        {
            ShortChannelId = s_shortChannelId
        };
        _channels.Add(channel);
        return channel;
    }

    private void RaiseChannelUpdated(ChannelModel channel)
    {
        _channelMemoryRepository.Raise(r => r.OnChannelUpdated += null, _channelMemoryRepository.Object,
                                       new ChannelUpdatedEventArgs(channel));
    }

    private static Mock<ISecureKeyManager> CreateKeyManager(Key key)
    {
        var keyManager = new Mock<ISecureKeyManager>();

        // A fresh copy every time: the signer wipes the private key it is handed
        keyManager.Setup(k => k.GetNodeKeyPair())
                  .Returns(() => new CryptoKeyPair(key.ToBytes(), key.PubKey.ToBytes()));
        keyManager.Setup(k => k.GetNodePubKey()).Returns(() => key.PubKey.ToBytes());
        return keyManager;
    }

    private static ILightningSigner CreateSigner(Key key)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(CreateKeyManager(key).Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddSingleton(new Mock<IChannelMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        return services.BuildServiceProvider().GetRequiredService<ILightningSigner>();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}