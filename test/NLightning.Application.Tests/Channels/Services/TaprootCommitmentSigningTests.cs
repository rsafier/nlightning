namespace NLightning.Application.Tests.Channels.Services;

using Application.Channels.Safety;
using Application.Channels.Services;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Channels.Commitments;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// Simple taproot commitments signed and verified end to end (NL-877 T3): two engines over the production ports and
/// two real <c>LocalLightningSigner</c>s, MuSig2 partial signatures against the peer's counter-based verification
/// nonces (set at the start, consumed by each signature, renewed by each revoke_and_ack), BIP 340 HTLC signatures, and
/// the stored partial signature aggregated into a broadcastable commitment.
/// </summary>
public class TaprootCommitmentSigningTests
{
    private const ulong Sat = 1_000;

    [Fact]
    public void Given_TaprootPair_When_AddCommitRevokeFulfillAndFee_Then_PartialSignaturesVerifyOnTheSameTxIds()
    {
        // Arrange
        using var pair = new RealSigningCommitmentPair(hasAnchors: true, simpleTaproot: true);
        var alice = pair.Alice;
        var bob = pair.Bob;
        var preimage1 = RealSigningCommitmentPair.Preimage(1);
        var preimage2 = RealSigningCommitmentPair.Preimage(2);

        // Act
        var big = pair.Add(alice, 50_000 * Sat, preimage1);
        pair.Settle(alice);
        var back = pair.Add(bob, 30_000 * Sat, preimage2);
        pair.Settle(bob);
        pair.Fulfill(bob, big, preimage1);
        pair.Fail(alice, back);
        pair.Settle(bob);
        pair.UpdateFee(RealSigningCommitmentPair.InitialFeeratePerKw * 2);
        pair.Settle(alice);

        // Assert: every commitment verified with MuSig2 on the txid its signer built
        Assert.True(pair.Commitments.Count >= 6, $"only {pair.Commitments.Count} commitments were signed");
        Assert.All(pair.Commitments, c => Assert.Equal(c.Signed, c.Verified));
        Assert.NotNull(alice.State.LocalCommit.RemoteSignatures!.PartialSignature);
        Assert.NotNull(bob.State.LocalCommit.RemoteSignatures!.PartialSignature);
        Assert.Equal(CommitmentSignatures.ZeroSignature, bob.State.LocalCommit.RemoteSignatures.Signature);
        Assert.Empty(alice.State.Htlcs);
        Assert.Equal(alice.State.LocalBalanceMsat, bob.State.RemoteBalanceMsat);

        // The peer's next nonce arrived with each revoke_and_ack
        Assert.Single(alice.State.RemoteNextNonces);
        Assert.Single(bob.State.RemoteNextNonces);
    }

    [Fact]
    public void Given_StoredPartialSignature_When_LocalCommitmentBuiltForBroadcast_Then_AggregatedKeyPathWitness()
    {
        // Arrange: Bob holds Alice's partial signature of his commitment with an HTLC on it
        using var pair = new RealSigningCommitmentPair(hasAnchors: true, simpleTaproot: true);
        var bob = pair.Bob;
        pair.Add(pair.Alice, 50_000 * Sat, RealSigningCommitmentPair.Preimage(1));
        pair.Settle(pair.Alice);
        bob.Channel.UpdateCommitments(bob.State);
        var builder = new LocalCommitmentBroadcastBuilder(bob.GetRequiredService<ICommitmentTransactionModelFactory>(),
                                                          bob.GetRequiredService<ICommitmentTransactionBuilder>(),
                                                          bob.Signer);

        // Act
        var signed = builder.Build(bob.Channel);

        // Assert: one 64-byte BIP 340 signature (the signer checks the aggregate against the funding output key)
        var tx = NBitcoin.Transaction.Load(signed.Transaction.RawTxBytes, NBitcoin.Network.Main);
        Assert.Equal(bob.State.LocalCommit.Number, signed.CommitmentNumber);
        Assert.Equal(1, signed.HtlcOutputCount);
        var witness = Assert.Single(tx.Inputs).WitScript;
        Assert.Equal(1, witness.PushCount);
        Assert.Equal(64, witness[0].Length);
    }

    [Fact]
    public void Given_TamperedPartialSignature_When_ReceiveCommit_Then_Rejected()
    {
        // Arrange
        using var pair = new RealSigningCommitmentPair(hasAnchors: true, simpleTaproot: true);
        var alice = pair.Alice;
        var bob = pair.Bob;
        pair.Add(alice, 50_000 * Sat, RealSigningCommitmentPair.Preimage(1));
        var sent = alice.State.SendCommit(alice.CommitmentSigner);
        var signatures = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(sent.Outbound)).Signatures;
        var bytes = signatures.PartialSignature!.Value.ToBytes();
        bytes[5] ^= 0x01;
        var tampered = signatures with { PartialSignature = new MusigPartialSignatureWithNonce(bytes) };

        // Act
        var exception = Assert.Throws<CommitmentViolationException>(
            () => bob.State.ReceiveCommit(tampered, bob.CommitmentVerifier));

        // Assert
        Assert.Equal("B2-CS-R01", exception.RequirementId);
        Assert.NotNull(bob.State.ReceiveCommit(signatures, bob.CommitmentVerifier).Next.LocalCommit.RemoteSignatures);
    }

    [Fact]
    public void Given_TaprootChannel_When_SignedWithoutNonceOrVerifiedWithoutPartial_Then_SignerException()
    {
        // Arrange
        using var pair = new RealSigningCommitmentPair(hasAnchors: true, simpleTaproot: true);
        var alice = pair.Alice;
        var spec = CommitmentTxSpec.FromCommitmentSpec(alice.State.BuildSpec(CommitmentSide.Remote));
        var localSpec = CommitmentTxSpec.FromCommitmentSpec(alice.State.BuildSpec(CommitmentSide.Local));

        // Act / Assert
        Assert.Throws<SignerException>(() => alice.SigningService.SignRemoteCommitment(
                                           alice.Channel, null, spec, 1, pair.Bob.Point(1)));
        Assert.Throws<SignerException>(() => alice.SigningService.VerifyLocalCommitment(
                                           alice.Channel, null, localSpec, 1, CommitmentSignatures.ZeroSignature, []));
    }
}