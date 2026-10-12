using NBitcoin;

namespace NLightning.Integration.Tests.Scale;

using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// An in-memory regtest chain for the channel-scale benchmark (NL-1357): blocks with distinct hashes and parent links,
/// a transaction index, a mempool and a spent-output set, so every lookup the node makes is a dictionary lookup and the
/// chain never dominates what is measured. Thread-safe (the node's monitor, peers and the test call it concurrently).
/// </summary>
internal sealed class ScaleChain : IBitcoinChainService
{
    private readonly object _gate = new();
    private readonly List<Block> _blocks = [];
    private readonly Dictionary<uint256, (Transaction Tx, uint Height)> _confirmed = [];
    private readonly Dictionary<uint256, Transaction> _mempool = [];
    private readonly HashSet<OutPoint> _spent = [];
    private uint _nonce;

    public ScaleChain(uint tipHeight)
    {
        for (var height = 0u; height <= tipHeight; height++)
            AppendBlock([]);
    }

    public uint TipHeight
    {
        get
        {
            lock (_gate)
                return (uint)(_blocks.Count - 1);
        }
    }

    public Block this[uint height]
    {
        get
        {
            lock (_gate)
                return _blocks[(int)height];
        }
    }

    /// <summary>Appends a block holding <paramref name="transactions"/> and the mempool; returns it with its height.</summary>
    public (Block Block, uint Height) Mine(IEnumerable<Transaction> transactions)
    {
        lock (_gate)
        {
            var included = transactions.ToList();
            included.AddRange(_mempool.Values.Where(m => included.All(t => t.GetHash() != m.GetHash())));
            _mempool.Clear();
            var block = AppendBlock(included);
            return (block, (uint)(_blocks.Count - 1));
        }
    }

    public Task<uint256> SendTransactionAsync(Transaction transaction)
    {
        lock (_gate)
            _mempool.TryAdd(transaction.GetHash(), transaction);
        return Task.FromResult(transaction.GetHash());
    }

    public Task<Transaction?> GetTransactionAsync(uint256 txId)
    {
        lock (_gate)
            return Task.FromResult(_confirmed.TryGetValue(txId, out var c) ? c.Tx
                                   : _mempool.GetValueOrDefault(txId));
    }

    public Task<uint> GetCurrentBlockHeightAsync() => Task.FromResult(TipHeight);

    public Task<Block?> GetBlockAsync(uint height)
    {
        lock (_gate)
            return height < _blocks.Count
                       ? Task.FromResult<Block?>(_blocks[(int)height])
                       : throw new InvalidOperationException($"No block at height {height}");
    }

    public Task<uint256> GetBlockHashAsync(uint height)
    {
        lock (_gate)
            return height < _blocks.Count
                       ? Task.FromResult(_blocks[(int)height].GetHash())
                       : throw new InvalidOperationException($"No block at height {height}");
    }

    public Task<Block?> GetBlockAsync(uint256 blockHash)
    {
        lock (_gate)
            return Task.FromResult(_blocks.FirstOrDefault(b => b.GetHash() == blockHash));
    }

    public Task<uint> GetTransactionConfirmationsAsync(uint256 txId)
    {
        lock (_gate)
            return Task.FromResult(_confirmed.TryGetValue(txId, out var c) ? (uint)_blocks.Count - c.Height : 0u);
    }

    public Task<(TxOut Output, uint Height)?> GetUnspentOutputAsync(OutPoint outPoint) => FindUnspent(outPoint, true);

    public Task<(TxOut Output, uint Height)?> GetConfirmedUnspentOutputAsync(OutPoint outPoint) =>
        FindUnspent(outPoint, false);

    public Task<IReadOnlyDictionary<OutPoint, uint256>?> GetMempoolSpendersAsync(IReadOnlyCollection<OutPoint> outPoints)
    {
        lock (_gate)
        {
            var spenders = new Dictionary<OutPoint, uint256>();
            foreach (var tx in _mempool.Values)
                foreach (var input in tx.Inputs)
                    if (outPoints.Contains(input.PrevOut))
                        spenders[input.PrevOut] = tx.GetHash();
            return Task.FromResult<IReadOnlyDictionary<OutPoint, uint256>?>(spenders);
        }
    }

    private Task<(TxOut Output, uint Height)?> FindUnspent(OutPoint outPoint, bool includeMempool)
    {
        lock (_gate)
        {
            if (!_confirmed.TryGetValue(outPoint.Hash, out var c) || outPoint.N >= c.Tx.Outputs.Count
             || _spent.Contains(outPoint)
             || (includeMempool && _mempool.Values.Any(t => t.Inputs.Any(i => i.PrevOut == outPoint))))
                return Task.FromResult<(TxOut Output, uint Height)?>(null);

            return Task.FromResult<(TxOut Output, uint Height)?>((c.Tx.Outputs[outPoint.N], c.Height));
        }
    }

    private Block AppendBlock(List<Transaction> transactions)
    {
        var height = (uint)_blocks.Count;
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        block.Header.HashPrevBlock = height == 0 ? uint256.Zero : _blocks[^1].GetHash();
        block.Header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + height * 600);
        block.Header.Nonce = ++_nonce;

        var coinbase = Network.RegTest.CreateTransaction();
        coinbase.Inputs.Add(new TxIn(new Script(Op.GetPushOp(height), OpcodeType.OP_0)));
        coinbase.Outputs.Add(Money.Coins(50), new Key().PubKey.WitHash.ScriptPubKey);
        block.Transactions.Add(coinbase);
        block.Transactions.AddRange(transactions);
        block.UpdateMerkleRoot();
        _blocks.Add(block);
        foreach (var tx in block.Transactions)
        {
            _confirmed[tx.GetHash()] = (tx, height);
            foreach (var input in tx.Inputs)
                _spent.Add(input.PrevOut);
        }

        return block;
    }
}