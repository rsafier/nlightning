namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Gossip.Announcements;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Gossip.Announcements;

/// <summary>
/// BOLT 7 plan G1-T3 (NL-342): the peer's <c>announcement_signatures</c> handled with real signers on both ends.
/// </summary>
public class AnnouncementSignaturesMessageHandlerTests
{
    private static readonly FeatureOptions s_features = new();

    [Fact]
    public async Task Given_BothAtDepth_When_TheyExchangeAnnouncementSignatures_Then_BothAssembleTheSameVerifiedAnnouncement()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var alicesHalf = SendOwn(pair.Alice);

        // Act: Bob gets Alice's half and replies with his; Alice gets Bob's
        var bobsReplies = await pair.Bob.Handler.HandleAsync(alicesHalf, ChannelState.Open, s_features,
                                                             pair.Alice.NodeId);
        var bobsHalf = Assert.IsType<AnnouncementSignaturesMessage>(Assert.Single(bobsReplies));
        var alicesReplies = await pair.Alice.Handler.HandleAsync(bobsHalf, ChannelState.Open, s_features,
                                                                 pair.Bob.NodeId);

        // Assert: Alice already sent hers on this connection, so she does not answer again (no ping-pong)
        Assert.Empty(alicesReplies);
        var (aliceAnnouncement, aliceCapacity) = Assert.Single(pair.Alice.Sink.ChannelAnnouncements);
        var (bobAnnouncement, bobCapacity) = Assert.Single(pair.Bob.Sink.ChannelAnnouncements);
        Assert.Equal(aliceAnnouncement.GetBytes(), bobAnnouncement.GetBytes());
        Assert.Equal(AnnouncementTestPair.Capacity, aliceCapacity);
        Assert.Equal(AnnouncementTestPair.Capacity, bobCapacity);
        Assert.Equal(AnnouncementTestPair.ShortChannelId, aliceAnnouncement.ShortChannelId);
        Assert.Equal(BitcoinNetworkChainHash(pair), aliceAnnouncement.ChainHash);
        Assert.True(pair.Alice.Verifier.VerifyAll(ChannelAnnouncementBuilder.GetAllSignatureChecks(aliceAnnouncement)));
        Assert.True(((ReadOnlySpan<byte>)aliceAnnouncement.NodeId1).SequenceCompareTo(aliceAnnouncement.NodeId2) < 0);
        Assert.True(aliceAnnouncement.Features.IsEmpty);

        // Both stored the peer's half and the time they sent theirs, persisted before sending
        Assert.Equal(bobsHalf.Payload.NodeSignature, pair.Alice.Channel.RemoteAnnouncementSignatures!.NodeSignature);
        Assert.Equal(alicesHalf.Payload.BitcoinSignature,
                     pair.Bob.Channel.RemoteAnnouncementSignatures!.BitcoinSignature);
        Assert.NotNull(pair.Bob.Channel.LocalAnnouncementSignaturesSentAt);
        Assert.Equal(1, pair.Bob.Saves);
        Assert.Equal(1, pair.Alice.Saves);
    }

    [Fact]
    public async Task Given_AnnouncementSignatures_When_TheShortChannelIdDiffers_Then_WarningWithoutClosingAndNothingStored()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var half = SendOwn(pair.Alice);
        var other = new ShortChannelId(AnnouncementTestPair.FundingHeight + 1, 7, AnnouncementTestPair.FundingOutputIndex);
        var message = new AnnouncementSignaturesMessage(
            new AnnouncementSignaturesPayload(AnnouncementTestPair.ChannelId, other, half.Payload.NodeSignature,
                                              half.Payload.BitcoinSignature));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => pair.Bob.Handler.HandleAsync(message, ChannelState.Open, s_features,
                                                               pair.Alice.NodeId));

        // Assert
        Assert.False(exception.CloseConnection);
        Assert.Equal(AnnouncementTestPair.ChannelId, exception.ChannelId);
        Assert.Null(pair.Bob.Channel.RemoteAnnouncementSignatures);
        Assert.Equal(0, pair.Bob.Saves);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AnnouncementSignatures_When_ASignatureIsInvalid_Then_WarningAndCloseAndNothingStored(
        bool badNodeSignature)
    {
        // Arrange: a signature of the right hash by the wrong key
        using var pair = new AnnouncementTestPair();
        var half = SendOwn(pair.Alice).Payload;
        var message = new AnnouncementSignaturesMessage(
            new AnnouncementSignaturesPayload(half.ChannelId, half.ShortChannelId,
                                              badNodeSignature ? half.BitcoinSignature : half.NodeSignature,
                                              badNodeSignature ? half.BitcoinSignature : half.NodeSignature));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => pair.Bob.Handler.HandleAsync(message, ChannelState.Open, s_features,
                                                               pair.Alice.NodeId));

        // Assert: warning and close (BOLT 7 MAY; plan D10), the channel is not failed
        Assert.True(exception.CloseConnection);
        Assert.Equal(ChannelState.Open, pair.Bob.Channel.State);
        Assert.Null(pair.Bob.Channel.RemoteAnnouncementSignatures);
        Assert.Equal(0, pair.Bob.Saves);
        Assert.Empty(pair.Bob.Sink.ChannelAnnouncements);
    }

    [Fact]
    public async Task Given_BelowTheAnnouncementDepth_When_ThePeersHalfArrives_Then_ItIsStoredWithoutReplyOrAnnouncement()
    {
        // Arrange: Alice's chain is 6 deep, Bob's only 5
        using var pair = new AnnouncementTestPair();
        var half = SendOwn(pair.Alice);
        pair.Bob.Tip = AnnouncementTestPair.FundingHeight + 4;

        // Act
        var replies = await pair.Bob.Handler.HandleAsync(half, ChannelState.Open, s_features, pair.Alice.NodeId);

        // Assert
        Assert.Empty(replies);
        Assert.NotNull(pair.Bob.Channel.RemoteAnnouncementSignatures);
        Assert.Null(pair.Bob.Channel.LocalAnnouncementSignaturesSentAt);
        Assert.Equal(1, pair.Bob.Saves);
        Assert.Empty(pair.Bob.Sink.ChannelAnnouncements);
        Assert.False(pair.Bob.Service.CanSendAnnouncementSignatures(pair.Bob.Channel));

        // Once deep enough Bob may send his and assemble the announcement
        pair.Bob.Tip = AnnouncementTestPair.FundingHeight + 5;
        Assert.True(pair.Bob.Service.CanSendAnnouncementSignatures(pair.Bob.Channel));
    }

    [Fact]
    public async Task Given_BeforeOurChannelReady_When_ThePeersHalfArrives_Then_ItIsStoredAndDeferred()
    {
        // Arrange: Bob has not seen the funding confirm (no short channel id, channel_ready not sent)
        using var pair = new AnnouncementTestPair();
        var half = SendOwn(pair.Alice);
        var early = RebuildWithState(pair.Bob, ChannelState.ReadyForThem, withShortChannelId: false);

        // Act
        var replies = await pair.Bob.Handler.HandleAsync(half, ChannelState.ReadyForThem, s_features,
                                                         pair.Alice.NodeId);

        // Assert: stored (checked against the output index only) and persisted, nothing sent or announced
        Assert.Empty(replies);
        Assert.NotNull(early.RemoteAnnouncementSignatures);
        Assert.Equal(1, pair.Bob.Saves);
        Assert.Empty(pair.Bob.Sink.ChannelAnnouncements);
    }

    [Fact]
    public async Task Given_ADeferredHalfForAnotherShortChannelId_When_TheFundingConfirmsElsewhere_Then_NoAnnouncementIsAssembled()
    {
        // Arrange: Bob stores a half for a short channel id at another block, then confirms at the real one
        using var pair = new AnnouncementTestPair();
        var early = RebuildWithState(pair.Bob, ChannelState.ReadyForThem, withShortChannelId: false);
        var elsewhere = new ShortChannelId(AnnouncementTestPair.FundingHeight - 3, 2,
                                           AnnouncementTestPair.FundingOutputIndex);
        var aliceChannel = pair.Alice.Channel;
        aliceChannel.ShortChannelId = elsewhere;
        pair.Alice.Signer.RegisterChannel(aliceChannel.ChannelId, aliceChannel.GetSigningInfo());
        pair.Alice.Tip = AnnouncementTestPair.FundingHeight + 10;
        var half = SendOwn(pair.Alice);
        await pair.Bob.Handler.HandleAsync(half, ChannelState.ReadyForThem, s_features, pair.Alice.NodeId);

        // Act: Bob's funding confirms at the real short channel id and he sends his half
        early.ShortChannelId = AnnouncementTestPair.ShortChannelId;
        early.UpdateState(ChannelState.Open);
        pair.Bob.Signer.RegisterChannel(early.ChannelId, early.GetSigningInfo());
        early.MarkAnnouncementSignaturesSent(DateTimeOffset.UtcNow);
        var announcement = pair.Bob.Service.TryAssembleAnnouncement(early);

        // Assert: the stored half signs another announcement, so nothing is assembled and the half is forgotten (the
        // channel is not public, and ours goes out again on the next connection)
        Assert.Null(announcement);
        Assert.Null(early.RemoteAnnouncementSignatures);
        Assert.NotNull(early.LocalAnnouncementSignaturesSentAt);
        Assert.False(ChannelAnnouncementService.IsAnnounced(early));
    }

    [Fact]
    public async Task Given_TheSameHalfAgain_When_OnTheSameConnection_Then_NothingIsSavedSentOrAnnouncedAgain()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var half = SendOwn(pair.Alice);
        await pair.Bob.Handler.HandleAsync(half, ChannelState.Open, s_features, pair.Alice.NodeId);

        // Act
        var replies = await pair.Bob.Handler.HandleAsync(half, ChannelState.Open, s_features, pair.Alice.NodeId);

        // Assert
        Assert.Empty(replies);
        Assert.Equal(1, pair.Bob.Saves);
        Assert.Single(pair.Bob.Sink.ChannelAnnouncements);
    }

    [Fact]
    public async Task Given_TheSameHalfAfterAReconnection_When_Handled_Then_OursIsSentAgain()
    {
        // Arrange: BOLT 7 "if it receives announcement_signatures for the funding transaction: MUST respond with its
        // own" on reconnection
        using var pair = new AnnouncementTestPair();
        var half = SendOwn(pair.Alice);
        var first = await pair.Bob.Handler.HandleAsync(half, ChannelState.Open, s_features, pair.Alice.NodeId);
        pair.Bob.Service.OnPeerConnectionChanged(pair.Alice.NodeId);

        // Act
        var replies = await pair.Bob.Handler.HandleAsync(half, ChannelState.Open, s_features, pair.Alice.NodeId);

        // Assert: the same (deterministic) half again, its send time saved; the announcement is not handed on twice
        var again = Assert.IsType<AnnouncementSignaturesMessage>(Assert.Single(replies));
        Assert.Equal(((AnnouncementSignaturesMessage)Assert.Single(first)).Payload.GetBytes(), again.Payload.GetBytes());
        Assert.Equal(2, pair.Bob.Saves);
        Assert.Single(pair.Bob.Sink.ChannelAnnouncements);
    }

    [Fact]
    public async Task Given_APrivateChannel_When_AnnouncementSignaturesArrive_Then_WarningWithoutClosing()
    {
        // Arrange: Alice signs as if public; Bob's channel is private
        using var pair = new AnnouncementTestPair();
        var half = SendOwn(pair.Alice);
        using var privatePair = new AnnouncementTestPair(announce: false);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => privatePair.Bob.Handler.HandleAsync(half, ChannelState.Open, s_features,
                                                                      privatePair.Alice.NodeId));

        // Assert
        Assert.False(exception.CloseConnection);
        Assert.Equal(0, privatePair.Bob.Saves);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AShutdown_When_AnnouncementSignaturesArrive_Then_TheyAreIgnored(bool ours)
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var half = SendOwn(pair.Alice);
        var script = new BitcoinScript(Enumerable.Repeat((byte)0x51, 22).ToArray());
        if (ours)
            pair.Bob.Channel.SetLocalShutdownScript(script);
        else
            pair.Bob.Channel.SetRemoteShutdownScript(script);

        // Act
        var replies = await pair.Bob.Handler.HandleAsync(half, ChannelState.Open, s_features, pair.Alice.NodeId);

        // Assert: BOLT 7, nothing is announced once a shutdown was sent
        Assert.Empty(replies);
        Assert.Null(pair.Bob.Channel.RemoteAnnouncementSignatures);
        Assert.Equal(0, pair.Bob.Saves);
        Assert.Empty(pair.Bob.Sink.ChannelAnnouncements);
    }

    [Fact]
    public async Task Given_AChannelThatIsNotLoaded_When_AnnouncementSignaturesArrive_Then_TheyAreIgnored()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var half = SendOwn(pair.Alice);
        pair.Bob.Channels.TryRemoveChannel(AnnouncementTestPair.ChannelId);

        // Act
        var replies = await pair.Bob.Handler.HandleAsync(half, ChannelState.None, s_features, pair.Alice.NodeId);

        // Assert
        Assert.Empty(replies);
        Assert.Equal(0, pair.Bob.Saves);
    }

    [Fact]
    public async Task Given_AnotherPeer_When_ItSendsAnnouncementSignaturesForOurChannel_Then_Error()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var half = SendOwn(pair.Alice);

        // Act / Assert
        await Assert.ThrowsAsync<ChannelErrorException>(
            () => pair.Bob.Handler.HandleAsync(half, ChannelState.Open, s_features, pair.Bob.NodeId));
        Assert.Equal(0, pair.Bob.Saves);
    }

    /// <summary>The node's own half, marked sent on its connection as the announcement service does.</summary>
    private static AnnouncementSignaturesMessage SendOwn(AnnouncementTestNode node)
    {
        var message = node.Service.CreateAnnouncementSignatures(node.Channel);
        node.Channel.MarkAnnouncementSignaturesSent(DateTimeOffset.UtcNow);
        node.Service.MarkSentOnConnection(node.Channel.ChannelId, node.Channel.RemoteNodeId);
        return message;
    }

    /// <summary>Replaces the node's channel by a copy in <paramref name="state"/>, registered as such.</summary>
    private static ChannelModel RebuildWithState(AnnouncementTestNode node, ChannelState state,
                                                 bool withShortChannelId)
    {
        var c = node.Channel;
        var copy = new ChannelModel(c.ChannelParams, c.ChannelId, c.CommitmentNumber, c.FundingOutput, c.IsInitiator,
                                    null, null, c.LocalBalance, c.LocalKeySet, 0, 0, c.RemoteBalance, c.RemoteKeySet,
                                    0, c.RemoteNodeId, 0, state, c.Version);
        if (withShortChannelId)
            copy.ShortChannelId = c.ShortChannelId;
        node.Channels.AddChannel(copy);
        return copy;
    }

    private static ChainHash BitcoinNetworkChainHash(AnnouncementTestPair pair) =>
        pair.Alice.NodeOptions.BitcoinNetwork.ChainHash;
}