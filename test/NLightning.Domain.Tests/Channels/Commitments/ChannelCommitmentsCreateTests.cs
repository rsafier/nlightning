namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using static CommitmentsTestKit;

/// <summary>
/// Creating and restoring the aggregate, and the transition record it hands to persistence.
/// </summary>
public class ChannelCommitmentsCreateTests
{
    [Fact]
    public void Given_OpeningBalances_When_Create_Then_NumbersZeroIdsZeroAndOneFinalFee()
    {
        // Act
        var c = Create(600_000, 400_000, feeratePerKw: 2_500);

        // Assert
        Assert.Equal(0UL, c.LocalCommit.Number);
        Assert.Equal(0UL, c.RemoteCommit.Number);
        Assert.Equal(0UL, c.LocalNextHtlcId);
        Assert.Equal(0UL, c.RemoteNextHtlcId);
        var fee = Assert.Single(c.FeeUpdates);
        Assert.Equal(HtlcState.SentAddAckRevocation, fee.State);
        Assert.Equal(2_500u, c.FeeratePerKw(CommitmentSide.Local));
        Assert.Equal(2_500u, c.FeeratePerKw(CommitmentSide.Remote));
        Assert.Equal(c.LocalCommit.Spec, c.BuildSpec(CommitmentSide.Local));
        Assert.Equal(c.RemoteCommit.Spec, c.BuildSpec(CommitmentSide.Remote));
        Assert.Null(c.RemoteNextCommit);
        Assert.False(c.CanSendCommit);
    }

    [Fact]
    public void Given_PeerIsFunder_When_Create_Then_OpeningFeeOwnedByPeer()
    {
        // Act
        var c = Create(400_000, 600_000, localIsFunder: false);

        // Assert
        Assert.Equal(HtlcState.RcvdAddAckRevocation, Assert.Single(c.FeeUpdates).State);
        Assert.Equal(HtlcDirection.Incoming, c.FeeUpdates[0].Owner);
    }

    [Fact]
    public void Given_BalancesNotMatchingFunding_When_Create_Then_Throws()
    {
        // Arrange
        var parameters = Params(600_000, 400_000);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => ChannelCommitments.Create(ChannelId, parameters, 600_000 * Sat,
                                                                         400_000 * Sat - 1, 1_000, Point(BobTag, 0),
                                                                         Point(BobTag, 1)));
    }

    [Fact]
    public void Given_MidDanceSnapshot_When_Restored_Then_EqualBehaviour()
    {
        // Arrange: Alice signed, waiting for Bob's revoke_and_ack.
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        var raa = pair.AliceCommits();
        var a = pair.Alice;

        // Act
        var restored = ChannelCommitments.Restore(a.ChannelId, a.Params, a.LocalBalanceMsat, a.RemoteBalanceMsat,
                                                  a.Htlcs.Values, a.FeeUpdates, a.LocalNextHtlcId, a.RemoteNextHtlcId,
                                                  a.LocalCommit, a.RemoteCommit, a.RemoteNextCommit,
                                                  a.RemoteNextPerCommitmentPoint);

        // Assert
        Assert.Equal(a.BuildSpec(CommitmentSide.Remote), restored.BuildSpec(CommitmentSide.Remote));
        var next = restored.ReceiveRevoke(SecretFor(BobTag, raa.RevokedCommitmentNumber),
                                          Point(BobTag, raa.NextCommitmentNumber), new FakeRevocationVerifier()).Next;
        Assert.Equal(HtlcState.RcvdAddRevocation, next.GetHtlc(HtlcDirection.Outgoing, 0)!.State);
    }

    [Fact]
    public void Given_LegacyHtlcState_When_Restored_Then_Throws()
    {
        // Arrange
        var c = Create(600_000, 400_000);
        var legacy = new HtlcRecord(HtlcDirection.Outgoing, 0, 10_000 * Sat, PaymentHash(1), 600, HtlcState.Offered);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => ChannelCommitments.Restore(c.ChannelId, c.Params,
                                                                          c.LocalBalanceMsat,
                                                                          c.RemoteBalanceMsat, [legacy], c.FeeUpdates,
                                                                          1, 0, c.LocalCommit, c.RemoteCommit, null,
                                                                          c.RemoteNextPerCommitmentPoint));
    }

    [Theory]
    [InlineData(HtlcDirection.Incoming, HtlcState.SentAddHtlc, 1UL)] // owner mismatch
    [InlineData(HtlcDirection.Outgoing, HtlcState.SentAddHtlc, 0UL)] // id not below the next id
    [InlineData(HtlcDirection.Outgoing, HtlcState.RcvdRemoveHtlc, 1UL)] // removal state without removal data
    public void Given_InconsistentHtlc_When_Restored_Then_Throws(HtlcDirection direction, HtlcState state,
                                                                 ulong nextId)
    {
        // Arrange
        var c = Create(600_000, 400_000);
        var htlc = new HtlcRecord(direction, 0, 10_000 * Sat, PaymentHash(1), 600, state);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => ChannelCommitments.Restore(c.ChannelId, c.Params,
                                                                          c.LocalBalanceMsat,
                                                                          c.RemoteBalanceMsat, [htlc], c.FeeUpdates,
                                                                          nextId, nextId, c.LocalCommit,
                                                                          c.RemoteCommit, null,
                                                                          c.RemoteNextPerCommitmentPoint));
    }

    [Fact]
    public void Given_BalancesNotConserved_When_Restored_Then_Throws()
    {
        // Arrange
        var c = Create(600_000, 400_000);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => ChannelCommitments.Restore(c.ChannelId, c.Params, c.LocalBalanceMsat,
                                                                          c.RemoteBalanceMsat + 1, [], c.FeeUpdates,
                                                                          0, 0, c.LocalCommit, c.RemoteCommit, null,
                                                                          c.RemoteNextPerCommitmentPoint));
    }

    [Fact]
    public void Given_FinalRemoval_When_Revoked_Then_TransitionListsSettledHtlc()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);
        var id = pair.AliceAdd(10_000 * Sat);
        pair.AliceFullRound();
        pair.BobFulfill(id);
        pair.DeliverAliceRevoke(pair.BobCommits());
        var bobRaa = pair.AliceCommits();

        // Act
        var result = pair.Alice.ReceiveRevoke(SecretFor(BobTag, bobRaa.RevokedCommitmentNumber),
                                              Point(BobTag, bobRaa.NextCommitmentNumber),
                                              new FakeRevocationVerifier());

        // Assert
        var settled = Assert.Single(result.Transition.SettledHtlcs);
        Assert.Equal(HtlcState.RcvdRemoveAckRevocation, settled.State);
        Assert.Equal(HtlcRemovalKind.Fulfill, settled.Removal!.Kind);
        Assert.Empty(result.Next.Htlcs);
        Assert.True(result.Transition.ScalarsChanged);
    }
}