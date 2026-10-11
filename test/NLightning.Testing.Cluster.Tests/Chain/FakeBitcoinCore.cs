using Newtonsoft.Json.Linq;

namespace NLightning.Testing.Cluster.Tests.Chain;

using Cluster.Chain;
using Cluster.Nodes.BitcoinCore.Rpc;

/// <summary>
/// An in-memory regtest bitcoind for the chain helpers: a block tree with invalidation, the most-work (highest) valid
/// branch active, a mempool that takes back the transactions of disconnected blocks, and a fee estimator that answers
/// once enough transactions were sent at a rate.
/// </summary>
internal sealed class FakeBitcoinCore : IBitcoinCoreRpc
{
    private readonly Dictionary<string, Block> _blocks = new(StringComparer.Ordinal);
    private readonly List<string> _mempool = [];
    private readonly HashSet<string> _invalid = new(StringComparer.Ordinal);
    private int _nextBlock;
    private int _nextTx;
    private int _nextAddress;

    public FakeBitcoinCore()
    {
        _blocks["b0"] = new Block("b0", null, 0, []);
        Tip = _blocks["b0"];
    }

    private Block Tip { get; set; }

    /// <summary>Every call, in order: method name and its arguments.</summary>
    public List<(string Method, object?[] Args)> Calls { get; } = [];

    /// <summary>Sends needed before <see cref="EstimateSmartFeeAsync"/> answers (null: never).</summary>
    public int? SendsBeforeEstimate { get; set; } = 10;

    /// <summary>Fee rates of the sends so far.</summary>
    public List<decimal?> SendFeeRates { get; } = [];

    /// <summary>Runs before every <see cref="GetTransactionStatusAsync"/> (to make a transaction appear later).</summary>
    public Action<int>? OnStatusPoll { get; set; }

    private int _statusPolls;

    public string Description => "fake";

    public IReadOnlyList<string> ActiveChain()
    {
        var hashes = new List<string>();
        for (var block = Tip; block is not null; block = block.Parent is null ? null : _blocks[block.Parent])
            hashes.Add(block.Hash);
        hashes.Reverse();
        return hashes;
    }

    public IReadOnlyList<string> Mempool => _mempool;

    public IReadOnlyList<string> TransactionsOf(string blockHash) => _blocks[blockHash].Transactions;

    /// <summary>Adds a transaction to the mempool, as a peer's broadcast.</summary>
    public string Broadcast()
    {
        var txId = $"t{++_nextTx}";
        _mempool.Add(txId);
        return txId;
    }

    public Task<JToken> CallAsync(string method, IReadOnlyDictionary<string, object?>? namedArgs,
                                  CancellationToken cancellationToken) =>
        throw new NotSupportedException(method);

    public Task<ChainInfo> GetChainInfoAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ChainInfo("regtest", Tip.Height, Tip.Height, Tip.Hash, false));

    public Task<ChainTip> GetTipAsync(CancellationToken cancellationToken)
    {
        Calls.Add(("tip", []));
        return Task.FromResult(new ChainTip(Tip.Height, Tip.Hash));
    }

    public Task<string> GetBlockHashAsync(long height, CancellationToken cancellationToken) =>
        Task.FromResult(ActiveChain()[(int)height]);

    public Task<BlockHeaderInfo> GetBlockHeaderAsync(string blockHash, CancellationToken cancellationToken)
    {
        var block = _blocks[blockHash];
        var active = ActiveChain().Contains(blockHash);
        return Task.FromResult(new BlockHeaderInfo(blockHash, block.Height, active ? Tip.Height - block.Height + 1 : -1,
                                                   block.Parent));
    }

    public Task<IReadOnlyList<string>> GenerateToAddressAsync(int blocks, string address,
                                                              CancellationToken cancellationToken)
    {
        Calls.Add(("generatetoaddress", [blocks, address]));
        var hashes = new List<string>();
        for (var i = 0; i < blocks; i++)
        {
            hashes.Add(AddBlock([.. _mempool]));
            _mempool.Clear();
        }

        return Task.FromResult<IReadOnlyList<string>>(hashes);
    }

    public Task<string> GenerateBlockAsync(string address, IReadOnlyList<string> transactions,
                                           CancellationToken cancellationToken)
    {
        Calls.Add(("generateblock", [address, transactions.ToArray()]));
        var hash = AddBlock([.. transactions]);
        _mempool.RemoveAll(transactions.Contains);
        return Task.FromResult(hash);
    }

    public Task<string> GetNewAddressAsync(CancellationToken cancellationToken)
    {
        Calls.Add(("getnewaddress", []));
        return Task.FromResult($"addr{++_nextAddress}");
    }

    public Task<string> SendToAddressAsync(string address, long amountSat, decimal? feeRateSatPerVb,
                                           CancellationToken cancellationToken)
    {
        Calls.Add(("sendtoaddress", [address, amountSat, feeRateSatPerVb]));
        SendFeeRates.Add(feeRateSatPerVb);
        return Task.FromResult(Broadcast());
    }

    public Task<string> SendManyAsync(IReadOnlyDictionary<string, long> amountsSat, decimal? feeRateSatPerVb,
                                      CancellationToken cancellationToken)
    {
        Calls.Add(("sendmany", [amountsSat.Count, feeRateSatPerVb]));
        return Task.FromResult(Broadcast());
    }

    public Task<long> GetTrustedBalanceSatAsync(CancellationToken cancellationToken) => Task.FromResult(50_0000_0000L);

    public Task<IReadOnlyList<string>> GetRawMempoolAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([.. _mempool]);

    public Task<TxStatus> GetTransactionStatusAsync(string txId, CancellationToken cancellationToken)
    {
        OnStatusPoll?.Invoke(++_statusPolls);
        if (_mempool.Contains(txId))
            return Task.FromResult(TxStatus.Mempool(txId));

        foreach (var hash in ActiveChain())
        {
            var block = _blocks[hash];
            if (block.Transactions.Contains(txId))
                return Task.FromResult(new TxStatus(txId, TxState.Confirmed, hash, block.Height,
                                                    Tip.Height - block.Height + 1));
        }

        return Task.FromResult(TxStatus.NotFound(txId));
    }

    public Task InvalidateBlockAsync(string blockHash, CancellationToken cancellationToken)
    {
        Calls.Add(("invalidateblock", [blockHash]));
        var before = ActiveChain();
        _invalid.Add(blockHash);
        SelectTip(before);
        return Task.CompletedTask;
    }

    public Task ReconsiderBlockAsync(string blockHash, CancellationToken cancellationToken)
    {
        Calls.Add(("reconsiderblock", [blockHash]));
        var before = ActiveChain();
        _invalid.Remove(blockHash);
        SelectTip(before);
        return Task.CompletedTask;
    }

    /// <summary>Answers <c>settxfee</c> like Bitcoin Core 31 (method not found).</summary>
    public bool NoSetTxFee { get; set; }

    public Task SetTxFeeAsync(decimal satPerVb, CancellationToken cancellationToken)
    {
        Calls.Add(("settxfee", [satPerVb]));
        if (NoSetTxFee)
            throw new BitcoinRpcException("settxfee", BitcoinRpcErrorCodes.MethodNotFound, "Method not found");
        return Task.CompletedTask;
    }

    public Task<FeeEstimate?> EstimateSmartFeeAsync(int confirmationTarget, FeeEstimateMode mode,
                                                    CancellationToken cancellationToken)
    {
        Calls.Add(("estimatesmartfee", [confirmationTarget, mode]));
        var rated = SendFeeRates.Where(r => r is not null).ToList();
        return Task.FromResult(SendsBeforeEstimate is { } needed && rated.Count >= needed
                                   ? new FeeEstimate(rated[^1]!.Value, confirmationTarget)
                                   : null);
    }

    public Task<int> GetConnectionCountAsync(CancellationToken cancellationToken) => Task.FromResult(1);

    public Task AddNodeAsync(string node, string command, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnsureWalletAsync(string wallet, CancellationToken cancellationToken) => Task.CompletedTask;

    private string AddBlock(List<string> transactions)
    {
        var hash = $"b{++_nextBlock}x";
        var block = new Block(hash, Tip.Hash, Tip.Height + 1, transactions);
        _blocks[hash] = block;
        Tip = block;
        return hash;
    }

    /// <summary>The highest block whose branch is valid is the tip (first one created wins a tie, as first seen).</summary>
    private void SelectTip(IReadOnlyList<string> before)
    {
        Tip = _blocks.Values.Where(IsValid)
                     .OrderByDescending(b => b.Height)
                     .ThenBy(b => int.Parse(b.Hash.TrimStart('b').TrimEnd('x'), System.Globalization.CultureInfo.InvariantCulture))
                     .First();
        var after = ActiveChain();
        // Disconnected blocks' transactions go back to the mempool unless the new branch holds them
        var confirmed = after.SelectMany(h => _blocks[h].Transactions).ToHashSet(StringComparer.Ordinal);
        foreach (var hash in before.Except(after))
        {
            foreach (var tx in _blocks[hash].Transactions.Where(t => !confirmed.Contains(t) && !_mempool.Contains(t)))
                _mempool.Add(tx);
        }

        _mempool.RemoveAll(confirmed.Contains);
    }

    private bool IsValid(Block block)
    {
        for (var b = block; b is not null; b = b.Parent is null ? null : _blocks[b.Parent])
        {
            if (_invalid.Contains(b.Hash))
                return false;
        }

        return true;
    }

    private sealed record Block(string Hash, string? Parent, long Height, List<string> Transactions);
}