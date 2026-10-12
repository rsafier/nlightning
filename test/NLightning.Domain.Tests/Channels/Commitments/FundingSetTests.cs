namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using static SpliceTestKit;

/// <summary>
/// <see cref="FundingSet"/> transitions (splicing plan §3.3, SP-S-01, SP-LK-03): one splice at a time plus its RBF
/// attempts, value conservation, lock and discard.
/// </summary>
public class FundingSetTests
{
    private static FundingSet Set() => FundingSet.Single(Initial(1_000_000));

    [Fact]
    public void Given_NothingPending_When_ASpliceIsAdded_Then_ActiveListsCurrentFirst()
    {
        // Arrange
        var splice = Splice(0x22, 1_500_000, 500_000, 0);

        // Act
        var set = Set().AddPending(splice);

        // Assert
        Assert.True(set.HasPending);
        Assert.Equal(2, set.ActiveCount);
        Assert.Equal([InitialTxId, splice.FundingTxId], set.Active.Select(f => f.FundingTxId));
        Assert.Same(splice, set.Find(splice.FundingTxId));
    }

    [Fact]
    public void Given_ASplicePending_When_ASecondSpliceIsAdded_Then_Refused()
    {
        // Arrange: SP-S-01
        var set = Set().AddPending(Splice(0x22, 1_500_000, 500_000, 0));

        // Act / Assert
        Assert.Throws<ArgumentException>(() => set.AddPending(Splice(0x33, 1_200_000, 200_000, 0)));
    }

    [Fact]
    public void Given_ASplicePending_When_AnRbfAttemptOfItIsAdded_Then_Accepted()
    {
        // Arrange
        var set = Set().AddPending(Splice(0x22, 1_500_000, 500_000, 0));
        var rbf = Splice(0x23, 1_499_000, 499_000, 0, kind: ChannelFundingKind.SpliceRbf, rbfOf: TxIdOf(0x22));

        // Act
        var next = set.AddPending(rbf);

        // Assert
        Assert.Equal(3, next.ActiveCount);
    }

    [Theory]
    [InlineData("rbf-without-parent")]
    [InlineData("not-pending-status")]
    [InlineData("initial-kind")]
    [InlineData("not-conserving")]
    [InlineData("duplicate")]
    public void Given_AnInvalidFunding_When_Added_Then_ArgumentException(string defect)
    {
        // Arrange
        var set = Set();
        var funding = defect switch
        {
            "rbf-without-parent" => Splice(0x23, 1_500_000, 500_000, 0, kind: ChannelFundingKind.SpliceRbf,
                                           rbfOf: TxIdOf(0x22)),
            "not-pending-status" => Splice(0x22, 1_500_000, 500_000, 0) with { Status = ChannelFundingStatus.Current },
            "initial-kind" => Splice(0x22, 1_500_000, 500_000, 0) with { Kind = ChannelFundingKind.Initial },
            "not-conserving" => Splice(0x22, 1_500_000, 500_000, 1),
            _ => Splice(0x11, 1_500_000, 500_000, 0)
        };

        // Act / Assert
        Assert.Throws<ArgumentException>(() => set.AddPending(funding));
    }

    [Fact]
    public void Given_ASpliceAndItsRbf_When_TheRbfIsLocked_Then_ItIsCurrentAndTheOthersRetired()
    {
        // Arrange
        var rbf = Splice(0x23, 1_499_000, 499_000, 0, kind: ChannelFundingKind.SpliceRbf, rbfOf: TxIdOf(0x22));
        var set = Set().AddPending(Splice(0x22, 1_500_000, 500_000, 0)).AddPending(rbf);

        // Act
        var (next, retired) = set.Lock(rbf.FundingTxId);

        // Assert
        Assert.Equal(rbf.FundingTxId, next.Current.FundingTxId);
        Assert.Equal(ChannelFundingStatus.Current, next.Current.Status);
        Assert.Equal(0, next.Current.LocalBalanceDeltaMsat);
        Assert.Equal(0, next.Current.RemoteBalanceDeltaMsat);
        Assert.False(next.HasPending);
        Assert.Equal([(InitialTxId, ChannelFundingStatus.Replaced), (TxIdOf(0x22), ChannelFundingStatus.Discarded)],
                     retired.Select(f => (f.FundingTxId, f.Status)));
    }

    [Fact]
    public void Given_AFundingNotPending_When_Locked_Then_ArgumentException()
    {
        // Arrange / Act / Assert: SP-LK-02 is the caller's; the set refuses anything but a pending funding
        Assert.Throws<ArgumentException>(() => Set().Lock(InitialTxId));
    }

    [Fact]
    public void Given_PendingFundings_When_Discarded_Then_OnlyTheCurrentRemains()
    {
        // Arrange
        var set = Set().AddPending(Splice(0x22, 1_500_000, 500_000, 0));

        // Act
        var (next, discarded) = set.Discard();

        // Assert
        Assert.False(next.HasPending);
        Assert.Equal(InitialTxId, next.Current.FundingTxId);
        Assert.Equal(ChannelFundingStatus.Discarded, Assert.Single(discarded).Status);
    }
}