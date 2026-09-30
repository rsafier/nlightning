namespace NLightning.Application.Tests.OnionMessages;

using Application.OnionMessages;
using Application.Payments.Routing;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Enums;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Payloads;
using Harness;

/// <summary>
/// The BOLT 4 onion-message reader rules (plan §1.3 OM-R-02..OM-R-07, OM2-T3) on the harness nodes: every ignored
/// message is dropped with its reason and nothing is forwarded or delivered; the forwarded path key honours
/// <c>next_path_key_override</c>.
/// </summary>
public sealed class OnionMessageServiceTests : IDisposable
{
    private const ulong TestType = 65;

    private readonly RecordingHandler _carolHandler = new(TestType, 64);
    private readonly OnionMessageTestNode _alice;
    private readonly OnionMessageTestNode _bob;
    private readonly OnionMessageTestNode _carol;

    public OnionMessageServiceTests()
    {
        _alice = new OnionMessageTestNode("alice", 1);
        _bob = new OnionMessageTestNode("bob", 2);
        _carol = new OnionMessageTestNode("carol", 3, [_carolHandler]);
        OnionMessageTestNode.Connect(_alice, _bob);
        OnionMessageTestNode.Connect(_bob, _carol);
    }

    public void Dispose()
    {
        _alice.Dispose();
        _bob.Dispose();
        _carol.Dispose();
    }

    [Fact]
    public async Task Given_ANonFinalHopWithAnExtraField_When_BobReadsIt_Then_Ignored()
    {
        // Arrange
        var path = _carol.PathBuilder.CreateMessagePath([_bob.NodeId, _carol.NodeId]);
        var message = Craft(path, (i, hop) => i == 0
                                                  ? Tlvs(hop, [new OnionMessageTlvRecord(TestType, new byte[] { 1 })])
                                                  : Tlvs(hop, [Record(TestType)]));

        // Act
        await AliceSendsToBobAsync(message);

        // Assert
        await ExpectBobDropAsync(OnionMessageDropReasons.NonFinalExtraFields);
    }

    [Fact]
    public async Task Given_ANonFinalHopWithAPathId_When_BobReadsIt_Then_Ignored()
    {
        // Arrange
        var path = _carol.Raw.CreatePath([_bob.NodeId, _carol.NodeId],
        [
            new BlindedRecipientData { NextNodeId = _carol.NodeId, PathId = new byte[] { 1 } },
            new BlindedRecipientData()
        ]);

        // Act
        await AliceSendsToBobAsync(CraftStandard(path));

        // Assert
        await ExpectBobDropAsync(OnionMessageDropReasons.NonFinalPathId);
    }

    [Fact]
    public async Task Given_AllowedFeaturesWithABit_When_BobReadsIt_Then_Ignored()
    {
        // Arrange
        var path = _carol.PathBuilder.CreatePath([_bob.NodeId, _carol.NodeId],
        [
            new BlindedRecipientData { NextNodeId = _carol.NodeId, AllowedFeatures = new byte[] { 0x01 } },
            new BlindedRecipientData()
        ]);

        // Act
        await AliceSendsToBobAsync(CraftStandard(path));

        // Assert
        await ExpectBobDropAsync(OnionMessageDropReasons.ForbiddenRecipientData);
    }

    [Fact]
    public async Task Given_PaymentRelayInAMessagePath_When_BobReadsIt_Then_Ignored()
    {
        // Arrange (a path whose creator broke the message-path rules; our factory refuses to make one)
        var blinding = _carol.RouteBlinding;
        var path = blinding.CreateBlindedPath(
            [_bob.NodeId, _carol.NodeId],
            [
                blinding.EncodeRecipientData(new BlindedRecipientData
                {
                    NextNodeId = _carol.NodeId,
                    PaymentRelay = new BlindedPaymentRelay(40, 100, 1000)
                }),
                blinding.EncodeRecipientData(new BlindedRecipientData())
            ], new PrivKey(PaymentOnionFactory.CreateSessionKey()));

        // Act
        await AliceSendsToBobAsync(CraftStandard(path));

        // Assert
        await ExpectBobDropAsync(OnionMessageDropReasons.ForbiddenRecipientData);
        Assert.Throws<ArgumentException>(() => _carol.PathBuilder.CreatePath(
                                             [_carol.NodeId],
                                             [
                                                 new BlindedRecipientData
                                                 {
                                                     PaymentRelay = new BlindedPaymentRelay(40, 100, 1000)
                                                 }
                                             ]));
    }

    [Fact]
    public async Task Given_AFinalHopWithTwoPayloadFields_When_CarolReadsIt_Then_Ignored()
    {
        // Arrange
        var path = _carol.PathBuilder.CreateMessagePath([_bob.NodeId, _carol.NodeId]);
        var message = Craft(path, (i, hop) => i == 0 ? Tlvs(hop, []) : Tlvs(hop, [Record(64), Record(TestType)]));

        // Act
        await AliceSendsToBobAsync(message);

        // Assert
        await ExpectDropAsync(_carol, OnionMessageDropReasons.MultiplePayloadFields);
        Assert.Empty(_carolHandler.Received);
    }

    [Fact]
    public async Task Given_AnUnknownEvenTypeAtTheFinalHop_When_CarolReadsIt_Then_Ignored()
    {
        // Arrange
        var path = _carol.PathBuilder.CreateMessagePath([_bob.NodeId, _carol.NodeId]);
        var message = Craft(path, (i, hop) => i == 0 ? Tlvs(hop, []) : Tlvs(hop, [Record(70)]));

        // Act
        await AliceSendsToBobAsync(message);

        // Assert
        await ExpectDropAsync(_carol, OnionMessageDropReasons.InvalidPayload);
    }

    [Fact]
    public async Task Given_AnUnknownOddTypeBelow64AtTheFinalHop_When_CarolReadsIt_Then_KeptWithThePayloadField()
    {
        // Arrange
        var path = _carol.PathBuilder.CreateMessagePath([_bob.NodeId, _carol.NodeId]);
        var message = Craft(path, (i, hop) => i == 0 ? Tlvs(hop, []) : Tlvs(hop, [Record(33), Record(TestType)]));

        // Act
        await AliceSendsToBobAsync(message);
        await _carolHandler.WaitForAsync(1, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([33UL, TestType], _carolHandler.Received[0].Contents.Records.Select(r => r.Type));
    }

    [Fact]
    public async Task Given_AHopWithoutEncryptedRecipientData_When_BobReadsIt_Then_Ignored()
    {
        // Arrange
        var path = _carol.PathBuilder.CreateMessagePath([_bob.NodeId, _carol.NodeId]);
        var message = Craft(path, (i, hop) => i == 0
                                                  ? OnionMessageTlvsCodec.Encode(new OnionMessageTlvs(null, null, []))
                                                  : Tlvs(hop, [Record(TestType)]));

        // Act
        await AliceSendsToBobAsync(message);

        // Assert
        await ExpectBobDropAsync(OnionMessageDropReasons.InvalidRecipientData);
    }

    [Fact]
    public async Task Given_AFlippedBit_When_BobPeels_Then_IgnoredWithoutAnyReply()
    {
        // Arrange
        var path = _carol.PathBuilder.CreateMessagePath([_bob.NodeId, _carol.NodeId]);
        var message = CraftStandard(path);
        var packet = message.Payload.OnionMessagePacket.ToArray();
        packet[100] ^= 0x01;
        var corrupted = new OnionMessageMessage(new OnionMessagePayload(message.Payload.PathKey, packet));

        // Act
        await AliceSendsToBobAsync(corrupted);

        // Assert
        await ExpectBobDropAsync(OnionMessageDropReasons.Undecryptable);
        Assert.Empty(_bob.LinkTo(_alice).Sent);
    }

    [Fact]
    public async Task Given_ANextNodeThatIsNotConnected_When_BobForwards_Then_Dropped()
    {
        // Arrange
        var dave = new Payments.TestNodeKeyManager(4).NodeId;
        var path = _carol.PathBuilder.CreateMessagePath([_bob.NodeId, dave]);

        // Act
        await AliceSendsToBobAsync(CraftStandard(path));

        // Assert
        await ExpectBobDropAsync(OnionMessageDropReasons.NextPeerUnreachable);
    }

    [Fact]
    public async Task Given_ANextPeerWithoutOnionMessages_When_BobForwards_Then_Dropped()
    {
        // Arrange
        _bob.RemovePeer(_carol);
        _carol.RemovePeer(_bob);
        OnionMessageTestNode.Connect(_bob, _carol, onionMessages: false);
        var path = _carol.PathBuilder.CreateMessagePath([_bob.NodeId, _carol.NodeId]);

        // Act
        await AliceSendsToBobAsync(CraftStandard(path));

        // Assert
        await ExpectBobDropAsync(OnionMessageDropReasons.NextPeerUnreachable);
        Assert.Empty(_bob.LinkTo(_carol).Sent);
    }

    [Fact]
    public async Task Given_ANextNodeThatIsTheSender_When_BobForwards_Then_DroppedNotEchoed()
    {
        // Arrange
        var path = _alice.PathBuilder.CreateMessagePath([_bob.NodeId, _alice.NodeId]);

        // Act
        await AliceSendsToBobAsync(CraftStandard(path));

        // Assert
        await ExpectBobDropAsync(OnionMessageDropReasons.Echo);
        Assert.Empty(_bob.LinkTo(_alice).Sent);
    }

    [Fact]
    public async Task Given_AHopThatRelaysToHimself_When_BobReadsIt_Then_HePeelsItLikeHisOwnDummyHop()
    {
        // Arrange (NL-525): a hop whose next_node_id is Bob himself is a dummy hop of a path he made; he peels it
        // and processes the rest here instead of dropping the message as a loop. The final hop carries no payload
        // field, so the peeled message is delivered as an empty one.
        var path = _carol.Raw.CreatePath([_bob.NodeId, _bob.NodeId, _bob.NodeId],
        [
            new BlindedRecipientData { NextNodeId = _bob.NodeId },
            new BlindedRecipientData { NextNodeId = _bob.NodeId },
            new BlindedRecipientData()
        ]);
        var message = Craft(path, (_, hop) => Tlvs(hop, []));

        // Act
        await AliceSendsToBobAsync(message);

        // Assert
        await OnionMessageTestWaits.UntilAsync(() => _bob.Metrics.GetDelivered("empty") == 1,
                                               TestContext.Current.CancellationToken);
        Assert.Equal(0, _bob.Metrics.GetDropped(OnionMessageDropReasons.Loop));
        Assert.Empty(_bob.LinkTo(_carol).Sent);
    }

    [Fact]
    public void Given_MoreHopsRelayingToHimselfThanTheCap_When_BobProcessesIt_Then_DroppedAsLoop()
    {
        // Arrange: 22 self hops, past OnionMessageService.MaxSelfForwardHops (a message cannot get that deep from a
        // path we made; a sender flooding self relays must not spin the worker either)
        var path = _carol.Raw.CreatePath(Enumerable.Repeat(_bob.NodeId, 22).ToList(),
                                         Enumerable.Repeat(new BlindedRecipientData { NextNodeId = _bob.NodeId }, 21)
                                                   .Append(new BlindedRecipientData())
                                                   .ToList());
        var message = _alice.PacketBuilder.Build([], path, OnionMessageContents.Single(TestType, new byte[] { 1 }),
                                                 null);

        // Act
        _bob.Service.ProcessIncoming(_alice.NodeId, message);

        // Assert
        Assert.Equal(1, _bob.Metrics.GetDropped(OnionMessageDropReasons.Loop));
        Assert.Equal(0, _bob.Metrics.GetDelivered("handler"));
        Assert.Equal(0, _bob.Metrics.GetDelivered("empty"));
    }

    [Fact]
    public async Task Given_AReplyPathWithItsDummyHop_When_AliceSendsToIt_Then_CarolDeliversAfterPeeelingIt()
    {
        // Arrange (NL-525): Carol's default reply path is [Bob, Carol, Carol (dummy)]; the dummy relays to herself
        var pathId = new byte[] { 1, 2, 3, 4 };
        var path = _carol.PathBuilder.CreateMessagePath([_bob.NodeId, _carol.NodeId], pathId, dummyHops: 1);

        // Act
        await AliceSendsToBobAsync(CraftStandard(path));

        // Assert: Carol peeled her dummy hop and delivered the final hop to her handler
        await _carolHandler.WaitForAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal(pathId, Assert.Single(_carolHandler.Received).PathId!.Value.ToArray());
        Assert.Equal(0, _carol.Metrics.GetDropped(OnionMessageDropReasons.Loop));
        Assert.Equal(1, _bob.Metrics.Forwarded);
    }

    [Fact]
    public async Task Given_ANonFinalHopWithoutANextNode_When_BobForwards_Then_Dropped()
    {
        // Arrange
        var path = _carol.Raw.CreatePath([_bob.NodeId, _carol.NodeId],
                                         [new BlindedRecipientData(), new BlindedRecipientData()]);

        // Act
        await AliceSendsToBobAsync(CraftStandard(path));

        // Assert
        await ExpectBobDropAsync(OnionMessageDropReasons.NoNextHop);
    }

    [Fact]
    public async Task Given_ANextPathKeyOverride_When_BobForwards_Then_HeSendsThatPathKey()
    {
        // Arrange
        var overrideKey = new Payments.TestNodeKeyManager(9).NodeId;
        var path = _carol.PathBuilder.CreatePath([_bob.NodeId, _carol.NodeId],
        [
            new BlindedRecipientData { NextNodeId = _carol.NodeId, NextPathKeyOverride = overrideKey },
            new BlindedRecipientData()
        ]);

        // Act
        await AliceSendsToBobAsync(CraftStandard(path));
        await OnionMessageTestWaits.UntilAsync(() => _bob.Metrics.Forwarded == 1,
                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(overrideKey, Assert.Single(_bob.LinkTo(_carol).Sent).Payload.PathKey);
        // Carol cannot peel with a path key that is not hers
        await ExpectDropAsync(_carol, OnionMessageDropReasons.Undecryptable);
    }

    [Fact]
    public async Task Given_AHandlerThatThrows_When_MoreMessagesArrive_Then_TheyAreStillDelivered()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var failing = new ThrowingHandler();
        using var dave = new OnionMessageTestNode("dave", 4, [failing, _carolHandler]);
        OnionMessageTestNode.Connect(_alice, dave);
        var destination = OnionMessageDestination.ToNode(dave.NodeId);

        // Act
        await _alice.Service.SendAsync(destination, OnionMessageContents.Single(67, new byte[] { 1 }), null, ct);
        await _alice.Service.SendAsync(destination, OnionMessageContents.Single(TestType, new byte[] { 2 }), null,
                                       ct);
        await _carolHandler.WaitForAsync(1, ct);

        // Assert
        Assert.Equal(1, failing.Calls);
    }

    [Fact]
    public async Task Given_AFullHandlerQueue_When_AnotherMessageIsDelivered_Then_Dropped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var blocking = new BlockingHandler();
        using var dave = new OnionMessageTestNode("dave", 4, [blocking],
                                                  options: new OnionMessageOptions { MaxQueuedHandlerWork = 1 });
        OnionMessageTestNode.Connect(_alice, dave);
        var destination = OnionMessageDestination.ToNode(dave.NodeId);
        var contents = OnionMessageContents.Single(TestType, new byte[] { 1 });

        // Act
        await _alice.Service.SendAsync(destination, contents, null, ct);
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await _alice.Service.SendAsync(destination, contents, null, ct);
        await _alice.Service.SendAsync(destination, contents, null, ct);
        await OnionMessageTestWaits.UntilAsync(
            () => dave.Metrics.GetDropped(OnionMessageDropReasons.HandlerQueueFull) == 1, ct);
        blocking.Release.SetResult();

        // Assert
        Assert.Equal(2, dave.Metrics.GetDelivered("handler"));
    }

    private OnionMessageMessage CraftStandard(BlindedPath path) =>
        Craft(path, (i, hop) => i == path.Hops.Count - 1 ? Tlvs(hop, [Record(TestType)]) : Tlvs(hop, []));

    private OnionMessageMessage Craft(BlindedPath path, Func<int, BlindedPathHop, byte[]> payloadFor) =>
        _alice.Raw.BuildFromPayloads(path.FirstPathKey, path.Hops.Select(h => h.BlindedNodeId).ToList(),
                                         path.Hops.Select((hop, i) => payloadFor(i, hop)).ToList());

    private static byte[] Tlvs(BlindedPathHop hop, IReadOnlyList<OnionMessageTlvRecord> records) =>
        OnionMessageTlvsCodec.Encode(new OnionMessageTlvs(null, hop.EncryptedRecipientData, records));

    private static OnionMessageTlvRecord Record(ulong type) => new(type, new byte[] { 0x42 });

    private Task AliceSendsToBobAsync(OnionMessageMessage message) =>
        _alice.LinkTo(_bob).SendOnionMessageAsync(message, TestContext.Current.CancellationToken);

    private Task ExpectBobDropAsync(string reason) => ExpectDropAsync(_bob, reason);

    private async Task ExpectDropAsync(OnionMessageTestNode node, string reason)
    {
        await OnionMessageTestWaits.UntilAsync(() => node.Metrics.GetDropped(reason) == 1,
                                               TestContext.Current.CancellationToken);
        Assert.Equal(1, node.Metrics.DroppedTotal);
        if (node == _bob)
            Assert.Equal(0, _bob.Metrics.Forwarded);
        Assert.Empty(_carolHandler.Received);
    }

    [Theory]
    [InlineData(OnionMessageIgnoreReason.Undecryptable, OnionMessageDropReasons.Undecryptable)]
    [InlineData(OnionMessageIgnoreReason.InvalidPayload, OnionMessageDropReasons.InvalidPayload)]
    [InlineData(OnionMessageIgnoreReason.InvalidRecipientData, OnionMessageDropReasons.InvalidRecipientData)]
    [InlineData(OnionMessageIgnoreReason.ForbiddenRecipientData, OnionMessageDropReasons.ForbiddenRecipientData)]
    [InlineData(OnionMessageIgnoreReason.NonFinalExtraFields, OnionMessageDropReasons.NonFinalExtraFields)]
    [InlineData(OnionMessageIgnoreReason.NonFinalPathId, OnionMessageDropReasons.NonFinalPathId)]
    [InlineData(OnionMessageIgnoreReason.NoNextHop, OnionMessageDropReasons.NoNextHop)]
    [InlineData(OnionMessageIgnoreReason.MultiplePayloadFields, OnionMessageDropReasons.MultiplePayloadFields)]
    public void Given_AnUnwrapperReaderRule_When_Mapped_Then_TheDropTagIsTheServicesOwn(OnionMessageIgnoreReason kind,
                                                                                         string expected)
    {
        // Act (NL-442: the service reads every message through IOnionMessageUnwrapper and keeps its metric tags)
        var tag = OnionMessageService.ToDropReason(kind);

        // Assert
        Assert.Equal(expected, tag);
    }

    [Fact]
    public void Given_ARecipientDataWithPaymentRelay_When_Received_Then_DroppedAsForbidden()
    {
        // Arrange: a path through Bob whose data breaks the message-path rule (written without the production checks)
        var path = _carol.Raw.CreatePath([_bob.NodeId, _carol.NodeId],
        [
            new BlindedRecipientData
            {
                NextNodeId = _carol.NodeId,
                PaymentRelay = new BlindedPaymentRelay(40, 100, 1000)
            },
            new BlindedRecipientData()
        ]);
        var message = _alice.PacketBuilder.Build([], path, OnionMessageContents.Single(TestType, new byte[] { 1 }),
                                                 null);

        // Act
        _bob.Service.ProcessIncoming(_alice.NodeId, message);

        // Assert
        Assert.Equal(1, _bob.Metrics.GetDropped(OnionMessageDropReasons.ForbiddenRecipientData));
    }

    private sealed class ThrowingHandler : IOnionMessageHandler
    {
        private int _calls;

        public int Calls => _calls;
        public IReadOnlyCollection<ulong> PayloadTypes => [67];

        public Task HandleAsync(ReceivedOnionMessage message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("Handler failure");
        }
    }

    private sealed class BlockingHandler : IOnionMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<ulong> PayloadTypes => [TestType];

        public async Task HandleAsync(ReceivedOnionMessage message, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
        }
    }
}