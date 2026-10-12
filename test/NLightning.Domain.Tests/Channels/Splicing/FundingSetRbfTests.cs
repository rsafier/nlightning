namespace NLightning.Domain.Tests.Channels.Splicing;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.InteractiveTx;

/// <summary>
/// Wave SPR, SPR-T2: RBF siblings in <see cref="FundingSet"/>. An attempt replaces the latest one at a feerate that
/// beats it (IT-RBF-01) within the batch limit (SP-OP-04); the lock of any attempt discards every other one in the same
/// set change (SP-LK-03).
/// </summary>
public class FundingSetRbfTests
{
    private static readonly CompactPubKey s_key = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);
    private static readonly TxId s_fundingTxId = Id(0x10);

    [Fact]
    public void Given_APendingSplice_When_AnRbfSiblingIsAdded_Then_ItIsTheLatestAttempt()
    {
        // Arrange
        var set = WithSplice();

        // Act
        var next = set.AddRbfSibling(Rbf(0x21, Id(0x20), 1_041));

        // Assert
        Assert.Equal(2, next.Pending.Count);
        Assert.Equal(3, next.ActiveCount);
        Assert.Equal(Id(0x21), next.LatestAttempt!.FundingTxId);
        Assert.Equal(Id(0x20), next.LatestAttempt.RbfOf);
        Assert.Null(set.LatestAttempt!.RbfOf);
    }

    [Fact]
    public void Given_NothingPending_When_AnRbfSiblingIsAdded_Then_Throws()
    {
        // Arrange
        var set = FundingSet.Single(Current());

        // Act & Assert
        Assert.Null(set.LatestAttempt);
        Assert.Throws<ArgumentException>(() => set.AddRbfSibling(Rbf(0x21, Id(0x20), 1_041)));
    }

    [Fact]
    public void Given_AnRbfOfAnOlderAttempt_When_Added_Then_Throws()
    {
        // Arrange: 0x20 then 0x21 pending; a third attempt must replace 0x21
        var set = WithSplice().AddRbfSibling(Rbf(0x21, Id(0x20), 1_041));

        // Act & Assert
        var e = Assert.Throws<ArgumentException>(() => set.AddRbfSibling(Rbf(0x22, Id(0x20), 2_000)));
        Assert.Contains("not the latest attempt", e.Message);
    }

    [Theory]
    [InlineData(1_040u, false)]
    [InlineData(1_041u, true)]
    [InlineData(5_000u, true)]
    public void Given_AnRbfFeerate_When_Added_Then_ItMustBeatTheLatestAttempt(uint feerate, bool accepted)
    {
        // Arrange
        var set = WithSplice();

        // Act
        var exception = Record.Exception(() => set.AddRbfSibling(Rbf(0x21, Id(0x20), feerate)));

        // Assert
        Assert.Equal(accepted, exception is null);
        if (!accepted)
            Assert.Contains("IT-RBF-01", exception!.Message);
    }

    [Fact]
    public void Given_ASpliceKind_When_AddedAsAnRbfSibling_Then_Throws()
    {
        // Arrange
        var set = WithSplice();
        var notRbf = Rbf(0x21, Id(0x20), 2_000) with { Kind = ChannelFundingKind.Splice, RbfOf = null };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => set.AddRbfSibling(notRbf));
    }

    [Fact]
    public void Given_NineteenPendingAttempts_When_AnotherIsAdded_Then_TheBatchLimitRefusesIt()
    {
        // Arrange: the current funding and 19 attempts = 20 active fundings (start_batch's limit)
        var set = WithSplice();
        uint feerate = 1_000;
        for (byte i = 1; set.ActiveCount < ChannelCommitments.MaxActiveFundings; i++)
        {
            feerate = InteractiveTxRbfRules.GetMinimumNextFeerate(feerate);
            set = set.AddRbfSibling(Rbf((byte)(0x20 + i), set.LatestAttempt!.FundingTxId, feerate));
        }

        // Act & Assert
        Assert.Equal(20, set.ActiveCount);
        var e = Assert.Throws<ArgumentException>(
            () => set.AddRbfSibling(Rbf(0x60, set.LatestAttempt!.FundingTxId, feerate * 2)));
        Assert.Contains("SP-OP-04", e.Message);
    }

    [Fact]
    public void Given_ThreeAttempts_When_SiblingsAreRead_Then_TheOthersAreReturned()
    {
        // Arrange
        var set = ThreeAttempts();

        // Act
        var siblings = set.Siblings(Id(0x21));

        // Assert
        Assert.Equal([Id(0x20), Id(0x22)], siblings.Select(f => f.FundingTxId));
        Assert.Empty(WithSplice().Siblings(Id(0x20)));
        Assert.Throws<ArgumentException>(() => set.Siblings(s_fundingTxId));
    }

    [Theory]
    [InlineData(0x20)]
    [InlineData(0x21)]
    [InlineData(0x22)]
    public void Given_ThreeAttempts_When_AnyIsLocked_Then_TheOthersAreDiscarded(byte locked)
    {
        // Arrange
        var set = ThreeAttempts();

        // Act
        var (next, retired) = set.Lock(Id(locked));

        // Assert: the locked attempt is the only funding left; the old one Replaced, the siblings Discarded
        Assert.False(next.HasPending);
        Assert.Equal(Id(locked), next.Current.FundingTxId);
        Assert.Equal(ChannelFundingStatus.Current, next.Current.Status);
        Assert.Equal(0, next.Current.LocalBalanceDeltaMsat);
        Assert.Equal(3, retired.Count);
        Assert.Equal(s_fundingTxId, retired[0].FundingTxId);
        Assert.Equal(ChannelFundingStatus.Replaced, retired[0].Status);
        Assert.Equal(set.Siblings(Id(locked)).Select(f => f.FundingTxId),
                     retired.Skip(1).Select(f => f.FundingTxId));
        Assert.All(retired.Skip(1), f => Assert.Equal(ChannelFundingStatus.Discarded, f.Status));
    }

    private static FundingSet ThreeAttempts() =>
        WithSplice().AddRbfSibling(Rbf(0x21, Id(0x20), 1_041)).AddRbfSibling(Rbf(0x22, Id(0x21), 1_100));

    private static FundingSet WithSplice() =>
        FundingSet.Single(Current())
                  .AddPending(new ChannelFunding(Id(0x20), 0, 1_100_000, s_key, s_key, 1, 100_000_000, 0,
                                                 ChannelFundingKind.Splice, ChannelFundingStatus.Pending, 1_000,
                                                 800_000));

    private static ChannelFunding Rbf(byte id, TxId rbfOf, uint feerate) =>
        new(Id(id), 0, 1_090_000, s_key, s_key, 1, 90_000_000, 0, ChannelFundingKind.SpliceRbf,
            ChannelFundingStatus.Pending, feerate, 800_000, rbfOf);

    private static ChannelFunding Current() =>
        new(s_fundingTxId, 1, 1_000_000, s_key, s_key, 0, 0, 0, ChannelFundingKind.Initial,
            ChannelFundingStatus.Current);

    private static TxId Id(byte value) => new(Enumerable.Repeat(value, 32).ToArray());
}