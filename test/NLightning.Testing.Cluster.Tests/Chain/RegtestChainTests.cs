namespace NLightning.Testing.Cluster.Tests.Chain;

using Cluster.Chain;
using Cluster.Nodes.BitcoinCore.Rpc;

public class RegtestChainTests
{
    private static readonly TimeSpan s_short = TimeSpan.FromMilliseconds(200);

    private static RegtestChain Chain(FakeBitcoinCore bitcoin) =>
        new(bitcoin, new RegtestChainOptions { PollInterval = TimeSpan.FromMilliseconds(1), DefaultTimeout = s_short });

    [Fact]
    public async Task Given_TwoMines_When_Mined_Then_TheBlocksPayOneMiningAddress()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();
        var chain = Chain(bitcoin);

        // Act
        var first = await chain.MineAsync(3, ct);
        var second = await chain.MineAsync(2, ct);
        var none = await chain.MineAsync(0, ct);

        // Assert
        Assert.Equal(3, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Empty(none);
        Assert.Equal(5, (await chain.GetTipAsync(ct)).Height);
        Assert.Single(bitcoin.Calls, c => c.Method == "getnewaddress");
        Assert.All(bitcoin.Calls.Where(c => c.Method == "generatetoaddress"), c => Assert.Equal("addr1", c.Args[1]));
    }

    [Fact]
    public async Task Given_ALaggingFollower_When_WaitingAtTheTip_Then_ItReturnsOnceTheFollowerCatchesUp()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();
        var chain = Chain(bitcoin);
        var polls = 0;
        var lagging = ChainFollower.AtHeight("lagging", _ => Task.FromResult(++polls < 3 ? 1L : 4L));
        var synced = ChainFollower.Bitcoind("peer", bitcoin);

        // Act
        var tip = await chain.MineAndWaitAsync(4, [lagging, synced], ct);

        // Assert
        Assert.Equal(4, tip.Height);
        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task Given_FollowersThatNeverArrive_When_Waiting_Then_TheTimeoutListsEveryFollowersState()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();
        var chain = Chain(bitcoin);
        await chain.MineAsync(5, ct);
        ChainFollower[] followers =
        [
            ChainFollower.AtHeight("slow", _ => Task.FromResult(3L)),
            ChainFollower.FromReport("lnd", _ => Task.FromResult(new FollowerReport(5, Synced: false))),
            ChainFollower.AtHeight("down", _ => throw new InvalidOperationException("connection refused")),
            ChainFollower.Bitcoind("peer", bitcoin)
        ];

        // Act
        var e = await Assert.ThrowsAsync<TimeoutException>(() => chain.WaitAllAtTipAsync(followers, ct, s_short));

        // Assert
        Assert.Contains("tip 5", e.Message);
        Assert.Contains("slow 3", e.Message);
        Assert.Contains("lnd 5 (not synced)", e.Message);
        Assert.Contains("down unreachable: connection refused", e.Message);
        Assert.Contains("peer 5", e.Message);
    }

    [Fact]
    public async Task Given_ATransactionBroadcastLater_When_WaitingForTheMempool_Then_ItIsFound()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();
        var chain = Chain(bitcoin);
        bitcoin.OnStatusPoll = poll =>
        {
            if (poll == 3)
                bitcoin.Broadcast();
        };

        // Act
        var status = await chain.WaitForMempoolAsync("t1", ct);

        // Assert
        Assert.Equal(TxState.InMempool, status.State);
    }

    [Fact]
    public async Task Given_AnUnminedTransaction_When_WaitingForAConfirmation_Then_TheTimeoutSaysWhereItIs()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();
        var txId = bitcoin.Broadcast();

        // Act
        var e = await Assert.ThrowsAsync<TimeoutException>(
            () => Chain(bitcoin).WaitForConfirmationAsync(txId, 1, ct, s_short));

        // Assert
        Assert.Contains($"{txId} 1 confirmation(s)", e.Message);
        Assert.Contains("last InMempool, 0 confirmation(s)", e.Message);
    }

    [Fact]
    public async Task Given_ATransactionInTheMempool_When_MinedUntilConfirmed_Then_ExactlyTheMissingBlocksAreMined()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();
        var chain = Chain(bitcoin);
        await chain.MineAsync(10, ct);
        var txId = await chain.SendAsync("addr9", 1_000, ct, 2m);

        // Act
        var status = await chain.MineUntilConfirmedAsync(txId, 3, ct);
        var again = await chain.MineUntilConfirmedAsync(txId, 2, ct);

        // Assert
        Assert.Equal(TxState.Confirmed, status.State);
        Assert.Equal(11, status.BlockHeight);
        Assert.Equal(3, status.Confirmations);
        Assert.Equal(13, (await chain.GetTipAsync(ct)).Height);
        Assert.Equal(status, again);
        Assert.Equal([2m], bitcoin.SendFeeRates);
    }

    [Fact]
    public async Task Given_AConfirmedTransaction_When_ReorgedWithRemine_Then_TheNewBranchIsLongerAndHoldsIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();
        var chain = Chain(bitcoin);
        await chain.MineAsync(10, ct);
        var txId = bitcoin.Broadcast();
        await chain.MineAsync(2, ct);
        var oldChain = bitcoin.ActiveChain();

        // Act
        var reorg = await chain.ReorgAsync(2, ct);

        // Assert
        Assert.Equal(10, reorg.ForkHeight);
        Assert.Equal(2, reorg.Depth);
        Assert.Equal(oldChain.Skip(11), reorg.DisconnectedHashes);
        Assert.Equal(oldChain[11], reorg.InvalidatedHash);
        Assert.Equal(12, reorg.OldTip.Height);
        Assert.Equal(13, reorg.NewTip.Height);
        Assert.Equal(3, reorg.NewHashes.Count);
        Assert.Empty(reorg.OtherInvalidatedHashes);
        Assert.Equal(oldChain.Take(11), bitcoin.ActiveChain().Take(11));
        var status = await bitcoin.GetTransactionStatusAsync(txId, ct);
        Assert.Equal(reorg.NewHashes[0], status.BlockHash);
        // Every new block pays a fresh address (no block can repeat an invalidated one)
        var addresses = bitcoin.Calls.Where(c => c.Method == "generatetoaddress").Skip(2).Select(c => c.Args[1]);
        Assert.Equal(3, addresses.Distinct().Count());
    }

    [Fact]
    public async Task Given_AConfirmedTransaction_When_ReorgedWithDrop_Then_ItIsBackInTheMempoolOnEmptyBlocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();
        var chain = Chain(bitcoin);
        await chain.MineAsync(5, ct);
        var txId = bitcoin.Broadcast();
        await chain.MineAsync(1, ct);

        // Act
        var reorg = await chain.ReorgAsync(1, ct, new ReorgOptions { Transactions = ReorgTransactions.Drop, NewBlocks = 2 });

        // Assert
        Assert.Equal(7, reorg.NewTip.Height);
        Assert.Equal(TxState.InMempool, (await bitcoin.GetTransactionStatusAsync(txId, ct)).State);
        Assert.All(reorg.NewHashes, h => Assert.Empty(bitcoin.TransactionsOf(h)));
        Assert.Equal(2, bitcoin.Calls.Count(c => c.Method == "generateblock"));
    }

    [Fact]
    public async Task Given_FirstBlockTransactions_When_Reorged_Then_TheFirstNewBlockHoldsExactlyThem()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();
        var chain = Chain(bitcoin);
        await chain.MineAsync(5, ct);
        var funding = bitcoin.Broadcast();
        var other = bitcoin.Broadcast();
        await chain.MineAsync(1, ct);

        // Act
        var reorg = await chain.ReorgAsync(1, ct, new ReorgOptions
        {
            Transactions = ReorgTransactions.Drop,
            FirstBlockTransactions = [funding]
        });

        // Assert
        Assert.Equal([funding], bitcoin.TransactionsOf(reorg.NewHashes[0]));
        Assert.Empty(bitcoin.TransactionsOf(reorg.NewHashes[1]));
        Assert.Equal([other], bitcoin.Mempool);
    }

    [Fact]
    public async Task Given_AReconsideredShorterBranch_When_ReorgedAgain_Then_ItIsInvalidatedTooAndTheBranchGrowsOnTheFork()
    {
        // Arrange: 6 blocks, a 1-deep reorg onto 2 empty blocks, the old branch reconsidered (shorter: stays inactive)
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();
        var chain = Chain(bitcoin);
        await chain.MineAsync(5, ct);
        var txId = bitcoin.Broadcast();
        await chain.MineAsync(1, ct);
        var first = await chain.ReorgAsync(1, ct, new ReorgOptions { Transactions = ReorgTransactions.Drop });
        var afterReconsider = await chain.ReconsiderAsync(first, ct);
        Assert.Equal(first.NewTip, afterReconsider);

        // Act: invalidating the newer branch would bring the reconsidered one back
        var second = await chain.ReorgAsync(2, ct);

        // Assert
        Assert.Equal(5, second.ForkHeight);
        Assert.Equal([first.InvalidatedHash], second.OtherInvalidatedHashes);
        Assert.Equal(8, second.NewTip.Height);
        Assert.Equal(second.NewHashes, bitcoin.ActiveChain().Skip(6));
        Assert.Equal(second.NewHashes[0], (await bitcoin.GetTransactionStatusAsync(txId, ct)).BlockHash);
    }

    [Fact]
    public async Task Given_AShorterNewBranch_When_Reconsidered_Then_TheOldBranchIsActiveAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();
        var chain = Chain(bitcoin);
        await chain.MineAsync(8, ct);
        var oldTip = await chain.GetTipAsync(ct);
        var reorg = await chain.ReorgAsync(3, ct, new ReorgOptions { NewBlocks = 1 });
        Assert.Equal(6, reorg.NewTip.Height);

        // Act
        var tip = await chain.ReconsiderAsync(reorg, ct);

        // Assert
        Assert.Equal(oldTip, tip);
        Assert.Contains(bitcoin.Calls, c => c.Method == "reconsiderblock" && (string?)c.Args[0] == reorg.InvalidatedHash);
    }

    [Fact]
    public async Task Given_ADepthAboveTheHeight_When_Reorged_Then_ItThrowsBeforeTouchingTheChain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();
        var chain = Chain(bitcoin);
        await chain.MineAsync(2, ct);

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => chain.ReorgAsync(3, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => chain.ReorgAsync(0, ct));
        Assert.DoesNotContain(bitcoin.Calls, c => c.Method == "invalidateblock");
    }

    [Fact]
    public async Task Given_ARate_When_SeedingFeeEstimates_Then_ConfirmedOutputsAndRatedSendsGoInEveryBlock()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore { SendsBeforeEstimate = 12 };
        var chain = Chain(bitcoin);
        await chain.MineAsync(101, ct);
        var options = new FeeSeedOptions { Blocks = 4, TransactionsPerBlock = 3 };

        // Act
        var estimate = await chain.SeedFeeEstimatesAsync(12m, ct, options);

        // Assert
        Assert.Equal(12m, estimate.SatPerVb);
        Assert.Equal(2, estimate.Blocks);
        var (_, fanOutArgs) = Assert.Single(bitcoin.Calls, c => c.Method == "sendmany");
        Assert.Equal(6, fanOutArgs[0]);
        Assert.Equal(12m, fanOutArgs[1]);
        Assert.Equal(12, bitcoin.SendFeeRates.Count);
        Assert.All(bitcoin.SendFeeRates, r => Assert.Equal(12m, r));
        Assert.Equal(101 + 1 + 4, (await chain.GetTipAsync(ct)).Height);
        Assert.Empty(bitcoin.Mempool);
    }

    [Fact]
    public async Task Given_AnEstimatorThatNeverAnswers_When_Seeding_Then_ItSaysWhatItTried()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore { SendsBeforeEstimate = null };

        // Act
        var e = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Chain(bitcoin).SeedFeeEstimatesAsync(5m, ct, new FeeSeedOptions { Blocks = 2, TransactionsPerBlock = 1 }));

        // Assert
        Assert.Contains("No fee estimate at target 2 after 2 block(s) of 1 transaction(s) at 5 sat/vB", e.Message);
    }

    [Fact]
    public async Task Given_ARateBelowOneSatPerVb_When_Seeding_Then_ItIsRefused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Chain(new FakeBitcoinCore()).SeedFeeEstimatesAsync(0.5m, ct));
    }

    [Fact]
    public async Task Given_AWalletRate_When_Set_Then_SetTxFeeGetsIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore();

        // Act
        await Chain(bitcoin).SetWalletFeeRateAsync(4m, ct);

        // Assert
        Assert.Contains(bitcoin.Calls, c => c.Method == "settxfee" && (decimal?)c.Args[0] == 4m);
    }

    [Fact]
    public async Task Given_ABitcoindWithoutSetTxFee_When_SettingTheWalletRate_Then_ItSaysWhatToDoInstead()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bitcoin = new FakeBitcoinCore { NoSetTxFee = true };

        // Act
        var e = await Assert.ThrowsAsync<NotSupportedException>(() => Chain(bitcoin).SetWalletFeeRateAsync(4m, ct));

        // Assert
        Assert.Contains("Bitcoin Core 31", e.Message);
        Assert.IsType<BitcoinRpcException>(e.InnerException);
    }

    [Fact]
    public async Task Given_ALog_When_Mining_Then_ItIsWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var lines = new List<string>();
        var chain = new RegtestChain(new FakeBitcoinCore(), new RegtestChainOptions { Log = lines.Add });

        // Act
        await chain.MineAsync(2, ct);

        // Assert
        Assert.Contains(lines, l => l.StartsWith("[nltg-chain] mined 2 block(s)", StringComparison.Ordinal));
    }
}