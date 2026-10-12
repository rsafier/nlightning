namespace NLightning.Application.Tests.Gossip.Announcements;

using Application.Gossip.Announcements;
using Application.Gossip.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;

/// <summary>
/// BOLT 7 plan G1-T3/G1-T4 building blocks: when our <c>announcement_signatures</c> may go out, the announcement both
/// ends build, and the per-connection record.
/// </summary>
public class ChannelAnnouncementServiceTests
{
    [Fact]
    public void Given_APublicOpenChannelSixDeep_When_Checked_Then_OurHalfMayGoOutAndSignsTheAnnouncement()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var channel = pair.Alice.Channel;

        // Act
        var message = pair.Alice.Service.CreateAnnouncementSignatures(channel);

        // Assert: our node and funding keys sign the announcement both ends build
        Assert.True(pair.Alice.Service.CanSendAnnouncementSignatures(channel));
        Assert.Equal(AnnouncementTestPair.ChannelId, message.Payload.ChannelId);
        Assert.Equal(AnnouncementTestPair.ShortChannelId, message.Payload.ShortChannelId);
        var unsigned = ChannelAnnouncementBuilder.BuildUnsigned(pair.Bob.Channel, AnnouncementTestPair.ShortChannelId,
                                                                pair.Bob.NodeId,
                                                                pair.Bob.NodeOptions.BitcoinNetwork.ChainHash);
        var hash = unsigned.GetSignatureHash();
        Assert.True(pair.Bob.Verifier.Verify(hash, message.Payload.NodeSignature, pair.Alice.NodeId));
        Assert.True(pair.Bob.Verifier.Verify(hash, message.Payload.BitcoinSignature,
                                             pair.Alice.Basepoints.FundingPubKey));
    }

    [Fact]
    public void Given_FiveConfirmations_When_Checked_Then_NotYet()
    {
        // Arrange
        using var pair = new AnnouncementTestPair(tipDepth: 5);

        // Act / Assert
        Assert.False(pair.Alice.Service.CanSendAnnouncementSignatures(pair.Alice.Channel));
        Assert.Throws<InvalidOperationException>(
            () => pair.Alice.Service.CreateAnnouncementSignatures(pair.Alice.Channel));
    }

    [Theory]
    [InlineData(1u, 1u, true)]
    [InlineData(3u, 2u, false)]
    [InlineData(3u, 3u, true)]
    public void Given_ARegtestDepth_When_Checked_Then_ItApplies(uint configured, uint depth, bool expected)
    {
        // Arrange (only regtest may announce below 6 confirmations)
        using var pair = new AnnouncementTestPair(tipDepth: depth,
                                                  gossipOptions: new GossipOptions { AnnouncementDepth = configured });

        // Act / Assert
        Assert.Equal(expected, pair.Alice.Service.CanSendAnnouncementSignatures(pair.Alice.Channel));
    }

    [Fact]
    public void Given_AShallowConfiguredDepth_When_NotOnRegtest_Then_SixIsEnforced()
    {
        // Arrange
        var options = new GossipOptions { AnnouncementDepth = 1 };

        // Act / Assert
        Assert.Equal(6u, options.GetAnnouncementDepth(BitcoinNetwork.Mainnet));
        Assert.Equal(6u, options.GetAnnouncementDepth(BitcoinNetwork.Signet));
        Assert.Equal(1u, options.GetAnnouncementDepth(BitcoinNetwork.Regtest));
        Assert.Equal(8u, new GossipOptions { AnnouncementDepth = 8 }.GetAnnouncementDepth(BitcoinNetwork.Mainnet));
    }

    [Fact]
    public void Given_APrivateChannel_When_Checked_Then_NeverAnnounced()
    {
        // Arrange
        using var pair = new AnnouncementTestPair(announce: false);

        // Act / Assert
        Assert.False(pair.Alice.Service.CanSendAnnouncementSignatures(pair.Alice.Channel));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_AShutdown_When_Checked_Then_NotAnnounced(bool ours)
    {
        // Arrange (BOLT 7: MUST NOT send announcement_signatures once a shutdown was sent)
        using var pair = new AnnouncementTestPair();
        var script = new BitcoinScript(Enumerable.Repeat((byte)0x51, 22).ToArray());
        if (ours)
            pair.Alice.Channel.SetLocalShutdownScript(script);
        else
            pair.Alice.Channel.SetRemoteShutdownScript(script);

        // Act / Assert
        Assert.False(pair.Alice.Service.CanSendAnnouncementSignatures(pair.Alice.Channel));
    }

    [Fact]
    public void Given_DataLoss_When_Checked_Then_NotAnnounced()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        pair.Alice.Channel.MarkDataLossDetected();

        // Act / Assert
        Assert.False(pair.Alice.Service.CanSendAnnouncementSignatures(pair.Alice.Channel));
    }

    [Fact]
    public void Given_TwoPeers_When_OneReconnects_Then_OnlyItsChannelsAreForgotten()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var service = pair.Alice.Service;
        var otherChannel = new ChannelId(Enumerable.Repeat((byte)0x77, 32).ToArray());
        service.MarkSentOnConnection(AnnouncementTestPair.ChannelId, pair.Bob.NodeId);
        service.MarkSentOnConnection(otherChannel, pair.Alice.NodeId);

        // Act
        service.OnPeerConnectionChanged(pair.Bob.NodeId);

        // Assert
        Assert.False(service.WasSentOnConnection(AnnouncementTestPair.ChannelId));
        Assert.True(service.WasSentOnConnection(otherChannel));
    }

    [Fact]
    public void Given_OnlyTheirHalf_When_Assembling_Then_NothingUntilOursWasSent()
    {
        // Arrange (BOLT 7: queue the announcement once it "has sent AND received" announcement_signatures)
        using var pair = new AnnouncementTestPair();
        var theirs = pair.Bob.Service.CreateAnnouncementSignatures(pair.Bob.Channel).Payload;
        pair.Alice.Channel.SetRemoteAnnouncementSignatures(
            new ChannelAnnouncementSignatures(theirs.NodeSignature, theirs.BitcoinSignature));

        // Act / Assert
        Assert.Null(pair.Alice.Service.TryAssembleAnnouncement(pair.Alice.Channel));
        pair.Alice.Channel.MarkAnnouncementSignaturesSent(DateTimeOffset.UtcNow);
        Assert.NotNull(pair.Alice.Service.TryAssembleAnnouncement(pair.Alice.Channel));
    }

    [Fact]
    public void Given_TheSameAnnouncementTwice_When_HandedOn_Then_TheSinkGetsItOnce()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var theirs = pair.Bob.Service.CreateAnnouncementSignatures(pair.Bob.Channel).Payload;
        var channel = pair.Alice.Channel;
        channel.SetRemoteAnnouncementSignatures(
            new ChannelAnnouncementSignatures(theirs.NodeSignature, theirs.BitcoinSignature));
        channel.MarkAnnouncementSignaturesSent(DateTimeOffset.UtcNow);
        var announcement = pair.Alice.Service.TryAssembleAnnouncement(channel)!;

        // Act
        pair.Alice.Service.OnChannelAnnounced(channel, announcement);
        pair.Alice.Service.OnChannelAnnounced(channel, announcement);

        // Assert
        var (recorded, capacity) = Assert.Single(pair.Alice.Sink.ChannelAnnouncements);
        Assert.Same(announcement, recorded);
        Assert.Equal(AnnouncementTestPair.Capacity, capacity);
    }

    [Fact]
    public void Given_EitherNodeOrder_When_Assembled_Then_EachHalfIsInItsNodesSlot()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var alice = pair.Alice.Service.CreateAnnouncementSignatures(pair.Alice.Channel).Payload;
        var bob = pair.Bob.Service.CreateAnnouncementSignatures(pair.Bob.Channel).Payload;
        var aliceHalf = new ChannelAnnouncementSignatures(alice.NodeSignature, alice.BitcoinSignature);
        var bobHalf = new ChannelAnnouncementSignatures(bob.NodeSignature, bob.BitcoinSignature);
        var chainHash = pair.Alice.NodeOptions.BitcoinNetwork.ChainHash;

        // Act: assembled from each end's point of view
        var fromAlice = ChannelAnnouncementBuilder.Assemble(
            ChannelAnnouncementBuilder.BuildUnsigned(pair.Alice.Channel, AnnouncementTestPair.ShortChannelId,
                                                     pair.Alice.NodeId, chainHash), pair.Alice.NodeId, aliceHalf,
            bobHalf);
        var fromBob = ChannelAnnouncementBuilder.Assemble(
            ChannelAnnouncementBuilder.BuildUnsigned(pair.Bob.Channel, AnnouncementTestPair.ShortChannelId,
                                                     pair.Bob.NodeId, chainHash), pair.Bob.NodeId, bobHalf,
            aliceHalf);

        // Assert
        Assert.Equal(fromAlice.GetBytes(), fromBob.GetBytes());
        var aliceIsNode1 = ChannelAnnouncementBuilder.IsNode1(pair.Alice.NodeId, pair.Bob.NodeId);
        Assert.Equal(aliceIsNode1 ? pair.Alice.NodeId : pair.Bob.NodeId, fromAlice.NodeId1);
        Assert.Equal(aliceIsNode1 ? pair.Alice.Basepoints.FundingPubKey : pair.Bob.Basepoints.FundingPubKey,
                     fromAlice.BitcoinKey1);
        Assert.Equal(aliceIsNode1 ? alice.NodeSignature : bob.NodeSignature, fromAlice.NodeSignature1);
        Assert.Equal(!aliceIsNode1, ChannelAnnouncementBuilder.IsNode1(pair.Bob.NodeId, pair.Alice.NodeId));
        Assert.True(pair.Alice.Verifier.VerifyAll(ChannelAnnouncementBuilder.GetAllSignatureChecks(fromAlice)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Given_Mainnet_When_Checked_Then_AnnouncedOnlyWhenPublicChannelsAreAllowed(bool allow, bool expected)
    {
        // Arrange (plan D12: a public channel a peer opened to us is not announced on mainnet before Proof G1)
        using var pair = new AnnouncementTestPair(
            gossipOptions: new GossipOptions { AllowPublicChannelsOnMainnet = allow });
        pair.Alice.NodeOptions.BitcoinNetwork = BitcoinNetwork.Mainnet;

        // Act / Assert
        Assert.Equal(expected, pair.Alice.Service.CanSendAnnouncementSignatures(pair.Alice.Channel));
    }

    [Fact]
    public async Task Given_OurHalfDue_When_Prepared_Then_SavedMarkedAndNotRepeatedOnTheConnection()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var channel = pair.Alice.Channel;

        // Act
        var first = await pair.Alice.Service.PrepareOwnAnnouncementSignaturesAsync(channel, pair.Bob.NodeId,
                                                                                   pair.Alice.UnitOfWork.Object);
        var again = await pair.Alice.Service.PrepareOwnAnnouncementSignaturesAsync(channel, pair.Bob.NodeId,
                                                                                   pair.Alice.UnitOfWork.Object);

        // Assert: persisted before it is handed out, once per connection
        Assert.NotNull(first);
        Assert.Null(again);
        Assert.NotNull(channel.LocalAnnouncementSignaturesSentAt);
        Assert.Equal(1, pair.Alice.Saves);
        pair.Alice.ChannelDb.Verify(r => r.UpdateAsync(channel), Times.Once);
        Assert.True(pair.Alice.Service.WasSentOnConnection(AnnouncementTestPair.ChannelId));
    }

    [Fact]
    public async Task Given_OurHalfLost_When_TheConnectionChanges_Then_ItIsDueAgain()
    {
        // Arrange (BOLT 7: on reconnection, while the peer's half is missing, MUST send its own again)
        using var pair = new AnnouncementTestPair();
        var channel = pair.Alice.Channel;
        await pair.Alice.Service.PrepareOwnAnnouncementSignaturesAsync(channel, pair.Bob.NodeId,
                                                                       pair.Alice.UnitOfWork.Object);

        // Act
        pair.Alice.Service.OnPeerConnectionChanged(pair.Bob.NodeId);
        var retransmitted = await pair.Alice.Service.PrepareOwnAnnouncementSignaturesAsync(
                                channel, pair.Bob.NodeId, pair.Alice.UnitOfWork.Object);

        // Assert
        Assert.NotNull(retransmitted);
    }

    [Fact]
    public async Task Given_AStoredHalfThatDoesNotSignTheAnnouncement_When_Completing_Then_ItIsForgottenAndOursIsDueAgain()
    {
        // Arrange: both halves count as exchanged, but the stored peer half does not verify for the current short
        // channel id (stored before our funding confirmation, or before a reorg moved it)
        using var pair = new AnnouncementTestPair();
        var channel = pair.Alice.Channel;
        var ours = await pair.Alice.Service.PrepareOwnAnnouncementSignaturesAsync(channel, pair.Bob.NodeId,
                                                                                  pair.Alice.UnitOfWork.Object);
        var sentAt = channel.LocalAnnouncementSignaturesSentAt;
        var theirs = pair.Bob.Service.CreateAnnouncementSignatures(pair.Bob.Channel).Payload;
        channel.SetRemoteAnnouncementSignatures(
            new ChannelAnnouncementSignatures(theirs.BitcoinSignature, theirs.NodeSignature));
        Assert.True(ChannelAnnouncementService.IsAnnounced(channel));
        Assert.True(ChannelUpdateService.IsPublic(channel));
        var savesBefore = pair.Alice.Saves;

        // Act
        await pair.Alice.Service.CompleteAnnouncementAsync(channel, pair.Alice.UnitOfWork.Object);
        await pair.Alice.Service.CompleteAnnouncementAsync(channel, pair.Alice.UnitOfWork.Object);
        pair.Alice.Service.OnPeerConnectionChanged(pair.Bob.NodeId);
        var again = await pair.Alice.Service.PrepareOwnAnnouncementSignaturesAsync(channel, pair.Bob.NodeId,
                                                                                   pair.Alice.UnitOfWork.Object);

        // Assert: forgotten and saved once, no longer public, nothing handed on, ours sent again on the new connection
        Assert.NotNull(ours);
        Assert.Null(channel.RemoteAnnouncementSignatures);
        Assert.Equal(savesBefore + 2, pair.Alice.Saves);
        pair.Alice.ChannelDb.Verify(r => r.UpdateAsync(It.Is<ChannelModel>(c => c.RemoteAnnouncementSignatures == null)),
                                    Times.AtLeastOnce);
        Assert.False(ChannelAnnouncementService.IsAnnounced(channel));
        Assert.False(ChannelUpdateService.IsPublic(channel));
        Assert.False(pair.Alice.Service.IsAnnouncementComplete(AnnouncementTestPair.ChannelId));
        Assert.Empty(pair.Alice.Sink.ChannelAnnouncements);
        Assert.NotNull(again);
        Assert.NotNull(sentAt);
    }

    [Fact]
    public async Task Given_BothHalvesExchanged_When_Reconnected_Then_NothingIsDue()
    {
        // Arrange (a peer that lacks ours sends its own on reconnection, and gets ours as the reply)
        using var pair = new AnnouncementTestPair();
        var channel = pair.Alice.Channel;
        var theirs = pair.Bob.Service.CreateAnnouncementSignatures(pair.Bob.Channel).Payload;
        channel.SetRemoteAnnouncementSignatures(
            new ChannelAnnouncementSignatures(theirs.NodeSignature, theirs.BitcoinSignature));
        channel.MarkAnnouncementSignaturesSent(DateTimeOffset.UtcNow);

        // Act
        var due = await pair.Alice.Service.PrepareOwnAnnouncementSignaturesAsync(channel, pair.Bob.NodeId,
                                                                                 pair.Alice.UnitOfWork.Object);

        // Assert
        Assert.Null(due);
        Assert.Equal(0, pair.Alice.Saves);
    }

    [Fact]
    public async Task Given_TheirHalfFirst_When_OursIsDue_Then_SentAndTheAnnouncementCompletes()
    {
        // Arrange (their half arrived before our funding had 6 confirmations)
        using var pair = new AnnouncementTestPair();
        var channel = pair.Alice.Channel;
        var theirs = pair.Bob.Service.CreateAnnouncementSignatures(pair.Bob.Channel).Payload;
        channel.SetRemoteAnnouncementSignatures(
            new ChannelAnnouncementSignatures(theirs.NodeSignature, theirs.BitcoinSignature));

        // Act
        var ours = await pair.Alice.Service.PrepareOwnAnnouncementSignaturesAsync(channel, pair.Bob.NodeId,
                                                                                  pair.Alice.UnitOfWork.Object);
        await pair.Alice.Service.CompleteAnnouncementAsync(channel, pair.Alice.UnitOfWork.Object);
        await pair.Alice.Service.CompleteAnnouncementAsync(channel, pair.Alice.UnitOfWork.Object);

        // Assert
        Assert.NotNull(ours);
        Assert.True(pair.Alice.Service.IsAnnouncementComplete(AnnouncementTestPair.ChannelId));
        var (announcement, _) = Assert.Single(pair.Alice.Sink.ChannelAnnouncements);
        Assert.Same(announcement, Assert.Single(pair.Alice.Relay.Queued));
        Assert.True(ChannelAnnouncementService.IsAnnounced(channel));
    }

    [Fact]
    public async Task Given_AnAnnouncedChannelWhoseScidMoved_When_TheNewBlockIsDeep_Then_ItIsAnnouncedAgainUnderIt()
    {
        // Arrange (NL-350): the channel is announced at its first short channel id on both ends
        using var pair = new AnnouncementTestPair();
        await ExchangeHalvesAndCompleteAsync(pair);
        Assert.True(pair.Alice.Service.IsAnnouncementComplete(AnnouncementTestPair.ChannelId));
        var moved = new ShortChannelId(AnnouncementTestPair.FundingHeight + 3, 9,
                                       AnnouncementTestPair.FundingOutputIndex);

        // Act: a reorg moves the funding transaction (what FundingReconfirmationHandler does on each end, on the same
        // connection), then the new funding block gets 5, then 6 confirmations
        foreach (var node in new[] { pair.Alice, pair.Bob })
        {
            node.Channel.ShortChannelId = moved;
            node.Channel.ResetAnnouncementSignatures();
            node.Service.OnShortChannelIdChanged(AnnouncementTestPair.ChannelId);
            node.Signer.RegisterChannel(AnnouncementTestPair.ChannelId, node.Channel.GetSigningInfo());
            node.Tip = moved.BlockHeight + GossipOptions.MinimumAnnouncementDepth - 2;
        }

        var belowDepth = await pair.Alice.Service.PrepareOwnAnnouncementSignaturesAsync(
                             pair.Alice.Channel, pair.Bob.NodeId, pair.Alice.UnitOfWork.Object);
        pair.Alice.Tip = pair.Bob.Tip = moved.BlockHeight + GossipOptions.MinimumAnnouncementDepth - 1;
        await ExchangeHalvesAndCompleteAsync(pair);

        // Assert: nothing public or complete in between, then both halves for the new short channel id and a second
        // announcement naming it, on both ends
        Assert.Null(belowDepth);
        foreach (var node in new[] { pair.Alice, pair.Bob })
        {
            Assert.True(node.Service.IsAnnouncementComplete(AnnouncementTestPair.ChannelId));
            Assert.Equal([AnnouncementTestPair.ShortChannelId, moved],
                         node.Sink.ChannelAnnouncements.Select(a => a.Announcement.ShortChannelId));
            Assert.True(ChannelAnnouncementService.IsAnnounced(node.Channel));
        }

        Assert.Equal(pair.Alice.Sink.ChannelAnnouncements[1].Announcement.GetBytes(),
                     pair.Bob.Sink.ChannelAnnouncements[1].Announcement.GetBytes());
    }

    [Fact]
    public async Task Given_ScidMoved_When_Checked_Then_TheOldAnnouncementNoLongerCountsAndOursIsDueOnTheConnection()
    {
        // Arrange (NL-350)
        using var pair = new AnnouncementTestPair();
        await ExchangeHalvesAndCompleteAsync(pair);
        Assert.True(pair.Alice.Service.WasSentOnConnection(AnnouncementTestPair.ChannelId));

        // Act
        pair.Alice.Service.OnShortChannelIdChanged(AnnouncementTestPair.ChannelId);

        // Assert
        Assert.False(pair.Alice.Service.IsAnnouncementComplete(AnnouncementTestPair.ChannelId));
        Assert.False(pair.Alice.Service.WasSentOnConnection(AnnouncementTestPair.ChannelId));
        Assert.True(pair.Bob.Service.IsAnnouncementComplete(AnnouncementTestPair.ChannelId));
    }

    [Fact]
    public void Given_OurOwnNodeId_When_Ordered_Then_Refused()
    {
        // Arrange
        CompactPubKey key = Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => ChannelAnnouncementBuilder.IsNode1(key, key));
    }

    /// <summary>Both ends send their half on the current connection, store the other's, and complete.</summary>
    private static async Task ExchangeHalvesAndCompleteAsync(AnnouncementTestPair pair)
    {
        var fromAlice = await pair.Alice.Service.PrepareOwnAnnouncementSignaturesAsync(
                            pair.Alice.Channel, pair.Bob.NodeId, pair.Alice.UnitOfWork.Object);
        var fromBob = await pair.Bob.Service.PrepareOwnAnnouncementSignaturesAsync(
                          pair.Bob.Channel, pair.Alice.NodeId, pair.Bob.UnitOfWork.Object);
        Assert.NotNull(fromAlice);
        Assert.NotNull(fromBob);
        Assert.Equal(pair.Alice.Channel.ShortChannelId, fromAlice.Payload.ShortChannelId);
        pair.Alice.Channel.SetRemoteAnnouncementSignatures(
            new ChannelAnnouncementSignatures(fromBob.Payload.NodeSignature, fromBob.Payload.BitcoinSignature));
        pair.Bob.Channel.SetRemoteAnnouncementSignatures(
            new ChannelAnnouncementSignatures(fromAlice.Payload.NodeSignature, fromAlice.Payload.BitcoinSignature));
        await pair.Alice.Service.CompleteAnnouncementAsync(pair.Alice.Channel, pair.Alice.UnitOfWork.Object);
        await pair.Bob.Service.CompleteAnnouncementAsync(pair.Bob.Channel, pair.Bob.UnitOfWork.Object);
    }
}