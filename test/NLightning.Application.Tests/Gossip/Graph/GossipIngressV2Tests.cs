using NLightning.Tests.Utils.Gossip;

namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Graph;
using Domain.Gossip.Validation;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// The taproot gossip ingress (BOLTs PR #1059, NL-878) with real MuSig2 and BIP 340 signatures: a
/// <c>channel_announcement_2</c> proven against a P2TR (BIP 86) funding output, <c>channel_update_2</c>s and
/// <c>node_announcement_2</c>s.
/// </summary>
public class GossipIngressV2Tests
{
    private const uint Tip = 3_000;
    private static readonly ShortChannelId s_scid = new(900, 1, 0);
    private static readonly GossipV2TestKey s_alice = new(1);
    private static readonly GossipV2TestKey s_bob = new(2);
    private static readonly GossipV2TestKey s_aliceFunding = new(11);
    private static readonly GossipV2TestKey s_bobFunding = new(12);

    private static GraphTestKit CreateKit(bool gossipV2 = true)
    {
        var kit = new GraphTestKit(gossipV2: gossipV2, tipHeight: Tip);
        kit.OutputFound(GossipV2TestSigner.TaprootFundingScript(s_aliceFunding.PubKey, s_bobFunding.PubKey));
        return kit;
    }

    private static ChannelAnnouncement2Message Announcement(ShortChannelId? scid = null, ulong capacity = 1_000_000)
    {
        var shortChannelId = scid ?? s_scid;
        return new ChannelAnnouncement2Message(
            GossipV2TestSigner.SignedChannelAnnouncement2(ChainConstants.Regtest, shortChannelId, capacity, s_alice,
                                                          s_bob, s_aliceFunding, s_bobFunding,
                                                          GraphTestKit.TxIdFor(shortChannelId)));
    }

    private static byte DirectionOf(GossipV2TestKey node, GossipV2TestKey other) =>
        ((ReadOnlySpan<byte>)node.PubKey).SequenceCompareTo(other.PubKey) < 0 ? (byte)0 : (byte)1;

    private static ChannelUpdate2Message Update(GossipV2TestKey origin, GossipV2TestKey other, uint blockHeight,
                                                uint inboundFeeBaseMsat = 0, GossipV2TestKey? signer = null) =>
        new(GossipV2TestSigner.SignedChannelUpdate2(ChainConstants.Regtest, s_scid, DirectionOf(origin, other),
                                                    blockHeight, signer ?? origin,
                                                    inboundFeeBaseMsat: inboundFeeBaseMsat));

    [Fact]
    public async Task Given_AValidChannelAnnouncement2OfAP2trOutput_When_Processed_Then_TheChannelIsStoredAsV2WithItsBytes()
    {
        // Arrange
        var kit = CreateKit();
        var message = Announcement();

        // Act
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object, message, 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(GraphGossipVersions.V2, channel.Versions);
        Assert.False(channel.HasV1);
        Assert.Equal(1_000_000UL, channel.CapacitySat);
        Assert.Equal(message.Payload.GetBytes(), channel.RawAnnouncement2.ToArray());
        Assert.True(channel.RawAnnouncement.IsEmpty);
        Assert.Equal(GraphChannelVerification.Verified, channel.Verification);
        Assert.True(kit.Store.TryGetFundingTxId(s_scid, out var txId));
        Assert.Equal(GraphTestKit.TxIdFor(s_scid), txId);
    }

    [Fact]
    public void Given_GossipV2NotAdvertised_When_AV2MessageIsEnqueued_Then_ItIsDroppedSilently()
    {
        // Arrange
        var kit = CreateKit(gossipV2: false);
        var peer = GraphTestKit.CreatePeer();

        // Act
        var queued = kit.Ingress.TryEnqueue(peer.Object, Announcement());

        // Assert
        Assert.False(kit.Ingress.IsGossipV2Enabled);
        Assert.False(queued);
        peer.Verify(p => p.SendWarningAsync(It.IsAny<WarningException>()), Times.Never);
    }

    [Fact]
    public async Task Given_AnAnnouncementSignedByOtherKeys_When_Processed_Then_WarnedAndClosed()
    {
        // Arrange: the funding output is the keys' P2TR, but the signature is the aggregate of other keys
        var kit = CreateKit();
        var peer = GraphTestKit.CreatePeer();
        var valid = Announcement().Payload;
        var forged = valid.WithSignature(new Domain.Crypto.ValueObjects.CompactSignature(
                                             GossipV2TestSigner.MusigSign([s_alice, s_bob], (byte[])valid.GetSignatureHash())));

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, new ChannelAnnouncement2Message(forged), 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Warned, result.Outcome);
        Assert.True(result.CloseConnection);
        peer.Verify(p => p.Disconnect(It.IsAny<WarningException>()), Times.Once);
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
    }

    [Fact]
    public async Task Given_AFundingOutputOfOtherKeys_When_Processed_Then_RefusedWithoutBlamingThePeer()
    {
        // Arrange: the short channel id names a P2TR of other keys (the chain contradicts the announcement)
        var kit = CreateKit();
        kit.OutputFound(GossipV2TestSigner.TaprootFundingScript(s_alice.PubKey, s_bob.PubKey));
        var peer = GraphTestKit.CreatePeer();

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, Announcement(), 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
        peer.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
    }

    [Fact]
    public async Task Given_AnOutpointThatIsNotTheScidsOutput_When_Processed_Then_Refused()
    {
        // Arrange: the transaction at the short channel id is another one than the announced outpoint
        var kit = CreateKit();
        kit.OutputFound(GossipV2TestSigner.TaprootFundingScript(s_aliceFunding.PubKey, s_bobFunding.PubKey),
                        txId: GraphTestKit.TxIdFor(new ShortChannelId(1, 1, 1)));

        // Act
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object, Announcement(), 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
    }

    [Fact]
    public async Task Given_ACapacityAboveTheOutput_When_Processed_Then_Refused()
    {
        // Arrange: the output holds 1,000,000 sat
        var kit = CreateKit();

        // Act
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object,
                                                    Announcement(capacity: 1_000_001), 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
    }

    [Fact]
    public async Task Given_AStoredV2Channel_When_ItsNodesSignUpdates_Then_TheV2PoliciesAreStoredWithTheirBlockHeights()
    {
        // Arrange
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        await kit.Ingress.ProcessAsync(peer, Announcement(), 0, ct);
        var aliceUpdate = Update(s_alice, s_bob, Tip - 10, inboundFeeBaseMsat: 7);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer, aliceUpdate, 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        var direction = DirectionOf(s_alice, s_bob);
        var policy = channel.GetPolicy(direction, 2);
        Assert.NotNull(policy);
        Assert.True(policy.IsV2);
        Assert.Equal(Tip - 10, policy.Timestamp);
        Assert.Equal(7u, policy.InboundFeeBaseMsat);
        Assert.Equal(aliceUpdate.Payload.GetBytes(), policy.RawUpdate.ToArray());
        Assert.Null(channel.GetPolicy(direction));
        Assert.Same(policy, channel.GetRoutingPolicy(direction));
    }

    [Fact]
    public async Task Given_AnUpdateSignedByTheOtherNode_When_Processed_Then_WarnedAndClosed()
    {
        // Arrange: alice's direction signed by bob
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer();
        await kit.Ingress.ProcessAsync(peer.Object, Announcement(), 0, ct);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, Update(s_alice, s_bob, Tip - 1, signer: s_bob), 0,
                                                    ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Warned, result.Outcome);
        peer.Verify(p => p.Disconnect(It.IsAny<WarningException>()), Times.Once);
    }

    [Theory]
    [InlineData(Tip - 2017, GossipRejectReason.StaleUpdate)]
    [InlineData(Tip + 2, GossipRejectReason.TimestampTooFarInFuture)]
    [InlineData(899, GossipRejectReason.OutdatedUpdate)]
    public async Task Given_AnUpdateOutsideTheBlockHeightWindow_When_Processed_Then_Ignored(uint blockHeight,
                                                                                           GossipRejectReason reason)
    {
        // Arrange: the window is [tip - 2016, tip + 1] and never below the funding block (900)
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        await kit.Ingress.ProcessAsync(peer, Announcement(), 0, ct);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer, Update(s_alice, s_bob, blockHeight), 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        Assert.Equal(reason, result.RejectReason);
    }

    [Fact]
    public async Task Given_AnOlderUpdate_When_Processed_Then_TheNewerBlockHeightWins()
    {
        // Arrange
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        await kit.Ingress.ProcessAsync(peer, Announcement(), 0, ct);
        await kit.Ingress.ProcessAsync(peer, Update(s_alice, s_bob, Tip - 5), 0, ct);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer, Update(s_alice, s_bob, Tip - 6), 0, ct);

        // Assert
        Assert.Equal(GossipRejectReason.OutdatedUpdate, result.RejectReason);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(Tip - 5, channel.GetPolicy(DirectionOf(s_alice, s_bob), 2)!.Timestamp);
    }

    [Fact]
    public async Task Given_UpdatesAndANodeAnnouncementBeforeTheirChannel_When_TheAnnouncementArrives_Then_TheyAreReplayed()
    {
        // Arrange: the update and alice's node_announcement_2 come first, as orphans
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        var orphan = await kit.Ingress.ProcessAsync(peer, Update(s_alice, s_bob, Tip - 3), 0, ct);
        var orphanNode = await kit.Ingress.ProcessAsync(
                             peer, new NodeAnnouncement2Message(
                                 GossipV2TestSigner.SignedNodeAnnouncement2(s_alice, Tip - 3)), 0, ct);

        // Act
        await kit.Ingress.ProcessAsync(peer, Announcement(), 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Orphaned, orphan.Outcome);
        Assert.Equal(GossipIngressOutcome.Orphaned, orphanNode.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.NotNull(channel.GetPolicy(DirectionOf(s_alice, s_bob), 2));
        Assert.True(kit.Store.TryGetNode(s_alice.PubKey, out var node));
        Assert.Equal(GraphGossipVersions.V2, node.Versions);
        Assert.Equal(Tip - 3, node.BlockHeight);
        Assert.Equal("v2node", node.AliasText);
    }

    [Fact]
    public async Task Given_AStoredNodeAnnouncement2_When_AnOlderOneArrives_Then_ItIsIgnored()
    {
        // Arrange
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        await kit.Ingress.ProcessAsync(peer, Announcement(), 0, ct);
        await kit.Ingress.ProcessAsync(peer, new NodeAnnouncement2Message(
                                           GossipV2TestSigner.SignedNodeAnnouncement2(s_alice, Tip - 2, "new")), 0, ct);

        // Act
        var result = await kit.Ingress.ProcessAsync(
                         peer, new NodeAnnouncement2Message(
                             GossipV2TestSigner.SignedNodeAnnouncement2(s_alice, Tip - 2, "same")), 0, ct);

        // Assert
        Assert.Equal(GossipRejectReason.NotNewer, result.RejectReason);
        Assert.True(kit.Store.TryGetNode(s_alice.PubKey, out var node));
        Assert.Equal("new", node.AliasText);
    }

    [Fact]
    public async Task Given_ANodeAnnouncement2WithABadSignature_When_Processed_Then_WarnedAndClosed()
    {
        // Arrange: alice's announcement signed by bob
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer();
        await kit.Ingress.ProcessAsync(peer.Object, Announcement(), 0, ct);
        var announcement = GossipV2TestSigner.SignedNodeAnnouncement2(s_alice, Tip - 2);
        var forged = announcement.WithSignature(s_bob.SignBip340(announcement.GetSignatureHash()));

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, new NodeAnnouncement2Message(forged), 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Warned, result.Outcome);
        peer.Verify(p => p.Disconnect(It.IsAny<WarningException>()), Times.Once);
        Assert.False(kit.Store.TryGetNode(s_alice.PubKey, out _));
    }

    [Fact]
    public async Task Given_AChannelKnownFromItsChannelAnnouncement_When_ItsAnnouncement2Arrives_Then_BothAreKeptAndRoutingPrefersV2()
    {
        // Arrange: the same channel (same node ids) stored from a BOLT 7 announcement with a v1 policy
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var v2 = Announcement().Payload;
        var v1Raw = new byte[] { 1, 2, 3 };
        var v1Channel = new GraphChannel(s_scid, v2.NodeId1, v2.NodeId2, v2.BitcoinKey1, v2.BitcoinKey2, 1_000_000)
        {
            RawAnnouncement = v1Raw
        };
        await kit.Store.LoadAsync(ct);
        Assert.True(kit.Store.TryAddChannel(v1Channel));
        var direction = DirectionOf(s_alice, s_bob);
        kit.Store.TryApplyPolicy(s_scid, new GraphPolicy(1_700_000_000, 1, direction, 40, 1, 400_000_000, 5, 5)
        {
            RawUpdate = new byte[] { 9 }
        });
        var peer = GraphTestKit.CreatePeer().Object;

        // Act
        var result = await kit.Ingress.ProcessAsync(peer, new ChannelAnnouncement2Message(v2), 0, ct);
        await kit.Ingress.ProcessAsync(peer, Update(s_alice, s_bob, Tip - 1), 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(GraphGossipVersions.V1 | GraphGossipVersions.V2, channel.Versions);
        Assert.Equal(v1Raw, channel.RawAnnouncement.ToArray());
        Assert.Equal(v2.GetBytes(), channel.RawAnnouncement2.ToArray());
        Assert.Equal(1_700_000_000u, channel.GetPolicy(direction)!.Timestamp);
        Assert.True(channel.GetRoutingPolicy(direction)!.IsV2);
    }

    [Fact]
    public async Task Given_AV2OnlyChannel_When_AChannelUpdateArrives_Then_ItWaitsForTheChannelAnnouncement()
    {
        // Arrange: a BOLT 7 update belongs to a channel_announcement, which this channel lacks
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        await kit.Ingress.ProcessAsync(peer, Announcement(), 0, ct);
        var update = new ChannelUpdateMessage(new ChannelUpdatePayload(
                                                  ChannelUpdatePayload.EmptySignature, ChainConstants.Regtest, s_scid,
                                                  (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds(),
                                                  ChannelUpdatePayload.MessageFlagMustBeOne, 0, 40, 1_000, 1_000,
                                                  100, 500_000_000));

        // Act
        var result = await kit.Ingress.ProcessAsync(peer, update, 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Orphaned, result.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Null(channel.Policy1);
        Assert.Null(channel.Policy2);
    }

    [Fact]
    public async Task Given_OurOwnV2Gossip_When_Applied_Then_TheChannelIsOwnAndThePolicyAndNodeAreStored()
    {
        // Arrange
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        await kit.Store.LoadAsync(ct);
        var update = Update(s_alice, s_bob, Tip - 1);
        var node = new NodeAnnouncement2Message(GossipV2TestSigner.SignedNodeAnnouncement2(s_alice, Tip - 1));

        // Act: the update first (it waits for the announcement), as the own loop may see them
        await kit.Ingress.ApplyOwnAsync(update, null, ct);
        await kit.Ingress.ApplyOwnAsync(Announcement(), LightningMoney.Satoshis(900_000), ct);
        await kit.Ingress.ApplyOwnAsync(node, null, ct);

        // Assert
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(GraphChannelVerification.Own, channel.Verification);
        Assert.Equal(GraphGossipVersions.V2, channel.Versions);
        Assert.Equal(900_000UL, channel.CapacitySat);
        Assert.NotNull(channel.GetPolicy(DirectionOf(s_alice, s_bob), 2));
        Assert.True(kit.Store.TryGetNode(s_alice.PubKey, out var stored));
        Assert.True(stored.HasV2);

        // Our node announcement is never written by the write-behind (its row has one writer)
        await kit.Store.FlushAsync(ct);
        Assert.False(kit.Repository.Nodes.ContainsKey(s_alice.PubKey));
        Assert.True(kit.Repository.Channels.ContainsKey(s_scid));
        Assert.True(kit.Repository.PoliciesV2.ContainsKey((s_scid, DirectionOf(s_alice, s_bob))));
    }

    [Fact]
    public async Task Given_AV2Graph_When_FlushedAndLoadedAgain_Then_TheSameGraphComesBack()
    {
        // Arrange
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        await kit.Ingress.ProcessAsync(peer, Announcement(), 0, ct);
        await kit.Ingress.ProcessAsync(peer, Update(s_alice, s_bob, Tip - 1, inboundFeeBaseMsat: 3), 0, ct);
        await kit.Ingress.ProcessAsync(peer, Update(s_bob, s_alice, Tip - 2), 0, ct);
        await kit.Ingress.ProcessAsync(peer, new NodeAnnouncement2Message(
                                           GossipV2TestSigner.SignedNodeAnnouncement2(s_bob, Tip - 1)), 0, ct);

        // Act
        await kit.Store.FlushAsync(ct);
        var reloaded = new GraphTestKit(kit.Repository, gossipV2: true, tipHeight: Tip);
        await reloaded.Store.LoadAsync(ct);

        // Assert
        var expected = kit.Store.GetSnapshot();
        var actual = reloaded.Store.GetSnapshot();
        Assert.True(actual.TryGetChannel(s_scid, out var channel));
        Assert.True(expected.TryGetChannel(s_scid, out var original));
        Assert.Equal(original, channel);
        Assert.Equal(original.RawAnnouncement2.ToArray(), channel.RawAnnouncement2.ToArray());
        for (byte direction = 0; direction < 2; direction++)
            Assert.Equal(original.GetPolicy(direction, 2)!.RawUpdate.ToArray(),
                         channel.GetPolicy(direction, 2)!.RawUpdate.ToArray());
        Assert.True(actual.TryGetNode(s_bob.PubKey, out var node));
        Assert.True(expected.TryGetNode(s_bob.PubKey, out var originalNode));
        Assert.Equal(originalNode, node);
        Assert.Equal(originalNode.RawAnnouncement2.ToArray(), node.RawAnnouncement2.ToArray());
    }
}