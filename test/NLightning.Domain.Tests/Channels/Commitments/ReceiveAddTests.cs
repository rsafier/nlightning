namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Exceptions;
using static CommitmentsTestKit;

/// <summary>
/// BOLT 2 <c>update_add_htlc</c> receiver rules (matrix §6.3: B2-ADD-R01..R05, R07).
/// </summary>
public class ReceiveAddTests
{
    private static CommitmentsResult Receive(ChannelCommitments c, ulong id, ulong amountMsat, byte preimageTag = 1,
                                             uint cltv = 600) =>
        c.ReceiveAdd(id, amountMsat, PaymentHash(preimageTag), cltv, Onion);

    private static void AssertViolation(string requirementId, Func<CommitmentsResult> act)
    {
        var exception = Assert.Throws<CommitmentViolationException>(() => act());
        Assert.Equal(requirementId, exception.RequirementId);
        Assert.Equal(ChannelId, exception.ChannelId);
        Assert.NotNull(exception.PeerMessage);
    }

    [Fact]
    public void Given_ValidAdd_When_Received_Then_StateRcvdAddHtlcAndNothingSent()
    {
        // Arrange
        var c = Create(600_000, 400_000);

        // Act
        var result = Receive(c, 0, 10_000 * Sat);

        // Assert
        var htlc = Assert.Single(result.Next.Htlcs).Value;
        Assert.Equal(HtlcState.RcvdAddHtlc, htlc.State);
        Assert.Equal(HtlcDirection.Incoming, htlc.Direction);
        Assert.Equal(1UL, result.Next.RemoteNextHtlcId);
        Assert.Empty(result.Outbound);
        Assert.True(result.Next.HasPendingChangesForLocal);
        Assert.False(result.Next.HasPendingChangesForRemote);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(999UL)]
    public void Given_BelowOwnMinimum_When_Received_Then_Violation(ulong amountMsat)
    {
        // Arrange
        var c = Create(600_000, 400_000, local: Party(htlcMinMsat: 1_000));

        // Act / Assert
        AssertViolation("B2-ADD-R01", () => Receive(c, 0, amountMsat));
    }

    [Fact]
    public void Given_SenderBelowReserveAfterAdd_When_Received_Then_Violation()
    {
        // Arrange: the peer (non-funder) holds 15000 sat and must keep the 10000 sat reserve we announced.
        var c = Create(985_000, 15_000);

        // Act / Assert
        AssertViolation("B2-ADD-R02", () => Receive(c, 0, 6_000 * Sat));
        Assert.Single(Receive(c, 0, 5_000 * Sat).Next.Htlcs);
    }

    [Fact]
    public void Given_FunderSenderCantPayFee_When_Received_Then_Violation()
    {
        // Arrange: the peer is the funder: 11000 - 1000 (HTLC, trimmed) - 724 (fee) < 10000.
        var c = Create(500_000, 11_000, localIsFunder: false);

        // Act / Assert
        AssertViolation("B2-ADD-R02", () => Receive(c, 0, 1_000 * Sat));
    }

    [Fact]
    public void Given_OwnMaxAcceptedExceeded_When_Received_Then_Violation()
    {
        // Arrange
        var c = Receive(Create(600_000, 400_000, local: Party(maxAccepted: 1)), 0, 10_000 * Sat).Next;

        // Act / Assert
        AssertViolation("B2-ADD-R03", () => Receive(c, 1, 10_000 * Sat));
    }

    [Fact]
    public void Given_OwnInFlightExceeded_When_Received_Then_Violation()
    {
        // Arrange
        var c = Receive(Create(600_000, 400_000, local: Party(maxInFlightMsat: 20_000 * Sat)), 0, 15_000 * Sat).Next;

        // Act / Assert
        AssertViolation("B2-ADD-R03", () => Receive(c, 1, 5_001 * Sat));
        Assert.Equal(2, Receive(c, 1, 5_000 * Sat).Next.Htlcs.Count);
    }

    [Fact]
    public void Given_TimestampCltv_When_Received_Then_Violation()
    {
        AssertViolation("B2-ADD-R04", () => Receive(Create(600_000, 400_000), 0, 10_000 * Sat, cltv: 500_000_000));
    }

    [Fact]
    public void Given_DuplicateHash_When_Received_Then_Accepted()
    {
        // Arrange (B2-ADD-R05: MUST allow multiple HTLCs with the same payment_hash)
        var c = Receive(Create(600_000, 400_000), 0, 10_000 * Sat, preimageTag: 7).Next;

        // Act
        var result = Receive(c, 1, 20_000 * Sat, preimageTag: 7);

        // Assert
        Assert.Equal(2, result.Next.Htlcs.Count);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(5UL)]
    public void Given_IdGap_When_Received_Then_Violation(ulong id)
    {
        AssertViolation("B2-ADD-R07", () => Receive(Create(600_000, 400_000), id, 10_000 * Sat));
    }

    [Fact]
    public void Given_RepeatedId_When_Received_Then_Violation()
    {
        // Arrange
        var c = Receive(Create(600_000, 400_000), 0, 10_000 * Sat).Next;

        // Act / Assert
        AssertViolation("B2-ADD-R07", () => Receive(c, 0, 10_000 * Sat));
    }
}