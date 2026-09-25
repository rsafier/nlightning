namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;

/// <summary>
/// BOLT 3 fee and trimming math against the spec's own numbers (§Fee Calculation example and the Appendix C
/// "base commitment transaction fee" / <c>num_htlcs</c> of every commitment vector).
/// </summary>
public class CommitmentFeesTests
{
    private const ulong AppendixCDustLimit = 546;

    /// <summary>
    /// Appendix C HTLCs 0-4 on the local (opener's) commitment: 0, 1, 4 are remote->local (received by the holder),
    /// 2, 3 are local->remote (offered by the holder).
    /// </summary>
    private static CommitmentSpec AppendixCSpec(uint feeratePerKw) =>
        new(CommitmentSide.Local, feeratePerKw, 6_988_000_000, 3_000_000_000,
            [
                Htlc(HtlcDirection.Incoming, 0, 1_000_000, 500),
                Htlc(HtlcDirection.Incoming, 1, 2_000_000, 501),
                Htlc(HtlcDirection.Outgoing, 2, 2_000_000, 502),
                Htlc(HtlcDirection.Outgoing, 3, 3_000_000, 503),
                Htlc(HtlcDirection.Incoming, 4, 4_000_000, 504)
            ]);

    private static SpecHtlc Htlc(HtlcDirection direction, ulong id, ulong amountMsat, uint cltv) =>
        new(direction, id, amountMsat, new Hash(new byte[32]), cltv);

    [Fact]
    public void Given_BoltExample_Feerate5000_When_Computing_Then_3315_3515_Base5340_Actual7140()
    {
        // Arrange (BOLT 3 §Fee Calculation example: feerate 5000, dust 546; two offered 5000/1000 sat, two received
        // 7000/800 sat)
        var spec = new CommitmentSpec(CommitmentSide.Local, 5000, 0, 0,
                                      [
                                          Htlc(HtlcDirection.Outgoing, 0, 5_000_000, 500),
                                          Htlc(HtlcDirection.Outgoing, 1, 1_000_000, 500),
                                          Htlc(HtlcDirection.Incoming, 0, 7_000_000, 500),
                                          Htlc(HtlcDirection.Incoming, 1, 800_000, 500)
                                      ]);

        // Act
        var timeoutFee = CommitmentFees.HtlcTimeoutFee(5000, false);
        var successFee = CommitmentFees.HtlcSuccessFee(5000, false);
        var baseFee = CommitmentFees.BaseCommitmentFee(spec, 546, false);
        var trimmed = CommitmentFees.TrimmedHtlcTotalMsat(spec, 546, false);

        // Assert
        Assert.Equal(3315UL, timeoutFee);
        Assert.Equal(3515UL, successFee);
        Assert.Equal(2, CommitmentFees.UntrimmedHtlcCount(spec, 546, false));
        Assert.Equal(5340UL, baseFee);
        Assert.Equal(7140UL, baseFee + trimmed / 1000);
    }

    [Theory]
    [InlineData(0u, 0UL, 5, 0UL)]
    [InlineData(647u, 1024UL, 5, 1024UL)]
    [InlineData(648u, 914UL, 4, 1914UL)]
    [InlineData(2069u, 2921UL, 4, 3921UL)]
    [InlineData(2070u, 2566UL, 3, 5566UL)]
    [InlineData(2194u, 2720UL, 3, 5720UL)]
    [InlineData(2195u, 2344UL, 2, 7344UL)]
    [InlineData(3702u, 3953UL, 2, 8953UL)]
    [InlineData(3703u, 3317UL, 1, 11317UL)]
    [InlineData(4914u, 4402UL, 1, 12402UL)]
    [InlineData(4915u, 3558UL, 0, 15558UL)]
    [InlineData(9651180u, 6987454UL, 0, 6999454UL)]
    public void Given_AppendixCVector_When_Computing_Then_BaseFeeNumHtlcsAndActualFeeMatch(
        uint feeratePerKw, ulong expectedBaseFee, int expectedNumHtlcs, ulong expectedActualFee)
    {
        // Arrange
        var spec = AppendixCSpec(feeratePerKw);

        // Act
        var baseFee = CommitmentFees.BaseCommitmentFee(spec, AppendixCDustLimit, false);
        var numHtlcs = CommitmentFees.UntrimmedHtlcCount(spec, AppendixCDustLimit, false);
        var trimmedSat = CommitmentFees.TrimmedHtlcTotalMsat(spec, AppendixCDustLimit, false) / 1000;

        // Assert
        Assert.Equal(expectedBaseFee, baseFee);
        Assert.Equal(expectedNumHtlcs, numHtlcs);
        Assert.Equal(expectedActualFee, baseFee + trimmedSat);
    }

    [Theory]
    [InlineData(9651181u, 6987455UL)]
    [InlineData(9651936u, 6988001UL)]
    public void Given_AppendixCVectorWithDustyToLocal_When_Computing_Then_BaseFeeMatches(uint feeratePerKw,
                                                                                        ulong expectedBaseFee)
    {
        // Arrange ("one output untrimmed" and "fee greater than funder amount": only the base fee is spec math here)
        var spec = AppendixCSpec(feeratePerKw);

        // Act / Assert
        Assert.Equal(expectedBaseFee, CommitmentFees.BaseCommitmentFee(spec, AppendixCDustLimit, false));
        Assert.Equal(0, CommitmentFees.UntrimmedHtlcCount(spec, AppendixCDustLimit, false));
    }

    [Fact]
    public void Given_AppendixCSameAmountVector_When_Counting_Then_ThreeHtlcOutputs()
    {
        // Arrange ("commitment tx with 3 htlc outputs, 2 offered having the same amount and preimage", feerate 253)
        var spec = new CommitmentSpec(CommitmentSide.Local, 253, 6_987_999_999, 3_000_000_000,
                                      [
                                          Htlc(HtlcDirection.Incoming, 1, 2_000_000, 501),
                                          Htlc(HtlcDirection.Outgoing, 5, 5_000_000, 506),
                                          Htlc(HtlcDirection.Outgoing, 6, 5_000_001, 505)
                                      ]);

        // Act / Assert
        Assert.Equal(3, CommitmentFees.UntrimmedHtlcCount(spec, AppendixCDustLimit, false));
        Assert.Equal(253UL * (724 + 3 * 172) / 1000, CommitmentFees.BaseCommitmentFee(spec, AppendixCDustLimit, false));
    }

    [Fact]
    public void Given_Anchors_When_Computing_Then_HtlcTxFeesZeroBaseWeight1124AndTwoAnchors()
    {
        // Arrange: with option_anchors an HTLC is trimmed only below the dust limit.
        var spec = new CommitmentSpec(CommitmentSide.Remote, 5000, 1_000_000_000, 0,
                                      [
                                          Htlc(HtlcDirection.Outgoing, 0, 546_000, 500), // received by the holder
                                          Htlc(HtlcDirection.Incoming, 0, 545_999, 500) // offered by the holder
                                      ]);

        // Act / Assert
        Assert.Equal(0UL, CommitmentFees.HtlcTimeoutFee(5000, true));
        Assert.Equal(0UL, CommitmentFees.HtlcSuccessFee(5000, true));
        Assert.Equal(1, CommitmentFees.UntrimmedHtlcCount(spec, 546, true));
        Assert.Equal(5000UL * (1124 + 172) / 1000, CommitmentFees.BaseCommitmentFee(spec, 546, true));
        Assert.Equal((5000UL * (1124 + 172) / 1000 + 660) * 1000, CommitmentFees.FunderCostMsat(spec, 546, true));
    }

    [Fact]
    public void Given_RemoteHolder_When_Trimming_Then_OfferedByUsIsReceivedByHolder()
    {
        // Arrange: at feerate 1000 success fee is 703, timeout 663; 1240 sat is trimmed only as a received HTLC.
        var ours = Htlc(HtlcDirection.Outgoing, 0, 1_240_000, 500);
        var remoteSpec = new CommitmentSpec(CommitmentSide.Remote, 1000, 0, 0, [ours]);
        var localSpec = new CommitmentSpec(CommitmentSide.Local, 1000, 0, 0, [ours]);

        // Act / Assert
        Assert.Equal(0, CommitmentFees.UntrimmedHtlcCount(remoteSpec, 546, false)); // 1240 < 546 + 703
        Assert.Equal(1, CommitmentFees.UntrimmedHtlcCount(localSpec, 546, false)); // 1240 >= 546 + 663
    }
}