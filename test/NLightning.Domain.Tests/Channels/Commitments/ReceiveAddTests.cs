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

    [Theory]
    [InlineData(HtlcState.SentAddHtlc)] // unsigned
    [InlineData(HtlcState.SentAddCommit)] // signed, commitment_signed maybe not received yet
    [InlineData(HtlcState.RcvdAddRevocation)] // acked, but the funder may have offered before it received them
    public void Given_OwnAddsNotYetSignedByFunder_When_FunderAddReceived_Then_FeeCountsOnlyWhatFunderSaw(
        HtlcState ownAddsState)
    {
        // Arrange (simulator seeds 1795, 76293: both sides offered at once): the funder holds 20000 sat at feerate
        // 10000. Our three 100000-sat adds, which the funder may not have seen when it offered, would raise its fee to
        // (724 + 4 * 172) * 10 = 14120 sat; without them its 10000-sat HTLC costs (724 + 172) * 10 = 8960 sat and fits
        // in the 10000 sat it keeps. Only once the funder signs a commitment with our adds (which it sends before any
        // later update_add_htlc) do they count.
        var party = Party(reserveSat: 0);
        var c = Create(900_000, 20_000, feeratePerKw: 10_000, localIsFunder: false, local: party, remote: party,
                       selfTag: BobTag);
        for (byte i = 1; i <= 3; i++)
            c = c.Add(100_000 * Sat, i).Next;
        if (ownAddsState != HtlcState.SentAddHtlc)
            c = c.SendCommit(new FakeCommitmentSigner(party.DustLimitSatoshis, false)).Next;
        if (ownAddsState == HtlcState.RcvdAddRevocation)
            c = c.ReceiveRevoke(SecretFor(AliceTag, 0), Point(AliceTag, 2), new FakeRevocationVerifier()).Next;
        Assert.Equal(ownAddsState, c.GetHtlc(HtlcDirection.Outgoing, 0)!.State);

        // Act
        var result = Receive(c, 0, 10_000 * Sat, 9);

        // Assert
        Assert.Equal(HtlcState.RcvdAddHtlc, result.Next.GetHtlc(HtlcDirection.Incoming, 0)!.State);
        AssertViolation("B2-ADD-R02", () => Receive(c, 0, 12_000 * Sat, 9)); // 8000 sat left < 8960 sat fee
    }

    [Fact]
    public void Given_OwnAddsSignedByFunder_When_FunderAddReceived_Then_FeeCountsThem()
    {
        // Arrange: the funder signed our adds (they are in our commitment), so it knew them when it offered.
        var party = Party(reserveSat: 0);
        var pair = new CommitmentPair(20_000, 900_000, 10_000, aliceParty: party, bobParty: party);
        for (byte i = 1; i <= 3; i++)
            pair.BobAdd(100_000 * Sat, i);
        pair.BobFullRound();

        // Act / Assert: 20000 - 8000 = 12000 sat left < (724 + 4 * 172) * 10 = 14120 sat with this HTLC
        var exception = Assert.Throws<CommitmentViolationException>(() => Receive(pair.Bob, 0, 8_000 * Sat, 9));
        Assert.Equal("B2-ADD-R02", exception.RequirementId);
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