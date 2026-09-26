namespace NLightning.Application.Tests.Channels.Fees;

using Application.Channels.Fees;
using Domain.Bitcoin.Transactions.Enums;
using static FeeTestKit;

/// <summary>
/// BOLT2 plan N9-T3, BOLT 2 <c>max_dust_htlc_exposure_msat</c>. At 2,500 sat/kw without anchors (dust limit 546 sat)
/// an HTLC is trimmed below 2,203 sat when the holder offered it (HTLC-timeout, 1,657 sat fee) and below 2,303 sat when
/// the holder received it (HTLC-success, 1,757 sat fee).
/// </summary>
public class DustExposurePolicyTests
{
    private const uint Feerate = 2_500;
    private const ulong MaxDustMsat = 10_000 * Sat;

    [Fact]
    public void Given_DustUpToTheLimit_When_LastHtlcLocksIn_Then_Accepted()
    {
        // Arrange - five 2,000 sat HTLCs: exactly 10,000 sat of dust, which is not over the limit
        var htlcs = Enumerable.Range(0, 5).Select(i => Incoming((ulong)i, 2_000)).ToList();
        var commitments = Create(800_000, 200_000, Feerate, htlcs: htlcs);

        // Act
        var excess = DustExposurePolicy.CheckLockedInIncoming(commitments, htlcs[4], MaxDustMsat);

        // Assert
        Assert.Null(excess);
    }

    [Fact]
    public void Given_RemoteDustOverLimit_When_HtlcLocksIn_Then_MarkedForFailure()
    {
        // Arrange - B2-DUST-01: the sixth 2,000 sat HTLC takes the peer's commitment to 12,000 sat of dust
        var htlcs = Enumerable.Range(0, 6).Select(i => Incoming((ulong)i, 2_000)).ToList();
        var commitments = Create(800_000, 200_000, Feerate, htlcs: htlcs);

        // Act
        var excess = DustExposurePolicy.CheckLockedInIncoming(commitments, htlcs[5], MaxDustMsat);

        // Assert
        Assert.NotNull(excess);
        Assert.Equal(CommitmentSide.Remote, excess.Holder);
        Assert.Equal(12_000 * Sat, excess.ExposureMsat);
        Assert.Equal(MaxDustMsat, excess.MaxDustMsat);
    }

    [Fact]
    public void Given_LocalDustOverLimit_When_HtlcTrimmedOnlyOnOurCommitment_Then_LocalIsReported()
    {
        // Arrange - B2-DUST-02: 2,250 sat is above the peer's trim point (2,203) but below ours (2,303)
        var htlcs = Enumerable.Range(0, 5).Select(i => Incoming((ulong)i, 2_250)).ToList();
        var commitments = Create(800_000, 200_000, Feerate, htlcs: htlcs);

        // Act
        var excess = DustExposurePolicy.CheckLockedInIncoming(commitments, htlcs[4], MaxDustMsat);

        // Assert
        Assert.NotNull(excess);
        Assert.Equal(CommitmentSide.Local, excess.Holder);
        Assert.Equal(11_250 * Sat, excess.ExposureMsat);
    }

    [Fact]
    public void Given_LaterHtlcs_When_EarlierHtlcIsJudged_Then_OnlyWhatArrivedBeforeCounts()
    {
        // Arrange - the same committed state gives every HTLC the answer it had when it arrived
        var htlcs = Enumerable.Range(0, 6).Select(i => Incoming((ulong)i, 2_000)).ToList();
        var commitments = Create(800_000, 200_000, Feerate, htlcs: htlcs);

        // Act
        var excess = DustExposurePolicy.CheckLockedInIncoming(commitments, htlcs[3], MaxDustMsat);

        // Assert
        Assert.Null(excess);
    }

    [Fact]
    public void Given_OurTrimmedOffers_When_IncomingDustLocksIn_Then_TheyCount()
    {
        // Arrange - 8,000 sat of our own trimmed HTLCs plus a 2,100 sat incoming one
        List<Domain.Channels.Commitments.HtlcRecord> htlcs =
        [
            Outgoing(0, 2_000), Outgoing(1, 2_000), Outgoing(2, 2_000), Outgoing(3, 2_000), Incoming(0, 2_100)
        ];
        var commitments = Create(800_000, 200_000, Feerate, htlcs: htlcs);

        // Act
        var excess = DustExposurePolicy.CheckLockedInIncoming(commitments, htlcs[4], MaxDustMsat);

        // Assert
        Assert.NotNull(excess);
        Assert.Equal(10_100 * Sat, excess.ExposureMsat);
    }

    [Fact]
    public void Given_UntrimmedHtlc_When_DustIsHigh_Then_Accepted()
    {
        // Arrange - the rule is only about trimmed HTLCs
        var htlcs = Enumerable.Range(0, 6).Select(i => Incoming((ulong)i, 2_000)).Append(Incoming(6, 50_000)).ToList();
        var commitments = Create(800_000, 200_000, Feerate, htlcs: htlcs);

        // Act
        var excess = DustExposurePolicy.CheckLockedInIncoming(commitments, htlcs[6], MaxDustMsat);

        // Assert
        Assert.Null(excess);
    }

    [Fact]
    public void Given_OutgoingHtlc_When_JudgedAsIncoming_Then_Throws()
    {
        // Arrange
        var htlc = Outgoing(0, 2_000);
        var commitments = Create(800_000, 200_000, Feerate, htlcs: [htlc]);

        // Act + Assert
        Assert.Throws<ArgumentException>(() => DustExposurePolicy.CheckLockedInIncoming(commitments, htlc,
                                                                                         MaxDustMsat));
    }

    [Theory]
    [InlineData(5_000_000UL, 7_000_000UL, 5_000_000UL)]
    [InlineData(null, 7_000_000UL, 7_000_000UL)]
    [InlineData(null, null, null)]
    public void Given_StoredAndNodeLimits_When_Resolving_Then_StoredWins(ulong? stored, ulong? node, ulong? expected)
    {
        // Arrange
        var commitments = Create(800_000, 200_000, Feerate, maxDustMsat: stored);

        // Act
        var limit = DustExposurePolicy.Resolve(commitments, node);

        // Assert
        Assert.Equal(expected, limit);
    }

    [Fact]
    public void Given_Anchors_When_FeerateRises_Then_NoDustCheck()
    {
        // Arrange - with anchors HTLC transactions pay no fee: trimming does not depend on the feerate
        var commitments = Create(800_000, 200_000, 1_000, anchors: true,
                                 htlcs: [Incoming(0, 400), Incoming(1, 400)]);

        // Act
        var excess = DustExposurePolicy.CheckFeeIncrease(commitments, 2_500, 1);

        // Assert
        Assert.Null(excess);
    }

    [Fact]
    public void Given_FeerateFalls_When_Checking_Then_NoDustCheck()
    {
        // Arrange
        var commitments = Create(800_000, 200_000, 10_000, htlcs: [Incoming(0, 7_000), Incoming(1, 7_000)]);

        // Act
        var excess = DustExposurePolicy.CheckFeeIncrease(commitments, 5_000, 1);

        // Assert
        Assert.Null(excess);
    }

    [Fact]
    public void Given_PendingAdd_When_FeerateRises_Then_ItCounts()
    {
        // Arrange - a pending (not yet committed) add is in the next commitments, so it counts
        var commitments = Create(800_000, 200_000, 2_000,
                                 htlcs:
                                 [
                                     Incoming(0, 7_000),
                                     Incoming(1, 7_000, Domain.Channels.Enums.HtlcState.RcvdAddHtlc)
                                 ]);

        // Act
        var excess = DustExposurePolicy.CheckFeeIncrease(commitments, 10_000, MaxDustMsat);

        // Assert
        Assert.NotNull(excess);
        Assert.Equal(14_000 * Sat, excess.ExposureMsat);
    }

    [Fact]
    public void Given_SnapshotWithoutAStoredLimit_When_Checking_Then_ReceiveSideUsesTheNodeLimitButSendSideIsUnlimited()
    {
        // Arrange - a first snapshot taken before max_dust_htlc_exposure_msat was stored (Params limit null); 2,000 sat
        // HTLCs are trimmed at 2,500 sat/kw without anchors; the node limit is 3,000 sat
        const ulong nodeLimitMsat = 3_000 * Sat;
        var commitments = Create(800_000, 200_000, 2_500, maxDustMsat: null);
        var hash = PaymentHash(7);

        // Act - the receive and fee checks fall back to the node limit
        var resolved = DustExposurePolicy.Resolve(commitments, nodeLimitMsat);
        var first = commitments.SendAdd(2_000 * Sat, hash, 700, new byte[1366]).Next;
        var second = first.SendAdd(2_000 * Sat, hash, 700, new byte[1366]).Next;

        // Assert - the engine's send-side rules (B2-DUST-03/04) read only the stored limit, so 4,000 sat of our own
        // dust is accepted over the 3,000 sat node limit (known gap until the limit is backfilled into old snapshots)
        Assert.Equal(nodeLimitMsat, resolved);
        Assert.Null(second.Params.MaxDustHtlcExposureMsat);
        Assert.True(DustExposurePolicy.ProspectiveExposureMsat(second, CommitmentSide.Remote, 2_500) > nodeLimitMsat);
    }
}