using System.Diagnostics.CodeAnalysis;
using NBitcoin;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Gossip;

using Bitcoin.Wallet.Interfaces;

/// <summary>
/// <see cref="FakeBitcoinChain"/> behind <see cref="IBitcoinChainService"/> with call counters and fault hooks for the
/// funding output lookup tests.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class InstrumentedChain(FakeBitcoinChain inner) : IBitcoinChainService
{
    private int _blockTxIdCalls;
    private int _unspentOutputCalls;
    private int _tipCalls;

    public FakeBitcoinChain Inner => inner;

    public int BlockTxIdCalls => Volatile.Read(ref _blockTxIdCalls);
    public int UnspentOutputCalls => Volatile.Read(ref _unspentOutputCalls);
    public int TipCalls => Volatile.Read(ref _tipCalls);

    /// <summary>When set, the txid lists of these heights are unavailable (a pruned node).</summary>
    public HashSet<uint> PrunedHeights { get; } = [];

    /// <summary>When set, <see cref="GetCurrentBlockHeightAsync"/> throws it (bitcoind down).</summary>
    public Exception? TipFailure { get; set; }

    /// <summary>When set, awaited by <see cref="GetCurrentBlockHeightAsync"/> before it answers.</summary>
    public Func<Task>? BeforeTip { get; set; }

    /// <summary>When set, the height <see cref="GetUnspentOutputAsync"/> reports is replaced by its result.</summary>
    public Func<uint, uint>? ReportedOutputHeight { get; set; }

    public Task<uint256> SendTransactionAsync(Transaction transaction) => inner.SendTransactionAsync(transaction);

    public Task<Transaction?> GetTransactionAsync(uint256 txId) => inner.GetTransactionAsync(txId);

    public async Task<uint> GetCurrentBlockHeightAsync()
    {
        Interlocked.Increment(ref _tipCalls);
        if (BeforeTip is not null)
            await BeforeTip();
        if (TipFailure is not null)
            throw TipFailure;

        return await inner.GetCurrentBlockHeightAsync();
    }

    public Task<Block?> GetBlockAsync(uint height) => inner.GetBlockAsync(height);

    public Task<uint256> GetBlockHashAsync(uint height) => inner.GetBlockHashAsync(height);

    public Task<uint> GetTransactionConfirmationsAsync(uint256 txId) => inner.GetTransactionConfirmationsAsync(txId);

    public Task<Block?> GetBlockAsync(uint256 blockHash) => inner.GetBlockAsync(blockHash);

    public async Task<(TxOut Output, uint Height)?> GetUnspentOutputAsync(OutPoint outPoint)
    {
        Interlocked.Increment(ref _unspentOutputCalls);
        var result = await inner.GetUnspentOutputAsync(outPoint);
        if (result is { } found && ReportedOutputHeight is not null)
            return (found.Output, ReportedOutputHeight(found.Height));

        return result;
    }

    public Task<(uint256 BlockHash, IReadOnlyList<uint256> TxIds)?> GetBlockTxIdsAsync(uint height)
    {
        Interlocked.Increment(ref _blockTxIdCalls);
        if (PrunedHeights.Contains(height))
            return Task.FromResult<(uint256 BlockHash, IReadOnlyList<uint256> TxIds)?>(null);

        // The interface's default implementation over the fake's blocks
        return ((IBitcoinChainService)inner).GetBlockTxIdsAsync(height);
    }
}