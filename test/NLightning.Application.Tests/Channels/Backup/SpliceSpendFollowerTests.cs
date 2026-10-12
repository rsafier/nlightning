using NBitcoin;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

public class SpliceSpendFollowerTests
{
    [Fact]
    public void Given_ACommitmentAndASplice_When_Checked_Then_OnlyTheCommitmentHasTheBolt3Shape()
    {
        // Arrange
        var kit = new SpliceBackupKit();

        // Act / Assert
        Assert.True(SpliceSpendFollower.IsCommitment(kit.Commitment));
        Assert.False(SpliceSpendFollower.IsCommitment(kit.Splice));
    }

    [Fact]
    public void Given_ASpliceWithOurNextKey_When_Identified_Then_TheEntryMovesToItsFundingOutput()
    {
        // Arrange: a backup written before the splice started (no pending splice named)
        var kit = new SpliceBackupKit();
        var entry = kit.PreSpliceEntry();

        // Act
        var next = SpliceSpendFollower.TryIdentifyNextFunding(entry, kit.Splice, SpliceBackupKit.SpliceHeight,
                                                              SpliceBackupKit.SpliceTransactionIndex,
                                                              i => kit.KeySource.GetFundingPubKey(
                                                                  SpliceBackupKit.ChannelKeyIndex, i));

        // Assert: the 2-of-2 output of our key 1 (m/0'/1') and the peer's key, with its short channel id
        Assert.NotNull(next);
        Assert.Equal(kit.SpliceTxId, next.FundingTxId);
        Assert.Equal(SpliceBackupKit.SpliceOutputIndex, next.FundingOutputIndex);
        Assert.Equal((ulong)SpliceBackupKit.SpliceSat, next.CapacitySat);
        Assert.Equal(1u, next.LocalFundingKeyIndex);
        Assert.Equal(kit.LocalFundingKey(1), next.LocalFundingPubKey);
        Assert.NotEqual(kit.LocalFundingKey(0), next.LocalFundingPubKey);
        Assert.Equal(kit.RemoteFundingKey, next.RemoteFundingPubKey);
        Assert.Equal(SpliceBackupKit.SpliceHeight, next.FundingHeight);
        Assert.Equal(new ShortChannelId(SpliceBackupKit.SpliceHeight, SpliceBackupKit.SpliceTransactionIndex,
                                        SpliceBackupKit.SpliceOutputIndex), next.ShortChannelId);
        Assert.Empty(next.PendingFundings);
        Assert.Equal(entry.ChannelId, next.ChannelId);
        Assert.Equal(entry.LocalPaymentBasepoint, next.LocalPaymentBasepoint);
    }

    [Fact]
    public void Given_APendingSpliceWithThePeersRotatedKey_When_Identified_Then_ThePendingFundingIsFollowed()
    {
        // Arrange: the peer rotated its key too, and the backup named the pending splice
        var kit = new SpliceBackupKit(peerRotatesItsKey: true);
        var entry = kit.PreSpliceEntry(withPendingSplice: true);

        // Act
        var next = SpliceSpendFollower.TryIdentifyNextFunding(entry, kit.Splice, SpliceBackupKit.SpliceHeight,
                                                              SpliceBackupKit.SpliceTransactionIndex, _ => null);

        // Assert
        Assert.NotNull(next);
        Assert.Equal(kit.SpliceTxId, next.FundingTxId);
        Assert.Equal(kit.SpliceRemoteKey, next.RemoteFundingPubKey);
        Assert.Equal(1u, next.LocalFundingKeyIndex);
    }

    [Fact]
    public void Given_ThePeersRotatedKeyAndNoPendingSplice_When_Identified_Then_NullAndTheWitnessOfTheOutputsSpendNamesIt()
    {
        // Arrange
        var kit = new SpliceBackupKit(peerRotatesItsKey: true);
        var entry = kit.PreSpliceEntry();
        CompactPubKey? Derive(uint i) => kit.KeySource.GetFundingPubKey(SpliceBackupKit.ChannelKeyIndex, i);

        // Act
        var byScript = SpliceSpendFollower.TryIdentifyNextFunding(entry, kit.Splice, SpliceBackupKit.SpliceHeight,
                                                                  SpliceBackupKit.SpliceTransactionIndex, Derive);
        var byWitness = SpliceSpendFollower.TryParseFundingWitness(entry, kit.Commitment.Inputs[0].WitScript, Derive);

        // Assert
        Assert.Null(byScript);
        Assert.NotNull(byWitness);
        Assert.Equal(1u, byWitness.Value.Index);
        Assert.Equal(kit.LocalFundingKey(1), byWitness.Value.Local);
        Assert.Equal(kit.SpliceRemoteKey, byWitness.Value.Remote);
    }

    [Fact]
    public void Given_AMutualClose_When_Identified_Then_Null()
    {
        // Arrange: the funding spent to two P2WPKH outputs
        var kit = new SpliceBackupKit();
        var close = SpliceBackupKit.NewTransaction(0);
        close.Inputs.Add(new TxIn(kit.FundingOutPoint) { Sequence = 0xFFFFFFFF });
        close.Outputs.Add(new TxOut(Money.Satoshis(500_000), new Key().PubKey.WitHash.ScriptPubKey));
        close.Outputs.Add(new TxOut(Money.Satoshis(499_000), new Key().PubKey.WitHash.ScriptPubKey));

        // Act
        var next = SpliceSpendFollower.TryIdentifyNextFunding(kit.PreSpliceEntry(), close, 700, 1,
                                                              i => kit.KeySource.GetFundingPubKey(
                                                                  SpliceBackupKit.ChannelKeyIndex, i));

        // Assert
        Assert.Null(next);
        Assert.False(SpliceSpendFollower.IsCommitment(close));
    }

    [Fact]
    public void Given_OurKeyBeyondTheLookahead_When_Identified_Then_Null()
    {
        // Arrange: a backup at key index 3 only tries 3..11; the splice uses our key 1 (an older one)
        var kit = new SpliceBackupKit();
        var entry = kit.PreSpliceEntry() with { LocalFundingKeyIndex = 3 };

        // Act
        var next = SpliceSpendFollower.TryIdentifyNextFunding(entry, kit.Splice, SpliceBackupKit.SpliceHeight,
                                                              SpliceBackupKit.SpliceTransactionIndex,
                                                              i => kit.KeySource.GetFundingPubKey(
                                                                  SpliceBackupKit.ChannelKeyIndex, i));

        // Assert
        Assert.Null(next);
        Assert.Equal(Enumerable.Range(3, 9).Select(i => (uint)i), SpliceSpendFollower.CandidateKeyIndexes(entry));
    }

    [Fact]
    public void Given_ASignerThatCannotDeriveRotatedKeys_When_Identified_Then_Null()
    {
        // Arrange: only index 0 derives (a signer without m/0'/i')
        var kit = new SpliceBackupKit();

        // Act
        var next = SpliceSpendFollower.TryIdentifyNextFunding(kit.PreSpliceEntry(), kit.Splice,
                                                              SpliceBackupKit.SpliceHeight,
                                                              SpliceBackupKit.SpliceTransactionIndex,
                                                              i => i == 0 ? kit.LocalFundingKey(0) : null);

        // Assert
        Assert.Null(next);
    }
}