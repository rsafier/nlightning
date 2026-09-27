using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Gossip;

using Bitcoin.Gossip;
using Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Events;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Onchain.Interfaces;

/// <summary>
/// NL-414: a funding output spent only by a mempool transaction is looked up on chain once per block, not at every
/// retry of its announcement; NL-415: the short channel ids a lookup holds are pending for the gossip sync.
/// </summary>
public class FundingOutputLookupMempoolSpentTests
{
    private const uint FundingHeight = 101;
    private const long FundingSatoshis = 1_000_000;

    private readonly InstrumentedChain _chain = new(new FakeBitcoinChain());
    private readonly Transaction _fundingTx;

    /// <summary>Tip 100, block 101 = [coinbase, funding tx (vout 1 the 2-of-2)], and a close of it in the mempool.
    /// </summary>
    public FundingOutputLookupMempoolSpentTests()
    {
        _fundingTx = Network.RegTest.CreateTransaction();
        _fundingTx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0));
        _fundingTx.Outputs.Add(Money.Satoshis(50_000), new Key().PubKey.WitHash.ScriptPubKey);
        _fundingTx.Outputs.Add(Money.Satoshis(FundingSatoshis), new Key().PubKey.WitHash.ScriptPubKey);
        _chain.Inner.Mine(_fundingTx);

        var close = Network.RegTest.CreateTransaction();
        close.Inputs.Add(new OutPoint(_fundingTx, 1));
        close.Outputs.Add(Money.Satoshis(FundingSatoshis - 1_000), new Key().PubKey.WitHash.ScriptPubKey);
        _chain.Inner.SendTransactionAsync(close).GetAwaiter().GetResult();
    }

    private static ShortChannelId FundingScid => new(FundingHeight, 1, 1);

    [Fact]
    public async Task Given_NoBlockMonitor_When_LookedUpAgainAtTheSameTip_Then_OnlyTheTipIsAsked()
    {
        // Arrange
        using var lookup = CreateLookup();
        var ct = TestContext.Current.CancellationToken;
        await lookup.LookupAsync(FundingScid, ct);
        var (tips, unspent, confirmed, blocks) = Calls();

        // Act: the ingress retries the announcement five times within the block
        var results = new List<FundingOutputStatus>();
        for (var i = 0; i < 5; i++)
            results.Add((await lookup.LookupAsync(FundingScid, ct)).Status);

        // Assert: the same answer, from one getblockcount each instead of the four-RPC lookup
        Assert.All(results, s => Assert.Equal(FundingOutputStatus.OutputSpentInMempool, s));
        Assert.Equal(tips + 5, _chain.TipCalls);
        Assert.Equal(unspent, _chain.UnspentOutputCalls);
        Assert.Equal(confirmed, _chain.ConfirmedUnspentOutputCalls);
        Assert.Equal(blocks, _chain.BlockHashCalls + _chain.BlockTxIdCalls);
    }

    [Fact]
    public async Task Given_NoBlockMonitor_When_ABlockCame_Then_TheOutputIsLookedUpAgain()
    {
        // Arrange: a block without the close
        using var lookup = CreateLookup();
        var ct = TestContext.Current.CancellationToken;
        await lookup.LookupAsync(FundingScid, ct);
        var unspent = _chain.UnspentOutputCalls;
        _chain.Inner.Mine(false);

        // Act
        var result = await lookup.LookupAsync(FundingScid, ct);

        // Assert: asked again (still in the mempool), and kept again for the new tip
        Assert.Equal(FundingOutputStatus.OutputSpentInMempool, result.Status);
        Assert.Equal(unspent + 1, _chain.UnspentOutputCalls);
        Assert.Equal(1, lookup.MempoolSpentCount);
    }

    [Fact]
    public async Task Given_ABlockMonitor_When_LookedUpAgainBeforeItsNextBlock_Then_BitcoindIsNotAskedAtAll()
    {
        // Arrange
        var monitor = new Mock<IBlockchainMonitor>();
        using var lookup = CreateLookup(outpointWatcher: monitor.Object);
        var ct = TestContext.Current.CancellationToken;
        await lookup.LookupAsync(FundingScid, ct);
        var (tips, unspent, _, _) = Calls();

        // Act
        var again = await lookup.LookupAsync(FundingScid, ct);

        // Assert
        Assert.Equal(FundingOutputStatus.OutputSpentInMempool, again.Status);
        Assert.True(again.IsTransient);
        Assert.Equal(tips, _chain.TipCalls);
        Assert.Equal(unspent, _chain.UnspentOutputCalls);
    }

    [Fact]
    public async Task Given_ABlockMonitor_When_ItReportsTheNextBlock_Then_TheOutputIsLookedUpOnceForIt()
    {
        // Arrange: the close confirms in block 102
        var monitor = new Mock<IBlockchainMonitor>();
        using var lookup = CreateLookup(outpointWatcher: monitor.Object);
        var ct = TestContext.Current.CancellationToken;
        await lookup.LookupAsync(FundingScid, ct);
        _chain.Inner.Mine();
        var unspent = _chain.UnspentOutputCalls;

        // Act
        monitor.Raise(m => m.OnNewBlockDetected += null, new NewBlockEventArgs(102, new Hash(new byte[32])));
        var first = await lookup.LookupAsync(FundingScid, ct);
        var second = await lookup.LookupAsync(FundingScid, ct);

        // Assert: one real lookup for the block, which finds the output spent for good (nothing kept)
        Assert.Equal(FundingOutputStatus.OutputSpentOrMissing, first.Status);
        Assert.Equal(FundingOutputStatus.OutputSpentOrMissing, second.Status);
        Assert.Equal(unspent + 2, _chain.UnspentOutputCalls);
        Assert.Equal(0, lookup.MempoolSpentCount);
    }

    [Fact]
    public async Task Given_ABlockMonitorThatReportsNothing_When_TheRecheckIntervalPassed_Then_TheOutputIsLookedUpAgain()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var monitor = new Mock<IBlockchainMonitor>();
        using var lookup = CreateLookup(new FundingOutputLookupOptions
        {
            MempoolSpentRecheckInterval = TimeSpan.FromMinutes(10)
        }, clock, monitor.Object);
        var ct = TestContext.Current.CancellationToken;
        await lookup.LookupAsync(FundingScid, ct);
        var unspent = _chain.UnspentOutputCalls;

        // Act
        clock.Advance(TimeSpan.FromMinutes(9));
        await lookup.LookupAsync(FundingScid, ct);
        var afterNine = _chain.UnspentOutputCalls;
        clock.Advance(TimeSpan.FromMinutes(1));
        await lookup.LookupAsync(FundingScid, ct);

        // Assert
        Assert.Equal(unspent, afterNine);
        Assert.Equal(unspent + 1, _chain.UnspentOutputCalls);
    }

    [Fact]
    public async Task Given_TheRecheckIntervalZero_When_LookedUpAgain_Then_NothingIsKept()
    {
        // Arrange
        var monitor = new Mock<IBlockchainMonitor>();
        using var lookup = CreateLookup(new FundingOutputLookupOptions { MempoolSpentRecheckInterval = TimeSpan.Zero },
                                        outpointWatcher: monitor.Object);
        var ct = TestContext.Current.CancellationToken;
        await lookup.LookupAsync(FundingScid, ct);
        var unspent = _chain.UnspentOutputCalls;

        // Act
        await lookup.LookupAsync(FundingScid, ct);

        // Assert
        Assert.Equal(unspent + 1, _chain.UnspentOutputCalls);
        Assert.False(lookup.IsPending(FundingScid));
    }

    [Fact]
    public async Task Given_AMempoolSpentAnswer_When_AskedIfPending_Then_PendingUntilTheNextBlock()
    {
        // Arrange
        var monitor = new Mock<IBlockchainMonitor>();
        using var lookup = CreateLookup(outpointWatcher: monitor.Object);
        await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Act
        var before = lookup.IsPending(FundingScid);
        monitor.Raise(m => m.OnNewBlockDetected += null, new NewBlockEventArgs(102, new Hash(new byte[32])));
        var after = lookup.IsPending(FundingScid);

        // Assert
        Assert.True(before);
        Assert.False(after);
        Assert.False(lookup.IsPending(new ShortChannelId(FundingHeight, 1, 0)));
    }

    [Fact]
    public async Task Given_ALookupWaitingForBitcoind_When_AskedIfPending_Then_PendingUntilItAnswers()
    {
        // Arrange: bitcoind holds the lookup's first RPC
        using var lookup = CreateLookup();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _chain.BeforeTip = () =>
        {
            asked.TrySetResult();
            return release.Task;
        };
        var unspentScid = new ShortChannelId(FundingHeight, 1, 0);

        // Act
        var lookupTask = lookup.LookupAsync(unspentScid, TestContext.Current.CancellationToken);
        await asked.Task.WaitAsync(TestContext.Current.CancellationToken);
        var during = lookup.IsPending(unspentScid);
        release.SetResult();
        var result = await lookupTask;

        // Assert
        Assert.True(during);
        Assert.Equal(FundingOutputStatus.Found, result.Status);
        Assert.False(lookup.IsPending(unspentScid));
    }

    [Fact]
    public async Task Given_Disposed_When_TheMonitorReportsABlock_Then_Unsubscribed()
    {
        // Arrange
        var monitor = new Mock<IBlockchainMonitor>();
        var lookup = CreateLookup(outpointWatcher: monitor.Object);
        await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Act
        lookup.Dispose();
        monitor.Raise(m => m.OnNewBlockDetected += null, new NewBlockEventArgs(102, new Hash(new byte[32])));

        // Assert: the answer was not dropped by the block
        Assert.Equal(1, lookup.MempoolSpentCount);
    }

    private (int Tips, int Unspent, int Confirmed, int Blocks) Calls() =>
        (_chain.TipCalls, _chain.UnspentOutputCalls, _chain.ConfirmedUnspentOutputCalls,
         _chain.BlockHashCalls + _chain.BlockTxIdCalls);

    private FundingOutputLookup CreateLookup(FundingOutputLookupOptions? options = null,
                                             TimeProvider? timeProvider = null,
                                             IOutpointWatcher? outpointWatcher = null) =>
        new(_chain, NullLogger<FundingOutputLookup>.Instance,
            Microsoft.Extensions.Options.Options.Create(options ?? new FundingOutputLookupOptions()), timeProvider,
            outpointWatcher);
}