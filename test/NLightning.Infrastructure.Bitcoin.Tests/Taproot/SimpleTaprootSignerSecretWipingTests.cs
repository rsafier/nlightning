namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// NL-911: the signer's simple taproot paths zero every extended channel key the key manager hands them once the key
/// they need is derived (verification nonces, rotated funding keys, just-in-time MuSig2 signing), on success and when
/// the signing is refused.
/// </summary>
public class SimpleTaprootSignerSecretWipingTests
{
    private const ulong Number = 5;

    [Fact]
    public void Given_AVerificationNonce_When_Derived_Then_TheChannelKeyHandedOutIsZeroed()
    {
        // Arrange
        var handedOut = new List<byte[]>();
        var signer = TaprootSignerKit.CreateSigner(0xa1, handedOut);

        // Act
        signer.GetLocalVerificationNonce(0u, new TxId(Enumerable.Repeat((byte)0x42, 32).ToArray()), Number);

        // Assert
        AssertAllZeroed(handedOut);
    }

    [Fact]
    public void Given_ASpliceFundingKey_When_Derived_Then_TheChannelKeyHandedOutIsZeroed()
    {
        // Arrange
        var handedOut = new List<byte[]>();
        var signer = TaprootSignerKit.CreateSigner(0xa1, handedOut);

        // Act: m/0'/1' (a rotated funding key) and the original m/0'
        var rotated = signer.GetFundingPubKey(0u, 1);
        var original = signer.GetFundingPubKey(0u, 0);

        // Assert: still the same keys, and nothing left behind
        Assert.NotEqual(rotated, original);
        Assert.Equal(rotated, TaprootSignerKit.CreateSigner(0xa1).GetFundingPubKey(0u, 1));
        AssertAllZeroed(handedOut);
    }

    [Fact]
    public void Given_AJustInTimeCommitmentSignature_When_Signed_Then_EveryChannelKeyHandedOutIsZeroed()
    {
        // Arrange
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var handedOut = new List<byte[]>();
        var alice = TaprootSignerKit.CreateSigner(0xa1, handedOut);
        alice.RegisterChannel(TaprootSignerKit.ChannelId, kit.SigningInfo(alice: true));
        var commitment = kit.UnsignedSpend();
        var bobNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, kit.FundingTxId, Number);

        // Act
        var signature = alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, commitment, bobNonce);

        // Assert: Bob accepts it, and Alice's signer kept no copy of its channel key
        kit.Bob.ValidateLocalCommitmentPartialSignature(TaprootSignerKit.ChannelId, null, Number, signature,
                                                        commitment);
        AssertAllZeroed(handedOut);
    }

    [Fact]
    public void Given_ARefusedNonce_When_SigningFails_Then_EveryChannelKeyHandedOutIsZeroed()
    {
        // Arrange: an invalid peer nonce makes the session fail after the funding key was derived
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var handedOut = new List<byte[]>();
        var alice = TaprootSignerKit.CreateSigner(0xa1, handedOut);
        alice.RegisterChannel(TaprootSignerKit.ChannelId, kit.SigningInfo(alice: true));
        var badNonce = new MusigPublicNonce(new byte[66]);

        // Act
        Assert.ThrowsAny<Exception>(() => alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null,
                                                                             kit.UnsignedSpend(), badNonce));

        // Assert
        AssertAllZeroed(handedOut);
    }

    private static void AssertAllZeroed(List<byte[]> handedOut)
    {
        Assert.NotEmpty(handedOut);
        Assert.All(handedOut, key => Assert.All(key, b => Assert.Equal(0, b)));
    }
}