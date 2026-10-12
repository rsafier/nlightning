using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Bitcoin.Signers;
using Domain.Exceptions;
using static SimpleTaprootCommitmentSigningTests;

/// <summary>
/// NL-1376: the verification nonce of a simple taproot commitment is derived the same way in every process, so its
/// broadcast session is claimed in the durable guard store before the signed commitment leaves the signer. Two
/// signers over one store stand for two processes (a standby, a restart) over one database: a second session over the
/// nonce is refused in both, the same session again is allowed.
/// </summary>
public class SimpleTaprootDurableBroadcastSessionTests
{
    private const ulong Number = 5;

    [Fact]
    public void Given_ABroadcastSessionInOneProcess_When_AnotherProcessSignsAnotherSession_Then_ItIsRefused()
    {
        // Arrange: Alice signs Bob's commitment twice (two just-in-time nonces: two sessions over Bob's one nonce);
        // Bob's first process broadcasts with the first
        var store = new InMemoryChannelSignerGuardStore();
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var (commitment, first) = AliceSignsBobsCommitment(kit);
        var second = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, commitment,
                                                           kit.Bob.GetLocalVerificationNonce(
                                                               TaprootSignerKit.ChannelId, null, Number));
        var processA = CreateBob(kit, store);
        var signedByA = processA.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, null, Number, commitment,
                                                                 first);
        var processB = CreateBob(kit, store);

        // Act
        var exception = Assert.Throws<SignerException>(() => processB.SignLocalCommitmentForBroadcast(
                                                           TaprootSignerKit.ChannelId, null, Number, commitment,
                                                           second));

        // Assert: refused (its memory knew nothing), and the session of A again gives A's transaction
        Assert.Contains("another session", exception.Message);
        var signedByB = processB.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, null, Number, commitment,
                                                                 first);
        Assert.Equal(signedByA.RawTxBytes, signedByB.RawTxBytes);
        Assert.Equal(1, store.ClaimedSessions);
    }

    [Fact]
    public void Given_AClaimThatCannotBeWritten_When_Broadcasting_Then_NothingLeavesAndARetryCanClaim()
    {
        // Arrange
        var store = new InMemoryChannelSignerGuardStore { FailClaims = true };
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var (commitment, first) = AliceSignsBobsCommitment(kit);
        var second = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null, commitment,
                                                           kit.Bob.GetLocalVerificationNonce(
                                                               TaprootSignerKit.ChannelId, null, Number));
        var bob = CreateBob(kit, store);

        // Act / Assert: no signed commitment while the claim fails
        Assert.Throws<SignerException>(() => bob.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, null,
                                                                                 Number, commitment, first));

        // The failed claim left no memory record: once the store works, another session can be the one claimed (the
        // first never left the signer), and from then on it is the only one
        store.FailClaims = false;
        Assert.Null(Record.Exception(() => bob.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, null,
                                                                               Number, commitment, second)));
        Assert.Throws<SignerException>(() => bob.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, null,
                                                                                 Number, commitment, first));
    }

    private static LocalLightningSigner CreateBob(TaprootSignerKit kit, InMemoryChannelSignerGuardStore store)
    {
        var bob = TaprootSignerKit.CreateSigner(0xb0, guardStore: store);
        bob.RegisterChannel(TaprootSignerKit.ChannelId, kit.SigningInfo(alice: false, Number));
        return bob;
    }
}