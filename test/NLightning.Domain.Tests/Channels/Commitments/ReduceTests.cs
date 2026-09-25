namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using static CommitmentsTestKit;

/// <summary>
/// How HTLC states become commitment content (<see cref="ChannelCommitments.BuildSpec"/>): offerer debit, fulfill vs
/// fail, fee updates (B2-FEE-X01).
/// </summary>
public class ReduceTests
{
    [Fact]
    public void Given_OfferedHtlc_When_OnlyInRemoteCommit_Then_DebitedFromOffererOnlyThere()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);

        // Act
        pair.AliceCommits();

        // Assert
        var remote = pair.Alice.BuildSpec(CommitmentSide.Remote);
        Assert.Equal((600_000 - 10_000) * Sat, remote.LocalMsat);
        Assert.Equal(400_000 * Sat, remote.RemoteMsat);
        Assert.Equal(HtlcDirection.Outgoing, Assert.Single(remote.Htlcs).Direction);

        var local = pair.Alice.BuildSpec(CommitmentSide.Local);
        Assert.Equal(600_000 * Sat, local.LocalMsat);
        Assert.Empty(local.Htlcs);

        var bobLocal = pair.Bob.BuildSpec(CommitmentSide.Local);
        Assert.Equal(HtlcDirection.Incoming, Assert.Single(bobLocal.Htlcs).Direction);
        Assert.Equal((600_000 - 10_000) * Sat, bobLocal.RemoteMsat);
        pair.AssertConserved();
    }

    [Fact]
    public void Given_FulfilledHtlc_When_Final_Then_AmountMovedToReceiverAndRecordFolded()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);
        var id = pair.AliceAdd(10_000 * Sat);
        pair.AliceFullRound();

        // Act
        pair.BobFulfill(id);
        pair.BobFullRound();

        // Assert
        Assert.Empty(pair.Alice.Htlcs);
        Assert.Empty(pair.Bob.Htlcs);
        Assert.Equal(590_000 * Sat, pair.Alice.LocalBalanceMsat);
        Assert.Equal(410_000 * Sat, pair.Alice.RemoteBalanceMsat);
        Assert.Equal(410_000 * Sat, pair.Bob.LocalBalanceMsat);
        Assert.Equal(590_000 * Sat, pair.Alice.BuildSpec(CommitmentSide.Local).LocalMsat);
        Assert.Equal(590_000 * Sat, pair.Alice.BuildSpec(CommitmentSide.Remote).LocalMsat);
        pair.AssertConserved();
    }

    [Fact]
    public void Given_FailedHtlc_When_Final_Then_AmountReturnedToOfferer()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);
        var id = pair.AliceAdd(10_000 * Sat);
        pair.AliceFullRound();

        // Act
        pair.BobFail(id);
        pair.BobFullRound();

        // Assert
        Assert.Empty(pair.Alice.Htlcs);
        Assert.Equal(600_000 * Sat, pair.Alice.LocalBalanceMsat);
        Assert.Equal(400_000 * Sat, pair.Bob.LocalBalanceMsat);
        pair.AssertConserved();
    }

    [Fact]
    public void Given_FulfillCommittedOnOneSideOnly_When_Building_Then_EachViewReflectsItsOwnCommitment()
    {
        // Arrange: Bob fulfills and signs Alice's commitment; Bob's own commitment still has the HTLC.
        var pair = new CommitmentPair(600_000, 400_000);
        var id = pair.AliceAdd(10_000 * Sat);
        pair.AliceFullRound();
        pair.BobFulfill(id);

        // Act
        pair.BobCommits();

        // Assert
        var aliceLocal = pair.Alice.BuildSpec(CommitmentSide.Local);
        Assert.Empty(aliceLocal.Htlcs);
        Assert.Equal(410_000 * Sat, aliceLocal.RemoteMsat);
        var aliceRemote = pair.Alice.BuildSpec(CommitmentSide.Remote);
        Assert.Single(aliceRemote.Htlcs);
        Assert.Equal(400_000 * Sat, aliceRemote.RemoteMsat);
        pair.AssertConserved();
    }

    [Fact]
    public void Given_TwoFeeUpdates_When_BothBeforeSigning_Then_LastReplacesFirst()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);

        // Act
        pair.AliceFee(2_000);
        pair.AliceFee(3_000);

        // Assert (B2-FEE-X01: a fee update is replaced, never removed)
        Assert.Equal(2, pair.Alice.FeeUpdates.Count);
        Assert.Equal(3_000u, pair.Alice.LatestFeeratePerKw);
        Assert.Equal(2, pair.Bob.FeeUpdates.Count);
        pair.AliceFullRound();
        Assert.Equal(3_000u, pair.Alice.FeeratePerKw(CommitmentSide.Local));
        Assert.Equal(3_000u, pair.Alice.FeeratePerKw(CommitmentSide.Remote));
        Assert.Equal(3_000u, pair.Bob.LocalCommit.Spec.FeeratePerKw);
        Assert.Single(pair.Alice.FeeUpdates);
        Assert.Single(pair.Bob.FeeUpdates);
    }

    [Fact]
    public void Given_TwoFeeUpdates_When_SignedSeparately_Then_EachCommitmentUsesLastIncludedAndLastApplies()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceFee(2_000);
        var raa = pair.AliceCommits();

        // Act: a second update while the first one is only in Bob's commitment.
        pair.AliceFee(3_000);

        // Assert
        Assert.Equal(2_000u, pair.Alice.FeeratePerKw(CommitmentSide.Remote));
        Assert.Equal(1_000u, pair.Alice.FeeratePerKw(CommitmentSide.Local));
        Assert.Equal(3_000u, pair.Alice.LatestFeeratePerKw);
        Assert.Equal(3, pair.Alice.FeeUpdates.Count);

        pair.DeliverBobRevoke(raa);
        pair.DeliverAliceRevoke(pair.BobCommits());
        Assert.Equal(2_000u, pair.Alice.FeeratePerKw(CommitmentSide.Local));
        pair.AliceFullRound();
        Assert.Equal(3_000u, pair.Alice.FeeratePerKw(CommitmentSide.Local));
        Assert.Equal(3_000u, pair.Bob.FeeratePerKw(CommitmentSide.Local));
        Assert.Single(pair.Alice.FeeUpdates);
    }
}