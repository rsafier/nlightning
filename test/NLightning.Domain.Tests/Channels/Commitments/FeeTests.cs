namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Exceptions;
using static CommitmentsTestKit;

/// <summary>
/// <c>update_fee</c> in the engine (decision D9; matrix §6.8: B2-FEE-S02, R01, R02, R03, X01).
/// </summary>
public class FeeTests
{
    [Fact]
    public void Given_Funder_When_SendFee_Then_PendingUpdateAndOutbound()
    {
        // Arrange
        var c = Create(600_000, 400_000);

        // Act
        var result = c.SendFee(2_000);

        // Assert
        Assert.Equal(2_000u, Assert.IsType<OutboundUpdateFee>(Assert.Single(result.Outbound)).FeeratePerKw);
        Assert.Equal(HtlcState.SentAddHtlc, result.Next.FeeUpdates[^1].State);
        Assert.True(result.Transition.FeeUpdatesChanged);
        Assert.Equal(1_000u, result.Next.FeeratePerKw(CommitmentSide.Remote));
        Assert.True(result.Next.HasPendingChangesForRemote);
    }

    [Fact]
    public void Given_NotFunder_When_SendFee_Then_Rejected()
    {
        // Arrange
        var c = Create(600_000, 400_000, localIsFunder: false);

        // Act
        var exception = Assert.Throws<CommitmentRefusedException>(() => c.SendFee(2_000));

        // Assert
        Assert.Equal("B2-FEE-S02", exception.RequirementId);
    }

    [Fact]
    public void Given_FunderCantAffordNewFee_When_SendFee_Then_Refused()
    {
        // Arrange: we hold 10800 sat and must keep the peer's 10000 sat reserve.
        var c = Create(10_800, 989_200);

        // Act (feerate 2000 -> 1448 sat > 800 above the reserve)
        var exception = Assert.Throws<CommitmentRefusedException>(() => c.SendFee(2_000));

        // Assert
        Assert.Equal("B2-FEE-R03", exception.RequirementId);
        Assert.Single(c.SendFee(1_100).Outbound); // 796 sat fits
    }

    [Fact]
    public void Given_FromNonFunder_When_ReceiveFee_Then_Violation()
    {
        // Arrange: we are the funder, so the peer may not send update_fee.
        var c = Create(600_000, 400_000);

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(() => c.ReceiveFee(2_000, 253, 100_000));

        // Assert
        Assert.Equal("B2-FEE-R02", exception.RequirementId);
    }

    [Theory]
    [InlineData(252u)]
    [InlineData(100_001u)]
    public void Given_FeeOutsideBounds_When_ReceiveFee_Then_Violation(uint feerate)
    {
        // Arrange
        var c = Create(400_000, 600_000, localIsFunder: false);

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(() => c.ReceiveFee(feerate, 253, 100_000));

        // Assert
        Assert.Equal("B2-FEE-R01", exception.RequirementId);
    }

    [Fact]
    public void Given_FunderCantAffordNewFee_When_ReceiveFee_Then_Violation()
    {
        // Arrange: the peer (funder) holds 1000 sat; feerate 2000 -> 1448 sat.
        var c = Create(999_000, 1_000, feeratePerKw: 253, localIsFunder: false);

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(() => c.ReceiveFee(2_000, 253, 100_000));

        // Assert (B2-FEE-R03; no reserve is required by BOLT 2 here)
        Assert.Equal("B2-FEE-R03", exception.RequirementId);
        Assert.Empty(c.ReceiveFee(1_300, 253, 100_000).Outbound); // 941 sat fits
    }

    [Fact]
    public void Given_UnsignedPeerFee_When_AnotherReceived_Then_Replaced()
    {
        // Arrange
        var c = Create(400_000, 600_000, localIsFunder: false).ReceiveFee(2_000, 253, 100_000).Next;

        // Act
        var next = c.ReceiveFee(3_000, 253, 100_000).Next;

        // Assert
        Assert.Equal(2, next.FeeUpdates.Count);
        Assert.Equal(3_000u, next.FeeUpdates[^1].FeeratePerKw);
        Assert.Equal(HtlcState.RcvdAddHtlc, next.FeeUpdates[^1].State);
        Assert.True(next.FeeUpdates[^1].Sequence > c.FeeUpdates[^1].Sequence);
    }

    [Fact]
    public void Given_OnlyFeeUpdate_When_Signing_Then_CanSignAndFeeCommits()
    {
        // Arrange (B2-CS-S02: a commitment_signed that only alters the fee is allowed)
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceFee(5_000);

        // Act
        Assert.True(pair.Alice.CanSendCommit);
        pair.AliceFullRound();

        // Assert
        Assert.Equal(5_000u, pair.Alice.LocalCommit.Spec.FeeratePerKw);
        Assert.Equal(5_000u, pair.Bob.LocalCommit.Spec.FeeratePerKw);
        Assert.Equal(HtlcState.SentAddAckRevocation, Assert.Single(pair.Alice.FeeUpdates).State);
        Assert.Equal(HtlcState.RcvdAddAckRevocation, Assert.Single(pair.Bob.FeeUpdates).State);
        Assert.False(pair.Alice.HasPendingChangesForRemote);
        Assert.False(pair.Bob.HasPendingChangesForRemote);
    }
}