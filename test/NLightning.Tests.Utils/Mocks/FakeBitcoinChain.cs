using NBitcoin;

namespace NLightning.Tests.Utils.Mocks;

using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// An in-memory regtest chain behind <see cref="IBitcoinChainService"/> for chain-monitor tests (BOLT 5 plan O0):
/// linked blocks (each header points at its parent), reorgs to a competing branch, and a send log with an optional
/// refusal.
/// </summary>
/// <remarks>
/// Blocks are not mined (no proof of work); they only need distinct hashes and correct parent links. Transactions
/// sent with <see cref="SendTransactionAsync"/> go to <see cref="Mempool"/>, and <see cref="Mine"/> includes the
/// mempool unless told otherwise.
/// </remarks>
public sealed class FakeBitcoinChain : IBitcoinChainService
{
    private readonly List<Block> _blocks = [];
    private readonly List<Transaction> _sent = [];
    private uint _nonce;

    /// <summary>A chain with blocks 0..<paramref name="tipHeight"/>.</summary>
    public FakeBitcoinChain(uint tipHeight = 100)
    {
        for (var height = 0u; height <= tipHeight; height++)
            _blocks.Add(CreateBlock(height, height == 0 ? uint256.Zero : _blocks[^1].GetHash(), []));
    }

    public uint TipHeight => (uint)(_blocks.Count - 1);

    /// <summary>Transactions accepted by <see cref="SendTransactionAsync"/> and not mined yet.</summary>
    public List<Transaction> Mempool { get; } = [];

    /// <summary>Every transaction passed to <see cref="SendTransactionAsync"/>, accepted or not.</summary>
    public IReadOnlyList<Transaction> SendAttempts => _sent;

    /// <summary>When set, <see cref="SendTransactionAsync"/> throws it.</summary>
    public Exception? SendFailure { get; set; }

    public Block this[uint height] => _blocks[(int)height];

    /// <summary>Appends a block holding <paramref name="transactions"/> (and, by default, the mempool).</summary>
    public Block Mine(params Transaction[] transactions) => Mine(true, transactions);

    public Block Mine(bool includeMempool, params Transaction[] transactions)
    {
        var included = transactions.ToList();
        if (includeMempool)
        {
            included.AddRange(Mempool.Where(m => included.All(t => t.GetHash() != m.GetHash())));
            Mempool.Clear();
        }

        var block = CreateBlock(TipHeight + 1, _blocks[^1].GetHash(), included);
        _blocks.Add(block);
        return block;
    }

    /// <summary>
    /// Replaces every block above <paramref name="forkHeight"/> with <paramref name="newBlockCount"/> new blocks; the
    /// first one holds <paramref name="firstBlockTransactions"/>. Returns the new blocks. The transactions of the
    /// removed blocks are not put back into the mempool.
    /// </summary>
    public IReadOnlyList<Block> Reorg(uint forkHeight, int newBlockCount, params Transaction[] firstBlockTransactions)
    {
        _blocks.RemoveRange((int)forkHeight + 1, _blocks.Count - (int)forkHeight - 1);
        var added = new List<Block>();
        for (var i = 0; i < newBlockCount; i++)
            added.Add(Mine(false, i == 0 ? firstBlockTransactions : []));

        return added;
    }

    public Task<uint256> SendTransactionAsync(Transaction transaction)
    {
        _sent.Add(transaction);
        if (SendFailure is not null)
            throw SendFailure;

        if (Mempool.All(t => t.GetHash() != transaction.GetHash()))
            Mempool.Add(transaction);
        return Task.FromResult(transaction.GetHash());
    }

    public Task<Transaction?> GetTransactionAsync(uint256 txId) =>
        Task.FromResult(_blocks.SelectMany(b => b.Transactions).Concat(Mempool)
                               .FirstOrDefault(t => t.GetHash() == txId));

    public Task<uint> GetCurrentBlockHeightAsync() => Task.FromResult(TipHeight);

    public Task<Block?> GetBlockAsync(uint height) =>
        height <= TipHeight
            ? Task.FromResult<Block?>(_blocks[(int)height])
            : throw new InvalidOperationException($"No block at height {height}");

    public Task<uint256> GetBlockHashAsync(uint height) =>
        height <= TipHeight
            ? Task.FromResult(_blocks[(int)height].GetHash())
            : throw new InvalidOperationException($"No block at height {height}");

    public Task<uint> GetTransactionConfirmationsAsync(uint256 txId)
    {
        for (var height = 0; height < _blocks.Count; height++)
            if (_blocks[height].Transactions.Any(t => t.GetHash() == txId))
                return Task.FromResult((uint)(_blocks.Count - height));

        return Task.FromResult(0u);
    }

    private Block CreateBlock(uint height, uint256 previousHash, IEnumerable<Transaction> transactions)
    {
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        block.Header.HashPrevBlock = previousHash;
        block.Header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + height * 600);
        block.Header.Nonce = ++_nonce;

        var coinbase = Network.RegTest.CreateTransaction();
        coinbase.Inputs.Add(new TxIn(new Script(Op.GetPushOp(height), OpcodeType.OP_0)));
        coinbase.Outputs.Add(Money.Coins(50), new Key().PubKey.WitHash.ScriptPubKey);
        block.Transactions.Add(coinbase);
        block.Transactions.AddRange(transactions);
        block.UpdateMerkleRoot();
        return block;
    }
}