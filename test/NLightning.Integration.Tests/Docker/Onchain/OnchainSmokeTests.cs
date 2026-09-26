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
/// of the funding block (<c>invalidateblock</c>, a competing block, <c>reconsiderblock</c>) leaves our chain state on
/// bitcoind's tip and the channel's short channel id equal to the funding transaction's position in the active chain.
/// </summary>
/// <remarks>Run with <c>scripts/run-onchain.sh</c> (own process, own fixture).</remarks>
[Collection(OnchainRegtestCollection.Name)]
public class OnchainSmokeTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);

    /// <summary>The Critical line the monitor writes for a completed watch in a disconnected block.</summary>
    private const string CompletedWatchDisconnectedLog = "its confirmation is not rolled back";

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

        // Act: disconnect the funding block, mine a competing block (it takes the funding tx back from the mempool),
        // then reconsider the original, longer branch
        var bitcoin = _fixture.Bitcoin;
        var displayTxId = new uint256((byte[])fundingTxId);
        var fundingBlock = (await bitcoin.GetRawTransactionInfoAsync(displayTxId, ct)).BlockHash;
        var tipBefore = (uint)await bitcoin.GetBlockCountAsync(ct);
        Console.WriteLine($"Funding block {fundingBlock}, tip {tipBefore}");
        await bitcoin.InvalidateBlockAsync(fundingBlock, ct);
        await bitcoin.GenerateToAddressAsync(1, await bitcoin.GetNewAddressAsync(ct), ct);
        var shortTip = (uint)await bitcoin.GetBlockCountAsync(ct);
        Assert.True(shortTip < tipBefore, $"the competing branch ({shortTip}) should be shorter than {tipBefore}");
        await Poll.UntilAsync(() => Task.FromResult(node.BlockchainMonitor.LastProcessedBlockHeight == shortTip
                                                 && node.CountLogLines(CompletedWatchDisconnectedLog) > 0),
                              s_timeout, $"our monitor rewound to the competing tip {shortTip}", ct);
        await AssertMonitorOnBitcoindTipAsync(node, ct);

        await bitcoin.SendCommandAsync("reconsiderblock", ct, fundingBlock.ToString());
        Assert.Equal(tipBefore, (uint)await bitcoin.GetBlockCountAsync(ct));
        await ChainSync.WaitAllAtTipAsync(_fixture, [david], [node], ct);

        // Assert: our state follows bitcoind's tip, the funding output is still watched and unspent, and the short
        // channel id is the funding transaction's position in the active chain
        Assert.False(node.BlockchainMonitor.IsChainProcessingHalted);
        await AssertMonitorOnBitcoindTipAsync(node, ct);
        await AssertFundingWatchedAsync(node, fundingTxId, fundingIndex, ct);
        var after = await node.GetChannelAsync(opened.ChannelId, ct);
        Assert.Equal(scidBefore, after.ShortChannelId);
        await AssertScidMatchesActiveChainAsync(node, opened, fundingTxId, ct);
        await WaitUsableAsync(node, david, opened, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
            await _node.DisposeAsync();
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
        var bitcoin = _fixture.Bitcoin;
        var displayTxId = new uint256((byte[])fundingTxId);
        var info = await bitcoin.GetRawTransactionInfoAsync(displayTxId, ct);
        var block = await bitcoin.GetBlockAsync(info.BlockHash, ct);
        var height = (uint)(await bitcoin.GetBlockCountAsync(ct) - info.Confirmations + 1);
        var index = (uint)block.Transactions.FindIndex(t => t.GetHash() == displayTxId);
        var channel = await node.GetChannelAsync(opened.ChannelId, ct);
        Assert.NotNull(channel.ShortChannelId);
        Assert.Equal(height, channel.ShortChannelId.Value.BlockHeight);
        Assert.Equal(index, channel.ShortChannelId.Value.TransactionIndex);
        Assert.Equal(opened.Index, channel.ShortChannelId.Value.OutputIndex);
        Assert.Equal(ChannelState.Open, channel.State);
    }
}