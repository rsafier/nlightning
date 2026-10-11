using NBitcoin;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;

/// <summary>
/// NL-1059: the pure half of following a simple taproot channel's splices: P2TR candidates, MuSig2 matches and the
/// commitment that proves a funding output is ours.
/// </summary>
public class SpliceSpendFollowerTaprootTests
{
    private readonly TaprootSpliceBackupKit _kit = new(peerRotatesItsKey: false);

    [Fact]
    public void Given_ATaprootSplice_When_CandidatesAreRead_Then_OnlyItsP2TrOutputsCount()
    {
        // Arrange: a P2WPKH output next to the two P2TR ones
        var splice = _kit.Splice1.Clone();
        splice.Outputs.Add(new TxOut(Money.Satoshis(5_000), new Key().PubKey.WitHash.ScriptPubKey));

        // Act / Assert
        Assert.Equal([(ushort)0, (ushort)1], SpliceSpendFollower.GetCandidateOutputs(splice, true));
        Assert.Empty(SpliceSpendFollower.GetCandidateOutputs(splice));
        Assert.Equal([(ushort)0, (ushort)1], SpliceSpendFollower.GetSpliceCandidateOutputs(splice.ToBytes(), true));
    }

    [Fact]
    public void Given_TheFundingKeys_When_Matched_Then_OnlyTheMusig2OutputOfThoseKeysMatches()
    {
        // Arrange
        var output = _kit.Splice1.Outputs[1];

        // Act / Assert
        Assert.True(SpliceSpendFollower.Matches(output, _kit.LocalFundingKey(1), _kit.RemoteFundingKey(0), true,
                                                _kit.Musig2));
        Assert.False(SpliceSpendFollower.Matches(output, _kit.LocalFundingKey(2), _kit.RemoteFundingKey(0), true,
                                                 _kit.Musig2));
        Assert.False(SpliceSpendFollower.Matches(output, _kit.LocalFundingKey(1), _kit.RemoteFundingKey(0), true,
                                                 null));
        Assert.False(SpliceSpendFollower.Matches(output, _kit.LocalFundingKey(1), _kit.RemoteFundingKey(0)));
    }

    [Fact]
    public void Given_ThePeersCommitment_When_Checked_Then_OnlyOneThatPaysOurPaymentBasepointProves()
    {
        // Arrange
        var paying = _kit.CommitmentOn(_kit.Splice1FundingOutPoint);
        var foreign = _kit.CommitmentOn(_kit.Splice1FundingOutPoint, paysUs: false);

        // Act / Assert: a splice (no commitment shape) never proves, even with the same outputs
        Assert.True(SpliceSpendFollower.PaysUsOnTaprootCommitment(paying, _kit.Basepoints.PaymentBasepoint));
        Assert.False(SpliceSpendFollower.PaysUsOnTaprootCommitment(foreign, _kit.Basepoints.PaymentBasepoint));
        var notACommitment = paying.Clone();
        notACommitment.LockTime = 0;
        Assert.False(SpliceSpendFollower.PaysUsOnTaprootCommitment(notACommitment, _kit.Basepoints.PaymentBasepoint));
    }
}