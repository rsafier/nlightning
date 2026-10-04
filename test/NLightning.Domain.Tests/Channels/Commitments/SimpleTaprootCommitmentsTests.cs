namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Splicing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using static CommitmentsTestKit;

/// <summary>
/// The commitment engine on a simple taproot channel (NL-877 T3): the peer's verification nonces per funding
/// (set, consumed by signing, replaced by revoke_and_ack / channel_reestablish), MuSig2 partial signatures carried in
/// the snapshot, and fees with the taproot commitment weight (NL-904 item 1).
/// </summary>
public class SimpleTaprootCommitmentsTests
{
    private static readonly ChannelId s_channelId = CommitmentsTestKit.ChannelId;
    private static readonly ChannelFunding s_funding = SpliceTestKit.Initial(1_000_000);

    private static MusigPublicNonce Nonce(byte n) => new(Enumerable.Repeat(n, 66).ToArray());

    private static MusigPartialSignatureWithNonce Partial(byte n) =>
        new(Enumerable.Repeat(n, 98).ToArray().AsSpan());

    private static CommitmentParams TaprootParams(ulong localSat, ulong remoteSat, bool localIsFunder = true) =>
        Params(localSat, remoteSat, localIsFunder, anchors: true) with
        {
            OptionSimpleTaproot = true,
            Funding = SpliceTestKit.Initial(localSat + remoteSat)
        };

    private static ChannelCommitments CreateTaproot(ulong localSat = 600_000, ulong remoteSat = 400_000,
                                                    uint feeratePerKw = 1_000, bool localIsFunder = true,
                                                    MusigPublicNonce? nonce = null) =>
        ChannelCommitments.Create(s_channelId, TaprootParams(localSat, remoteSat, localIsFunder), localSat * Sat,
                                  remoteSat * Sat, feeratePerKw, Point(BobTag, 0), Point(BobTag, 1),
                                  remoteNextNonce: nonce);

    private static ChannelCommitments CreateAnchors(ulong localSat, ulong remoteSat, uint feeratePerKw,
                                                    bool localIsFunder)
    {
        var parameters = Params(localSat, remoteSat, localIsFunder, anchors: true) with
        {
            Funding = SpliceTestKit.Initial(localSat + remoteSat)
        };
        return ChannelCommitments.Create(s_channelId, parameters, localSat * Sat, remoteSat * Sat, feeratePerKw,
                                         Point(BobTag, 0), Point(BobTag, 1));
    }

    #region Nonces and signing

    [Fact]
    public void Given_TaprootWithoutNonce_When_SendCommit_Then_RefusedAndNothingSigned()
    {
        // Arrange
        var c = CreateTaproot().Add(50_000 * Sat).Next;
        var signer = new TaprootSigner();

        // Act
        var exception = Assert.Throws<CommitmentRefusedException>(() => c.SendCommit(signer));

        // Assert
        Assert.Equal("TAPROOT-NONCE", exception.RequirementId);
        Assert.False(c.CanSendCommit);
        Assert.Empty(signer.Nonces);
    }

    [Fact]
    public void Given_ChannelReadyNonce_When_SendCommit_Then_SignedWithItAndConsumed()
    {
        // Arrange
        var c = CreateTaproot().ReceiveChannelReadyNonce(Nonce(1)).Next.Add(50_000 * Sat).Next;
        var signer = new TaprootSigner();
        Assert.True(c.CanSendCommit);

        // Act
        var result = c.SendCommit(signer);

        // Assert: the nonce signed one session, the partial signature is sent and kept for the retransmission
        Assert.Equal([Nonce(1)], signer.Nonces);
        Assert.Empty(result.Next.RemoteNextNonces);
        Assert.True(result.Transition.ScalarsChanged);
        var outbound = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(result.Outbound));
        Assert.Equal(Partial(1), outbound.Signatures.PartialSignature);
        Assert.Equal(CommitmentSignatures.ZeroSignature, outbound.Signatures.Signature);
        Assert.Equal(Partial(1), result.Next.RemoteNextCommit!.SentSignatures.PartialSignature);
    }

    [Fact]
    public void Given_SignedCommit_When_RevokeAndAckWithNonces_Then_NoncesReplaced()
    {
        // Arrange
        var signed = CreateTaproot(nonce: Nonce(1)).Add(50_000 * Sat).Next.SendCommit(new TaprootSigner()).Next;
        var otherFunding = new TxId(Enumerable.Repeat((byte)0x77, 32).ToArray());

        // Act: an entry for a funding that is not active is dropped
        var result = signed.ReceiveRevoke(SecretFor(BobTag, 0), Point(BobTag, 2), new FakeRevocationVerifier(),
                                          new Dictionary<TxId, MusigPublicNonce>
                                          {
                                              [s_funding.FundingTxId] = Nonce(2),
                                              [otherFunding] = Nonce(9)
                                          });

        // Assert
        var entry = Assert.Single(result.Next.RemoteNextNonces);
        Assert.Equal(s_funding.FundingTxId, entry.Key);
        Assert.Equal(Nonce(2), entry.Value);
        Assert.True(result.Transition.ScalarsChanged);
    }

    [Fact]
    public void Given_SignedCommit_When_RevokeAndAckWithoutNonces_Then_ChannelMustFail()
    {
        // Arrange
        var signed = CreateTaproot(nonce: Nonce(1)).Add(50_000 * Sat).Next.SendCommit(new TaprootSigner()).Next;

        // Act
        var missing = Assert.Throws<CommitmentViolationException>(
            () => signed.ReceiveRevoke(SecretFor(BobTag, 0), Point(BobTag, 2), new FakeRevocationVerifier()));
        var wrongFunding = Assert.Throws<CommitmentViolationException>(
            () => signed.ReceiveRevoke(SecretFor(BobTag, 0), Point(BobTag, 2), new FakeRevocationVerifier(),
                                       new Dictionary<TxId, MusigPublicNonce>
                                       {
                                           [new TxId(Enumerable.Repeat((byte)0x77, 32).ToArray())] = Nonce(2)
                                       }));

        // Assert
        Assert.True(missing.MustFailChannel);
        Assert.True(wrongFunding.MustFailChannel);
        Assert.Equal("TAPROOT-NONCE-R01", wrongFunding.RequirementId);
    }

    [Fact]
    public void Given_ReestablishNonces_When_ResignRemoteNextCommit_Then_SameCommitNewSignatureAndNonceConsumed()
    {
        // Arrange: our commitment_signed went out, then the connection dropped
        var signed = CreateTaproot(nonce: Nonce(1)).Add(50_000 * Sat).Next.SendCommit(new TaprootSigner()).Next;
        var reestablished = signed.ReceiveRemoteNonces(new Dictionary<TxId, MusigPublicNonce>
        {
            [s_funding.FundingTxId] = Nonce(3)
        }).Next;
        var signer = new TaprootSigner();

        // Act
        var result = reestablished.ResignRemoteNextCommit(signer);

        // Assert: never replayed byte for byte, the peer's new nonce used once
        Assert.Equal([Nonce(3)], signer.Nonces);
        Assert.Equal(signed.RemoteNextCommit!.Commit, result.Next.RemoteNextCommit!.Commit);
        Assert.Equal(Partial(3), result.Next.RemoteNextCommit.SentSignatures.PartialSignature);
        Assert.Empty(result.Next.RemoteNextNonces);
        Assert.True(result.Transition.RemoteCommitChanged);
        Assert.Equal(signed.RemoteNextCommit.Commit.Number,
                     Assert.IsType<OutboundCommitmentSigned>(Assert.Single(result.Outbound)).RemoteCommitmentNumber);
    }

    [Fact]
    public void Given_ReestablishWithoutCurrentFunding_When_ReceiveRemoteNonces_Then_ChannelMustFail()
    {
        // Arrange
        var c = CreateTaproot();

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(
            () => c.ReceiveRemoteNonces(new Dictionary<TxId, MusigPublicNonce>()));

        // Assert
        Assert.True(exception.MustFailChannel);
    }

    [Fact]
    public void Given_PeerPartialSignature_When_ReceiveCommit_Then_KeptOnLocalCommit()
    {
        // Arrange
        var c = CreateTaproot();
        var verifier = new FakeCommitmentVerifier();

        // Act
        var result = c.ReceiveCommit(CommitmentSignatures.Taproot(Partial(5), []), verifier);

        // Assert
        Assert.Equal(Partial(5), result.Next.LocalCommit.RemoteSignatures!.PartialSignature);
        Assert.Single(verifier.Calls);
    }

    [Fact]
    public void Given_NoPartialSignature_When_ReceiveCommitOnTaproot_Then_ChannelMustFail()
    {
        // Arrange
        var c = CreateTaproot();

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(
            () => c.ReceiveCommit(new CommitmentSignatures(Signature(1), []), new FakeCommitmentVerifier()));

        // Assert
        Assert.True(exception.MustFailChannel);
        Assert.Equal("TAPROOT-CS-R01", exception.RequirementId);
    }

    [Fact]
    public void Given_NonTaprootChannel_When_NonceGiven_Then_RefusedOrIgnored()
    {
        // Arrange
        var anchors = CreateAnchors(600_000, 400_000, 1_000, true);
        var signed = anchors.Add(50_000 * Sat).Next.SendCommit(new FakeCommitmentSigner(546, true)).Next;

        // Act / Assert
        Assert.Throws<ArgumentException>(() => ChannelCommitments.Create(s_channelId, anchors.Params, 600_000 * Sat,
                                                                         400_000 * Sat, 1_000, Point(BobTag, 0),
                                                                         Point(BobTag, 1),
                                                                         remoteNextNonce: Nonce(1)));
        Assert.Throws<InvalidOperationException>(() => anchors.ReceiveChannelReadyNonce(Nonce(1)));
        var revoked = signed.ReceiveRevoke(SecretFor(BobTag, 0), Point(BobTag, 2), new FakeRevocationVerifier(),
                                           new Dictionary<TxId, MusigPublicNonce>
                                           {
                                               [s_funding.FundingTxId] = Nonce(2)
                                           });
        Assert.Empty(revoked.Next.RemoteNextNonces);
    }

    [Fact]
    public void Given_PartialSignatures_When_Compared_Then_EqualityIncludesThem()
    {
        // Act / Assert
        Assert.Equal(CommitmentSignatures.Taproot(Partial(1), [Signature(2)]),
                     CommitmentSignatures.Taproot(Partial(1), [Signature(2)]));
        Assert.NotEqual(CommitmentSignatures.Taproot(Partial(1), []), CommitmentSignatures.Taproot(Partial(2), []));
        Assert.NotEqual(new CommitmentSignatures(CommitmentSignatures.ZeroSignature, []),
                        CommitmentSignatures.Taproot(Partial(1), []));
    }

    [Fact]
    public void Given_TaprootSplice_When_SignedWithoutTheTxCompleteNonce_Then_RefusedElseSignedWithItAlone()
    {
        // Arrange (NL-965: the splice commitment_signed signs the peer's current commitment on the new funding against
        // its tx_complete commit_nonces, not against RemoteNextNonces, which stay for the next commitment)
        var c = CreateTaproot(nonce: Nonce(1));
        var splice = SpliceTestKit.Splice(0x22, 1_100_000, 100_000, 0);
        var signer = new TaprootSigner();

        // Act
        var refused = Assert.Throws<CommitmentRefusedException>(() => c.SignSpliceCommitment(splice, signer));
        var result = c.SignSpliceCommitment(splice, signer, Nonce(5));

        // Assert
        Assert.Equal("TAPROOT-NONCE", refused.RequirementId);
        Assert.Equal([Nonce(5)], signer.Nonces);
        var outbound = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(result.Outbound));
        Assert.Equal(Partial(5), outbound.Signatures.PartialSignature);
        Assert.Equal(splice.FundingTxId, outbound.FundingTxId);
        Assert.Equal(Nonce(1), Assert.Single(result.Next.RemoteNextNonces).Value);
    }

    [Fact]
    public void Given_TaprootSplice_When_ThePeersCommitmentIsReceived_Then_ItsNextNonceMakesTheFundingSignable()
    {
        // Arrange
        var c = CreateTaproot(nonce: Nonce(1));
        var splice = SpliceTestKit.Splice(0x22, 1_100_000, 100_000, 0);
        var signatures = CommitmentSignatures.Taproot(Partial(7), []);

        // Act
        var missing = Assert.Throws<CommitmentViolationException>(
            () => c.ReceiveSpliceCommitment(splice, signatures, new FakeCommitmentVerifier()));
        var unsigned = Assert.Throws<CommitmentViolationException>(
            () => c.ReceiveSpliceCommitment(splice, new CommitmentSignatures(CommitmentSignatures.ZeroSignature, []),
                                            new FakeCommitmentVerifier(), Nonce(3)));
        var received = c.ReceiveSpliceCommitment(splice, signatures, new FakeCommitmentVerifier(), Nonce(3)).Next;
        var signer = new TaprootSigner();
        var batch = received.Add(10_000 * Sat).Next.SendCommit(signer);

        // Assert: both fundings signed, each against its own nonce
        Assert.Equal("TAPROOT-NONCE-R01", missing.RequirementId);
        Assert.Equal("TAPROOT-CS-R01", unsigned.RequirementId);
        Assert.Equal(Nonce(3), received.RemoteNextNonces[splice.FundingTxId]);
        Assert.Equal(Nonce(1), received.RemoteNextNonces[s_funding.FundingTxId]);
        Assert.True(received.HasRemoteNoncesForActiveFundings);
        Assert.Equal([Nonce(1), Nonce(3)], signer.Nonces);
        Assert.Equal(2, batch.Outbound.OfType<OutboundCommitmentSigned>().Count());
    }

    #endregion

    #region Fees by format (NL-904 item 1)

    [Fact]
    public void Given_TaprootFunderExactlyAffordingUpdateFee_When_ReceiveFee_Then_Accepted()
    {
        // Arrange: the peer funds and holds 2596 sat; feerate 2000 costs 968 * 2 + 660 = 2596 sat on taproot, while
        // the anchors weight would charge 1124 * 2 + 660 = 2908 sat
        var taproot = CreateTaproot(997_404, 2_596, 253, localIsFunder: false);
        var anchors = CreateAnchors(997_404, 2_596, 253, localIsFunder: false);

        // Act
        var accepted = taproot.ReceiveFee(2_000, 253, 100_000);
        var refused = Assert.Throws<CommitmentViolationException>(() => anchors.ReceiveFee(2_000, 253, 100_000));

        // Assert
        Assert.Equal(2_000u, accepted.Next.LatestFeeratePerKw);
        Assert.Equal("B2-FEE-R03", refused.RequirementId);
    }

    [Fact]
    public void Given_TaprootFunderExactlyAffordingAdd_When_ReceiveAdd_Then_Accepted()
    {
        // Arrange: the peer funds with 16800 sat and a 10000 sat reserve; its 5000 sat HTLC costs (968 + 172) + 660 =
        // 1800 sat of fee on taproot (16800 - 5000 - 1800 = 10000), but 1296 + 660 = 1956 sat with the anchors weight
        var taproot = CreateTaproot(983_200, 16_800, localIsFunder: false);
        var anchors = CreateAnchors(983_200, 16_800, 1_000, localIsFunder: false);

        // Act
        var accepted = taproot.ReceiveAdd(0, 5_000 * Sat, PaymentHash(1), 600, Onion);
        var refused = Assert.Throws<CommitmentViolationException>(
            () => anchors.ReceiveAdd(0, 5_000 * Sat, PaymentHash(1), 600, Onion));

        // Assert
        Assert.Single(accepted.Next.Htlcs);
        Assert.Equal("B2-ADD-R02", refused.RequirementId);
    }

    [Fact]
    public void Given_TaprootParams_When_SpecFees_Then_TaprootWeightAndAnchorsUnchanged()
    {
        // Arrange: one untrimmed HTLC at 1000 sat/kw
        var spec = new CommitmentSpec(CommitmentSide.Local, 1_000, 500_000_000, 400_000_000,
                                      [new SpecHtlc(HtlcDirection.Outgoing, 0, 100_000_000, PaymentHash(1), 600)]);

        // Act / Assert
        Assert.Equal(1_140UL, CommitmentFeeCalculator.CommitmentBaseFeeSatoshis(spec, 354, CommitmentFormat.SimpleTaproot));
        Assert.Equal((1_140UL + 660) * 1_000, CommitmentFeeCalculator.FunderCostMsat(spec, 354, CommitmentFormat.SimpleTaproot));
        Assert.Equal(1_296UL, CommitmentFeeCalculator.CommitmentBaseFeeSatoshis(spec, 354, CommitmentFormat.Anchors));
        Assert.Equal(CommitmentFormat.SimpleTaproot, TaprootParams(1, 1).Format);
        Assert.Equal(CommitmentFormat.Anchors, Params(1, 1, anchors: true).Format);
    }

    #endregion

    /// <summary>Signs with the given nonce: the partial signature's bytes are the nonce's first byte.</summary>
    private sealed class TaprootSigner : ICommitmentSigner
    {
        public List<MusigPublicNonce> Nonces { get; } = [];

        public CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ChannelFunding? funding, ulong number,
                                                         CommitmentSpec spec, CompactPubKey remotePerCommitmentPoint,
                                                         MusigPublicNonce? remoteVerificationNonce = null)
        {
            var nonce = remoteVerificationNonce ?? throw new InvalidOperationException("No nonce");
            Nonces.Add(nonce);
            var count = CommitmentFeeCalculator.UntrimmedHtlcCount(spec, 546, CommitmentFormat.SimpleTaproot);
            return CommitmentSignatures.Taproot(Partial(((byte[])nonce)[0]),
                                                Enumerable.Range(0, count).Select(i => Signature((byte)i)).ToList());
        }
    }
}