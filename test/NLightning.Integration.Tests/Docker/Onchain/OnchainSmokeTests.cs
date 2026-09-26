using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Onchain;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Persistence.Interfaces;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 5 plan Proof O0 (chain plumbing), against bitcoind and LND david: the funding output of a channel we open is
/// a stored watch (and the funding transaction a confirmed broadcast) after the open and after a restart; and a reorg
/// of the funding block (<c>invalidateblock</c>, a shorter competing branch that holds the funding transaction one
/// block higher, <c>reconsiderblock</c>) keeps our chain state on bitcoind's tip on both branches, keeps the funding
/// output watched, and leaves the short channel id equal to the funding transaction's position once the original
/// branch is back.
/// </summary>
/// <remarks>
/// <para>A funding confirmation that completed in a disconnected block is pending again after the rewind (BOLT 5 plan
/// O6-T3, NL-292): once the funding transaction reaches the channel's depth on the competing branch, the short channel
/// id follows it (<see cref="Given_FundingBlockReorged_When_CompetingBranchIsActive_Then_ScidFollowsTheFundingTransaction"/>,
/// an explicit reproducer until ABCD wave 6).</para>
/// <para>Run with <c>scripts/run-onchain.sh</c> (own process, own fixture).</para>
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
public class OnchainSmokeTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public OnchainSmokeTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "onchain");
        await _node.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_OpenChannel_When_RestartedAndTheFundingBlockIsReorged_Then_FundingOutputWatchedAndScidConsistent()
    {
        // Arrange: a channel from us to david, usable on both sides
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _fixture.GetLndNode("david");
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var peerAddress = await node.ConnectToAsync(david, ct);
        var opened = await node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress,
                                                                              LightningMoney.Satoshis(1_000_000))
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {opened.ChannelId} ({opened.ChannelPoint()}) to david");
        var channel = await WaitUsableAsync(node, david, opened, ct);
        Assert.NotNull(opened.TxId);
        var fundingTxId = opened.TxId.Value;
        var fundingIndex = (uint)opened.Index!.Value;

        // Assert: the funding output is watched and the funding transaction is a confirmed broadcast
        await AssertFundingWatchedAsync(node, fundingTxId, fundingIndex, ct);
        Assert.Equal(BroadcastState.Confirmed, await GetBroadcastStateAsync(node, fundingTxId));

        // Act: restart
        await node.StopAsync();
        await node.StartAsync(ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [david], [node], ct);

        // Assert: still watched (and loaded as an active watch)
        await AssertFundingWatchedAsync(node, fundingTxId, fundingIndex, ct);
        var scidBefore = channel.ShortChannelId;
        Assert.NotNull(scidBefore);
        await AssertScidMatchesActiveChainAsync(node, opened, fundingTxId, ct);

        // Act: switch to a shorter competing branch that holds the funding transaction one block higher
        var fundingBlock = await SwitchToCompetingBranchAsync(node, fundingTxId, ct);

        // Assert: our state follows the competing tip and the funding output is still watched and unspent (the short
        // channel id is stale here, see the remarks)
        Assert.False(node.BlockchainMonitor.IsChainProcessingHalted);
        await AssertMonitorOnBitcoindTipAsync(node, ct);
        await AssertFundingWatchedAsync(node, fundingTxId, fundingIndex, ct);
        var (competingHeight, competingIndex) = await GetActivePositionAsync(fundingTxId, ct);
        var stale = (await node.GetChannelAsync(opened.ChannelId, ct)).ShortChannelId;
        Console.WriteLine(
            $"Competing branch: funding at {competingHeight}x{competingIndex}, channel short channel id {stale} (moves once the funding is deep enough there)");

        // Act: reconsider the original, longer branch
        var tipBefore = await ReconsiderAsync(fundingBlock, ct);
        Assert.Equal(tipBefore, (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct));
        await ChainSync.WaitAllAtTipAsync(_fixture, [david], [node], ct);

        // Assert: our state follows bitcoind's tip, the funding output is still watched and unspent, and the short
        // channel id is again the funding transaction's position in the active chain
        Assert.False(node.BlockchainMonitor.IsChainProcessingHalted);
        await AssertMonitorOnBitcoindTipAsync(node, ct);
        await AssertFundingWatchedAsync(node, fundingTxId, fundingIndex, ct);
        var after = await node.GetChannelAsync(opened.ChannelId, ct);
        Assert.Equal(scidBefore, after.ShortChannelId);
        await AssertScidMatchesActiveChainAsync(node, opened, fundingTxId, ct);
        await WaitUsableAsync(node, david, opened, ct);
    }

    /// <summary>
    /// NL-292 (BOLT 5 plan O6-T3): the funding confirmation completed in the disconnected block is pending again; once
    /// the funding transaction is as deep as the channel requires on the competing branch, the short channel id names
    /// its position there.
    /// </summary>
    [Fact]
    public async Task Given_FundingBlockReorged_When_CompetingBranchIsActive_Then_ScidFollowsTheFundingTransaction()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _fixture.GetLndNode("david");
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var peerAddress = await node.ConnectToAsync(david, ct);
        var opened = await node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress,
                                                                              LightningMoney.Satoshis(1_000_000))
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        await WaitUsableAsync(node, david, opened, ct);
        Assert.NotNull(opened.TxId);

        // Act: the competing branch holds the funding transaction one block higher; it is mined on until the funding
        // transaction is deep enough there
        var fundingBlock = await SwitchToCompetingBranchAsync(node, opened.TxId.Value, ct);

        try
        {
            // Assert
            var (height, index) = (0u, 0u);
            await Poll.UntilAsync(async () =>
            {
                (height, index) = await GetActivePositionAsync(opened.TxId.Value, ct);
                var scid = (await node.GetChannelAsync(opened.ChannelId, ct)).ShortChannelId;
                if (scid is { } current && current.BlockHeight == height && current.TransactionIndex == index)
                    return true;

                await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);
                return false;
            }, s_timeout, "the short channel id at the funding transaction's position on the competing branch", ct);
            Console.WriteLine($"Short channel id moved to {height}x{index}");
            await AssertScidMatchesActiveChainAsync(node, opened, opened.TxId.Value, ct);
        }
        finally
        {
            await ReconsiderAsync(fundingBlock, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
            await _node.DisposeAsync();
    }

    /// <summary>
    /// Invalidates the funding transaction's block, mines an empty block and then one that takes the funding
    /// transaction back from the mempool (so it sits one block higher than before), and waits until our monitor is on
    /// that shorter branch's tip. Returns the original funding block.
    /// </summary>
    private async Task<uint256> SwitchToCompetingBranchAsync(NLightningTestNode node, TxId fundingTxId,
                                                            CancellationToken ct)
    {
        var bitcoin = _fixture.Bitcoin;

        // The original branch must stay longer than the competing one (fork + 2 blocks)
        await ChainSync.MineAndWaitAsync(_fixture, 2, [_fixture.GetLndNode("david")], [node], ct);
        var displayTxId = new uint256((byte[])fundingTxId);
        var fundingBlock = (await bitcoin.GetRawTransactionInfoAsync(displayTxId, ct)).BlockHash;
        var tipBefore = (uint)await bitcoin.GetBlockCountAsync(ct);
        Console.WriteLine($"Funding block {fundingBlock}, tip {tipBefore}");

        await bitcoin.InvalidateBlockAsync(fundingBlock, ct);
        var address = await bitcoin.GetNewAddressAsync(ct);
        await bitcoin.SendCommandAsync("generateblock", ct, address.ToString(), Array.Empty<string>());
        await bitcoin.GenerateToAddressAsync(1, address, ct);
        var shortTip = (uint)await bitcoin.GetBlockCountAsync(ct);
        Assert.True(shortTip < tipBefore, $"the competing branch ({shortTip}) should be shorter than {tipBefore}");
        var competing = await bitcoin.GetRawTransactionInfoAsync(displayTxId, ct);
        Assert.NotEqual(fundingBlock, competing.BlockHash);

        await Poll.UntilAsync(() => Task.FromResult(node.BlockchainMonitor.LastProcessedBlockHeight == shortTip),
                              s_timeout, $"our monitor rewound to the competing tip {shortTip}", ct);
        return fundingBlock;
    }

    /// <summary>Reconsiders the original funding block; returns bitcoind's new tip height.</summary>
    private async Task<uint> ReconsiderAsync(uint256 fundingBlock, CancellationToken ct)
    {
        await _fixture.Bitcoin.SendCommandAsync("reconsiderblock", ct, fundingBlock.ToString());
        return (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
    }

    /// <summary>The funding transaction's height and index in the active chain.</summary>
    private async Task<(uint Height, uint Index)> GetActivePositionAsync(TxId fundingTxId, CancellationToken ct)
    {
        var bitcoin = _fixture.Bitcoin;
        var displayTxId = new uint256((byte[])fundingTxId);
        var info = await bitcoin.GetRawTransactionInfoAsync(displayTxId, ct);
        var block = await bitcoin.GetBlockAsync(info.BlockHash, ct);
        var height = (uint)(await bitcoin.GetBlockCountAsync(ct) - info.Confirmations + 1);
        var index = (uint)block.Transactions.FindIndex(t => t.GetHash() == displayTxId);
        return (height, index);
    }

    private async Task<ChannelInfoClientResponse> WaitUsableAsync(NLightningTestNode node,
                                                                  LNUnit.LND.LNDNodeConnection peer,
                                                                  OpenChannelClientSubscriptionResponse opened,
                                                                  CancellationToken ct)
    {
        ChannelInfoClientResponse? usable = null;
        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(opened.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, opened.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && lnd is { Active: true })
            {
                usable = ours;
                return true;
            }

            // LND may want more confirmations than we do
            await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [node], ct);
            return false;
        }, s_timeout, $"channel {opened.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [peer], [node], ct);
        return usable!;
    }

    private static async Task AssertFundingWatchedAsync(NLightningTestNode node, TxId fundingTxId, uint fundingIndex,
                                                        CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().WatchedOutpointDbRepository;
        var watch = await repository.GetAsync(fundingTxId, fundingIndex);
        Assert.NotNull(watch);
        Assert.Equal(WatchedOutpointPurpose.FundingOutput, watch.Purpose);
        Assert.False(watch.IsSpent);
        Assert.Contains(await repository.GetActiveAsync(),
                        w => w.TransactionId == fundingTxId && w.OutputIndex == fundingIndex);
        ct.ThrowIfCancellationRequested();
    }

    private static async Task<BroadcastState?> GetBroadcastStateAsync(NLightningTestNode node, TxId txId)
    {
        using var scope = node.Services.CreateScope();
        var broadcast = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BroadcastTransactionDbRepository
                                   .GetByTransactionIdAsync(txId);
        return broadcast?.State;
    }

    private async Task AssertMonitorOnBitcoindTipAsync(NLightningTestNode node, CancellationToken ct)
    {
        var bitcoin = _fixture.Bitcoin;
        var tip = (uint)await bitcoin.GetBlockCountAsync(ct);
        var bestHash = await bitcoin.GetBestBlockHashAsync(ct);
        Assert.Equal(tip, node.BlockchainMonitor.LastProcessedBlockHeight);
        using var scope = node.Services.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BlockchainStateDbRepository
                               .GetStateAsync();
        Assert.NotNull(state);
        Assert.Equal(tip, state.LastProcessedHeight);
        Assert.Equal(bestHash.ToBytes(), (byte[])state.LastProcessedBlockHash);
    }

    private async Task AssertScidMatchesActiveChainAsync(NLightningTestNode node,
                                                         OpenChannelClientSubscriptionResponse opened,
                                                         TxId fundingTxId, CancellationToken ct)
    {
        var (height, index) = await GetActivePositionAsync(fundingTxId, ct);
        var channel = await node.GetChannelAsync(opened.ChannelId, ct);
        Assert.NotNull(channel.ShortChannelId);
        Assert.Equal(height, channel.ShortChannelId.Value.BlockHeight);
        Assert.Equal(index, channel.ShortChannelId.Value.TransactionIndex);
        Assert.Equal(opened.Index, channel.ShortChannelId.Value.OutputIndex);
        Assert.Equal(ChannelState.Open, channel.State);
    }
}