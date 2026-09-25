namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Exceptions;
using static CommitmentsTestKit;

/// <summary>
/// BOLT 2 <c>update_add_htlc</c> sender rules (matrix §6.2/§6.3: B2-ADD-S01..S10, B2-CLTV-02, B2-DUST-03/04).
/// </summary>
public class SendAddTests
{
    private static void AssertRefused(string requirementId, Func<CommitmentsResult> act)
    {
        var exception = Assert.Throws<CommitmentRefusedException>(() => act());
        Assert.Equal(requirementId, exception.RequirementId);
    }

    [Fact]
    public void Given_ValidHtlc_When_SendAdd_Then_StateSentAddHtlcAndOutboundAdd()
    {
        // Arrange
        var c = Create(600_000, 400_000);

        // Act
        var result = c.Add(10_000 * Sat);

        // Assert
        var htlc = Assert.IsType<OutboundAddHtlc>(Assert.Single(result.Outbound)).Htlc;
        Assert.Equal(HtlcState.SentAddHtlc, htlc.State);
        Assert.Equal(HtlcDirection.Outgoing, htlc.Direction);
        Assert.Same(htlc, result.Next.GetHtlc(HtlcDirection.Outgoing, 0));
        Assert.Equal(htlc, Assert.Single(result.Transition.UpsertedHtlcs));
        Assert.True(result.Transition.ScalarsChanged);
        Assert.Empty(c.Htlcs); // the old snapshot is untouched
    }

    [Fact]
    public void Given_ZeroAmount_When_SendAdd_Then_Rejected()
    {
        AssertRefused("B2-ADD-S05", () => Create(600_000, 400_000).Add(0));
    }

    [Fact]
    public void Given_BelowRemoteMinimum_When_SendAdd_Then_Rejected()
    {
        // Arrange
        var c = Create(600_000, 400_000, remote: Party(htlcMinMsat: 1_000));

        // Act / Assert
        AssertRefused("B2-ADD-S06", () => c.Add(999));
        Assert.Single(c.Add(1_000).Next.Htlcs);
    }

    [Fact]
    public void Given_TimestampCltv_When_SendAdd_Then_Rejected()
    {
        // Arrange
        var c = Create(600_000, 400_000);

        // Act / Assert
        AssertRefused("B2-ADD-S07", () => c.Add(10_000 * Sat, cltv: 500_000_000));
        Assert.Single(c.Add(10_000 * Sat, cltv: 499_999_999).Next.Htlcs);
    }

    [Fact]
    public void Given_ExpiryInPast_When_SendAdd_Then_Rejected()
    {
        // Arrange
        var c = Create(600_000, 400_000);

        // Act / Assert (B2-CLTV-02)
        AssertRefused("B2-CLTV-02", () => c.SendAdd(10_000 * Sat, PaymentHash(1), 800, Onion, currentBlockHeight: 800));
        Assert.Single(c.SendAdd(10_000 * Sat, PaymentHash(1), 801, Onion, currentBlockHeight: 800).Next.Htlcs);
    }

    [Fact]
    public void Given_RemoteMaxAcceptedReached_When_SendAdd_Then_Rejected()
    {
        // Arrange
        var c = Create(600_000, 400_000, remote: Party(maxAccepted: 2));
        c = c.Add(10_000 * Sat).Next.Add(10_000 * Sat).Next;

        // Act / Assert
        AssertRefused("B2-ADD-S08", () => c.Add(10_000 * Sat));
    }

    [Fact]
    public void Given_InFlightExceeded_When_SendAdd_Then_Rejected()
    {
        // Arrange
        var c = Create(600_000, 400_000, remote: Party(maxInFlightMsat: 50_000 * Sat));
        c = c.Add(30_000 * Sat).Next;

        // Act / Assert
        AssertRefused("B2-ADD-S09", () => c.Add(20_001 * Sat));
        Assert.Equal(2, c.Add(20_000 * Sat).Next.Htlcs.Count);
    }

    [Fact]
    public void Given_ThreeAddsAcrossTwoCommits_When_SendAdd_Then_Ids012()
    {
        // Arrange (B2-ADD-S10: ids start at 0, +1 per offer, never reset after revoke_and_ack)
        var pair = new CommitmentPair(600_000, 400_000);

        // Act
        var first = pair.AliceAdd(10_000 * Sat, 1);
        pair.AliceFullRound();
        var second = pair.AliceAdd(10_000 * Sat, 2);
        var third = pair.AliceAdd(10_000 * Sat, 3);
        pair.AliceFullRound();

        // Assert
        Assert.Equal([0UL, 1UL, 2UL], [first, second, third]);
        Assert.Equal(3UL, pair.Alice.LocalNextHtlcId);
        Assert.Equal(3UL, pair.Bob.RemoteNextHtlcId);
    }

    [Fact]
    public void Given_FunderAtReserve_When_SendAdd_Then_Rejected()
    {
        // Arrange: feerate 1000 -> fee with one HTLC = 896 sat; our reserve (peer's announced) = 10000 sat.
        var c = Create(20_000, 980_000);

        // Act / Assert (B2-ADD-S01: 20000 - 10000 - 896 < 10000)
        AssertRefused("B2-ADD-S01", () => c.Add(10_000 * Sat));
    }

    [Fact]
    public void Given_AnchorsFunder_When_OnlyOneAnchorAffordable_Then_Rejected()
    {
        // Arrange: anchors, feerate 1000 -> fee with one HTLC = 1296 sat, anchors 660 sat.
        var c = Create(20_000, 980_000, anchors: true);

        // Act / Assert (B2-ADD-S02: 20000 - 8500 - 1296 >= 10000 but - 660 < 10000)
        AssertRefused("B2-ADD-S02", () => c.Add(8_500 * Sat));
    }

    [Fact]
    public void Given_SpikeBufferViolated_When_SendAdd_Then_Refused()
    {
        // Arrange: at 2x feerate the fee is 1792 sat plus 344 sat for one more HTLC.
        var c = Create(20_000, 980_000);

        // Act / Assert (B2-ADD-S03: 20000 - 8000 - 896 >= 10000 but 20000 - 8000 - 2136 < 10000)
        AssertRefused("B2-ADD-S03", () => c.Add(8_000 * Sat));
        Assert.Single(c.Add(7_800 * Sat).Next.Htlcs);
    }

    [Fact]
    public void Given_NonFunderAdd_When_FunderCantPayFee_Then_Refused()
    {
        // Arrange: we are not the funder; the peer (funder) holds 10800 sat and must keep our 10000 sat reserve.
        var c = Create(500_000, 10_800, localIsFunder: false);

        // Act / Assert (B2-ADD-S04: 10800 - 724 >= 10000 today, 10800 - 896 < 10000 with the HTLC)
        AssertRefused("B2-ADD-S04", () => c.Add(10_000 * Sat));
    }

    [Fact]
    public void Given_NonFunderBelowReserveAfterAdd_When_SendAdd_Then_Refused()
    {
        // Arrange
        var c = Create(15_000, 985_000, localIsFunder: false);

        // Act / Assert (the peer would reject it under B2-ADD-R02)
        AssertRefused("B2-ADD-R02", () => c.Add(6_000 * Sat));
        Assert.Single(c.Add(5_000 * Sat).Next.Htlcs);
    }

    [Fact]
    public void Given_OfferOverRemoteDust_When_SendAdd_Then_Refused()
    {
        // Arrange: 1000 sat is trimmed on the peer's commitment (< 546 + 703); limit 5000 sat.
        var c = Create(600_000, 400_000, maxDustExposureMsat: 5_000 * Sat);
        for (byte i = 0; i < 5; i++)
            c = c.Add(1_000 * Sat, i).Next;

        // Act / Assert (B2-DUST-03)
        AssertRefused("B2-DUST-03", () => c.Add(1_000 * Sat, 9));
        Assert.Equal(6, c.Add(10_000 * Sat, 9).Next.Htlcs.Count); // non-dust HTLCs don't count
    }

    [Fact]
    public void Given_OfferOverLocalDust_When_SendAdd_Then_Refused()
    {
        // Arrange: 1300 sat is trimmed only on our commitment (our dust 2000 sat); limit 2000 sat.
        var c = Create(600_000, 400_000, local: Party(dustSat: 2_000), maxDustExposureMsat: 2_000 * Sat);
        c = c.Add(1_300 * Sat).Next;

        // Act / Assert (B2-DUST-04)
        AssertRefused("B2-DUST-04", () => c.Add(1_300 * Sat, 2));
    }

    [Fact]
    public void Given_Refused_When_SendAdd_Then_StateUnchanged()
    {
        // Arrange
        var c = Create(600_000, 400_000);

        // Act
        Assert.Throws<CommitmentRefusedException>(() => c.Add(0));

        // Assert
        Assert.Equal(0UL, c.LocalNextHtlcId);
        Assert.Empty(c.Htlcs);
    }
}