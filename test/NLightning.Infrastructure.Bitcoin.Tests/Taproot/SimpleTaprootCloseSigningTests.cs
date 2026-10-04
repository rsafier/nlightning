using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;

/// <summary>
/// The <c>option_simple_close</c> MuSig2 signatures of a simple taproot channel (bolt-simple-taproot.md §RBF
/// Cooperative Close): closee nonces sent in <c>shutdown</c> and <c>closing_sig</c>, just-in-time closer nonces in
/// <c>closing_complete</c>, both roles aggregating the key-path signature, and an RBF round on the closee's next nonce.
/// </summary>
public class SimpleTaprootCloseSigningTests
{
    private static readonly Domain.Channels.ValueObjects.ChannelId s_id = TaprootSignerKit.ChannelId;

    [Fact]
    public void Given_ShutdownNonces_When_AliceClosesAndBobAgrees_Then_BothAggregateAValidClosingTransaction()
    {
        // Arrange: shutdown_nonce both ways
        var kit = new TaprootSignerKit();
        var aliceCloseeNonce = kit.Alice.CreateClosingNonce(s_id);
        var bobCloseeNonce = kit.Bob.CreateClosingNonce(s_id);
        var closing = kit.UnsignedSpend(outputSat: 995_000, lockTime: 0);

        // Act: closing_complete (Alice), closing_sig (Bob, with his next closee nonce)
        var closerSignature = kit.Alice.SignClosingAsCloser(s_id, closing, bobCloseeNonce);
        var closeeSignature = kit.Bob.SignClosingAsClosee(s_id, closing, bobCloseeNonce, closerSignature);
        kit.Alice.ValidateClosingPartialSignature(s_id, closing, closeeSignature, bobCloseeNonce,
                                                  closerSignature.PublicNonce);
        var aliceSigned = kit.Alice.AggregateClosingSignature(s_id, closing, closerSignature.PartialSignature,
                                                              closerSignature.PublicNonce, closeeSignature,
                                                              bobCloseeNonce);
        var bobSigned = kit.Bob.AggregateClosingSignature(s_id, closing, closeeSignature, bobCloseeNonce,
                                                          closerSignature.PartialSignature,
                                                          closerSignature.PublicNonce);

        // Assert: one transaction, a 64-byte key-path signature that spends the funding output
        Assert.Equal(aliceSigned.RawTxBytes, bobSigned.RawTxBytes);
        var tx = Transaction.Load(aliceSigned.RawTxBytes, Network.Main);
        Assert.Equal(64, tx.Inputs[0].WitScript[0].Length);
        Assert.Null(TaprootSignerKit.Execute(aliceSigned, kit.FundingTxOut));
        Assert.NotEqual(aliceCloseeNonce, bobCloseeNonce);
    }

    [Fact]
    public void Given_AnRbfRound_When_BobClosesOnAlicesNextCloseeNonce_Then_TheNewTransactionIsValid()
    {
        // Arrange: round 1, Bob closes on Alice's shutdown nonce; Alice answers with her next closee nonce
        var kit = new TaprootSignerKit();
        var aliceShutdownNonce = kit.Alice.CreateClosingNonce(s_id);
        var first = kit.UnsignedSpend(outputSat: 995_000, lockTime: 0);
        var bobCloser1 = kit.Bob.SignClosingAsCloser(s_id, first, aliceShutdownNonce);
        _ = kit.Alice.SignClosingAsClosee(s_id, first, aliceShutdownNonce, bobCloser1);
        var aliceNextNonce = kit.Alice.CreateClosingNonce(s_id);

        // Act: round 2 (a higher fee) on the next closee nonce
        var second = kit.UnsignedSpend(outputSat: 990_000, lockTime: 0);
        var bobCloser2 = kit.Bob.SignClosingAsCloser(s_id, second, aliceNextNonce);
        var aliceClosee2 = kit.Alice.SignClosingAsClosee(s_id, second, aliceNextNonce, bobCloser2);
        var signed = kit.Bob.AggregateClosingSignature(s_id, second, bobCloser2.PartialSignature,
                                                       bobCloser2.PublicNonce, aliceClosee2, aliceNextNonce);

        // Assert
        Assert.NotEqual(bobCloser1.PublicNonce, bobCloser2.PublicNonce);
        Assert.Null(TaprootSignerKit.Execute(signed, kit.FundingTxOut));
    }

    [Fact]
    public void Given_AUsedCloseeNonce_When_SigningAgain_Then_ItIsRefused()
    {
        // Arrange: a closee nonce signs exactly one closing_sig
        var kit = new TaprootSignerKit();
        var bobNonce = kit.Bob.CreateClosingNonce(s_id);
        var first = kit.UnsignedSpend(outputSat: 995_000, lockTime: 0);
        var second = kit.UnsignedSpend(outputSat: 990_000, lockTime: 0);
        kit.Bob.SignClosingAsClosee(s_id, first, bobNonce, kit.Alice.SignClosingAsCloser(s_id, first, bobNonce));
        var closer2 = kit.Alice.SignClosingAsCloser(s_id, second, bobNonce);

        // Act / Assert
        Assert.Throws<SignerException>(() => kit.Bob.SignClosingAsClosee(s_id, second, bobNonce, closer2));
    }

    [Fact]
    public void Given_AnInvalidCloserSignature_When_BobSigns_Then_ItIsRefusedAndTheNonceKept()
    {
        // Arrange: the closer signed another transaction
        var kit = new TaprootSignerKit();
        var bobNonce = kit.Bob.CreateClosingNonce(s_id);
        var closing = kit.UnsignedSpend(outputSat: 995_000, lockTime: 0);
        var wrong = kit.Alice.SignClosingAsCloser(s_id, kit.UnsignedSpend(outputSat: 900_000, lockTime: 0),
                                                  bobNonce);

        // Act
        Assert.Throws<SignerException>(() => kit.Bob.SignClosingAsClosee(s_id, closing, bobNonce, wrong));

        // Assert: the nonce is still live for a valid closing_complete
        var valid = kit.Alice.SignClosingAsCloser(s_id, closing, bobNonce);
        var closee = kit.Bob.SignClosingAsClosee(s_id, closing, bobNonce, valid);
        kit.Alice.ValidateClosingPartialSignature(s_id, closing, closee, bobNonce, valid.PublicNonce);
    }

    [Fact]
    public void Given_ForgottenNonces_When_SigningAsClosee_Then_ItIsRefused()
    {
        // Arrange
        var kit = new TaprootSignerKit();
        var bobNonce = kit.Bob.CreateClosingNonce(s_id);
        var closing = kit.UnsignedSpend(outputSat: 995_000, lockTime: 0);
        var closer = kit.Alice.SignClosingAsCloser(s_id, closing, bobNonce);

        // Act
        kit.Bob.ForgetClosingNonces(s_id);

        // Assert: an unknown nonce (never ours, or dropped) never signs
        Assert.Throws<SignerException>(() => kit.Bob.SignClosingAsClosee(s_id, closing, bobNonce, closer));
        var aliceNonce = kit.Alice.CreateClosingNonce(s_id);
        Assert.Throws<SignerException>(() => kit.Bob.SignClosingAsClosee(s_id, closing, aliceNonce, closer));
    }

    [Fact]
    public void Given_AWrongCloseeSignature_When_TheCloserValidatesOrAggregates_Then_ItIsRejected()
    {
        // Arrange
        var kit = new TaprootSignerKit();
        var bobNonce = kit.Bob.CreateClosingNonce(s_id);
        var closing = kit.UnsignedSpend(outputSat: 995_000, lockTime: 0);
        var closer = kit.Alice.SignClosingAsCloser(s_id, closing, bobNonce);
        var closee = kit.Bob.SignClosingAsClosee(s_id, closing, bobNonce, closer);
        var tampered = ((byte[])closee).ToArray();
        tampered[0] ^= 0x01;

        // Act / Assert
        Assert.Throws<SignerException>(() => kit.Alice.ValidateClosingPartialSignature(
                                           s_id, closing, tampered, bobNonce, closer.PublicNonce));
        Assert.Throws<SignerException>(() => kit.Alice.AggregateClosingSignature(
                                           s_id, closing, closer.PartialSignature, closer.PublicNonce,
                                           tampered, bobNonce));
        Assert.Throws<SignerException>(() => kit.Alice.AggregateClosingSignature(
                                           s_id, closing, closer.PartialSignature, closer.PublicNonce,
                                           (MusigPartialSignature)closee, kit.Bob.CreateClosingNonce(s_id)));
    }

    [Fact]
    public void Given_ABroadcastSignedCommitment_When_Closing_Then_TheCloseeAndCloserAreRefused()
    {
        // Arrange (S1, as SignChannelTransaction): Bob broadcast his commitment
        var kit = new TaprootSignerKit(bobLocalNumber: 5);
        var (commitment, signature) = SimpleTaprootCommitmentSigningTests.AliceSignsBobsCommitment(kit);
        var bobNonce = kit.Bob.CreateClosingNonce(s_id);
        var closing = kit.UnsignedSpend(outputSat: 995_000, lockTime: 0);
        var closer = kit.Alice.SignClosingAsCloser(s_id, closing, bobNonce);
        kit.Bob.SignLocalCommitmentForBroadcast(s_id, null, 5, commitment, signature);

        // Act / Assert
        Assert.Throws<SignerException>(() => kit.Bob.SignClosingAsClosee(s_id, closing, bobNonce, closer));
        Assert.Throws<SignerException>(() => kit.Bob.SignClosingAsCloser(s_id, closing,
                                                                         kit.Alice.CreateClosingNonce(s_id)));
    }
}