using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// <see cref="ChainFundingSpendLocator.FollowSpliceAsync"/> over a fake chain (lane SP2-E, NL-478): a splice of the
/// backed-up funding is followed to its new funding output, a commitment is not.
/// </summary>
public class ChainFundingSpendLocatorSpliceTests
{
    private const uint Tip = 700;

    private readonly Mock<IBitcoinChainService> _chain = new();
    private readonly Dictionary<uint, Block> _blocks = [];

    public ChainFundingSpendLocatorSpliceTests()
    {
        _chain.Setup(c => c.GetCurrentBlockHeightAsync()).ReturnsAsync(Tip);
        _chain.Setup(c => c.GetBlockAsync(It.IsAny<uint>()))
              .ReturnsAsync((uint height) => _blocks.TryGetValue(height, out var block)
                                                 ? block
                                                 : SpliceBackupKit.BlockWith(height));
    }

    [Fact]
    public async Task Given_ASpliceSpend_When_Followed_Then_TheEntryAtTheSplicesFundingOutputIsReturned()
    {
        // Arrange
        var kit = new SpliceBackupKit();
        var entry = kit.PreSpliceEntry();

        // Act
        var next = await CreateLocator().FollowSpliceAsync(entry, kit.SpliceSpend(entry), Derive(kit),
                                                           TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(next);
        Assert.Equal(kit.SpliceTxId, next.FundingTxId);
        Assert.Equal(1u, next.LocalFundingKeyIndex);
        _chain.Verify(c => c.GetBlockAsync(It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public async Task Given_ACommitmentSpend_When_Followed_Then_Null()
    {
        // Arrange: the spend of the post-splice funding is the peer's commitment
        var kit = new SpliceBackupKit();
        var entry = kit.PostSpliceEntry();
        var spend = new Domain.Bitcoin.Events.OutpointSpentEventArgs(
            entry.ChannelId, new Domain.Bitcoin.ValueObjects.SignedTransaction(
                                 new Domain.Bitcoin.ValueObjects.TxId(kit.Commitment.GetHash().ToBytes()),
                                 kit.Commitment.ToBytes()), 650, 1, entry.FundingTxId, entry.FundingOutputIndex,
            new Domain.Crypto.ValueObjects.Hash(new byte[32]));

        // Act
        var next = await CreateLocator().FollowSpliceAsync(entry, spend, Derive(kit),
                                                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(next);
    }

    [Fact]
    public async Task Given_ThePeerRotatedItsKey_When_Followed_Then_TheWitnessOfTheCommitmentNamesTheNewFunding()
    {
        // Arrange: nothing in the backup names the peer's new key; the peer's commitment spent the new output
        var kit = new SpliceBackupKit(peerRotatesItsKey: true);
        var entry = kit.PreSpliceEntry();
        _blocks[650] = SpliceBackupKit.BlockWith(650, kit.Commitment);

        // Act
        var next = await CreateLocator().FollowSpliceAsync(entry, kit.SpliceSpend(entry), Derive(kit),
                                                           TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(next);
        Assert.Equal(kit.SpliceTxId, next.FundingTxId);
        Assert.Equal(SpliceBackupKit.SpliceOutputIndex, next.FundingOutputIndex);
        Assert.Equal(1u, next.LocalFundingKeyIndex);
        Assert.Equal(kit.LocalFundingKey(1), next.LocalFundingPubKey);
        Assert.Equal(kit.SpliceRemoteKey, next.RemoteFundingPubKey);
    }

    [Fact]
    public async Task Given_ThePeerRotatedItsKeyAndTheNewOutputIsUnspent_When_Followed_Then_Null()
    {
        // Arrange: the splice's outputs are unspent, so its funding output can't be told apart
        var kit = new SpliceBackupKit(peerRotatesItsKey: true);
        var entry = kit.PreSpliceEntry();
        _chain.Setup(c => c.GetConfirmedUnspentOutputAsync(It.IsAny<OutPoint>()))
              .ReturnsAsync((new TxOut(Money.Satoshis(1), new Script()), SpliceBackupKit.SpliceHeight));

        // Act
        var next = await CreateLocator().FollowSpliceAsync(entry, kit.SpliceSpend(entry), Derive(kit),
                                                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(next);
    }

    private static Func<uint, CompactPubKey?> Derive(SpliceBackupKit kit) =>
        i => kit.KeySource.GetFundingPubKey(SpliceBackupKit.ChannelKeyIndex, i);

    private ChainFundingSpendLocator CreateLocator() =>
        new(_chain.Object, Options.Create(new ChannelBackupOptions { RestoreSpendSearchBatchSize = 4 }));
}