namespace NLightning.Application.Tests.Channels.Services;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;

/// <summary>
/// Two commitment engines joined through the production engine ports with <b>real</b> secp256k1 signatures (NL-230,
/// BOLT2 plan "Remaining before N5" §1): every commitment one side signs is verified by the other against the
/// commitment it builds itself, and both sides compute the same txid, through add, commitment_signed,
/// revoke_and_ack, fulfill, fail and update_fee.
/// </summary>
public class EngineCommitmentPortsTwoNodeTests
{
    private const ulong Sat = 1_000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_TwoEnginesWithRealSigners_When_AddCommitRevokeFulfillFailAndFee_Then_BothSidesSignAndVerifyTheSameTxIds(
        bool hasAnchors)
    {
        // Arrange
        using var pair = new RealSigningCommitmentPair(hasAnchors);
        var alice = pair.Alice;
        var bob = pair.Bob;
        var preimage1 = RealSigningCommitmentPair.Preimage(1);
        var preimage2 = RealSigningCommitmentPair.Preimage(2);
        var preimage3 = RealSigningCommitmentPair.Preimage(3);

        // Act
        // 1. Alice offers a large HTLC and one that is untrimmed on her commitment only without anchors (dust 546 +
        //    HTLC-timeout 1657 <= 2250 < dust 600 + HTLC-success 1757), then both commit
        var big = pair.Add(alice, 50_000 * Sat, preimage1);
        var small = pair.Add(alice, 2_250 * Sat, preimage2);
        pair.Settle(alice);

        // 2. Bob offers one back
        var back = pair.Add(bob, 30_000 * Sat, preimage3);
        pair.Settle(bob);

        // 3. Bob fulfills the large one and fails the small one
        pair.Fulfill(bob, big, preimage1);
        pair.Fail(bob, small);
        pair.Settle(bob);

        // 4. Alice fails Bob's HTLC
        pair.Fail(alice, back);
        pair.Settle(alice);

        // 5. The funder doubles the feerate
        pair.UpdateFee(RealSigningCommitmentPair.InitialFeeratePerKw * 2);
        pair.Settle(alice);

        // Assert
        Assert.True(pair.Commitments.Count >= 10, $"only {pair.Commitments.Count} commitments were signed");
        Assert.All(pair.Commitments, c => Assert.Equal(c.Signed, c.Verified));
        Assert.Equal(pair.Commitments.Count, pair.Commitments.Select(c => c.Signed).Distinct().Count());

        Assert.Empty(alice.State.Htlcs);
        Assert.Empty(bob.State.Htlcs);
        var aliceExpectedMsat =
            (RealSigningCommitmentPair.FundingSatoshis - RealSigningCommitmentPair.AlicePushedSatoshis - 50_000) * Sat;
        Assert.Equal(aliceExpectedMsat, alice.State.LocalBalanceMsat);
        Assert.Equal(alice.State.LocalBalanceMsat, bob.State.RemoteBalanceMsat);
        Assert.Equal(alice.State.LocalCommit.Number, bob.State.RemoteCommit.Number);
        Assert.Equal(bob.State.LocalCommit.Number, alice.State.RemoteCommit.Number);
        Assert.Equal(RealSigningCommitmentPair.InitialFeeratePerKw * 2, alice.State.LocalCommit.Spec.FeeratePerKw);
        Assert.Equal(RealSigningCommitmentPair.InitialFeeratePerKw * 2, bob.State.LocalCommit.Spec.FeeratePerKw);

        // N4-T4 events: each lock-in once on revoke_and_ack, the fulfill at once, fails and settles only when final
        Assert.Equal([(typeof(IncomingHtlcLockedIn), big), (typeof(IncomingHtlcLockedIn), small),
                      (typeof(OutgoingHtlcFailed), back), (typeof(OutgoingHtlcSettled), back)],
                     bob.Events.Select(e => (e.Event.GetType(), e.Event.HtlcId)));
        Assert.Equal([(typeof(IncomingHtlcLockedIn), back), (typeof(OutgoingHtlcFulfilled), big),
                      (typeof(OutgoingHtlcSettled), big),
                      (typeof(OutgoingHtlcFailed), small), (typeof(OutgoingHtlcSettled), small)],
                     alice.Events.Select(e => (e.Event.GetType(), e.Event.HtlcId)));
        Assert.All(alice.Events.Concat(bob.Events).Where(e => e.Event is not OutgoingHtlcFulfilled),
                   e => Assert.Equal("receive revoke", e.Step));
        Assert.Equal("receive fulfill", Assert.Single(alice.Events, e => e.Event is OutgoingHtlcFulfilled).Step);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_IncomingAdd_When_BothRevokedWithRealSignatures_Then_IncomingHtlcLockedInOnce(bool hasAnchors)
    {
        // Arrange
        using var pair = new RealSigningCommitmentPair(hasAnchors);
        var id = pair.Add(pair.Alice, 50_000 * Sat, RealSigningCommitmentPair.Preimage(1));

        // Act / Assert - Alice signs, Bob revokes: the add is in both commitments, Alice's old one not yet revoked
        pair.Commit(pair.Alice);
        Assert.Empty(pair.Bob.Events);
        Assert.Equal(HtlcState.SentAddRevocation, pair.Bob.State.GetHtlc(HtlcDirection.Incoming, id)!.State);

        // Bob signs, Alice revokes: locked in
        pair.Commit(pair.Bob);
        var (step, domainEvent) = Assert.Single(pair.Bob.Events);
        Assert.Equal("receive revoke", step);
        var lockedIn = Assert.IsType<IncomingHtlcLockedIn>(domainEvent);
        Assert.Equal(id, lockedIn.HtlcId);
        Assert.Equal(RealSigningCommitmentPair.ChannelId, lockedIn.ChannelId);
        Assert.Equal(RealSigningCommitmentPair.Hash(RealSigningCommitmentPair.Preimage(1)), lockedIn.Htlc.PaymentHash);

        // Later commitments never raise it again, and the replay still has it until Bob resolves the HTLC
        pair.UpdateFee(RealSigningCommitmentPair.InitialFeeratePerKw * 2);
        pair.Settle(pair.Alice);
        Assert.Single(pair.Bob.Events);
        Assert.Equal(lockedIn.HtlcId,
                     Assert.IsType<IncomingHtlcLockedIn>(Assert.Single(ChannelDomainEvents.DerivePending(pair.Bob.State)))
                           .HtlcId);
        Assert.All(pair.Commitments, c => Assert.Equal(c.Signed, c.Verified));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_DownstreamFailWithRealSignatures_Then_OutgoingHtlcFailedOnlyWhenIrrevocable(bool hasAnchors)
    {
        // Arrange - Alice's HTLC is locked in, then Bob fails it
        using var pair = new RealSigningCommitmentPair(hasAnchors);
        var id = pair.Add(pair.Alice, 50_000 * Sat, RealSigningCommitmentPair.Preimage(1));
        pair.Settle(pair.Alice);
        pair.Fail(pair.Bob, id);

        // Act / Assert - received, then committed and revoked on Alice's side: still revocable on Bob's
        Assert.Empty(pair.Alice.Events);
        pair.Commit(pair.Bob);
        Assert.Empty(pair.Alice.Events);
        Assert.Equal(HtlcState.SentRemoveRevocation, pair.Alice.State.GetHtlc(HtlcDirection.Outgoing, id)!.State);

        // Alice signs Bob's commitment without it and Bob revokes the old one: irrevocable
        pair.Commit(pair.Alice);

        Assert.Collection(pair.Alice.Events,
                          e =>
                          {
                              Assert.Equal("receive revoke", e.Step);
                              var failed = Assert.IsType<OutgoingHtlcFailed>(e.Event);
                              Assert.Equal(id, failed.HtlcId);
                              Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
                          },
                          e =>
                          {
                              Assert.Equal("receive revoke", e.Step);
                              Assert.Equal(id, Assert.IsType<OutgoingHtlcSettled>(e.Event).HtlcId);
                          });
        Assert.Empty(pair.Alice.State.Htlcs);
        Assert.All(pair.Commitments, c => Assert.Equal(c.Signed, c.Verified));
    }

    [Fact]
    public void Given_TamperedCommitmentSignature_When_PeerReceivesIt_Then_RejectedWithB2CsR01()
    {
        // Arrange
        using var pair = new RealSigningCommitmentPair(false);
        pair.Add(pair.Alice, 50_000 * Sat, RealSigningCommitmentPair.Preimage(1));
        var sent = pair.Alice.State.SendCommit(pair.Alice.CommitmentSigner);
        var signatures = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(sent.Outbound)).Signatures;
        byte[] tampered = signatures.Signature;
        tampered[10] ^= 0x01;

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(
            () => pair.Bob.State.ReceiveCommit(signatures with { Signature = new CompactSignature(tampered) },
                                               pair.Bob.CommitmentVerifier));

        // Assert
        Assert.Equal("B2-CS-R01", exception.RequirementId);
    }

    [Fact]
    public void Given_HtlcSignaturesOfAnotherHtlc_When_PeerReceivesThem_Then_RejectedWithB2CsR01()
    {
        // Arrange - two untrimmed HTLCs, their signatures swapped
        using var pair = new RealSigningCommitmentPair(false);
        pair.Add(pair.Alice, 50_000 * Sat, RealSigningCommitmentPair.Preimage(1));
        pair.Add(pair.Alice, 60_000 * Sat, RealSigningCommitmentPair.Preimage(2));
        var sent = pair.Alice.State.SendCommit(pair.Alice.CommitmentSigner);
        var signatures = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(sent.Outbound)).Signatures;
        Assert.Equal(2, signatures.HtlcSignatures.Count);
        var swapped = signatures with { HtlcSignatures = [signatures.HtlcSignatures[1], signatures.HtlcSignatures[0]] };

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(
            () => pair.Bob.State.ReceiveCommit(swapped, pair.Bob.CommitmentVerifier));

        // Assert
        Assert.Equal("B2-CS-R01", exception.RequirementId);
    }

    [Fact]
    public void Given_SecretOfAnotherCommitment_When_RevokeAndAckReceived_Then_RejectedWithB2RaaR01()
    {
        // Arrange - Bob answers Alice's commitment_signed with the secret of the wrong commitment
        using var pair = new RealSigningCommitmentPair(false);
        pair.Add(pair.Alice, 50_000 * Sat, RealSigningCommitmentPair.Preimage(1));
        var sent = pair.Alice.State.SendCommit(pair.Alice.CommitmentSigner);
        pair.Alice.State = sent.Next;
        var commitmentSigned = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(sent.Outbound));
        pair.Bob.State = pair.Bob.State.ReceiveCommit(commitmentSigned.Signatures, pair.Bob.CommitmentVerifier).Next;
        pair.Bob.Signer.AdvanceLocalCommitment(RealSigningCommitmentPair.ChannelId, 2);
        var wrongSecret = pair.Bob.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, 1);

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(
            () => pair.Alice.State.ReceiveRevoke(wrongSecret, pair.Bob.Point(2), pair.Alice.RevocationVerifier));

        // Assert
        Assert.Equal("B2-RAA-R01", exception.RequirementId);
        Assert.True(exception.MustFailChannel);
    }

    [Fact]
    public void Given_HtlcTrimmedOnBothCommitments_When_Committed_Then_NoHtlcSignaturesAndTheTxIdsMatch()
    {
        // Arrange - an HTLC that is trimmed on both commitments still needs a commitment_signed
        using var pair = new RealSigningCommitmentPair(false);
        pair.Add(pair.Alice, 1_000 * Sat, RealSigningCommitmentPair.Preimage(1));

        // Act
        pair.Settle(pair.Alice);

        // Assert
        Assert.All(pair.Commitments, c => Assert.Equal(c.Signed, c.Verified));
        Assert.Equal(HtlcState.SentAddAckRevocation, Assert.Single(pair.Alice.State.Htlcs.Values).State);
        Assert.Empty(pair.Alice.State.LocalCommit.RemoteSignatures!.HtlcSignatures);
    }
}