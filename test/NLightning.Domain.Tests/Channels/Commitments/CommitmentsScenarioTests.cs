namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Exceptions;
using static CommitmentsTestKit;

/// <summary>
/// Multi-step flows between two engines: the BOLT 2 update lifecycle (B2-NO-01), crossed commitments, disconnect
/// reversal and many HTLCs, checking spec agreement and conservation after every signature.
/// </summary>
public class CommitmentsScenarioTests
{
    private static HtlcState AliceState(CommitmentPair pair, ulong id) =>
        pair.Alice.GetHtlc(HtlcDirection.Outgoing, id)?.State ?? (HtlcState)0xFF;

    private static HtlcState BobState(CommitmentPair pair, ulong id) =>
        pair.Bob.GetHtlc(HtlcDirection.Incoming, id)?.State ?? (HtlcState)0xFF;

    [Fact]
    public void Given_SpecDiagram_When_Steps1To9_Then_EachUpdateTraversesFiveStates()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);
        const HtlcState gone = (HtlcState)0xFF;

        // Act / Assert: the add (Alice -> Bob)
        var id = pair.AliceAdd(10_000 * Sat);
        Assert.Equal((HtlcState.SentAddHtlc, HtlcState.RcvdAddHtlc), (AliceState(pair, id), BobState(pair, id)));

        var bobRaa = pair.AliceCommits(); // 1: in Bob's commitment (Bob revokes at once)
        Assert.Equal((HtlcState.SentAddCommit, HtlcState.SentAddRevocation),
                     (AliceState(pair, id), BobState(pair, id)));

        pair.DeliverBobRevoke(bobRaa); // 2: Bob's previous commitment revoked
        Assert.Equal(HtlcState.RcvdAddRevocation, AliceState(pair, id));

        var aliceRaa = pair.BobCommits(); // 3-4: in Alice's commitment, Alice revokes
        Assert.Equal((HtlcState.SentAddAckRevocation, HtlcState.SentAddAckCommit),
                     (AliceState(pair, id), BobState(pair, id)));

        pair.DeliverAliceRevoke(aliceRaa); // 5: irrevocably committed, locked in at Bob
        Assert.Equal(HtlcState.RcvdAddAckRevocation, BobState(pair, id));
        Assert.True(HtlcStateTable.IsAddIrrevocablyCommitted(BobState(pair, id)));

        // Act / Assert: the removal (Bob fulfills)
        pair.BobFulfill(id);
        Assert.Equal((HtlcState.RcvdRemoveHtlc, HtlcState.SentRemoveHtlc), (AliceState(pair, id), BobState(pair, id)));

        aliceRaa = pair.BobCommits(); // 6: out of Alice's commitment, Alice revokes
        Assert.Equal((HtlcState.SentRemoveRevocation, HtlcState.SentRemoveCommit),
                     (AliceState(pair, id), BobState(pair, id)));

        pair.DeliverAliceRevoke(aliceRaa); // 7
        Assert.Equal(HtlcState.RcvdRemoveRevocation, BobState(pair, id));

        bobRaa = pair.AliceCommits(); // 8: out of Bob's commitment; Bob's record is final and folded
        Assert.Equal((HtlcState.SentRemoveAckCommit, gone), (AliceState(pair, id), BobState(pair, id)));

        pair.DeliverBobRevoke(bobRaa); // 9: final on Alice's side
        Assert.Equal(gone, AliceState(pair, id));
        Assert.Equal(590_000 * Sat, pair.Alice.LocalBalanceMsat);
        Assert.Equal(410_000 * Sat, pair.Bob.LocalBalanceMsat);
        pair.AssertConserved();
    }

    [Fact]
    public void Given_BothSidesAdd_When_CommitmentsCross_Then_BothLockInAndAgree()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat, 1);
        pair.BobAdd(20_000 * Sat, 2);
        var aliceSigner = new FakeCommitmentSigner(pair.Alice.Params.Remote.DustLimitSatoshis, false);
        var bobSigner = new FakeCommitmentSigner(pair.Bob.Params.Remote.DustLimitSatoshis, false);
        var verifier = new FakeCommitmentVerifier();
        var revocations = new FakeRevocationVerifier();

        // Act: both sign before seeing the other's commitment_signed.
        var aliceSent = pair.Alice.SendCommit(aliceSigner);
        var bobSent = pair.Bob.SendCommit(bobSigner);
        var aliceCs = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(aliceSent.Outbound));
        var bobCs = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(bobSent.Outbound));
        var alice = aliceSent.Next.ReceiveCommit(bobCs.Signatures, verifier);
        var bob = bobSent.Next.ReceiveCommit(aliceCs.Signatures, verifier);
        CommitmentPair.AssertMirrored(aliceSent.Next.RemoteNextCommit!.Commit.Spec, bob.Next.LocalCommit.Spec);
        CommitmentPair.AssertMirrored(bobSent.Next.RemoteNextCommit!.Commit.Spec, alice.Next.LocalCommit.Spec);

        var aliceRaa = Assert.IsType<OutboundRevokeAndAck>(Assert.Single(alice.Outbound));
        var bobRaa = Assert.IsType<OutboundRevokeAndAck>(Assert.Single(bob.Outbound));
        var aliceNext = alice.Next.ReceiveRevoke(SecretFor(BobTag, bobRaa.RevokedCommitmentNumber),
                                                 Point(BobTag, bobRaa.NextCommitmentNumber), revocations).Next;
        var bobNext = bob.Next.ReceiveRevoke(SecretFor(AliceTag, aliceRaa.RevokedCommitmentNumber),
                                             Point(AliceTag, aliceRaa.NextCommitmentNumber), revocations).Next;

        // Assert: each side now owes a commitment for the other's add.
        Assert.True(aliceNext.CanSendCommit);
        Assert.True(bobNext.CanSendCommit);
        var pairAfter = new CommitmentPairState(aliceNext, bobNext);
        pairAfter.CrossAgain(aliceSigner, bobSigner, verifier, revocations);
        Assert.Equal(HtlcState.SentAddAckRevocation, pairAfter.Alice.GetHtlc(HtlcDirection.Outgoing, 0)!.State);
        Assert.Equal(HtlcState.RcvdAddAckRevocation, pairAfter.Alice.GetHtlc(HtlcDirection.Incoming, 0)!.State);
        Assert.Equal(HtlcState.SentAddAckRevocation, pairAfter.Bob.GetHtlc(HtlcDirection.Outgoing, 0)!.State);
        Assert.Equal(HtlcState.RcvdAddAckRevocation, pairAfter.Bob.GetHtlc(HtlcDirection.Incoming, 0)!.State);
        Assert.Equal(2UL, pairAfter.Alice.LocalCommit.Number);
        Assert.Equal(2UL, pairAfter.Alice.RemoteCommit.Number);
        CommitmentPair.AssertMirrored(pairAfter.Alice.LocalCommit.Spec, pairAfter.Bob.RemoteCommit.Spec);
        CommitmentPair.AssertMirrored(pairAfter.Bob.LocalCommit.Spec, pairAfter.Alice.RemoteCommit.Spec);
    }

    [Fact]
    public void Given_UnsignedPeerUpdates_When_RevertUncommitted_Then_ReversedAndSameIdAcceptedOnce()
    {
        // Arrange: Bob's add 0 is signed and locked in; his add 1, a removal and a fee are unsigned at Alice.
        var pair = new CommitmentPair(400_000, 600_000);
        pair.BobAdd(20_000 * Sat, 2);
        pair.BobFullRound();
        pair.AliceAdd(10_000 * Sat, 1);
        pair.AliceFullRound();
        pair.BobFulfill(0);
        pair.BobAdd(15_000 * Sat, 3);
        var beforeRevert = pair.Alice;

        // Act
        var result = pair.Alice.RevertUncommitted();

        // Assert
        var next = result.Next;
        Assert.Equal(1UL, Assert.Single(result.Transition.DroppedHtlcs).Id);
        Assert.Null(next.GetHtlc(HtlcDirection.Incoming, 1));
        Assert.Equal(1UL, next.RemoteNextHtlcId);
        var reverted = next.GetHtlc(HtlcDirection.Outgoing, 0)!;
        Assert.Equal(HtlcState.SentAddAckRevocation, reverted.State);
        Assert.Null(reverted.Removal);
        Assert.Equal(HtlcState.RcvdAddAckRevocation, next.GetHtlc(HtlcDirection.Incoming, 0)!.State);
        Assert.Equal(2UL, beforeRevert.RemoteNextHtlcId);

        // B2-ADD-R06: after reconnection the peer re-sends id 1 and it is accepted once.
        var resent = next.ReceiveAdd(1, 15_000 * Sat, PaymentHash(3), 600, Onion).Next;
        Assert.Equal(HtlcState.RcvdAddHtlc, resent.GetHtlc(HtlcDirection.Incoming, 1)!.State);
        Assert.Throws<CommitmentViolationException>(() => resent.ReceiveAdd(1, 15_000 * Sat, PaymentHash(3), 600,
                                                                            Onion));
    }

    [Fact]
    public void Given_OurUnsignedUpdates_When_RevertUncommitted_Then_Kept()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        pair.AliceFee(2_000);

        // Act
        var result = pair.Alice.RevertUncommitted();

        // Assert
        Assert.Equal(HtlcState.SentAddHtlc, result.Next.GetHtlc(HtlcDirection.Outgoing, 0)!.State);
        Assert.Equal(2_000u, result.Next.LatestFeeratePerKw);
        Assert.True(result.Transition.IsEmpty);
    }

    [Fact]
    public void Given_PeerUnsignedFee_When_RevertUncommitted_Then_Dropped()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceFee(2_000);

        // Act
        var result = pair.Bob.RevertUncommitted();

        // Assert
        Assert.Single(result.Next.FeeUpdates);
        Assert.Equal(1_000u, result.Next.LatestFeeratePerKw);
        Assert.True(result.Transition.FeeUpdatesChanged);
    }

    [Fact]
    public void Given_ThirtyHtlcsEachWay_When_AllSettled_Then_BalancesConserveAndAgree()
    {
        // Arrange
        var pair = new CommitmentPair(500_000, 500_000);

        // Act
        for (byte i = 0; i < 30; i++)
        {
            pair.AliceAdd((1_000 + i * 500UL) * Sat, 1);
            pair.BobAdd((2_000 + i * 300UL) * Sat, 2);
        }

        pair.Converge();
        for (ulong i = 0; i < 30; i++)
        {
            if (i % 2 == 0)
                pair.BobFulfill(i);
            else
                pair.BobFail(i);
            pair.AliceFulfill(i);
        }

        pair.Converge();

        // Assert: Bob receives the even Alice HTLCs, Alice receives all of Bob's.
        var aliceToBob = Enumerable.Range(0, 30).Where(i => i % 2 == 0).Sum(i => (1_000 + i * 500L) * 1_000);
        var bobToAlice = Enumerable.Range(0, 30).Sum(i => (2_000 + i * 300L) * 1_000);
        Assert.Empty(pair.Alice.Htlcs);
        Assert.Empty(pair.Bob.Htlcs);
        Assert.Equal((ulong)(500_000_000 - aliceToBob + bobToAlice), pair.Alice.LocalBalanceMsat);
        Assert.Equal(pair.Alice.LocalBalanceMsat, pair.Bob.RemoteBalanceMsat);
        CommitmentPair.AssertMirrored(pair.Alice.LocalCommit.Spec, pair.Bob.RemoteCommit.Spec);
        pair.AssertConserved();
    }

    /// <summary>Small helper for the crossed-commitment test: both sign, both receive, both revoke.</summary>
    private sealed class CommitmentPairState(ChannelCommitments alice, ChannelCommitments bob)
    {
        public ChannelCommitments Alice { get; private set; } = alice;
        public ChannelCommitments Bob { get; private set; } = bob;

        public void CrossAgain(FakeCommitmentSigner aliceSigner, FakeCommitmentSigner bobSigner,
                               FakeCommitmentVerifier verifier, FakeRevocationVerifier revocations)
        {
            var aliceSent = Alice.SendCommit(aliceSigner);
            var bobSent = Bob.SendCommit(bobSigner);
            var alice = aliceSent.Next.ReceiveCommit(((OutboundCommitmentSigned)bobSent.Outbound[0]).Signatures,
                                                     verifier);
            var bob = bobSent.Next.ReceiveCommit(((OutboundCommitmentSigned)aliceSent.Outbound[0]).Signatures,
                                                 verifier);
            var aliceRaa = (OutboundRevokeAndAck)alice.Outbound[0];
            var bobRaa = (OutboundRevokeAndAck)bob.Outbound[0];
            Alice = alice.Next.ReceiveRevoke(SecretFor(BobTag, bobRaa.RevokedCommitmentNumber),
                                             Point(BobTag, bobRaa.NextCommitmentNumber), revocations).Next;
            Bob = bob.Next.ReceiveRevoke(SecretFor(AliceTag, aliceRaa.RevokedCommitmentNumber),
                                         Point(AliceTag, aliceRaa.NextCommitmentNumber), revocations).Next;
        }
    }
}