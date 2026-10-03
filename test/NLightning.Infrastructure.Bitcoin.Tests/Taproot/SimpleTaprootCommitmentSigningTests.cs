using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;

/// <summary>
/// The MuSig2 commitment signatures of simple taproot channels through <c>LocalLightningSigner</c> (plan T3): Alice
/// signs Bob's commitment with a just-in-time nonce against Bob's verification nonce, Bob checks it and can broadcast
/// (the aggregated key-path witness passes script execution), with the broadcast guards (I4, I12, S1) and the
/// refusal of every ECDSA path for a taproot channel.
/// </summary>
public class SimpleTaprootCommitmentSigningTests
{
    private const ulong Number = 5;

    [Fact]
    public void Given_BobsVerificationNonce_When_AliceSignsAndBobValidates_Then_BobCanBroadcast()
    {
        // Arrange
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var commitment = kit.UnsignedSpend();
        var bobNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, kit.FundingTxId, Number);

        // Act
        var aliceSignature = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, commitment,
                                                                   bobNonce);
        kit.Bob.ValidateLocalCommitmentPartialSignature(TaprootSignerKit.ChannelId, null, Number, aliceSignature,
                                                        commitment);
        var signed = kit.Bob.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, null, Number, commitment,
                                                             aliceSignature);

        // Assert: a one-element key-path witness (64 bytes, SIGHASH_DEFAULT) that spends the funding output
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        Assert.Equal(commitment.TxId, signed.TxId);
        Assert.Equal(1, tx.Inputs[0].WitScript.PushCount);
        Assert.Equal(64, tx.Inputs[0].WitScript[0].Length);
        Assert.Null(TaprootSignerKit.Execute(signed, kit.FundingTxOut));
        Assert.True(kit.Bob.TryGetBroadcastSignedCommitment(TaprootSignerKit.ChannelId, out var marked));
        Assert.Equal(Number, marked);
    }

    [Fact]
    public void Given_TheSameCommitment_When_SignedTwice_Then_TheJitNoncesDifferAndBothVerify()
    {
        // Arrange
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var commitment = kit.UnsignedSpend();
        var bobNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, Number);

        // Act: a retransmitted commitment_signed is signed again, never replayed
        var first = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, commitment, bobNonce);
        var second = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, commitment, bobNonce);

        // Assert
        Assert.NotEqual(first.PublicNonce, second.PublicNonce);
        Assert.NotEqual(first.PartialSignature, second.PartialSignature);
        kit.Bob.ValidateLocalCommitmentPartialSignature(TaprootSignerKit.ChannelId, null, Number, first, commitment);
        kit.Bob.ValidateLocalCommitmentPartialSignature(TaprootSignerKit.ChannelId, null, Number, second, commitment);
    }

    [Fact]
    public void Given_ASignatureMadeForAnotherNumber_When_BobValidates_Then_ItIsRejected()
    {
        // Arrange: Alice used Bob's nonce of commitment 6, Bob checks commitment 5
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var commitment = kit.UnsignedSpend();
        var wrongNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, Number + 1);
        var signature = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, commitment,
                                                              wrongNonce);

        // Act / Assert
        Assert.Throws<SignerException>(() => kit.Bob.ValidateLocalCommitmentPartialSignature(
                                           TaprootSignerKit.ChannelId, null, Number, signature, commitment));
        Assert.Throws<SignerException>(() => kit.Bob.SignLocalCommitmentForBroadcast(
                                           TaprootSignerKit.ChannelId, null, Number, commitment, signature));
    }

    [Fact]
    public void Given_ATamperedSignatureOrAnotherTransaction_When_BobValidates_Then_ItIsRejected()
    {
        // Arrange
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var commitment = kit.UnsignedSpend();
        var bobNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, Number);
        var signature = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, commitment, bobNonce);
        var tampered = ((byte[])signature.PartialSignature).ToArray();
        tampered[31] ^= 0x01;
        var otherNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, Number + 2);

        // Act / Assert: another s, another nonce in the TLV, another transaction
        Assert.Throws<SignerException>(() => kit.Bob.ValidateLocalCommitmentPartialSignature(
                                           TaprootSignerKit.ChannelId, null, Number,
                                           new MusigPartialSignatureWithNonce(tampered, signature.PublicNonce),
                                           commitment));
        Assert.Throws<SignerException>(() => kit.Bob.ValidateLocalCommitmentPartialSignature(
                                           TaprootSignerKit.ChannelId, null, Number,
                                           new MusigPartialSignatureWithNonce(signature.PartialSignature,
                                                                              otherNonce), commitment));
        Assert.Throws<SignerException>(() => kit.Bob.ValidateLocalCommitmentPartialSignature(
                                           TaprootSignerKit.ChannelId, null, Number, signature,
                                           kit.UnsignedSpend(outputSat: 980_000)));
    }

    [Fact]
    public void Given_AnUndecodableNonce_When_AliceSigns_Then_ItThrowsASignerException()
    {
        // Arrange: 66 bytes that are not two points
        var kit = new TaprootSignerKit();
        MusigPublicNonce garbage = Enumerable.Repeat((byte)0x05, 66).ToArray();

        // Act / Assert
        Assert.Throws<SignerException>(() => kit.Alice.SignRemoteCommitmentPartial(
                                           TaprootSignerKit.ChannelId, null, kit.UnsignedSpend(), garbage));
    }

    [Fact]
    public void Given_AWrongFundingOutpoint_When_AliceSigns_Then_ItIsRefused()
    {
        // Arrange
        var kit = new TaprootSignerKit();
        var bobNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, 1);
        TxId other = Enumerable.Repeat((byte)0x44, 32).ToArray();

        // Act / Assert
        Assert.Throws<SignerException>(() => kit.Alice.SignRemoteCommitmentPartial(
                                           TaprootSignerKit.ChannelId, null, kit.UnsignedSpend(fundingTxId: other),
                                           bobNonce));
    }

    [Fact]
    public void Given_APendingSpliceFunding_When_SigningOnIt_Then_BobBroadcastsOnThatFunding()
    {
        // Arrange
        var kit = new TaprootSignerKit(aliceLocalNumber: Number, bobLocalNumber: Number);
        var (spliceTxId, spliceTx) = kit.RegisterPendingFunding();
        var commitment = kit.UnsignedSpend(fundingTxId: spliceTxId);
        var bobNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, spliceTxId, Number);

        // Act
        var signature = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, spliceTxId, commitment,
                                                              bobNonce);
        kit.Bob.ValidateLocalCommitmentPartialSignature(TaprootSignerKit.ChannelId, spliceTxId, Number, signature,
                                                        commitment);
        var signed = kit.Bob.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, spliceTxId, Number,
                                                             commitment, signature);

        // Assert
        Assert.Null(TaprootSignerKit.Execute(signed, spliceTx.Outputs[0]));
    }

    [Fact]
    public void Given_ARevokedCommitment_When_BobSignsForBroadcast_Then_ItIsRefused()
    {
        // Arrange (I4): Bob's commitment 6 is persisted, 5 is revoked
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var (commitment, signature) = AliceSignsBobsCommitment(kit);
        kit.Bob.AdvanceLocalCommitment(TaprootSignerKit.ChannelId, Number + 1);

        // Act / Assert
        var exception = Assert.Throws<SignerException>(() => kit.Bob.SignLocalCommitmentForBroadcast(
                                                           TaprootSignerKit.ChannelId, null, Number, commitment,
                                                           signature));
        Assert.Contains("revoked", exception.Message);
    }

    [Fact]
    public void Given_DataLoss_When_SigningAnything_Then_ItIsRefused()
    {
        // Arrange (I12)
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var (commitment, signature) = AliceSignsBobsCommitment(kit);
        var aliceNonce = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, 1);

        // Act
        kit.Bob.MarkDataLoss(TaprootSignerKit.ChannelId);

        // Assert
        Assert.Throws<SignerException>(() => kit.Bob.SignLocalCommitmentForBroadcast(
                                           TaprootSignerKit.ChannelId, null, Number, commitment, signature));
        Assert.Throws<SignerException>(() => kit.Bob.SignRemoteCommitmentPartial(
                                           TaprootSignerKit.ChannelId, null, commitment, aliceNonce));
        Assert.Throws<SignerException>(() => kit.Bob.CreateClosingNonce(TaprootSignerKit.ChannelId));
    }

    [Fact]
    public void Given_ACommitmentSignedForBroadcast_When_SigningAnotherOrANewCommitment_Then_ItIsRefused()
    {
        // Arrange (S1)
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var (commitment, signature) = AliceSignsBobsCommitment(kit);
        var first = kit.Bob.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, null, Number, commitment,
                                                            signature);

        // Act: the same commitment again (a rebroadcast) is allowed and identical
        var again = kit.Bob.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, null, Number, commitment,
                                                            signature);

        // Assert: nothing later, no new commitment for the peer, no revealing of the broadcast secret
        Assert.Equal(first.RawTxBytes, again.RawTxBytes);
        var bobNext = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, Number + 1);
        var nextSignature = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, commitment,
                                                                  bobNext);
        Assert.Throws<SignerException>(() => kit.Bob.SignLocalCommitmentForBroadcast(
                                           TaprootSignerKit.ChannelId, null, Number + 1, commitment, nextSignature));
        var aliceNonce = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, 1);
        Assert.Throws<SignerException>(() => kit.Bob.SignRemoteCommitmentPartial(
                                           TaprootSignerKit.ChannelId, null, kit.UnsignedSpend(), aliceNonce));
        Assert.Throws<SignerException>(() => kit.Bob.RevealPerCommitmentSecret(TaprootSignerKit.ChannelId, Number));
    }

    [Fact]
    public void Given_ACommitmentSignedForBroadcast_When_TheSameNumberComesWithAnotherPeerNonce_Then_ItIsRefused()
    {
        // Arrange: our verification nonce of commitment 5 signed once; a second session over it would reveal our key
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var (commitment, signature) = AliceSignsBobsCommitment(kit);
        kit.Bob.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, null, Number, commitment, signature);
        var bobNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, Number);
        var resigned = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, commitment, bobNonce);

        // Act / Assert: a valid signature of the same commitment with another JIT nonce
        kit.Bob.ValidateLocalCommitmentPartialSignature(TaprootSignerKit.ChannelId, null, Number, resigned,
                                                        commitment);
        var exception = Assert.Throws<SignerException>(() => kit.Bob.SignLocalCommitmentForBroadcast(
                                                           TaprootSignerKit.ChannelId, null, Number, commitment,
                                                           resigned));
        Assert.Contains("nonce", exception.Message);
    }

    [Fact]
    public void Given_TwoOpenAttempts_When_CommitmentZeroIsBroadcastOnBoth_Then_TheSecondIsRefused()
    {
        // Arrange: commitment 0's verification nonce has no funding txid in its context, so every attempt of a
        // dual-funded open shares it (SP-I4 would allow the same number on another funding)
        var kit = new TaprootSignerKit();
        var (otherTxId, _) = kit.RegisterPendingFunding(keyIndex: 0);
        var bobNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, 0);
        Assert.Equal(bobNonce, kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, otherTxId, 0));
        var first = kit.UnsignedSpend();
        var other = kit.UnsignedSpend(fundingTxId: otherTxId);
        var firstSignature = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, first, bobNonce);
        var otherSignature = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, otherTxId, other,
                                                                   bobNonce);
        kit.Bob.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, null, 0, first, firstSignature);

        // Act / Assert: one nonce never signs two transactions
        Assert.Throws<SignerException>(() => kit.Bob.SignLocalCommitmentForBroadcast(
                                           TaprootSignerKit.ChannelId, otherTxId, 0, other, otherSignature));
    }

    [Fact]
    public void Given_ADualFundedOpensAttempts_When_CommitmentZeroIsBroadcastOnBoth_Then_EachSignsWithItsOwnNonce()
    {
        // Arrange: we broadcast commitment 0 of one attempt, then another attempt confirms instead (NL-528); its
        // commitment 0 must be signable, with another verification nonce than the first (NL-972)
        var kit = new TaprootSignerKit(isDualFunded: true);
        var (otherTxId, otherTx) = kit.RegisterPendingFunding(keyIndex: 0);
        var firstNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, 0);
        var otherNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, otherTxId, 0);
        var first = kit.UnsignedSpend();
        var other = kit.UnsignedSpend(fundingTxId: otherTxId);
        var firstSignature = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, first,
                                                                   firstNonce);
        var otherSignature = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, otherTxId, other,
                                                                   otherNonce);

        // Act
        var firstSigned = kit.Bob.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, null, 0, first,
                                                                  firstSignature);
        var otherSigned = kit.Bob.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, otherTxId, 0, other,
                                                                  otherSignature);

        // Assert
        Assert.NotEqual(firstNonce, otherNonce);
        Assert.Null(TaprootSignerKit.Execute(firstSigned, kit.FundingTxOut));
        Assert.Null(TaprootSignerKit.Execute(otherSigned, otherTx.Outputs[0]));
    }

    [Fact]
    public void Given_ATaprootChannel_When_CallingTheEcdsaPaths_Then_EachThrowsASignerException()
    {
        // Arrange
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var commitment = kit.UnsignedSpend();
        CompactSignature ecdsa = new byte[64];
        var id = TaprootSignerKit.ChannelId;

        // Act / Assert: no P2WSH 2-of-2 signature is ever made or accepted for the MuSig2 funding output
        Assert.Throws<SignerException>(() => kit.Alice.SignChannelTransaction(id, commitment));
        Assert.Throws<SignerException>(() => kit.Alice.SignChannelTransaction(id, kit.FundingTxId, commitment));
        Assert.Throws<SignerException>(() => kit.Alice.ValidateSignature(id, ecdsa, commitment));
        Assert.Throws<SignerException>(() => kit.Alice.ValidateSignature(id, kit.FundingTxId, ecdsa, commitment));
        Assert.Throws<SignerException>(() => kit.Bob.SignLocalCommitmentForBroadcast(id, Number, commitment, ecdsa));
        Assert.Throws<SignerException>(() => kit.Bob.SignLocalCommitmentForBroadcast(id, kit.FundingTxId, Number,
                                                                                      commitment, ecdsa));
        Assert.Throws<SignerException>(() => kit.Alice.SignSpliceSharedInput(id, kit.FundingTxId, commitment, 0));
        Assert.Throws<SignerException>(() => kit.Alice.ValidateSpliceSharedInputSignature(id, commitment, 0, ecdsa));
        Assert.Throws<SignerException>(() => kit.Alice.SignAnchorInput(id, commitment, 0, LightningMoney.Satoshis(330)));
        Assert.False(kit.Bob.TryGetBroadcastSignedCommitment(id, out _));
    }

    [Fact]
    public void Given_ANonTaprootChannel_When_CallingTheMusig2Paths_Then_EachThrowsASignerException()
    {
        // Arrange
        var kit = new TaprootSignerKit(isSimpleTaproot: false);
        var commitment = kit.UnsignedSpend();
        var nonce = kit.Bob.GetLocalVerificationNonce(0u, kit.FundingTxId, 1);
        var id = TaprootSignerKit.ChannelId;

        // Act / Assert
        Assert.Throws<SignerException>(() => kit.Alice.SignRemoteCommitmentPartial(id, null, commitment, nonce));
        Assert.Throws<SignerException>(() => kit.Alice.CreateClosingNonce(id));
        Assert.Throws<SignerException>(() => kit.Alice.SignClosingAsCloser(id, commitment, nonce));
    }

    [Fact]
    public void Given_AnUnregisteredChannel_When_SigningWithMusig2_Then_ItIsRefused()
    {
        // Arrange
        var kit = new TaprootSignerKit(register: false);
        var nonce = kit.Bob.GetLocalVerificationNonce(0u, kit.FundingTxId, 1);

        // Act / Assert
        Assert.Throws<SignerException>(() => kit.Alice.SignRemoteCommitmentPartial(
                                           TaprootSignerKit.ChannelId, null, kit.UnsignedSpend(), nonce));
    }

    [Fact]
    public void Given_ATaprootFundingTransaction_When_TheFunderSignsIt_Then_ThePtrFundingOutputIsAccepted()
    {
        // Arrange: the channel validation of SignFundingTransaction knows the MuSig2 P2TR output (no locked UTXO here,
        // so it stops after the output check, at the first input)
        var kit = new TaprootSignerKit();
        var funding = new SignedTransaction(kit.FundingTxId, kit.FundingTx.ToBytes());

        // Act
        var exception = Assert.Throws<SignerException>(() => kit.Alice.SignFundingTransaction(
                                                           TaprootSignerKit.ChannelId, funding));

        // Assert
        Assert.Contains("No locked UTXO", exception.Message);
    }

    internal static (SignedTransaction Commitment, MusigPartialSignatureWithNonce Signature) AliceSignsBobsCommitment(
        TaprootSignerKit kit)
    {
        var commitment = kit.UnsignedSpend();
        var bobNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, Number);
        return (commitment,
                kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, commitment, bobNonce));
    }
}