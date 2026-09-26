using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Gossip.Relay;

using Application.Gossip.Relay;
using Application.Gossip.Relay.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;

/// <summary>BOLT 7 plan G1-T7: the own-message path of the gossip relay.</summary>
public class GossipRelaySchedulerTests : IDisposable
{
    private static readonly ShortChannelId s_scid = new(700, 7, 1);
    private static readonly CompactPubKey s_node1 = new Key(Enumerable.Repeat((byte)0x31, 32).ToArray()).PubKey.ToBytes();
    private static readonly CompactPubKey s_node2 = new Key(Enumerable.Repeat((byte)0x32, 32).ToArray()).PubKey.ToBytes();

    private readonly List<GossipPeer> _peers = [];
    private readonly Mock<IGossipPeerDirectory> _directory = new();
    private readonly GossipRelayScheduler _relay;

    public GossipRelaySchedulerTests()
    {
        _directory.Setup(d => d.GetConnectedPeers()).Returns(() => _peers.ToList());
        _relay = new GossipRelayScheduler(_directory.Object, NullLogger<GossipRelayScheduler>.Instance,
                                          Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                          Options.Create(new GossipOptions
                                          {
                                              OwnGossipFlushInterval = TimeSpan.FromHours(1)
                                          }));
    }

    [Fact]
    public async Task Given_OurGossipQueuedInAnyOrder_When_Flushed_Then_AnnouncementUpdateNodeGoOutInThatOrder()
    {
        // Arrange (BOLT 7: a channel_announcement before its channel_update and the node_announcement)
        var sent = AddPeer(s_node2);
        _relay.EnqueueOwnNodeAnnouncement(NodeAnnouncement(100));
        _relay.EnqueueOwnChannelUpdate(ChannelUpdate(100));
        _relay.EnqueueOwnChannelAnnouncement(ChannelAnnouncement());

        // Act
        await _relay.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate, MessageTypes.NodeAnnouncement],
                     sent.Select(m => m.Type));
    }

    [Fact]
    public async Task Given_AChannelAnnouncementWithoutAnUpdate_When_Flushed_Then_ItWaitsForTheUpdate()
    {
        // Arrange (BOLT 7: MUST NOT send a channel_announcement without a channel_update)
        var sent = AddPeer(s_node2);
        _relay.EnqueueOwnChannelAnnouncement(ChannelAnnouncement());

        // Act
        await _relay.FlushAsync(TestContext.Current.CancellationToken);
        var before = sent.Count;
        _relay.EnqueueOwnChannelUpdate(ChannelUpdate(100));
        await _relay.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, before);
        Assert.Equal([MessageTypes.ChannelAnnouncement, MessageTypes.ChannelUpdate], sent.Select(m => m.Type));
    }

    [Fact]
    public async Task Given_AFlushedConnection_When_FlushedAgain_Then_NothingIsRepeatedButANewConnectionGetsAll()
    {
        // Arrange
        var first = AddPeer(s_node2);
        _relay.EnqueueOwnChannelAnnouncement(ChannelAnnouncement());
        _relay.EnqueueOwnChannelUpdate(ChannelUpdate(100));
        await _relay.FlushAsync(TestContext.Current.CancellationToken);

        // Act: the same connection, then the peer reconnects (a new peer service)
        await _relay.FlushAsync(TestContext.Current.CancellationToken);
        _peers.Clear();
        var second = AddPeer(s_node2);
        await _relay.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public async Task Given_ANewerUpdate_When_Queued_Then_ItReplacesTheOlderAndAnOlderOneIsIgnored()
    {
        // Arrange
        var sent = AddPeer(s_node2);
        _relay.EnqueueOwnChannelUpdate(ChannelUpdate(100));
        _relay.EnqueueOwnChannelUpdate(ChannelUpdate(200));
        _relay.EnqueueOwnChannelUpdate(ChannelUpdate(150));

        // Act
        await _relay.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        var update = Assert.IsType<ChannelUpdatePayload>(Assert.Single(sent).Payload);
        Assert.Equal(200u, update.Timestamp);
    }

    [Fact]
    public async Task Given_PeersOnOtherChains_When_Flushed_Then_OnlyPeersOnOurChainOrWithoutNetworksGetIt()
    {
        // Arrange (BOLT 7: SHOULD NOT forward gossip to a peer whose init networks exclude the chain)
        var mainnetOnly = AddPeer(s_node1, ChainConstants.Main);
        var anyChain = AddPeer(s_node2);
        _relay.EnqueueOwnNodeAnnouncement(NodeAnnouncement(100));

        // Act
        await _relay.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(mainnetOnly);
        Assert.Single(anyChain);
    }

    [Fact]
    public async Task Given_ASendFailure_When_FlushedAgain_Then_TheRestIsSent()
    {
        // Arrange
        var sent = new List<IMessage>();
        var fail = true;
        var service = new Mock<IPeerService>();
        service.SetupGet(s => s.Features).Returns(new FeatureOptions());
        service.Setup(s => s.SendGossipMessageAsync(It.IsAny<IMessage>())).Returns((IMessage message) =>
        {
            if (fail)
                throw new InvalidOperationException("connection closing");

            sent.Add(message);
            return Task.CompletedTask;
        });
        _peers.Add(new GossipPeer(s_node2, service.Object));
        _relay.EnqueueOwnNodeAnnouncement(NodeAnnouncement(100));

        // Act
        await _relay.FlushAsync(TestContext.Current.CancellationToken);
        fail = false;
        await _relay.FlushAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(sent);
    }

    public void Dispose() => _relay.Dispose();

    private List<IMessage> AddPeer(CompactPubKey nodeId, params ChainHash[] chains)
    {
        var sent = new List<IMessage>();
        var service = new Mock<IPeerService>();
        service.SetupGet(s => s.Features).Returns(new FeatureOptions { ChainHashes = chains });
        service.Setup(s => s.SendGossipMessageAsync(It.IsAny<IMessage>()))
               .Callback((IMessage message) => sent.Add(message))
               .Returns(Task.CompletedTask);
        _peers.Add(new GossipPeer(nodeId, service.Object));
        return sent;
    }

    private static ChannelAnnouncementPayload ChannelAnnouncement()
    {
        var signature = ChannelAnnouncementPayload.EmptySignature;
        return new ChannelAnnouncementPayload(signature, signature, signature, signature, ReadOnlyMemory<byte>.Empty,
                                              ChainConstants.Regtest, s_scid, s_node1, s_node2, s_node1, s_node2);
    }

    private static ChannelUpdatePayload ChannelUpdate(uint timestamp) =>
        new(ChannelUpdatePayload.EmptySignature, ChainConstants.Regtest, s_scid, timestamp,
            ChannelUpdatePayload.MessageFlagMustBeOne, 0, 40, 1_000, 1_000, 100, 990_000_000);

    private static NodeAnnouncementPayload NodeAnnouncement(uint timestamp) =>
        new(NodeAnnouncementPayload.EmptySignature, ReadOnlyMemory<byte>.Empty, timestamp, s_node1, new byte[3],
            new byte[NodeAnnouncementPayload.AliasLength], ReadOnlyMemory<byte>.Empty);
}