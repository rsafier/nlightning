using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-1059: <see cref="ChainFundingSpendLocator"/> follows a simple taproot channel's splices. Its funding outputs are
/// MuSig2 P2TR outputs spent by key path, so a splice is recognized by <c>KeyAgg(our next key, the peer's known key)</c>,
/// and past one whose peer key the backup does not name, by the peer's commitment at the end of the chain that pays our
/// taproot <c>to_remote</c>; an output still unspent, or a commitment that pays someone else, proves nothing.
/// </summary>
public class ChainFundingSpendLocatorTaprootSpliceTests
{
    private const uint Tip = 700;

    private readonly Mock<IBitcoinChainService> _chain = new();
    private readonly Dictionary<uint, Block> _blocks = [];
    private readonly HashSet<OutPoint> _unspent = [];

    public ChainFundingSpendLocatorTaprootSpliceTests()
    {
        _chain.Setup(c => c.GetCurrentBlockHeightAsync()).ReturnsAsync(Tip);
        _chain.Setup(c => c.GetBlockAsync(It.IsAny<uint>()))
              .ReturnsAsync((uint height) => _blocks.TryGetValue(height, out var block)
                                                 ? block
                                                 : SpliceBackupKit.BlockWith(height));
        _chain.Setup(c => c.GetConfirmedUnspentOutputAsync(It.IsAny<OutPoint>()))
              .ReturnsAsync((OutPoint outPoint) => _unspent.Contains(outPoint)
                                                       ? (new TxOut(Money.Satoshis(1), new Script()), 1u)
                                                       : null);
    }

    [Fact]
    public async Task Given_APeerThatKeepsItsKey_When_Followed_Then_TheMusig2OutputOfOurNextKeyIsRecognizedAtOnce()
    {
        // Arrange
        var kit = new TaprootSpliceBackupKit(peerRotatesItsKey: false);
        var entry = kit.PreSpliceEntry();

        // Act
        var next = await CreateLocator(kit).FollowSpliceAsync(entry, kit.Splice1Spend(entry), Derive(kit),
                                                              TestContext.Current.CancellationToken);

        // Assert: KeyAgg(our key 1, the peer's key) is vout 1; no block was read
        Assert.NotNull(next);
        Assert.Equal(kit.Splice1TxId, next.FundingTxId);
        Assert.Equal(1, next.FundingOutputIndex);
        Assert.Equal(1u, next.LocalFundingKeyIndex);
        Assert.Equal(kit.LocalFundingKey(1), next.LocalFundingPubKey);
        Assert.Equal(kit.RemoteFundingKey(0), next.RemoteFundingPubKey);
        _chain.Verify(c => c.GetBlockAsync(It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public async Task Given_NoMusig2Service_When_Followed_Then_NoTaprootOutputMatchesByKey()
    {
        // Arrange: the same splice, but the locator cannot aggregate keys (and the new output is unspent)
        var kit = new TaprootSpliceBackupKit(peerRotatesItsKey: false);
        var entry = kit.PreSpliceEntry();
        _unspent.Add(kit.Splice1FundingOutPoint);

        // Act
        var next = await CreateLocator(null).FollowSpliceAsync(entry, kit.Splice1Spend(entry), Derive(kit),
                                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(next);
    }

    [Fact]
    public async Task Given_ThePeerRotatedItsKeyAndCommittedOnTheSplice_When_Followed_Then_TheCommitmentPayingUsProvesTheOutput()
    {
        // Arrange: the backup knows neither of the peer's later keys; the peer force-closed on the splice
        var kit = new TaprootSpliceBackupKit();
        var entry = kit.PreSpliceEntry();
        _blocks[TaprootSpliceBackupKit.CommitmentHeight] =
            TaprootSpliceBackupKit.BlockAt(TaprootSpliceBackupKit.CommitmentHeight,
                                           kit.CommitmentOn(kit.Splice1FundingOutPoint));

        // Act
        var next = await CreateLocator(kit).FollowSpliceAsync(entry, kit.Splice1Spend(entry), Derive(kit),
                                                              TestContext.Current.CancellationToken);

        // Assert: vout 1, not the change output; the keys stay the last known (a key-path spend names none)
        Assert.NotNull(next);
        Assert.Equal(kit.Splice1TxId, next.FundingTxId);
        Assert.Equal(1, next.FundingOutputIndex);
        Assert.Equal((ulong)TaprootSpliceBackupKit.Splice1Sat, next.CapacitySat);
        Assert.Equal(entry.RemoteFundingPubKey, next.RemoteFundingPubKey);
        Assert.Equal(entry.LocalFundingKeyIndex, next.LocalFundingKeyIndex);
    }

    [Fact]
    public async Task Given_TwoSplicesWithRotatedPeerKeys_When_Followed_Then_EachStepIsProvenByTheCommitmentAtTheEnd()
    {
        // Arrange: splice 1, splice 2 and the peer's commitment on splice 2's funding, nothing named by the backup
        var kit = new TaprootSpliceBackupKit();
        var entry = kit.PreSpliceEntry();
        _blocks[TaprootSpliceBackupKit.Splice2Height] =
            TaprootSpliceBackupKit.BlockAt(TaprootSpliceBackupKit.Splice2Height, kit.Splice2);
        _blocks[TaprootSpliceBackupKit.CommitmentHeight] =
            TaprootSpliceBackupKit.BlockAt(TaprootSpliceBackupKit.CommitmentHeight,
                                           kit.CommitmentOn(kit.Splice2FundingOutPoint));
        var locator = CreateLocator(kit);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var first = await locator.FollowSpliceAsync(entry, kit.Splice1Spend(entry), Derive(kit), ct);
        var second = first is null
                         ? null
                         : await locator.FollowSpliceAsync(first, kit.Splice2Spend(first), Derive(kit), ct);

        // Assert
        Assert.NotNull(first);
        Assert.Equal(kit.Splice1TxId, first.FundingTxId);
        Assert.Equal(1, first.FundingOutputIndex);
        Assert.NotNull(second);
        Assert.Equal(kit.Splice2TxId, second.FundingTxId);
        Assert.Equal(0, second.FundingOutputIndex);
        Assert.Equal(new ShortChannelId(TaprootSpliceBackupKit.Splice2Height,
                                        TaprootSpliceBackupKit.SpliceTransactionIndex, 0), second.ShortChannelId);
    }

    [Fact]
    public async Task Given_ACommitmentThatPaysSomeoneElse_When_Followed_Then_NoOutputIsTheChannels()
    {
        // Arrange: the splice's funding output was closed by a commitment without our to_remote
        var kit = new TaprootSpliceBackupKit();
        var entry = kit.PreSpliceEntry();
        _blocks[TaprootSpliceBackupKit.CommitmentHeight] =
            TaprootSpliceBackupKit.BlockAt(TaprootSpliceBackupKit.CommitmentHeight,
                                           kit.CommitmentOn(kit.Splice1FundingOutPoint, paysUs: false));

        // Act
        var next = await CreateLocator(kit).FollowSpliceAsync(entry, kit.Splice1Spend(entry), Derive(kit),
                                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(next);
    }

    [Fact]
    public async Task Given_ThePeerRotatedItsKeyAndTheSpliceIsOpen_When_Checked_Then_TheOutputIsUndecided()
    {
        // Arrange: nothing has spent the splice's outputs yet (the peer closes once asked)
        var kit = new TaprootSpliceBackupKit();
        var entry = kit.PreSpliceEntry();
        _unspent.Add(kit.Splice1FundingOutPoint);
        _unspent.Add(new OutPoint(kit.Splice1, 0));
        var locator = CreateLocator(kit);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var next = await locator.FollowSpliceAsync(entry, kit.Splice1Spend(entry), Derive(kit), ct);
        var check = await locator.CheckSpliceOutputAsync(entry, kit.Splice1Spend(entry), 1, Derive(kit), ct);
        var missing = await locator.CheckSpliceOutputAsync(entry, kit.Splice1Spend(entry), 7, Derive(kit), ct);

        // Assert: not followed, waited for; an output the splice does not have is never the channel's
        Assert.Null(next);
        Assert.Equal(SpliceOutputStatus.Unspent, check.Status);
        Assert.Equal(SpliceOutputStatus.NotTheChannel, missing.Status);
    }

    [Fact]
    public async Task Given_ThePeersCommitmentLater_When_TheWaitedOutputIsChecked_Then_Followed()
    {
        // Arrange
        var kit = new TaprootSpliceBackupKit();
        var entry = kit.PreSpliceEntry();
        _blocks[TaprootSpliceBackupKit.CommitmentHeight] =
            TaprootSpliceBackupKit.BlockAt(TaprootSpliceBackupKit.CommitmentHeight,
                                           kit.CommitmentOn(kit.Splice1FundingOutPoint));

        // Act
        var check = await CreateLocator(kit).CheckSpliceOutputAsync(entry, kit.Splice1Spend(entry), 1, Derive(kit),
                                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(SpliceOutputStatus.Followed, check.Status);
        Assert.Equal(kit.Splice1TxId, check.Next!.FundingTxId);
        Assert.Equal(1, check.Next.FundingOutputIndex);
    }

    private static Func<uint, CompactPubKey?> Derive(TaprootSpliceBackupKit kit) =>
        i => kit.KeySource.GetFundingPubKey(TaprootSpliceBackupKit.ChannelKeyIndex, i);

    private ChainFundingSpendLocator CreateLocator(TaprootSpliceBackupKit? kit) =>
        new(_chain.Object, Options.Create(new ChannelBackupOptions { RestoreSpendSearchBatchSize = 4 }), null,
            kit?.Musig2);
}