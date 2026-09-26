using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Channels;

namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Gossip.Enums;
using Domain.Gossip.Graph;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Validation;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Gossip;

public class GossipIngressTests
{
    private static readonly ShortChannelId s_scid = new(110, 1, 0);
    private static readonly TestGossipKey s_alice = new(1);
    private static readonly TestGossipKey s_bob = new(2);
    private static readonly TestGossipKey s_aliceFunding = new(11);
    private static readonly TestGossipKey s_bobFunding = new(12);
    private static readonly uint s_now = (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds();

    [Fact]
    public async Task Given_AnnouncementForAnotherChain_When_Processed_Then_IgnoredWithoutAChainLookup()
    {
        // Arrange
        var kit = CreateKit();
        var message = GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding,
                                                             ChainConstants.Main);

        // Act
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object, message, 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        Assert.Equal(GossipRejectReason.UnknownChain, result.RejectReason);
        kit.FundingLookup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_AnnouncementWithUnorderedNodeIds_When_Processed_Then_WarnedWithoutClosing()
    {
        // Arrange: node_id_1 must be the lesser key (BOLT 7 SHOULD send a warning)
        var kit = CreateKit();
        var peer = GraphTestKit.CreatePeer();
        var ordered = GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding)
                                  .Payload;
        var swapped = new ChannelAnnouncementMessage(
            new ChannelAnnouncementPayload(ordered.NodeSignature2, ordered.NodeSignature1, ordered.BitcoinSignature2,
                                           ordered.BitcoinSignature1, ordered.Features, ordered.ChainHash,
                                           ordered.ShortChannelId, ordered.NodeId2, ordered.NodeId1,
                                           ordered.BitcoinKey2, ordered.BitcoinKey1));

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, swapped, 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Warned, result.Outcome);
        Assert.Equal(GossipRejectReason.NodeIdsNotOrdered, result.RejectReason);
        peer.Verify(p => p.SendWarningAsync(It.IsAny<WarningException>()), Times.Once);
        peer.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
    }

    [Fact]
    public async Task Given_AStoredChannel_When_ItsFundingKeysSignAnotherNodePair_Then_AllFourNodesAreBannedAndTheirChannelsForgotten()
    {
        // Arrange: the same funding output (same bitcoin keys, which signed both) announced for other nodes; alice
        // also has another channel (with eve), and two unrelated nodes have one
        var kit = CreateKit();
        var peer = GraphTestKit.CreatePeer();
        await kit.Ingress.ProcessAsync(peer.Object,
                                       GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding,
                                                                              s_bobFunding), 0,
                                       TestContext.Current.CancellationToken);
        var eve = new TestGossipKey(5);
        var aliceEve = new ShortChannelId(111, 1, 0);
        var unrelated = new ShortChannelId(112, 1, 0);
        await kit.Ingress.ProcessAsync(peer.Object,
                                       GraphTestKit.SignedChannelAnnouncement(aliceEve, s_alice, eve,
                                                                              new TestGossipKey(21),
                                                                              new TestGossipKey(22)), 0,
                                       TestContext.Current.CancellationToken);
        await kit.Ingress.ProcessAsync(peer.Object,
                                       GraphTestKit.SignedChannelAnnouncement(unrelated, eve, new TestGossipKey(6),
                                                                              new TestGossipKey(23),
                                                                              new TestGossipKey(24)), 0,
                                       TestContext.Current.CancellationToken);
        Assert.Equal(3, kit.Store.ChannelCount);
        var carol = new TestGossipKey(3);
        var dave = new TestGossipKey(4);
        var (fundingCarol, fundingDave) = GraphTestKit.DirectionOf(carol, dave) == 0
                                              ? (s_aliceFunding, s_bobFunding)
                                              : (s_bobFunding, s_aliceFunding);
        var conflicting = GraphTestKit.SignedChannelAnnouncement(s_scid, carol, dave, fundingCarol, fundingDave);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var stored));
        Assert.Equal(stored.BitcoinKey1, conflicting.Payload.BitcoinKey1);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, conflicting, 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipRejectReason.ConflictingAnnouncement, result.RejectReason);
        Assert.All(new[] { s_alice, s_bob, carol, dave }, k => Assert.True(kit.Store.IsBanned(k.PubKey)));
        Assert.False(kit.Store.IsBanned(eve.PubKey));

        // BOLT 7: "blacklist ... AND forget any channels connected to them"
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
        Assert.False(kit.Store.TryGetChannel(aliceEve, out _));
        Assert.True(kit.Store.TryGetChannel(unrelated, out _));
        var update = GraphTestKit.SignedChannelUpdate(s_scid, s_alice, GraphTestKit.DirectionOf(s_alice, s_bob),
                                                      s_now);
        Assert.NotEqual(GossipIngressOutcome.Accepted,
                        (await kit.Ingress.ProcessAsync(peer.Object, update, 0,
                                                        TestContext.Current.CancellationToken)).Outcome);
        // (attempt 1 skips the exact-duplicate filter: the validator itself refuses the banned nodes)
        Assert.Equal(GossipRejectReason.BlacklistedNode,
                     (await kit.Ingress.ProcessAsync(peer.Object,
                                                     GraphTestKit.SignedChannelAnnouncement(
                                                         s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding), 1,
                                                     TestContext.Current.CancellationToken)).RejectReason);
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, kit.Repository.Bans.Count);
        Assert.DoesNotContain(s_scid, kit.Repository.Channels.Keys);
        Assert.DoesNotContain(aliceEve, kit.Repository.Channels.Keys);
    }

    [Fact]
    public async Task Given_OurFundingKeysSignAConflictingAnnouncement_When_Processed_Then_WeAreNotBannedAndOurChannelStays()
    {
        // Arrange: alice is our node; our channel with bob is ours (Own)
        var kit = new GraphTestKit(ourNodeId: s_alice.PubKey);
        kit.FundingFound();
        await kit.Ingress.ApplyOwnAsync(
            GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding),
            LightningMoney.Satoshis(1_000), TestContext.Current.CancellationToken);
        var carol = new TestGossipKey(3);
        var dave = new TestGossipKey(4);
        var (fundingCarol, fundingDave) = GraphTestKit.DirectionOf(carol, dave) == 0
                                              ? (s_aliceFunding, s_bobFunding)
                                              : (s_bobFunding, s_aliceFunding);

        // Act
        await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object,
                                       GraphTestKit.SignedChannelAnnouncement(s_scid, carol, dave, fundingCarol,
                                                                              fundingDave), 0,
                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.False(kit.Store.IsBanned(s_alice.PubKey));
        Assert.True(kit.Store.IsBanned(s_bob.PubKey));
        Assert.True(kit.Store.IsBanned(carol.PubKey));
        Assert.True(kit.Store.TryGetChannel(s_scid, out _));
    }

    [Fact]
    public async Task Given_AStoredChannel_When_OtherKeysAnnounceTheSameScid_Then_NobodyIsBanned()
    {
        // Arrange: anyone can sign an announcement with their own keys for any scid; it proves nothing
        var kit = CreateKit();
        var peer = GraphTestKit.CreatePeer();
        await kit.Ingress.ProcessAsync(peer.Object,
                                       GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding,
                                                                              s_bobFunding), 0,
                                       TestContext.Current.CancellationToken);
        var forged = GraphTestKit.SignedChannelAnnouncement(s_scid, new TestGossipKey(3), new TestGossipKey(4),
                                                            new TestGossipKey(13), new TestGossipKey(14));

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, forged, 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipRejectReason.ConflictingAnnouncement, result.RejectReason);
        Assert.False(kit.Store.IsBanned(s_alice.PubKey));
        Assert.False(kit.Store.IsBanned(s_bob.PubKey));
    }

    [Fact]
    public async Task Given_DontForwardUpdateForAnUnknownChannel_When_Processed_Then_IgnoredNotKept()
    {
        // Arrange: a private channel's update, sent only to its peer
        var kit = CreateKit();
        var update = GraphTestKit.SignedChannelUpdate(s_scid, s_alice, 0, s_now,
                                                      messageFlags: ChannelUpdatePayload.MessageFlagMustBeOne
                                                                  | ChannelUpdatePayload.MessageFlagDontForward);

        // Act
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object, update, 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        Assert.Equal(0, kit.Ingress.Orphans.Count);
    }

    [Fact]
    public async Task Given_AcceptedUpdate_When_AnOlderOrEqualOneArrives_Then_ItIsIgnoredAndTheNewerStays()
    {
        // Arrange
        var kit = await CreateKitWithChannelAsync();
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);
        var peer = GraphTestKit.CreatePeer();
        var newer = GraphTestKit.SignedChannelUpdate(s_scid, s_alice, direction, s_now - 10, feeBaseMsat: 7);
        var older = GraphTestKit.SignedChannelUpdate(s_scid, s_alice, direction, s_now - 20, feeBaseMsat: 9);
        var sameTimeOtherFee = GraphTestKit.SignedChannelUpdate(s_scid, s_alice, direction, s_now - 10,
                                                                feeBaseMsat: 8);

        // Act
        var first = await kit.Ingress.ProcessAsync(peer.Object, newer, 0, TestContext.Current.CancellationToken);
        var second = await kit.Ingress.ProcessAsync(peer.Object, older, 0, TestContext.Current.CancellationToken);
        var third = await kit.Ingress.ProcessAsync(peer.Object, sameTimeOtherFee, 0,
                                                   TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, first.Outcome);
        Assert.Equal(GossipRejectReason.OutdatedUpdate, second.RejectReason);
        Assert.Equal(GossipRejectReason.ConflictingSameTimestamp, third.RejectReason);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(7U, channel.GetPolicy(direction)!.FeeBaseMsat);
        Assert.Equal(newer.Payload.GetBytes(), channel.GetPolicy(direction)!.RawUpdate.ToArray());
    }

    [Fact]
    public async Task Given_UpdateSignedByTheOtherNode_When_Processed_Then_WarnedAndDisconnected()
    {
        // Arrange: direction 0 must be signed by node_id_1
        var kit = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);
        var update = GraphTestKit.SignedChannelUpdate(s_scid, s_bob, direction, s_now);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, update, 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Warned, result.Outcome);
        peer.Verify(p => p.Disconnect(It.IsAny<WarningException>()), Times.Once);
    }

    [Fact]
    public async Task Given_SpentChannel_When_AnEnabledUpdateArrives_Then_IgnoredButADisableIsAccepted()
    {
        // Arrange
        var kit = await CreateKitWithChannelAsync();
        kit.Store.MarkSpent(s_scid, 200);
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);
        var peer = GraphTestKit.CreatePeer();

        // Act
        var enabled = await kit.Ingress.ProcessAsync(peer.Object,
                                                     GraphTestKit.SignedChannelUpdate(
                                                         s_scid, s_alice, direction, s_now - 5),
                                                     0, TestContext.Current.CancellationToken);
        var disabled = await kit.Ingress.ProcessAsync(peer.Object,
                                                      GraphTestKit.SignedChannelUpdate(
                                                          s_scid, s_alice, direction, s_now - 4, disabled: true),
                                                      0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipRejectReason.ChannelSpent, enabled.RejectReason);
        Assert.Equal(GossipIngressOutcome.Accepted, disabled.Outcome);
    }

    [Fact]
    public async Task Given_NodeAnnouncementWithTruncatedAddress_When_Processed_Then_WarnedWithoutClosingAndNotApplied()
    {
        // Arrange: an IPv4 descriptor needs 6 bytes after its type (BOLT 7: SHOULD send a warning for a bad addrlen)
        var kit = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        var announcement = GraphTestKit.SignedNodeAnnouncement(s_alice, s_now, addresses: [1, 127, 0, 0]);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, announcement, 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Warned, result.Outcome);
        Assert.Equal(GossipRejectReason.MalformedAddresses, result.RejectReason);
        peer.Verify(p => p.SendWarningAsync(It.IsAny<WarningException>()), Times.Once);
        peer.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
        Assert.Equal(0, kit.Store.NodeCount);
    }

    [Fact]
    public async Task Given_NodeAnnouncementWithBadSignature_When_Processed_Then_WarnedAndDisconnected()
    {
        // Arrange
        var kit = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        var signedByBob = GraphTestKit.SignedNodeAnnouncement(s_bob, s_now).Payload;
        var forged = new NodeAnnouncementMessage(
            new NodeAnnouncementPayload(signedByBob.Signature, signedByBob.Features, signedByBob.Timestamp,
                                        s_alice.PubKey, signedByBob.RgbColor, signedByBob.Alias,
                                        signedByBob.Addresses));

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, forged, 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Warned, result.Outcome);
        peer.Verify(p => p.Disconnect(It.IsAny<WarningException>()), Times.Once);
        Assert.Equal(0, kit.Store.NodeCount);
    }

    [Fact]
    public async Task Given_OwnChannelAnnouncement_When_Added_Then_StoredAsOwnWithTheGivenCapacityAndNoChainLookup()
    {
        // Arrange
        var memory = new Mock<IChannelMemoryRepository>();
        var ours = CreateOpenChannelModel();
        memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
              .Returns((Func<ChannelModel, bool> predicate) => new[] { ours }.Where(predicate).ToList());
        var kit = new GraphTestKit(channelMemoryRepository: memory.Object);
        var update = GraphTestKit.SignedChannelUpdate(s_scid, s_alice, GraphTestKit.DirectionOf(s_alice, s_bob),
                                                      s_now);

        // Act: our update first (it waits), then the announcement, through the sink the G1 services call
        IOwnGossipSink sink = kit.Ingress;
        sink.AddOwnChannelUpdate(update.Payload);
        sink.AddOwnChannelAnnouncement(
            GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding).Payload,
            LightningMoney.Satoshis(400_000));
        await kit.Ingress.WhenOwnGossipAppliedAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(kit.Store.TryGetChannel(s_scid, out var stored));
        Assert.Equal(GraphChannelVerification.Own, stored.Verification);
        Assert.Equal(400_000UL, stored.CapacitySat);
        Assert.NotNull(stored.GetPolicy(GraphTestKit.DirectionOf(s_alice, s_bob)));
        Assert.True(kit.Store.TryGetFundingTxId(s_scid, out var fundingTxId));
        Assert.Equal(GraphTestKit.TxIdFor(s_scid), fundingTxId);
        kit.FundingLookup.VerifyNoOtherCalls();
        await kit.Ingress.StopAsync();
    }

    [Fact]
    public async Task Given_OwnGossipAddedTwice_When_Applied_Then_TheGraphIsTheSame()
    {
        // Arrange: B1 repeats its messages after a restart or a reconnection
        var kit = CreateKit();
        IOwnGossipSink sink = kit.Ingress;
        var announcement = GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding,
                                                                  s_bobFunding).Payload;
        var update = GraphTestKit.SignedChannelUpdate(s_scid, s_alice, GraphTestKit.DirectionOf(s_alice, s_bob),
                                                      s_now).Payload;

        // Act
        for (var i = 0; i < 2; i++)
        {
            sink.AddOwnChannelAnnouncement(announcement, LightningMoney.Satoshis(1_000));
            sink.AddOwnChannelUpdate(update);
            sink.AddOwnNodeAnnouncement(GraphTestKit.SignedNodeAnnouncement(s_alice, s_now).Payload);
        }

        await kit.Ingress.WhenOwnGossipAppliedAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, kit.Store.ChannelCount);
        Assert.Equal(1, kit.Store.NodeCount);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var stored));
        Assert.Equal(s_now, stored.GetPolicy(GraphTestKit.DirectionOf(s_alice, s_bob))!.Timestamp);
        await kit.Ingress.StopAsync();
    }

    [Fact]
    public async Task Given_OwnNodeAnnouncement_When_Added_Then_ItIsInTheGraphButTheStoreNeverWritesItsRow()
    {
        // Arrange: a peer relayed an older announcement of ours, still waiting for the write-behind; the node
        // announcement service (G1-T6) writes our row itself before it publishes
        var kit = await CreateKitWithChannelAsync();
        await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object,
                                       GraphTestKit.SignedNodeAnnouncement(s_alice, s_now - 10, "older"), 0,
                                       TestContext.Current.CancellationToken);
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object,
                                       GraphTestKit.SignedNodeAnnouncement(s_alice, s_now - 5, "relayed"), 0,
                                       TestContext.Current.CancellationToken);
        var announcement = GraphTestKit.SignedNodeAnnouncement(s_alice, s_now, "our node");

        // Act
        await kit.Ingress.ApplyOwnAsync(announcement, null, TestContext.Current.CancellationToken);
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);

        // Assert: the graph shows ours; the older relayed one pending before it is never written over the row
        Assert.Equal("our node", kit.Store.GetSnapshot().Nodes.Single().AliasText);
        Assert.True(kit.Repository.Nodes.TryGetValue(s_alice.PubKey, out var row));
        Assert.Equal(s_now - 10, row.Timestamp);
        Assert.Equal(0, kit.Store.PendingChanges);
    }

    [Fact]
    public async Task Given_NotGraphGossip_When_AppliedAsOwn_Then_Throws()
    {
        // Arrange
        var kit = CreateKit();

        // Act + Assert
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Ingress.ApplyOwnAsync(
                                                        new GossipTimestampFilterMessage(
                                                            new GossipTimestampFilterPayload(
                                                                ChainConstants.Regtest, 0, 1)),
                                                        null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Given_GraphDisabled_When_OwnGossipIsAdded_Then_NothingStarts()
    {
        // Arrange
        var (ingress, store) = CreateIngressOverBlockedStore(network: "mainnet");

        // Act
        ((IOwnGossipSink)ingress).AddOwnNodeAnnouncement(GraphTestKit.SignedNodeAnnouncement(s_alice, s_now).Payload);

        // Assert
        store.Verify(s => s.LoadAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_AStoreStillLoading_When_OwnGossipIsAdded_Then_TheCallReturnsAtOnce()
    {
        // Arrange: the load never ends; the caller may hold a channel lock and must not wait for it
        var (ingress, _) = CreateIngressOverBlockedStore();
        IOwnGossipSink sink = ingress;

        // Act
        var call = Task.Run(() => sink.AddOwnChannelAnnouncement(
                                GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding,
                                                                       s_bobFunding).Payload,
                                LightningMoney.Satoshis(1)), TestContext.Current.CancellationToken);

        // Assert
        var completed = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(5),
                                                            TestContext.Current.CancellationToken));
        Assert.Same(call, completed);
    }

    [Theory]
    [InlineData("mainnet", null, false)]
    [InlineData("regtest", null, true)]
    [InlineData("signet", null, true)]
    [InlineData("mainnet", true, true)]
    [InlineData("regtest", false, false)]
    public void Given_NetworkAndSetting_When_Asked_Then_TheGraphIsOffOnMainnetByDefault(string network,
        bool? enabled, bool expected)
    {
        // Arrange
        var options = new GossipGraphOptions { Enabled = enabled };

        // Act + Assert (plan D12)
        Assert.Equal(expected, options.IsEnabledFor(BitcoinNetwork.Resolve(network)));
    }

    [Fact]
    public void Given_GraphDisabled_When_GossipIsQueued_Then_ItIsDropped()
    {
        // Arrange
        var (ingress, store) = CreateIngressOverBlockedStore(network: "mainnet");

        // Act
        var queued = ingress.TryEnqueue(GraphTestKit.CreatePeer().Object,
                                        GraphTestKit.SignedNodeAnnouncement(s_alice, s_now));

        // Assert
        Assert.False(ingress.IsEnabled);
        Assert.False(queued);
        store.Verify(s => s.LoadAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Given_APeerOverItsQueueLimit_When_MoreGossipArrives_Then_OnlyThatPeersExcessIsDropped()
    {
        // Arrange: the store never finishes loading, so no worker drains the queue
        var (ingress, _) = CreateIngressOverBlockedStore(maxPerPeer: 2);
        var noisy = GraphTestKit.CreatePeer(0x70);
        var quiet = GraphTestKit.CreatePeer(0x71);
        var message = GraphTestKit.SignedNodeAnnouncement(s_alice, s_now);

        // Act
        var results = Enumerable.Range(0, 3).Select(_ => ingress.TryEnqueue(noisy.Object, message)).ToList();
        var other = ingress.TryEnqueue(quiet.Object, message);

        // Assert
        Assert.Equal([true, true, false], results);
        Assert.True(other);
        Assert.Equal(3, ingress.QueuedCount);
    }

    [Fact]
    public void Given_APeerOverItsQueueLimit_When_AnAnnouncementIsDropped_Then_ItsScidIsKeptForTheNextSync()
    {
        // Arrange: no worker drains the queue
        var (ingress, _) = CreateIngressOverBlockedStore(maxPerPeer: 1);
        var peer = GraphTestKit.CreatePeer();
        var dropped = new ShortChannelId(111, 1, 0);

        // Act
        Assert.True(ingress.TryEnqueue(peer.Object,
                                       GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding,
                                                                              s_bobFunding)));
        Assert.False(ingress.TryEnqueue(peer.Object,
                                        GraphTestKit.SignedChannelAnnouncement(dropped, s_alice, s_bob,
                                                                               s_aliceFunding, s_bobFunding)));
        Assert.False(ingress.TryEnqueue(peer.Object, GraphTestKit.SignedNodeAnnouncement(s_alice, s_now)));
        var missed = ingress.TakeMissedShortChannelIds();

        // Assert
        Assert.Equal([dropped], missed);
        Assert.Empty(ingress.TakeMissedShortChannelIds());
        Assert.Equal(2, ingress.DroppedCount);
    }

    [Fact]
    public async Task Given_AnAnnouncementGivenUpAfterItsRetries_When_ItArrivesAgainLater_Then_ItIsMissedUntilStored()
    {
        // Arrange: bitcoind is down and no retry is allowed
        var kit = new GraphTestKit(configure: o => o.MaxRetries = 0);
        kit.FundingFails(FundingOutputStatus.ChainUnavailable);
        await kit.Ingress.StartAsync();
        var announcement = GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding,
                                                                  s_bobFunding);

        // Act
        Assert.True(kit.Ingress.TryEnqueue(GraphTestKit.CreatePeer().Object, announcement));
        await WaitUntilAsync(() => kit.Ingress.DroppedCount == 1);
        kit.FundingFound();
        await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object, announcement, 1,
                                       TestContext.Current.CancellationToken);
        await kit.Ingress.StopAsync();

        // Assert: stored by the later attempt, so nothing is left to ask for
        Assert.Equal(1, kit.Store.ChannelCount);
        Assert.Empty(kit.Ingress.TakeMissedShortChannelIds());
    }

    [Theory]
    [InlineData("regtest", 1U, 1U)]
    [InlineData("regtest", 0U, 1U)]
    [InlineData("signet", 1U, 6U)]
    [InlineData("mainnet", 3U, 6U)]
    [InlineData("signet", 10U, 10U)]
    public void Given_AnAnnouncementDepth_When_ReadForANetwork_Then_OnlyRegtestMayLowerIt(string network, uint configured,
                                                                                          uint expected)
    {
        // Arrange
        var options = new GossipGraphOptions { AnnouncementDepth = configured };

        // Act
        var depth = options.GetAnnouncementDepth(BitcoinNetwork.Resolve(network));

        // Assert
        Assert.Equal(expected, depth);
    }

    [Fact]
    public async Task Given_AStartedIngress_When_DisposedSynchronously_Then_ItStopsWithoutThrowing()
    {
        // Arrange: a container disposed with Dispose() (not DisposeAsync) must not throw for the ingress
        var kit = CreateKit();
        await kit.Ingress.StartAsync();

        var fresh = new GraphTestKit().Ingress;

        // Act
        kit.Ingress.Dispose();
        await kit.Ingress.StopAsync();
        fresh.Dispose();

        // Assert: a disposed ingress never starts
        Assert.Same(Task.CompletedTask, fresh.StartAsync());
    }

    [Fact]
    public void Given_AMessageThatIsNotGraphGossip_When_Queued_Then_ItIsRefused()
    {
        // Arrange
        var (ingress, _) = CreateIngressOverBlockedStore();

        // Act
        var queued = ingress.TryEnqueue(GraphTestKit.CreatePeer().Object,
                                        new GossipTimestampFilterMessage(
                                            new GossipTimestampFilterPayload(ChainConstants.Regtest, 0, 1)));

        // Assert
        Assert.False(queued);
    }

    [Fact]
    public async Task Given_StartedIngress_When_GossipIsQueued_Then_TheWorkersApplyItAndStopWritesIt()
    {
        // Arrange
        var kit = CreateKit();
        var peer = GraphTestKit.CreatePeer();
        await kit.Ingress.StartAsync();

        // Act: the update first, so it waits for its channel on another worker round
        Assert.True(kit.Ingress.TryEnqueue(peer.Object,
                                           GraphTestKit.SignedChannelUpdate(
                                               s_scid, s_alice, GraphTestKit.DirectionOf(s_alice, s_bob), s_now)));
        Assert.True(kit.Ingress.TryEnqueue(peer.Object,
                                           GraphTestKit.SignedChannelAnnouncement(
                                               s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding)));
        Assert.True(kit.Ingress.TryEnqueue(peer.Object, GraphTestKit.SignedNodeAnnouncement(s_bob, s_now)));
        await WaitUntilAsync(() => kit.Store.NodeCount == 1
                                && kit.Store.TryGetChannel(s_scid, out var c)
                                && c.GetPolicy(GraphTestKit.DirectionOf(s_alice, s_bob)) is not null);
        await kit.Ingress.StopAsync();

        // Assert
        Assert.Single(kit.Repository.Channels);
        Assert.Single(kit.Repository.Policies);
        Assert.Single(kit.Repository.Nodes);
    }

    [Fact]
    public async Task Given_TransientChainAnswer_When_TheWorkerMeetsIt_Then_TheAnnouncementIsRetriedLater()
    {
        // Arrange
        var kit = new GraphTestKit(configure: o =>
        {
            o.RetryDelay = TimeSpan.FromMilliseconds(20);
            o.MaxRetries = 5;
        });
        kit.FundingFails(FundingOutputStatus.ChainUnavailable);
        await kit.Ingress.StartAsync();

        // Act
        Assert.True(kit.Ingress.TryEnqueue(GraphTestKit.CreatePeer().Object,
                                           GraphTestKit.SignedChannelAnnouncement(
                                               s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding)));
        await WaitUntilAsync(() => kit.FundingLookup.Invocations.Count >= 1);
        kit.FundingFound();
        await WaitUntilAsync(() => kit.Store.ChannelCount == 1);
        await kit.Ingress.StopAsync();

        // Assert
        Assert.True(kit.FundingLookup.Invocations.Count >= 2);
    }

    private static GraphTestKit CreateKit()
    {
        var kit = new GraphTestKit();
        kit.FundingFound();
        return kit;
    }

    private static async Task<GraphTestKit> CreateKitWithChannelAsync()
    {
        var kit = CreateKit();
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object,
                                                    GraphTestKit.SignedChannelAnnouncement(
                                                        s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding),
                                                    0, TestContext.Current.CancellationToken);
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        return kit;
    }

    private static ChannelModel CreateOpenChannelModel()
    {
        var channelParams = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Satoshis(2_500),
                                                     LightningMoney.MilliSatoshis(5_000),
                                                     LightningMoney.Satoshis(354), 30,
                                                     LightningMoney.MilliSatoshis(800_000_000), 3, false,
                                                     LightningMoney.Satoshis(354), 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_alice.PubKey, s_alice.PubKey, s_alice.PubKey, s_alice.PubKey,
                                            s_alice.PubKey, s_alice.PubKey);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(500_000), s_aliceFunding.PubKey,
                                                  s_bobFunding.PubKey)
        {
            TransactionId = GraphTestKit.TxIdFor(s_scid),
            Index = 0
        };
        return new ChannelModel(channelParams, new ChannelId(new byte[32]), null, fundingOutput, true, null, null,
                                LightningMoney.Zero, keySet, 0, 0, LightningMoney.Zero, keySet, 0, s_bob.PubKey, 0,
                                ChannelState.Open, ChannelVersion.V1)
        {
            ShortChannelId = s_scid
        };
    }

    private static (GossipIngress Ingress, Mock<IGraphStore> Store) CreateIngressOverBlockedStore(
        string network = "regtest", int maxPerPeer = 2_000)
    {
        var store = new Mock<IGraphStore>();
        store.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
             .Returns(new TaskCompletionSource().Task);
        var options = new GossipGraphOptions { MaxQueuedPerPeer = maxPerPeer, Workers = 1 };
        var nodeOptions = new NodeOptions { BitcoinNetwork = BitcoinNetwork.Resolve(network) };
        var ingress = new GossipIngress(store.Object, new GossipSignatureVerifier(),
                                        new Mock<IFundingOutputLookup>().Object,
                                        Microsoft.Extensions.Options.Options.Create(options),
                                        Microsoft.Extensions.Options.Options.Create(nodeOptions),
                                        NullLogger<GossipIngress>.Instance);
        return (ingress, store);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for the ingress");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}