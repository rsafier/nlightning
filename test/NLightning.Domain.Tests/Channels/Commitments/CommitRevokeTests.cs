namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Exceptions;
using static CommitmentsTestKit;

/// <summary>
/// <c>commitment_signed</c> and <c>revoke_and_ack</c> in the engine (matrix §6.6/§6.7: B2-CS-S01..S03, S06, R01, R02,
/// R05; B2-RAA-S01/S02 numbering, R01, R03; decision D7).
/// </summary>
public class CommitRevokeTests
{
    private static FakeCommitmentSigner SignerFor(ChannelCommitments c) =>
        new(c.Params.Remote.DustLimitSatoshis, c.Params.OptionAnchors);

    [Fact]
    public void Given_NoChanges_When_SendCommit_Then_CannotSign()
    {
        // Arrange
        var c = Create(600_000, 400_000);

        // Act
        var exception = Assert.Throws<CommitmentRefusedException>(() => c.SendCommit(SignerFor(c)));

        // Assert (B2-CS-S01)
        Assert.Equal("B2-CS-S01", exception.RequirementId);
        Assert.False(c.CanSendCommit);
    }

    [Fact]
    public void Given_PendingAdd_When_SendCommit_Then_SignsNextRemoteNumberWithNextPoint()
    {
        // Arrange
        var c = Create(600_000, 400_000).Add(10_000 * Sat).Next;
        var signer = SignerFor(c);

        // Act
        var result = c.SendCommit(signer);

        // Assert
        var cs = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(result.Outbound));
        Assert.Equal(1UL, cs.RemoteCommitmentNumber);
        Assert.Single(cs.Signatures.HtlcSignatures);
        var call = Assert.Single(signer.Calls);
        Assert.Equal(Point(BobTag, 1), call.Point);
        Assert.Equal(result.Next.RemoteNextCommit!.Commit.Spec, call.Spec);
        Assert.Equal(HtlcState.SentAddCommit, result.Next.GetHtlc(HtlcDirection.Outgoing, 0)!.State);
        Assert.Equal(0UL, result.Next.RemoteCommit.Number);
        Assert.True(result.Transition.RemoteCommitChanged);
    }

    [Fact]
    public void Given_WaitingForRevocation_When_SendCommit_Then_CannotSign()
    {
        // Arrange (B2-CS-S06 / D7: one outstanding commitment_signed per direction)
        var c = Create(600_000, 400_000).Add(10_000 * Sat).Next;
        c = c.SendCommit(SignerFor(c)).Next.Add(5_000 * Sat, 2).Next;

        // Act
        var exception = Assert.Throws<CommitmentRefusedException>(() => c.SendCommit(SignerFor(c)));

        // Assert
        Assert.Equal("B2-CS-S06", exception.RequirementId);
        Assert.True(c.HasPendingChangesForRemote);
        Assert.False(c.CanSendCommit);
    }

    [Fact]
    public void Given_NoNextPoint_When_SendCommit_Then_CannotSign()
    {
        // Arrange
        var c = ChannelCommitments.Create(ChannelId, Params(600_000, 400_000), 600_000 * Sat, 400_000 * Sat, 1_000,
                                          Point(BobTag, 0), null)
                                  .Add(10_000 * Sat).Next;

        // Act
        var exception = Assert.Throws<CommitmentRefusedException>(() => c.SendCommit(SignerFor(c)));

        // Assert
        Assert.Equal("B2-CS-S06", exception.RequirementId);
    }

    [Fact]
    public void Given_DustOnlyAdd_When_Signing_Then_CanSign_And_NumHtlcs0()
    {
        // Arrange (B2-CS-S03: a commitment_signed may change nothing but the number, e.g. a dust HTLC)
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(1_000 * Sat);

        // Act
        pair.AliceFullRound();

        // Assert
        Assert.Single(pair.Alice.RemoteCommit.Spec.Htlcs);
        Assert.Single(pair.Bob.LocalCommit.Spec.Htlcs);
        Assert.Empty(pair.Bob.LocalCommit.RemoteSignatures!.HtlcSignatures);
        Assert.Equal(HtlcState.SentAddAckRevocation, pair.Alice.GetHtlc(HtlcDirection.Outgoing, 0)!.State);
    }

    [Fact]
    public void Given_WrongNumHtlcs_When_ReceiveCommit_Then_Violation()
    {
        // Arrange: Bob received Alice's add; her CS carries no HTLC signature.
        var c = Create(400_000, 600_000, localIsFunder: false, selfTag: BobTag)
               .ReceiveAdd(0, 10_000 * Sat, PaymentHash(1), 600, Onion).Next;
        var signatures = new CommitmentSignatures(Signature(1), []);

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(
            () => c.ReceiveCommit(signatures, new FakeCommitmentVerifier()));

        // Assert
        Assert.Equal("B2-CS-R02", exception.RequirementId);
    }

    [Fact]
    public void Given_BadSig_When_ReceiveCommit_Then_ViolationAndStateUnchanged()
    {
        // Arrange
        var c = Create(400_000, 600_000, localIsFunder: false, selfTag: BobTag)
               .ReceiveAdd(0, 10_000 * Sat, PaymentHash(1), 600, Onion).Next;
        var signatures = new CommitmentSignatures(Signature(1), [Signature(2)]);

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(
            () => c.ReceiveCommit(signatures, new FakeCommitmentVerifier(valid: false)));

        // Assert
        Assert.Equal("B2-CS-R01", exception.RequirementId);
        Assert.Equal(0UL, c.LocalCommit.Number);
        Assert.Equal(HtlcState.RcvdAddHtlc, c.GetHtlc(HtlcDirection.Incoming, 0)!.State);
    }

    [Fact]
    public void Given_ValidCommit_When_Received_Then_RevokeAndAckQueuedForPreviousAndNextPlusOne()
    {
        // Arrange
        var c = Create(400_000, 600_000, localIsFunder: false, selfTag: BobTag)
               .ReceiveAdd(0, 10_000 * Sat, PaymentHash(1), 600, Onion).Next;
        var verifier = new FakeCommitmentVerifier();

        // Act
        var result = c.ReceiveCommit(new CommitmentSignatures(Signature(1), [Signature(2)]), verifier);

        // Assert (B2-CS-R05; B2-RAA-S01/S02: secret of commitment 0, point of commitment 2)
        var raa = Assert.IsType<OutboundRevokeAndAck>(Assert.Single(result.Outbound));
        Assert.Equal(0UL, raa.RevokedCommitmentNumber);
        Assert.Equal(2UL, raa.NextCommitmentNumber);
        Assert.Equal(1UL, result.Next.LocalCommit.Number);
        Assert.Equal(1UL, Assert.Single(verifier.Calls).Number);
        Assert.Equal(HtlcState.SentAddRevocation, result.Next.GetHtlc(HtlcDirection.Incoming, 0)!.State);
        Assert.True(result.Transition.LocalCommitChanged);
        Assert.True(result.Next.HasPendingChangesForRemote);
    }

    [Fact]
    public void Given_CommitWithoutUpdates_When_Received_Then_AcceptedAndRevoked()
    {
        // Arrange: BOLT 2 forbids sending it but gives the receiver no rule, so we accept it (interop).
        var c = Create(400_000, 600_000, localIsFunder: false, selfTag: BobTag);

        // Act
        var result = c.ReceiveCommit(new CommitmentSignatures(Signature(1), []), new FakeCommitmentVerifier());

        // Assert
        Assert.Equal(1UL, result.Next.LocalCommit.Number);
        Assert.IsType<OutboundRevokeAndAck>(Assert.Single(result.Outbound));
    }

    [Fact]
    public void Given_NoPendingCommit_When_ReceiveRevoke_Then_Violation()
    {
        // Arrange
        var c = Create(600_000, 400_000);

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(
            () => c.ReceiveRevoke(SecretFor(BobTag, 0), Point(BobTag, 2), new FakeRevocationVerifier()));

        // Assert (B2-RAA-R03)
        Assert.Equal("B2-RAA-R03", exception.RequirementId);
    }

    [Fact]
    public void Given_WrongSecret_When_ReceiveRevoke_Then_ViolationThatFailsTheChannel()
    {
        // Arrange
        var c = Create(600_000, 400_000).Add(10_000 * Sat).Next;
        c = c.SendCommit(SignerFor(c)).Next;

        // Act: secret of commitment 1 instead of 0.
        var exception = Assert.Throws<CommitmentViolationException>(
            () => c.ReceiveRevoke(SecretFor(BobTag, 1), Point(BobTag, 2), new FakeRevocationVerifier()));

        // Assert (B2-RAA-R01: MUST send an error and fail the channel)
        Assert.Equal("B2-RAA-R01", exception.RequirementId);
        Assert.True(exception.MustFailChannel);
        Assert.NotNull(c.RemoteNextCommit);
    }

    [Fact]
    public void Given_ValidRevoke_When_Received_Then_RemoteCommitRotatesAndNextPointStored()
    {
        // Arrange
        var c = Create(600_000, 400_000).Add(10_000 * Sat).Next;
        c = c.SendCommit(SignerFor(c)).Next;

        // Act
        var result = c.ReceiveRevoke(SecretFor(BobTag, 0), Point(BobTag, 2), new FakeRevocationVerifier());

        // Assert
        var next = result.Next;
        Assert.Null(next.RemoteNextCommit);
        Assert.Equal(1UL, next.RemoteCommit.Number);
        Assert.Equal(Point(BobTag, 1), next.RemoteCommit.PerCommitmentPoint);
        Assert.Equal(Point(BobTag, 2), next.RemoteNextPerCommitmentPoint);
        Assert.Equal(HtlcState.RcvdAddRevocation, next.GetHtlc(HtlcDirection.Outgoing, 0)!.State);
        Assert.Empty(result.Outbound);
        Assert.True(result.Transition.RemoteCommitChanged);
        Assert.True(result.Transition.ScalarsChanged);
    }
}