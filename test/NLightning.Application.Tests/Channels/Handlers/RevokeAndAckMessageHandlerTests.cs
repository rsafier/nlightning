using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using static NormalOperationTestContext;

public class RevokeAndAckMessageHandlerTests
{
    private readonly NormalOperationTestContext _context = new();

    [Fact]
    public async Task Given_OutstandingCommit_When_RevokeAndAck_Then_ShachainIsSavedInTheSameTransition()
    {
        // Arrange - we offered an HTLC and signed the peer's commitment 1
        var state = _context.State.SendAdd(50_000_000, HashOf(SecretOf(1)), 600, Onion).Next;
        _context.SetState(state.SendCommit(_context.Ports).Next);
        var handler = CreateHandler();

        // Act
        var replies = await handler.HandleAsync(CreateRevokeAndAck(SecretOf(0x90), Point(0x22)), ChannelState.Open,
                                                new FeatureOptions(), PeerNodeId);

        // Assert - NL-136 / B2-RAA-R04: the secret of commitment 0 goes into the shachain, saved with the transition
        Assert.Empty(replies);
        _context.Shachain.Verify(s => s.InsertSecret(SecretOf(0x90), PerCommitmentIndex.From(0)), Times.Once);
        Assert.Equal(["apply", "save"], _context.Calls);
        var extras = Assert.Single(_context.Applied).Extras;
        Assert.Same(_context.Shachain.Object.Export().GetType(), extras!.RemoteShachain!.GetType());
        Assert.Single(extras.RemoteShachain);
        Assert.Equal(1UL, _context.State.RemoteCommit.Number);
        Assert.Null(_context.State.RemoteNextCommit);
        Assert.Equal(Point(0x22), _context.State.RemoteNextPerCommitmentPoint);
        _context.Shachain.Verify(s => s.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Given_PeerHtlcLockedInByTheRevoke_When_RevokeAndAck_Then_IncomingHtlcLockedInIsQueuedOnce()
    {
        // Arrange - the peer added, signed; we revoked and signed back; its revoke locks the HTLC in (B2-FWD-01)
        var state = _context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(1)), 600, Onion).Next;
        state = state.ReceiveCommit(_context.Ports.SignaturesFor(state), _context.Ports).Next;
        _context.SetState(state.SendCommit(_context.Ports).Next);
        var handler = CreateHandler();

        // Act
        await handler.HandleAsync(CreateRevokeAndAck(SecretOf(0x90), Point(0x22)), ChannelState.Open,
                                  new FeatureOptions(), PeerNodeId);

        // Assert
        var lockedIn = Assert.IsType<IncomingHtlcLockedIn>(Assert.Single(_context.Events.Drain()));
        Assert.Equal(0UL, lockedIn.HtlcId);
        Assert.Equal(HtlcState.RcvdAddAckRevocation, _context.State.GetHtlc(HtlcDirection.Incoming, 0)!.State);
    }

    [Fact]
    public async Task Given_ChangesPendingAfterTheRevoke_When_RevokeAndAck_Then_OurCommitmentSignedFollows()
    {
        // Arrange - we signed; meanwhile the peer added an HTLC and committed it to our commitment
        var state = _context.State.SendAdd(50_000_000, HashOf(SecretOf(1)), 600, Onion).Next;
        state = state.SendCommit(_context.Ports).Next;
        state = state.ReceiveAdd(0, 40_000_000, HashOf(SecretOf(2)), 600, Onion).Next;
        state = state.ReceiveCommit(_context.Ports.SignaturesFor(state), _context.Ports).Next;
        _context.SetState(state);
        var handler = CreateHandler();

        // Act
        var replies = await handler.HandleAsync(CreateRevokeAndAck(SecretOf(0x90), Point(0x22)), ChannelState.Open,
                                                new FeatureOptions(), PeerNodeId);

        // Assert
        var commitmentSigned = Assert.IsType<CommitmentSignedMessage>(Assert.Single(replies));
        Assert.Equal(2, commitmentSigned.Payload.HtlcSignatures.Count());
        Assert.Equal(["apply", "save", "apply", "save"], _context.Calls);
        Assert.Equal(2UL, _context.State.RemoteNextCommit!.Commit.Number);
    }

    [Fact]
    public async Task Given_WrongSecret_When_RevokeAndAck_Then_TheChannelMustBeFailed()
    {
        // Arrange - B2-RAA-R01: MUST send an error and fail the channel
        var state = _context.State.SendAdd(50_000_000, HashOf(SecretOf(1)), 600, Onion).Next;
        _context.SetState(state.SendCommit(_context.Ports).Next);
        _context.Ports.SecretsValid = false;
        var handler = CreateHandler();

        // Act
        var exception = await Assert.ThrowsAsync<ChannelFailedException>(
                            () => handler.HandleAsync(CreateRevokeAndAck(SecretOf(0x91), Point(0x22)),
                                                      ChannelState.Open, new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.Equal(TestChannelId, exception.FailedChannelId);
        Assert.Equal("B2-RAA-R01", exception.RequirementId);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_SecretOutsideTheShachain_When_RevokeAndAck_Then_WarningAndCloseWithoutPersisting()
    {
        // Arrange - B2-RAA-R02: MAY warn and close
        var state = _context.State.SendAdd(50_000_000, HashOf(SecretOf(1)), 600, Onion).Next;
        _context.SetState(state.SendCommit(_context.Ports).Next);
        _context.Shachain.Setup(s => s.InsertSecret(It.IsAny<Secret>(), It.IsAny<ulong>())).Returns(false);
        var before = _context.State;
        var handler = CreateHandler();

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(CreateRevokeAndAck(SecretOf(0x90), Point(0x22)),
                                                      ChannelState.Open, new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("B2-RAA-R02", exception.Message);
        Assert.Empty(_context.Calls);
        Assert.Same(before, _context.State);
    }

    [Fact]
    public async Task Given_NoOutstandingCommit_When_RevokeAndAck_Then_WarningAndClose()
    {
        // Arrange - B2-RAA-R03
        var handler = CreateHandler();

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(CreateRevokeAndAck(SecretOf(0x90), Point(0x22)),
                                                      ChannelState.Open, new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("B2-RAA-R03", exception.Message);
    }

    [Fact]
    public async Task Given_PersistFails_When_RevokeAndAck_Then_SnapshotUnchangedAndNoEvents()
    {
        // Arrange
        var state = _context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(1)), 600, Onion).Next;
        state = state.ReceiveCommit(_context.Ports.SignaturesFor(state), _context.Ports).Next;
        _context.SetState(state.SendCommit(_context.Ports).Next);
        var before = _context.State;
        _context.FailSaves();
        var handler = CreateHandler();

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(CreateRevokeAndAck(SecretOf(0x90), Point(0x22)), ChannelState.Open,
                                      new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.Same(before, _context.State);
        Assert.Empty(_context.Events.Drain());
    }

    private RevokeAndAckMessageHandler CreateHandler() =>
        new(NullLogger<RevokeAndAckMessageHandler>.Instance, _context.Ports, _context.CreateTransitions());

    private static RevokeAndAckMessage CreateRevokeAndAck(Secret secret, CompactPubKey nextPoint) =>
        new(new RevokeAndAckPayload(TestChannelId, nextPoint, (byte[])secret));
}