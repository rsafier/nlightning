using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Gossip;

using Bitcoin.Gossip;
using Bitcoin.Wallet.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Money;
using Domain.Onchain.Events;
using Domain.Onchain.Interfaces;

/// <summary>
/// SCID → funding output (BOLT 7 plan §3.4, G2-T2) over a fake chain: block height → txid at the index → unspent
/// output, the P2WSH 2-of-2 of the two bitcoin keys, the amount, reorgs, the LRU of txid lists and the limits.
/// </summary>
public class FundingOutputLookupTests
{
    private const uint FundingHeight = 101;
    private const long FundingSatoshis = 1_000_000;

    private static readonly Key s_bitcoinKey1 = new(Enumerable.Repeat((byte)0x31, 32).ToArray());
    private static readonly Key s_bitcoinKey2 = new(Enumerable.Repeat((byte)0x32, 32).ToArray());
    private static readonly Key s_otherKey = new(Enumerable.Repeat((byte)0x33, 32).ToArray());

    private readonly InstrumentedChain _chain;
    private readonly Transaction _fundingTx;

    /// <summary>Tip 100, then block 101 = [coinbase, funding tx (vout 0 P2WPKH change, vout 1 the 2-of-2)].</summary>
    public FundingOutputLookupTests()
    {
        _chain = new InstrumentedChain(new FakeBitcoinChain());
        _fundingTx = CreateFundingTx(s_bitcoinKey1.PubKey, s_bitcoinKey2.PubKey, FundingSatoshis);
        _chain.Inner.Mine(_fundingTx);
    }

    private static ShortChannelId FundingScid => new(FundingHeight, 1, 1);

    [Fact]
    public async Task Given_UnspentFundingOutput_When_Lookup_Then_FoundWithAmountScriptAndDepth()
    {
        // Arrange: 5 blocks on top, so the funding block has 6 confirmations
        for (var i = 0; i < 5; i++)
            _chain.Inner.Mine();
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.Found, result.Status);
        Assert.Equal(_fundingTx.GetHash().ToBytes(), (byte[])result.TransactionId!.Value);
        Assert.Equal(LightningMoney.Satoshis(FundingSatoshis), result.Amount);
        Assert.Equal(_fundingTx.Outputs[1].ScriptPubKey.ToBytes(), result.ScriptPubKey);
        Assert.Equal(6u, result.Confirmations);
        Assert.False(result.IsTransient);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_BitcoinKeysInEitherOrder_When_Verify_Then_Found(bool swapped)
    {
        // Arrange: BOLT 3 sorts the keys, whatever order the announcement names them in
        using var lookup = CreateLookup();
        var key1 = Compact(swapped ? s_bitcoinKey2.PubKey : s_bitcoinKey1.PubKey);
        var key2 = Compact(swapped ? s_bitcoinKey1.PubKey : s_bitcoinKey2.PubKey);

        // Act
        var result = await lookup.VerifyAsync(FundingScid, key1, key2, LightningMoney.Satoshis(FundingSatoshis),
                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.Found, result.Status);
        Assert.Equal(1u, result.Confirmations);
    }

    [Fact]
    public void Given_TwoKeys_When_CreatingFundingScript_Then_P2WshOfSortedTwoOfTwo()
    {
        // Arrange: the witness script written out by hand, keys in lexicographic order
        var keys = new[] { s_bitcoinKey1.PubKey.ToBytes(), s_bitcoinKey2.PubKey.ToBytes() }
                  .OrderBy(k => Convert.ToHexString(k), StringComparer.Ordinal).ToArray();
        byte[] witnessScript = [0x52, 0x21, .. keys[0], 0x21, .. keys[1], 0x52, 0xAE];
        byte[] expected = [0x00, 0x20, .. System.Security.Cryptography.SHA256.HashData(witnessScript)];

        // Act
        var script = FundingOutputLookup.TryCreateFundingScriptPubKey(Compact(s_bitcoinKey2.PubKey),
                                                                      Compact(s_bitcoinKey1.PubKey));

        // Assert
        Assert.Equal(expected, script);
    }

    [Fact]
    public async Task Given_OtherBitcoinKey_When_Verify_Then_ScriptMismatch()
    {
        // Arrange
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.VerifyAsync(FundingScid, Compact(s_bitcoinKey1.PubKey), Compact(s_otherKey.PubKey),
                                              cancellationToken: TestContext.Current.CancellationToken);

        // Assert: the output is reported, with the reason it is not the channel's
        Assert.Equal(FundingOutputStatus.ScriptMismatch, result.Status);
        Assert.Equal(LightningMoney.Satoshis(FundingSatoshis), result.Amount);
    }

    [Fact]
    public async Task Given_ScidOfNonMultisigOutput_When_Verify_Then_ScriptMismatch()
    {
        // Arrange: vout 0 is the P2WPKH change
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.VerifyAsync(new ShortChannelId(FundingHeight, 1, 0), Compact(s_bitcoinKey1.PubKey),
                                              Compact(s_bitcoinKey2.PubKey),
                                              cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.ScriptMismatch, result.Status);
    }

    [Fact]
    public async Task Given_KeyNotOnCurve_When_Verify_Then_ScriptMismatch()
    {
        // Arrange
        using var lookup = CreateLookup();
        var notAPoint = new byte[33];
        notAPoint[0] = 0x02;

        // Act
        var result = await lookup.VerifyAsync(FundingScid, Compact(s_bitcoinKey1.PubKey), new CompactPubKey(notAPoint),
                                              cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.ScriptMismatch, result.Status);
    }

    [Fact]
    public async Task Given_OtherExpectedAmount_When_Verify_Then_AmountMismatch()
    {
        // Arrange
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.VerifyAsync(FundingScid, Compact(s_bitcoinKey1.PubKey),
                                              Compact(s_bitcoinKey2.PubKey), LightningMoney.Satoshis(999_999),
                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.AmountMismatch, result.Status);
        Assert.Equal(LightningMoney.Satoshis(FundingSatoshis), result.Amount);
    }

    [Fact]
    public async Task Given_ZeroValueFundingOutput_When_Verify_Then_AmountMismatch()
    {
        // Arrange: a 2-of-2 with 0 sat in block 102, tx index 1
        var zeroTx = CreateFundingTx(s_bitcoinKey1.PubKey, s_bitcoinKey2.PubKey, 0);
        _chain.Inner.Mine(zeroTx);
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.VerifyAsync(new ShortChannelId(FundingHeight + 1, 1, 1),
                                              Compact(s_bitcoinKey1.PubKey), Compact(s_bitcoinKey2.PubKey),
                                              cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.AmountMismatch, result.Status);
    }

    [Fact]
    public async Task Given_TransactionIndexOutOfRange_When_Lookup_Then_TransactionIndexOutOfRange()
    {
        // Arrange: block 101 has 2 transactions
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.LookupAsync(new ShortChannelId(FundingHeight, 2, 0),
                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.TransactionIndexOutOfRange, result.Status);
        Assert.Equal(0, _chain.UnspentOutputCalls);
    }

    [Fact]
    public async Task Given_OutputIndexOutOfRange_When_Lookup_Then_OutputSpentOrMissing()
    {
        // Arrange
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.LookupAsync(new ShortChannelId(FundingHeight, 1, 2),
                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.OutputSpentOrMissing, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_SpentFundingOutput_When_Lookup_Then_OutputSpentOrMissing(bool mined)
    {
        // Arrange: a close in the mempool counts as spent (gettxout with the mempool), and in a block too
        var close = Network.RegTest.CreateTransaction();
        close.Inputs.Add(new OutPoint(_fundingTx, 1));
        close.Outputs.Add(Money.Satoshis(FundingSatoshis - 1_000), new Key().PubKey.WitHash.ScriptPubKey);
        await _chain.SendTransactionAsync(close);
        if (mined)
            _chain.Inner.Mine();
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.OutputSpentOrMissing, result.Status);
    }

    [Fact]
    public async Task Given_HeightAboveTip_When_Lookup_Then_BlockNotFound()
    {
        // Arrange
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.LookupAsync(new ShortChannelId(FundingHeight + 1, 1, 0),
                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.BlockNotFound, result.Status);
        Assert.Equal(0, _chain.BlockTxIdCalls);
    }

    [Fact]
    public async Task Given_PrunedBlock_When_Lookup_Then_BlockUnavailable()
    {
        // Arrange
        _chain.PrunedHeights.Add(FundingHeight);
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert: not cached, so the block is asked for again next time
        Assert.Equal(FundingOutputStatus.BlockUnavailable, result.Status);
        Assert.False(result.IsTransient);
        Assert.Empty(lookup.CachedHeights);
    }

    [Fact]
    public async Task Given_BitcoindDown_When_Lookup_Then_ChainUnavailableWithoutThrowing()
    {
        // Arrange
        _chain.TipFailure = new HttpRequestException("connection refused");
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.ChainUnavailable, result.Status);
        Assert.True(result.IsTransient);
    }

    [Fact]
    public async Task Given_CancelledToken_When_Lookup_Then_Throws()
    {
        // Arrange
        using var lookup = CreateLookup();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.LookupAsync(FundingScid, cts.Token));
    }

    [Fact]
    public async Task Given_FundingBlockReorgedOutWithoutNotification_When_Lookup_Then_NewBlockIsRead()
    {
        // Arrange: the txid list of block 101 is cached, then 101 is replaced by an empty block
        using var lookup = CreateLookup();
        var ct = TestContext.Current.CancellationToken;
        Assert.True((await lookup.LookupAsync(FundingScid, ct)).IsFound);
        _chain.Inner.Reorg(FundingHeight - 1, 2);

        // Act: getblockhash names another block, so the cached list is not used
        var result = await lookup.LookupAsync(FundingScid, ct);

        // Assert
        Assert.Equal(FundingOutputStatus.TransactionIndexOutOfRange, result.Status);
        Assert.Equal(2, _chain.BlockTxIdCalls);
    }

    [Fact]
    public async Task Given_FundingTxMovedToAnotherHeight_When_LookupOldScid_Then_NotFoundThere()
    {
        // Arrange: the reorg puts the funding tx into block 100 (index 1) and block 101 is empty
        using var lookup = CreateLookup();
        var ct = TestContext.Current.CancellationToken;
        _chain.Inner.Reorg(FundingHeight - 2, 3, _fundingTx);

        // Act
        var oldScid = await lookup.LookupAsync(FundingScid, ct);
        var newScid = await lookup.LookupAsync(new ShortChannelId(FundingHeight - 1, 1, 1), ct);

        // Assert
        Assert.Equal(FundingOutputStatus.TransactionIndexOutOfRange, oldScid.Status);
        Assert.Equal(FundingOutputStatus.Found, newScid.Status);
        Assert.Equal(3u, newScid.Confirmations);
    }

    [Fact]
    public async Task Given_BlockDisconnected_When_Raised_Then_CachedHeightsAtOrAboveDropped()
    {
        // Arrange: blocks 99, 100 and 101 cached
        var watcher = new Mock<IOutpointWatcher>();
        using var lookup = CreateLookup(outpointWatcher: watcher.Object);
        var ct = TestContext.Current.CancellationToken;
        foreach (var height in new uint[] { 99, 100, FundingHeight })
            await lookup.LookupAsync(new ShortChannelId(height, 0, 0), ct);
        Assert.Equal([FundingHeight, 100u, 99u], lookup.CachedHeights);

        // Act
        watcher.Raise(w => w.OnBlockDisconnected += null,
                      new BlockDisconnectedEventArgs(100, new Hash(new byte[32]), 99));

        // Assert
        Assert.Equal([99u], lookup.CachedHeights);
    }

    [Fact]
    public async Task Given_Disposed_When_BlockDisconnected_Then_Unsubscribed()
    {
        // Arrange
        var watcher = new Mock<IOutpointWatcher>();
        var lookup = CreateLookup(outpointWatcher: watcher.Object);
        await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Act
        lookup.Dispose();
        watcher.Raise(w => w.OnBlockDisconnected += null,
                      new BlockDisconnectedEventArgs(FundingHeight, new Hash(new byte[32]), FundingHeight - 1));

        // Assert
        Assert.Equal([FundingHeight], lookup.CachedHeights);
    }

    [Fact]
    public async Task Given_OutputReportedAtOtherHeight_When_Lookup_Then_RetriedOnceThenChainMoved()
    {
        // Arrange: gettxout keeps naming another height (the chain moving under the lookup)
        _chain.ReportedOutputHeight = h => h + 1;
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert: the cached list was dropped and read again for the retry
        Assert.Equal(FundingOutputStatus.ChainMoved, result.Status);
        Assert.True(result.IsTransient);
        Assert.Equal(2, _chain.UnspentOutputCalls);
        Assert.Equal(2, _chain.BlockTxIdCalls);
    }

    [Fact]
    public async Task Given_OutputReportedAtOtherHeightOnce_When_Lookup_Then_RetryFindsIt()
    {
        // Arrange: only the first gettxout is off (the tip moved between gettxout and getblockcount)
        var calls = 0;
        _chain.ReportedOutputHeight = h => Interlocked.Increment(ref calls) == 1 ? h + 1 : h;
        using var lookup = CreateLookup();

        // Act
        var result = await lookup.LookupAsync(FundingScid, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingOutputStatus.Found, result.Status);
    }

    [Fact]
    public async Task Given_SameHeightTwice_When_Lookup_Then_TxIdListFetchedOnce()
    {
        // Arrange
        using var lookup = CreateLookup();
        var ct = TestContext.Current.CancellationToken;

        // Act
        await lookup.LookupAsync(FundingScid, ct);
        await lookup.LookupAsync(new ShortChannelId(FundingHeight, 1, 0), ct);

        // Assert
        Assert.Equal(1, _chain.BlockTxIdCalls);
        Assert.Equal(2, _chain.UnspentOutputCalls);
    }

    [Fact]
    public async Task Given_CacheFull_When_NewHeight_Then_LeastRecentlyUsedEvicted()
    {
        // Arrange: room for 2 heights
        using var lookup = CreateLookup(new FundingOutputLookupOptions { ChainLookupCacheHeights = 2 });
        var ct = TestContext.Current.CancellationToken;
        await lookup.LookupAsync(new ShortChannelId(99, 0, 0), ct);
        await lookup.LookupAsync(new ShortChannelId(100, 0, 0), ct);
        await lookup.LookupAsync(new ShortChannelId(99, 0, 0), ct); // 99 is now the most recent
        Assert.Equal(2, _chain.BlockTxIdCalls);

        // Act: 101 evicts 100, not 99
        await lookup.LookupAsync(FundingScid, ct);
        await lookup.LookupAsync(new ShortChannelId(99, 0, 0), ct);
        await lookup.LookupAsync(new ShortChannelId(100, 0, 0), ct);

        // Assert: 99 hit, 100 fetched again (evicting 101)
        Assert.Equal(4, _chain.BlockTxIdCalls);
        Assert.Equal([100u, 99u], lookup.CachedHeights);
    }

    [Fact]
    public async Task Given_RateOfOnePerSecond_When_TwoLookups_Then_SecondWaitsForTheClock()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        using var lookup = CreateLookup(new FundingOutputLookupOptions { ChainLookupsPerSecond = 1 }, clock);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var first = await lookup.LookupAsync(FundingScid, ct);
        var second = lookup.LookupAsync(FundingScid, ct);
        var waitedBeforeAdvance = !second.IsCompleted && clock.PendingTimers == 1 && _chain.TipCalls == 1;
        clock.Advance(TimeSpan.FromMilliseconds(999));
        var waitedAt999Ms = !second.IsCompleted;
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var result = await second.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert
        Assert.True(first.IsFound);
        Assert.True(waitedBeforeAdvance);
        Assert.True(waitedAt999Ms);
        Assert.True(result.IsFound);
        Assert.Equal(2, _chain.TipCalls);
    }

    [Fact]
    public async Task Given_ConcurrencyOfOne_When_TwoLookups_Then_SecondStartsAfterFirst()
    {
        // Arrange: the first lookup is held inside bitcoind's first call
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _chain.BeforeTip = () =>
        {
            entered.TrySetResult();
            return gate.Task;
        };
        using var lookup = CreateLookup(new FundingOutputLookupOptions { ChainLookupConcurrency = 1 });
        var ct = TestContext.Current.CancellationToken;

        // Act
        var first = lookup.LookupAsync(FundingScid, ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var second = lookup.LookupAsync(FundingScid, ct);
        await Task.Delay(100, ct);
        var tipCallsWhileHeld = _chain.TipCalls;
        gate.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert
        Assert.Equal(1, tipCallsWhileHeld);
        Assert.All(results, r => Assert.True(r.IsFound));
        Assert.Equal(2, _chain.TipCalls);
    }

    [Fact]
    public void Given_InvalidOptions_When_Constructed_Then_Throws()
    {
        // Arrange
        var options = new FundingOutputLookupOptions
        {
            ChainLookupConcurrency = 0,
            ChainLookupsPerSecond = 0,
            ChainLookupCacheHeights = 0
        };

        // Act & Assert
        Assert.Equal(3, options.GetValidationErrors().Count);
        Assert.Throws<ArgumentException>(() => CreateLookup(options));
    }

    [Fact]
    public async Task Given_FakeChain_When_DefaultGetBlockTxIds_Then_BlockOrderAndNullAboveTip()
    {
        // Arrange: the interface default reads the whole block
        IBitcoinChainService chain = _chain.Inner;

        // Act
        var block = await chain.GetBlockTxIdsAsync(FundingHeight);
        var aboveTip = await chain.GetBlockTxIdsAsync(FundingHeight + 1);

        // Assert
        Assert.NotNull(block);
        Assert.Equal(_chain.Inner[FundingHeight].GetHash(), block.Value.BlockHash);
        Assert.Equal([_chain.Inner[FundingHeight].Transactions[0].GetHash(), _fundingTx.GetHash()],
                     block.Value.TxIds);
        Assert.Null(aboveTip);
    }

    private FundingOutputLookup CreateLookup(FundingOutputLookupOptions? options = null,
                                             TimeProvider? timeProvider = null,
                                             IOutpointWatcher? outpointWatcher = null) =>
        new(_chain, NullLogger<FundingOutputLookup>.Instance,
            Microsoft.Extensions.Options.Options.Create(options ?? new FundingOutputLookupOptions()), timeProvider,
            outpointWatcher);

    private static Transaction CreateFundingTx(PubKey key1, PubKey key2, long satoshis)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0));
        tx.Outputs.Add(Money.Satoshis(50_000), new Key().PubKey.WitHash.ScriptPubKey);
        var ordered = new[] { key1, key2 }.OrderBy(k => k, PubKeyComparer.Instance).ToArray();
        tx.Outputs.Add(Money.Satoshis(satoshis),
                       PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, ordered).WitHash.ScriptPubKey);
        return tx;
    }

    private static CompactPubKey Compact(PubKey key) => key.ToBytes();
}