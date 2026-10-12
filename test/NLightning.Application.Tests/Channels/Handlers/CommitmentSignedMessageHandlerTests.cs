using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Application.Channels.Services;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using static NormalOperationTestContext;

public class CommitmentSignedMessageHandlerTests
{
    private readonly NormalOperationTestContext _context = new();

    [Fact]
    public async Task Given_PeerAddAndValidCommit_When_CommitmentSigned_Then_RevokeAndAckAfterTheSaveThenOurCommit()
    {
        // Arrange - the peer added an HTLC and signs our commitment with it
        _context.SetState(_context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(1)), 600, Onion).Next);
        var handler = CreateHandler();

        // Act
        var replies = await handler.HandleAsync(CreateCommitmentSigned(_context.Ports.SignaturesFor(_context.State)),
                                                ChannelState.Open, new FeatureOptions(), PeerNodeId);

        // Assert - B2-CS-R06 then I3: the secret of commitment 0 is released only after commitment 1 is saved
        Assert.Equal(["apply", "save", "advance 1", "reveal 0", "apply", "save"], _context.Calls);
        Assert.Equal(2, replies.Count);
        var revokeAndAck = Assert.IsType<RevokeAndAckMessage>(replies[0]);
        Assert.Equal(SecretOf(0), revokeAndAck.Payload.PerCommitmentSecret.ToArray());
        Assert.Equal(Point(0x42), revokeAndAck.Payload.NextPerCommitmentPoint);
        var commitmentSigned = Assert.IsType<CommitmentSignedMessage>(replies[1]);
        Assert.Single(commitmentSigned.Payload.HtlcSignatures);
        Assert.Equal(_context.Channel.FundingOutput!.TransactionId, commitmentSigned.FundingTxIdTlv!.FundingTxId);

        // Both transitions carried the retransmission data (D4, LastSentOrder)
        Assert.Equal(LastSentCommitmentMessage.RevokeAndAck, _context.Applied[0].Extras!.LastSent);
        Assert.Equal(LastSentCommitmentMessage.CommitmentSigned, _context.Applied[1].Extras!.LastSent);
        var diff = SentCommitDiffCodec.Split(_context.Applied[1].Extras!.SentCommitDiff!.Value);
        Assert.Single(diff);
        Assert.Equal(1UL, _context.State.LocalCommit.Number);
        Assert.NotNull(_context.State.RemoteNextCommit);
        Assert.Equal(LastSentCommitmentMessage.CommitmentSigned, _context.Channel.LastSentCommitmentMessage);
        Assert.NotNull(_context.Channel.SentCommitDiff);
    }

    [Fact]
    public async Task Given_PersistFails_When_CommitmentSigned_Then_NoRevokeSentAndNoSecretReleased()
    {
        // Arrange - B2-CS-R06 / I3: nothing may reveal the old secret before the new commitment is on disk
        _context.SetState(_context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(1)), 600, Onion).Next);
        var signatures = _context.Ports.SignaturesFor(_context.State);
        var before = _context.State;
        _context.FailSaves();
        var handler = CreateHandler();

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(CreateCommitmentSigned(signatures), ChannelState.Open, new FeatureOptions(),
                                      PeerNodeId));

        // Assert
        Assert.Equal(["apply", "save failed"], _context.Calls);
        Assert.Same(before, _context.State);
        _context.LightningSigner.Verify(s => s.AdvanceLocalCommitment(It.IsAny<Domain.Channels.ValueObjects.ChannelId>(),
                                                                      It.IsAny<ulong>()), Times.Never);
        _context.LightningSigner.Verify(s => s.RevealPerCommitmentSecret(
                                            It.IsAny<Domain.Channels.ValueObjects.ChannelId>(), It.IsAny<ulong>()),
                                        Times.Never);
    }

    [Fact]
    public async Task Given_NothingPendingForThePeer_When_CommitmentSigned_Then_OnlyRevokeAndAck()
    {
        // Arrange - a fee-only / empty commitment_signed is accepted (no receiver requirement), and there is nothing
        // for us to sign back
        var handler = CreateHandler();

        // Act
        var replies = await handler.HandleAsync(CreateCommitmentSigned(_context.Ports.SignaturesFor(_context.State)),
                                                ChannelState.Open, new FeatureOptions(), PeerNodeId);

        // Assert
        Assert.IsType<RevokeAndAckMessage>(Assert.Single(replies));
        Assert.Equal(["apply", "save", "advance 1", "reveal 0"], _context.Calls);
    }

    [Fact]
    public async Task Given_InvalidSignature_When_CommitmentSigned_Then_WarningAndCloseAndNothingPersisted()
    {
        // Arrange - B2-CS-R01
        _context.SetState(_context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(1)), 600, Onion).Next);
        var signatures = _context.Ports.SignaturesFor(_context.State);
        _context.Ports.CommitmentSignaturesValid = false;
        var handler = CreateHandler();

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(CreateCommitmentSigned(signatures), ChannelState.Open,
                                                      new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("B2-CS-R01", exception.Message);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_WrongHtlcSignatureCount_When_CommitmentSigned_Then_WarningAndClose()
    {
        // Arrange - B2-CS-R02: num_htlcs must equal the untrimmed HTLC outputs
        _context.SetState(_context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(1)), 600, Onion).Next);
        var handler = CreateHandler();

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(CreateCommitmentSigned(new CommitmentSignatures(Signature(1), [])),
                                                      ChannelState.Open, new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("B2-CS-R02", exception.Message);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_OurSigningFails_When_CommitmentSigned_Then_TheRevokeAndAckIsStillSent()
    {
        // Arrange - the revoke_and_ack is persisted and must go out even when our follow-up signature fails
        _context.SetState(_context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(1)), 600, Onion).Next);
        var signatures = _context.Ports.SignaturesFor(_context.State);
        var saves = 0;
        _context.UnitOfWork.Setup(u => u.SaveChangesAsync())
                .Returns(() => ++saves == 1
                                   ? Task.CompletedTask
                                   : Task.FromException(new InvalidOperationException("database is down")));
        var handler = CreateHandler();

        // Act
        var replies = await handler.HandleAsync(CreateCommitmentSigned(signatures), ChannelState.Open,
                                                new FeatureOptions(), PeerNodeId);

        // Assert
        Assert.IsType<RevokeAndAckMessage>(Assert.Single(replies));
        Assert.Null(_context.State.RemoteNextCommit);
        Assert.Equal(1UL, _context.State.LocalCommit.Number);
    }

    private CommitmentSignedMessageHandler CreateHandler() =>
        new(_context.Ports, NullLogger<CommitmentSignedMessageHandler>.Instance, _context.CreateTransitions());

    private static CommitmentSignedMessage CreateCommitmentSigned(CommitmentSignatures signatures) =>
        new(new CommitmentSignedPayload(TestChannelId, signatures.HtlcSignatures, signatures.Signature));
}