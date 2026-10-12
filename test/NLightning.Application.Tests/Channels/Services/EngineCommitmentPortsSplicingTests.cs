namespace NLightning.Application.Tests.Channels.Services;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;

/// <summary>
/// Splicing plan SP1-C-T1: the production engine ports sign and verify the commitment of a pending splice funding
/// with real secp256k1 signatures (its outpoint, capacity and rotated funding keys, anchors included), and a null or
/// current funding signs exactly as before splicing.
/// </summary>
public class EngineCommitmentPortsSplicingTests
{
    private const ulong SpliceCapacitySatoshis = 1_300_000;
    private static readonly TxId s_spliceTxId = new(Enumerable.Repeat((byte)0x88, 32).ToArray());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_APendingSpliceOnBothSides_When_AliceSignsItsCommitment_Then_BobVerifiesItOnlyForThatFunding(
        bool hasAnchors)
    {
        // Arrange: both register the splice with their funding key 1 (D5)
        using var pair = new RealSigningCommitmentPair(hasAnchors);
        var (aliceView, bobView) = RegisterSplice(pair);
        var remote = pair.Alice.State.RemoteCommit;
        var local = pair.Bob.State.LocalCommit;

        // Act
        var signatures = pair.Alice.CommitmentSigner.SignRemoteCommitment(RealSigningCommitmentPair.ChannelId,
                                                                          aliceView, remote.Number, remote.Spec,
                                                                          remote.PerCommitmentPoint);

        // Assert: valid for the splice's commitment, not for the current funding's
        Assert.True(pair.Bob.CommitmentVerifier.VerifyLocalCommitment(RealSigningCommitmentPair.ChannelId, bobView,
                                                                      local.Number, local.Spec, signatures));
        Assert.False(pair.Bob.CommitmentVerifier.VerifyLocalCommitment(RealSigningCommitmentPair.ChannelId, null,
                                                                       local.Number, local.Spec, signatures));
        var current = pair.Alice.CommitmentSigner.SignRemoteCommitment(RealSigningCommitmentPair.ChannelId, null,
                                                                       remote.Number, remote.Spec,
                                                                       remote.PerCommitmentPoint);
        Assert.NotEqual(current.Signature, signatures.Signature);
    }

    [Fact]
    public void Given_TheCurrentFunding_When_SignedWithAndWithoutIt_Then_TheSameSignatures()
    {
        // Arrange
        using var pair = new RealSigningCommitmentPair(true);
        var remote = pair.Alice.State.RemoteCommit;
        var current = ChannelFunding.FromFundingOutput(pair.Alice.Channel.FundingOutput!);

        // Act
        var withNull = pair.Alice.CommitmentSigner.SignRemoteCommitment(RealSigningCommitmentPair.ChannelId, null,
                                                                        remote.Number, remote.Spec,
                                                                        remote.PerCommitmentPoint);
        var withCurrent = pair.Alice.CommitmentSigner.SignRemoteCommitment(RealSigningCommitmentPair.ChannelId,
                                                                           current, remote.Number, remote.Spec,
                                                                           remote.PerCommitmentPoint);

        // Assert
        Assert.Equal(withNull.Signature, withCurrent.Signature);
        Assert.Equal(withNull.HtlcSignatures, withCurrent.HtlcSignatures);
    }

    [Fact]
    public void Given_AnUnregisteredSplice_When_Signed_Then_TheSignerRefuses()
    {
        // Arrange
        using var pair = new RealSigningCommitmentPair(false);
        var remote = pair.Alice.State.RemoteCommit;
        var funding = SpliceFunding(pair.Alice.Signer.GetFundingPubKey(RealSigningCommitmentPair.ChannelId, 1),
                                    pair.Bob.Signer.GetFundingPubKey(RealSigningCommitmentPair.ChannelId, 1));

        // Act / Assert
        Assert.Throws<Domain.Exceptions.SignerException>(
            () => pair.Alice.CommitmentSigner.SignRemoteCommitment(RealSigningCommitmentPair.ChannelId, funding,
                                                                   remote.Number, remote.Spec,
                                                                   remote.PerCommitmentPoint));
    }

    private static (ChannelFunding Alice, ChannelFunding Bob) RegisterSplice(RealSigningCommitmentPair pair)
    {
        var aliceKey = pair.Alice.Signer.GetFundingPubKey(RealSigningCommitmentPair.ChannelId, 1);
        var bobKey = pair.Bob.Signer.GetFundingPubKey(RealSigningCommitmentPair.ChannelId, 1);
        var aliceView = SpliceFunding(aliceKey, bobKey);
        var bobView = SpliceFunding(bobKey, aliceKey);
        pair.Alice.Signer.RegisterFunding(RealSigningCommitmentPair.ChannelId, aliceView);
        pair.Bob.Signer.RegisterFunding(RealSigningCommitmentPair.ChannelId, bobView);
        return (aliceView, bobView);
    }

    private static ChannelFunding SpliceFunding(Domain.Crypto.ValueObjects.CompactPubKey local,
                                                Domain.Crypto.ValueObjects.CompactPubKey remote) =>
        new(s_spliceTxId, 1, SpliceCapacitySatoshis, local, remote, 1, 0, 0, ChannelFundingKind.Splice,
            ChannelFundingStatus.Pending, 2_500, 0);
}