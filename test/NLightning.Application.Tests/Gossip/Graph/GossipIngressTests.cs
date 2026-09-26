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
    public async Task Given_AStoredChannel_When_ItsFundingKeysSignAnotherNodePair_Then_AllFourNodesAreBanned()
    {
        // Arrange: the same funding output (same bitcoin keys, which signed both) announced for other nodes
        var kit = CreateKit();
        var peer = GraphTestKit.CreatePeer();
        await kit.Ingress.ProcessAsync(peer.Object,
                                       GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding,
                                                                              s_bobFunding), 0,
                                       TestContext.Current.CancellationToken);
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
        var update = GraphTestKit.SignedChannelUpdate(s_scid, s_alice, GraphTestKit.DirectionOf(s_alice, s_bob),
                                                      s_now);
        Assert.Equal(GossipIngressOutcome.Ignored,
                     (await kit.Ingress.ProcessAsync(peer.Object, update, 0,
                                                     TestContext.Current.CancellationToken)).Outcome);
        await kit.Store.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, kit.Repository.Bans.Count);
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
    public async Task Given_OwnChannelAnnouncement_When_Submitted_Then_StoredAsOwnWithTheChannelCapacityAndNoChainLookup()
    {
        // Arrange
        var memory = new Mock<IChannelMemoryRepository>();
        var ours = CreateOpenChannelModel();
        memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
              .Returns((Func<ChannelModel, bool> predicate) => new[] { ours }.Where(predicate).ToList());
        var kit = new GraphTestKit(channelMemoryRepository: memory.Object);
        var update = GraphTestKit.SignedChannelUpdate(s_scid, s_alice, GraphTestKit.DirectionOf(s_alice, s_bob),
                                                      s_now);

        // Act: our update first (it waits), then the announcement
        await kit.Ingress.SubmitOwnAsync(update, TestContext.Current.CancellationToken);
        await kit.Ingress.SubmitOwnAsync(
            GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob, s_aliceFunding, s_bobFunding),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.True(kit.Store.TryGetChannel(s_scid, out var stored));
        Assert.Equal(GraphChannelVerification.Own, stored.Verification);
        Assert.Equal(500_000UL, stored.CapacitySat);
        Assert.NotNull(stored.GetPolicy(GraphTestKit.DirectionOf(s_alice, s_bob)));
        Assert.True(kit.Store.TryGetFundingTxId(s_scid, out _));
        kit.FundingLookup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_OwnNodeAnnouncement_When_Submitted_Then_ItIsPersistedBeforeTheCallReturns()
    {
        // Arrange
        var kit = await CreateKitWithChannelAsync();
        var announcement = GraphTestKit.SignedNodeAnnouncement(s_alice, s_now, "our node");

        // Act
        await kit.Ingress.SubmitOwnAsync(announcement, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(kit.Repository.Nodes.TryGetValue(s_alice.PubKey, out var stored));
        Assert.Equal(announcement.Payload.GetBytes(), stored.RawAnnouncement);
        Assert.Equal("our node", kit.Store.GetSnapshot().Nodes.Single().AliasText);
    }

    [Fact]
    public async Task Given_NotGraphGossip_When_SubmittedAsOwn_Then_Throws()
    {
        // Arrange
        var kit = CreateKit();

        // Act + Assert
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Ingress.SubmitOwnAsync(
                                                        new GossipTimestampFilterMessage(
                                                            new GossipTimestampFilterPayload(
                                                                ChainConstants.Regtest, 0, 1)),
                                                        TestContext.Current.CancellationToken));
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