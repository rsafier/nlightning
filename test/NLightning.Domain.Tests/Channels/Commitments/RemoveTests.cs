namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Exceptions;
using static CommitmentsTestKit;

/// <summary>
/// Removing HTLCs (matrix §6.4: B2-DEL-00, B2-DEL-03, B2-DEL-R01, R02, R04, R07).
/// </summary>
public class RemoveTests
{
    /// <summary>Alice offered HTLC 0 (preimage 1) and it is locked in on both sides.</summary>
    private static CommitmentPair LockedInPair()
    {
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        pair.AliceFullRound();
        return pair;
    }

    private static readonly byte[] s_sha256OfOnion = new byte[32];

    [Fact]
    public void Given_LockedInIncoming_When_SendFulfill_Then_SentRemoveHtlcAndOutboundFulfill()
    {
        // Arrange
        var pair = LockedInPair();

        // Act
        var result = pair.Bob.SendFulfill(0, Preimage(1), Sha256);

        // Assert
        var outbound = Assert.IsType<OutboundFulfillHtlc>(Assert.Single(result.Outbound));
        Assert.Equal(0UL, outbound.Id);
        var htlc = result.Next.GetHtlc(HtlcDirection.Incoming, 0)!;
        Assert.Equal(HtlcState.SentRemoveHtlc, htlc.State);
        Assert.Equal(HtlcRemovalKind.Fulfill, htlc.Removal!.Kind);
        Assert.True(result.Next.HasPendingChangesForRemote);
    }

    [Fact]
    public void Given_RemoveOwnOfferedHtlc_When_SendFulfill_Then_Rejected()
    {
        // Arrange (B2-DEL-00: only the other node's HTLCs can be removed)
        var pair = LockedInPair();

        // Act
        var exception = Assert.Throws<CommitmentRefusedException>(() => pair.Alice.SendFail(0, new byte[] { 1 }));

        // Assert
        Assert.Equal("B2-DEL-00", exception.RequirementId);
    }

    [Fact]
    public void Given_AddNotLockedIn_When_SendFulfill_Then_Rejected()
    {
        // Arrange (B2-DEL-03): Bob has the add committed in his commitment but not yet in Alice's.
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        pair.DeliverBobRevoke(pair.AliceCommits());

        // Act
        var exception = Assert.Throws<CommitmentRefusedException>(() => pair.Bob.SendFulfill(0, Preimage(1), Sha256));

        // Assert
        Assert.Equal("B2-DEL-03", exception.RequirementId);
        Assert.Equal(HtlcState.SentAddRevocation, pair.Bob.GetHtlc(HtlcDirection.Incoming, 0)!.State);
    }

    [Fact]
    public void Given_WrongPreimage_When_SendFulfill_Then_Refused()
    {
        // Arrange
        var pair = LockedInPair();

        // Act
        var exception = Assert.Throws<CommitmentRefusedException>(() => pair.Bob.SendFulfill(0, Preimage(9), Sha256));

        // Assert
        Assert.Equal("B2-DEL-R02", exception.RequirementId);
    }

    [Fact]
    public void Given_NoBadOnionBit_When_SendFailMalformed_Then_Refused()
    {
        // Arrange
        var pair = LockedInPair();

        // Act
        var exception =
            Assert.Throws<CommitmentRefusedException>(() => pair.Bob.SendFailMalformed(0, 0x4005, s_sha256OfOnion));

        // Assert
        Assert.Equal("B2-DEL-R04", exception.RequirementId);
        Assert.Single(pair.Bob.SendFailMalformed(0, 0xC005, s_sha256OfOnion).Outbound);
    }

    [Fact]
    public void Given_UnknownId_When_ReceiveFulfill_Then_Violation()
    {
        // Arrange
        var pair = LockedInPair();

        // Act
        var exception =
            Assert.Throws<CommitmentViolationException>(() => pair.Alice.ReceiveFulfill(7, Preimage(1), Sha256));

        // Assert
        Assert.Equal("B2-DEL-R01", exception.RequirementId);
    }

    [Fact]
    public void Given_HtlcNotInOurCurrentCommitment_When_ReceiveFail_Then_Violation()
    {
        // Arrange: Alice signed Bob's commitment with the add, but her own commitment does not have it yet.
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        pair.DeliverBobRevoke(pair.AliceCommits());

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(() => pair.Alice.ReceiveFail(0, new byte[] { 1 }));

        // Assert
        Assert.Equal("B2-DEL-R01", exception.RequirementId);
    }

    [Fact]
    public void Given_WrongPreimage_When_ReceiveFulfill_Then_Violation()
    {
        // Arrange
        var pair = LockedInPair();

        // Act
        var exception =
            Assert.Throws<CommitmentViolationException>(() => pair.Alice.ReceiveFulfill(0, Preimage(2), Sha256));

        // Assert
        Assert.Equal("B2-DEL-R02", exception.RequirementId);
    }

    [Fact]
    public void Given_MalformedWithoutBadOnion_When_Received_Then_Violation()
    {
        // Arrange (NL-023)
        var pair = LockedInPair();

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(
            () => pair.Alice.ReceiveFailMalformed(0, 0x4005, s_sha256OfOnion));

        // Assert
        Assert.Equal("B2-DEL-R04", exception.RequirementId);
        var ok = pair.Alice.ReceiveFailMalformed(0, 0xC005, s_sha256OfOnion).Next.GetHtlc(HtlcDirection.Outgoing, 0)!;
        Assert.Equal(HtlcRemovalKind.FailMalformed, ok.Removal!.Kind);
        Assert.Equal(HtlcState.RcvdRemoveHtlc, ok.State);
    }

    [Fact]
    public void Given_DoubleFulfill_When_Received_Then_Violation()
    {
        // Arrange
        var pair = LockedInPair();
        var once = pair.Alice.ReceiveFulfill(0, Preimage(1), Sha256).Next;

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(() => once.ReceiveFulfill(0, Preimage(1), Sha256));

        // Assert
        Assert.Equal("B2-DEL-R07", exception.RequirementId);
    }

    [Fact]
    public void Given_DoubleRemoval_When_Sent_Then_Refused()
    {
        // Arrange
        var pair = LockedInPair();
        var once = pair.Bob.SendFail(0, new byte[] { 1 }).Next;

        // Act
        var exception = Assert.Throws<CommitmentRefusedException>(() => once.SendFulfill(0, Preimage(1), Sha256));

        // Assert
        Assert.Equal("B2-DEL-R07", exception.RequirementId);
    }
}