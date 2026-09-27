using System.Security.Cryptography;

namespace NLightning.Application.Tests.OnionMessages;

using Application.Gossip.Graph.Interfaces;
using Application.OnionMessages;
using Application.Tests.Payments;
using Application.Tests.Payments.Switch;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Enums;
using Harness;

/// <summary>
/// Plan OM2-T5: three in-process nodes (Alice - Bob - Carol, no channels) with the production
/// <see cref="OnionMessageService"/>, real Sphinx and route blinding: forward through a blinded path with Bob as the
/// introduction node, a reply over Alice's reply path, a 32 KiB message, Bob's rate limit, and delivery by payload
/// type.
/// </summary>
public sealed class OnionMessageHarnessTests
{
    private const ulong RequestType = 65;
    private const ulong ReplyType = 67;

    [Fact]
    public async Task Given_CarolsBlindedPathThroughBob_When_AliceSends_Then_BobForwardsAndCarolsHandlerGetsIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(RequestType);
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        byte[] pathId = [1, 2, 3, 4];
        var path = carol.PathFactory.Create([bob.NodeId, carol.NodeId], pathId);

        // Act
        var result = await alice.Service.SendAsync(
                         OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(path)),
                         OnionMessageContents.Single(RequestType, "hello"u8.ToArray()), null, ct);
        await handler.WaitForAsync(1, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.Sent, result.Status);
        var received = Assert.Single(handler.Received);
        var record = Assert.Single(received.Contents.Records);
        Assert.Equal(RequestType, record.Type);
        Assert.Equal("hello"u8.ToArray(), record.Value.ToArray());
        Assert.Equal(pathId, received.PathId!.Value.ToArray());
        Assert.Equal(bob.NodeId, received.FromPeer);
        Assert.Null(received.ReplyPath);
        Assert.Equal(1, bob.Metrics.Forwarded);
        Assert.Equal(1, alice.Metrics.Sent);
        // The small packet: 1300 bytes of payloads (len = 1366)
        Assert.Equal(1366, Assert.Single(alice.LinkTo(bob).Sent).Payload.OnionMessagePacket.Length);
        Assert.Equal(1366, Assert.Single(bob.LinkTo(carol).Sent).Payload.OnionMessagePacket.Length);
    }

    [Fact]
    public async Task Given_ARequestWithAReplyPath_When_CarolReplies_Then_AliceGetsTheReplyThroughBob()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(RequestType)
        {
            ReplyWith = OnionMessageContents.Single(ReplyType, "pong"u8.ToArray())
        };
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        var path = carol.PathFactory.Create([bob.NodeId, carol.NodeId]);

        // Act
        var result = await alice.Service.SendAndWaitForReplyAsync(
                         OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(path)),
                         OnionMessageContents.Single(RequestType, "ping"u8.ToArray()), [ReplyType],
                         TimeSpan.FromSeconds(10), ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.Replied, result.Status);
        var reply = result.Reply!;
        var record = Assert.Single(reply.Contents.Records);
        Assert.Equal(ReplyType, record.Type);
        Assert.Equal("pong"u8.ToArray(), record.Value.ToArray());
        Assert.Equal(bob.NodeId, reply.FromPeer);
        Assert.Equal(PendingReplyRegistry.PathIdLength, reply.PathId!.Value.Length);
        // Carol got a reply path whose introduction node is Bob, Alice's only peer (plan D7)
        var replyPath = Assert.Single(handler.Received).ReplyPath!;
        Assert.Equal(bob.NodeId, replyPath.FirstNode.NodeId);
        Assert.Equal(2, replyPath.Hops.Count);
        Assert.Equal(2, bob.Metrics.Forwarded);
        Assert.Equal(1, alice.Metrics.GetDelivered("reply"));
        Assert.Equal(0, alice.Service.PendingReplies);
    }

    [Fact]
    public async Task Given_A32KiBMessage_When_AliceSendsItThroughBob_Then_CarolGetsItWholeInALargePacket()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(RequestType);
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        var path = carol.PathFactory.Create([bob.NodeId, carol.NodeId]);
        var payload = RandomNumberGenerator.GetBytes(30_000);

        // Act
        var result = await alice.Service.SendAsync(
                         OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(path)),
                         OnionMessageContents.Single(RequestType, payload), null, ct);
        await handler.WaitForAsync(1, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.Sent, result.Status);
        Assert.Equal(payload, Assert.Single(Assert.Single(handler.Received).Contents.Records).Value.ToArray());
        // 32768 bytes of payloads (len = 32834) on both links
        Assert.Equal(32834, Assert.Single(alice.LinkTo(bob).Sent).Payload.OnionMessagePacket.Length);
        Assert.Equal(32834, Assert.Single(bob.LinkTo(carol).Sent).Payload.OnionMessagePacket.Length);
    }

    [Fact]
    public async Task Given_MoreThanFitsTheLargePacket_When_AliceSends_Then_TooLargeAndNothingSent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2);
        OnionMessageTestNode.Connect(alice, bob);

        // Act
        var result = await alice.Service.SendAsync(OnionMessageDestination.ToNode(bob.NodeId),
                                                   OnionMessageContents.Single(RequestType, new byte[32_750]), null,
                                                   ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.TooLarge, result.Status);
        Assert.Empty(alice.LinkTo(bob).Sent);
    }

    [Fact]
    public async Task Given_ARateLimitOf20PerSecondAtBob_When_AliceSends25InOneSecond_Then_BobDropsTheLast5()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var clock = new FrozenTimeProvider(DateTimeOffset.UnixEpoch.AddDays(20_000));
        var handler = new RecordingHandler(RequestType);
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2, rateLimiter: new MessageCountRateLimiter(20, clock));
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        var destination = OnionMessageDestination.ToBlindedPath(
            WireBlindedPath.FromBlindedPath(carol.PathFactory.Create([bob.NodeId, carol.NodeId])));

        // Act
        for (var i = 0; i < 25; i++)
        {
            var result = await alice.Service.SendAsync(destination, OnionMessageContents.Single(RequestType, new byte[] { (byte)i }),
                                                       null, ct);
            Assert.Equal(OnionMessageSendStatus.Sent, result.Status);
        }

        await handler.WaitForAsync(20, ct);
        clock.Advance(TimeSpan.FromSeconds(1));
        await alice.Service.SendAsync(destination, OnionMessageContents.Single(RequestType, new byte[] { 99 }), null, ct);
        await handler.WaitForAsync(21, ct);

        // Assert
        Assert.Equal(5, bob.Metrics.GetDropped(OnionMessageDropReasons.RateLimited));
        Assert.Equal(21, bob.Metrics.Received);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (byte)i).Append((byte)99),
                     handler.Received.Select(m => m.Contents.Records[0].Value.Span[0]));
    }

    [Fact]
    public async Task Given_HandlersForTwoTypes_When_MessagesOfThreeTypesArrive_Then_EachGoesToItsHandlerOrIsDropped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var invoiceRequests = new RecordingHandler(64);
        var tests = new RecordingHandler(RequestType);
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2, [invoiceRequests, tests]);
        OnionMessageTestNode.Connect(alice, bob);
        var bobNode = OnionMessageDestination.ToNode(bob.NodeId);

        // Act
        await alice.Service.SendAsync(bobNode, OnionMessageContents.Single(64, new byte[] { 1 }), null, ct);
        await alice.Service.SendAsync(bobNode, OnionMessageContents.Single(RequestType, new byte[] { 2 }), null, ct);
        await alice.Service.SendAsync(bobNode, OnionMessageContents.Single(69, new byte[] { 3 }), null, ct);
        await alice.Service.SendAsync(bobNode, new OnionMessageContents([]), null, ct);
        await invoiceRequests.WaitForAsync(1, ct);
        await tests.WaitForAsync(1, ct);
        await OnionMessageTestWaits.UntilAsync(() => bob.Metrics.GetDropped(OnionMessageDropReasons.NoHandler) == 1
                                                  && bob.Metrics.GetDelivered("empty") == 1, ct);

        // Assert
        Assert.Equal(64UL, Assert.Single(Assert.Single(invoiceRequests.Received).Contents.Records).Type);
        Assert.Equal(RequestType, Assert.Single(Assert.Single(tests.Received).Contents.Records).Type);
        // Sent by node id, so no path_id: the path is the sender's own
        Assert.Null(invoiceRequests.Received[0].PathId);
        Assert.Equal(2, bob.Metrics.GetDelivered("handler"));
    }

    [Fact]
    public async Task Given_APathWhoseIntroductionNodeIsAlice_When_AliceSends_Then_SheReadsHerOwnHopAndSendsToBob()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(RequestType);
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        var path = carol.PathFactory.Create([alice.NodeId, bob.NodeId, carol.NodeId], new byte[] { 9 });

        // Act
        var result = await alice.Service.SendAsync(
                         OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(path)),
                         OnionMessageContents.Single(RequestType, new byte[] { 7 }), null, ct);
        await handler.WaitForAsync(1, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.Sent, result.Status);
        Assert.Equal(new byte[] { 9 }, Assert.Single(handler.Received).PathId!.Value.ToArray());
        Assert.Single(alice.LinkTo(bob).Sent);
        Assert.Equal(1, bob.Metrics.Forwarded);
    }

    [Fact]
    public async Task Given_AGraphPathToCarol_When_AliceSendsToHerNodeId_Then_ItGoesThroughBob()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(RequestType);
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        var graph = GraphOf(new ShortChannelId(1, 1, 0), bob.NodeId, carol.NodeId, advertise: true);
        using var alice = new OnionMessageTestNode("alice", 1, graphStore: graph);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);

        // Act
        var result = await alice.Service.SendAsync(OnionMessageDestination.ToNode(carol.NodeId),
                                                   OnionMessageContents.Single(RequestType, new byte[] { 5 }), null, ct);
        await handler.WaitForAsync(1, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.Sent, result.Status);
        Assert.Equal(bob.NodeId, Assert.Single(handler.Received).FromPeer);
        Assert.Equal(1, bob.Metrics.Forwarded);
    }

    [Fact]
    public async Task Given_ACarolThatDoesNotAdvertiseOnionMessagesInTheGraph_When_AliceSendsToHer_Then_NoPath()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3);
        var graph = GraphOf(new ShortChannelId(1, 1, 0), bob.NodeId, carol.NodeId, advertise: false);
        using var alice = new OnionMessageTestNode("alice", 1, graphStore: graph);
        OnionMessageTestNode.Connect(alice, bob);

        // Act
        var result = await alice.Service.SendAsync(OnionMessageDestination.ToNode(carol.NodeId),
                                                   OnionMessageContents.Single(RequestType, new byte[] { 5 }), null, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.NoPath, result.Status);
        Assert.Empty(alice.LinkTo(bob).Sent);
    }

    [Fact]
    public async Task Given_APathWhoseIntroductionIsASciddirOfTheGraph_When_AliceSends_Then_ItReachesCarol()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(RequestType);
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        var scid = new ShortChannelId(700_000, 5, 1);
        using var alice = new OnionMessageTestNode("alice", 1,
                                                   graphStore: GraphOf(scid, bob.NodeId, carol.NodeId, true));
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        var path = carol.PathFactory.Create([bob.NodeId, carol.NodeId]);
        var bobDirection = (byte)(GraphChannel.CompareNodeIds(bob.NodeId, carol.NodeId) < 0 ? 0 : 1);
        var wirePath = new WireBlindedPath(SciddirOrPubkey.FromShortChannelId(scid, bobDirection), path.FirstPathKey,
                                           path.Hops);

        // Act
        var result = await alice.Service.SendAsync(OnionMessageDestination.ToBlindedPath(wirePath),
                                                   OnionMessageContents.Single(RequestType, new byte[] { 5 }), null, ct);
        await handler.WaitForAsync(1, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.Sent, result.Status);
        Assert.Single(handler.Received);
    }

    [Fact]
    public async Task Given_BobsPathHopNamesTheNextNodeByScid_When_Forwarding_Then_BobResolvesItThroughHisChannel()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(RequestType);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        using var alice = new OnionMessageTestNode("alice", 1);
        var scid = new ShortChannelId(800_000, 1, 0);
        // Bob's own announced channel to Carol
        using var bob = new OnionMessageTestNode("bob", 2,
                                                 graphStore: GraphOf(scid, new TestNodeKeyManager(2).NodeId, carol.NodeId,
                                                                     true));
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        var path = carol.PathFactory.Create(
            [bob.NodeId, carol.NodeId],
            [
                new Domain.Protocol.Onion.Models.BlindedRecipientData { ShortChannelId = scid },
                new Domain.Protocol.Onion.Models.BlindedRecipientData()
            ]);

        // Act
        var result = await alice.Service.SendAsync(
                         OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(path)),
                         OnionMessageContents.Single(RequestType, new byte[] { 5 }), null, ct);
        await handler.WaitForAsync(1, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.Sent, result.Status);
        Assert.Equal(1, bob.Metrics.Forwarded);
    }

    [Fact]
    public async Task Given_NoReply_When_TheDeadlinePasses_Then_ReplyTimedOutAndALateReplyIsIgnored()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var clock = new SteppedTimeProvider();
        var handler = new RecordingHandler(RequestType);
        using var alice = new OnionMessageTestNode("alice", 1, timeProvider: clock);
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        var path = carol.PathFactory.Create([bob.NodeId, carol.NodeId]);

        // Act
        var pending = alice.Service.SendAndWaitForReplyAsync(
            OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(path)),
            OnionMessageContents.Single(RequestType, new byte[] { 1 }), [ReplyType], TimeSpan.FromSeconds(30), ct);
        await handler.WaitForAsync(1, ct);
        await OnionMessageTestWaits.UntilAsync(() => clock.PendingTimers > 0, ct);
        clock.Advance(TimeSpan.FromSeconds(31));
        var result = await pending;
        // The reply comes too late
        await carol.Service.SendAsync(OnionMessageDestination.ToBlindedPath(handler.Received[0].ReplyPath!),
                                      OnionMessageContents.Single(ReplyType, new byte[] { 2 }), null, ct);
        await OnionMessageTestWaits.UntilAsync(
            () => alice.Metrics.GetDropped(OnionMessageDropReasons.UnexpectedReply) == 1, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.ReplyTimedOut, result.Status);
        Assert.Null(result.Reply);
        Assert.Equal(0, alice.Service.PendingReplies);
        Assert.Equal(0, alice.Metrics.GetDelivered("reply"));
    }

    [Fact]
    public async Task Given_AReplyOfAnUnexpectedType_When_ItArrives_Then_AliceIgnoresItAndKeepsWaiting()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var clock = new SteppedTimeProvider();
        var handler = new RecordingHandler(RequestType)
        {
            ReplyWith = OnionMessageContents.Single(69, new byte[] { 3 })
        };
        using var alice = new OnionMessageTestNode("alice", 1, timeProvider: clock);
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3, [handler]);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        var path = carol.PathFactory.Create([bob.NodeId, carol.NodeId]);

        // Act
        var pending = alice.Service.SendAndWaitForReplyAsync(
            OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(path)),
            OnionMessageContents.Single(RequestType, new byte[] { 1 }), [ReplyType], TimeSpan.FromSeconds(30), ct);
        await OnionMessageTestWaits.UntilAsync(
            () => alice.Metrics.GetDropped(OnionMessageDropReasons.UnexpectedReply) == 1, ct);
        Assert.False(pending.IsCompleted);
        await OnionMessageTestWaits.UntilAsync(() => clock.PendingTimers > 0, ct);
        clock.Advance(TimeSpan.FromSeconds(31));
        var result = await pending;

        // Assert
        Assert.Equal(OnionMessageSendStatus.ReplyTimedOut, result.Status);
    }

    [Fact]
    public async Task Given_OnionMessagesNotAdvertised_When_SendingOrReceiving_Then_NotAvailableAndDropped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(RequestType);
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2, [handler], advertiseOnionMessages: false);
        OnionMessageTestNode.Connect(alice, bob);

        // Act
        var bobSends = await bob.Service.SendAsync(OnionMessageDestination.ToNode(alice.NodeId),
                                                   OnionMessageContents.Single(RequestType, new byte[] { 1 }), null, ct);
        var bobWaits = await bob.Service.SendAndWaitForReplyAsync(OnionMessageDestination.ToNode(alice.NodeId),
                                                                  OnionMessageContents.Single(RequestType, new byte[] { 1 }),
                                                                  [ReplyType], TimeSpan.FromSeconds(1), ct);
        var aliceSends = await alice.Service.SendAsync(OnionMessageDestination.ToNode(bob.NodeId),
                                                       OnionMessageContents.Single(RequestType, new byte[] { 1 }), null, ct);

        // Assert
        Assert.False(bob.Service.IsAvailable);
        Assert.Equal(OnionMessageSendStatus.NotAvailable, bobSends.Status);
        Assert.Equal(OnionMessageSendStatus.NotAvailable, bobWaits.Status);
        // The connection says onion messages, so Alice sends; Bob drops it
        Assert.Equal(OnionMessageSendStatus.Sent, aliceSends.Status);
        await OnionMessageTestWaits.UntilAsync(
            () => bob.Metrics.GetDropped(OnionMessageDropReasons.NotAvailable) == 1, ct);
        Assert.Empty(handler.Received);
    }

    [Fact]
    public async Task Given_NoConnectedPath_When_Sending_Then_NoPath()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2);
        using var carol = new OnionMessageTestNode("carol", 3);
        OnionMessageTestNode.Connect(alice, bob, onionMessages: false);
        var viaBob = carol.PathFactory.Create([bob.NodeId, carol.NodeId]);
        var unknownScid = new WireBlindedPath(SciddirOrPubkey.FromShortChannelId(new ShortChannelId(1, 2, 3), 0),
                                              viaBob.FirstPathKey, viaBob.Hops);

        // Act
        var toCarol = await alice.Service.SendAsync(OnionMessageDestination.ToNode(carol.NodeId),
                                                    OnionMessageContents.Single(RequestType, new byte[] { 1 }), null, ct);
        var toSelf = await alice.Service.SendAsync(OnionMessageDestination.ToNode(alice.NodeId),
                                                   OnionMessageContents.Single(RequestType, new byte[] { 1 }), null, ct);
        var throughBob = await alice.Service.SendAsync(
                             OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(viaBob)),
                             OnionMessageContents.Single(RequestType, new byte[] { 1 }), null, ct);
        var throughScid = await alice.Service.SendAsync(OnionMessageDestination.ToBlindedPath(unknownScid),
                                                        OnionMessageContents.Single(RequestType, new byte[] { 1 }), null, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.NoPath, toCarol.Status);
        Assert.Equal(OnionMessageSendStatus.NoPath, toSelf.Status);
        // Bob is connected but did not negotiate onion messages
        Assert.Equal(OnionMessageSendStatus.NoPath, throughBob.Status);
        Assert.Equal(OnionMessageSendStatus.NoPath, throughScid.Status);
        Assert.Empty(alice.LinkTo(bob).Sent);
    }

    [Fact]
    public async Task Given_TheFirstHopRefusesTheMessage_When_Sending_Then_Dropped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2);
        OnionMessageTestNode.Connect(alice, bob);
        alice.LinkTo(bob).RefuseSends = true;

        // Act
        var result = await alice.Service.SendAsync(OnionMessageDestination.ToNode(bob.NodeId),
                                                   OnionMessageContents.Single(RequestType, new byte[] { 1 }), null, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.Dropped, result.Status);
        Assert.Equal(0, alice.Metrics.Sent);
        Assert.Equal(1, alice.Metrics.GetDropped(OnionMessageDropReasons.OutboxFull));
    }

    [Fact]
    public async Task Given_APeerThatStopsReading_When_BobForwardsToIt_Then_OnlyItsMessagesAreDroppedAndOthersFlow()
    {
        // Arrange: Bob forwards for Alice to Carol and Dave; Carol stops reading (a full TCP window), so a socket
        // write to her never completes. Plan §3.4: a slow reader only loses onion messages; Bob's single worker must
        // never wait on her connection
        var ct = TestContext.Current.CancellationToken;
        const int toCarol = 10;
        const int bobOutboxCap = 2;
        var carolHandler = new RecordingHandler(RequestType);
        var daveHandler = new RecordingHandler(RequestType);
        using var alice = new OnionMessageTestNode("alice", 1);
        using var bob = new OnionMessageTestNode("bob", 2,
                                                 options: new OnionMessageOptions { MaxOutboxPerPeer = bobOutboxCap });
        using var carol = new OnionMessageTestNode("carol", 3, [carolHandler]);
        using var dave = new OnionMessageTestNode("dave", 4, [daveHandler]);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        OnionMessageTestNode.Connect(bob, dave);
        bob.LinkTo(carol).Stalled = true;
        var carolPath = WireBlindedPath.FromBlindedPath(carol.PathFactory.Create([bob.NodeId, carol.NodeId]));
        var davePath = WireBlindedPath.FromBlindedPath(dave.PathFactory.Create([bob.NodeId, dave.NodeId]));

        // Act: fill Carol's outbox at Bob and beyond (one message in the stalled write, the cap queued, the rest
        // refused), then send to Dave through the same Bob
        for (var i = 0; i < toCarol; i++)
        {
            var sent = await alice.Service.SendAsync(OnionMessageDestination.ToBlindedPath(carolPath),
                                                     OnionMessageContents.Single(RequestType, new[] { (byte)i }), null, ct);
            Assert.Equal(OnionMessageSendStatus.Sent, sent.Status);
        }

        await OnionMessageTestWaits.UntilAsync(
            () => bob.Metrics.Forwarded + bob.Metrics.GetDropped(OnionMessageDropReasons.OutboxFull) == toCarol, ct);
        var heldForCarol = (int)bob.Metrics.Forwarded;
        var toDave = await alice.Service.SendAsync(OnionMessageDestination.ToBlindedPath(davePath),
                                                   OnionMessageContents.Single(RequestType, "dave"u8.ToArray()), null,
                                                   ct);
        await daveHandler.WaitForAsync(1, ct);

        // Assert: Dave got his message while Carol's link is still stalled; nothing was lost to Bob's inbound queue
        Assert.Equal(OnionMessageSendStatus.Sent, toDave.Status);
        Assert.Equal("dave"u8.ToArray(), Assert.Single(daveHandler.Received).Contents.Records[0].Value.ToArray());
        Assert.Empty(carolHandler.Received);
        Assert.Equal(0, bob.Metrics.GetDropped(OnionMessageDropReasons.QueueFull));
        // At most the cap queued plus the one in the stalled write
        Assert.InRange(heldForCarol, bobOutboxCap, bobOutboxCap + 1);
        Assert.Equal(toCarol - heldForCarol, bob.Metrics.GetDropped(OnionMessageDropReasons.OutboxFull));

        // Once Carol reads again, what Bob held for her goes out
        bob.LinkTo(carol).Stalled = false;
        await carolHandler.WaitForAsync(heldForCarol, ct);
        Assert.Equal(heldForCarol, carolHandler.Received.Count);
    }

    private static IGraphStore GraphOf(ShortChannelId scid, CompactPubKey a, CompactPubKey b, bool advertise)
    {
        var (first, second) = GraphChannel.CompareNodeIds(a, b) < 0 ? (a, b) : (b, a);
        var channel = new GraphChannel(scid, first, second, first, second, 100_000);
        // Bit 39 (option_onion_messages optional) in a big-endian 5-byte bitmap
        byte[] features = advertise ? [0x80, 0, 0, 0, 0] : [0];
        var nodes = new[] { a, b }.Select(id => new GraphNode(id, 1, features, new byte[32], new byte[3]));
        var snapshot = new GraphSnapshot([channel], nodes);
        var store = new Mock<IGraphStore>();
        store.SetupGet(s => s.IsLoaded).Returns(true);
        store.Setup(s => s.GetSnapshot()).Returns(snapshot);
        return store.Object;
    }
}