namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Splicing.Enums;
using Domain.Exceptions;
using static SpliceTestKit;

/// <summary>
/// The commitment engine with several fundings (splicing plan §3.3, SP1-B-T1..T4): per-funding specs (I6 per
/// funding), batched send (SP-OP-03) and receive (SP-OP-05/06) with one revocation (SP-OP-07), the splice commitment
/// step (SP-CS-01/02), validation against every funding (SP-OP-01, SP-I6, D9), lock and discard.
/// </summary>
public class SpliceCommitmentsTests
{
    private const ulong Sat = CommitmentsTestKit.Sat;

    // Alice (funder) 600,000 sat, Bob 400,000 sat; Alice splices in 500,000 sat
    private static SplicePair SplicedIn()
    {
        var pair = new SplicePair(600_000, 400_000);
        pair.Splice(Splice(0x22, 1_500_000, 500_000, 0));
        return pair;
    }

    #region Single funding (byte-identical)

    [Fact]
    public void Given_NoPendingSplice_When_SendCommit_Then_OneCommitmentSignedNamingTheCurrentFunding()
    {
        // Arrange
        var alice = SpliceTestKit.Create(600_000, 400_000).Add(10_000 * Sat).Next;
        var withoutFundingData = CommitmentsTestKit.Create(600_000, 400_000).Add(10_000 * Sat).Next;
        var signer = new FakeCommitmentSigner(546, false);
        var referenceSigner = new FakeCommitmentSigner(546, false);

        // Act
        var result = alice.SendCommit(signer);
        var reference = withoutFundingData.SendCommit(referenceSigner);

        // Assert: the same single outbound as an engine without any funding data (no start_batch), with the current
        // funding's txid (the channel's funding output before any splice)
        var cs = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(result.Outbound));
        Assert.Equal(InitialTxId, cs.FundingTxId);
        var referenceCs = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(reference.Outbound));
        Assert.Equal(referenceCs.RemoteCommitmentNumber, cs.RemoteCommitmentNumber);
        Assert.Equal(referenceCs.Signatures.Signature, cs.Signatures.Signature);
        Assert.Equal(referenceCs.Signatures.HtlcSignatures, cs.Signatures.HtlcSignatures);
        Assert.Equal(referenceSigner.Calls, signer.Calls);
        Assert.Empty(result.Next.RemoteNextCommit!.PendingFundingSignatures);
        Assert.False(result.Transition.FundingsChanged);
        Assert.Null(result.Transition.RetiredFundings);
    }

    [Fact]
    public void Given_NoPendingSplice_When_ARevokeIsReceived_Then_NoRevokedFundingsAreListed()
    {
        // Arrange
        var pair = new SplicePair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        var sent = pair.Alice.SendCommit(new BindingCommitmentSigner(546, false));
        var received = pair.Bob.ReceiveCommit(Assert.Single(ToBatch(sent.Outbound)).Signatures,
                                              new BindingCommitmentVerifier());
        var raa = Assert.IsType<OutboundRevokeAndAck>(Assert.Single(received.Outbound));

        // Act
        var revoked = sent.Next.ReceiveRevoke(CommitmentsTestKit.SecretFor(CommitmentsTestKit.BobTag, raa.RevokedCommitmentNumber),
                                              CommitmentsTestKit.Point(CommitmentsTestKit.BobTag, raa.NextCommitmentNumber),
                                              new FakeRevocationVerifier());

        // Assert
        Assert.NotNull(revoked.Transition.RevokedRemoteCommit);
        Assert.Null(revoked.Transition.RevokedRemoteCommitFundings);
    }

    #endregion

    #region SP-CS-01/02: splice commitment step

    [Fact]
    public void Given_ASettledChannel_When_SigningASpliceCommitment_Then_CurrentNumberOnTheNewFundingWithoutStateChange()
    {
        // Arrange
        var pair = new SplicePair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        pair.Converge();
        var splice = Splice(0x22, 1_500_000, 500_000, 0);
        var signer = new BindingCommitmentSigner(546, false);

        // Act
        var result = pair.Alice.SignSpliceCommitment(splice, signer);

        // Assert: SP-CS-01 same number as the existing remote commitment, balances moved to the new funding
        var cs = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(result.Outbound));
        Assert.Equal(pair.Alice.RemoteCommit.Number, cs.RemoteCommitmentNumber);
        Assert.Equal(splice.FundingTxId, cs.FundingTxId);
        var (funding, number, spec) = Assert.Single(signer.Calls);
        Assert.Equal(splice.FundingTxId, funding);
        Assert.Equal(pair.Alice.RemoteCommit.Number, number);
        Assert.Equal(1_500_000 * Sat, spec.TotalMsat);
        Assert.Equal(pair.Alice.RemoteCommit.Spec.LocalMsat + 500_000 * Sat, spec.LocalMsat);
        Assert.Equal(pair.Alice.RemoteCommit.Spec.Htlcs, spec.Htlcs);
        Assert.Same(pair.Alice, result.Next);
        Assert.True(result.Transition.IsEmpty);
    }

    [Fact]
    public void Given_APeerSpliceCommitment_When_Received_Then_FundingPendingAndNoRevokeAndAck()
    {
        // Arrange
        var pair = new SplicePair(600_000, 400_000);
        var aliceView = Splice(0x22, 1_500_000, 500_000, 0);
        var bobView = Mirror(aliceView);
        var aliceCs = Assert.IsType<OutboundCommitmentSigned>(
            Assert.Single(pair.Alice.SignSpliceCommitment(aliceView, new BindingCommitmentSigner(546, false)).Outbound));
        var verifier = new BindingCommitmentVerifier();

        // Act
        var result = pair.Bob.ReceiveSpliceCommitment(bobView, aliceCs.Signatures, verifier);

        // Assert: SP-CS-02 no revoke_and_ack, the number does not move; SP-I2 the signatures are kept
        Assert.Empty(result.Outbound);
        Assert.Equal(pair.Bob.LocalCommit.Number, result.Next.LocalCommit.Number);
        Assert.Equal(pair.Bob.LocalCommit.Number, Assert.Single(verifier.Calls).Number);
        Assert.Equal(bobView, Assert.Single(result.Next.PendingFundings));
        Assert.Equal(aliceCs.Signatures, result.Next.LocalCommit.SignaturesFor(bobView.FundingTxId));
        Assert.True(result.Transition.FundingsChanged);
        Assert.True(result.Transition.LocalCommitChanged);
        Assert.Equal(1_500_000 * Sat, result.Next.BuildSpec(CommitmentSide.Local, bobView).TotalMsat);
    }

    [Fact]
    public void Given_AnInvalidSpliceCommitment_When_Received_Then_ChannelFailsAndNothingChanges()
    {
        // Arrange
        var pair = new SplicePair(600_000, 400_000);
        var bobView = Mirror(Splice(0x22, 1_500_000, 500_000, 0));
        var wrong = new CommitmentSignatures(CommitmentsTestKit.Signature(9), []);

        // Act
        var e = Assert.Throws<CommitmentViolationException>(
            () => pair.Bob.ReceiveSpliceCommitment(bobView, wrong, new BindingCommitmentVerifier()));

        // Assert
        Assert.Equal("B2-CS-R01", e.RequirementId);
        Assert.True(e.MustFailChannel);
        Assert.Empty(pair.Bob.PendingFundings);
    }

    [Fact]
    public void Given_UpdatesPending_When_SigningASpliceCommitment_Then_Refused()
    {
        // Arrange: SP-I8, a splice commitment needs the quiescent state
        var pair = new SplicePair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);

        // Act
        var e = Assert.Throws<CommitmentRefusedException>(
            () => pair.Alice.SignSpliceCommitment(Splice(0x22, 1_500_000, 500_000, 0),
                                                  new BindingCommitmentSigner(546, false)));
        var violation = Assert.Throws<CommitmentViolationException>(
            () => pair.Bob.ReceiveSpliceCommitment(Mirror(Splice(0x22, 1_500_000, 500_000, 0)),
                                                   new CommitmentSignatures(CommitmentsTestKit.Signature(1), []),
                                                   new BindingCommitmentVerifier()));

        // Assert
        Assert.Equal("SP-CS-01", e.RequirementId);
        Assert.Equal("SP-CS-01", violation.RequirementId);
    }

    [Fact]
    public void Given_ASpliceNotConservingValue_When_Signing_Then_ArgumentException()
    {
        // Arrange: +500,000 sat of capacity but only +400,000 sat of balance
        var pair = new SplicePair(600_000, 400_000);

        // Act / Assert
        Assert.Throws<ArgumentException>(
            () => pair.Alice.SignSpliceCommitment(Splice(0x22, 1_500_000, 400_000, 0),
                                                  new BindingCommitmentSigner(546, false)));
    }

    [Fact]
    public void Given_ASpliceOutAboveTheBalance_When_Signing_Then_Refused()
    {
        // Arrange: Alice has 600,000 sat and splices out 700,000 sat
        var pair = new SplicePair(600_000, 400_000);

        // Act / Assert
        Assert.Throws<InvalidOperationException>(
            () => pair.Alice.SignSpliceCommitment(Splice(0x22, 300_000, -700_000, 0),
                                                  new BindingCommitmentSigner(546, false)));
    }

    #endregion

    #region SP-OP-03: batched send

    [Fact]
    public void Given_APendingSplice_When_SendCommit_Then_StartBatchThenCurrentThenSpliceAtTheSameNumber()
    {
        // Arrange
        var pair = SplicedIn();
        pair.AliceAdd(20_000 * Sat);
        var signer = new BindingCommitmentSigner(546, false);

        // Act
        var result = pair.Alice.SendCommit(signer);

        // Assert
        Assert.Equal(3, result.Outbound.Count);
        Assert.Equal(new OutboundStartBatch(2), result.Outbound[0]);
        var current = Assert.IsType<OutboundCommitmentSigned>(result.Outbound[1]);
        var splice = Assert.IsType<OutboundCommitmentSigned>(result.Outbound[2]);
        Assert.Equal(InitialTxId, current.FundingTxId);
        Assert.Equal(TxIdOf(0x22), splice.FundingTxId);
        Assert.Equal(current.RemoteCommitmentNumber, splice.RemoteCommitmentNumber);
        Assert.Equal([InitialTxId, TxIdOf(0x22)], signer.Calls.Select(c => c.FundingTxId!.Value));
        Assert.Equal(1_000_000 * Sat, signer.Calls[0].Spec.TotalMsat);
        Assert.Equal(1_500_000 * Sat, signer.Calls[1].Spec.TotalMsat);
        Assert.Equal(signer.Calls[0].Spec.Htlcs, signer.Calls[1].Spec.Htlcs);
        Assert.Equal(splice.Signatures, result.Next.RemoteNextCommit!.SignaturesFor(TxIdOf(0x22)));
    }

    [Fact]
    public void Given_APendingSplice_When_PaymentsFlowBothWays_Then_EveryCommitmentIsBatchedAndConserved()
    {
        // Arrange
        var pair = SplicedIn();

        // Act
        var id = pair.AliceAdd(50_000 * Sat);
        var outbound = pair.Commit(fromAlice: true);
        pair.Converge();
        pair.BobFulfill(id);
        pair.Converge();
        pair.BobAdd(20_000 * Sat);
        pair.Converge();

        // Assert
        Assert.IsType<OutboundStartBatch>(outbound[0]);
        Assert.Equal(550_000 * Sat, pair.Alice.LocalBalanceMsat);
        Assert.Equal(450_000 * Sat, pair.Bob.LocalBalanceMsat);
        Assert.Equal(pair.Alice.LocalCommit.Number, pair.Bob.RemoteCommit.Number);
        Assert.Equal(1_500_000 * Sat,
                     ChannelCommitments.SpecFor(pair.Alice.LocalCommit.Spec, pair.Alice.PendingFundings[0]).TotalMsat);
    }

    #endregion

    #region SP-OP-05/06/07: batched receive

    [Fact]
    public void Given_APendingSplice_When_ALoneCommitmentSignedArrives_Then_ChannelFails()
    {
        // Arrange
        var pair = SplicedIn();
        pair.AliceAdd(10_000 * Sat);
        var sent = pair.Alice.SendCommit(new BindingCommitmentSigner(546, false));

        // Act
        var e = Assert.Throws<CommitmentViolationException>(
            () => pair.Bob.ReceiveCommit(ToBatch(sent.Outbound)[0].Signatures, new BindingCommitmentVerifier()));

        // Assert
        Assert.Equal("SP-OP-05", e.RequirementId);
        Assert.True(e.MustFailChannel);
    }

    [Fact]
    public void Given_ABatchMemberWithoutFundingTxId_When_Received_Then_ChannelFails()
    {
        // Arrange
        var pair = SplicedIn();
        pair.AliceAdd(10_000 * Sat);
        var batch = ToBatch(pair.Alice.SendCommit(new BindingCommitmentSigner(546, false)).Outbound).ToList();
        batch[1] = batch[1] with { FundingTxId = null };

        // Act
        var e = Assert.Throws<CommitmentViolationException>(
            () => pair.Bob.ReceiveCommitBatch(batch, new BindingCommitmentVerifier()));

        // Assert
        Assert.Equal("SP-OP-05", e.RequirementId);
        Assert.True(e.MustFailChannel);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("missing-current")]
    public void Given_APendingSplice_When_TheBatchDoesNotMatchTheFundings_Then_ChannelFails(string defect)
    {
        // Arrange
        var pair = SplicedIn();
        pair.AliceAdd(10_000 * Sat);
        var batch = ToBatch(pair.Alice.SendCommit(new BindingCommitmentSigner(546, false)).Outbound).ToList();
        switch (defect)
        {
            case "missing":
                batch.RemoveAt(1);
                break;
            case "duplicate":
                batch.Add(batch[1]);
                break;
            default:
                batch.RemoveAt(0);
                break;
        }

        // Act
        var e = Assert.Throws<CommitmentViolationException>(
            () => pair.Bob.ReceiveCommitBatch(batch, new BindingCommitmentVerifier()));

        // Assert
        Assert.Equal("SP-OP-05", e.RequirementId);
        Assert.True(e.MustFailChannel);
    }

    [Fact]
    public void Given_APendingSplice_When_TheBatchAlsoHoldsAnObsoleteMember_Then_ItIsIgnoredAndOneRevokeAndAck()
    {
        // Arrange: a member for a funding that is no longer active (a discarded sibling), with an invalid signature
        var pair = SplicedIn();
        pair.AliceAdd(10_000 * Sat);
        var batch = ToBatch(pair.Alice.SendCommit(new BindingCommitmentSigner(546, false)).Outbound).ToList();
        batch.Insert(1, batch[1] with
        {
            FundingTxId = TxIdOf(0x99),
            Signatures = batch[1].Signatures with { Signature = CommitmentsTestKit.Signature(7) }
        });
        var verifier = new BindingCommitmentVerifier();

        // Act
        var result = pair.Bob.ReceiveCommitBatch(batch, verifier);

        // Assert: SP-OP-06 the obsolete member is neither verified nor stored, SP-OP-07 one revoke_and_ack
        Assert.IsType<OutboundRevokeAndAck>(Assert.Single(result.Outbound));
        Assert.Equal(2, verifier.Calls.Count);
        Assert.Equal(TxIdOf(0x22), Assert.Single(result.Next.LocalCommit.PendingFundingSignatures).FundingTxId);
    }

    [Fact]
    public void Given_ABatchWithAnInvalidSpliceSignature_When_Received_Then_NothingIsAcceptedOrRevoked()
    {
        // Arrange: the current funding's signature is valid, the splice's is not (all-or-nothing, risk 1)
        var pair = SplicedIn();
        pair.AliceAdd(10_000 * Sat);
        var batch = ToBatch(pair.Alice.SendCommit(new BindingCommitmentSigner(546, false)).Outbound).ToList();
        batch[1] = batch[1] with { Signatures = batch[1].Signatures with { Signature = CommitmentsTestKit.Signature(7) } };
        var before = pair.Bob;

        // Act
        var e = Assert.Throws<CommitmentViolationException>(
            () => pair.Bob.ReceiveCommitBatch(batch, new BindingCommitmentVerifier()));

        // Assert
        Assert.Equal("B2-CS-R01", e.RequirementId);
        Assert.Contains("splice funding", e.Message);
        Assert.Same(before, pair.Bob);
    }

    [Fact]
    public void Given_ABatchWithTheCurrentSignatureSwappedWithTheSplices_When_Received_Then_Rejected()
    {
        // Arrange: each signature is checked against its own funding
        var pair = SplicedIn();
        pair.AliceAdd(10_000 * Sat);
        var batch = ToBatch(pair.Alice.SendCommit(new BindingCommitmentSigner(546, false)).Outbound).ToList();
        var swapped = new List<ReceivedCommitmentSigned>
        {
            batch[0] with { Signatures = batch[1].Signatures },
            batch[1] with { Signatures = batch[0].Signatures }
        };

        // Act / Assert
        Assert.Throws<CommitmentViolationException>(
            () => pair.Bob.ReceiveCommitBatch(swapped, new BindingCommitmentVerifier()));
    }

    [Fact]
    public void Given_AValidBatch_When_Received_Then_EveryFundingVerifiedAtTheSameNumberAndOneRevokeAndAck()
    {
        // Arrange
        var pair = SplicedIn();
        pair.AliceAdd(10_000 * Sat);
        var batch = ToBatch(pair.Alice.SendCommit(new BindingCommitmentSigner(546, false)).Outbound);
        var verifier = new BindingCommitmentVerifier();

        // Act: members in reverse arrival order are matched by funding_txid
        var result = pair.Bob.ReceiveCommitBatch(batch.Reverse().ToList(), verifier);

        // Assert: SP-OP-07 one revoke_and_ack for the whole batch
        var raa = Assert.IsType<OutboundRevokeAndAck>(Assert.Single(result.Outbound));
        Assert.Equal(pair.Bob.LocalCommit.Number, raa.RevokedCommitmentNumber);
        Assert.Equal(2, verifier.Calls.Count);
        Assert.All(verifier.Calls, c => Assert.Equal(pair.Bob.LocalCommit.Number + 1, c.Number));
        Assert.Equal(TxIdOf(0x22), Assert.Single(result.Next.LocalCommit.PendingFundingSignatures).FundingTxId);
        Assert.Equal(batch[1].Signatures, result.Next.LocalCommit.SignaturesFor(TxIdOf(0x22)));
    }

    [Fact]
    public void Given_APendingSplice_When_TheRevokeAndAckArrives_Then_TheRevokedCommitmentIsListedForEveryFunding()
    {
        // Arrange
        var pair = SplicedIn();
        pair.AliceAdd(10_000 * Sat);
        var sent = pair.Alice.SendCommit(new BindingCommitmentSigner(546, false));
        var received = pair.Bob.ReceiveCommitBatch(ToBatch(sent.Outbound), new BindingCommitmentVerifier());
        var raa = Assert.IsType<OutboundRevokeAndAck>(Assert.Single(received.Outbound));

        // Act
        var revoked = sent.Next.ReceiveRevoke(CommitmentsTestKit.SecretFor(CommitmentsTestKit.BobTag, raa.RevokedCommitmentNumber),
                                              CommitmentsTestKit.Point(CommitmentsTestKit.BobTag, raa.NextCommitmentNumber),
                                              new FakeRevocationVerifier());

        // Assert: one secret revokes the number on every funding (SP-I3, SP-I5)
        Assert.Equal(TxIdOf(0x22), Assert.Single(revoked.Transition.RevokedRemoteCommitFundings!).FundingTxId);
        Assert.Empty(revoked.Next.RemoteNextCommit?.PendingFundingSignatures ?? []);
    }

    [Fact]
    public void Given_ASiblingDiscardedBeforeTheRevoke_When_TheRevokeAndAckArrives_Then_ItIsStillListed()
    {
        // Arrange: regression, the revoked commitment was signed on an RBF attempt discarded before the RAA (SP-I5)
        var pair = SplicedIn();
        pair.Splice(Splice(0x23, 1_500_000, 500_000, 0, kind: ChannelFundingKind.SpliceRbf, rbfOf: TxIdOf(0x22)));
        pair.AliceAdd(10_000 * Sat);
        var sent = pair.Alice.SendCommit(new BindingCommitmentSigner(546, false));
        var received = pair.Bob.ReceiveCommitBatch(ToBatch(sent.Outbound), new BindingCommitmentVerifier());
        var raa = Assert.IsType<OutboundRevokeAndAck>(Assert.Single(received.Outbound));
        var discarded = sent.Next.DiscardPendingFundings(TxIdOf(0x23)).Next;

        // Act
        var revoked = discarded.ReceiveRevoke(CommitmentsTestKit.SecretFor(CommitmentsTestKit.BobTag, raa.RevokedCommitmentNumber),
                                              CommitmentsTestKit.Point(CommitmentsTestKit.BobTag, raa.NextCommitmentNumber),
                                              new FakeRevocationVerifier());

        // Assert: both attempts keep the revoked number in the log, and the new remote commitment remembers both
        Assert.Equal([TxIdOf(0x22), TxIdOf(0x23)],
                     revoked.Transition.RevokedRemoteCommitFundings!.Select(f => f.FundingTxId));
        Assert.Equal([InitialTxId, TxIdOf(0x22), TxIdOf(0x23)],
                     revoked.Next.RemoteCommit.SignedOnFundings!.Select(f => f.FundingTxId));
        Assert.Equal([TxIdOf(0x22)], revoked.Next.PendingFundings.Select(f => f.FundingTxId));
    }

    [Fact]
    public void Given_ALockBeforeTheRevoke_When_TheRevokeAndAckArrives_Then_TheReplacedFundingIsListedWithItsSpec()
    {
        // Arrange: the splice locks between our batch and the peer's revoke_and_ack
        var pair = SplicedIn();
        pair.AliceAdd(10_000 * Sat);
        var sent = pair.Alice.SendCommit(new BindingCommitmentSigner(546, false));
        var received = pair.Bob.ReceiveCommitBatch(ToBatch(sent.Outbound), new BindingCommitmentVerifier());
        var raa = Assert.IsType<OutboundRevokeAndAck>(Assert.Single(received.Outbound));
        var locked = sent.Next.LockFunding(TxIdOf(0x22)).Next;

        // Act
        var revoked = locked.ReceiveRevoke(CommitmentsTestKit.SecretFor(CommitmentsTestKit.BobTag, raa.RevokedCommitmentNumber),
                                           CommitmentsTestKit.Point(CommitmentsTestKit.BobTag, raa.NextCommitmentNumber),
                                           new FakeRevocationVerifier());

        // Assert: the replaced initial funding, with deltas rebased so SpecFor still gives its 1,000,000 sat commitment
        var replaced = Assert.Single(revoked.Transition.RevokedRemoteCommitFundings!);
        Assert.Equal(InitialTxId, replaced.FundingTxId);
        var spec = ChannelCommitments.SpecFor(revoked.Transition.RevokedRemoteCommit!.Spec, replaced);
        Assert.Equal(1_000_000 * Sat, spec.TotalMsat);
        Assert.Equal(1_500_000 * Sat, revoked.Transition.RevokedRemoteCommit.Spec.TotalMsat);
    }

    [Fact]
    public void Given_ALockedSplice_When_SendCommit_Then_TheSingleCommitmentSignedNamesTheSpliceFunding()
    {
        // Arrange: regression, the funding_txid came from the channel's funding output, which the lock does not move
        var pair = SplicedIn();
        pair.Alice = pair.Alice.LockFunding(TxIdOf(0x22)).Next;
        pair.Bob = pair.Bob.LockFunding(TxIdOf(0x22)).Next;
        pair.AliceAdd(10_000 * Sat);

        // Act
        var result = pair.Alice.SendCommit(new BindingCommitmentSigner(546, false));

        // Assert
        var cs = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(result.Outbound));
        Assert.Equal(TxIdOf(0x22), cs.FundingTxId);
    }

    [Fact]
    public void Given_NoPendingSplice_When_ABatchWithObsoleteMembersArrives_Then_TheyAreIgnored()
    {
        // Arrange: SP-OP-06, the peer signed a funding we no longer have (sent before our splice_locked arrived)
        var pair = new SplicePair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        var current = Assert.Single(ToBatch(pair.Alice.SendCommit(new BindingCommitmentSigner(546, false)).Outbound));
        var batch = new List<ReceivedCommitmentSigned>
        {
            current with { FundingTxId = InitialTxId },
            new(TxIdOf(0x33), new CommitmentSignatures(CommitmentsTestKit.Signature(5), []))
        };
        var verifier = new BindingCommitmentVerifier();

        // Act
        var result = pair.Bob.ReceiveCommitBatch(batch, verifier);

        // Assert
        Assert.IsType<OutboundRevokeAndAck>(Assert.Single(result.Outbound));
        Assert.Equal(InitialTxId, Assert.Single(verifier.Calls).FundingTxId);
    }

    [Fact]
    public void Given_NoPendingSplice_When_ABatchLacksTheCurrentFunding_Then_ChannelFails()
    {
        // Arrange
        var pair = new SplicePair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        var batch = new List<ReceivedCommitmentSigned>
        {
            new(TxIdOf(0x33), new CommitmentSignatures(CommitmentsTestKit.Signature(5), [])),
            new(TxIdOf(0x44), new CommitmentSignatures(CommitmentsTestKit.Signature(6), []))
        };

        // Act
        var e = Assert.Throws<CommitmentViolationException>(
            () => pair.Bob.ReceiveCommitBatch(batch, new BindingCommitmentVerifier()));

        // Assert
        Assert.Equal("SP-OP-06", e.RequirementId);
        Assert.True(e.MustFailChannel);
    }

    #endregion

    #region SP-OP-01 / SP-I6 / D9: valid for every funding

    [Fact]
    public void Given_ASpliceOut_When_AnAddFitsTheCurrentFundingButNotTheSplice_Then_RefusedBySender()
    {
        // Arrange: Bob splices out 350,000 of his 400,000 sat; Bob's HTLC of 60,000 sat fits the current funding
        // but leaves him below the reserve on the splice
        var pair = new SplicePair(600_000, 400_000);
        pair.Splice(Splice(0x22, 650_000, 0, -350_000));

        // Act
        var e = Assert.Throws<CommitmentRefusedException>(() => pair.Bob.Add(60_000 * Sat));

        // Assert
        Assert.Equal("B2-ADD-S01", e.RequirementId);
        Assert.Contains("splice funding", e.Message);
        var fits = new SplicePair(600_000, 400_000);
        fits.Bob.Add(60_000 * Sat);
    }

    [Fact]
    public void Given_ASpliceOut_When_ThePeerAddsWhatOnlyTheCurrentFundingAllows_Then_Violation()
    {
        // Arrange: Alice's view of Bob's splice-out; Bob offers 60,000 sat anyway
        var pair = new SplicePair(600_000, 400_000);
        pair.Splice(Splice(0x22, 650_000, 0, -350_000));

        // Act
        var e = Assert.Throws<CommitmentViolationException>(
            () => pair.Alice.ReceiveAdd(0, 60_000 * Sat, CommitmentsTestKit.PaymentHash(2), 600,
                                        CommitmentsTestKit.Onion));

        // Assert
        Assert.Equal("B2-ADD-R02", e.RequirementId);
        Assert.Contains("splice funding", e.Message);
    }

    [Fact]
    public void Given_ALargeSpliceIn_When_TheReserveIsComputed_Then_OnePercentOfTheNewCapacity()
    {
        // Arrange: D9, reserve on the splice = max(10,000 sat announced, 1 % of 10,400,000 sat) = 104,000 sat;
        // Bob (non-funder) keeps 110,000 sat after a 290,000 sat HTLC, above the announced reserve only
        var pair = new SplicePair(600_000, 400_000);
        pair.Splice(Splice(0x22, 10_400_000, 9_400_000, 0));
        var parameters = pair.Bob.Params;

        // Act
        var onSplice = parameters.LocalReserveMsatOn(pair.Bob.PendingFundings[0]);
        var onCurrent = parameters.LocalReserveMsatOn(pair.Bob.Params.Funding);

        // Assert
        Assert.Equal(104_000 * Sat, onSplice);
        Assert.Equal(10_000 * Sat, onCurrent);
        Assert.Throws<CommitmentRefusedException>(() => pair.Bob.Add(300_000 * Sat));
        pair.Bob.Add(290_000 * Sat);
    }

    [Fact]
    public void Given_ASpliceOutBelowTheAnnouncedReserve_When_ThePeerKeepsOnePercent_Then_Accepted()
    {
        // Arrange: regression (D9, Q3), the capacity goes from 1,000,000 to 500,000 sat with a 10,000 sat announced
        // reserve; Eclair lets the peer go down to 1 % (5,000 sat) there, so we must not fail it for keeping 7,000 sat
        var pair = new SplicePair(600_000, 400_000);
        pair.Splice(Splice(0x22, 500_000, -300_000, -200_000));
        var splice = pair.Alice.PendingFundings[0];

        // Act
        var accepted = pair.Alice.ReceiveAdd(0, 193_000 * Sat, CommitmentsTestKit.PaymentHash(2), 600,
                                             CommitmentsTestKit.Onion);

        // Assert: the receive reserve is the smaller reading, our own sends keep the larger one
        Assert.Single(accepted.Next.Htlcs);
        Assert.Equal(5_000 * Sat, pair.Alice.Params.RemoteReceiveReserveMsatOn(splice));
        Assert.Equal(10_000 * Sat, pair.Alice.Params.RemoteReserveMsatOn(splice));
        Assert.Equal(10_000 * Sat, pair.Alice.Params.RemoteReceiveReserveMsatOn(pair.Alice.Params.Funding));
        Assert.Equal("B2-ADD-S01",
                     Assert.Throws<CommitmentRefusedException>(() => pair.Bob.Add(193_000 * Sat)).RequirementId);
        var e = Assert.Throws<CommitmentViolationException>(
            () => pair.Alice.ReceiveAdd(0, 196_000 * Sat, CommitmentsTestKit.PaymentHash(2), 600,
                                        CommitmentsTestKit.Onion));
        Assert.Equal("B2-ADD-R02", e.RequirementId);
    }

    [Fact]
    public void Given_ASpliceOut_When_TheFunderRaisesTheFeeBeyondTheSplice_Then_Refused()
    {
        // Arrange: Alice splices out almost everything above her reserve
        var pair = new SplicePair(600_000, 400_000);
        pair.Splice(Splice(0x22, 415_000, -585_000, 0));

        // Act
        var e = Assert.Throws<CommitmentRefusedException>(() => pair.Alice.SendFee(20_000));

        // Assert
        Assert.Equal("B2-FEE-R03", e.RequirementId);
        Assert.Contains("splice funding", e.Message);
        new SplicePair(600_000, 400_000).Alice.SendFee(20_000);
    }

    #endregion

    #region Lock and discard

    [Fact]
    public void Given_APendingSplice_When_Locked_Then_ItBecomesCurrentWithItsDeltasFolded()
    {
        // Arrange: an HTLC in flight, and a second (RBF) attempt that loses
        var pair = SplicedIn();
        var rbf = Splice(0x23, 1_500_000, 500_000, 0, kind: ChannelFundingKind.SpliceRbf, rbfOf: TxIdOf(0x22));
        pair.Splice(rbf);
        var id = pair.AliceAdd(10_000 * Sat);
        pair.Commit(fromAlice: true);
        var localBefore = pair.Alice.LocalBalanceMsat;
        var spliceSignatures = pair.Alice.LocalCommit.SignaturesFor(TxIdOf(0x22));

        // Act
        var result = pair.Alice.LockFunding(TxIdOf(0x22));
        pair.Alice = result.Next;
        pair.Bob = pair.Bob.LockFunding(TxIdOf(0x22)).Next;

        // Assert
        var next = result.Next;
        Assert.Equal(TxIdOf(0x22), next.Params.Funding!.FundingTxId);
        Assert.Equal(ChannelFundingStatus.Current, next.Params.Funding.Status);
        Assert.Equal(0, next.Params.Funding.LocalBalanceDeltaMsat);
        Assert.Equal(1_500_000UL, next.Params.FundingSatoshis);
        Assert.Empty(next.PendingFundings);
        Assert.Equal(localBefore + 500_000 * Sat, next.LocalBalanceMsat);
        Assert.Equal(spliceSignatures, next.LocalCommit.RemoteSignatures);
        Assert.Empty(next.LocalCommit.PendingFundingSignatures);
        Assert.True(result.Transition.FundingsChanged);
        Assert.Collection(result.Transition.RetiredFundings!,
                          r =>
                          {
                              Assert.Equal(InitialTxId, r.FundingTxId);
                              Assert.Equal(ChannelFundingStatus.Replaced, r.Status);
                          },
                          r =>
                          {
                              Assert.Equal(TxIdOf(0x23), r.FundingTxId);
                              Assert.Equal(ChannelFundingStatus.Discarded, r.Status);
                          });
        pair.AssertConserved();

        // After the lock the channel signs single commitments again, on the new funding
        pair.Converge();
        pair.BobFulfill(id);
        var outbound = pair.Commit(fromAlice: false);
        Assert.IsType<OutboundCommitmentSigned>(Assert.Single(outbound));
        pair.Converge();
        pair.AssertConserved();
    }

    [Fact]
    public void Given_AnUnknownFunding_When_Locked_Then_ArgumentException()
    {
        // Arrange
        var pair = SplicedIn();

        // Act / Assert
        Assert.Throws<ArgumentException>(() => pair.Alice.LockFunding(TxIdOf(0x77)));
    }

    [Fact]
    public void Given_PendingSplices_When_Discarded_Then_OnlyTheCurrentFundingIsSigned()
    {
        // Arrange
        var pair = SplicedIn();
        pair.AliceAdd(10_000 * Sat);

        // Act
        var result = pair.Alice.DiscardPendingFundings();
        pair.Alice = result.Next;
        pair.Bob = pair.Bob.DiscardPendingFundings(TxIdOf(0x22)).Next;

        // Assert
        Assert.Empty(pair.Alice.PendingFundings);
        Assert.Empty(pair.Alice.LocalCommit.PendingFundingSignatures);
        Assert.Equal(ChannelFundingStatus.Discarded, Assert.Single(result.Transition.RetiredFundings!).Status);
        var outbound = pair.Commit(fromAlice: true);
        Assert.IsType<OutboundCommitmentSigned>(Assert.Single(outbound));
    }

    #endregion

    #region Restore

    [Fact]
    public void Given_ASnapshotWithAPendingSplice_When_Restored_Then_Equal()
    {
        // Arrange
        var pair = SplicedIn();
        pair.AliceAdd(10_000 * Sat);
        pair.Commit(fromAlice: true);
        var alice = pair.Alice;

        // Act
        var restored = ChannelCommitments.Restore(alice.ChannelId, alice.Params, alice.LocalBalanceMsat,
                                                  alice.RemoteBalanceMsat, alice.Htlcs.Values, alice.FeeUpdates,
                                                  alice.LocalNextHtlcId, alice.RemoteNextHtlcId, alice.LocalCommit,
                                                  alice.RemoteCommit, alice.RemoteNextCommit,
                                                  alice.RemoteNextPerCommitmentPoint, alice.PendingFundings);

        // Assert
        Assert.Equal(alice.PendingFundings, restored.PendingFundings);
        Assert.Equal(alice.LocalCommit, restored.LocalCommit);
    }

    [Fact]
    public void Given_APendingSpliceWithoutItsSignatures_When_Restored_Then_ArgumentException()
    {
        // Arrange: SP-I2, every active funding must stay broadcastable
        var pair = SplicedIn();
        var alice = pair.Alice;
        var withoutSignatures = alice.LocalCommit with { PendingFundingSignatures = [] };

        // Act / Assert
        Assert.Throws<ArgumentException>(
            () => ChannelCommitments.Restore(alice.ChannelId, alice.Params, alice.LocalBalanceMsat,
                                             alice.RemoteBalanceMsat, alice.Htlcs.Values, alice.FeeUpdates,
                                             alice.LocalNextHtlcId, alice.RemoteNextHtlcId, withoutSignatures,
                                             alice.RemoteCommit, alice.RemoteNextCommit,
                                             alice.RemoteNextPerCommitmentPoint, alice.PendingFundings));
    }

    #endregion
}