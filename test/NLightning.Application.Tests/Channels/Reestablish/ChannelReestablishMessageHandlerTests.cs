using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Reestablish;

using Application.Channels.Handlers;
using Application.Channels.Reestablish;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Handlers;

/// <summary>
/// BOLT2 plan N7-T2..T4: <see cref="ChannelReestablishMessageHandler"/> on a fresh channel (L = R = 0) with mocked
/// persistence and signer (<see cref="NormalOperationTestContext"/>).
/// </summary>
public class ChannelReestablishMessageHandlerTests
{
    private static readonly ChannelId s_channelId = NormalOperationTestContext.TestChannelId;
    private static readonly CompactPubKey s_peer = NormalOperationTestContext.PeerNodeId;

    private readonly NormalOperationTestContext _context;
    private readonly ReestablishTracker _tracker = new();
    private readonly Mock<IRevocationVerifier> _revocationVerifier = new();

    public ChannelReestablishMessageHandlerTests() : this(ChannelState.Open)
    {
    }

    private ChannelReestablishMessageHandlerTests(ChannelState state)
    {
        _context = new NormalOperationTestContext(state: state);
    }

    [Fact]
    public async Task Given_OursNotSentYet_When_BothNextNumbersAreOne_Then_OursThenChannelReadyWithOurNextPoint()
    {
        // Arrange (B2-RE-15; a peer that reestablishes before we did gets ours first)
        var handler = CreateHandler();

        // Act
        var replies = await handler.HandleAsync(Reestablish(1, 0), ChannelState.Open, new FeatureOptions(), s_peer);

        // Assert
        Assert.Equal(2, replies.Count);
        var ours = Assert.IsType<ChannelReestablishMessage>(replies[0]);
        Assert.Equal(1UL, ours.Payload.NextCommitmentNumber);
        Assert.Equal(0UL, ours.Payload.NextRevocationNumber);
        Assert.True(ours.Payload.YourLastPerCommitmentSecret.ToArray().All(b => b == 0));
        Assert.Equal(NormalOperationTestContext.Point(0x40), ours.Payload.MyCurrentPerCommitmentPoint);
        var channelReady = Assert.IsType<ChannelReadyMessage>(replies[1]);
        Assert.Equal(NormalOperationTestContext.Point(0x41), channelReady.Payload.SecondPerCommitmentPoint);
        Assert.Equal(ReestablishStatus.Sent, _tracker.GetStatus(s_channelId));
    }

    [Fact]
    public async Task Given_OursAlreadySent_When_Handled_Then_OnlyTheRetransmissions()
    {
        // Arrange
        var handler = CreateHandler();
        _tracker.MarkSent(s_channelId, s_peer);

        // Act
        var replies = await handler.HandleAsync(Reestablish(1, 0), ChannelState.Open, new FeatureOptions(), s_peer);

        // Assert
        Assert.IsType<ChannelReadyMessage>(Assert.Single(replies));
    }

    [Fact]
    public async Task Given_AlreadyAnsweredOnThisConnection_When_ReestablishRepeats_Then_Ignored()
    {
        // Arrange
        var handler = CreateHandler();
        _tracker.MarkSent(s_channelId, s_peer);
        Assert.True(_tracker.TryMarkReestablished(s_channelId));

        // Act
        var replies = await handler.HandleAsync(Reestablish(1, 0), ChannelState.Open, new FeatureOptions(), s_peer);

        // Assert
        Assert.Empty(replies);
    }

    [Fact]
    public async Task Given_OpenedOnThisConnection_When_ThePeersReestablishArrives_Then_OursThenChannelReady()
    {
        // Arrange - regression: LND sends channel_reestablish after channel_ready for a channel that was pending when
        // the connection started (or whose channel_ready its funding manager queued first) and waits for ours forever
        var handler = CreateHandler();
        _tracker.MarkOpened(s_channelId, s_peer);

        // Act
        var replies = await handler.HandleAsync(Reestablish(1, 0), ChannelState.Open, new FeatureOptions(), s_peer);

        // Assert - ours first, then the plan (channel_ready again: harmless); still usable meanwhile
        Assert.Equal(2, replies.Count);
        Assert.IsType<ChannelReestablishMessage>(replies[0]);
        Assert.IsType<ChannelReadyMessage>(replies[1]);
        Assert.Equal(ReestablishStatus.Sent, _tracker.GetStatus(s_channelId));
        Assert.True(_tracker.IsReestablished(s_channelId));
    }

    [Fact]
    public async Task Given_OursSentThenOpenedOnThisConnection_When_ThePeersReestablishArrives_Then_OnlyThePlan()
    {
        // Arrange - ours went out at connect, then channel_ready opened the channel before the peer's reestablish
        var handler = CreateHandler();
        _tracker.MarkSent(s_channelId, s_peer);
        _tracker.MarkOpened(s_channelId, s_peer);

        // Act
        var replies = await handler.HandleAsync(Reestablish(1, 0), ChannelState.Open, new FeatureOptions(), s_peer);

        // Assert - ours is not sent twice
        Assert.IsType<ChannelReadyMessage>(Assert.Single(replies));
    }

    [Fact]
    public async Task Given_PeerAhead_WithValidSecret_Then_NoBroadcast_And_ErrorSent()
    {
        // Arrange (B2-RE-23): we are at commitment 0, the peer expects revocation 5 and knows our secret 4
        var secret = NormalOperationTestContext.SecretOf(0x99);
        _revocationVerifier.Setup(v => v.IsValidSecret(secret, NormalOperationTestContext.Point(0x44))).Returns(true);
        var handler = CreateHandler();
        _tracker.MarkSent(s_channelId, s_peer);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelFailedException>(
                            () => handler.HandleAsync(Reestablish(1, 5, secret), ChannelState.Open,
                                                      new FeatureOptions(), s_peer));

        // Assert - the flag is saved before the failure leaves (I12), and nothing asks for a broadcast
        Assert.Equal("B2-RE-23", exception.RequirementId);
        Assert.False(exception.MustBroadcast);
        Assert.Equal(s_channelId, exception.FailedChannelId);
        Assert.True(_context.Channel.DataLossDetected);
        _context.ChannelDbRepository.Verify(r => r.UpdateAsync(It.Is<ChannelModel>(c => c.DataLossDetected)),
                                            Times.Once);
        Assert.Contains("save", _context.Calls);
        _context.LightningSigner.Verify(s => s.MarkDataLoss(s_channelId), Times.Once);
    }

    [Fact]
    public async Task Given_PeerAheadWithAWrongSecret_When_Handled_Then_FailedWithoutDataLoss()
    {
        // Arrange
        var handler = CreateHandler();
        _tracker.MarkSent(s_channelId, s_peer);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelFailedException>(
                            () => handler.HandleAsync(Reestablish(1, 5, NormalOperationTestContext.SecretOf(0x98)),
                                                      ChannelState.Open, new FeatureOptions(), s_peer));

        // Assert
        Assert.Equal("B2-RE-21", exception.RequirementId);
        Assert.False(_context.Channel.DataLossDetected);
        _context.LightningSigner.Verify(s => s.MarkDataLoss(It.IsAny<ChannelId>()), Times.Never);
    }

    [Fact]
    public async Task Given_NextCommitmentNumberAhead_When_Handled_Then_TheChannelFails()
    {
        // Arrange (B2-RE-19)
        var handler = CreateHandler();
        _tracker.MarkSent(s_channelId, s_peer);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelFailedException>(
                            () => handler.HandleAsync(Reestablish(5, 0), ChannelState.Open, new FeatureOptions(),
                                                      s_peer));

        // Assert
        Assert.Equal("B2-RE-19", exception.RequirementId);
        Assert.StartsWith("channel_reestablish mismatch", exception.PeerMessage);
    }

    [Fact]
    public async Task Given_NextCommitmentNumberZero_When_Handled_Then_FailAndBroadcastIsAsked()
    {
        // Arrange (B2-RE-14; the broadcast itself is N9-T4)
        var handler = CreateHandler();
        _tracker.MarkSent(s_channelId, s_peer);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelFailedException>(
                            () => handler.HandleAsync(Reestablish(0, 0), ChannelState.Open, new FeatureOptions(),
                                                      s_peer));

        // Assert
        Assert.True(exception.MustBroadcast);
    }

    [Fact]
    public async Task Given_NextFundingOnV1_When_Handled_Then_TxAbortIsSent()
    {
        // Arrange (B2-RE-25)
        var handler = CreateHandler();
        _tracker.MarkSent(s_channelId, s_peer);
        var message = new ChannelReestablishMessage(
            new ChannelReestablishPayload(s_channelId, NormalOperationTestContext.Point(0x30), 1, 0, new byte[32]),
            new NextFundingTlv(new byte[32], 1));

        // Act
        var replies = await handler.HandleAsync(message, ChannelState.Open, new FeatureOptions(), s_peer);

        // Assert
        Assert.IsType<TxAbortMessage>(replies[0]);
        Assert.IsType<ChannelReadyMessage>(replies[1]);
    }

    [Fact]
    public async Task Given_FailedChannel_When_ReestablishArrives_Then_TheErrorIsSentAgain()
    {
        // Arrange (B2-RE-05)
        var context = new ChannelReestablishMessageHandlerTests(ChannelState.Failed);
        var handler = context.CreateHandler();

        // Act / Assert
        var exception = await Assert.ThrowsAsync<ChannelFailedException>(
                            () => handler.HandleAsync(Reestablish(1, 0), ChannelState.Failed, new FeatureOptions(),
                                                      s_peer));
        Assert.Equal(s_channelId, exception.FailedChannelId);
    }

    [Fact]
    public async Task Given_ChannelWaitingForOurConfirmation_When_BothNextNumbersAreOne_Then_NoChannelReady()
    {
        // Arrange - we never sent channel_ready (ReadyForThem): there is nothing to retransmit
        var context = new ChannelReestablishMessageHandlerTests(ChannelState.ReadyForThem);
        var handler = context.CreateHandler();

        // Act
        var replies = await handler.HandleAsync(Reestablish(1, 0), ChannelState.ReadyForThem, new FeatureOptions(),
                                                s_peer);

        // Assert - only ours
        Assert.IsType<ChannelReestablishMessage>(Assert.Single(replies));
    }

    [Fact]
    public async Task Given_ChannelOfAnotherPeer_When_ReestablishArrives_Then_ErrorForThatChannel()
    {
        // Arrange
        var handler = CreateHandler();

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => handler.HandleAsync(Reestablish(1, 0), ChannelState.Open, new FeatureOptions(),
                                                      NormalOperationTestContext.Point(0x55)));

        // Assert
        Assert.Equal(s_channelId, exception.ChannelId);
    }

    private ChannelReestablishMessageHandler CreateHandler()
    {
        var transitions = _context.CreateTransitions();
        var service = new ReestablishService(_context.LightningSigner.Object, NullLogger<ReestablishService>.Instance,
                                             _context.MessageFactory, _revocationVerifier.Object, transitions);
        return new ChannelReestablishMessageHandler(_context.ChannelMemoryRepository.Object,
                                                    _context.LightningSigner.Object,
                                                    NullLogger<ChannelReestablishMessageHandler>.Instance,
                                                    _context.MessageFactory, _context.MessageSerializer.Object,
                                                    service, _tracker, transitions, _context.UnitOfWork.Object);
    }

    private static ChannelReestablishMessage Reestablish(ulong nextCommitment, ulong nextRevocation,
                                                         Secret? secret = null) =>
        new(new ChannelReestablishPayload(s_channelId, NormalOperationTestContext.Point(0x30), nextCommitment,
                                          nextRevocation, secret is { } s ? (byte[])s : new byte[32]));
}