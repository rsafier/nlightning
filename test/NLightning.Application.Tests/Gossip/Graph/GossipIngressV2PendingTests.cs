using NLightning.Tests.Utils.Gossip;

namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Graph;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// NL-1140: the taproot gossip ingress checks a 4-key <c>channel_announcement_2</c>'s MuSig2 signature before the
/// funding output lookup, and keeps a keyless (3-key) one pending, without a lookup, until its first valid
/// <c>channel_update_2</c> (as NL-406 does for BOLT 7). Real MuSig2 and BIP 340 signatures.
/// </summary>
public class GossipIngressV2PendingTests
{
    private const uint Tip = 3_000;
    private static readonly ShortChannelId s_scid = new(900, 1, 0);
    private static readonly GossipV2TestKey s_alice = new(1);
    private static readonly GossipV2TestKey s_bob = new(2);
    private static readonly GossipV2TestKey s_aliceFunding = new(11);
    private static readonly GossipV2TestKey s_bobFunding = new(12);

    /// <summary>The P2TR output key of the keyless announcements: a test key with an even y (the third signer).</summary>
    private static readonly GossipV2TestKey s_outputKey =
        Enumerable.Range(40, 200).Select(i => new GossipV2TestKey((byte)i))
                  .First(k => ((byte[])k.PubKey)[0] == 0x02);

    private static readonly byte[] s_keylessScript = [0x51, 0x20, .. ((byte[])s_outputKey.PubKey).AsSpan(1)];

    private static (GossipV2TestKey Node1, GossipV2TestKey Node2) Ordered =>
        ((ReadOnlySpan<byte>)s_alice.PubKey).SequenceCompareTo(s_bob.PubKey) < 0 ? (s_alice, s_bob) : (s_bob, s_alice);

    private static GraphTestKit CreateKit(byte[]? script = null, Action<GossipGraphOptions>? configure = null)
    {
        var kit = new GraphTestKit(gossipV2: true, tipHeight: Tip, configure: configure);
        kit.OutputFound(script ?? s_keylessScript);
        return kit;
    }

    private static ChannelAnnouncement2Message KeylessAnnouncement(ShortChannelId? scid = null, bool forged = false)
    {
        var shortChannelId = scid ?? s_scid;
        var (node1, node2) = Ordered;
        var unsigned = ChannelAnnouncement2Payload.Create(ChainConstants.Regtest, ReadOnlySpan<byte>.Empty,
                                                          shortChannelId, 1_000_000, node1.PubKey, node2.PubKey, null,
                                                          null, ReadOnlySpan<byte>.Empty,
                                                          GraphTestKit.TxIdFor(shortChannelId),
                                                          shortChannelId.OutputIndex);
        GossipV2TestKey[] signers = forged ? [node1, node2] : [node1, node2, s_outputKey];
        var signature = GossipV2TestSigner.MusigSign(signers, (byte[])unsigned.GetSignatureHash());
        return new ChannelAnnouncement2Message(unsigned.WithSignature(new CompactSignature(signature)));
    }

    private static ChannelAnnouncement2Message FourKeyAnnouncement(ShortChannelId scid, bool forged = false)
    {
        var valid = GossipV2TestSigner.SignedChannelAnnouncement2(ChainConstants.Regtest, scid, 1_000_000, s_alice,
                                                                  s_bob, s_aliceFunding, s_bobFunding,
                                                                  GraphTestKit.TxIdFor(scid));
        if (!forged)
            return new ChannelAnnouncement2Message(valid);

        // Signed by the two nodes only: the 4-key aggregate does not verify it
        var signature = GossipV2TestSigner.MusigSign([s_alice, s_bob], (byte[])valid.GetSignatureHash());
        return new ChannelAnnouncement2Message(valid.WithSignature(new CompactSignature(signature)));
    }

    private static ChannelUpdate2Message Update(ShortChannelId? scid = null, GossipV2TestKey? signer = null,
                                                uint blockHeight = Tip - 1)
    {
        var (node1, _) = Ordered;
        return new ChannelUpdate2Message(
            GossipV2TestSigner.SignedChannelUpdate2(ChainConstants.Regtest, scid ?? s_scid, 0, blockHeight,
                                                    signer ?? node1));
    }

    private static void VerifyLookups(GraphTestKit kit, Times times) =>
        kit.FundingLookup.Verify(l => l.LookupAsync(It.IsAny<ShortChannelId>(), It.IsAny<CancellationToken>()),
                                 times);

    [Fact]
    public async Task Given_AFloodOfFourKeyAnnouncementsWithInvalidSignatures_When_Processed_Then_NoFundingLookupRuns()
    {
        // Arrange: every short channel id names a real output of the announced keys
        var kit = CreateKit(GossipV2TestSigner.TaprootFundingScript(s_aliceFunding.PubKey, s_bobFunding.PubKey));
        var ct = TestContext.Current.CancellationToken;
        var results = new List<GossipIngressResult>();

        // Act
        for (uint i = 0; i < 20; i++)
        {
            var peer = GraphTestKit.CreatePeer((byte)(0x10 + i));
            results.Add(await kit.Ingress.ProcessAsync(peer.Object,
                                                       FourKeyAnnouncement(new ShortChannelId(900 + i, 1, 0),
                                                                           forged: true), 0, ct));
        }

        // Assert
        Assert.All(results, r =>
        {
            Assert.Equal(GossipIngressOutcome.Warned, r.Outcome);
            Assert.True(r.CloseConnection);
            Assert.Equal(Application.Gossip.Metrics.GossipMetricReasons.InvalidSignature, r.LimitReason);
        });
        VerifyLookups(kit, Times.Never());
        Assert.Equal(0, kit.Store.ChannelCount);
    }

    [Fact]
    public async Task Given_AForgedFourKeyAnnouncement_When_Processed_Then_ThePeerIsScoredAndDisconnected()
    {
        // Arrange
        var kit = CreateKit(GossipV2TestSigner.TaprootFundingScript(s_aliceFunding.PubKey, s_bobFunding.PubKey));
        var peer = GraphTestKit.CreatePeer();

        // Act
        await kit.Ingress.ProcessAsync(peer.Object, FourKeyAnnouncement(s_scid, forged: true), 0,
                                       TestContext.Current.CancellationToken);

        // Assert
        peer.Verify(p => p.Disconnect(It.IsAny<WarningException>()), Times.Once);
        Assert.Equal(1, kit.Ingress.Misbehaviour.GetScore(peer.Object.PeerPubKey));
    }

    [Fact]
    public async Task Given_AValidFourKeyAnnouncement_When_Processed_Then_ItIsStoredAfterOneLookup()
    {
        // Arrange
        var kit = CreateKit(GossipV2TestSigner.TaprootFundingScript(s_aliceFunding.PubKey, s_bobFunding.PubKey));

        // Act
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object, FourKeyAnnouncement(s_scid), 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(GraphGossipVersions.V2, channel.Versions);
        VerifyLookups(kit, Times.Once());
        Assert.Equal(0, kit.Ingress.PendingAnnouncement2Count);
    }

    [Fact]
    public async Task Given_AValidFourKeyAnnouncementWhoseOutputIsOfOtherKeys_When_Processed_Then_RefusedAfterTheLookup()
    {
        // Arrange: the signature is valid, the short channel id names a P2TR of other keys
        var kit = CreateKit(GossipV2TestSigner.TaprootFundingScript(s_alice.PubKey, s_bob.PubKey));
        var peer = GraphTestKit.CreatePeer();

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, FourKeyAnnouncement(s_scid), 0,
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        Assert.Equal(Application.Gossip.Metrics.GossipMetricReasons.ChainMismatch, result.LimitReason);
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
        VerifyLookups(kit, Times.Once());
        peer.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
    }

    [Fact]
    public async Task Given_AKeylessAnnouncement_When_Processed_Then_ItWaitsWithoutALookupOutsideTheGraph()
    {
        // Arrange
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        await kit.Store.LoadAsync(ct);

        // Act
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object, KeylessAnnouncement(), 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Pending, result.Outcome);
        VerifyLookups(kit, Times.Never());
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
        Assert.False(kit.Store.GetSnapshot().TryGetChannel(s_scid, out _));
        Assert.Equal(1, kit.Ingress.PendingAnnouncement2Count);
        Assert.Equal(0, kit.Ingress.PendingAnnouncementCount);
        Assert.True(kit.Ingress.IsPending(s_scid));
    }

    [Fact]
    public async Task Given_APendingKeylessAnnouncement_When_ItsFirstValidUpdateArrives_Then_ItIsLookedUpVerifiedAndStored()
    {
        // Arrange
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        var announcement = KeylessAnnouncement();
        await kit.Ingress.ProcessAsync(peer, announcement, 0, ct);
        var update = Update();

        // Act
        var result = await kit.Ingress.ProcessAsync(peer, update, 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        VerifyLookups(kit, Times.Once());
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(GraphGossipVersions.V2, channel.Versions);
        Assert.Equal(GraphChannelVerification.Verified, channel.Verification);
        Assert.Null(channel.BitcoinKey1);
        Assert.Equal(announcement.Payload.GetBytes(), channel.RawAnnouncement2.ToArray());
        Assert.Equal(update.Payload.GetBytes(), channel.GetPolicy(0, 2)!.RawUpdate.ToArray());
        Assert.Equal(0, kit.Ingress.PendingAnnouncement2Count);
        Assert.False(kit.Ingress.IsPending(s_scid));
    }

    [Fact]
    public async Task Given_AnUpdateBeforeTheKeylessAnnouncement_When_TheAnnouncementArrives_Then_ItIsPromotedAtOnce()
    {
        // Arrange
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        var orphan = await kit.Ingress.ProcessAsync(peer, Update(), 0, ct);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer, KeylessAnnouncement(), 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Orphaned, orphan.Outcome);
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.NotNull(channel.GetPolicy(0, 2));
        VerifyLookups(kit, Times.Once());
    }

    [Fact]
    public async Task Given_APendingKeylessAnnouncement_When_AnUpdateSignedByNoCandidateArrives_Then_ItWaitsWithoutALookup()
    {
        // Arrange: direction 0 signed by another key than node_id_1
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer();
        await kit.Ingress.ProcessAsync(peer.Object, KeylessAnnouncement(), 0, ct);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer.Object, Update(signer: new GossipV2TestKey(99)), 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Orphaned, result.Outcome);
        VerifyLookups(kit, Times.Never());
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
        Assert.Equal(1, kit.Ingress.PendingAnnouncement2Count);
        peer.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
    }

    [Fact]
    public async Task Given_APendingKeylessAnnouncementWithAForgedProof_When_ItsFirstUpdateArrives_Then_ItIsDroppedWithoutBlamingTheUpdatesSender()
    {
        // Arrange: signed without the output key
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var announcer = GraphTestKit.CreatePeer(0x31);
        var updater = GraphTestKit.CreatePeer(0x32);
        await kit.Ingress.ProcessAsync(announcer.Object, KeylessAnnouncement(forged: true), 0, ct);

        // Act
        var result = await kit.Ingress.ProcessAsync(updater.Object, Update(), 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        Assert.Equal(Application.Gossip.Metrics.GossipMetricReasons.InvalidSignature, result.LimitReason);
        VerifyLookups(kit, Times.Once());
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
        Assert.Equal(0, kit.Ingress.PendingAnnouncement2Count);
        updater.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
        updater.Verify(p => p.SendWarningAsync(It.IsAny<WarningException>()), Times.Never);
        Assert.Equal(1, kit.Ingress.Misbehaviour.GetScore(announcer.Object.PeerPubKey));
        Assert.Equal(0, kit.Ingress.Misbehaviour.GetScore(updater.Object.PeerPubKey));
    }

    [Fact]
    public async Task Given_AForgedPendingProofAtTheThreshold_When_Promoted_Then_OnlyItsOriginalSenderIsBanned()
    {
        var kit = CreateKit(configure: o => o.MisbehaviourThreshold = 1);
        var ct = TestContext.Current.CancellationToken;
        var announcer = GraphTestKit.CreatePeer(0x31);
        var updater = GraphTestKit.CreatePeer(0x32);
        await kit.Ingress.ProcessAsync(announcer.Object, KeylessAnnouncement(forged: true), 0, ct);

        await kit.Ingress.ProcessAsync(updater.Object, Update(), 0, ct);

        Assert.True(kit.Ingress.IsBannedForMisbehaviour(announcer.Object.PeerPubKey));
        Assert.True(kit.Store.IsBanned(announcer.Object.PeerPubKey));
        Assert.False(kit.Ingress.IsBannedForMisbehaviour(updater.Object.PeerPubKey));
        updater.Verify(p => p.Disconnect(It.IsAny<Exception>()), Times.Never);
    }

    [Fact]
    public async Task Given_AV2NodeWithAPendingChannel_When_TheOrphanTtlPasses_Then_ItWaitsAndReplaysAtPromotion()
    {
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        await kit.Ingress.ProcessAsync(peer, KeylessAnnouncement(), 0, ct);
        var node = new NodeAnnouncement2Message(GossipV2TestSigner.SignedNodeAnnouncement2(s_alice, Tip - 1));
        Assert.Equal(GossipIngressOutcome.Orphaned, (await kit.Ingress.ProcessAsync(peer, node, 0, ct)).Outcome);

        kit.Clock.Advance(kit.Options.OrphanTtl + TimeSpan.FromSeconds(1));
        kit.Ingress.PrunePendingAnnouncements();
        kit.Ingress.KeepOrphansOfPendingNodes();
        kit.Ingress.Orphans.PruneExpired();
        await kit.Ingress.ProcessAsync(peer, Update(), 0, ct);

        Assert.True(kit.Store.TryGetNode(s_alice.PubKey, out var stored));
        Assert.Equal(node.Payload.GetBytes(), stored.RawAnnouncement2.ToArray());
        Assert.Equal(0, kit.Ingress.Orphans.Count);
    }

    [Fact]
    public async Task Given_AV2NodeWhosePendingChannelExpires_When_Refreshed_Then_ItsOrphanAlsoExpires()
    {
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        await kit.Ingress.ProcessAsync(peer, KeylessAnnouncement(), 0, ct);
        await kit.Ingress.ProcessAsync(peer,
            new NodeAnnouncement2Message(GossipV2TestSigner.SignedNodeAnnouncement2(s_alice, Tip - 1)), 0, ct);

        kit.Clock.Advance(kit.Options.PendingAnnouncementTtl + TimeSpan.FromSeconds(1));
        kit.Ingress.PrunePendingAnnouncements();
        kit.Ingress.KeepOrphansOfPendingNodes();
        Assert.Equal(1, kit.Ingress.Orphans.PruneExpired());
        Assert.Equal(0, kit.Ingress.Orphans.Count);
    }

    [Fact]
    public async Task Given_APendingKeylessAnnouncementOfAnotherOutput_When_ItsFirstUpdateArrives_Then_ItIsRefused()
    {
        // Arrange: the short channel id names another P2TR output
        var kit = CreateKit(GossipV2TestSigner.TaprootFundingScript(s_aliceFunding.PubKey, s_bobFunding.PubKey));
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        await kit.Ingress.ProcessAsync(peer, KeylessAnnouncement(), 0, ct);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer, Update(), 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
        Assert.Equal(0, kit.Ingress.PendingAnnouncement2Count);
    }

    [Fact]
    public async Task Given_APendingKeylessAnnouncementAndAShallowOutput_When_ItsFirstUpdateArrives_Then_TheUpdateIsDeferredAndTheAnnouncementStays()
    {
        // Arrange
        var kit = CreateKit();
        kit.OutputFound(s_keylessScript, confirmations: 2);
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        await kit.Ingress.ProcessAsync(peer, KeylessAnnouncement(), 0, ct);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer, Update(), 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Deferred, result.Outcome);
        Assert.Equal(1, kit.Ingress.PendingAnnouncement2Count);
        Assert.False(kit.Store.TryGetChannel(s_scid, out _));
    }

    [Fact]
    public async Task Given_ThePendingIndexIsFull_When_ANewKeylessAnnouncementArrives_Then_TheOldestOfThePeerHoldingTheMostIsEvicted()
    {
        // Arrange: room for two
        var kit = CreateKit(configure: o => o.MaxPendingAnnouncements = 2);
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        var first = new ShortChannelId(900, 1, 0);
        var second = new ShortChannelId(901, 1, 0);
        var third = new ShortChannelId(902, 1, 0);

        // Act
        foreach (var scid in (ShortChannelId[])[first, second, third])
        {
            Assert.Equal(GossipIngressOutcome.Pending,
                         (await kit.Ingress.ProcessAsync(peer, KeylessAnnouncement(scid), 0, ct)).Outcome);
            kit.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        // Assert
        Assert.Equal(2, kit.Ingress.PendingAnnouncement2Count);
        Assert.False(kit.Ingress.IsPending(first));
        Assert.True(kit.Ingress.IsPending(second));
        Assert.True(kit.Ingress.IsPending(third));
        VerifyLookups(kit, Times.Never());
    }

    [Fact]
    public async Task Given_FourOtherPeersCandidates_When_AFifthAnnouncementForTheSameScidArrives_Then_ItIsRefused()
    {
        // Arrange: four different announcements (different capacities) from four peers
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        for (byte i = 0; i < PendingAnnouncementIndex.MaxCandidatesPerChannel; i++)
            await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer((byte)(0x40 + i)).Object,
                                           Variant(1_000_000UL - i), 0, ct);

        // Act
        var result = await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer(0x50).Object, Variant(1), 0, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        Assert.Equal(Application.Gossip.Metrics.GossipMetricReasons.PendingCandidatesFull, result.LimitReason);
        Assert.Equal(PendingAnnouncementIndex.MaxCandidatesPerChannel, kit.Ingress.PendingAnnouncement2Count);
        return;

        static ChannelAnnouncement2Message Variant(ulong capacity)
        {
            var (node1, node2) = Ordered;
            var unsigned = ChannelAnnouncement2Payload.Create(ChainConstants.Regtest, ReadOnlySpan<byte>.Empty, s_scid,
                                                              capacity, node1.PubKey, node2.PubKey, null, null,
                                                              ReadOnlySpan<byte>.Empty, GraphTestKit.TxIdFor(s_scid),
                                                              s_scid.OutputIndex);
            var signature = GossipV2TestSigner.MusigSign([node1, node2, s_outputKey],
                                                         (byte[])unsigned.GetSignatureHash());
            return new ChannelAnnouncement2Message(unsigned.WithSignature(new CompactSignature(signature)));
        }
    }

    [Fact]
    public async Task Given_APendingKeylessAnnouncement_When_ItsTtlEnds_Then_ThePruneDropsIt()
    {
        // Arrange
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        await kit.Ingress.ProcessAsync(GraphTestKit.CreatePeer().Object, KeylessAnnouncement(), 0, ct);

        // Act
        kit.Clock.Advance(kit.Options.PendingAnnouncementTtl + TimeSpan.FromSeconds(1));
        var pruned = kit.Ingress.PrunePendingAnnouncements();

        // Assert
        Assert.Equal(1, pruned);
        Assert.Equal(0, kit.Ingress.PendingAnnouncement2Count);
        Assert.False(kit.Ingress.IsPending(s_scid));
    }

    [Fact]
    public async Task Given_TheSameKeylessAnnouncementTwice_When_Processed_Then_TheSecondIsIgnoredAsAlreadyWaiting()
    {
        // Arrange
        var kit = CreateKit();
        var ct = TestContext.Current.CancellationToken;
        var peer = GraphTestKit.CreatePeer().Object;
        var announcement = KeylessAnnouncement();
        await kit.Ingress.ProcessAsync(peer, announcement, 0, ct);

        // Act
        var result = await kit.Ingress.ProcessAsync(peer, announcement, 1, ct);

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, result.Outcome);
        Assert.Equal(1, kit.Ingress.PendingAnnouncement2Count);
    }

    [Fact]
    public void Given_TheVerifierUsedByTheKit_When_AKeylessAnnouncementIsCheckedWithoutTheOutput_Then_ItIsMalformed()
    {
        // Arrange
        var verifier = GossipV2TestSigner.Verifier;

        // Act / Assert: the 3-key form cannot be pre-checked, which is why it waits pending
        Assert.Equal(Domain.Gossip.Enums.GossipV2ProofResult.MalformedProof,
                     verifier.CheckChannelSignature(KeylessAnnouncement().Payload));
        Assert.Equal(Domain.Gossip.Enums.GossipV2ProofResult.Valid,
                     verifier.CheckChannelProof(KeylessAnnouncement().Payload, s_keylessScript));
    }
}