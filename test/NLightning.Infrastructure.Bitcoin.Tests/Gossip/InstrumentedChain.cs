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
    private int _confirmedUnspentOutputCalls;
    private int _tipCalls;
    private int _blockHashCalls;
    private int _headerSummaryCalls;

    public FakeBitcoinChain Inner => inner;

    public int BlockTxIdCalls => Volatile.Read(ref _blockTxIdCalls);
    public int UnspentOutputCalls => Volatile.Read(ref _unspentOutputCalls);
    public int ConfirmedUnspentOutputCalls => Volatile.Read(ref _confirmedUnspentOutputCalls);
    public int TipCalls => Volatile.Read(ref _tipCalls);
    public int BlockHashCalls => Volatile.Read(ref _blockHashCalls);
    public int HeaderSummaryCalls => Volatile.Read(ref _headerSummaryCalls);

    /// <summary>
    /// When set, the blocks of these heights are unavailable (a pruned node): their txid lists and
    /// <c>GetBlockAsync</c> answer null, only the header (<see cref="GetBlockHeaderSummaryAsync"/>) is kept.
    /// </summary>
    public HashSet<uint> PrunedHeights { get; } = [];

    /// <summary>When true, headers carry no <c>nTx</c> (0: a block the node never downloaded, e.g. assumeutxo).</summary>
    public bool UnknownHeaderTxCount { get; set; }

    /// <summary>When set, <see cref="GetCurrentBlockHeightAsync"/> throws it (bitcoind down).</summary>
    public Exception? TipFailure { get; set; }

    /// <summary>When set, awaited by <see cref="GetCurrentBlockHeightAsync"/> before it answers.</summary>
    public Func<Task>? BeforeTip { get; set; }

    /// <summary>When set, the height <see cref="GetUnspentOutputAsync"/> reports is replaced by its result.</summary>
    public Func<uint, uint>? ReportedOutputHeight { get; set; }

    /// <summary>When set, runs after <see cref="GetUnspentOutputAsync"/> read the output (the chain moving then).</summary>
    public Action? AfterUnspentOutput { get; set; }

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

    public Task<Block?> GetBlockAsync(uint height) =>
        PrunedHeights.Contains(height) ? Task.FromResult<Block?>(null) : inner.GetBlockAsync(height);

    public Task<uint256> GetBlockHashAsync(uint height)
    {
        Interlocked.Increment(ref _blockHashCalls);
        return inner.GetBlockHashAsync(height);
    }

    public Task<uint> GetTransactionConfirmationsAsync(uint256 txId) => inner.GetTransactionConfirmationsAsync(txId);

    public async Task<Block?> GetBlockAsync(uint256 blockHash)
    {
        var block = await inner.GetBlockAsync(blockHash);
        return block is not null && IsPruned(blockHash) ? null : block;
    }

    /// <summary><c>getblockheader</c>: kept for every block, pruned or not (read here from the fake's full block).</summary>
    public async Task<(uint256 MerkleRoot, int TxCount)?> GetBlockHeaderSummaryAsync(uint256 blockHash)
    {
        Interlocked.Increment(ref _headerSummaryCalls);
        var block = await inner.GetBlockAsync(blockHash);
        if (block is null)
            return null;

        return (block.Header.HashMerkleRoot, UnknownHeaderTxCount ? 0 : block.Transactions.Count);
    }

    private bool IsPruned(uint256 blockHash) =>
        PrunedHeights.Any(h => h <= inner.TipHeight && inner[h].GetHash() == blockHash);

    public async Task<(TxOut Output, uint Height)?> GetUnspentOutputAsync(OutPoint outPoint)
    {
        Interlocked.Increment(ref _unspentOutputCalls);
        var result = await inner.GetUnspentOutputAsync(outPoint);
        AfterUnspentOutput?.Invoke();
        if (result is { } found && ReportedOutputHeight is not null)
            return (found.Output, ReportedOutputHeight(found.Height));

        return result;
    }

    /// <summary><c>gettxout</c> without the mempool over the fake's blocks: a mempool spend does not count.</summary>
    public Task<(TxOut Output, uint Height)?> GetConfirmedUnspentOutputAsync(OutPoint outPoint)
    {
        Interlocked.Increment(ref _confirmedUnspentOutputCalls);
        var blocks = Enumerable.Range(0, (int)inner.TipHeight + 1).Select(h => inner[(uint)h]).ToList();
        for (var height = 0; height < blocks.Count; height++)
        {
            var tx = blocks[height].Transactions.FirstOrDefault(t => t.GetHash() == outPoint.Hash);
            if (tx is null || outPoint.N >= tx.Outputs.Count)
                continue;

            var spent = blocks.SelectMany(b => b.Transactions).SelectMany(t => t.Inputs)
                              .Any(i => i.PrevOut == outPoint);
            return Task.FromResult<(TxOut Output, uint Height)?>(spent ? null : (tx.Outputs[outPoint.N], (uint)height));
        }

        return Task.FromResult<(TxOut Output, uint Height)?>(null);
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