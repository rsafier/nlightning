namespace NLightning.Application.Tests.OnionMessages;

using Application.Tests.Node.PeerStorage;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Enums;
using Harness;

/// <summary>
/// What our channels change (BOLT 4 reader OM-R-05, plan D7, OG8): a <c>short_channel_id</c> hop resolved through
/// our channel's real SCID or local alias, a <c>sciddir_or_pubkey</c> introduction node resolved through an
/// unannounced channel of ours, and a reply path introduced by a peer we have a channel with.
/// </summary>
public sealed class OnionMessageChannelTests
{
    private const ulong TestType = 65;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ABobHopNamingHisChannelToCarol_When_Forwarding_Then_ResolvedByRealScidOrLocalAlias(
        bool byAlias)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(TestType);
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        var realScid = new ShortChannelId(800_000, 2, 0);
        var alias = new ShortChannelId(16_000_000, 1, 1);
        var channel = PeerStorageTestContext.CreateChannel(carol.NodeId, ChannelState.Open);
        channel.ShortChannelId = realScid;
        channel.LocalAliases = [alias];
        bob.Channels.Add(channel);
        var path = carol.PathFactory.Create([bob.NodeId, carol.NodeId],
        [
            new BlindedRecipientData { ShortChannelId = byAlias ? alias : realScid },
            new BlindedRecipientData()
        ]);

        // Act
        var result = await alice.Service.SendAsync(
                         OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(path)),
                         OnionMessageContents.Single(TestType, new byte[] { 1 }), null, ct);
        await handler.WaitForAsync(1, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.Sent, result.Status);
        Assert.Equal(1, bob.Metrics.Forwarded);
    }

    [Fact]
    public async Task Given_AnUnknownScid_When_BobForwards_Then_NoNextHop()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2);
        OnionMessageTestNode.Connect(alice, bob);
        var path = alice.PathFactory.Create([bob.NodeId, alice.NodeId],
        [
            new BlindedRecipientData { ShortChannelId = new ShortChannelId(1, 1, 1) },
            new BlindedRecipientData()
        ]);

        // Act
        await alice.Service.SendAsync(OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(path)),
                                      OnionMessageContents.Single(TestType, new byte[] { 1 }), null, ct);

        // Assert
        await OnionMessageTestWaits.UntilAsync(
            () => bob.Metrics.GetDropped(Application.OnionMessages.OnionMessageDropReasons.NoNextHop) == 1, ct);
    }

    [Fact]
    public async Task Given_ASciddirOfOurUnannouncedChannel_When_AliceSends_Then_TheIntroductionIsBob()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(TestType);
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        var scid = new ShortChannelId(800_001, 7, 0);
        var channel = PeerStorageTestContext.CreateChannel(bob.NodeId, ChannelState.Open);
        channel.ShortChannelId = scid;
        alice.Channels.Add(channel);
        var bobDirection = (byte)(GraphChannel.CompareNodeIds(bob.NodeId, alice.NodeId) < 0 ? 0 : 1);
        var path = carol.PathFactory.Create([bob.NodeId, carol.NodeId]);
        var wirePath = new WireBlindedPath(SciddirOrPubkey.FromShortChannelId(scid, bobDirection), path.FirstPathKey,
                                           path.Hops);

        // Act
        var result = await alice.Service.SendAsync(OnionMessageDestination.ToBlindedPath(wirePath),
                                                   OnionMessageContents.Single(TestType, new byte[] { 1 }), null, ct);
        await handler.WaitForAsync(1, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.Sent, result.Status);
        Assert.Single(alice.LinkTo(bob).Sent);
    }

    [Fact]
    public async Task Given_TwoPeersOneWithAChannel_When_AliceWaitsForAReply_Then_HerReplyPathStartsAtTheChannelPeer()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(TestType)
        {
            ReplyWith = OnionMessageContents.Single(67, new byte[] { 2 })
        };
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        using var dave = new OnionMessageTestNode("dave", 4);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(alice, dave);
        OnionMessageTestNode.Connect(bob, carol);
        OnionMessageTestNode.Connect(dave, carol);
        alice.Channels.Add(PeerStorageTestContext.CreateChannel(dave.NodeId, ChannelState.Open));
        var path = carol.PathFactory.Create([bob.NodeId, carol.NodeId]);

        // Act
        var result = await alice.Service.SendAndWaitForReplyAsync(
                         OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(path)),
                         OnionMessageContents.Single(TestType, new byte[] { 1 }), [67], TimeSpan.FromSeconds(10), ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.Replied, result.Status);
        Assert.Equal(dave.NodeId, Assert.Single(handler.Received).ReplyPath!.FirstNode.NodeId);
        Assert.Equal(dave.NodeId, result.Reply!.FromPeer);
        Assert.Equal(1, dave.Metrics.Forwarded);
    }

    [Fact]
    public async Task Given_TheDestinationIsTheOnlyPeer_When_AliceWaitsForAReply_Then_TheDestinationReadsItsOwnReplyPathHop()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(TestType)
        {
            ReplyWith = OnionMessageContents.Single(67, new byte[] { 2 })
        };
        using var alice = new OnionMessageTestNode("alice", 1);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        OnionMessageTestNode.Connect(alice, carol);

        // Act
        var result = await alice.Service.SendAndWaitForReplyAsync(OnionMessageDestination.ToNode(carol.NodeId),
                                                                  OnionMessageContents.Single(TestType,
                                                                      new byte[] { 1 }), [67],
                                                                  TimeSpan.FromSeconds(10), ct);

        // Assert: the only peer is Carol, so the reply path starts at Carol, who reads her own hop
        Assert.Equal(OnionMessageSendStatus.Replied, result.Status);
        Assert.Equal(carol.NodeId, Assert.Single(handler.Received).ReplyPath!.FirstNode.NodeId);
    }
}